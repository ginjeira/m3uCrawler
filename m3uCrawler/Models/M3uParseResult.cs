namespace m3uCrawler.Models
{
    /// <summary>
    /// Resultado detalhado de parsing M3U (contrato W3, <c>04-PLAYLIST-STREAM.md</c> §7).
    /// <para>
    /// <see cref="Streams"/> contém apenas entradas válidas/inutilizáveis
    /// utilizáveis pela camada seguinte. Os contadores reflectem o que foi
    /// observado: <see cref="MalformedCount"/> e <see cref="UnusableTargetCount"/>
    /// excluem entradas de <see cref="Streams"/>, mas continuam o parsing.
    /// </para>
    /// </summary>
    public class M3uParseResult
    {
        public IReadOnlyList<M3uStream> Streams { get; set; } = Array.Empty<M3uStream>();

        public M3uPlaylistStatus Status { get; set; } = M3uPlaylistStatus.Failed;

        public IReadOnlyList<M3uParseDiagnostic> Diagnostics { get; set; } = Array.Empty<M3uParseDiagnostic>();

        /// <summary>Entradas válidas (com URL utilizável http/https).</summary>
        public int ValidCount { get; set; }

        /// <summary>Entradas estruturalmente inválidas, registadas e excluídas.</summary>
        public int MalformedCount { get; set; }

        /// <summary>Entradas estruturalmente válidas com alvo não utilizável (não http/https).</summary>
        public int UnusableTargetCount { get; set; }

        /// <summary>Linhas de metadados benignas (comentários/directivas não-entrada).</summary>
        public int BenignMetadataCount { get; set; }

        /// <summary>Linhas ignoradas (vazias ou texto que não é URL nem segue #EXTINF).</summary>
        public int IgnoredCount { get; set; }
    }
}
