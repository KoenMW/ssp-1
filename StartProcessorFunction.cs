using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ssp_1;

public class StartProcessorFunction(IConfiguration config, ILogger<StartProcessorFunction> logger)
{
    private readonly ILogger<StartProcessorFunction> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IConfiguration _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly HttpClient _httpClient = new();

    [Function("StartProcessor")]
    public async Task Run(
    [QueueTrigger("%START_QUEUE_NAME%", Connection = "AzureWebJobsStorage")] QueueMessage message)
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

        _logger.LogInformation("Processing start message: {MessageId}", message.MessageId);

        string? processId = ParseProcessId(message);
        if (string.IsNullOrEmpty(processId))
        {
            _logger.LogError("Invalid queue message content. MessageId: {MessageId}", message.MessageId);
            return;
        }

        try
        {
            _logger.LogInformation("Fetching Buienradar data for process {ProcessId}", processId);
            BuienradarResponse? buienData = await FetchBuienradarDataAsync();

            if (buienData?.actual?.stationmeasurements == null || buienData.actual.stationmeasurements.Count == 0)
            {
                _logger.LogWarning("No station measurements found in Buienradar feed.");
                return;
            }

            int maxJobs = Math.Min(_config.GetValue<int>("MAX_STATIONS", 5), buienData.actual.stationmeasurements.Count);
            QueueClient imageQueue = InitializeQueueClient(storageConnectionString, _config["IMAGE_QUEUE_NAME"] ?? "image-queue");

            await EnqueueStationJobsAsync(processId, buienData, maxJobs, imageQueue);

            _logger.LogInformation("Enqueued {JobCount} station jobs for process {ProcessId}", maxJobs, processId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start processing for process {ProcessId}", processId);
            throw;
        }
    }

    private string? ParseProcessId(QueueMessage message)
    {
        try
        {
            string messageText = message.MessageText;
            using JsonDocument jsonDoc = JsonDocument.Parse(messageText);
            JsonElement root = jsonDoc.RootElement;
            if (root.TryGetProperty("processId", out JsonElement idElement))
            {
                return idElement.GetString();
            }
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse queue message as JSON. MessageId: {MessageId}", message.MessageId);
            return null;
        }
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

    private async Task<BuienradarResponse?> FetchBuienradarDataAsync()
    {
        string apiUrl = _config["WEATHER_FEED_URL"] ?? "";
        if (string.IsNullOrEmpty(apiUrl))
        {
            _logger.LogError("WEATHER_FEED_URL is missing from configuration.");
            return null;
        }

        HttpResponseMessage response = await _httpClient.GetAsync(apiUrl);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<BuienradarResponse>(json);
    }

    private async Task EnqueueStationJobsAsync(string processId, BuienradarResponse buienData, int maxJobs, QueueClient queueClient)
    {
        for (int i = 0; i < maxJobs; i++)
        {
            StationMeasurement station = buienData.actual.stationmeasurements[i];

            string stationJobMessage = JsonSerializer.Serialize(new
            {
                processId,
                stationNumber = station.stationid,
                stationName = station.stationname,
                station.lat,
                station.lon,
                weatherDescription = station.weatherdescription,
                station.temperature,
                windSpeed = station.windspeed,
                runningJobs = maxJobs
            });

            string base64Message = Convert.ToBase64String(Encoding.UTF8.GetBytes(stationJobMessage));

            try
            {
                await queueClient.SendMessageAsync(base64Message);
                _logger.LogInformation("Enqueued job for station {StationId} ({StationName})", station.stationid, station.stationname);
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Failed to enqueue job for station {StationId} ({StationName})", station.stationid, station.stationname);
            }
        }
    }


    private sealed class BuienradarResponse
    {
        public string id { get; set; } = default!;
        public Actual actual { get; set; } = default!;
    }

    private sealed class Actual
    {
        public List<StationMeasurement> stationmeasurements { get; set; } = [];
    }

    private sealed class StationMeasurement
    {
        public int stationid { get; set; }
        public string stationname { get; set; } = default!;
        public double lat { get; set; }
        public double lon { get; set; }
        public string weatherdescription { get; set; } = default!;

        public double temperature { get; set; }
        public double windspeed { get; set; }


    }

}