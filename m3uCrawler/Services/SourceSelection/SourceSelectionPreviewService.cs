using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;

namespace m3uCrawler.Services.SourceSelection;

/// <summary>
/// PHASE 13 (Wave 13-5) — Preview/dry-run read-only da selecção de fontes.
///
/// <para>
/// Lê o catálogo (canais canónicos + <c>ChannelSource</c>), sintetiza as
/// streams correspondentes, resolve as políticas efectivas em modo
/// estritamente read-only (nunca cria a linha global) e corre o
/// <b>mesmo</b> <see cref="SourceSelectionStage"/>/<see cref="ChannelSourceSelector"/>
/// usado em produção. Não publica, não escreve ficheiros, não muta o catálogo
/// nem persiste diagnósticos.
/// </para>
///
/// <para>
/// Todo o output é determinístico para o mesmo estado do catálogo, excepto
/// <see cref="SourceSelectionPreviewResult.GeneratedAtUtc"/>.
/// </para>
/// </summary>
public sealed class SourceSelectionPreviewService
{
    private const string OriginCatalog = "catalog";
    private const string PolicyScopeOverride = "override";
    private const string PolicyScopeGlobal = "global";
    private const string PolicyScopeDefault = "default";

    /// <summary>
    /// Motivo por-stream para streams sem qualquer correspondência no
    /// catálogo (não-ambíguas). A soma com <see cref="AmbiguousReason"/> está
    /// em <see cref="SourceSelectionPreviewMetrics.TotalUnmatchedStreamCount"/>.
    /// </summary>
    public const string UnmatchedReason = "unmatched";

    /// <summary>
    /// Motivo por-stream para streams cuja URL mapeia para mais de um canal
    /// canónico. Disjunto de <see cref="UnmatchedReason"/>.
    /// </summary>
    public const string AmbiguousReason = "ambiguous";

    private readonly CatalogResolver _catalog;

