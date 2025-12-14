using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text;
using System.Net.Http;
using Cronos;
using Google.Analytics.Data.V1Beta;

namespace OAuthLogin.Services
{
    /// <summary>
    /// Background service that retrieves OAuth tokens from Secret Manager and fetches GA reports.
    /// Sends reports to Kafka using HTTP sink connector approach.
    /// Runs on a configurable schedule using cron expressions.
    /// </summary>
    public class KafkaHttpSinkWorkerService : BackgroundService
    {
        private readonly ILogger<KafkaHttpSinkWorkerService> _logger;
        private readonly IConfiguration _configuration;
        private readonly GoogleSecretManagerService _secretManagerService;
        private readonly GoogleAnalyticsReportService _reportService;
        private readonly HttpClient _httpClient;
        private readonly KafkaHttpSinkWorkerOptions _options;
        private CronExpression? _cronExpression;

        public KafkaHttpSinkWorkerService(
            ILogger<KafkaHttpSinkWorkerService> logger,
            IConfiguration configuration,
            GoogleSecretManagerService secretManagerService,
            GoogleAnalyticsReportService reportService,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _configuration = configuration;
            _secretManagerService = secretManagerService;
            _reportService = reportService;
            _httpClient = httpClientFactory.CreateClient("KafkaHttpSink");
            
            // Load configuration
            _options = new KafkaHttpSinkWorkerOptions();
            configuration.GetSection("KafkaHttpSinkWorker").Bind(_options);
            
            // Parse cron expression
            if (!string.IsNullOrEmpty(_options.Schedule))
            {
                _cronExpression = CronExpression.Parse(_options.Schedule);
            }

            // Configure HttpClient
            _httpClient.BaseAddress = new Uri(_options.HttpSinkUrl);
            _httpClient.DefaultRequestHeaders.Add("Content-Type", "application/vnd.kafka.json.v2+json");
            _httpClient.Timeout = TimeSpan.FromMinutes(5);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation("Kafka HTTP Sink Worker is disabled");
                return;
            }

            _logger.LogInformation("Kafka HTTP Sink Worker started with schedule: {Schedule}, sink URL: {HttpSinkUrl}", 
                _options.Schedule, _options.HttpSinkUrl);

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
                    _logger.LogError(ex, "Error in Kafka HTTP Sink Worker");
                    
                    // Wait for a bit before retrying to avoid rapid-fire errors
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
            }

            _logger.LogInformation("Kafka HTTP Sink Worker stopped");
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

                // Create HTTP sink message
                var httpSinkMessage = CreateHttpSinkMessage(report);

                // Send to Kafka HTTP Sink
                await SendToHttpSinkAsync(httpSinkMessage);

                _logger.LogInformation("Successfully sent analytics report to HTTP sink for user: {UserId}, rows: {RowCount}", 
                    _options.UserId, report.Rows.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch and send analytics report to HTTP sink");
                throw;
            }
        }

        private object CreateHttpSinkMessage(Google.Analytics.Data.V1Beta.RunReportResponse report)
        {
            var records = new List<object>();

            foreach (var row in report.Rows)
            {
                var record = new
                {
                    key = _options.UserId,
                    value = new
                    {
                        userId = _options.UserId,
                        propertyId = _options.PropertyId,
                        timestamp = DateTime.UtcNow,
                        dimensions = report.DimensionHeaders.Select((dh, index) => new
                        {
                            name = dh.Name,
                            displayName = dh.DisplayName,
                            value = row.DimensionValues.ElementAtOrDefault(index)
                        }).ToList(),
                        metrics = report.MetricHeaders.Select((mh, index) => new
                        {
                            name = mh.Name,
                            displayName = mh.DisplayName,
                            type = mh.Type,
                            value = row.MetricValues.ElementAtOrDefault(index)?.Value
                        }).ToList()
                    }
                };
                records.Add(record);
            }

            return new
            {
                records = records
            };
        }

        private async Task SendToHttpSinkAsync(object message)
        {
            try
            {
                var jsonContent = JsonSerializer.Serialize(message, new JsonSerializerOptions { WriteIndented = true });
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/vnd.kafka.json.v2+json");

                var response = await _httpClient.PostAsync("", content);
                
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Successfully sent message to Kafka HTTP sink");
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Failed to send message to Kafka HTTP sink. Status: {StatusCode}, Error: {Error}", 
                        response.StatusCode, errorContent);
                    
                    response.EnsureSuccessStatusCode();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending message to Kafka HTTP sink");
                throw;
            }
        }
    }

    public class KafkaHttpSinkWorkerOptions
    {
        public bool Enabled { get; set; } = false;
        public string UserId { get; set; } = string.Empty;
        public string PropertyId { get; set; } = string.Empty;
        public string Schedule { get; set; } = "0 30 * * *"; // Daily at 30 minutes past midnight
        public List<string> Dimensions { get; set; } = new List<string>();
        public List<string> Metrics { get; set; } = new List<string>();
        public string HttpSinkUrl { get; set; } = string.Empty;
    }
}