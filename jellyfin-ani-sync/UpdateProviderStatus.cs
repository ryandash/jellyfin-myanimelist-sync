#nullable enable
using jellyfin_ani_sync.Api;
using jellyfin_ani_sync.Configuration;
using jellyfin_ani_sync.Helpers;
using jellyfin_ani_sync.Interfaces;
using jellyfin_ani_sync.Models;
using jellyfin_ani_sync.Models.Mal;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace jellyfin_ani_sync
{
    public class UpdateProviderStatus
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IApplicationPaths? _applicationPaths;
        private readonly IServerApplicationHost _serverApplicationHost;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IMemoryCache _memoryCache;
        private readonly IAsyncDelayer _delayer;

        private readonly ILogger<UpdateProviderStatus> _logger;

        internal IApiCallHelpers ApiCallHelpers;
        private UserConfig? _userConfig;
        private Type? _animeType;
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem? _fileSystem;

        private readonly ILoggerFactory _loggerFactory;
        private AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse _apiIds = new();

        public UpdateProviderStatus(IFileSystem fileSystem,
            ILibraryManager libraryManager,
            ILoggerFactory loggerFactory,
            IHttpContextAccessor httpContextAccessor,
            IServerApplicationHost serverApplicationHost,
            IHttpClientFactory httpClientFactory,
            IApplicationPaths applicationPaths,
            IMemoryCache memoryCache,
            IAsyncDelayer delayer)
        {
            _fileSystem = fileSystem;
            _libraryManager = libraryManager;
            _httpContextAccessor = httpContextAccessor;
            _serverApplicationHost = serverApplicationHost;
            _httpClientFactory = httpClientFactory;
            _applicationPaths = applicationPaths;
            _logger = loggerFactory.CreateLogger<UpdateProviderStatus>();
            _loggerFactory = loggerFactory;
            _memoryCache = memoryCache;
            _delayer = delayer;
        }

        public async Task Update(BaseItem e, Guid? userId, bool playedToCompletion)
        {
            var video = e as Video;
            Episode? episode = video as Episode;
            Movie? movie = video as Movie;
            if (video is Episode)
            {
                _animeType = typeof(Episode);
            }
            else if (video is Movie)
            {
                _animeType = typeof(Movie);
                video.IndexNumber = 1;
            }

            _userConfig = Plugin.Instance?.PluginConfiguration.UserConfig.FirstOrDefault(item => item.UserId == userId);
            if (_userConfig == null)
            {
                _logger.LogWarning($"The user {userId} does not exist in the plugins config file. Skipping");
                return;
            }

            if (_userConfig.UserApiAuth == null)
            {
                _logger.LogWarning($"The user {userId} is not authenticated. Skipping");
                return;
            }

            if (LibraryCheck(_userConfig, _libraryManager, _fileSystem, _logger, e) && video is Episode or Movie && playedToCompletion)
            {
                if ((video is Episode && (episode.IndexNumber == null ||
                                          episode.Season.IndexNumber == null)) ||
                    (video is Movie && movie.IndexNumber == null))
                {
                    _logger.LogError("Video does not contain required index numbers to sync; skipping");
                    return;
                }
                int? myAnimeListId = int.Parse(_animeType == typeof(Episode)
                        ? episode.Season.ProviderIds["MyAnimeList"]
                        : movie.ProviderIds["MyAnimeList"]);

                ApiCallHelpers = new ApiCallHelpers(malApiCalls: new MalApiCalls(_httpClientFactory, _loggerFactory, _serverApplicationHost, _httpContextAccessor, _memoryCache, _delayer, _userConfig));
                if (_apiIds.MyAnimeList != null && _apiIds.MyAnimeList != 0 && (episode != null && episode.Season.IndexNumber.Value != 0))
                {
                    await CheckUserListAnimeStatus(_apiIds.MyAnimeList.Value, _animeType == typeof(Episode)
                            ? episode.IndexNumber.Value
                            : movie.IndexNumber.Value);
                }

                Anime? matchingAnime = (await ApiCallHelpers.GetAnime(myAnimeListId.Value));
                _logger.LogInformation($"Found matching {_animeType}: {GetAnimeTitle(matchingAnime)}");
                if (_animeType == typeof(Episode))
                {
                    int episodeNumber = episode.IndexNumber.Value;

                    if (episode?.Season.IndexNumber == 0)
                    {
                        // the episode is an ova or special
                        matchingAnime = await GetOva(matchingAnime.Id, episode.Name, alternativeId: matchingAnime.AlternativeId);
                        if (matchingAnime == null)
                        {
                            _logger.LogWarning($"Could not find OVA");
                            return;
                        }
                    }
                    else if (matchingAnime.NumEpisodes < episode?.IndexNumber.Value)
                    {
                        _logger.LogInformation($"Watched episode passes total episodes in season! Checking for additional seasons/cours...");
                        // either we have found the wrong series (highly unlikely) or it is a multi cour series/Jellyfin has grouped next season into the current.
                        int seasonEpisodeCounter = matchingAnime.NumEpisodes;
                        int totalEpisodesWatched = 0;
                        int seasonCounter = episode.Season.IndexNumber.Value;
                        int episodeCount = episode.IndexNumber.Value;
                        Anime? season = matchingAnime;
                        bool isRootSeason = false;
                        if (matchingAnime.NumEpisodes != 0)
                        {
                            while (seasonEpisodeCounter < episodeCount)
                            {

                                var nextSeason = await GetDifferentSeasonAnime(season.Id, seasonCounter + 1, alternativeId: season.AlternativeId);
                                if (nextSeason == null)
                                {
                                    _logger.LogWarning($"Could not find next season");
                                    if (matchingAnime.Status == AiringStatus.currently_airing && matchingAnime.NumEpisodes == 0)
                                    {
                                        _logger.LogWarning($"Show is currently airing and API reports 0 episodes, going to use first season");
                                        isRootSeason = true;
                                    }
                                    break;
                                }

                                seasonEpisodeCounter += nextSeason.NumEpisodes;
                                seasonCounter++;
                                // complete the current season; we have surpassed it onto the next season/cour
                                totalEpisodesWatched += season.NumEpisodes;
                                await CheckUserListAnimeStatus(season.Id, season.NumEpisodes, alternativeId: matchingAnime.AlternativeId);
                                season = nextSeason;
                            }

                            if (!isRootSeason)
                            {
                                if (season.Id != matchingAnime.Id)
                                {
                                    matchingAnime = season;
                                    episodeNumber = episodeCount - totalEpisodesWatched;
                                }
                                else
                                {
                                    return;
                                }
                            }
                        }
                    }
                    await CheckUserListAnimeStatus(matchingAnime.Id, episodeNumber, alternativeId: matchingAnime.AlternativeId);
                    return;
                }

                if (_animeType == typeof(Movie))
                {
                    await CheckUserListAnimeStatus(matchingAnime.Id, movie.IndexNumber.Value, alternativeId: matchingAnime.AlternativeId);
                    return;
                }
            }
        }

        /// <summary>
        /// Gets anime's title.
        /// </summary>
        /// <param name="anime">The API anime</param>
        /// <returns>
        /// <see cref="Anime.Title"/> if it isn't empty.<br/>
        /// If it is, then the first <see cref="AlternativeTitles.Synonyms">synonym</see>.<br/>
        /// If there isn't any, then the <see cref="AlternativeTitles.Ja">japanese title</see>.
        /// </returns>
        private static string GetAnimeTitle(Anime anime)
        {
            var title = string.IsNullOrWhiteSpace(anime.Title)
                ? anime.AlternativeTitles.Synonyms.Count > 0
                    ? anime.AlternativeTitles.Synonyms[0]
                    : anime.AlternativeTitles.Ja
                : anime.Title;
            return title;
        }

        /// <summary>
        /// Check if a string exists in another, ignoring symbols and case.
        /// </summary>
        /// <param name="first">The first string.</param>
        /// <param name="second">The second string.</param>
        /// <returns>True if first string contains second string, false if not.</returns>
        private bool ContainsExtended(string? first, string? second)
        {
            return StringFormatter.RemoveSpecialCharacters(first).Contains(StringFormatter.RemoveSpecialCharacters(second), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks if the supplied item is in a folder the user wants to track for anime updates.
        /// </summary>
        /// <param name="userConfig">User config.</param>
        /// <param name="libraryManager">Library manager instance.</param>
        /// <param name="fileSystem">File system instance.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="item">Item to check location of.</param>
        /// <returns></returns>
        public static bool LibraryCheck(UserConfig userConfig, ILibraryManager libraryManager, IFileSystem? fileSystem, ILogger logger, BaseItem item)
        {
            try
            {
                // user has no library filters
                if (userConfig.LibraryToCheck is { Length: 0 })
                {
                    return true;
                }

                // item is in a path of a folder the user wants to be monitored
                var topParent = item.GetTopParent();
                if (topParent is not null)
                {
                    var allLocations = libraryManager.GetVirtualFolders()
                        .Where(item => userConfig.LibraryToCheck.Contains(item.ItemId))
                        .SelectMany(f => f.Locations)
                        .ToHashSet();
                    if (allLocations.Contains(topParent.Path))
                    {
                        return true;
                    }
                }

                logger.LogInformation("Item is in a folder the user does not want to be monitored; ignoring");
                return false;
            }
            catch (Exception e)
            {
                logger.LogInformation($"Library check ran into an issue: {e.Message}");
                return false;
            }
        }

        private async Task CheckUserListAnimeStatus(int matchingAnimeId, int? episodeNumber, string? alternativeId = null)
        {
            Anime? detectedAnime = await ApiCallHelpers.GetAnime(matchingAnimeId, alternativeId: alternativeId);
            if (detectedAnime == null) return;
            if (detectedAnime.MyListStatus.NumEpisodesWatched >= episodeNumber.Value)
            {
                _logger.LogInformation($"Already watched up to {detectedAnime.MyListStatus.NumEpisodesWatched}"); 
                return;
            }
            if (detectedAnime.MyListStatus != null && detectedAnime.MyListStatus.Status == Status.Watching)
            {
                _logger.LogInformation($"{(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) found on watching list");
                await UpdateAnimeStatus(detectedAnime, episodeNumber);
                return;
            }

            if (detectedAnime.MyListStatus != null && detectedAnime.MyListStatus.Status == Status.Plan_to_watch)
            {
                _logger.LogInformation($"{(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) found on plan to watch list");
                await UpdateAnimeStatus(detectedAnime, episodeNumber);
            }

            // also check if rewatch completed is checked
            _logger.LogInformation($"{(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) not found in plan to watch list{(_userConfig.RewatchCompleted ? ", checking completed list.." : null)}");
            bool updated = true;
            _ = await CheckIfRewatchCompleted(detectedAnime, episodeNumber);

            if (!updated)
            {
                _logger.LogInformation($"Could not update.");
            }

            // only plan to watch
            if (_userConfig.PlanToWatchOnly) return;

            _logger.LogInformation("User does not have plan to watch only ticked");

            // check if rewatch completed is checked
            if (await CheckIfRewatchCompleted(detectedAnime, episodeNumber))
            {
                return;
            }

            // everything else
            if (detectedAnime.MyListStatus != null)
            {
                // anime is on user list
                _logger.LogInformation($"{(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) found on {detectedAnime.MyListStatus.Status} list");
                if (detectedAnime.MyListStatus.Status == Status.Completed)
                {
                    _logger.LogInformation($"{(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) found on Completed list, but user does not want to automatically set as rewatching. Skipping");
                    return;
                }
            }
            else
            {
                _logger.LogInformation($"Could not find {GetAnimeTitle(detectedAnime)}");
            }

            await UpdateAnimeStatus(detectedAnime, episodeNumber);
        }

        private async Task<bool> CheckIfRewatchCompleted(Anime detectedAnime, int? indexNumber)
        {
            if (detectedAnime.MyListStatus is { Status: Status.Completed } ||
                detectedAnime.MyListStatus is { Status: Status.Rewatching } && detectedAnime.MyListStatus.NumEpisodesWatched < indexNumber)
            {
                if (_userConfig.RewatchCompleted)
                {
                    if (detectedAnime.MyListStatus != null && detectedAnime.MyListStatus.Status == Status.Completed)
                    {
                        _logger.LogInformation($"{(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) found on completed list, setting as re-watching");
                        await UpdateAnimeStatus(detectedAnime, indexNumber, true);
                        return true;
                    }
                }
                else
                {
                    _logger.LogInformation($"{(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) found on Completed list, but user does not want to automatically set as rewatching. Skipping");
                    return true;
                }
            }
            else if (detectedAnime.MyListStatus != null && detectedAnime.MyListStatus.NumEpisodesWatched >= indexNumber)
            {
                _logger.LogInformation($"{(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) found, but provider reports episode already watched. Skipping");
                return true;
            }
            else if (_userConfig.PlanToWatchOnly)
            {
                _logger.LogInformation($"{(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) found, but not on completed or plan to watch list. Skipping");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Update a users anime status.
        /// </summary>
        /// <param name="detectedAnime">The anime search result to update.</param>
        /// <param name="episodeNumber">The episode number to update the anime to.</param>
        /// <param name="setRewatching">Whether to set the show as being re-watched or not.</param>
        internal async Task UpdateAnimeStatus(Anime detectedAnime, int? episodeNumber, bool? setRewatching = null)
        {
            if (episodeNumber == null) return;

            bool isSingleEpisode = detectedAnime.NumEpisodes == 1;
            bool isRewatching = detectedAnime.MyListStatus?.IsRewatching ?? false;
            bool alreadyCompleted = detectedAnime.MyListStatus?.Status == Status.Completed;
            bool shouldMarkComplete = episodeNumber.Value == detectedAnime.NumEpisodes || isSingleEpisode;
            bool shouldIncreaseRewatchCount = setRewatching == true || isRewatching;

            var status = shouldMarkComplete ? Status.Completed : Status.Watching;
            var startDate = (!alreadyCompleted && !isRewatching && episodeNumber == 1) ? DateTime.Now : (DateTime?)null;
            var endDate = shouldMarkComplete && !alreadyCompleted ? DateTime.Now : (DateTime?)null;

            int? newRewatchCount = shouldIncreaseRewatchCount ? (detectedAnime.MyListStatus?.RewatchCount ?? 0) + 1 : null;

            // Logging
            string animeTitle = GetAnimeTitle(detectedAnime);
            string animeType = _animeType == typeof(Episode) ? "series" : "movie";

            if (shouldMarkComplete)
            {
                _logger.LogInformation($"Marking {animeType} ({animeTitle}) as completed{(newRewatchCount.HasValue ? ", increasing re-watch count" : "")}.");
            }
            else if (episodeNumber == 1)
            {
                _logger.LogInformation($"Starting new {animeType} ({animeTitle}).");
            }
            else
            {
                _logger.LogInformation($"Updating {animeType} ({animeTitle}) progress to episode {episodeNumber.Value}.");
            }

            // Perform API update
            var response = await ApiCallHelpers.UpdateAnime(
                detectedAnime.Id,
                episodeNumber.Value,
                status,
                startDate: startDate,
                endDate: endDate,
                isRewatching: isRewatching,
                numberOfTimesRewatched: newRewatchCount,
                alternativeId: detectedAnime.AlternativeId,
                ids: _apiIds,
                isShow: _animeType == typeof(Episode)
            );

            if (response == null)
            {
                _logger.LogError($"Could not update anime status for {animeTitle}.");
                return;
            }

            // Special handling for MAL when re-watching
            if (shouldIncreaseRewatchCount)
            {
                _logger.LogInformation($"Increasing re-watch count for {animeType} ({animeTitle}).");
                await ApiCallHelpers.UpdateAnime(
                    detectedAnime.Id,
                    episodeNumber.Value,
                    Status.Completed,
                    numberOfTimesRewatched: response.NumTimesRewatched + 1,
                    isRewatching: false,
                    alternativeId: detectedAnime.AlternativeId,
                    ids: _apiIds,
                    isShow: _animeType == typeof(Episode)
                );
            }
        }


        /// <summary>
        /// Get further anime seasons. Jellyfin uses numbered seasons whereas MAL uses entirely different entities.
        /// </summary>
        /// <param name="animeId">ID of the anime to get the different season of.</param>
        /// <param name="seasonNumber">Index of the season to get.</param>
        /// <returns>The different seasons anime or null if unable to retrieve the relations.</returns>
        internal async Task<Anime?> GetDifferentSeasonAnime(int animeId, int? seasonNumber, string? alternativeId = null)
        {
            _logger.LogInformation($"Attempting to get season 1...");
            Anime? retrievedSeason = await ApiCallHelpers.GetAnime(animeId, getRelated: true, alternativeId: alternativeId);

            if (retrievedSeason != null)
            {
                int i = 1;
                while (i != seasonNumber)
                {
                    RelatedAnime? initialSeasonRelatedAnime = retrievedSeason.RelatedAnime?.FirstOrDefault(item => item.RelationType == RelationType.Sequel);
                    if (initialSeasonRelatedAnime != null)
                    {
                        _logger.LogInformation($"Attempting to get season {i + 1}...");
                        Anime? nextSeason = await ApiCallHelpers.GetAnime(initialSeasonRelatedAnime.Anime.Id, getRelated: true, alternativeId: initialSeasonRelatedAnime.Anime.AlternativeId);

                        if (nextSeason != null)
                        {
                            retrievedSeason = nextSeason;
                        }
                    }
                    else
                    {
                        _logger.LogInformation($"Could not find any related anime sequel");
                        return null;
                    }

                    i++;
                }

                return retrievedSeason;
            }

            return null;
        }

        private async Task<Anime?> GetOva(int animeId, string? episodeName, string? alternativeId = null)
        {
            Anime? anime = await ApiCallHelpers.GetAnime(animeId, getRelated: true, alternativeId: alternativeId);

            if (anime != null)
            {
                var listOfRelatedAnime = anime.RelatedAnime.Where(relation => relation.RelationType is RelationType.Side_Story or RelationType.Alternative_Version or RelationType.Alternative_Setting);
                foreach (RelatedAnime relatedAnime in listOfRelatedAnime)
                {
                    var detailedRelatedAnime = await ApiCallHelpers.GetAnime(relatedAnime.Anime.Id, alternativeId: relatedAnime.Anime.AlternativeId);
                    if (detailedRelatedAnime is { Title: { }, AlternativeTitles: { En: { } } })
                    {
                        if (ContainsExtended(detailedRelatedAnime.Title, episodeName) ||
                            (detailedRelatedAnime.AlternativeTitles.En != null && ContainsExtended(detailedRelatedAnime.AlternativeTitles.En, episodeName)) ||
                            (detailedRelatedAnime.AlternativeTitles.Ja != null && ContainsExtended(detailedRelatedAnime.AlternativeTitles.Ja, episodeName)))
                        {
                            // rough match
                            return detailedRelatedAnime;
                        }
                    }
                }
            }

            // no matches
            return null;
        }
    }
}
