using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Cronos;
using Google.Cloud.Firestore;
using Google.Cloud.BigQuery.V2;
using OAuthLogin.Models;
using OAuthLogin.Data;

namespace OAuthLogin.Services
{
    /// <summary>
    /// Background service that reads Kafka topic data and processes it.
    /// Consumes analytics messages from Kafka and stores them in multiple destinations:
    /// - PostgreSQL for raw and normalized data (relational database)
    /// - Firebase Firestore for real-time access (NoSQL database)
    /// - Google BigQuery for data warehousing and analysis
    /// Provides comprehensive logging and error handling.
    /// </summary>
    public class KafkaDataProcessorWorkerService : BackgroundService
    {
        private readonly ILogger<KafkaDataProcessorWorkerService> _logger;
        private readonly IConfiguration _configuration;
        private readonly KafkaConsumerService _kafkaConsumerService;
        private readonly ApplicationDbContext _dbContext;
        private readonly FirestoreDb _firestoreDb;
        private readonly BigQueryClient _bigQueryClient;
        private readonly KafkaDataProcessorOptions _options;
        private CronExpression? _cronExpression;

        public KafkaDataProcessorWorkerService(
            ILogger<KafkaDataProcessorWorkerService> logger,
            IConfiguration configuration,
            KafkaConsumerService kafkaConsumerService,
            ApplicationDbContext dbContext,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _configuration = configuration;
            _kafkaConsumerService = kafkaConsumerService;
            _dbContext = dbContext;
            
            // Load configuration
            _options = new KafkaDataProcessorOptions();
            configuration.GetSection("KafkaDataProcessor").Bind(_options);
            
            // Parse cron expression
            if (!string.IsNullOrEmpty(_options.Schedule))
            {
                _cronExpression = CronExpression.Parse(_options.Schedule);
            }

            // Initialize Firebase Firestore
            var projectId = configuration["Google:ProjectId"] ?? throw new ArgumentNullException("Google:ProjectId");
            _firestoreDb = new FirestoreDbBuilder
            {
                ProjectId = projectId,
                Credential = Google.Apis.Auth.OAuth2.GoogleCredential.GetApplicationDefault()
            }.Build();

            // Initialize BigQuery
            _bigQueryClient = BigQueryClient.Create(projectId);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation("Kafka Data Processor Worker is disabled");
                return;
            }

            _logger.LogInformation("Kafka Data Processor Worker started with schedule: {Schedule}", _options.Schedule);

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
                        await ProcessKafkaMessagesAsync(stoppingToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Normal cancellation, just exit
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in Kafka Data Processor Worker");
                    
                    // Wait for a bit before retrying to avoid rapid-fire errors
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
            }

            _logger.LogInformation("Kafka Data Processor Worker stopped");
        }

        private async Task ProcessKafkaMessagesAsync(CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation("Starting to process Kafka messages");

                // Consume messages from Kafka
                var messages = await _kafkaConsumerService.ConsumeMessagesAsync(
                    stoppingToken, 
                    _options.MaxMessagesPerBatch, 
                    TimeSpan.FromMinutes(2)
                );

                if (messages.Count == 0)
                {
                    _logger.LogInformation("No messages to process");
                    return;
                }

                var processingLog = new AnalyticsProcessingLog
                {
                    UserId = "system",
                    PropertyId = "multiple",
                    RecordsProcessed = messages.Count,
                    ProcessorType = "Direct"
                };

                try
                {
                    // Store raw data in PostgreSQL
                    await StoreRawDataInPostgresAsync(messages);

                    // Process and store normalized data
                    var normalizedData = NormalizeMessages(messages);
                    await StoreNormalizedDataInPostgresAsync(normalizedData);

                    // Store in Firebase Firestore
                    if (_options.StoreInFirebase)
                    {
                        await StoreDataInFirebaseAsync(normalizedData);
                        processingLog.RecordsStoredInFirebase = normalizedData.Count;
                    }

                    // Store in BigQuery
                    if (_options.StoreInBigQuery)
                    {
                        await StoreDataInBigQueryAsync(normalizedData);
                        processingLog.RecordsStoredInBigQuery = normalizedData.Count;
                    }

                    processingLog.RecordsNormalized = normalizedData.Count;
                    processingLog.RecordsStoredInPostgresql = normalizedData.Count;

                    _logger.LogInformation("Successfully processed {Count} messages", messages.Count);
                }
                catch (Exception ex)
                {
                    processingLog.ErrorMessage = ex.Message;
                    _logger.LogError(ex, "Error processing Kafka messages");
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
                _logger.LogError(ex, "Failed to process Kafka messages");
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
                        Source = "kafka-topic"
                    };

                    _dbContext.RawAnalyticsData.Add(rawRecord);
                }

