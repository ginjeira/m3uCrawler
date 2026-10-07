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

        /// <summary>
        /// Valor original do atributo <c>tvg-id</c> do EXTINF (ou do
        /// <c>#EXT-X-STREAM-INF</c>), preservado verbatim. É evidência
        /// de identidade externa (ADR-0002 §5) e <b>nunca</b> cria por
        /// si só um <c>CanonicalChannel</c> (DL-002). Vazio quando a
        /// source não fornece tvg-id.
        /// </summary>
        [JsonPropertyName("tvgId")]
        public string OriginalTvgId { get; set; } = string.Empty;

        [JsonPropertyName("logo")]
        public string Logo { get; set; } = string.Empty;

        [JsonPropertyName("isWorking")]
        public bool IsWorking { get; set; }

        [JsonPropertyName("lastTested")]
        public DateTime LastTested { get; set; }

        [JsonPropertyName("responseTime")]
        public double ResponseTime { get; set; }

        [JsonPropertyName("originalExtInf")]
        public string OriginalExtInf { get; set; } = string.Empty;

        public override string ToString()
        {
            if (!string.IsNullOrWhiteSpace(OriginalExtInf))
            {
                return $"{OriginalExtInf}\n{Url}";
            }

            return $"#EXTINF:-1 tvg-name=\"{Title}\" tvg-logo=\"{Logo}\" group-title=\"{Group}\",{Title}\n{Url}";
        }
    }
}
