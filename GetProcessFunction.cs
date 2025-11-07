using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ssp_1;

public class GetProcessFunction(IConfiguration config, ILogger<GetProcessFunction> logger)
{
    private readonly ILogger<GetProcessFunction> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));

    [Function("GetProcess")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "process/{processId}")] HttpRequestData req,
        string processId)
    {
        if (string.IsNullOrEmpty(processId))
        {
            return await CreateErrorResponseAsync(req, "Missing process ID.", System.Net.HttpStatusCode.BadRequest);
        }

        string storageConnectionString = _config["AzureWebJobsStorage"] ?? "";
        if (string.IsNullOrEmpty(storageConnectionString))
        {
            return await CreateErrorResponseAsync(req, "Storage connection string not configured.", System.Net.HttpStatusCode.InternalServerError);
        }

        try
        {
            BlobContainerClient metadataContainer = InitializeContainer(storageConnectionString, "METADATA_CONTAINER", "metadata");
            BlobContainerClient imageContainer = InitializeContainer(storageConnectionString, "IMAGES_CONTAINER", "images");

            string? metadataJson = await ReadMetadataJsonAsync(metadataContainer, processId);

            _logger.LogInformation("metadataJson: {MetadataJson}", metadataJson);

            if (string.IsNullOrEmpty(metadataJson))
            {
                return await CreateErrorResponseAsync(req, "Process not found.", System.Net.HttpStatusCode.NotFound);
            }

            ProcessMetadata processData = ParseMetadata(metadataJson);

            object responseBody = BuildResponseBody(imageContainer, processData);

            HttpResponseData response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await response.WriteAsJsonAsync(responseBody);
            return response;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Blob storage request failed while retrieving process {ProcessId}", processId);
            return await CreateErrorResponseAsync(req, "Failed to access blob storage. Try again later.", System.Net.HttpStatusCode.ServiceUnavailable);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while retrieving process {ProcessId}", processId);
            return await CreateErrorResponseAsync(req, "Unexpected error occurred.", System.Net.HttpStatusCode.InternalServerError);
        }
    }

    private BlobContainerClient InitializeContainer(string connectionString, string parameter, string defaultValue)
    {
        string containerName = _config[parameter] ?? defaultValue;
        BlobContainerClient container = new(connectionString, containerName);
        container.CreateIfNotExists();
        return container;
    }

    private async Task<string?> ReadMetadataJsonAsync(BlobContainerClient container, string processId)
    {
        string blobName = $"{processId}.json";
        BlobClient blob = container.GetBlobClient(blobName);

        if (!await blob.ExistsAsync())
        {
            _logger.LogWarning("Metadata blob {BlobName} not found in container {Container}", blobName, container.Name);
            return null;
        }

        Response<BlobDownloadInfo> downloadResponse = await blob.DownloadAsync();
        using StreamReader reader = new(downloadResponse.Value.Content);
        string json = await reader.ReadToEndAsync();

        return json;
    }

    private static ProcessMetadata ParseMetadata(string metadataJson)
    {
        ProcessMetadata? data = JsonSerializer.Deserialize<ProcessMetadata>(metadataJson) ?? throw new InvalidOperationException("Failed to deserialize process metadata.");
        return data;
    }

    private static object BuildResponseBody(BlobContainerClient client, ProcessMetadata data)
    {
        List<string> imageLinks = [];

        foreach (string blobName in data.images ?? [])
        {
            BlobClient blobClient = client.GetBlobClient(blobName);

            // Generate a read-only SAS token valid 1 hour
            BlobSasBuilder sasBuilder = new(BlobSasPermissions.Read, DateTimeOffset.UtcNow.AddHours(1))
            {
                BlobContainerName = client.Name,
                BlobName = blobName
            };

            string sasUri = blobClient.GenerateSasUri(sasBuilder).ToString();
            imageLinks.Add(sasUri);
        }

        return new
        {
            data.processId,
            data.createdAt,
            data.status,
            Images = imageLinks
        };
    }


    private static async Task<HttpResponseData> CreateErrorResponseAsync(HttpRequestData req, string message, System.Net.HttpStatusCode statusCode)
    {
        HttpResponseData response = req.CreateResponse(statusCode);
        await response.WriteAsJsonAsync(new { error = message });
        return response;
    }


    private sealed class ProcessMetadata
    {
        public string processId { get; set; } = "";
        public DateTime? createdAt { get; set; } = null;
        public string status { get; set; } = "";
        public string[] images { get; set; } = [];
    }
}

