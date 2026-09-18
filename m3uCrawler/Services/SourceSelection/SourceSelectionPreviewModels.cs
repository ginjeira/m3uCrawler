using System;
using System.Collections.Generic;

namespace m3uCrawler.Services.SourceSelection;

/// <summary>
/// PHASE 13 (Wave 13-5) — Vocabulário estável das decisões por candidato no
/// preview. Distinto de <see cref="SelectionReasons"/> (que é o motivo):
/// aqui apenas se distingue seleccionado de rejeitado.
/// </summary>
public static class SourceSelectionPreviewDecisions
{
    /// <summary>O candidato foi seleccionado no rank indicado.</summary>
    public const string Selected = "selected";

    /// <summary>O candidato foi rejeitado; <see cref="SourceSelectionPreviewCandidate.Reason"/> explica porquê.</summary>
    public const string Rejected = "rejected";
}

/// <summary>
/// PHASE 13 (Wave 13-5) — Candidato individual projectado para
/// preview/dry-run. <see cref="StreamUrlSanitized"/> é sempre o resultado de
/// <c>CredentialSanitizer.SanitizeUrl</c> (defensivo, mesmo que o catálogo já
/// guarde URLs sanitizadas). Os enums são achatados via <c>ToString()</c>.
/// </summary>
public sealed record SourceSelectionPreviewCandidate(
    string StreamUrlSanitized,
    long SourceId,
    int SourcePriority,
    string Provider,
    string? ExternalStreamId,
    string Quality,
    string Epg,
    string Availability,
    long LastResponseTimeMs,
    bool IsWorking,
    int? Rank,
    string Decision,
    string Reason);

/// <summary>
/// PHASE 13 (Wave 13-5) — Resultado de selecção de um canal canónico no
/// preview. <see cref="Policy"/> é a política efectiva resolvida para o
/// canal e <see cref="PolicyScope"/> rotula a origem da mesma
/// (<c>override</c>/<c>global</c>/<c>default</c>).
/// </summary>
public sealed record SourceSelectionPreviewChannel(
    long CanonicalChannelId,
    string? CanonicalChannelKey,
    string? DisplayName,
    string PolicyScope,
    SourceSelectionPolicy Policy,
    int CandidateCount,
    int SelectedCount,
    int RejectedCount,
    IReadOnlyList<SourceSelectionPreviewCandidate> Selected,
    IReadOnlyList<SourceSelectionPreviewCandidate> Rejected);

/// <summary>
/// PHASE 13 (Wave 13-5) — Stream do input sem correspondência inequívoca no
/// catálogo. Desde a correcção Wave 13-5, <see cref="Reason"/> distingue
/// <c>unmatched</c> (sem hit no catálogo) de <c>ambiguous</c> (URL mapeada a
/// mais de um canal canónico); as duas listas são disjuntas por construção.
/// </summary>
public sealed record SourceSelectionPreviewUnmatched(
    string StreamUrlSanitized,
    string Title,
    string Reason);

/// <summary>
/// PHASE 13 (Wave 13-5) — Distribuição de selecções por fornecedor.
/// <see cref="ChannelCount"/> é o número de canais distintos onde o
/// fornecedor teve pelo menos uma selecção.
/// </summary>
public sealed record SourceSelectionPreviewProviderStat(
    string Provider,
    int SelectedCount,
    int ChannelCount);

