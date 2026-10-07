using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using m3uCrawler.Services.Matching;

namespace m3uCrawler.Services.Recognition;

/// <summary>
/// W5.3 — Contrato do passo 6 (fuzzy) de Recognition.
/// <c>docs/Reestructure/48-RECOGNITION-FUZZY-CONTRACT.md</c> F1–F14.
///
/// <para>
/// O score técnico é <c>0..100</c> (F4) e nunca é <c>MatchConfidence</c>
/// (<c>0..1</c>, W5.6). A avaliação é puramente determinística: não depende de
/// ordem de enumeração, <c>Id</c>, ordem de BD, inserção ou playlist (F9/F10).
/// </para>
/// </summary>
public static class FuzzyDecisionReasons
{
    /// <summary>Candidato único aceite (F9).</summary>
    public const string Canonical = "fuzzy-canonical";

    /// <summary>Vários candidatos indistinguíveis pela margem (F8/F9).</summary>
    public const string Ambiguous = "fuzzy-ambiguous";

    /// <summary>Candidatos plausíveis sem nenhum aceite (F7).</summary>
    public const string BelowThreshold = "fuzzy-below-threshold";

    /// <summary>Nenhum candidato na banda de plausibilidade (F7).</summary>
    public const string NoCandidate = "fuzzy-no-candidate";

    /// <summary>Fuzzy ligado mas threshold nulo/fora de <c>0..100</c> (F6, fail-closed).</summary>
    public const string ThresholdInvalid = "fuzzy-threshold-invalid";
}

/// <summary>
/// Canal candidato ao passo fuzzy: apenas <c>DisplayName</c> e
/// <c>NormalizedAlias</c> (F1). Nenhum outro campo participa no score.
/// </summary>
public readonly record struct FuzzyCandidate(
    long CanonicalChannelId,
    string? CanonicalKey,
    string? DisplayName,
    IReadOnlyList<string> NormalizedAliases);

/// <summary>Diagnóstico técnico por candidato (W5.4), score <c>0..100</c>.</summary>
public readonly record struct FuzzyCandidateDiagnostic(
    long CanonicalChannelId,
    string? CanonicalKey,
    int Score,
    string Reason);

/// <summary>
/// Diagnóstico técnico do passo fuzzy. Não é <c>MatchConfidence</c> nem
/// autoridade de decisão: é evidência para W5.4.
/// </summary>
public readonly record struct FuzzyRecognitionDiagnostic(
    string DecisionReason,
    int? Threshold,
    int? AmbiguityMargin,
    int Floor,
    IReadOnlyList<FuzzyCandidateDiagnostic> Candidates);

/// <summary>Resultado interno do passo fuzzy (F9).</summary>
public enum FuzzyDecisionKind
{
    /// <summary>Fuzzy desligado ou falha de configuração (fail-closed).</summary>
    NotExecuted = 0,

    /// <summary>Candidato único aceite com margem suficiente.</summary>
    Canonical = 1,

    /// <summary>Candidatos indistinguíveis ou plausíveis sem aceite.</summary>
    Ambiguous = 2,

    /// <summary>Sem candidato na banda de plausibilidade.</summary>
    Unknown = 3,
}

/// <summary>Decisão do passo fuzzy, com score e diagnóstico opcionais.</summary>
public readonly record struct FuzzyRecognitionDecision(
    FuzzyDecisionKind Kind,
    long? CanonicalChannelId,
    int? Score,
    FuzzyRecognitionDiagnostic? Diagnostic);

/// <summary>
/// W5.3 — Avaliação determinística do passo fuzzy (F5–F10).
/// Reutiliza <see cref="FuzzyMatcher"/> e <see cref="ChannelNormalizer"/> como
/// métrica/normalização normativas (F2/F3); não introduz métrica nova.
/// </summary>
public static class FuzzyRecognitionEvaluator
{
    public const string DisplayNameField = "displayName";
    public const string AliasField = "alias";

    private const double DefaultWeight = 1.0;

    private readonly record struct FuzzyWeights(double DisplayName, double Alias)
    {
        public static FuzzyWeights Default => new(DefaultWeight, DefaultWeight);
    }

    /// <summary>
    /// Avalia o passo fuzzy. Quando <see cref="RecognitionPolicy.FuzzyEnabled"/>
    /// é <c>false</c>, devolve <see cref="FuzzyDecisionKind.NotExecuted"/> sem
    /// diagnóstico. Threshold nulo/fora de <c>0..100</c> é fail-closed com
    /// diagnóstico de configuração (F6).
    /// </summary>
    public static FuzzyRecognitionDecision Evaluate(
        string normalizedQuery,
        IReadOnlyList<FuzzyCandidate> candidates,
        RecognitionPolicy? policy,
        FuzzyMatcher? fuzzyMatcher = null)
    {
        if (policy?.FuzzyEnabled != true)
        {
            return new FuzzyRecognitionDecision(FuzzyDecisionKind.NotExecuted, null, null, null);
        }

        var threshold = policy.FuzzyThreshold;
        if (threshold is null || threshold < 0 || threshold > 100)
        {
            var invalid = new FuzzyRecognitionDiagnostic(
                FuzzyDecisionReasons.ThresholdInvalid,
                threshold,
                policy.FuzzyAmbiguityMargin,
                Floor: 0,
                Array.Empty<FuzzyCandidateDiagnostic>());
            return new FuzzyRecognitionDecision(FuzzyDecisionKind.NotExecuted, null, null, invalid);
        }

        var margin = Math.Max(policy.FuzzyAmbiguityMargin ?? 0, 0);
        var floor = threshold.Value - margin;
        var weights = ParseWeights(policy.FuzzyWeightsJson);
        var matcher = fuzzyMatcher ?? new FuzzyMatcher();

        var scored = new List<ScoredCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var (score, reason) = ScoreCandidate(normalizedQuery, candidate, weights, matcher);
            scored.Add(new ScoredCandidate(candidate, score, reason));
        }

