using m3uCrawler.Services;

namespace m3uCrawler.Models
{
    /// <summary>
    /// Entrada de triagem no RunReport. Regista o que aconteceu a uma
    /// publicacao descoberta (Telegram ref ou URL HTTP) sem nunca expor
    /// credenciais ou informacao sensivel.
    /// </summary>
    public class PublicationTriageEntry
    {
        public string Kind { get; set; } = string.Empty;
        public string Reference { get; set; } = string.Empty;
        public long? ChannelId { get; set; }
        public int? MessageId { get; set; }
        public PublicationState State { get; set; }
        public string? Reason { get; set; }
        public int XtreamAccountsFound { get; set; }

        public override string ToString()
        {
            // Nunca incluir password/credentials; o Reference e' sanitizado via
            // CredentialSanitizer.SanitizeUrl (cobre http(s):// com userinfo,
            // /live/USER/PASS/, e parametros username/password/token). Reason
            // deve estar sanitizado antes de chegar aqui.
            var sanitizedRef = CredentialSanitizer.SanitizeUrl(Reference ?? string.Empty);
            return $"{Kind} ref={sanitizedRef} state={State} accounts={XtreamAccountsFound}"
                 + (Reason != null ? $" reason={CredentialSanitizer.SanitizeText(Reason)}" : "");
        }
    }

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

        // === Publicacao Discovery / Resolution (introduzido 2026-09-09) ===

        // Total de referencias Telegram + URLs HTTP publicas descobertas.
        public int PublicationsDiscovered { get; set; }

        // Publicacoes cujo conteudo foi obtido com sucesso (texto, attachment, etc.).
        public int PublicationsResolved { get; set; }

        // Publicacoes que nao puderam ser resolvidas (FLOOD_WAIT persistente,
        // canal inexistente, mensagem inacessivel).
        public int PublicationsResolutionFailed { get; set; }

        // Publicacoes resolvidas cujo conteudo nao tinha nada util.
        public int PublicationsUnsupported { get; set; }

        // Publicacoes que precisam de revisao humana (e.g. HTML sem cards Xtream).
        public int PublicationsRequiresReview { get; set; }

        // === XtreamPublicationResolver fan-out ===

        // Total de cards Xtream descobertas no HTML (antes de dedup).
        public int XtreamAccountsDiscovered { get; set; }

        // Apos dedup por identidade logica (endpoint + username).
        public int XtreamAccountsAfterDedup { get; set; }

        // Promovidas a CandidatePlaylist que entraram no pipeline existente.
        public int XtreamAccountsForwarded { get; set; }

        public List<string> RejectionReasons { get; set; } = new();
        public List<DiscoveredPlaylist> DiscoveredPlaylists { get; set; } = new();
        public List<PublicationTriageEntry> PublicationsTriageLog { get; set; } = new();
    }
}
