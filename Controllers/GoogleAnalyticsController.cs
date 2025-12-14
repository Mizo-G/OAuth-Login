using Microsoft.AspNetCore.Mvc;
using OAuthLogin.Services;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Analytics.Data.V1Beta;

namespace OAuthLogin.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class GoogleAnalyticsController : ControllerBase
    {
        private readonly GoogleAnalyticsPermissionsService _permissionsService;
        private readonly GoogleAnalyticsReportService _reportService;
        private readonly GoogleSecretManagerService _secretManagerService;

        public GoogleAnalyticsController(GoogleAnalyticsPermissionsService permissionsService, GoogleAnalyticsReportService reportService, GoogleSecretManagerService secretManagerService)
        {
            _permissionsService = permissionsService;
            _reportService = reportService;
            _secretManagerService = secretManagerService;
        }

        [HttpPost("oauth/callback")]
        public async Task<IActionResult> ExchangeCodeForTokens([FromBody] OAuthCallbackRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.Code))
                {
                    return BadRequest(new { error = "Authorization code is required" });
                }

                if (string.IsNullOrEmpty(request.RedirectUri))
                {
                    return BadRequest(new { error = "Redirect URI is required" });
                }

                var tokenResponse = await _permissionsService.ExchangeCodeForTokensAsync(request.Code, request.RedirectUri);

                var response = new OAuthTokenResponse
                {
                    AccessToken = tokenResponse.AccessToken,
                    RefreshToken = tokenResponse.RefreshToken,
                    TokenType = tokenResponse.TokenType,
                    ExpiresIn = tokenResponse.ExpiresInSeconds,
                    Scope = tokenResponse.Scope
                };

                // Store tokens in Secret Manager if UserId is provided
                if (!string.IsNullOrEmpty(request.UserId))
                {
                    await _secretManagerService.StoreTokensAsync(
                        request.UserId,
                        tokenResponse.AccessToken,
                        tokenResponse.RefreshToken ?? string.Empty
                    );
                }

                return Ok(response);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Failed to exchange authorization code", details = ex.Message });
            }
        }

        [HttpPost("oauth/refresh")]
        public async Task<IActionResult> RefreshAccessToken([FromBody] RefreshTokenRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.RefreshToken))
                {
                    return BadRequest(new { error = "Refresh token is required" });
                }

                var tokenResponse = await _permissionsService.RefreshAccessTokenAsync(request.RefreshToken);

                return Ok(new OAuthTokenResponse
                {
                    AccessToken = tokenResponse.AccessToken,
                    RefreshToken = tokenResponse.RefreshToken,
                    TokenType = tokenResponse.TokenType,
                    ExpiresIn = tokenResponse.ExpiresInSeconds,
                    Scope = tokenResponse.Scope
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Failed to refresh access token", details = ex.Message });
            }
        }

        [HttpGet("oauth/authorize")]
        public IActionResult GetAuthorizationUrl([FromQuery] string redirectUri, [FromQuery] string state = null)
        {
            try
            {
                if (string.IsNullOrEmpty(redirectUri))
                {
                    return BadRequest(new { error = "Redirect URI is required" });
                }

                var authUrl = _permissionsService.GetAuthorizationUrl(redirectUri, state);
                
                return Ok(new { authorizationUrl = authUrl });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Failed to generate authorization URL", details = ex.Message });
            }
        }

        [HttpPost("report")]
        public async Task<IActionResult> GetReport([FromBody] ReportRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.AccessToken) && string.IsNullOrEmpty(request.RefreshToken))
                {
                    return BadRequest(new { error = "Access token or refresh token is required" });
                }

                if (string.IsNullOrEmpty(request.PropertyId))
                {
                    return BadRequest(new { error = "Property ID is required" });
                }

                if (request.Dimensions == null || !request.Dimensions.Any())
                {
                    return BadRequest(new { error = "At least one dimension is required" });
                }

                if (request.Metrics == null || !request.Metrics.Any())
                {
                    return BadRequest(new { error = "At least one metric is required" });
                }

                var dateRange = request.DateRange ?? new DateRange
                {
                    StartDate = "28daysAgo",
                    EndDate = "today"
                };

                RunReportResponse response;
                
                if (!string.IsNullOrEmpty(request.AccessToken))
                {
                    response = await _reportService.GetReportAsync(request.AccessToken, request.PropertyId, request.Dimensions, request.Metrics, dateRange);
                }
                else
                {
                    response = await _reportService.GetReportWithRefreshTokenAsync(request.RefreshToken, request.PropertyId, request.Dimensions, request.Metrics, dateRange);
                }

                return Ok(new ReportResponse
                {
                    DimensionHeaders = response.DimensionHeaders.Select(dh => new DimensionHeaderResponse
                    {
                        Name = dh.Name,
                        DisplayName = dh.DisplayName
                    }).ToList(),
                    MetricHeaders = response.MetricHeaders.Select(mh => new MetricHeaderResponse
                    {
                        Name = mh.Name,
                        DisplayName = mh.DisplayName,
                        Type = mh.Type
                    }).ToList(),
                    Rows = response.Rows.Select(row => new RowResponse
                    {
                        DimensionValues = row.DimensionValues,
                        MetricValues = row.MetricValues.Select(mv => new MetricValueResponse
                        {
                            Value = mv.Value
                        }).ToList()
                    }).ToList(),
                    RowCount = response.RowCount,
                    Metadata = response.Metadata != null ? new MetadataResponse
                    {
                        CurrencyCode = response.Metadata.CurrencyCode,
                        TimeZone = response.Metadata.TimeZone
                    } : null
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Failed to retrieve report", details = ex.Message });
            }
        }

        [HttpPost("accounts")]
        public async Task<IActionResult> GetAccountSummaries([FromBody] AccountSummariesRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.AccessToken) && string.IsNullOrEmpty(request.RefreshToken))
                {
                    return BadRequest(new { error = "Access token or refresh token is required" });
                }

                var accountSummaries = !string.IsNullOrEmpty(request.AccessToken)
                    ? await _reportService.GetAccountSummariesAsync(request.AccessToken)
                    : await _reportService.GetAccountSummariesWithRefreshTokenAsync(request.RefreshToken);

                return Ok(new AccountSummariesResponse
                {
                    Accounts = accountSummaries.Select(account => new AccountSummaryResponse
                    {
                        Name = account.Name,
                        DisplayName = account.DisplayName,
                        Account = account.Account,
                        Properties = account.Properties.Select(prop => new PropertySummaryResponse
                        {
                            Property = prop.Property,
                            DisplayName = prop.DisplayName,
                            PropertyType = prop.PropertyType,
                            Parent = prop.Parent
                        }).ToList()
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Failed to retrieve account summaries", details = ex.Message });
            }
        }

        [HttpPost("oauth/store-tokens")]
        public async Task<IActionResult> StoreTokens([FromBody] StoreTokensRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.UserId))
                {
                    return BadRequest(new { error = "User ID is required" });
                }

                if (string.IsNullOrEmpty(request.AccessToken))
                {
                    return BadRequest(new { error = "Access token is required" });
                }

                if (string.IsNullOrEmpty(request.RefreshToken))
                {
                    return BadRequest(new { error = "Refresh token is required" });
                }

                await _secretManagerService.StoreTokensAsync(request.UserId, request.AccessToken, request.RefreshToken);

                return Ok(new { message = "Tokens stored successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Failed to store tokens", details = ex.Message });
            }
        }

        [HttpGet("oauth/tokens/{userId}")]
        public async Task<IActionResult> GetTokens(string userId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId))
                {
                    return BadRequest(new { error = "User ID is required" });
                }

                var tokens = await _secretManagerService.GetTokensAsync(userId);

                if (tokens == null)
                {
                    return NotFound(new { error = "Tokens not found for the specified user" });
                }

                return Ok(new TokenDataResponse
                {
                    AccessToken = tokens.AccessToken,
                    RefreshToken = tokens.RefreshToken,
                    CreatedAt = tokens.CreatedAt,
                    UpdatedAt = tokens.UpdatedAt
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Failed to retrieve tokens", details = ex.Message });
            }
        }

        [HttpDelete("oauth/tokens/{userId}")]
        public async Task<IActionResult> DeleteTokens(string userId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId))
                {
                    return BadRequest(new { error = "User ID is required" });
                }

                await _secretManagerService.DeleteTokensAsync(userId);

                return Ok(new { message = "Tokens deleted successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Failed to delete tokens", details = ex.Message });
            }
        }
    }

    public record ReportRequest(
        string AccessToken = "",
        string RefreshToken = "",
        string PropertyId = "",
        List<string> Dimensions = null,
        List<string> Metrics = null,
        DateRange? DateRange = null
    )
    {
        public List<string> Dimensions { get; init; } = Dimensions ?? new List<string>();
        public List<string> Metrics { get; init; } = Metrics ?? new List<string>();
    }

    public record ReportResponse(
        List<DimensionHeaderResponse> DimensionHeaders = null,
        List<MetricHeaderResponse> MetricHeaders = null,
        List<RowResponse> Rows = null,
        int? RowCount = null,
        MetadataResponse? Metadata = null
    )
    {
        public List<DimensionHeaderResponse> DimensionHeaders { get; init; } = DimensionHeaders ?? new List<DimensionHeaderResponse>();
        public List<MetricHeaderResponse> MetricHeaders { get; init; } = MetricHeaders ?? new List<MetricHeaderResponse>();
        public List<RowResponse> Rows { get; init; } = Rows ?? new List<RowResponse>();
    }

    public record DimensionHeaderResponse(
        string Name = "",
        string DisplayName = ""
    );

    public record MetricHeaderResponse(
        string Name = "",
        string DisplayName = "",
        string Type = ""
    );

    public record RowResponse(
        List<string> DimensionValues = null,
        List<MetricValueResponse> MetricValues = null
    )
    {
        public List<string> DimensionValues { get; init; } = DimensionValues ?? new List<string>();
        public List<MetricValueResponse> MetricValues { get; init; } = MetricValues ?? new List<MetricValueResponse>();
    }

    public record MetricValueResponse(
        string Value = ""
    );

    public record MetadataResponse(
        string CurrencyCode = "",
        string TimeZone = ""
    );

    public record AccountSummariesRequest(
        string AccessToken = "",
        string RefreshToken = ""
    );

    public record AccountSummariesResponse(
        List<AccountSummaryResponse> Accounts = null
    )
    {
        public List<AccountSummaryResponse> Accounts { get; init; } = Accounts ?? new List<AccountSummaryResponse>();
    }

    public record AccountSummaryResponse(
        string Name = "",
        string DisplayName = "",
        string Account = "",
        List<PropertySummaryResponse> Properties = null
    )
    {
        public List<PropertySummaryResponse> Properties { get; init; } = Properties ?? new List<PropertySummaryResponse>();
    }

    public record PropertySummaryResponse(
        string Property = "",
        string DisplayName = "",
        string PropertyType = "",
        string Parent = ""
    );

    public record OAuthCallbackRequest(
        string Code = "",
        string RedirectUri = "",
        string UserId = ""
    );

    public record StoreTokensRequest(
        string UserId = "",
        string AccessToken = "",
        string RefreshToken = ""
    );

    public record TokenDataResponse(
        string AccessToken = "",
        string RefreshToken = "",
        DateTime CreatedAt = default,
        DateTime UpdatedAt = default
    );

    public record RefreshTokenRequest(
        string RefreshToken = ""
    );

    public record OAuthTokenResponse(
        string AccessToken = "",
        string RefreshToken = "",
        string TokenType = "",
        int? ExpiresIn = null,
        string Scope = ""
    );
}