using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Queues.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ssp_1;

public class ImageProcessorFunction
{
    private readonly ILogger<ImageProcessorFunction> _logger;
    private readonly IConfiguration _config;
    private readonly HttpClient _httpClient;

    public ImageProcessorFunction(IConfiguration config, ILogger<ImageProcessorFunction> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _httpClient = new HttpClient();
    }

    [Function("ImageProcessor")]
    public async Task Run(
        [QueueTrigger("%IMAGE_QUEUE_NAME%", Connection = "AzureWebJobsStorage")] QueueMessage message)
    {
        string storageConnectionString = _config["AzureWebJobsStorage"] ?? "";
        if (string.IsNullOrEmpty(storageConnectionString))
        {
            _logger.LogError("AzureWebJobsStorage is missing from configuration.");
            throw new ArgumentException("AzureWebJobsStorage is missing from configuration.");
        }

        if (message == null)
        {
            _logger.LogError("Queue message is null. Nothing to process.");
            return;
        }

        _logger.LogInformation("Processing image queue message {MessageId}", message.MessageId);

        (string? processId, int stationNumber) = ParseMessage(message);
        if (string.IsNullOrEmpty(processId) || stationNumber <= 0)
        {
            _logger.LogError("Invalid queue message content. MessageId: {MessageId}", message.MessageId);
            return;
        }

        try
        {
            string imagesContainerName = _config["IMAGES_CONTAINER"] ?? "images";
            BlobContainerClient imagesContainer = InitializeBlobContainerClient(storageConnectionString, imagesContainerName);

            string metadataContainerName = _config["METADATA_CONTAINER"] ?? "metadata";
            BlobContainerClient metadataContainer = InitializeBlobContainerClient(storageConnectionString, metadataContainerName);


            await UpdateMetadataAsync(metadataContainer, processId, "Processing");

            Stream imageStream = await DownloadPublicImageAsync(stationNumber);

            Stream processedStream = ImageHelper.AddTextToImage(
                imageStream,
                ("Station " + stationNumber, (50f, 50f), 24, "#FFFFFF")
            );

            string blobName = $"{processId}_station_{stationNumber}.png";

            _logger.LogInformation("blob created: {BlobName}", blobName);

            await UploadImageAsync(processedStream, imagesContainer, blobName);

            await UpdateMetadataAsync(metadataContainer, processId, "Finished", blobName);

            _logger.LogInformation("Processed and uploaded image for station {StationNumber}, process {ProcessId}", stationNumber, processId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process image for station {StationNumber}, process {ProcessId}", stationNumber, processId);
        }
    }

    // ----------------------- Helper Methods -----------------------

    private (string? processId, int stationNumber) ParseMessage(QueueMessage message)
    {
        try
        {
            string messageText = message.MessageText;
            using JsonDocument jsonDoc = JsonDocument.Parse(messageText);
            JsonElement root = jsonDoc.RootElement;

            string processId = root.GetProperty("processId").GetString() ?? "";
            int stationNumber = root.GetProperty("stationNumber").GetInt32();

            return (processId, stationNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse image queue message: {MessageId}", message.MessageId);
            return (null, -1);
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

    private async Task<Stream> DownloadPublicImageAsync(int stationNumber)
    {
        try
        {
            string unsplashApiKey = _config["UNSPLASH_ACCESS_KEY"] ?? "";
            if (string.IsNullOrEmpty(unsplashApiKey))
            {
                _logger.LogError("UNSPLASH_ACCESS_KEY is missing from configuration.");
                throw new InvalidOperationException("Unsplash API key not configured.");
            }

            _logger.LogInformation("Fetching random image metadata for station {StationNumber}", stationNumber);

            // Fetch random image metadata from Unsplash
            UnsplashPhotoResponse? photo = await GetRandomPhotoAsync(unsplashApiKey);

            if (photo == null || photo.links == null || string.IsNullOrEmpty(photo.links.download))
            {
                _logger.LogError("Unsplash API returned an invalid or empty response for station {StationNumber}", stationNumber);
                throw new InvalidOperationException("No valid image found from Unsplash.");
            }

            _logger.LogInformation("Downloading image from {DownloadUrl}", photo.links.download);

            // Download the image using the provided download link
            Stream imageStream = await GetStreamAsync(photo.links.download, unsplashApiKey);
            return imageStream;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP request to Unsplash failed for station {StationNumber}", stationNumber);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while downloading Unsplash image for station {StationNumber}", stationNumber);
            throw;
        }
    }

    private async Task UploadImageAsync(Stream imageStream, BlobContainerClient containerClient, string blobName)
    {
        try
        {
            BlobClient blobClient = containerClient.GetBlobClient(blobName);
            imageStream.Position = 0;
            await blobClient.UploadAsync(imageStream, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload image {BlobName} to container {ContainerName}", blobName, containerClient.Name);
            throw;
        }
    }

    private async Task UpdateMetadataAsync(BlobContainerClient metadataContainer, string processId, string status, string blobName = "")
    {
        try
        {
            BlobClient metadataBlob = metadataContainer.GetBlobClient(processId + ".json");

            string metadataJson;
            try
            {
                Response<BlobDownloadInfo> download = await metadataBlob.DownloadAsync();
                using StreamReader reader = new(download.Value.Content);
                metadataJson = await reader.ReadToEndAsync();
            }
            catch (RequestFailedException)
            {
                metadataJson = "{}";
            }

            using JsonDocument jsonDoc = JsonDocument.Parse(metadataJson);
            JsonElement root = jsonDoc.RootElement;

            // Build updated JSON with new image
            List<string> images = root.TryGetProperty("images", out JsonElement imagesElem) ? [.. imagesElem.EnumerateArray().Select(e => e.GetString() ?? string.Empty)] : [];

            if (!string.IsNullOrEmpty(blobName)) images.Add(blobName);


            var updatedMetadata = new
            {
                processId,
                createdAt = root.TryGetProperty("createdAt", out JsonElement createdAtElem) ? createdAtElem.GetDateTime() : DateTime.UtcNow,
                status,
                images
            };

            string updatedJson = JsonSerializer.Serialize(updatedMetadata);
            byte[] metadataBytes = Encoding.UTF8.GetBytes(updatedJson);
            using MemoryStream metadataStream = new(metadataBytes);
            await metadataBlob.UploadAsync(metadataStream, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update metadata for process {ProcessId} with image {BlobName}", processId, blobName);
        }
    }

    private async Task<UnsplashPhotoResponse?> GetRandomPhotoAsync(string apiKey)
    {
        string requestUrl = _config["IMAGE_FEED_URL"] ?? "";

        if (string.IsNullOrWhiteSpace(requestUrl))
        {
            throw new InvalidOperationException("IMAGE_FEED_URL configuration is empty.");
        }

        HttpRequestMessage request = new(HttpMethod.Get, requestUrl);
        request.Headers.Add("Authorization", $"Client-ID {apiKey}");

        HttpResponseMessage response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<UnsplashPhotoResponse>(json);
    }


    private async Task<Stream> GetStreamAsync(string url, string apiKey)
    {

        // Create request with header-based auth (Unsplash preferred way)
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"Client-ID {apiKey}");
        request.Headers.Add("User-Agent", "SSP_1/1.0");

        using HttpResponseMessage response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        MemoryStream memoryStream = new();
        await response.Content.CopyToAsync(memoryStream);
        memoryStream.Position = 0;

        return memoryStream;
    }



    private sealed class UnsplashPhotoResponse
    {
        public UnsplashPhotoLinks links { get; set; } = new UnsplashPhotoLinks();
    }

    private sealed class UnsplashPhotoLinks
    {
        public string download { get; set; } = "https://unsplash.com/photos/YPDwKu1P0yo/download?ixid=M3w4MjcyNTl8MHwxfHJhbmRvbXx8fHx8fHx8fDE3NjI1MTk0MDR8";
    }

}
