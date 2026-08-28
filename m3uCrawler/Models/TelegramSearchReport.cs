using System.Text.Json.Serialization;

namespace m3uCrawler.Models
{
    public class TelegramSearchReport
    {
        [JsonPropertyName("searchTerm")]
        public string SearchTerm { get; set; } = string.Empty;

        [JsonPropertyName("generatedAt")]
        public DateTime GeneratedAt { get; set; }

        [JsonPropertyName("messagesMatched")]
        public int MessagesMatched { get; set; }

        [JsonPropertyName("foundUrls")]
        public int FoundUrls { get; set; }

        [JsonPropertyName("testedUrls")]
        public int TestedUrls { get; set; }

        [JsonPropertyName("workingStreams")]
        public int WorkingStreams { get; set; }

        [JsonPropertyName("failedStreams")]
        public int FailedStreams { get; set; }

        [JsonPropertyName("maxUrlsTested")]
        public int MaxUrlsTested { get; set; }

        [JsonPropertyName("xtreamCredentialsDetected")]
        public bool XtreamCredentialsDetected { get; set; }

        [JsonPropertyName("xtreamServers")]
        public List<XtreamCredentialInfo> XtreamServers { get; set; } = new();

        [JsonPropertyName("sampleFoundUrls")]
        public List<FoundUrlInfo> SampleFoundUrls { get; set; } = new();

        [JsonPropertyName("allFoundUrls")]
        public List<FoundUrlInfo> AllFoundUrls { get; set; } = new();
    }

    public class FoundUrlInfo
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("chatTitle")]
        public string ChatTitle { get; set; } = string.Empty;

        [JsonPropertyName("sourceText")]
        public string SourceText { get; set; } = string.Empty;

        [JsonPropertyName("originalExtInf")]
        public string OriginalExtInf { get; set; } = string.Empty;
    }

    public class XtreamCredentialInfo
    {
        [JsonPropertyName("origin")]
        public string Origin { get; set; } = string.Empty;

        [JsonPropertyName("user")]
        public string User { get; set; } = string.Empty;

        [JsonPropertyName("pass")]
        public string Pass { get; set; } = string.Empty;

        [JsonPropertyName("playlistUrl")]
        public string PlaylistUrl { get; set; } = string.Empty;
    }

    public class TelegramScrapeResult
    {
        [JsonPropertyName("workingStreams")]
        public List<M3uStream> WorkingStreams { get; set; } = new();

        [JsonPropertyName("allTestedStreams")]
        public List<M3uStream> AllTestedStreams { get; set; } = new();

        [JsonPropertyName("searchReport")]
        public TelegramSearchReport SearchReport { get; set; } = new();
    }
}