                await _dbContext.SaveChangesAsync();
                _logger.LogInformation("Stored {Count} raw records in PostgreSQL", messages.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error storing raw data in PostgreSQL");
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
                    // Extract country from dimension values (assuming it's the first dimension)
                    var country = rowData.DimensionValues?.FirstOrDefault() ?? "Unknown";
                    
                    // Extract metrics (assuming activeUsers and sessions are the metrics)
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
                        ProcessingSource = "kafka-direct"
                    };

                    normalizedData.Add(normalizedRecord);
                }
            }

            return normalizedData;
        }

        private async Task StoreNormalizedDataInPostgresAsync(List<NormalizedAnalyticsData> normalizedData)
        {
            try
            {
                _dbContext.NormalizedAnalyticsData.AddRange(normalizedData);
                await _dbContext.SaveChangesAsync();
                _logger.LogInformation("Stored {Count} normalized records in PostgreSQL", normalizedData.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error storing normalized data in PostgreSQL");
                throw;
            }
        }

        private async Task StoreDataInFirebaseAsync(List<NormalizedAnalyticsData> normalizedData)
        {
            try
            {
                var batch = _firestoreDb.StartBatch();
                var collection = _firestoreDb.Collection("analytics_data");

                foreach (var record in normalizedData)
                {
                    var firestoreRecord = new FirestoreAnalyticsData
                    {
                        UserId = record.UserId,
                        PropertyId = record.PropertyId,
                        EventDate = record.EventDate,
                        Country = record.Country,
                        ActiveUsers = record.ActiveUsers,
                        Sessions = record.Sessions,
                        ProcessedAt = record.ProcessedAt,
                        ProcessingSource = record.ProcessingSource
                    };

                    var docRef = collection.Document();
                    firestoreRecord.DocumentId = docRef.Id;
                    batch.Set(docRef, firestoreRecord);
                }

                await batch.CommitAsync();
                _logger.LogInformation("Stored {Count} records in Firebase Firestore", normalizedData.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error storing data in Firebase Firestore");
                throw;
            }
        }

        private async Task StoreDataInBigQueryAsync(List<NormalizedAnalyticsData> normalizedData)
        {
            try
            {
                var tableId = $"{_firestoreDb.ProjectId}.analytics_dataset.normalized_analytics_data";
                
                // Check if table exists, create if not
                await EnsureBigQueryTableExistsAsync(tableId);

                // Convert to BigQuery rows
                var rows = normalizedData.Select(record => new BigQueryInsertRow
                {
                    InsertId = Guid.NewGuid().ToString(),
                    Json = new Dictionary<string, object>
                    {
                        ["user_id"] = record.UserId,
                        ["property_id"] = record.PropertyId,
                        ["event_date"] = record.EventDate.ToString("yyyy-MM-dd"),
                        ["country"] = record.Country,
                        ["active_users"] = record.ActiveUsers,
                        ["sessions"] = record.Sessions,
                        ["processed_at"] = record.ProcessedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        ["processing_source"] = record.ProcessingSource
                    }
                }).ToList();

                // Insert data
                await _bigQueryClient.InsertRowsAsync(
                    tableId,
                    rows,
                    new InsertOptions { SkipInvalidRows = true, IgnoreUnknownValues = true }
                );

                _logger.LogInformation("Stored {Count} records in BigQuery", normalizedData.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error storing data in BigQuery");
                throw;
            }
        }

        private async Task EnsureBigQueryTableExistsAsync(string tableId)
        {
            try
            {
                var table = await _bigQueryClient.GetTableAsync(tableId);
                if (table != null)
                    return;
            }
            catch (Google.GoogleApiException)
            {
                // Table doesn't exist, create it
                var schema = new TableSchemaBuilder
                {
                    { "user_id", BigQueryFieldType.String, "User ID" },
                    { "property_id", BigQueryFieldType.String, "Property ID" },
                    { "event_date", BigQueryFieldType.Date, "Event Date" },
                    { "country", BigQueryFieldType.String, "Country" },
                    { "active_users", BigQueryFieldType.Integer, "Active Users" },
                    { "sessions", BigQueryFieldType.Integer, "Sessions" },
                    { "processed_at", BigQueryFieldType.Timestamp, "Processed At" },
                    { "processing_source", BigQueryFieldType.String, "Processing Source" }
                };

                await _bigQueryClient.CreateTableAsync(
                    tableId,
                    new Table
                    {
                        Schema = schema,
                        TimePartitioning = new TimePartitioning { Field = "event_date" }
                    }
                );
            }
        }
    }

    public class KafkaDataProcessorOptions
    {
        public bool Enabled { get; set; } = false;
        public string Schedule { get; set; } = "*/5 * * * *"; // Every 5 minutes
        public int MaxMessagesPerBatch { get; set; } = 100;
        public bool StoreInFirebase { get; set; } = true;
        public bool StoreInBigQuery { get; set; } = true;
    }
}