namespace m3uCrawler.Models
{
    /// <summary>
    /// Classificação técnica de um diagnóstico de parsing M3U.
    /// </summary>
    public enum M3uParseDiagnosticKind
    {
        /// <summary>Entrada estruturalmente incompleta ou inválida (ex.: URL sem #EXTINF).</summary>
        Malformed,

        /// <summary>
        /// Entrada estruturalmente válida cujo alvo não é utilizável
        /// (esquema diferente de http/https). Não é uma entrada malformada.
        /// </summary>
        UnusableTarget,

        /// <summary>A primeira linha não vazia não é <c>#EXTM3U</c>.</summary>
        MissingHeader,

        /// <summary>Limite/cancelamento/falha interna de parsing.</summary>
        ParserFailure,
    }

    /// <summary>
    /// Diagnóstico de parsing de uma entrada/linha. <see cref="SanitizedLine"/>
    /// passa obrigatoriamente por <c>CredentialSanitizer</c>, de forma que
    /// passwords/tokens de URLs nunca sejam expostos em relatórios ou logs.
    /// </summary>
    public class M3uParseDiagnostic
    {
        public M3uParseDiagnosticKind Kind { get; set; }

        /// <summary>Número de linha 1-based; 0 quando não aplicável (playlist vazia).</summary>
        public int LineNumber { get; set; }

        /// <summary>Linha original sanitizada (nunca contém credenciais).</summary>
        public string SanitizedLine { get; set; } = string.Empty;

        public string Reason { get; set; } = string.Empty;
    }
}
