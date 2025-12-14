using Google.Cloud.SecretManager.V1;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace OAuthLogin.Services
{
    /// <summary>
    /// Service for managing Google Cloud Secret Manager operations.
    /// Provides methods to store, retrieve, update, and delete OAuth tokens
    /// in Google Cloud Secret Manager for secure token storage and retrieval.
    /// </summary>
    public class GoogleSecretManagerService
    {
        private readonly IConfiguration _configuration;
        private readonly SecretManagerServiceClient _client;

        public GoogleSecretManagerService(IConfiguration configuration)
        {
            _configuration = configuration;
            
            // Initialize the Secret Manager client
            // It will automatically use the application default credentials
            _client = SecretManagerServiceClient.Create();
        }

        public async Task StoreTokensAsync(string userId, string accessToken, string refreshToken, string? projectId = null)
        {
            try
            {
                projectId ??= _configuration["Google:ProjectId"] ?? throw new ArgumentNullException("Google:ProjectId");
                
                // Create a secret name
                var secretName = new SecretName(projectId, $"ga-tokens-{userId}");
                
                // Create the secret if it doesn't exist
                try
                {
                    await _client.GetSecretAsync(secretName);
                }
                catch (Google.Rpc.NotFoundException)
                {
                    // Secret doesn't exist, create it
                    await _client.CreateSecretAsync(new Secret
                    {
                        SecretId = $"ga-tokens-{userId}",
                        Parent = $"projects/{projectId}",
                        Replication = new Replication
                        {
                            Automatic = new Replication.Types.Automatic()
                        }
                    });
                }

                // Create the token data
                var tokenData = new TokenData(
                    accessToken,
                    refreshToken,
                    DateTime.UtcNow,
                    DateTime.UtcNow
                );

                // Serialize the token data to JSON
                var jsonPayload = JsonSerializer.Serialize(tokenData);

                // Create the secret version
                await _client.AddSecretVersionAsync(
                    secretName,
                    new SecretPayload
                    {
                        Data = Google.Protobuf.ByteString.CopyFromUtf8(jsonPayload)
                    }
                );
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to store tokens for user {userId}.", ex);
            }
        }

        public async Task<TokenData?> GetTokensAsync(string userId, string? projectId = null)
        {
            try
            {
                projectId ??= _configuration["Google:ProjectId"] ?? throw new ArgumentNullException("Google:ProjectId");
                
                // Get the latest version of the secret
                var secretVersionName = new SecretVersionName(projectId, $"ga-tokens-{userId}", "latest");
                
                try
                {
                    var response = await _client.AccessSecretVersionAsync(secretVersionName);
                    var jsonPayload = response.Payload.Data.ToStringUtf8();
                    
                    return JsonSerializer.Deserialize<TokenData>(jsonPayload);
                }
                catch (Google.Rpc.NotFoundException)
                {
                    // Secret doesn't exist
                    return null;
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to retrieve tokens for user {userId}.", ex);
            }
        }

        public async Task DeleteTokensAsync(string userId, string? projectId = null)
        {
            try
            {
                projectId ??= _configuration["Google:ProjectId"] ?? throw new ArgumentNullException("Google:ProjectId");
                
                // Delete the secret
                var secretName = new SecretName(projectId, $"ga-tokens-{userId}");
                
                try
                {
                    await _client.DeleteSecretAsync(secretName);
                }
                catch (Google.Rpc.NotFoundException)
                {
                    // Secret doesn't exist, ignore
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to delete tokens for user {userId}.", ex);
            }
        }

        public async Task UpdateTokensAsync(string userId, string accessToken, string refreshToken, string? projectId = null)
        {
            try
            {
                projectId ??= _configuration["Google:ProjectId"] ?? throw new ArgumentNullException("Google:ProjectId");
                
                // Get existing tokens
                var existingTokens = await GetTokensAsync(userId, projectId);
                
                // Create the token data
                var tokenData = new TokenData(
                    accessToken,
                    refreshToken,
                    existingTokens?.CreatedAt ?? DateTime.UtcNow,
                    DateTime.UtcNow
                );

                // Serialize the token data to JSON
                var jsonPayload = JsonSerializer.Serialize(tokenData);

                // Create the secret name
                var secretName = new SecretName(projectId, $"ga-tokens-{userId}");

                // Create the secret version
                await _client.AddSecretVersionAsync(
                    secretName,
                    new SecretPayload
                    {
                        Data = Google.Protobuf.ByteString.CopyFromUtf8(jsonPayload)
                    }
                );
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to update tokens for user {userId}.", ex);
            }
        }
    }

    public record TokenData(
        string AccessToken = "",
        string RefreshToken = "",
        DateTime CreatedAt = default,
        DateTime UpdatedAt = default
    );
}