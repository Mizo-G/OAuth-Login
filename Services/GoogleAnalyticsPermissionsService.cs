using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.Configuration;

namespace OAuthLogin.Services
{
    /// <summary>
    /// Service for handling Google Analytics OAuth permissions and token management.
    /// Provides methods to exchange authorization codes for tokens, refresh tokens,
    /// and generate authorization URLs for Google Analytics API access.
    /// </summary>
    public class GoogleAnalyticsPermissionsService
    {
        private readonly IConfiguration _configuration;
        private readonly GoogleAuthorizationCodeFlow _flow;

        public GoogleAnalyticsPermissionsService(IConfiguration configuration)
        {
            _configuration = configuration;
            
            var clientSecrets = new ClientSecrets
            {
                ClientId = _configuration["Google:ClientId"] ?? throw new ArgumentNullException("Google:ClientId"),
                ClientSecret = _configuration["Google:ClientSecret"] ?? throw new ArgumentNullException("Google:ClientSecret")
            };

            _flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = clientSecrets,
                Scopes = new[] { "https://www.googleapis.com/auth/analytics.readonly" },
                DataStore = null
            });
        }

        public async Task<TokenResponse> ExchangeCodeForTokensAsync(string authorizationCode, string redirectUri)
        {
            try
            {
                var tokenResponse = await _flow.ExchangeCodeForTokenAsync(
                    userId: "user", // Can be any identifier for the user
                    code: authorizationCode,
                    redirectUri: redirectUri
                );

                return tokenResponse;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to exchange authorization code for tokens.", ex);
            }
        }

        public async Task<TokenResponse> RefreshAccessTokenAsync(string refreshToken)
        {
            try
            {
                var tokenResponse = await _flow.RefreshTokenAsync(
                    userId: "user",
                    refreshToken: refreshToken
                );

                return tokenResponse;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to refresh access token.", ex);
            }
        }

        public string GetAuthorizationUrl(string redirectUri, string state = null)
        {
            var uriBuilder = new UriBuilder(_flow.AuthorizationServerUrl);
            var query = System.Web.HttpUtility.ParseQueryString(uriBuilder.Query);
            
            query["client_id"] = _flow.ClientSecrets.ClientId;
            query["redirect_uri"] = redirectUri;
            query["response_type"] = "code";
            query["scope"] = string.Join(" ", _flow.Scopes);
            query["access_type"] = "offline";
            query["prompt"] = "consent";
            
            if (!string.IsNullOrEmpty(state))
            {
                query["state"] = state;
            }

            uriBuilder.Query = query.ToString();
            return uriBuilder.ToString();
        }
    }
}