using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Cronos;

namespace OAuthLogin.Services
{
    /// <summary>
    /// Background service that retrieves OAuth tokens from Secret Manager and fetches GA reports.
    /// Runs on a configurable schedule using cron expressions.
    /// Saves report summaries to local files for demonstration purposes.
    /// </summary>
    public class AnalyticsWorkerService : BackgroundService
    {
        private readonly ILogger<AnalyticsWorkerService> _logger;
        private readonly IConfiguration _configuration;
        private readonly GoogleSecretManagerService _secretManagerService;
        private readonly GoogleAnalyticsReportService _reportService;
        private readonly AnalyticsWorkerOptions _options;
        private CronExpression? _cronExpression;

        public AnalyticsWorkerService(
            ILogger<AnalyticsWorkerService> logger,
            IConfiguration configuration,
            GoogleSecretManagerService secretManagerService,
            GoogleAnalyticsReportService reportService)
        {
            _logger = logger;
            _configuration = configuration;
            _secretManagerService = secretManagerService;
            _reportService = reportService;
            
            // Load configuration
            _options = new AnalyticsWorkerOptions();
            configuration.GetSection("AnalyticsWorker").Bind(_options);
            
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
                _logger.LogInformation("Analytics Worker is disabled");
                return;
            }

            _logger.LogInformation("Analytics Worker started with schedule: {Schedule}", _options.Schedule);

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
                        await FetchAndProcessReportAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    // Normal cancellation, just exit
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in Analytics Worker");
                    
                    // Wait for a bit before retrying to avoid rapid-fire errors
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
            }

            _logger.LogInformation("Analytics Worker stopped");
        }

        private async Task FetchAndProcessReportAsync()
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
                var dateRange = new Google.Analytics.Data.V1Beta.DateRange
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

                // Process the report
                await ProcessReportAsync(report);

                _logger.LogInformation("Successfully fetched and processed analytics report");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch or process analytics report");
                throw;
            }
        }

        private async Task ProcessReportAsync(Google.Analytics.Data.V1Beta.RunReportResponse report)
        {
            try
            {
                // Log basic report information
                _logger.LogInformation("Report contains {RowCount} rows", report.Rows.Count);
                
                // Log dimension headers
                foreach (var dimensionHeader in report.DimensionHeaders)
                {
                    _logger.LogInformation("Dimension: {Name} ({DisplayName})", dimensionHeader.Name, dimensionHeader.DisplayName);
                }

                // Log metric headers
                foreach (var metricHeader in report.MetricHeaders)
                {
                    _logger.LogInformation("Metric: {Name} ({DisplayName}, Type: {Type})", 
                        metricHeader.Name, metricHeader.DisplayName, metricHeader.Type);
                }

                // Process and log each row
                foreach (var row in report.Rows)
                {
                    var dimensionValues = string.Join(", ", row.DimensionValues);
                    var metricValues = string.Join(", ", row.MetricValues.Select(mv => mv.Value));
                    
                    _logger.LogInformation("Row - Dimensions: [{DimensionValues}], Metrics: [{MetricValues}]", 
                        dimensionValues, metricValues);
                }

                // Here you could add additional processing like:
                // - Storing the report in a database
                // - Sending notifications
                // - Aggregating data
                // - Exporting to other systems
                
                // For demonstration, we'll save a summary to a file
                await SaveReportSummaryAsync(report);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing report");
                throw;
            }
        }

        private async Task SaveReportSummaryAsync(Google.Analytics.Data.V1Beta.RunReportResponse report)
        {
            try
            {
                var summary = new
                {
                    GeneratedAt = DateTime.UtcNow,
                    RowCount = report.Rows.Count,
                    Dimensions = report.DimensionHeaders.Select(d => new { d.Name, d.DisplayName }).ToList(),
                    Metrics = report.MetricHeaders.Select(m => new { m.Name, m.DisplayName, m.Type }).ToList(),
                    SampleRows = report.Rows.Take(5).Select(row => new
                    {
                        Dimensions = row.DimensionValues.ToList(),
                        Metrics = row.MetricValues.Select(mv => mv.Value).ToList()
                    }).ToList()
                };

                var fileName = $"analytics_report_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), "reports", fileName);
                
                // Ensure directory exists
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                
                await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
                
                _logger.LogInformation("Report summary saved to: {FilePath}", filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving report summary");
                // Don't throw here - this is not critical to the main functionality
            }
        }
    }

    public class AnalyticsWorkerOptions
    {
        public bool Enabled { get; set; } = false;
        public string UserId { get; set; } = string.Empty;
        public string PropertyId { get; set; } = string.Empty;
        public string Schedule { get; set; } = "0 0 * * *"; // Daily at midnight
        public List<string> Dimensions { get; set; } = new List<string>();
        public List<string> Metrics { get; set; } = new List<string>();
    }
}