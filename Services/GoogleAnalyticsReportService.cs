using Google.Analytics.Data.V1Beta;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.Configuration;

namespace OAuthLogin.Services
{
    /// <summary>
    /// Service for retrieving Google Analytics reports using OAuth tokens.
    /// Provides methods to fetch reports with specified dimensions, metrics, and date ranges.
    /// Supports both access tokens and refresh tokens for authentication.
    /// </summary>
    public class GoogleAnalyticsReportService
    {
        private readonly IConfiguration _configuration;
        private readonly GoogleAnalyticsPermissionsService _permissionsService;

        public GoogleAnalyticsReportService(IConfiguration configuration, GoogleAnalyticsPermissionsService permissionsService)
        {
            _configuration = configuration;
            _permissionsService = permissionsService;
        }

        public async Task<RunReportResponse> GetReportAsync(string accessToken, string propertyId, List<string> dimensions, List<string> metrics, DateRange? dateRange = null)
        {
            try
            {
                // Create credentials using the access token
                var credential = new GoogleCredential().CreateFromUserCredential(new UserCredential
                {
                    Token = new TokenResponse
                    {
                        AccessToken = accessToken
                    }
                });

                // Create the BetaAnalyticsData client
                var client = new BetaAnalyticsDataClientBuilder
                {
                    Credential = credential
                }.Build();

                // Set default date range if not provided
                if (dateRange == null)
                {
                    dateRange = new DateRange
                    {
                        StartDate = "28daysAgo",
                        EndDate = "today"
                    };
                }

                // Create the report request
                var request = new RunReportRequest
                {
                    Property = $"properties/{propertyId}",
                    DateRanges = { dateRange },
                    Dimensions = { dimensions.Select(d => new Dimension { Name = d }) },
                    Metrics = { metrics.Select(m => new Metric { Name = m }) }
                };

                // Run the report
                var response = await client.RunReportAsync(request);
                return response;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to retrieve Google Analytics report.", ex);
            }
        }

        public async Task<RunReportResponse> GetReportWithRefreshTokenAsync(string refreshToken, string propertyId, List<string> dimensions, List<string> metrics, DateRange? dateRange = null)
        {
            try
            {
                // First refresh the token to get a valid access token
                var tokenResponse = await _permissionsService.RefreshAccessTokenAsync(refreshToken);
                
                // Use the new access token to get the report
                return await GetReportAsync(tokenResponse.AccessToken, propertyId, dimensions, metrics, dateRange);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to retrieve Google Analytics report using refresh token.", ex);
            }
        }

        public async Task<List<AccountSummary>> GetAccountSummariesAsync(string accessToken)
        {
            try
            {
                // Create credentials using the access token
                var credential = new GoogleCredential().CreateFromUserCredential(new UserCredential
                {
                    Token = new TokenResponse
                    {
                        AccessToken = accessToken
                    }
                });

                // Create the Analytics Admin Service client
                var client = new Google.Analytics.Admin.V1Beta.AnalyticsAdminServiceClientBuilder
                {
                    Credential = credential
                }.Build();

                // Get account summaries
                var request = new Google.Analytics.Admin.V1Beta.ListAccountSummariesRequest();
                var response = await client.ListAccountSummariesAsync(request);
                
                return response.AccountSummaries.ToList();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to retrieve Google Analytics account summaries.", ex);
            }
        }

        public async Task<List<AccountSummary>> GetAccountSummariesWithRefreshTokenAsync(string refreshToken)
        {
            try
            {
                // First refresh the token to get a valid access token
                var tokenResponse = await _permissionsService.RefreshAccessTokenAsync(refreshToken);
                
                // Use the new access token to get the account summaries
                return await GetAccountSummariesAsync(tokenResponse.AccessToken);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to retrieve Google Analytics account summaries using refresh token.", ex);
            }
        }
    }
}