using System;
using System.Collections.Generic;

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
///
/// <para>
/// W5.6 — A tabela normativa de <c>MatchConfidence</c> por método
/// (<c>49-W56-MATCH-CONFIDENCE-SPECIFICATION.md §7</c>) vive aqui, centralizada.
/// <c>MatchConfidence</c> é <b>method-specific</b>: domínio <c>0..1</c>, sem
/// escala global comparável, e <b>nunca</b> derivada de <c>FuzzyScore</c>.
/// </para>
/// </summary>
public static class RecognitionMatchMethods
{
    /// <summary>
    /// W5.6 (OD-D) — versão da semântica de <c>MatchMethod</c>/<c>MatchConfidence</c>.
    /// Atribuída pelo servidor a novas rows de <c>ChannelSource</c>. Distinta de
    /// <c>FingerprintVersion</c> (<c>sfp1</c>) e de <c>RecognitionPolicy.Version</c>.
    /// </summary>
    public const string MatchSemanticsVersion = "msm1";

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

    /// <summary>
    /// Tabela normativa (W5.6 §7) de <c>MatchConfidence</c> por método. Valores
    /// ratificados (OD-A/B/C + exactos): exactos e <see cref="ManualReview"/> →
    /// <c>1.0</c>; <see cref="ExplicitHeuristic"/> → <c>0.80</c>;
    /// <see cref="Fuzzy"/> → <c>0.60</c>. Lida apenas por igualdade/compatibilidade
    /// method-specific; <b>nunca</b> como ranking comparável entre métodos.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, double> ConfidenceByMethod =
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [ExternalIdentityExact] = 1.0,
            [TvgIdExact] = 1.0,
            [CanonicalExact] = 1.0,
            [NormalizedName] = 1.0,
            [KnownAlias] = 1.0,
            [ExplicitHeuristic] = 0.80,
            [Fuzzy] = 0.60,
            [ManualReview] = 1.0,
        };

    /// <summary>
    /// Verdadeiro se <paramref name="method"/> é um dos 8 valores normativos
    /// (W5.6 §5). Qualquer valor fora deste conjunto é inválido quando
    /// fornecido explicitamente.
    /// </summary>
    public static bool IsKnownMethod(string? method)
        => method != null && ConfidenceByMethod.ContainsKey(method);

    /// <summary>
    /// Obtém o <c>MatchConfidence</c> normativo de <paramref name="method"/>
    /// (W5.6 §7). Devolve <c>false</c> (e <paramref name="matchConfidence"/> a
    /// <c>0</c>) quando o método é nulo/desconhecido — o caller deve representar
    /// isso como <c>null</c>, nunca como <c>0</c>.
    /// </summary>
    public static bool TryGetMatchConfidence(string? method, out double matchConfidence)
    {
        if (method != null && ConfidenceByMethod.TryGetValue(method, out matchConfidence))
        {
            return true;
        }

        matchConfidence = 0;
        return false;
    }
}
