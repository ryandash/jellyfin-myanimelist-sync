#nullable enable
using jellyfin_ani_sync.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace jellyfin_ani_sync.Api
{
    public class ApiAuthentication
    {
        private readonly ApiName _provider;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ApiAuthentication> _logger;
        private readonly string _authApiUrl;
        private readonly string _redirectUrl;
        private readonly ProviderApiAuth _providerApiAuth;
        private readonly IMemoryCache  _memoryCache;
        private readonly string _codeChallenge = "eZBLUX_JPk4~el62z_k3Q4fV5CzCYHoTz4iLKvwJ~9QTsTJNlzwveKCSYCSiSOa5zAm5Zt~cfyVM~3BuO4kQ0iYwCxPoeN0SOmBYR_C.QgnzyYE4KY-xIe4Vy1bf7_B4";

        public ApiAuthentication(ApiName provider, IHttpClientFactory httpClientFactory, IServerApplicationHost serverApplicationHost, IHttpContextAccessor httpContextAccessor, ILoggerFactory loggerFactory, ProviderApiAuth? overrideProviderApiAuth = null, string? overrideRedirectUrl = null)
        {
            _provider = provider;

            if (provider == ApiName.Mal)
            {
                _authApiUrl = "https://myanimelist.net/v1/oauth2";
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(provider), provider, null);
            }

            _httpClientFactory = httpClientFactory;
            _memoryCache = memoryCache;
            _logger = loggerFactory.CreateLogger<ApiAuthentication>();

            _providerApiAuth = overrideProviderApiAuth ??
                Plugin.Instance?.PluginConfiguration.ProviderApiAuth?.FirstOrDefault(item => item.Name == _provider)
                ?? throw new NullReferenceException($"No {provider} provider API auth in plugin config");

            var userCallbackUrl = Plugin.Instance.PluginConfiguration.callbackUrl;
            if (overrideRedirectUrl != null && overrideRedirectUrl != "local")
            {
                _redirectUrl = overrideRedirectUrl + "/AniSync/authCallback";
            }
            else
            {
                if (overrideRedirectUrl == "local" && httpContextAccessor.HttpContext != null)
                {
                    _redirectUrl = serverApplicationHost.ListenWithHttps ? $"https://{httpContextAccessor.HttpContext.Connection.LocalIpAddress}:{serverApplicationHost.HttpsPort}/AniSync/authCallback" : $"http://{httpContextAccessor.HttpContext.Connection.LocalIpAddress}:{serverApplicationHost.HttpPort}/AniSync/authCallback";
                }
                else
                {
                    _redirectUrl = userCallbackUrl != null
                        ? userCallbackUrl + "/AniSync/authCallback"
                        : serverApplicationHost.ListenWithHttps
                            ? $"https://{httpContextAccessor.HttpContext.Connection.LocalIpAddress}:{serverApplicationHost.HttpsPort}/AniSync/authCallback"
                            : $"http://{httpContextAccessor.HttpContext.Connection.LocalIpAddress}:{serverApplicationHost.HttpPort}/AniSync/authCallback";
                }
            }
        }

        public string BuildAuthorizeRequestUrl()
        {
            if (_provider == ApiName.Mal)
            {
                return $"{_authApiUrl}/authorize?response_type=code&client_id={_providerApiAuth.ClientId}&code_challenge={_codeChallenge}&redirect_uri={_redirectUrl}";
            }

            throw new ArgumentOutOfRangeException();
        }

        /// <summary>
        /// Get a new API auth token.
        /// </summary>
        /// <param name="httpClientFactory"></param>
        /// <param name="code">Optional auth code to generate a new token with.</param>
        /// <param name="refreshToken">Optional refresh token to refresh an existing token with.</param>
        public UserApiAuth GetToken(Guid userId, string? code = null, string? refreshToken = null)
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            HttpContent formUrlEncodedContent;

            if (refreshToken != null)
            {
                formUrlEncodedContent = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("client_id", _providerApiAuth.ClientId),
                    new KeyValuePair<string, string>("client_secret", _providerApiAuth.ClientSecret),
                    new KeyValuePair<string, string>("grant_type", "refresh_token"),
                    new KeyValuePair<string, string>("refresh_token", refreshToken)
                });
            }
            else
            {
                var content = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("client_id", _providerApiAuth.ClientId),
                    new KeyValuePair<string, string>("client_secret", _providerApiAuth.ClientSecret),
                    new KeyValuePair<string, string>("code", code),
                    new KeyValuePair<string, string>("grant_type", "authorization_code"),
                    new KeyValuePair<string, string>("redirect_uri", _redirectUrl)
                };
                content.Add(new KeyValuePair<string, string>("code_verifier", _codeChallenge));
                formUrlEncodedContent = new FormUrlEncodedContent(content);
            }

            var response = client.PostAsync(new Uri($"{_authApiUrl}/token"), formUrlEncodedContent).Result;

            if (response.IsSuccessStatusCode)
            {
                var content = response.Content.ReadAsStream();
                var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(new StreamReader(content).ReadToEnd());

                var pluginConfig = Plugin.Instance.PluginConfiguration.UserConfig.FirstOrDefault(item => item.UserId == userId);

                if (pluginConfig != null)
                {
                    var apiAuth = pluginConfig.UserApiAuth?.FirstOrDefault(item => item.Name == _provider);

                    var newUserApiAuth = new UserApiAuth
                    {
                        Name = _provider,
                        AccessToken = tokenResponse.access_token,
                        RefreshToken = tokenResponse.refresh_token
                    };

                    if (apiAuth != null)
                    {
                        apiAuth.AccessToken = tokenResponse.access_token;
                        apiAuth.RefreshToken = tokenResponse.refresh_token;
                    }
                    else
                    {
                        pluginConfig.AddUserApiAuth(newUserApiAuth);
                    }

                    Plugin.Instance.SaveConfiguration();
                    return newUserApiAuth;
                }

                throw new NullReferenceException("The user you are attempting to authenticate does not exist in the plugin's config file");
            }

            throw new AuthenticationException($"Could not retrieve {_provider} token: {response.StatusCode} - {response.ReasonPhrase}");
        }

        public static string GeneratePkce()
        {
            const string characters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~";
            var chars = new char[128];
            var random = new Random();

            for (var x = 0; x < chars.Length; x++)
            {
                chars[x] = characters[random.Next(characters.Length)];
            }

            return new string(chars);
        }
    }

    public class TokenResponse
    {
        public required string access_token { get; set; }
        public required string refresh_token { get; set; }
    }
}
