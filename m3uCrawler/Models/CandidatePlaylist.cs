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
    }
}
