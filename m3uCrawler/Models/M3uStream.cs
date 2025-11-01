using System.Text.Json.Serialization;

namespace m3uCrawler.Models
{
    public class M3uStream
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("group")]
        public string Group { get; set; } = string.Empty;

        [JsonPropertyName("logo")]
        public string Logo { get; set; } = string.Empty;

        [JsonPropertyName("isWorking")]
        public bool IsWorking { get; set; }

        [JsonPropertyName("lastTested")]
        public DateTime LastTested { get; set; }

        [JsonPropertyName("responseTime")]
        public double ResponseTime { get; set; }

        public override string ToString()
        {
            return $"#EXTINF:-1 tvg-name=\"{Title}\" tvg-logo=\"{Logo}\" group-title=\"{Group}\",{Title}\n{Url}";
        }
    }
}