/// <summary>
/// PHASE 13 (Wave 13-5) — Métricas agregadas determinísticas do preview.
///
/// <para>
/// <b>FillSelectionCount</b> é o proxy da Fase B / fallback fill do selector
/// (selecções com <c>SelectionReasons.Fill</c>).
/// <b>UnmatchedStreamCount</b> e <b>AmbiguousStreamCount</b> são disjuntos por
/// construção (respectivamente sem hit e URL ambígua); a soma está em
/// <b>TotalUnmatchedStreamCount</b>. Todas as contagens derivam do resultado
/// do <see cref="SourceSelectionStage"/> — o algoritmo não é duplicado.
/// </para>
/// </summary>
public sealed record SourceSelectionPreviewMetrics(
    int ChannelsProcessed,
    int ChannelsWithSources,
    int CandidateStreamCount,
    int SelectedStreamCount,
    int RejectedStreamCount,
    int UnmatchedStreamCount,
    int AmbiguousStreamCount,
    int TotalUnmatchedStreamCount,
    int ChannelsAtChannelLimit,
    int ChannelLimitRejectionCount,
    int ProviderLimitRejectionCount,
    int DiversitySelectionCount,
    int DistinctProviderCount,
    int FillSelectionCount,
    int FallbackDisabledRejectionCount,
    int SourceDisabledRejectionCount,
    IReadOnlyDictionary<string, int> RejectionCounts,
    IReadOnlyList<SourceSelectionPreviewProviderStat> ProviderDistribution);

/// <summary>
/// PHASE 13 (Wave 13-5) — Proveniência dos dados do preview. <c>Origin</c> é
/// um rótulo curto (<c>catalog</c>) e nunca expõe caminhos de filesystem nem
/// internals do <c>CatalogResolver</c>.
/// </summary>
public sealed record SourceSelectionPreviewSourceInfo(
    string Origin,
    string? ChannelKeyFilter,
    int CatalogChannelSourceCount,
    int CanonicalChannelCount);

/// <summary>
/// PHASE 13 (Wave 13-5, MAJOR-1) — Vocabulário estável das razões de nível
/// superior pelas quais o preview foi ou não aplicado. Distinto de
/// <see cref="SourceSelectionPreviewDecisions"/> (decisão por candidato) e de
/// <see cref="SelectionReasons"/> (motivo da rejeição).
/// </summary>
public static class SourceSelectionPreviewStatuses
{
    /// <summary>O estágio correu e aplicou a selecção (<see cref="SourceSelectionPreviewResult.Applied"/> é <c>true</c>).</summary>
    public const string Applied = "applied";

    /// <summary>Um <c>channelKey</c> não vazio não corresponde a nenhum canal canónico.</summary>
    public const string ChannelNotFound = "channel-not-found";

    /// <summary>Sem filtro (ou filtro correspondido) mas o catálogo não tem canais canónicos.</summary>
    public const string NoChannels = "no-channels";

    /// <summary>Há canais no âmbito mas nenhum tem <c>ChannelSource</c>, logo zero streams de entrada.</summary>
    public const string NoInput = "no-input";
}

/// <summary>
/// PHASE 13 (Wave 13-5, MAJOR-1) — Resultado completo do preview/dry-run da
/// selecção de fontes.
///
/// <para>
/// <see cref="Status"/> desambigua os quatro casos distintos que antes
/// colapsavam em <see cref="Applied"/>: <c>applied</c>,
/// <c>channel-not-found</c>, <c>no-channels</c> e <c>no-input</c>.
/// <see cref="Applied"/> é <c>true</c> apenas no caso <c>applied</c>.
/// </para>
///
/// <para>
/// <b>Importante:</b> <c>Applied=false</c> com <c>Status="no-input"</c> e
/// <c>channelsProcessed &gt; 0</c> é um estado válido e esperado — significa
/// que existem canais canónicos no âmbito mas nenhum tem fontes. As quatro
/// categorias de <see cref="Status"/> são mutuamente distintas e não devem
/// ser confundidas com "catálogo vazio".
/// </para>
/// </summary>
public sealed record SourceSelectionPreviewResult(
    bool Applied,
    string Status,
    DateTime GeneratedAtUtc,
    int InputStreamCount,
    SourceSelectionPreviewSourceInfo Source,
    SourceSelectionPreviewMetrics Metrics,
    IReadOnlyList<SourceSelectionPreviewChannel> Channels,
    IReadOnlyList<SourceSelectionPreviewUnmatched> Unmatched,
    IReadOnlyList<SourceSelectionPreviewUnmatched> Ambiguous);
