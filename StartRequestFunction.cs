using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ssp_1;

public class StartRequestFunction
{
    private readonly ILogger<StartRequestFunction> _logger;
    private readonly QueueClient _startQueue;
    private readonly BlobContainerClient _metadataContainer;

    public StartRequestFunction(IConfiguration config, ILogger<StartRequestFunction> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        string storageConnectionString = config["AzureWebJobsStorage"] ?? "";
        if (string.IsNullOrEmpty(storageConnectionString))
        {
            throw new ArgumentException("AzureWebJobsStorage is missing from configuration.");
        }

        string queueName = GetConfigValue(config, "START_QUEUE_NAME", "start-queue");
        string metadataContainerName = GetConfigValue(config, "METADATA_CONTAINER", "metadata");

        _startQueue = InitializeQueueClient(storageConnectionString, queueName);
        _metadataContainer = InitializeBlobContainerClient(storageConnectionString, metadataContainerName);
    }

    [Function("StartRequest")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "start")] HttpRequestData req)
    {
        string processId = Guid.NewGuid().ToString("N");

        try
        {
            _logger.LogInformation("Received start request. Creating process {ProcessId}", processId);

            // Step 1: Create metadata blob
            await UploadMetadataBlobAsync(processId);

            // Step 2: Enqueue the start message
            bool queued = await EnqueueStartMessageAsync(processId);
            if (!queued)
            {
                HttpResponseData failureResponse = req.CreateResponse(System.Net.HttpStatusCode.ServiceUnavailable);
                await failureResponse.WriteStringAsync("Could not enqueue job. Please try again later.");
                return failureResponse;
            }

            // Step 3: Build successful response
            HttpResponseData response = req.CreateResponse(System.Net.HttpStatusCode.Accepted);
            string baseUrl = req.Url.GetLeftPart(UriPartial.Authority);
            await response.WriteAsJsonAsync(new
            {
                processId,
                statusUrl = $"{baseUrl}/api/process/{processId}",
                message = "Started. Use the process id to check status or results."
            });

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in StartRequestFunction for process {ProcessId}", processId);
            HttpResponseData response = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await response.WriteStringAsync("An unexpected error occurred. Please check logs.");
            return response;
        }
    }

    private string GetConfigValue(IConfiguration config, string parameter, string defaultValue = "")
    {
        string value = config[parameter] ?? defaultValue;
        if (string.IsNullOrEmpty(value))
        {
            _logger.LogWarning("{Parameter} not set in configuration, using default: {DefaultValue}", parameter, defaultValue);
        }
        return value;
    }

    private QueueClient InitializeQueueClient(string connectionString, string queueName)
    {
        try
        {
            QueueClient queueClient = new(connectionString, queueName);
            queueClient.CreateIfNotExists();
            return queueClient;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize queue client for {QueueName}", queueName);
            throw;
        }
    }

    private BlobContainerClient InitializeBlobContainerClient(string connectionString, string containerName)
    {
        try
        {
            BlobContainerClient containerClient = new(connectionString, containerName);
            containerClient.CreateIfNotExists();
            return containerClient;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize blob container client for {ContainerName}", containerName);
            throw;
        }
    }

    private async Task UploadMetadataBlobAsync(string processId)
    {
        try
        {
            BlobClient blobClient = _metadataContainer.GetBlobClient(processId + ".json");

            _logger.LogInformation($"Uploading metadata for processId={processId}");


            var metadata = new
            {
                processId,
                createdAt = DateTime.UtcNow,
                status = "Queued",
                expectedCount = 1,
                stations = Array.Empty<object>()
            };

            string metadataJson = JsonSerializer.Serialize(metadata);
            byte[] metadataBytes = Encoding.UTF8.GetBytes(metadataJson);
            using MemoryStream metadataStream = new(metadataBytes);
            await blobClient.UploadAsync(metadataStream, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload metadata blob for process {ProcessId}", processId);
            throw;
        }
    }

    private async Task<bool> EnqueueStartMessageAsync(string processId)
    {
        try
        {
            string queueMessageJson = JsonSerializer.Serialize(new { processId });
            byte[] queueMessageBytes = Encoding.UTF8.GetBytes(queueMessageJson);
            string base64Message = Convert.ToBase64String(queueMessageBytes);

            await _startQueue.SendMessageAsync(base64Message);
            return true;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Failed to enqueue start message for process {ProcessId}. Queue might be unavailable.", processId);
            return false;
        }
    }
}
