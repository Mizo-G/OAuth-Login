using Microsoft.AspNetCore.Mvc;
using OAuthLogin.Models;
using OAuthLogin.Services;
using Google.Apis.Auth.OAuth2.Responses;

namespace OAuthLogin.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TokenManagementController : ControllerBase
    {
        private readonly GoogleAnalyticsPermissionsService _permissionsService;
        private readonly GoogleSecretManagerService _secretManagerService;
        private readonly TokenDatabaseService _databaseService;
        private readonly ILogger<TokenManagementController> _logger;

        public TokenManagementController(
            GoogleAnalyticsPermissionsService permissionsService,
            GoogleSecretManagerService secretManagerService,
            TokenDatabaseService databaseService,
            ILogger<TokenManagementController> logger)
        {
            _permissionsService = permissionsService;
            _secretManagerService = secretManagerService;
            _databaseService = databaseService;
            _logger = logger;
        }

        [HttpPost("exchange-and-store")]
        public async Task<IActionResult> ExchangeAndStoreTokens([FromBody] ExchangeAndStoreRequest request)
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

                if (string.IsNullOrEmpty(request.UserId))
                {
                    return BadRequest(new { error = "User ID is required" });
                }

                _logger.LogInformation("Exchanging authorization code for tokens for user: {UserId}", request.UserId);

                // Exchange code for tokens
                var tokenResponse = await _permissionsService.ExchangeCodeForTokensAsync(request.Code, request.RedirectUri);

                _logger.LogInformation("Successfully exchanged code for tokens for user: {UserId}", request.UserId);

                try
                {
                    // Store tokens in Secret Manager
                    await _secretManagerService.StoreTokensAsync(
                        request.UserId,
                        tokenResponse.AccessToken,
                        tokenResponse.RefreshToken ?? string.Empty
                    );

                    _logger.LogInformation("Successfully stored tokens in Secret Manager for user: {UserId}", request.UserId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to store tokens in Secret Manager for user: {UserId}", request.UserId);
                    // Continue with database storage even if Secret Manager fails
                }

                try
                {
                    // Store tokens in database
                    var tokenRecord = await _databaseService.StoreTokenAsync(
                        request.UserId,
                        tokenResponse.AccessToken,
                        tokenResponse.RefreshToken ?? string.Empty,
                        tokenResponse.TokenType,
                        tokenResponse.ExpiresInSeconds,
                        tokenResponse.Scope
                    );

                    _logger.LogInformation("Successfully stored tokens in database for user: {UserId}", request.UserId);

                    return Ok(new ExchangeAndStoreResponse
                    {
                        Success = true,
                        Message = "Tokens successfully stored in both Secret Manager and database",
                        TokenId = tokenRecord.Id,
                        AccessToken = tokenResponse.AccessToken,
                        TokenType = tokenResponse.TokenType,
                        ExpiresIn = tokenResponse.ExpiresInSeconds,
                        Scope = tokenResponse.Scope
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to store tokens in database for user: {UserId}", request.UserId);
                    
                    return StatusCode(500, new { 
                        error = "Failed to store tokens in database", 
                        details = ex.Message,
                        secretManagerStored = true
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to exchange and store tokens");
                return StatusCode(500, new { error = "Failed to exchange and store tokens", details = ex.Message });
            }
        }

        [HttpGet("tokens/{userId}")]
        public async Task<IActionResult> GetTokens(string userId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId))
                {
                    return BadRequest(new { error = "User ID is required" });
                }

                var tokenRecord = await _databaseService.GetTokenAsync(userId);

                if (tokenRecord == null)
                {
                    return NotFound(new { error = "No active tokens found for the specified user" });
                }

                return Ok(new TokenResponse
                {
                    Id = tokenRecord.Id,
                    UserId = tokenRecord.UserId,
                    TokenType = tokenRecord.TokenType,
                    ExpiresIn = tokenRecord.ExpiresIn,
                    Scope = tokenRecord.Scope,
                    CreatedAt = tokenRecord.CreatedAt,
                    UpdatedAt = tokenRecord.UpdatedAt,
                    ExpiresAt = tokenRecord.ExpiresAt,
                    IsActive = tokenRecord.IsActive
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve tokens for user: {UserId}", userId);
                return StatusCode(500, new { error = "Failed to retrieve tokens", details = ex.Message });
            }
        }

        [HttpGet("tokens/{userId}/all")]
        public async Task<IActionResult> GetAllTokens(string userId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId))
                {
                    return BadRequest(new { error = "User ID is required" });
                }

                var tokenRecords = await _databaseService.GetAllTokensAsync(userId);

                return Ok(new AllTokensResponse
                {
                    UserId = userId,
                    Tokens = tokenRecords.Select(t => new TokenResponse
                    {
                        Id = t.Id,
                        UserId = t.UserId,
                        TokenType = t.TokenType,
                        ExpiresIn = t.ExpiresIn,
                        Scope = t.Scope,
                        CreatedAt = t.CreatedAt,
                        UpdatedAt = t.UpdatedAt,
                        ExpiresAt = t.ExpiresAt,
                        IsActive = t.IsActive
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve all tokens for user: {UserId}", userId);
                return StatusCode(500, new { error = "Failed to retrieve all tokens", details = ex.Message });
            }
        }

        [HttpDelete("tokens/{userId}")]
        public async Task<IActionResult> DeleteAllTokens(string userId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId))
                {
                    return BadRequest(new { error = "User ID is required" });
                }

                // Delete from database
                await _databaseService.DeleteAllTokensAsync(userId);

                try
                {
                    // Delete from Secret Manager
                    await _secretManagerService.DeleteTokensAsync(userId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete tokens from Secret Manager for user: {UserId}", userId);
                    // Continue even if Secret Manager deletion fails
                }

                return Ok(new { message = "All tokens deleted successfully from database and Secret Manager" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete tokens for user: {UserId}", userId);
                return StatusCode(500, new { error = "Failed to delete tokens", details = ex.Message });
            }
        }

        [HttpPost("tokens/deactivate-expired")]
        public async Task<IActionResult> DeactivateExpiredTokens()
        {
            try
            {
                await _databaseService.DeactivateExpiredTokensAsync();
                return Ok(new { message = "Expired tokens deactivated successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deactivate expired tokens");
                return StatusCode(500, new { error = "Failed to deactivate expired tokens", details = ex.Message });
            }
        }
    }

    public record ExchangeAndStoreRequest(
        string Code = "",
        string RedirectUri = "",
        string UserId = ""
    );

    public record ExchangeAndStoreResponse(
        bool Success = false,
        string Message = "",
        int TokenId = 0,
        string AccessToken = "",
        string TokenType = "",
        int? ExpiresIn = null,
        string Scope = ""
    );

    public record TokenResponse(
        int Id = 0,
        string UserId = "",
        string TokenType = "",
        int? ExpiresIn = null,
        string Scope = "",
        DateTime CreatedAt = default,
        DateTime UpdatedAt = default,
        DateTime? ExpiresAt = null,
        bool IsActive = false
    );

    public record AllTokensResponse(
        string UserId = "",
        List<TokenResponse> Tokens = null
    )
    {
        public List<TokenResponse> Tokens { get; init; } = Tokens ?? new List<TokenResponse>();
    }
}