        // S = candidatos na banda de plausibilidade (F7).
        var withinBand = scored.Where(s => s.Score >= floor).ToList();

        if (withinBand.Count == 0)
        {
            return new FuzzyRecognitionDecision(
                FuzzyDecisionKind.Unknown, null, null,
                BuildDiagnostic(FuzzyDecisionReasons.NoCandidate, threshold, policy.FuzzyAmbiguityMargin, floor, withinBand));
        }

        var best = withinBand.Max(s => s.Score);

        if (best < threshold.Value)
        {
            // Plausíveis, nenhum aceite (F7).
            return new FuzzyRecognitionDecision(
                FuzzyDecisionKind.Ambiguous, null, best,
                BuildDiagnostic(FuzzyDecisionReasons.BelowThreshold, threshold, policy.FuzzyAmbiguityMargin, floor, withinBand));
        }

        var top = withinBand.Where(s => s.Score == best).ToList();
        if (top.Count > 1)
        {
            // Empate no topo: nunca escolher o primeiro (F9/F10).
            return new FuzzyRecognitionDecision(
                FuzzyDecisionKind.Ambiguous, null, best,
                BuildDiagnostic(FuzzyDecisionReasons.Ambiguous, threshold, policy.FuzzyAmbiguityMargin, floor, withinBand));
        }

        var winner = top[0];
        var second = withinBand
            .Where(s => s.Candidate.CanonicalChannelId != winner.Candidate.CanonicalChannelId)
            .Select(s => s.Score)
            .DefaultIfEmpty(int.MinValue)
            .Max();

        if (second != int.MinValue && best - second <= margin)
        {
            return new FuzzyRecognitionDecision(
                FuzzyDecisionKind.Ambiguous, null, best,
                BuildDiagnostic(FuzzyDecisionReasons.Ambiguous, threshold, policy.FuzzyAmbiguityMargin, floor, withinBand));
        }

        return new FuzzyRecognitionDecision(
            FuzzyDecisionKind.Canonical, winner.Candidate.CanonicalChannelId, best,
            BuildDiagnostic(FuzzyDecisionReasons.Canonical, threshold, policy.FuzzyAmbiguityMargin, floor, withinBand));
    }

    private static (int Score, string Reason) ScoreCandidate(
        string normalizedQuery,
        FuzzyCandidate candidate,
        FuzzyWeights weights,
        FuzzyMatcher matcher)
    {
        var bestScore = 0;
        var bestReason = "empty";
        var hasScore = false;

        void Consider(string? field, double weight)
        {
            if (weight <= 0 || string.IsNullOrWhiteSpace(field)) return;
            var match = matcher.Score(normalizedQuery, field!);
            var weighted = (int)Math.Round(
                Math.Clamp(weight, 0.0, 1.0) * match.Score,
                MidpointRounding.AwayFromZero);

            // Campos avaliados por ordem fixa (DisplayName, depois alias
            // ordenados Ordinal): em empate mantém-se a primeira razão.
            if (!hasScore || weighted > bestScore)
            {
                bestScore = weighted;
                bestReason = match.Reason;
                hasScore = true;
            }
        }

        Consider(candidate.DisplayName, weights.DisplayName);

        if (candidate.NormalizedAliases is { Count: > 0 })
        {
            foreach (var alias in candidate.NormalizedAliases.OrderBy(a => a, StringComparer.Ordinal))
            {
                Consider(alias, weights.Alias);
            }
        }

        return (Math.Clamp(bestScore, 0, 100), hasScore ? bestReason : "empty");
    }

    private static FuzzyWeights ParseWeights(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return FuzzyWeights.Default;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return FuzzyWeights.Default;
            }

            var displayName = DefaultWeight;
            var alias = DefaultWeight;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Number
                    || !property.Value.TryGetDouble(out var value))
                {
                    continue;
                }

                // Chaves desconhecidas (ex.: "fingerprint") são ignoradas:
                // não produzem score (F1).
                if (string.Equals(property.Name, DisplayNameField, StringComparison.OrdinalIgnoreCase))
                {
                    displayName = value;
                }
                else if (string.Equals(property.Name, AliasField, StringComparison.OrdinalIgnoreCase))
                {
                    alias = value;
                }
            }

            return new FuzzyWeights(displayName, alias);
        }
        catch (JsonException)
        {
            // JSON inválido ≡ null (pesos 1.0) — F5/F13.
            return FuzzyWeights.Default;
        }
    }

    private static FuzzyRecognitionDiagnostic BuildDiagnostic(
        string decisionReason,
        int? threshold,
        int? ambiguityMargin,
        int floor,
        IReadOnlyList<ScoredCandidate> withinBand)
    {
        var candidates = withinBand
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Candidate.CanonicalChannelId)
            .Select(s => new FuzzyCandidateDiagnostic(
                s.Candidate.CanonicalChannelId,
                s.Candidate.CanonicalKey,
                s.Score,
                s.Reason))
            .ToList();

        return new FuzzyRecognitionDiagnostic(
            decisionReason, threshold, ambiguityMargin, floor, candidates);
    }

    private readonly record struct ScoredCandidate(
        FuzzyCandidate Candidate, int Score, string Reason);
}
