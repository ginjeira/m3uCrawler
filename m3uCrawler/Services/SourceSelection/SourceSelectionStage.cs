using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;

namespace m3uCrawler.Services.SourceSelection;

/// <summary>
/// Defaults da política de selecção para a Wave 13-3 (sem persistência).
/// O valor <c>10</c> é apenas o default de configuração — o algoritmo
/// (<see cref="ChannelSourceSelector"/>) não tem limites hardcoded.
/// </summary>
public static class SourceSelectionDefaults
{
    public static SourceSelectionPolicy DefaultPolicy { get; } = new(
        MaxSourcesPerChannel: 10,
        PreferDistinctProviders: true,
        MaxSourcesPerProvider: null,
        AllowFallbackToSameProvider: true);
}

/// <summary>
/// PHASE 13 (Wave 13-3) — Contrato do estágio que aplica a selecção de
/// fontes do catálogo às streams do pipeline Telegram, antes da
/// publicação em <c>output/playlist.m3u</c>.
/// </summary>
public interface ISourceSelectionStage
{
    Task<SourceSelectionStageResult> ApplyAsync(
        IReadOnlyList<M3uStream> streams,
        SourceSelectionPolicy policy,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// PHASE 13 (Wave 13-3 / 13-4b) — Junta as streams do pipeline (URL real, em
/// memória) aos <c>ChannelSource</c> do catálogo (URL sanitizada) e
/// aplica <see cref="IChannelSourceSelector"/> por canal canónico.
///
/// <para>
/// <b>Read-only e sem persistência.</b> Só lê o catálogo. Não insere/
/// actualiza/apaga nada e nunca persiste a URL real. Não escreve ficheiros;
/// a publicação é responsabilidade do caller
/// (<c>PlaylistManagerService.SaveToM3uPlaylist</c>).
/// </para>
///
/// <para>
/// Recebe um <see cref="ISourceSelectionPolicyProvider"/> (tipicamente um
/// <see cref="SourceSelectionPolicySet"/>) e resolve a política efectiva por
/// canal canónico antes de invocar o selector. O overload que recebe uma
/// única <see cref="SourceSelectionPolicy"/> continua suportado e delega no
/// overload de provider através de <see cref="SourceSelectionPolicySet.Constant"/>.
/// </para>
///
/// <para>
/// <b>Junção exacta</b> por <c>CredentialSanitizer.SanitizeUrl(realUrl)</c>,
/// que é a chave de unicidade usada pelo próprio catálogo
/// (<c>CatalogResolver.RecordChannelSourceAsync</c>). Sem matching
/// aproximado, por título ou por host.
/// </para>
///
/// <para>
/// <b>Segurança / resiliência:</b> se o catálogo não estiver disponível,
/// estiver vazio, ou a leitura falhar, o estágio é um no-op e todas as
/// streams passam inalteradas. Streams sem correspondência inequívoca
/// (0 ou >1 canais canónicos) fazem pass-through e não contam para os
/// limites.
/// </para>
/// </summary>
public sealed class SourceSelectionStage : ISourceSelectionStage
{
    /// <summary>Motivo (stage-level) para uma stream cujo ChannelSource está desactivado.</summary>
    public const string SourceDisabledReason = "source-disabled";

    /// <summary>Motivo (stage-level) para URL mapeada a mais de um canal canónico.</summary>
    public const string AmbiguousMappingReason = "ambiguous-mapping";

    private readonly CatalogResolver? _catalog;
    private readonly IChannelSourceSelector _selector;

    public SourceSelectionStage(CatalogResolver? catalog, IChannelSourceSelector? selector = null)
    {
        _catalog = catalog;
        _selector = selector ?? new ChannelSourceSelector();
    }

    /// <summary>
    /// Overload legado: aplica uma política única a todos os canais.
    /// Delega no overload que recebe um provider, sem duplicar o algoritmo.
    /// </summary>
    public Task<SourceSelectionStageResult> ApplyAsync(
        IReadOnlyList<M3uStream> streams,
        SourceSelectionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return ApplyAsync(streams, SourceSelectionPolicySet.Constant(policy), cancellationToken);
    }

    /// <summary>
    /// Aplica a selecção resolvendo a política efectiva por canal canónico
    /// através de <paramref name="policies"/>.
    /// </summary>
    public async Task<SourceSelectionStageResult> ApplyAsync(
        IReadOnlyList<M3uStream> streams,
        ISourceSelectionPolicyProvider policies,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(policies);

        if (_catalog is null || streams.Count == 0)
        {
            return SourceSelectionStageResult.NoOp(streams);
        }

        IReadOnlyList<ChannelSourceEntity> channelSources;
        IReadOnlyList<SourceEntity> sources;
        try
        {
            channelSources = await _catalog.ListChannelSourcesAsync(cancellationToken: cancellationToken);
            sources = await _catalog.ListSourcesAsync(cancellationToken);
        }
        catch (Exception)
        {
            // Leitura do catálogo é best-effort: uma falha não pode quebrar a pipeline.
            return SourceSelectionStageResult.NoOp(streams);
        }

        if (channelSources.Count == 0)
        {
            return SourceSelectionStageResult.NoOp(streams);
        }

        var priorityBySource = new Dictionary<long, int>();
        foreach (var source in sources)
        {
            priorityBySource[source.Id] = source.Priority;
        }

        var bySanitizedUrl = new Dictionary<string, List<ChannelSourceEntity>>(StringComparer.Ordinal);
        foreach (var channelSource in channelSources)
        {
            if (string.IsNullOrEmpty(channelSource.StreamUrl)) continue;
            if (!bySanitizedUrl.TryGetValue(channelSource.StreamUrl, out var list))
            {
                list = new List<ChannelSourceEntity>();
                bySanitizedUrl[channelSource.StreamUrl] = list;
            }
            list.Add(channelSource);
        }

        var matched = new List<MatchedEntry>();
        var unmatched = new List<M3uStream>();
        var ambiguousCount = 0;

        foreach (var stream in streams)
        {
            var key = CredentialSanitizer.SanitizeUrl(stream.Url);
            if (string.IsNullOrEmpty(key)
                || !bySanitizedUrl.TryGetValue(key, out var hits)
                || hits.Count == 0)
            {
                unmatched.Add(stream);
                continue;
            }

            if (hits.Select(h => h.CanonicalChannelId).Distinct().Count() > 1)
            {
                ambiguousCount++;
                unmatched.Add(stream);
                continue;
            }

            var channelSource = hits.OrderBy(h => h.Id).First();
            var priority = priorityBySource.TryGetValue(channelSource.SourceId, out var p) ? p : 0;
            matched.Add(new MatchedEntry(stream, channelSource, priority));
        }

        var selected = new List<SelectedSource>();
        var rejected = new List<RejectedSource>();
        var published = new List<M3uStream>();
        var matchedChannelCount = 0;

        foreach (var group in matched
                     .GroupBy(m => m.ChannelSource.CanonicalChannelId)
                     .OrderBy(g => g.Key))
        {
            matchedChannelCount++;

            // Agrupamento continua por CanonicalChannelId; a Key serve apenas
            // para resolver o override de política (ListChannelSourcesAsync
            // já faz Include de CanonicalChannel).
            var canonicalKey = group
                .Select(e => e.ChannelSource.CanonicalChannel?.Key)
                .FirstOrDefault(k => !string.IsNullOrEmpty(k));
            var groupPolicy = policies.Resolve(canonicalKey);

            var enabled = new List<MatchedEntry>();
            foreach (var entry in group)
            {
                if (!entry.ChannelSource.IsEnabled)
                {
                    // Excluída da publicação e não entra no selector (Wave 13-3).
                    rejected.Add(new RejectedSource(entry.Candidate, SourceDisabledReason));
                    continue;
                }
                enabled.Add(entry);
            }

            if (enabled.Count == 0) continue;

            var candidates = enabled.Select(e => e.Candidate).ToList();
            var byCandidate = new Dictionary<SelectionCandidate, MatchedEntry>(ReferenceEqualityComparer.Instance);
            foreach (var entry in enabled)
            {
                byCandidate[entry.Candidate] = entry;
            }

            var result = _selector.Select(candidates, groupPolicy);

            foreach (var sel in result.Selected)
            {
                selected.Add(sel);
                published.Add(byCandidate[sel.Candidate].Stream);
            }
            foreach (var rej in result.Rejected)
            {
                rejected.Add(rej);
            }
        }

        // Streams sem correspondência inequívoca: pass-through na ordem de entrada.
        foreach (var stream in unmatched)
        {
            published.Add(stream);
        }

        return new SourceSelectionStageResult(
            Published: published,
            Selected: selected,
            Rejected: rejected,
            Unmatched: unmatched,
            MatchedChannelCount: matchedChannelCount,
            AmbiguousCount: ambiguousCount,
            Applied: true);
    }

    /// <summary>
    /// Deriva a identidade de fornecedor da URL real: host normalizado
    /// (lowercase, sem ponto final, sem prefixo <c>www.</c>, sem porta).
    /// Não resolve aliases/CDN/proxy. Devolve <c>null</c> se não for
    /// possível extrair um host (o selector trata como <c>Unknown</c>).
    /// </summary>
    internal static string? NormalizeProviderHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        var host = uri.Host;
        if (string.IsNullOrEmpty(host)) return null;
        host = host.TrimEnd('.');
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            host = host.Substring(4);
        }
        return host.ToLowerInvariant();
    }

    private sealed class MatchedEntry
    {
        public MatchedEntry(M3uStream stream, ChannelSourceEntity channelSource, int sourcePriority)
        {
            Stream = stream;
            ChannelSource = channelSource;
            Candidate = new SelectionCandidate(
                StreamUrl: stream.Url,
                SourceId: channelSource.SourceId,
                SourcePriority: sourcePriority,
                Quality: StreamQuality.Unknown,
                Epg: EpgState.Unknown,
                Availability: stream.IsWorking ? AvailabilityState.Reachable : AvailabilityState.Dead,
                LastResponseTimeMs: (long)stream.ResponseTime,
                ExternalStreamId: channelSource.ExternalStreamId,
                Provider: ProviderIdentity.Normalize(NormalizeProviderHost(stream.Url)),
                IsWorking: stream.IsWorking);
        }

        public M3uStream Stream { get; }
        public ChannelSourceEntity ChannelSource { get; }
        public SelectionCandidate Candidate { get; }
    }
}
