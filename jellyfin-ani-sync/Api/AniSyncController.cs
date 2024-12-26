#nullable enable
using jellyfin_ani_sync.Configuration;
using jellyfin_ani_sync.Helpers;
using jellyfin_ani_sync.Interfaces;
using jellyfin_ani_sync.Models;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;

namespace jellyfin_ani_sync.Api
{
    [ApiController]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Route("[controller]")]
    public class AniSyncController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IServerApplicationHost _serverApplicationHost;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IApplicationPaths _applicationPaths;
        private readonly IUserDataManager _userDataManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<AniSyncController> _logger;
        private readonly IMemoryCache _memoryCache;
        private readonly IAsyncDelayer _delayer;

        public AniSyncController(IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory,
            IServerApplicationHost serverApplicationHost,
            IHttpContextAccessor httpContextAccessor,
            ILibraryManager libraryManager,
            IUserManager userManager,
            IApplicationPaths applicationPaths,
            IUserDataManager userDataManager,
            IFileSystem fileSystem,
            IMemoryCache memoryCache)
        {
            _httpClientFactory = httpClientFactory;
            _loggerFactory = loggerFactory;
            _serverApplicationHost = serverApplicationHost;
            _httpContextAccessor = httpContextAccessor;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _applicationPaths = applicationPaths;
            _userDataManager = userDataManager;
            _fileSystem = fileSystem;
            _logger = loggerFactory.CreateLogger<AniSyncController>();
            _memoryCache = memoryCache;
            _delayer = new Delayer();
        }

        [HttpGet]
        [Route("buildAuthorizeRequestUrl")]
        public string BuildAuthorizeRequestUrl(ApiName provider, string clientId, string clientSecret, string? url)
        {
            return new ApiAuthentication(provider, _httpClientFactory, _serverApplicationHost, _httpContextAccessor, _loggerFactory, new ProviderApiAuth { ClientId = clientId, ClientSecret = clientSecret }, url).BuildAuthorizeRequestUrl();
        }

        [HttpGet]
        [Route("testAnimeListSaveLocation")]
        public async Task<IActionResult> TestAnimeSaveLocation(string saveLocation)
        {
            if (String.IsNullOrEmpty(saveLocation))
                return BadRequest("Save location is empty");

            try
            {
                await using (System.IO.File.Create(
                                 Path.Combine(
                                     saveLocation,
                                     Path.GetRandomFileName()
                                 ),
                                 1,
                                 FileOptions.DeleteOnClose))
                {
                }

                return Ok(string.Empty);
            }
            catch (Exception e)
            {
                return BadRequest(e.Message);
            }
        }

        [HttpGet]
        [Route("passwordGrant")]
        public async Task<IActionResult> PasswordGrantAuthentication(ApiName provider, string userId, string username, string password)
        {
            try
            {
                _ = new ApiAuthentication(provider, _httpClientFactory, _serverApplicationHost, _httpContextAccessor, _loggerFactory, new ProviderApiAuth { ClientId = username, ClientSecret = password }).GetToken(Guid.Parse(userId));
            }
            catch (Exception e)
            {
                return StatusCode(500, $"Could not authenticate; {e.Message}");
            }

            return Ok();
        }

        [AllowAnonymous]
        [HttpGet]
        [Route("authCallback")]
        public IActionResult MalCallback(string code)
        {
            Guid userId = Plugin.Instance.PluginConfiguration.currentlyAuthenticatingUser;
            ApiName provider = Plugin.Instance.PluginConfiguration.currentlyAuthenticatingProvider;
            if (userId != null && provider != null)
            {
                _ = new ApiAuthentication(provider, _httpClientFactory, _serverApplicationHost, _httpContextAccessor, _loggerFactory).GetToken(userId, code);
                Plugin.Instance.PluginConfiguration.currentlyAuthenticatingUser = Guid.Empty;
                Plugin.Instance.SaveConfiguration();
                if (!string.IsNullOrEmpty(Plugin.Instance?.PluginConfiguration.callbackRedirectUrl))
                {
                    string replacedCallbackRedirectUrl = Plugin.Instance.PluginConfiguration.callbackRedirectUrl.Replace("{{LocalIpAddress}}", Request.HttpContext.Connection.LocalIpAddress != null ? Request.HttpContext.Connection.LocalIpAddress.ToString() : "localhost")
                        .Replace("{{LocalPort}}", _serverApplicationHost.ListenWithHttps ? _serverApplicationHost.HttpsPort.ToString() : _serverApplicationHost.HttpPort.ToString());

                    if (Uri.TryCreate(replacedCallbackRedirectUrl, UriKind.Absolute, out _))
                    {
                        return Redirect(replacedCallbackRedirectUrl);
                    }
                    else
                    {
                        _logger.LogWarning($"Invalid redirect URL ({replacedCallbackRedirectUrl}), skipping redirect.");
                    }
                }

                return new ObjectResult("Success! Received access token, please contact the Jellyfin administrator to test the authentication.") { StatusCode = 200 };
            }
            else
            {
                _logger.LogError("Authenticated user ID could not be found in the configuration. Please regenerate the authentication URL and try again");
                return StatusCode(500);
            }

            return new ObjectResult("Success! Received access token, please contact the Jellyfin administrator to test the authentication.") { StatusCode = 200 };
        }

        [HttpGet]
        [Route("user")]
        // this only works for mal atm, needs to work for anilist as well
        public async Task<ActionResult> GetUser(ApiName apiName, string userId)
        {
            UserConfig? userConfig = Plugin.Instance?.PluginConfiguration.UserConfig.FirstOrDefault(item => item.UserId == Guid.Parse(userId));
            if (userConfig == null)
            {
                _logger.LogError("User not found in config");
                return StatusCode(500, "User not found in config");
            }

            switch (apiName)
            {
                case ApiName.Mal:
                    MalApiCalls malApiCalls = new MalApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, Plugin.Instance.PluginConfiguration.UserConfig.FirstOrDefault(item => item.UserId == Guid.Parse(userId)));

                    MalApiCalls.User? malUser = await malApiCalls.GetUserInformation();
                    return malUser != null ? new OkObjectResult(malUser) : StatusCode(500, "Authentication failed");
            }

            throw new Exception("Provider not supported.");
        }

        [HttpGet]
        [Route("parameters")]
        public object GetFrontendParameters(ParameterInclude[]? includes)
        {
            Parameters toReturn = new Parameters();
            if (includes == null || includes.Contains(ParameterInclude.ProviderList))
            {
                toReturn.providerList = new List<ExpandoObject>();
                foreach (ApiName apiName in Enum.GetValues<ApiName>())
                {
                    dynamic provider = new ExpandoObject();
                    provider.Name = apiName.GetType()
                        .GetMember(apiName.ToString())
                        .First()
                        .GetCustomAttribute<DisplayAttribute>()
                        ?.GetName();
                    provider.Key = apiName;
                    toReturn.providerList.Add(provider);
                }
            }

            if (includes == null || includes.Contains(ParameterInclude.LocalIpAddress))
                toReturn.localIpAddress = Request.HttpContext.Connection.LocalIpAddress != null ? Request.HttpContext.Connection.LocalIpAddress.ToString() : "localhost";
            if (includes == null || includes.Contains(ParameterInclude.LocalPort))
                toReturn.localPort = _serverApplicationHost.ListenWithHttps ? _serverApplicationHost.HttpsPort : _serverApplicationHost.HttpPort;
            if (includes == null || includes.Contains(ParameterInclude.Https))
                toReturn.https = _serverApplicationHost.ListenWithHttps;
            return toReturn;
        }

        public enum ParameterInclude
        {
            ProviderList = 0,
            LocalIpAddress = 1,
            LocalPort = 2,
            Https = 3
        }

        private class Parameters
        {
            public string localIpAddress { get; set; }
            public int localPort { get; set; }
            public bool https { get; set; }
            public List<ExpandoObject> providerList { get; set; }
        }

        [AllowAnonymous]
        [HttpGet]
        [Route("apiUrlTest")]
        public string ApiUrlTest()
        {
            return "This is the correct URL.";
        }

        [HttpPost]
        [Route("sync")]
        public Task Sync(ApiName provider, string userId, SyncHelper.Status status, SyncAction syncAction)
        {
            switch (syncAction)
            {
                case SyncAction.UpdateProvider:
                    SyncProviderFromLocal syncProviderFromLocal = new SyncProviderFromLocal(_userManager, _libraryManager, _loggerFactory, _httpClientFactory, _applicationPaths, _fileSystem, _memoryCache, _delayer, userId);
                    return syncProviderFromLocal.SyncFromLocal();
                case SyncAction.UpdateJellyfin:
                    Sync sync = new Sync(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _userManager, _libraryManager, _applicationPaths, _userDataManager, _memoryCache, _delayer, provider, status);
                    return sync.SyncFromProvider(userId);
            }

            return Task.CompletedTask;
        }

        public enum SyncAction
        {
            UpdateProvider,
            UpdateJellyfin
        }
    }
}