    public SourceSelectionPreviewService(CatalogResolver catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>
    /// Executa o preview. <paramref name="canonicalChannelKey"/> opcional
    /// restringe a um canal canónico (match exacto, ordinal). Se o filtro não
    /// corresponder a nenhum canal, devolve <c>Applied=false</c> e métricas
    /// zeradas, registando o filtro em
    /// <see cref="SourceSelectionPreviewSourceInfo.ChannelKeyFilter"/>.
    /// </summary>
    public async Task<SourceSelectionPreviewResult> PreviewAsync(
        string? canonicalChannelKey = null,
        CancellationToken cancellationToken = default)
    {
        var filter = string.IsNullOrWhiteSpace(canonicalChannelKey)
            ? null
            : canonicalChannelKey.Trim();
        var generatedAt = DateTime.UtcNow;

        var channels = await _catalog
            .ListCanonicalChannelsAsync(cancellationToken)
            .ConfigureAwait(false);
        var allChannelSources = await _catalog
            .ListChannelSourcesAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var sourceInfo = new SourceSelectionPreviewSourceInfo(
            Origin: OriginCatalog,
            ChannelKeyFilter: filter,
            CatalogChannelSourceCount: allChannelSources.Count,
            CanonicalChannelCount: channels.Count);

        IReadOnlyList<CanonicalChannelEntity> scopedChannels;
        IReadOnlyList<ChannelSourceEntity> scopedSources;

        if (filter is not null)
        {
            var channel = channels.FirstOrDefault(
                c => string.Equals(c.Key, filter, StringComparison.Ordinal));
            if (channel is null)
            {
                return new SourceSelectionPreviewResult(
                    Applied: false,
                    GeneratedAtUtc: generatedAt,
                    InputStreamCount: 0,
                    Source: sourceInfo,
                    Metrics: EmptyMetrics(),
                    Channels: Array.Empty<SourceSelectionPreviewChannel>(),
                    Unmatched: Array.Empty<SourceSelectionPreviewUnmatched>(),
                    Ambiguous: Array.Empty<SourceSelectionPreviewUnmatched>());
            }

            scopedChannels = new[] { channel };
            scopedSources = allChannelSources
                .Where(cs => cs.CanonicalChannelId == channel.Id)
                .ToList();
        }
        else
        {
            scopedChannels = channels;
            scopedSources = allChannelSources;
        }

        // Uma stream sintética por ChannelSource (incluindo desactivados, para
        // que as decisões `source-disabled` sejam visíveis).
        var streams = scopedSources.Select(ToStream).ToList();

        var policies = await new SourceSelectionPolicyResolver(_catalog)
            .LoadEffectivePoliciesReadOnlyAsync(cancellationToken)
            .ConfigureAwait(false);

        var result = await new SourceSelectionStage(_catalog)
            .ApplyAsync(streams, policies, cancellationToken)
            .ConfigureAwait(false);

        var displayNames = new Dictionary<long, string>(channels.Count);
        foreach (var channel in channels)
        {
            displayNames[channel.Id] = channel.DisplayName;
        }

        var previewChannels = result.Channels
            .Select(c => ToPreviewChannel(c, displayNames, policies))
            .ToList();

        // Duas listas disjuntas por construção: o stage coloca as streams
        // ambíguas em Unmatched (pass-through de produção inalterado) e expõe
        // as mesmas referências em AmbiguousStreams. A exclusão é por
        // identidade de referência (M3uStream é uma classe sem Equals próprio).
        var ambiguousStreams = new HashSet<M3uStream>(
            result.AmbiguousStreams, ReferenceEqualityComparer.Instance);

        var ambiguous = result.AmbiguousStreams
            .Select(u => new SourceSelectionPreviewUnmatched(
                StreamUrlSanitized: CredentialSanitizer.SanitizeUrl(u.Url),
                Title: CredentialSanitizer.SanitizeText(u.Title),
                Reason: AmbiguousReason))
            .ToList();

        var unmatched = result.Unmatched
            .Where(u => !ambiguousStreams.Contains(u))
            .Select(u => new SourceSelectionPreviewUnmatched(
                StreamUrlSanitized: CredentialSanitizer.SanitizeUrl(u.Url),
                Title: CredentialSanitizer.SanitizeText(u.Title),
                Reason: UnmatchedReason))
            .ToList();

        var metrics = BuildMetrics(
            scopedChannels.Count, scopedSources, result, unmatched.Count, ambiguous.Count);

        return new SourceSelectionPreviewResult(
            Applied: result.Applied,
            GeneratedAtUtc: generatedAt,
            InputStreamCount: streams.Count,
            Source: sourceInfo,
            Metrics: metrics,
            Channels: previewChannels,
            Unmatched: unmatched,
            Ambiguous: ambiguous);
    }

    private static M3uStream ToStream(ChannelSourceEntity channelSource) => new()
    {
        Url = channelSource.StreamUrl,
        Title = channelSource.CanonicalChannel?.DisplayName ?? string.Empty,
        IsWorking = channelSource.Availability
            is not AvailabilityState.Dead and not AvailabilityState.Unreachable,
        ResponseTime = channelSource.LastResponseTimeMs,
    };

    private static SourceSelectionPreviewChannel ToPreviewChannel(
        SourceSelectionChannelResult channel,
        IReadOnlyDictionary<long, string> displayNames,
        SourceSelectionPolicySet policies)
    {
        var key = channel.CanonicalChannelKey;
        var scope = policies.HasOverride(key)
            ? PolicyScopeOverride
            : policies.HasExplicitGlobal
                ? PolicyScopeGlobal
                : PolicyScopeDefault;

        var selected = channel.Selected
            .Select(s => ToCandidate(
                s.Candidate, s.Rank, SourceSelectionPreviewDecisions.Selected, s.Reason))
            .ToList();
        var rejected = channel.Rejected
            .Select(r => ToCandidate(
                r.Candidate, rank: null, SourceSelectionPreviewDecisions.Rejected, r.Reason))
            .ToList();

        displayNames.TryGetValue(channel.CanonicalChannelId, out var displayName);

        return new SourceSelectionPreviewChannel(
            CanonicalChannelId: channel.CanonicalChannelId,
            CanonicalChannelKey: key,
            DisplayName: displayName,
            PolicyScope: scope,
            Policy: channel.Policy,
            CandidateCount: channel.CandidateCount,
            SelectedCount: selected.Count,
            RejectedCount: rejected.Count,
            Selected: selected,
            Rejected: rejected);
    }

    private static SourceSelectionPreviewCandidate ToCandidate(
        SelectionCandidate candidate,
        int? rank,
        string decision,
        string reason)
        => new(
            StreamUrlSanitized: CredentialSanitizer.SanitizeUrl(candidate.StreamUrl),
            SourceId: candidate.SourceId,
            SourcePriority: candidate.SourcePriority,
            Provider: candidate.Provider.Key,
            ExternalStreamId: candidate.ExternalStreamId,
            Quality: candidate.Quality.ToString(),
            Epg: candidate.Epg.ToString(),
            Availability: candidate.Availability.ToString(),
            LastResponseTimeMs: candidate.LastResponseTimeMs,
            IsWorking: candidate.IsWorking,
            Rank: rank,
            Decision: decision,
            Reason: reason);

    private static SourceSelectionPreviewMetrics BuildMetrics(
        int channelsProcessed,
        IReadOnlyList<ChannelSourceEntity> scopedSources,
        SourceSelectionStageResult result,
        int unmatchedStreamCount,
        int ambiguousStreamCount)
    {
        var sourceChannelIds = new HashSet<long>();
        foreach (var channelSource in scopedSources)
        {
            sourceChannelIds.Add(channelSource.CanonicalChannelId);
        }

        var rejectionCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var rejection in result.Rejected)
        {
            rejectionCounts[rejection.Reason] =
                rejectionCounts.TryGetValue(rejection.Reason, out var n) ? n + 1 : 1;
        }

        var candidateStreamCount = 0;
        var channelsAtChannelLimit = 0;
        foreach (var channel in result.Channels)
        {
            candidateStreamCount += channel.CandidateCount;
            if (channel.Rejected.Any(r => r.Reason == SelectionReasons.LimitReached))
            {
                channelsAtChannelLimit++;
            }
        }

        var diversitySelections = 0;
        var fillSelections = 0;
        foreach (var selection in result.Selected)
        {
            if (selection.Reason == SelectionReasons.Diversity) diversitySelections++;
            else if (selection.Reason == SelectionReasons.Fill) fillSelections++;
        }

        // Distribuição por fornecedor a partir das selecções por canal.
        var providerSelections = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var providerChannels = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        foreach (var channel in result.Channels)
        {
            foreach (var selection in channel.Selected)
            {
                var provider = selection.Candidate.Provider.Key;
                providerSelections[provider] =
                    providerSelections.TryGetValue(provider, out var n) ? n + 1 : 1;
                if (!providerChannels.TryGetValue(provider, out var ids))
                {
                    ids = new HashSet<long>();
                    providerChannels[provider] = ids;
                }
                ids.Add(channel.CanonicalChannelId);
            }
        }

        var distribution = providerSelections
            .Select(kvp => new SourceSelectionPreviewProviderStat(
                Provider: kvp.Key,
                SelectedCount: kvp.Value,
                ChannelCount: providerChannels.TryGetValue(kvp.Key, out var ids)
                    ? ids.Count
                    : 0))
            .ToList();

        return new SourceSelectionPreviewMetrics(
            ChannelsProcessed: channelsProcessed,
            ChannelsWithSources: sourceChannelIds.Count,
            CandidateStreamCount: candidateStreamCount,
            SelectedStreamCount: result.Selected.Count,
            RejectedStreamCount: result.Rejected.Count,
            UnmatchedStreamCount: unmatchedStreamCount,
            AmbiguousStreamCount: ambiguousStreamCount,
            TotalUnmatchedStreamCount: unmatchedStreamCount + ambiguousStreamCount,
            ChannelsAtChannelLimit: channelsAtChannelLimit,
            ChannelLimitRejectionCount: CountReason(rejectionCounts, SelectionReasons.LimitReached),
            ProviderLimitRejectionCount: CountReason(rejectionCounts, SelectionReasons.ProviderLimit),
            DiversitySelectionCount: diversitySelections,
            DistinctProviderCount: distribution.Count,
            FillSelectionCount: fillSelections,
            FallbackDisabledRejectionCount: CountReason(rejectionCounts, SelectionReasons.FallbackDisabled),
            SourceDisabledRejectionCount: CountReason(rejectionCounts, SourceSelectionStage.SourceDisabledReason),
            RejectionCounts: rejectionCounts,
            ProviderDistribution: distribution);
    }

    private static int CountReason(IReadOnlyDictionary<string, int> counts, string reason)
        => counts.TryGetValue(reason, out var n) ? n : 0;

    private static SourceSelectionPreviewMetrics EmptyMetrics() => new(
        ChannelsProcessed: 0,
        ChannelsWithSources: 0,
        CandidateStreamCount: 0,
        SelectedStreamCount: 0,
        RejectedStreamCount: 0,
        UnmatchedStreamCount: 0,
        AmbiguousStreamCount: 0,
        TotalUnmatchedStreamCount: 0,
        ChannelsAtChannelLimit: 0,
        ChannelLimitRejectionCount: 0,
        ProviderLimitRejectionCount: 0,
        DiversitySelectionCount: 0,
        DistinctProviderCount: 0,
        FillSelectionCount: 0,
        FallbackDisabledRejectionCount: 0,
        SourceDisabledRejectionCount: 0,
        RejectionCounts: new SortedDictionary<string, int>(StringComparer.Ordinal),
        ProviderDistribution: Array.Empty<SourceSelectionPreviewProviderStat>());
}
