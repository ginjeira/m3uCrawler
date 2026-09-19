namespace m3uCrawler.Models
{
    /// <summary>
    /// Limites técnicos do parser M3U (MECHANISM). Os valores concretos são
    /// PARAMETER_GAP (<c>DG-04d</c>, <c>04-PLAYLIST-STREAM.md</c> §6) e não
    /// são normativos: estes defaults são conservadores e podem ser ajustados
    /// pelo chamador sem alterar a semântica do contrato.
    /// </summary>
    public sealed class M3uParserOptions
    {
        public static M3uParserOptions Default { get; } = new();

        /// <summary>Comprimento máximo do documento (caracteres). Default técnico: 8 MiB.</summary>
        public int MaxDocumentLength { get; init; } = 8 * 1024 * 1024;

        /// <summary>Número máximo de entradas válidas extraídas. Default técnico: 100 000.</summary>
        public int MaxEntries { get; init; } = 100_000;

        /// <summary>Comprimento máximo de uma linha/campo (caracteres). Default técnico: 16 KiB.</summary>
        public int MaxFieldLength { get; init; } = 16 * 1024;

        /// <summary>Tempo máximo de parsing. Default técnico: 30 s.</summary>
        public TimeSpan MaxParsingTime { get; init; } = TimeSpan.FromSeconds(30);
    }
}
