namespace m3uCrawler.Services.Recognition;

/// <summary>
/// W5.2 — Conjunto mínimo e versionado de métodos de reconhecimento
/// (<c>05-CATALOGUE.md §4.1</c>; <c>34-PIPELINE-CONTRACTS.md</c> P6).
///
/// <para>
/// O método é <b>registo, não autoridade</b>: não altera a decisão de
/// reconhecimento. A semântica é versionada. Cada constante corresponde a um
/// passo explícito da ordem determinística de reconhecimento.
/// </para>
/// </summary>
public static class RecognitionMatchMethods
{
    /// <summary>Identidade externa exacta (namespace provider/canónico).</summary>
    public const string ExternalIdentityExact = "ExternalIdentityExact";

    /// <summary>Identidade externa exacta no namespace <c>tvg-id</c>.</summary>
    public const string TvgIdExact = "TvgIdExact";

    /// <summary>Correspondência exacta da Key canónica normalizada.</summary>
    public const string CanonicalExact = "CanonicalExact";

    /// <summary>Correspondência do nome normalizado (passo próprio, distinto de alias).</summary>
    public const string NormalizedName = "NormalizedName";

    /// <summary>Alias conhecido persistido.</summary>
    public const string KnownAlias = "KnownAlias";

    /// <summary>Heurística explicitamente definida (afinidade de canal).</summary>
    public const string ExplicitHeuristic = "ExplicitHeuristic";

    /// <summary>Fuzzy matching (opt-in; não executado em W5.2).</summary>
    public const string Fuzzy = "Fuzzy";

    /// <summary>Decisão manual via regra de identidade/Review.</summary>
    public const string ManualReview = "ManualReview";
}
