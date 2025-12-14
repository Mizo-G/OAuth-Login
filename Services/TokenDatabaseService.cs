using Microsoft.EntityFrameworkCore;
using OAuthLogin.Data;
using OAuthLogin.Models;

namespace OAuthLogin.Services
{
    /// <summary>
    /// Service for managing OAuth tokens in PostgreSQL database.
    /// Provides CRUD operations for token storage, retrieval, and lifecycle management.
    /// Includes automatic token expiration handling and deactivation.
    /// </summary>
    public class TokenDatabaseService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<TokenDatabaseService> _logger;

        public TokenDatabaseService(ApplicationDbContext context, ILogger<TokenDatabaseService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<TokenRecord?> GetTokenAsync(string userId)
        {
            try
            {
                return await _context.UserTokens
                    .Where(t => t.UserId == userId && t.IsActive)
                    .OrderByDescending(t => t.CreatedAt)
                    .FirstOrDefaultAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving token for user: {UserId}", userId);
                throw;
            }
        }

        public async Task<List<TokenRecord>> GetAllTokensAsync(string userId)
        {
            try
            {
                return await _context.UserTokens
                    .Where(t => t.UserId == userId)
                    .OrderByDescending(t => t.CreatedAt)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving all tokens for user: {UserId}", userId);
                throw;
            }
        }

        public async Task<TokenRecord> StoreTokenAsync(string userId, string accessToken, string refreshToken, 
            string tokenType, int? expiresIn, string scope)
        {
            try
            {
                // Deactivate existing tokens for this user
                await DeactivateAllTokensAsync(userId);

                // Calculate expiration time
                DateTime? expiresAt = null;
                if (expiresIn.HasValue)
                {
                    expiresAt = DateTime.UtcNow.AddSeconds(expiresIn.Value);
                }

                // Create new token record
                var tokenRecord = new TokenRecord
                {
                    UserId = userId,
                    AccessToken = accessToken,
                    RefreshToken = refreshToken,
                    TokenType = tokenType,
                    ExpiresIn = expiresIn,
                    Scope = scope,
                    ExpiresAt = expiresAt,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                _context.UserTokens.Add(tokenRecord);
                await _context.SaveChangesAsync();

                _logger.LogInformation("Stored new token for user: {UserId}", userId);
                return tokenRecord;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error storing token for user: {UserId}", userId);
                throw;
            }
        }

        public async Task<TokenRecord> UpdateTokenAsync(string userId, string accessToken, string refreshToken, 
            string tokenType, int? expiresIn, string scope)
        {
            try
            {
                // Get existing active token
                var existingToken = await GetTokenAsync(userId);
                
                if (existingToken != null)
                {
                    // Update existing token
                    existingToken.AccessToken = accessToken;
                    existingToken.RefreshToken = refreshToken;
                    existingToken.TokenType = tokenType;
                    existingToken.ExpiresIn = expiresIn;
                    existingToken.Scope = scope;
                    existingToken.UpdatedAt = DateTime.UtcNow;
                    
                    if (expiresIn.HasValue)
                    {
                        existingToken.ExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn.Value);
                    }

                    await _context.SaveChangesAsync();
                    
                    _logger.LogInformation("Updated existing token for user: {UserId}", userId);
                    return existingToken;
                }
                else
                {
                    // Create new token if none exists
                    return await StoreTokenAsync(userId, accessToken, refreshToken, tokenType, expiresIn, scope);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating token for user: {UserId}", userId);
                throw;
            }
        }

        public async Task DeactivateAllTokensAsync(string userId)
        {
            try
            {
                var tokens = await _context.UserTokens
                    .Where(t => t.UserId == userId && t.IsActive)
                    .ToListAsync();

                foreach (var token in tokens)
                {
                    token.IsActive = false;
                    token.UpdatedAt = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync();
                
                _logger.LogInformation("Deactivated {Count} tokens for user: {UserId}", tokens.Count, userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deactivating tokens for user: {UserId}", userId);
                throw;
            }
        }

        public async Task DeleteTokenAsync(int tokenId)
        {
            try
            {
                var token = await _context.UserTokens.FindAsync(tokenId);
                if (token != null)
                {
                    _context.UserTokens.Remove(token);
                    await _context.SaveChangesAsync();
                    
                    _logger.LogInformation("Deleted token with ID: {TokenId}", tokenId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting token with ID: {TokenId}", tokenId);
                throw;
            }
        }

        public async Task DeleteAllTokensAsync(string userId)
        {
            try
            {
                var tokens = await _context.UserTokens
                    .Where(t => t.UserId == userId)
                    .ToListAsync();

                _context.UserTokens.RemoveRange(tokens);
                await _context.SaveChangesAsync();
                
                _logger.LogInformation("Deleted {Count} tokens for user: {UserId}", tokens.Count, userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting all tokens for user: {UserId}", userId);
                throw;
            }
        }

        public async Task<List<TokenRecord>> GetExpiredTokensAsync()
        {
            try
            {
                return await _context.UserTokens
                    .Where(t => t.ExpiresAt.HasValue && t.ExpiresAt.Value < DateTime.UtcNow && t.IsActive)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving expired tokens");
                throw;
            }
        }

        public async Task DeactivateExpiredTokensAsync()
        {
            try
            {
                var expiredTokens = await GetExpiredTokensAsync();
                
                foreach (var token in expiredTokens)
                {
                    token.IsActive = false;
                    token.UpdatedAt = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync();
                
                _logger.LogInformation("Deactivated {Count} expired tokens", expiredTokens.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deactivating expired tokens");
                throw;
            }
        }
    }
}