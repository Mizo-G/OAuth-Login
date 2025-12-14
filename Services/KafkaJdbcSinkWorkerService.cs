using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text;
using System.Net.Http;
using System.Collections.Generic;
using Cronos;
using OAuthLogin.Models;
using OAuthLogin.Data;

namespace OAuthLogin.Services
{
    /// <summary>
    /// Background service that reads Kafka topic data and processes it using JDBC sink.
    /// Consumes analytics messages from Kafka and sends them to external JDBC sink service.
    /// Alternative approach to database storage using HTTP API instead of direct connection.
    /// Demonstrates microservices architecture with decoupled processing and storage.
    /// </summary>
    public class KafkaJdbcSinkWorkerService : BackgroundService
    {
        private readonly ILogger<KafkaJdbcSinkWorkerService> _logger;
        private readonly IConfiguration _configuration;
        private readonly KafkaConsumerService _kafkaConsumerService;
        private readonly ApplicationDbContext _dbContext;
        private readonly HttpClient _httpClient;
        private readonly KafkaJdbcSinkWorkerOptions _options;
        private CronExpression? _cronExpression;

        public KafkaJdbcSinkWorkerService(
            ILogger<KafkaJdbcSinkWorkerService> logger,
            IConfiguration configuration,
            KafkaConsumerService kafkaConsumerService,
            ApplicationDbContext dbContext,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _configuration = configuration;
            _kafkaConsumerService = kafkaConsumerService;
            _dbContext = dbContext;
            _httpClient = httpClientFactory.CreateClient("KafkaJdbcSink");
            
            // Load configuration
            _options = new KafkaJdbcSinkWorkerOptions();
            configuration.GetSection("KafkaJdbcSinkWorker").Bind(_options);
            
            // Parse cron expression
            if (!string.IsNullOrEmpty(_options.Schedule))
            {
                _cronExpression = CronExpression.Parse(_options.Schedule);
            }

            // Configure HttpClient
            _httpClient.BaseAddress = new Uri(_options.JdbcSinkUrl);
            _httpClient.DefaultRequestHeaders.Add("Content-Type", "application/json");
            _httpClient.Timeout = TimeSpan.FromMinutes(5);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation("Kafka JDBC Sink Worker is disabled");
                return;
            }

            _logger.LogInformation("Kafka JDBC Sink Worker started with schedule: {Schedule}, JDBC sink URL: {JdbcSinkUrl}", 
                _options.Schedule, _options.JdbcSinkUrl);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    var nextRun = _cronExpression?.GetNextOccurrence(now) ?? now.AddMinutes(5);
                    var delay = nextRun - now;

                    _logger.LogInformation("Next data processing will run at: {NextRun}", nextRun);

                    await Task.Delay(delay, stoppingToken);

                    if (!stoppingToken.IsCancellationRequested)
                    {
                        await ProcessAndStoreViaJdbcSinkAsync(stoppingToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Normal cancellation, just exit
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in Kafka JDBC Sink Worker");
                    
                    // Wait for a bit before retrying to avoid rapid-fire errors
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
            }

            _logger.LogInformation("Kafka JDBC Sink Worker stopped");
        }

        private async Task ProcessAndStoreViaJdbcSinkAsync(CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation("Processing Kafka messages via JDBC sink");

                // Consume messages from Kafka
                var messages = await _kafkaConsumerService.ConsumeMessagesAsync(
                    stoppingToken, 
                    _options.MaxMessagesPerBatch, 
                    TimeSpan.FromMinutes(2)
                );

                if (messages.Count == 0)
                {
                    _logger.LogInformation("No messages to process via JDBC sink");
                    return;
                }

                var processingLog = new AnalyticsProcessingLog
                {
                    UserId = "system",
                    PropertyId = "multiple",
                    RecordsProcessed = messages.Count,
                    ProcessorType = "JDBC"
                };

                try
                {
                    // Store raw data in PostgreSQL
                    await StoreRawDataInPostgresAsync(messages);

                    // Process and store normalized data
                    var normalizedData = NormalizeMessages(messages);
                    await StoreNormalizedDataInPostgresAsync(normalizedData);

                    // Send to JDBC sink
                    await SendToJdbcSinkAsync(normalizedData);

                    processingLog.RecordsNormalized = normalizedData.Count;
                    processingLog.RecordsStoredInPostgresql = normalizedData.Count;

                    _logger.LogInformation("Successfully processed {Count} messages via JDBC sink", messages.Count);
                }
                catch (Exception ex)
                {
                    processingLog.ErrorMessage = ex.Message;
                    _logger.LogError(ex, "Error processing Kafka messages via JDBC sink");
                    throw;
                }
                finally
                {
                    // Log processing results
                    _dbContext.AnalyticsProcessingLogs.Add(processingLog);
                    await _dbContext.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process Kafka messages via JDBC sink");
                throw;
            }
        }

