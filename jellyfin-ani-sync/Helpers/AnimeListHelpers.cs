using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace jellyfin_ani_sync.Helpers
{
    public class AnimeListHelpers
    {
        /// <summary>
        /// Get the MyAnimeList ID from the set of providers provided.
        /// </summary>
        /// <param name="logger">Logger.</param>
        /// <param name="providers">Dictionary of providers.</param>
        /// <param name="episodeNumber">Episode number.</param>
        /// <param name="seasonNumber">Season number.</param>
        /// <returns></returns>
        public static async Task<(int? MyAnimeListId, int? episodeOffset)> GetMyAnimeListId(ILogger logger, Video video, int episodeNumber, int seasonNumber, AnimeListXml animeListXml)
        {
            int MyAnimeListId;
            if (animeListXml == null) return (null, null);
            Dictionary<string, string> providers;
            if (video is Episode)
            {
                //Search for MyAnimeList id at season level
                providers = (video as Episode).Season.ProviderIds.ContainsKey("MyAnimeList") ? (video as Episode).Season.ProviderIds : (video as Episode).Series.ProviderIds;
            }
            else if (video is Movie)
            {
                providers = (video as Movie).ProviderIds;
            }
            else
            {
                return (null, null);
            }

            if (providers.ContainsKey("MyAnimeList"))
            {
                logger.LogInformation("(MyAnimeList) Anime already has MyAnimeList ID; no need to look it up");
                if (!int.TryParse(providers["MyAnimeList"], out MyAnimeListId)) return (null, null);
                var foundAnime = animeListXml.Anime.Where(anime => int.TryParse(anime.MyAnimeListid, out int xmlMyAnimeListId) &&
                                                                   xmlMyAnimeListId == MyAnimeListId &&
                                                                   (
                                                                       (video as Episode).Season.ProviderIds.ContainsKey("MyAnimeList") ||
                                                                       (int.TryParse(anime.Defaulttvdbseason, out int xmlSeason) &&
                                                                        xmlSeason == seasonNumber ||
                                                                        anime.Defaulttvdbseason == "a")
                                                                   )
                ).ToList();
                switch (foundAnime.Count())
                {
                    case 1:
                        var related = animeListXml.Anime.Where(anime => anime.Tvdbid == foundAnime.First().Tvdbid).ToList();
                        if (video is Episode episode && episode.Series.Children.OfType<Season>().Count() > 1 && related.Count > 1)
                        {
                            // contains more than 1 season, need to do a lookup
                            logger.LogInformation($"(MyAnimeList) Anime {episode.Series.Name} found in anime XML file");
                            logger.LogInformation($"(MyAnimeList) Looking up anime {episode.Series.Name} in the anime XML file by absolute episode number...");
                            var (MyAnimeList, episodeOffset) = GetMyAnimeListByEpisodeOffset(logger, GetAbsoluteEpisodeNumber(episode), seasonNumber, episodeNumber, related);
                            if (MyAnimeList != null)
                            {
                                logger.LogInformation($"(MyAnimeList) Anime {episode.Series.Name} found in anime XML file, detected MyAnimeList ID {MyAnimeList}");
                                return (MyAnimeList, episodeOffset);
                            }
                            else
                            {
                                logger.LogInformation($"(MyAnimeList) Anime {episode.Series.Name} could not found in anime XML file; falling back to other metadata providers if available...");
                            }
                        }
                        else
                        {
                            if (video is Episode episodeWithMultipleSeasons && episodeWithMultipleSeasons.Season.IndexNumber > 1)
                            {
                                // user doesnt have full series; have to do season lookup
                                logger.LogInformation($"(MyAnimeList) Anime {episodeWithMultipleSeasons.Series.Name} found in anime XML file");
                                return SeasonLookup(logger, seasonNumber, episodeNumber, related);
                            }
                            else
                            {
                                logger.LogInformation($"(MyAnimeList) Anime {video.Name} found in anime XML file");
                                // is movie / only has one season / no related; just return the only result
                                return int.TryParse(related.First().MyAnimeListid, out MyAnimeListId) ? (MyAnimeListId, null) : (null, null);
                            }
                            logger.LogInformation($"(MyAnimeList) Anime {(video is Episode episodeWithoutSeason ? episodeWithoutSeason.Name : video.Name)} found in anime XML file");
                            // is movie / only has one season / no related; just return the only result
                            return int.TryParse(foundAnime.First().MyAnimeListid, out MyAnimeListId) ? (MyAnimeListId, null) : (null, null);
                        }

                        break;
                    case > 1:
                        // here
                        logger.LogWarning("(MyAnimeList) More than one result found; possibly an issue with the XML. Falling back to other metadata providers if available...");
                        break;
                    case 0:
                        logger.LogWarning("(MyAnimeList) Anime not found in anime list XML; falling back to other metadata providers if available...");
                        break;
                }
            }

            return (null, null);
        }

        private static (int? MyAnimeListId, int? episodeOffset) GetMyAnimeListByEpisodeOffset(ILogger logger, int? absoluteEpisodeNumber, int seasonNumber, int episodeNumber, List<AnimeListAnime> related)
        {
            if (absoluteEpisodeNumber != null)
            {
                // FIXME: return correct offset when using absolute episode
                // numbers.
                var foundMapping = related.FirstOrDefault(animeListAnime => animeListAnime.MappingList?.Mapping?.FirstOrDefault(mapping => mapping.Start < absoluteEpisodeNumber && mapping.End > absoluteEpisodeNumber) != null);
                if (foundMapping != null)
                {
                    return (int.TryParse(foundMapping.MyAnimeListid, out var MyAnimeListId) ? MyAnimeListId : null, null);
                }
                else
                {
                    logger.LogWarning("(MyAnimeList) Could not lookup using absolute episode number (reason: no mappings found)");
                    return SeasonLookup(logger, seasonNumber, episodeNumber, related);
                }
            }
            else
            {
                logger.LogWarning("(MyAnimeList) Could not lookup using absolute episode number (reason: absolute episode number is null)");
                return SeasonLookup(logger, seasonNumber, episodeNumber, related);
            }
        }

        private static (int? MyAnimeListId, int? episodeOffset) SeasonLookup(ILogger logger, int seasonNumber, int episodeNumber, List<AnimeListAnime> related)
        {
            logger.LogInformation("Looking up MyAnimeList by season offset");

            // First, consider mappings from absolute-numbered seasons. If there
            // are no matches, compare episode number against episodeoffset
            // attribute for each matching season number. Note that order is
            // important in this case: we do not want to match previous season
            // that would have lower episode offset.
            var foundMapping =
                related
                    .Where(animeListAnime => animeListAnime.Defaulttvdbseason == "a")
                    .FirstOrDefault(
                        animeListAnime =>
                            animeListAnime.MappingList.Mapping.FirstOrDefault(
                                mapping => mapping.Tvdbseason == seasonNumber
                            ) != null
                    )
                ?? related
                    .Where(animeListAnime => animeListAnime.Defaulttvdbseason == seasonNumber.ToString())
                    .OrderBy(animeListAnime => int.TryParse(animeListAnime.Episodeoffset, out var n) ? n : 0)
                    .LastOrDefault(
                        animeListAnime =>
                            animeListAnime.Episodeoffset == null
                            || !int.TryParse(animeListAnime.Episodeoffset, out var episodeOffset)
                            || episodeOffset < episodeNumber
                    );



            return (
                int.TryParse(foundMapping?.MyAnimeListid, out var MyAnimeListId) ? MyAnimeListId : null,
                int.TryParse(foundMapping?.Episodeoffset, out var episodeOffset) ? episodeOffset : null
            );
        }

        private static int? GetAbsoluteEpisodeNumber(Episode episode)
        {
            var previousSeasons = episode.Series.Children.OfType<Season>().Where(item => item.IndexNumber < episode.Season.IndexNumber).ToList();
            int previousSeasonIndexNumber = -1;
            foreach (int indexNumber in previousSeasons.Where(item => item.IndexNumber != null).Select(item => item.IndexNumber).OrderBy(item => item.Value))
            {
                if (previousSeasonIndexNumber == -1)
                {
                    previousSeasonIndexNumber = indexNumber;
                }
                else
                {
                    if (previousSeasonIndexNumber != indexNumber - 1)
                    {
                        // series does not contain all seasons, cannot get absolute episode number
                        return null;
                    }

                    previousSeasonIndexNumber = indexNumber;
                }
            }

            var previousSeasonsEpisodeCount = previousSeasons.SelectMany(item => item.Children.OfType<Episode>()).Count();
            // this is presuming the user has all episodes
            return previousSeasonsEpisodeCount + episode.IndexNumber;
        }

        /// <summary>
        /// Get the season number of an MyAnimeList entry.
        /// </summary>
        /// <param name="MyAnimeListId"></param>
        /// <returns>Season.</returns>
        public static AnimeListAnime GetMyAnimeListSeason(int MyAnimeListId, AnimeListXml animeListXml)
        {
            if (animeListXml == null) return null;

            return animeListXml.Anime.FirstOrDefault(anime => int.TryParse(anime.MyAnimeListid, out int xmlMyAnimeListId) && xmlMyAnimeListId == MyAnimeListId);
        }


        public static IEnumerable<AnimeListAnime> ListAllSeasonOfMyAnimeListSeries(int MyAnimeListId, AnimeListXml animeListXml)
        {
            if (animeListXml == null) return null;

            AnimeListAnime foundXmlAnime = animeListXml.Anime.FirstOrDefault(anime => int.TryParse(anime.MyAnimeListid, out int xmlMyAnimeListId) && xmlMyAnimeListId == MyAnimeListId);
            if (foundXmlAnime == null) return null;

            return animeListXml.Anime.Where(anime => anime.Tvdbid == foundXmlAnime.Tvdbid);
        }

        /// <summary>
        /// Get the contents of the anime list file.
        /// </summary>
        /// <param name="logger">Logger.</param>
        /// <returns></returns>
        public static async Task<AnimeListXml> GetAnimeListFileContents(ILogger logger, ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory, IApplicationPaths applicationPaths)
        {
            UpdateAnimeList updateAnimeList = new UpdateAnimeList(httpClientFactory, loggerFactory, applicationPaths);

            try
            {
                FileInfo animeListXml = new FileInfo(updateAnimeList.Path);
                if (!animeListXml.Exists)
                {
                    logger.LogInformation("Anime list XML not found; attempting to download...");
                    if (await updateAnimeList.Update())
                    {
                        logger.LogInformation("Anime list XML downloaded");
                    }
                }

                using (var stream = File.OpenRead(updateAnimeList.Path))
                {
                    var serializer = new XmlSerializer(typeof(AnimeListXml));
                    return (AnimeListXml)serializer.Deserialize(stream);
                }
            }
            catch (Exception e)
            {
                logger.LogError($"Could not deserialize anime list XML; {e.Message}. Try forcibly redownloading the XML file");
                return null;
            }
        }

        [XmlRoot(ElementName = "anime")]
        public class AnimeListAnime
        {
            [XmlElement(ElementName = "name")] public string Name { get; set; }

            [XmlElement(ElementName = "mapping-list")]
            public MappingList MappingList { get; set; }

            [XmlAttribute(AttributeName = "MyAnimeListid")]
            public string MyAnimeListid { get; set; }

            [XmlAttribute(AttributeName = "tvdbid")]
            public string Tvdbid { get; set; }

            [XmlAttribute(AttributeName = "defaulttvdbseason")]
            public string Defaulttvdbseason { get; set; }

            [XmlAttribute(AttributeName = "episodeoffset")]
            public string Episodeoffset { get; set; }

            [XmlAttribute(AttributeName = "tmdbid")]
            public string Tmdbid { get; set; }
        }

        [XmlRoot(ElementName = "mapping-list")]
        public class MappingList
        {
            [XmlElement(ElementName = "mapping")] public List<Mapping> Mapping { get; set; }
        }

        [XmlRoot(ElementName = "mapping")]
        public class Mapping
        {
            [XmlAttribute(AttributeName = "MyAnimeListseason")]
            public int MyAnimeListseason { get; set; }

            [XmlAttribute(AttributeName = "tvdbseason")]
            public int Tvdbseason { get; set; }

            [XmlText] public string Text { get; set; }

            [XmlAttribute(AttributeName = "start")]
            public int Start { get; set; }

            [XmlAttribute(AttributeName = "end")] public int End { get; set; }

            [XmlAttribute(AttributeName = "offset")]
            public int Offset { get; set; }
        }

        [XmlRoot(ElementName = "anime-list")]
        public class AnimeListXml
        {
            [XmlElement(ElementName = "anime")] public List<AnimeListAnime> Anime { get; set; }
        }
    }
}
