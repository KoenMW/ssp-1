using System;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ssp_1;

public class StartProcessorFunction
{
    private readonly ILogger<StartProcessorFunction> _logger;
    private readonly IConfiguration _config;

    public StartProcessorFunction(IConfiguration config, ILogger<StartProcessorFunction> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

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

        string processId = ParseProcessId(message) ?? "";
        if (string.IsNullOrEmpty(processId))
        {
            _logger.LogError("Invalid queue message content. MessageId: {MessageId}", message.MessageId);
            return;
        }

        try
        {
            _logger.LogInformation("Starting fan-out jobs for process {ProcessId}", processId);

            string queueName = _config["IMAGE_QUEUE_NAME"] ?? "image-queue";
            QueueClient imageQueue = InitializeQueueClient(storageConnectionString, queueName);

            int totalStations = 1; // Could be dynamic if you fetch from the weather feed
            await EnqueueStationJobsAsync(processId, totalStations, imageQueue);

            _logger.LogInformation("Enqueued {TotalStations} station jobs for process {ProcessId}", totalStations, processId);
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

    private async Task EnqueueStationJobsAsync(string processId, int totalStations, QueueClient queueClient)
    {
        for (int i = 1; i <= totalStations; i++)
        {
            string stationJobMessage = JsonSerializer.Serialize(new
            {
                processId,
                stationNumber = i
            });

            byte[] messageBytes = Encoding.UTF8.GetBytes(stationJobMessage);
            string base64Message = Convert.ToBase64String(messageBytes);

            try
            {
                await queueClient.SendMessageAsync(base64Message);
                _logger.LogInformation("Enqueued job for station {StationNumber} (process {ProcessId})", i, processId);
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Failed to enqueue job for station {StationNumber} (process {ProcessId})", i, processId);
            }
        }
    }
}