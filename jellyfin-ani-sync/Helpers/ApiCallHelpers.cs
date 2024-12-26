using jellyfin_ani_sync.Api;
using jellyfin_ani_sync.Models.Mal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace jellyfin_ani_sync.Helpers
{
    public class ApiCallHelpers : IApiCallHelpers
    {
        private MalApiCalls _malApiCalls;

        /// <summary>
        /// This class attempts to combine the different APIs into a single form.
        /// </summary>
        /// <param name="malApiCalls"></param>
        public ApiCallHelpers(MalApiCalls malApiCalls = null)
        {
            _malApiCalls = malApiCalls;
        }

        public async Task<List<Anime>> SearchAnime(string query)
        {
            bool updateNsfw = Plugin.Instance?.PluginConfiguration?.updateNsfw != null && Plugin.Instance.PluginConfiguration.updateNsfw;
            if (_malApiCalls != null)
            {
                return await _malApiCalls.SearchAnime(query, new[] { "id", "title", "alternative_titles", "num_episodes", "status" }, updateNsfw);
            }

            return null;
        }

        public async Task<Anime> GetAnime(int id, string? alternativeId = null, bool getRelated = false)
        {
            if (_malApiCalls != null)
            {
                return await _malApiCalls.GetAnime(id, new[] { "title", "related_anime", "my_list_status", "num_episodes" });
            }

            return null;
        }

        public async Task<UpdateAnimeStatusResponse> UpdateAnime(int animeId, int numberOfWatchedEpisodes, Status status,
            bool? isRewatching = null, int? numberOfTimesRewatched = null, DateTime? startDate = null, DateTime? endDate = null, string alternativeId = null, AnimeOfflineDatabaseHelpers.OfflineDatabaseResponse ids = null, bool? isShow = null)
        {
            if (_malApiCalls != null)
            {
                return await _malApiCalls.UpdateAnimeStatus(animeId, numberOfWatchedEpisodes, status, isRewatching, numberOfTimesRewatched, startDate, endDate);
            }

            return null;
        }

        public async Task<MalApiCalls.User> GetUser()
        {
            if (_malApiCalls != null)
            {
                return await _malApiCalls.GetUserInformation();
            }

            return null;
        }

        public async Task<List<Anime>> GetAnimeList(Status status, int? userId = null)
        {
            if (_malApiCalls != null)
            {
                var malAnimeList = await _malApiCalls.GetUserAnimeList(status);
                return malAnimeList?.Select(animeList => animeList.Anime).ToList();
            }

            return null;
        }
    }
}