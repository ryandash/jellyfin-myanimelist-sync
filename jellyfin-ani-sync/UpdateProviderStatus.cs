#nullable enable
using jellyfin_ani_sync.Api;
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
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
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

        private readonly ILoggerFactory _loggerFactory;
        private AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse _apiIds = new();

        public UpdateProviderStatus(ILibraryManager libraryManager,
            ILoggerFactory loggerFactory,
            IHttpContextAccessor httpContextAccessor,
            IServerApplicationHost serverApplicationHost,
            IHttpClientFactory httpClientFactory,
            IApplicationPaths applicationPaths,
            IMemoryCache memoryCache,
            IAsyncDelayer delayer) {
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

            if (LibraryCheck(_userConfig, _libraryManager, _logger, e) && video is Episode or Movie && playedToCompletion) {
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

                // FIX 4: diagnostics. Prints exactly which provider IDs Jellyfin holds at
                // each level, plus the season count that drives the absolute-episode path.
                // Logged at Debug: this runs on every played episode, and the information
                // is only useful when investigating a mis-resolution. The IsEnabled guard
                // keeps the interpolated strings from being built when Debug is off.
                if (_animeType == typeof(Episode) && _logger.IsEnabled(LogLevel.Debug)) {
                    string Dump(Dictionary<string, string> ids) => ids == null || ids.Count == 0
                        ? "<none>"
                        : string.Join(", ", ids.Select(kv => $"{kv.Key}={kv.Value}"));
                    _logger.LogDebug($"[ani-sync-diag] Series '{episode.Series?.Name}' providers: {Dump(episode.Series?.ProviderIds)}");
                    _logger.LogDebug($"[ani-sync-diag] Season {episode.Season?.IndexNumber} providers: {Dump(episode.Season?.ProviderIds)}");
                    _logger.LogDebug($"[ani-sync-diag] Episode {episode.IndexNumber} '{episode.Name}' providers: {Dump(episode.ProviderIds)}");
                    _logger.LogDebug($"[ani-sync-diag] Series contains {episode.Series?.Children.OfType<Season>().Count()} season(s)");
                    _logger.LogDebug($"[ani-sync-diag] Titles -> name: '{episode.SeriesName}', originalTitle: '{episode.Series?.OriginalTitle ?? "<none>"}'");
                }

                (int? aniDbId, int? episodeOffset) aniDbId = (null, null);
                if (_animeType == typeof(Episode)
                        ? episode.ProviderIds != null &&
                          episode.Series.ProviderIds.ContainsKey("AniList") &&
                          episode.Season.IndexNumber.Value == 1 &&
                          int.TryParse(episode.Series.ProviderIds["AniList"], out int retrievedAniListId)
                        : movie.ProviderIds != null &&
                          movie.ProviderIds.ContainsKey("AniList") &&
                          int.TryParse(movie.ProviderIds["AniList"], out retrievedAniListId)) {
                    _logger.LogInformation("AniList ID found. Retrieving provider IDs from offline database...");
                    _apiIds = await AnimeOfflineDatabaseHelpers.GetProviderIdsFromMetadataProvider(_httpClientFactory.CreateClient(NamedClient.Default), _logger, retrievedAniListId, AnimeOfflineDatabaseHelpers.Source.Anilist);
                    if (_apiIds is null) {
                        _apiIds = new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse {
                            Anilist = retrievedAniListId
                        };
                        _logger.LogWarning("Did not get provider IDs, defaulting to episode provided AniList ID");
                    } else {
                        _logger.LogInformation("Retrieved provider IDs");
                    }
                } else if (_animeType == typeof(Episode)
                               ? (AnimeListHelpers.HasProviderId(episode.Series.ProviderIds, "Tvdb") ||
                                  AnimeListHelpers.HasProviderId(episode.Season.ProviderIds, "Anidb") ||
                                  AnimeListHelpers.HasProviderId(episode.Series.ProviderIds, "Anidb"))
                               : movie.ProviderIds != null &&
                                 AnimeListHelpers.HasProviderId(movie.ProviderIds, "Anidb")) {
                    AnimeListHelpers.AnimeListXml animeListXml = await AnimeListHelpers.GetAnimeListFileContents(_logger, _loggerFactory, _httpClientFactory, _applicationPaths);
                    aniDbId = _animeType == typeof(Episode)
                        ? await AnimeListHelpers.GetAniDbId(_logger, episode, episode.IndexNumber.Value, episode.Season.IndexNumber.Value, animeListXml)
                        : await AnimeListHelpers.GetAniDbId(_logger, movie, movie.IndexNumber.Value, 1, animeListXml);
                    if (aniDbId.aniDbId != null) {
                        _logger.LogInformation($"Retrieving provider IDs from offline database for AniDb ID {aniDbId.aniDbId.Value}...");
                        _apiIds = await AnimeOfflineDatabaseHelpers.GetProviderIdsFromMetadataProvider(_httpClientFactory.CreateClient(NamedClient.Default), _logger, aniDbId.aniDbId.Value, AnimeOfflineDatabaseHelpers.Source.Anidb);
                        if (_apiIds is null) {
                            _apiIds = new AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse {
                                AniDb = aniDbId.aniDbId
                            };
                            _logger.LogWarning("Did not get provider IDs, defaulting to episode provided AniDb ID");
                        } else {
                            // FIX 8: distinguish "ARM gave us a usable ID" from "ARM gave us a
                            // row that carries none". Only the former avoids the title-search
                            // fallback, so reporting both as success made the fallback look
                            // like a bug in ID resolution rather than a gap in the database.
                            if (_apiIds.Anilist is null or 0 && _apiIds.MyAnimeList is null or 0 && _apiIds.Kitsu is null or 0) {
                                _logger.LogWarning($"ARM server has a record for AniDb ID {aniDbId.aniDbId.Value} but no tracker IDs; falling back to title search");
                            } else {
                                _logger.LogInformation("Retrieved provider IDs");
                            }
                        }
                    }
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
                        _logger.LogInformation($"Watched episode passes total episodes in season! {episode?.IndexNumber.Value}>{matchingAnime.NumEpisodes} Checking for additional seasons/cours...");
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

                                var nextSeason = await GetNextSeasonAnime(season.Id, seasonCounter + 1, alternativeId: season.AlternativeId);
                                if (nextSeason == null)
                                {
                                    _logger.LogWarning($"Could not find next season");
                                    if (matchingAnime.Status == AiringStatus.currently_airing && matchingAnime.NumEpisodes == 0)
                                    {
                                        _logger.LogWarning($"Show is currently airing and API reports 0 episodes, going to use first season");
                                        isRootSeason = true;
                                    }
                                    if (!checkMalId) {
                                        // should have already found the appropriate series/season/movie, no need to do other checks
                                        if (episode?.Season.IndexNumber is > 1) {
                                            // if this is not the first season, then we need to lookup the related season.
                                            matchingAnime = await GetDifferentSeasonAnime(anime.Id, episode.Season.IndexNumber.Value, alternativeId: anime.AlternativeId);
                                            if (matchingAnime == null) {
                                                _logger.LogWarning($"({ApiName}) Could not find next season");
                                                found = true;
                                                break;
                                            }

                                            _logger.LogInformation($"({ApiName}) Season being watched is {GetAnimeTitle(matchingAnime)}");
                                        } else if (episode?.Season.IndexNumber == 0) {
                                            // the episode is an ova or special
                                            matchingAnime = await GetOva(anime.Id, episode.Name, alternativeId: anime.AlternativeId);
                                            if (matchingAnime == null) {
                                                _logger.LogWarning($"({ApiName}) Could not find OVA");
                                                found = true;
                                                break;
                                            }
                                        } else if (matchingAnime.NumEpisodes > 0 && matchingAnime.NumEpisodes < episode?.IndexNumber.Value) {
                                            // FIX 9: NumEpisodes is a non-nullable int, so an unknown
                                            // episode count arrives as 0 - which is the norm for a
                                            // currently-airing show. Without the > 0 guard this branch
                                            // fires on every episode of every airing series that reaches
                                            // the title-search fallback.
                                            //
                                            // Walking cannot produce a correct answer in that case
                                            // regardless: the loop computes the next cour's episode
                                            // number as episodeCount - totalEpisodesWatched, and
                                            // totalEpisodesWatched accumulates season.NumEpisodes. With
                                            // 0 the offset is always 0, so the raw episode number gets
                                            // sent to the sequel. The arithmetic requires a known
                                            // length. When no sequel exists the code already recovers
                                            // via isRootSeason; this guard applies that same judgement
                                            // up front, so the outcome no longer depends on whether the
                                            // entry happens to have a Sequel relation.
                                            _logger.LogInformation($"({ApiName}) Watched episode passes total episodes in season! Checking for additional seasons/cours...");
                                            // either we have found the wrong series (highly unlikely) or it is a multi cour series/Jellyfin has grouped next season into the current.
                                            int seasonEpisodeCounter = matchingAnime.NumEpisodes;
                                            int totalEpisodesWatched = 0;
                                            int seasonCounter = episode.Season.IndexNumber.Value;
                                            int episodeCount = episode.IndexNumber.Value;
                                            Anime season = matchingAnime;
                                            bool isRootSeason = false;
                                            while (seasonEpisodeCounter < episodeCount) {
                                                var nextSeason = await GetDifferentSeasonAnime(season.Id, seasonCounter + 1, alternativeId: season.AlternativeId);
                                                if (nextSeason == null) {
                                                    _logger.LogWarning($"({ApiName}) Could not find next season");
                                                    if (matchingAnime.Status == AiringStatus.currently_airing && matchingAnime.NumEpisodes == 0) {
                                                        _logger.LogWarning($"({ApiName}) Show is currently airing and API reports 0 episodes, going to use first season");
                                                        isRootSeason = true;
                                                    }

                                                    found = true;
                                                    break;
                                                }

                                                seasonEpisodeCounter += nextSeason.NumEpisodes;
                                                seasonCounter++;
                                                // complete the current season; we have surpassed it onto the next season/cour
                                                totalEpisodesWatched += season.NumEpisodes;
                                                await CheckUserListAnimeStatus(season.Id, season.NumEpisodes, overrideCheckRewatch: false, alternativeId: matchingAnime.AlternativeId);
                                                season = nextSeason;
                                            }

                                            if (!isRootSeason) {
                                                if (season.Id != matchingAnime.Id) {
                                                    matchingAnime = season;
                                                    episodeNumber = episodeCount - totalEpisodesWatched;
                                                } else {
                                                    break;
                                                }
                                            }
                                        }
                                    }

                                    await CheckUserListAnimeStatus(matchingAnime.Id, episodeNumber, overrideCheckRewatch: false, alternativeId: matchingAnime.AlternativeId);
                                    found = true;
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
        /// Checks if the Jellyfin library entry matches the API names.
        /// </summary>
        /// <param name="anime">The API anime.</param>
        /// <param name="episode">The episode if its a series.</param>
        /// <param name="movie">The movie if its a single episode movie.</param>
        /// <returns></returns>
        private bool TitleCheck(Anime anime, Episode episode, Movie movie) {
            // FIX 7: try every title Jellyfin holds for the item, not just the display
            // name. Metadata providers such as Shokofin populate OriginalTitle with the
            // series' native title, and AniList carries a native title for effectively
            // every entry - so this matches even when the romanisations disagree
            // (e.g. AniDB "Otome Kaijuu Caramelise" vs AniList "Otome Kaijuu Caraméliser").
            // It lets a library display English or romaji titles while still resolving
            // correctly, instead of forcing the whole library into one naming scheme.
            var candidateTitles = new List<string>();
            if (_animeType == typeof(Episode)) {
                candidateTitles.Add(episode.SeriesName);
                if (episode.Series?.OriginalTitle != null) candidateTitles.Add(episode.Series.OriginalTitle);
            } else {
                candidateTitles.Add(movie.Name);
                candidateTitles.Add(movie.OriginalTitle);
            }

            foreach (var title in candidateTitles) {
                if (string.IsNullOrWhiteSpace(title)) continue;
                if (CompareStrings(anime.Title, title) ||
                    (anime.AlternativeTitles.En != null && CompareStrings(anime.AlternativeTitles.En, title)) ||
                    (anime.AlternativeTitles.Ja != null && CompareStrings(anime.AlternativeTitles.Ja, title)) ||
                    (anime.AlternativeTitles.Synonyms != null && anime.AlternativeTitles.Synonyms.Any(synonym => CompareStrings(synonym, title)))) {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Compare two strings, ignoring symbols and case.
        /// </summary>
        /// <param name="first">The first string.</param>
        /// <param name="second">The second string.</param>
        /// <returns>True if first string is equal to second string, false if not.</returns>
        private bool CompareStrings(string first, string second) {
            return String.Compare(first, second, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreSymbols) == 0;
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
        /// <param name="logger">Logger instance.</param>
        /// <param name="item">Item to check location of.</param>
        /// <returns></returns>
        public static bool LibraryCheck(UserConfig userConfig, ILibraryManager libraryManager, ILogger logger, BaseItem item) {
            try {
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
            if (detectedAnime.MyListStatus != null &&
                (detectedAnime.MyListStatus.Status == Status.Watching || detectedAnime.MyListStatus.Status == Status.Rewatching) &&
                ApiName != ApiName.Annict) {
                _logger.LogInformation($"({ApiName}) {(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) found on {detectedAnime.MyListStatus.Status} list");
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
            if (_userConfig.PlanToWatchOnly) {
                bool updated = false;
                if (detectedAnime.MyListStatus != null && detectedAnime.MyListStatus.Status == Status.Plan_to_watch) {
                    _logger.LogInformation($"({ApiName}) {(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) found on plan to watch list");
                    if (ApiName == ApiName.Annict) {
                        updated = true;
                        await UpdateAnnictStatus(detectedAnime, episodeNumber);
                    } else {
                        updated = true;
                        await UpdateAnimeStatus(detectedAnime, episodeNumber);
                    }
                } else if (ApiName != ApiName.Annict) {
                    // also check if rewatch completed is checked
                    _logger.LogInformation($"({ApiName}) {(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) not found in plan to watch list{(_userConfig.RewatchCompleted ? ", checking completed list.." : null)}");
                    updated = true;
                    await CheckIfRewatchCompleted(detectedAnime, episodeNumber, overrideCheckRewatch);
                }

                if (!updated) {
                    _logger.LogInformation($"({ApiName}) Could not update.");
                }

                return;
            }

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

                            _logger.LogInformation($"({ApiName}) {(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) complete, marking anime as complete{(ApiName != ApiName.Mal && (setRewatching != null && setRewatching.Value) ? ", increasing re-watch count by 1" : "")}");
                            if ((detectedAnime.MyListStatus.IsRewatching || (detectedAnime.NumEpisodes == 1 && detectedAnime.MyListStatus.Status == Status.Completed) || (setRewatching != null && setRewatching.Value)) && ApiName == ApiName.Mal) {
                                // also increase number of times re-watched by 1
                                // only way to get the number of times re-watched is by doing the update and capturing the response, and then re-updating for MAL :/
                                _logger.LogInformation($"({ApiName}) {(_animeType == typeof(Episode) ? "Series" : "Movie")} ({GetAnimeTitle(detectedAnime)}) has also been re-watched, increasing re-watch count by 1");
                                response = await ApiCallHelpers.UpdateAnime(detectedAnime.Id,
                                    episodeNumber.Value,
                                    Status.Completed,
                                    numberOfTimesRewatched: response.NumTimesRewatched + 1,
                                    isRewatching: false,
                                    alternativeId: detectedAnime.AlternativeId,
                                    ids: _apiIds,
                                    isShow: _animeType == typeof(Episode));
                            }
                        } else {
                            if (detectedAnime.MyListStatus.IsRewatching && ApiName == ApiName.Mal) {
                                // MAL likes to mark re-watching shows as completed, instead of watching. I guess technically both are correct
                                _logger.LogInformation($"({ApiName}) User is re-watching {(_animeType == typeof(Episode) ? "series" : "movie")} ({GetAnimeTitle(detectedAnime)}), set as completed but update re-watch progress");
                                response = await ApiCallHelpers.UpdateAnime(detectedAnime.Id,
                                    episodeNumber.Value,
                                    Status.Completed,
                                    isRewatching: true,
                                    alternativeId: detectedAnime.AlternativeId,
                                    ids: _apiIds,
                                    isShow: _animeType == typeof(Episode));
                            } else {
                                Status watchingStatus = detectedAnime.MyListStatus.IsRewatching &&
                                    (ApiName == ApiName.AniList || ApiName == ApiName.Shikimori)
                                        ? Status.Rewatching
                                        : Status.Watching;
                                if (episodeNumber > 1) {
                                    // don't set start date after first episode
                                    response = await ApiCallHelpers.UpdateAnime(detectedAnime.Id,
                                        episodeNumber.Value,
                                        watchingStatus,
                                        alternativeId: detectedAnime.AlternativeId,
                                        ids: _apiIds,
                                        isShow: _animeType == typeof(Episode));
                                } else {
                                    _logger.LogInformation($"({ApiName}) Setting new {(_animeType == typeof(Episode) ? "series" : "movie")} ({GetAnimeTitle(detectedAnime)}) as {watchingStatus}.");
                                    response = await ApiCallHelpers.UpdateAnime(detectedAnime.Id,
                                        episodeNumber.Value,
                                        watchingStatus,
                                        startDate: DateTime.Now,
                                        alternativeId: detectedAnime.AlternativeId,
                                        ids: _apiIds,
                                        isShow: _animeType == typeof(Episode));
                                }
                            }
                        }

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
        internal async Task<Anime?> GetDifferentSeasonAnime(int animeId, int seasonNumber, string? alternativeId = null) {
            _logger.LogInformation($"({ApiName}) Attempting to get season {seasonNumber}...");
            Anime retrievedSeason = await ApiCallHelpers.GetAnime(animeId, getRelated: true, alternativeId: alternativeId);

            var currentSeason = await ApiCallHelpers.GetAnime(animeId, getRelated: true, alternativeId: alternativeId);
            if (currentSeason == null)
                return null;

            var sequel = currentSeason.RelatedAnime?.FirstOrDefault(r => r.RelationType == RelationType.Sequel)?.Anime;

            if (sequel == null)
            {
                _logger.LogInformation($"No sequel found for AnimeId={animeId}");
                return null;
            }

            return await ApiCallHelpers.GetAnime(sequel.Id, getRelated: true, alternativeId: sequel.AlternativeId);
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
