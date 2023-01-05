using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace jellyfin_ani_sync.Models.Annict; 

public class AnnictSearch {
    public class AnnictSearchData
    {
        [JsonPropertyName("searchWorks")]
        public SearchWorks SearchWorks { get; set; }
    }

    public class AnnictAnime
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("titleEn")]
        public string TitleEn { get; set; }

        [JsonPropertyName("malAnimeId")]
        public string MalAnimeId { get; set; }
    }

    public class PageInfo
    {
        [JsonPropertyName("hasNextPage")]
        public bool HasNextPage { get; set; }
    }

    public class AnnictSearchMedia
    {
        [JsonPropertyName("data")]
        public AnnictSearchData AnnictSearchData { get; set; }
    }

    public class SearchWorks
    {
        [JsonPropertyName("nodes")]
        public List<AnnictAnime> Nodes { get; set; }

        [JsonPropertyName("pageInfo")]
        public PageInfo PageInfo { get; set; }
    }

    public enum AnnictMediaStatus {
        Wanna_watch,
        Watching,
        Watched,
        On_hold,
        Stop_watching,
        No_state
    }
}