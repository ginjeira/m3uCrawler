namespace m3uCrawler.Models
{
    public enum CandidateSourceKind
    {
        Url,
        Attachment,
        Inline
    }

    /// <summary>
    /// Representa uma playlist M3U/M3U8 descoberta como candidata (URL, anexo ou conteúdo inline),
    /// antes de ser descarregada/validada. A descoberta NÃO depende da presença de qualquer keyword
    /// no texto ou no nome de ficheiro.
    /// </summary>
    public class CandidatePlaylist
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public CandidateSourceKind Kind { get; set; }
        public string Source { get; set; } = string.Empty;
        public string? Url { get; set; }
        public string? FileName { get; set; }
        public string? SourceText { get; set; }
        public string? Content { get; set; }
        public string? DetectedFrom { get; set; }

        /// <summary>
        /// Verdadeiro para URLs sem extensão .m3u/.m3u8 detectadas por heurística: o conteúdo
        /// HTTP tem de ser confirmado como #EXTM3U antes de o candidato ser tratado como playlist.
        /// </summary>
        public bool RequiresContentVerification { get; set; }

        /// <summary>
        /// W-HISTWIN-PROV (2026-10-02): id da mensagem Telegram de origem (m.ID).
        /// null quando o candidate não veio de uma mensagem enumerada
        /// (ex.: --scan-domain) — a proveniência é opt-in por origem.
        /// </summary>
        public long? SourceMessageId { get; set; }

        /// <summary>
        /// W-HISTWIN-PROV (2026-10-02): data/hora UTC da mensagem Telegram de origem
        /// (m.date, UTC por contrato WTelegram). null nos mesmos casos de
        /// <see cref="SourceMessageId"/> e nas promoções t.me/c resolvidas
        /// pós-enumeração (a resolução não devolve a data da mensagem).
        /// </summary>
        public DateTime? SourceMessageDateUtc { get; set; }
    }
}