        private async Task StoreRawDataInPostgresAsync(List<AnalyticsReportKafkaMessage> messages)
        {
            try
            {
                foreach (var message in messages)
                {
                    var rawRecord = new RawAnalyticsData
                    {
                        UserId = message.UserId,
                        PropertyId = message.PropertyId,
                        Timestamp = message.GeneratedAt,
                        JsonData = JsonSerializer.Serialize(message.ReportData),
                        Source = "kafka-topic-jdbc"
                    };

                    _dbContext.RawAnalyticsData.Add(rawRecord);
                }

                await _dbContext.SaveChangesAsync();
                _logger.LogInformation("Stored {Count} raw records in PostgreSQL via JDBC sink", messages.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error storing raw data in PostgreSQL via JDBC sink");
                throw;
            }
        }

        private async Task StoreNormalizedDataInPostgresAsync(List<NormalizedAnalyticsData> normalizedData)
        {
            try
            {
                foreach (var record in normalizedData)
                {
                    record.ProcessingSource = "kafka-jdbc";
                }

                _dbContext.NormalizedAnalyticsData.AddRange(normalizedData);
                await _dbContext.SaveChangesAsync();
                _logger.LogInformation("Stored {Count} normalized records in PostgreSQL via JDBC sink", normalizedData.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error storing normalized data in PostgreSQL via JDBC sink");
                throw;
            }
        }

        private List<NormalizedAnalyticsData> NormalizeMessages(List<AnalyticsReportKafkaMessage> messages)
        {
            var normalizedData = new List<NormalizedAnalyticsData>();

            foreach (var message in messages)
            {
                foreach (var rowData in message.ReportData ?? new List<ReportRowData>())
                {
                    // Extract country from dimension values (assuming it's first dimension)
                    var country = rowData.DimensionValues?.FirstOrDefault() ?? "Unknown";
                    
                    // Extract metrics (assuming activeUsers and sessions are metrics)
                    var activeUsers = 0;
                    var sessions = 0;

                    foreach (var metric in rowData.MetricValues ?? new List<MetricValue>())
                    {
                        if (metric.Name.ToLower().Contains("activeuser"))
                            int.TryParse(metric.Value, out activeUsers);
                        else if (metric.Name.ToLower().Contains("session"))
                            int.TryParse(metric.Value, out sessions);
                    }

                    var normalizedRecord = new NormalizedAnalyticsData
                    {
                        UserId = message.UserId,
                        PropertyId = message.PropertyId,
                        EventDate = message.GeneratedAt.Date,
                        Country = country,
                        ActiveUsers = activeUsers,
                        Sessions = sessions,
                        ProcessingSource = "kafka-jdbc"
                    };

                    normalizedData.Add(normalizedRecord);
                }
            }

            return normalizedData;
        }

        private async Task SendToJdbcSinkAsync(List<NormalizedAnalyticsData> normalizedData)
        {
            try
            {
                // Create JDBC sink message format
                var jdbcMessage = new
                {
                    connection_string = _options.JdbcConnectionString,
                    table_name = "normalized_analytics_data",
                    records = normalizedData.Select(record => new
                    {
                        user_id = record.UserId,
                        property_id = record.PropertyId,
                        event_date = record.EventDate.ToString("yyyy-MM-dd"),
                        country = record.Country,
                        active_users = record.ActiveUsers,
                        sessions = record.Sessions,
                        processed_at = record.ProcessedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        processing_source = record.ProcessingSource
                    }).ToList()
                };

                var jsonContent = JsonSerializer.Serialize(jdbcMessage, new JsonSerializerOptions { WriteIndented = true });
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("/insert", content);
                
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Successfully sent data to JDBC sink");
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Failed to send data to JDBC sink. Status: {StatusCode}, Error: {Error}", 
                        response.StatusCode, errorContent);
                    
                    response.EnsureSuccessStatusCode();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending data to JDBC sink");
                throw;
            }
        }
    }

    public class KafkaJdbcSinkWorkerOptions
    {
        public bool Enabled { get; set; } = false;
        public string Schedule { get; set; } = "*/10 * * * *"; // Every 10 minutes
        public int MaxMessagesPerBatch { get; set; } = 100;
        public string JdbcSinkUrl { get; set; } = string.Empty;
        public string JdbcConnectionString { get; set; } = string.Empty;
    }
}