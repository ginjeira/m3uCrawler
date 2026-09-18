using System.Text.Json.Serialization;

namespace m3uCrawler.Models
{
    /// <summary>
    /// PHASE 13 (Wave 13-6) — Artefacto intermédio da selecção de fontes,
    /// entre a fase de selecção (stage) e o apply no Dispatcharr. Espelha a
    /// forma de <see cref="MatchPlan"/>, mas para o resultado da selecção de
    /// fontes por canal canónico.
    ///
    /// <para>
    /// <b>Sensível:</b> <see cref="SelectedStreamSelection.StreamUrl"/> contém
    /// a URL real (credenciais incluídas). A serialização é da
    /// responsabilidade do <c>DispatcharrSourceSelectionSerializer</c>, que
    /// aplica <c>CredentialSanitizer.SanitizeUrl</c> a todas as URLs.
    /// </para>
    /// </summary>
    public sealed class DispatcharrSourceSelection
    {
        [JsonPropertyName("generatedAtUtc")] public string GeneratedAtUtc { get; init; } = string.Empty;
        [JsonPropertyName("channels")] public IReadOnlyList<ChannelSourceSelection> Channels { get; init; } = Array.Empty<ChannelSourceSelection>();
        [JsonPropertyName("counts")] public SelectionCounts Counts { get; init; } = new();
    }

    /// <summary>
    /// Selecção de fontes para um canal canónico. Projeção determinística do
    /// <c>SourceSelectionChannelResult</c> do stage (a ordem é preservada).
    /// </summary>
    public sealed class ChannelSourceSelection
    {
        [JsonPropertyName("canonicalChannelKey")] public string CanonicalChannelKey { get; init; } = string.Empty;
        [JsonPropertyName("canonicalChannelId")] public long? CanonicalChannelId { get; init; }
        [JsonPropertyName("policyScope")] public string? PolicyScope { get; init; }
        [JsonPropertyName("candidateCount")] public int CandidateCount { get; init; }
        [JsonPropertyName("rejectedCount")] public int RejectedCount { get; init; }
        [JsonPropertyName("selected")] public IReadOnlyList<SelectedStreamSelection> Selected { get; init; } = Array.Empty<SelectedStreamSelection>();
    }

    /// <summary>
    /// Fonte seleccionada para publicação, com o rank e o motivo determinístico
    /// emitidos pelo selector.
    /// </summary>
    public sealed class SelectedStreamSelection
    {
        [JsonPropertyName("streamUrl")] public string StreamUrl { get; init; } = string.Empty;
        [JsonPropertyName("rank")] public int Rank { get; init; }
        [JsonPropertyName("provider")] public string Provider { get; init; } = string.Empty;
        [JsonPropertyName("reason")] public string Reason { get; init; } = string.Empty;
        [JsonPropertyName("sourceId")] public long SourceId { get; init; }
    }

    /// <summary>Contagens agregadas do artefacto de selecção de fontes.</summary>
    public sealed class SelectionCounts
    {
        [JsonPropertyName("channels")] public int Channels { get; init; }
        [JsonPropertyName("candidates")] public int Candidates { get; init; }
        [JsonPropertyName("selected")] public int Selected { get; init; }
        [JsonPropertyName("rejected")] public int Rejected { get; init; }
        [JsonPropertyName("unmatched")] public int Unmatched { get; init; }
        [JsonPropertyName("ambiguous")] public int Ambiguous { get; init; }
    }
}
