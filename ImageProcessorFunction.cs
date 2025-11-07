using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Queues.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ssp_1;

public class ImageProcessorFunction(IConfiguration config, ILogger<ImageProcessorFunction> logger)
{
    private readonly ILogger<ImageProcessorFunction> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly HttpClient _httpClient = new();

    [Function("ImageProcessor")]
    public async Task Run(
    [QueueTrigger("%IMAGE_QUEUE_NAME%", Connection = "AzureWebJobsStorage")] QueueMessage message)
    {
        string storageConnectionString = _config["AzureWebJobsStorage"] ?? "";
        if (string.IsNullOrEmpty(storageConnectionString))
            throw new ArgumentException("AzureWebJobsStorage is missing from configuration.");

        if (message == null)
            return;

        (string? processId, int stationNumber, string text, string weatherDescription, int runningJobs) = ParseMessage(message);
        if (string.IsNullOrEmpty(processId) || stationNumber <= 0)
            return;

        string imagesContainerName = _config["IMAGES_CONTAINER"] ?? "images";
        BlobContainerClient imagesContainer = InitializeBlobContainerClient(storageConnectionString, imagesContainerName);

        string metadataContainerName = _config["METADATA_CONTAINER"] ?? "metadata";
        BlobContainerClient metadataContainer = InitializeBlobContainerClient(storageConnectionString, metadataContainerName);

        await UpdateMetadataAsync(metadataContainer, processId, runningJobs);

        string? blobName = null;

        try
        {
            Stream imageStream = await DownloadPublicImageAsync(stationNumber, weatherDescription);
            Stream processedStream = ImageHelper.AddTextToImage(imageStream, (text, (50f, 50f), 100, "#FFFFFF"));

            blobName = $"{processId}_station_{stationNumber}.png";
            await UploadImageAsync(processedStream, imagesContainer, blobName);

            await UpdateMetadataAsync(metadataContainer, processId, runningJobs, blobName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process image for station {StationNumber}, process {ProcessId}", stationNumber, processId);
            await UpdateMetadataAsync(metadataContainer, processId, runningJobs, blobName, ex.Message);
        }
    }

    private (string? processId, int stationNumber, string text, string weatherDescription, int runningJobs) ParseMessage(QueueMessage message)
    {
        try
        {
            string json = message.MessageText;

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string? processId = root.GetProperty("processId").GetString();
            int stationNumber = root.GetProperty("stationNumber").GetInt32();
            string stationName = root.GetProperty("stationName").GetString() ?? "";
            double lat = root.GetProperty("lat").GetDouble();
            double lon = root.GetProperty("lon").GetDouble();
            double temperature = root.GetProperty("temperature").GetDouble();
            double windSpeed = root.GetProperty("windSpeed").GetDouble();
            string weatherDescription = root.GetProperty("weatherDescription").GetString() ?? "";
            int runningJobs = root.GetProperty("runningJobs").GetInt32();

            string text = $"Station: {stationName}\nLat: {lat}\nLon: {lon}\nTemperature: {temperature}\nWindSpeed: {windSpeed}";

            return (processId, stationNumber, text, weatherDescription, runningJobs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse queue message: {MessageId}", message.MessageId);
            return (null, -1, "", "", -1);
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

    private async Task<Stream> DownloadPublicImageAsync(int stationNumber, string weatherDescription)
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

            UnsplashPhotoResponse? photo = await GetRandomPhotoAsync(unsplashApiKey, weatherDescription);

            if (photo == null || photo.links == null || string.IsNullOrEmpty(photo.links.download))
            {
                _logger.LogError("Unsplash API returned an invalid or empty response for station {StationNumber}", stationNumber);
                throw new InvalidOperationException("No valid image found from Unsplash.");
            }

            _logger.LogInformation("Downloading image from {DownloadUrl}", photo.links.download);

            Stream imageStream = await GetStreamAsync(photo.links.download);
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

    private async Task UpdateMetadataAsync(BlobContainerClient metadataContainer, string processId, int totalJobs, string? blobName = null, string? errorMessage = null)
    {
        (string metadataJson, ETag? etag) = await DownloadMetadataAsync(metadataContainer, processId);

        using JsonDocument doc = JsonDocument.Parse(metadataJson);
        JsonElement root = doc.RootElement;

        List<string> images = root.TryGetProperty("images", out JsonElement imagesElem)
            ? [.. imagesElem.EnumerateArray().Select(e => e.GetString() ?? string.Empty)]
            : [];

        List<string> errors = root.TryGetProperty("errors", out JsonElement errorsElem)
            ? [.. errorsElem.EnumerateArray().Select(e => e.GetString() ?? string.Empty)]
            : [];

        if (!string.IsNullOrEmpty(blobName) && !images.Contains(blobName))
            images.Add(blobName);

        if (!string.IsNullOrEmpty(errorMessage))
            errors.Add(errorMessage);

        var updated = new
        {
            processId,
            createdAt = root.TryGetProperty("createdAt", out JsonElement createdAtElem)
                ? createdAtElem.GetDateTime()
                : DateTime.UtcNow,
            images,
            errors,
            totalJobs
        };

        string updatedJson = JsonSerializer.Serialize(updated);
        await UploadMetadataWithRetryAsync(metadataContainer, processId, updatedJson, etag);
    }

    private async Task<(string metadataJson, ETag? etag)> DownloadMetadataAsync(BlobContainerClient container, string processId)
    {
        BlobClient blob = container.GetBlobClient(processId + ".json");
        try
        {
            Response<BlobDownloadInfo> download = await blob.DownloadAsync();
            using StreamReader reader = new(download.Value.Content);
            return (await reader.ReadToEndAsync(), download.Value.Details.ETag);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return ("{}", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download metadata for process {ProcessId}", processId);
            throw;
        }
    }

    private static string UpdateMetadataJson(string metadataJson, string processId, int totalJobs, string blobName)
    {
        using JsonDocument doc = JsonDocument.Parse(metadataJson);
        JsonElement root = doc.RootElement;

        List<string> images = root.TryGetProperty("images", out JsonElement imagesElem)
            ? [.. imagesElem.EnumerateArray().Select(e => e.GetString() ?? string.Empty)]
            : [];

        if (!string.IsNullOrEmpty(blobName) && !images.Contains(blobName))
            images.Add(blobName);

        var updated = new
        {
            processId,
            createdAt = root.TryGetProperty("createdAt", out JsonElement createdAtElem)
                ? createdAtElem.GetDateTime()
                : DateTime.UtcNow,
            status = totalJobs == images.Count ? "Finished" : "Processing",
            images,
            totalJobs
        };

        return JsonSerializer.Serialize(updated);
    }

    private async Task UploadMetadataWithRetryAsync(BlobContainerClient container, string processId, string updatedJson, ETag? etag)
    {
        BlobClient blob = container.GetBlobClient(processId + ".json");

        using MemoryStream stream = new();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(updatedJson));
        stream.Position = 0;

        while (true)
        {
            try
            {
                await blob.UploadAsync(stream, new BlobUploadOptions
                {
                    Conditions = etag.HasValue ? new BlobRequestConditions { IfMatch = etag.Value } : null
                });
                return;
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                _logger.LogWarning(ex, "Metadata changed concurrently, retrying for process {ProcessId}", processId);
                await Task.Delay(100);

                (string latestJson, ETag? latestEtag) = await DownloadMetadataAsync(container, processId);
                string refreshedJson = UpdateMetadataJson(latestJson, processId, 0, "");
                etag = latestEtag;

                byte[] bytes = Encoding.UTF8.GetBytes(refreshedJson);
                stream.SetLength(0);
                await stream.WriteAsync(bytes, 0, bytes.Length);
                stream.Position = 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload metadata for process {ProcessId}", processId);
                throw;
            }
        }
    }


    private async Task<UnsplashPhotoResponse?> GetRandomPhotoAsync(string apiKey, string weatherDescription)
    {
        string baseUrl = _config["IMAGE_FEED_URL"] ?? "";

        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("IMAGE_FEED_URL configuration is empty.");

        UriBuilder uriBuilder = new(baseUrl);
        string query = uriBuilder.Query;
        if (!string.IsNullOrEmpty(query) && query.StartsWith('?'))
            query = query[1..];

        var queryParams = System.Web.HttpUtility.ParseQueryString(query ?? "");
        queryParams["weather"] = weatherDescription;
        uriBuilder.Query = queryParams.ToString();

        HttpRequestMessage request = new(HttpMethod.Get, uriBuilder.ToString());
        request.Headers.Add("Authorization", $"Client-ID {apiKey}");

        HttpResponseMessage response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<UnsplashPhotoResponse>(json);
    }

    private async Task<Stream> GetStreamAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
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
