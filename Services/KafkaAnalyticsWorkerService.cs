using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Cronos;
using Google.Analytics.Data.V1Beta;

namespace OAuthLogin.Services
{
    /// <summary>
    /// Background service that retrieves OAuth tokens from Secret Manager and fetches GA reports.
    /// Sends reports to Kafka using direct producer approach.
    /// Runs on a configurable schedule using cron expressions.
    /// </summary>
    public class KafkaAnalyticsWorkerService : BackgroundService
    {
        private readonly ILogger<KafkaAnalyticsWorkerService> _logger;
        private readonly IConfiguration _configuration;
        private readonly GoogleSecretManagerService _secretManagerService;
        private readonly GoogleAnalyticsReportService _reportService;
        private readonly KafkaProducerService _kafkaProducerService;
        private readonly KafkaAnalyticsWorkerOptions _options;
        private CronExpression? _cronExpression;

        public KafkaAnalyticsWorkerService(
            ILogger<KafkaAnalyticsWorkerService> logger,
            IConfiguration configuration,
            GoogleSecretManagerService secretManagerService,
            GoogleAnalyticsReportService reportService,
            KafkaProducerService kafkaProducerService)
        {
            _logger = logger;
            _configuration = configuration;
            _secretManagerService = secretManagerService;
            _reportService = reportService;
            _kafkaProducerService = kafkaProducerService;
            
            // Load configuration
            _options = new KafkaAnalyticsWorkerOptions();
            configuration.GetSection("KafkaAnalyticsWorker").Bind(_options);
            
            // Parse cron expression
            if (!string.IsNullOrEmpty(_options.Schedule))
            {
                _cronExpression = CronExpression.Parse(_options.Schedule);
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation("Kafka Analytics Worker is disabled");
                return;
            }

            _logger.LogInformation("Kafka Analytics Worker started with schedule: {Schedule}", _options.Schedule);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    var nextRun = _cronExpression?.GetNextOccurrence(now) ?? now.AddHours(1);
                    var delay = nextRun - now;

                    _logger.LogInformation("Next analytics report will be fetched at: {NextRun}", nextRun);

                    await Task.Delay(delay, stoppingToken);

                    if (!stoppingToken.IsCancellationRequested)
                    {
                        await FetchAndSendReportAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    // Normal cancellation, just exit
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in Kafka Analytics Worker");
                    
                    // Wait for a bit before retrying to avoid rapid-fire errors
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
            }

            _logger.LogInformation("Kafka Analytics Worker stopped");
        }

        private async Task FetchAndSendReportAsync()
        {
            try
            {
                _logger.LogInformation("Fetching analytics report for user: {UserId}, property: {PropertyId}", 
                    _options.UserId, _options.PropertyId);

                // Get tokens from Secret Manager
                var tokens = await _secretManagerService.GetTokensAsync(_options.UserId);
                if (tokens == null)
                {
                    _logger.LogWarning("No tokens found for user: {UserId}", _options.UserId);
                    return;
                }

                // Create date range (last 30 days by default)
                var dateRange = new DateRange
                {
                    StartDate = "30daysAgo",
                    EndDate = "today"
                };

                // Fetch the report
                var report = await _reportService.GetReportAsync(
                    tokens.AccessToken,
                    _options.PropertyId,
                    _options.Dimensions,
                    _options.Metrics,
                    dateRange
                );

                // Create Kafka message
                var kafkaMessage = new AnalyticsReportKafkaMessage
                {
                    UserId = _options.UserId,
                    PropertyId = _options.PropertyId,
                    GeneratedAt = DateTime.UtcNow,
                    ReportData = ProcessReportToKafkaFormat(report),
                    Metadata = new ReportMetadata
                    {
                        RowCount = report.Rows.Count,
                        Dimensions = report.DimensionHeaders.Select(d => new DimensionInfo { Name = d.Name, DisplayName = d.DisplayName }).ToList(),
                        Metrics = report.MetricHeaders.Select(m => new MetricInfo { Name = m.Name, DisplayName = m.DisplayName, Type = m.Type }).ToList()
                    }
                };

                // Send to Kafka
                await _kafkaProducerService.ProduceReportAsync(kafkaMessage, _options.UserId);

                _logger.LogInformation("Successfully sent analytics report to Kafka for user: {UserId}, rows: {RowCount}", 
                    _options.UserId, report.Rows.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch and send analytics report");
                throw;
            }
        }

        private List<ReportRowData> ProcessReportToKafkaFormat(Google.Analytics.Data.V1Beta.RunReportResponse report)
        {
            var result = new List<ReportRowData>();

            foreach (var row in report.Rows)
            {
                var rowData = new ReportRowData
                {
                    DimensionValues = row.DimensionValues.ToList(),
                    MetricValues = row.MetricValues.Select(mv => new MetricValue { Name = mv.Name, Value = mv.Value }).ToList()
                };
                result.Add(rowData);
            }

            return result;
        }
    }

    public class KafkaAnalyticsWorkerOptions
    {
        public bool Enabled { get; set; } = false;
        public string UserId { get; set; } = string.Empty;
        public string PropertyId { get; set; } = string.Empty;
        public string Schedule { get; set; } = "0 0 * * *"; // Daily at midnight
        public List<string> Dimensions { get; set; } = new List<string>();
        public List<string> Metrics { get; set; } = new List<string>();
    }

    public class AnalyticsReportKafkaMessage
    {
        public string UserId { get; set; } = string.Empty;
        public string PropertyId { get; set; } = string.Empty;
        public DateTime GeneratedAt { get; set; }
        public List<ReportRowData> ReportData { get; set; } = new List<ReportRowData>();
        public ReportMetadata Metadata { get; set; } = new ReportMetadata();
    }

    public class ReportMetadata
    {
        public int RowCount { get; set; }
        public List<DimensionInfo> Dimensions { get; set; } = new List<DimensionInfo>();
        public List<MetricInfo> Metrics { get; set; } = new List<MetricInfo>();
    }

    public class DimensionInfo
    {
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }

    public class MetricInfo
    {
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
    }

    public class ReportRowData
    {
        public List<string> DimensionValues { get; set; } = new List<string>();
        public List<MetricValue> MetricValues { get; set; } = new List<MetricValue>();
    }

    public class MetricValue
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }
}