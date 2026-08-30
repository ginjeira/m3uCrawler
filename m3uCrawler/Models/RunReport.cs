namespace m3uCrawler.Models
{
    /// <summary>
    /// Resumo de uma playlist descoberta numa execução, para o relatório detalhado e para o dashboard.
    /// </summary>
    public class DiscoveredPlaylist
    {
        public string Source { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string CountryDetected { get; set; } = string.Empty;
        public int ChannelsRecognized { get; set; }
        public int StreamCount { get; set; }

        // Streams que passaram o filtro per-canal/per-stream (ValidateStreams) e foram
        // submetidos a TestStreamsAsync. <= StreamCount. Adicionado em 2026-08-30
        // quando o pipeline deixou de aprovar streams com base apenas no gate per-playlist.
        public int StreamsAfterCountryFilter { get; set; }

        public int WorkingStreams { get; set; }
        public string State { get; set; } = string.Empty;
    }

    /// <summary>
    /// Relatório detalhado de uma execução do pipeline Telegram → candidato → país → streams.
    /// Permite distinguir exatamente onde ocorreu um zero (mensagens, candidatos, playlists,
    /// país ou streams), em vez de um genérico "foundUrls = 0".
    /// </summary>
    public class RunReport
    {
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime FinishedAt { get; set; }
        public long DurationMs { get; set; }
        public string Status { get; set; } = "pending";

        public int MessagesAnalyzed { get; set; }
        public int CandidatesFound { get; set; }
        public int PlaylistsDownloaded { get; set; }
        public int PlaylistsInvalid { get; set; }
        public int CountryMatches { get; set; }
        public int PlaylistsRejected { get; set; }
        public int ChannelsRecognized { get; set; }
        public int StreamsExtracted { get; set; }

        // Streams que efectivamente chegaram a TestStreamsAsync após o filtro per-canal.
        // <= StreamsExtracted. Adicionado em 2026-08-30.
        public int StreamsAfterCountryFilter { get; set; }

        // Streams rejeitados pelo filtro per-canal/per-stream. (= StreamsExtracted - StreamsAfterCountryFilter).
        // Adicionado em 2026-08-30.
        public int StreamsRejectedByCountry { get; set; }

        public int StreamsTested { get; set; }
        public int StreamsWorking { get; set; }
        public int StreamsFailed { get; set; }

        public List<string> RejectionReasons { get; set; } = new();
        public List<DiscoveredPlaylist> DiscoveredPlaylists { get; set; } = new();
    }
}
