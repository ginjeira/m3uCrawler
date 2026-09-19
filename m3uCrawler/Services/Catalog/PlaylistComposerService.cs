using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Models;
using m3uCrawler.Services.SourceSelection;

namespace m3uCrawler.Services.Catalog;

/// <summary>
/// PHASE 7 — Compõe uma playlist M3U a partir de:
///   <list type="bullet">
///     <item>uma <see cref="OrderingListEntity"/> (ordem dos canais);</item>
///     <item>os <see cref="ChannelSourceEntity"/> disponíveis para cada
///             <see cref="CanonicalChannelEntity"/>;</item>
///     <item>uma <see cref="SourcePriorityPolicyEntity"/> (global ou
///             por canal) que define a preferência de qualidade.</item>
///   </list>
///
/// A escolha da stream concreta é delegada no <b>único</b> selector
/// <see cref="IChannelSourceSelector"/> (DL-101 / ADR-0003), o mesmo usado
/// pelo <see cref="SourceSelectionStage"/> do pipeline Telegram. O composer
/// não re-rankeia nem adiciona scoring: projeta <see cref="SelectionCandidate"/>
/// e pede uma única fonte (<c>MaxSourcesPerChannel = 1</c>).
///
/// A playlist gerada preserva proveniência interna: o stream
/// escolhido fica registado em <see cref="PlaylistEntry.ChosenChannelSourceId"/>.
/// </summary>
public sealed class PlaylistComposerService
{
    private readonly IDbContextFactory<ChannelCatalogDbContext> _factory;
    private readonly IChannelSourceSelector _selector;

    public PlaylistComposerService(
        IDbContextFactory<ChannelCatalogDbContext> factory,
        IChannelSourceSelector? selector = null)
    {
        _factory = factory;
        _selector = selector ?? new ChannelSourceSelector();
    }

    /// <summary>
    /// Compõe a playlist para uma ordering list. Itens com
    /// <c>IsEnabled=false</c> ou cujo canal está desabilitado são
    /// saltados. Se o canal não tiver nenhum ChannelSource válido,
    /// a entrada é omitida (a omissão é silenciosa para não
    /// gerar warnings em massa; o caller pode ver
    /// <see cref="PlaylistComposition.MissingChannels"/>).
    /// </summary>
    public async Task<PlaylistComposition> ComposeAsync(
        long orderingListId,
        long? overridePolicyScopeChannelId = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);

        var list = await context.OrderingLists
            .AsNoTracking()
            .Include(l => l.Items.OrderBy(i => i.Position))
                .ThenInclude(i => i.CanonicalChannel)
            .FirstOrDefaultAsync(l => l.Id == orderingListId, cancellationToken);

        if (list == null) throw new InvalidOperationException($"OrderingList #{orderingListId} não encontrada.");

        var items = list.Items.Where(i => i.IsEnabled).ToList();
        if (items.Count == 0)
        {
            return new PlaylistComposition(list.Id, list.Name, Array.Empty<PlaylistEntry>(),
                Array.Empty<MissingChannel>(), 0);
        }

        var channels = items.Select(i => i.CanonicalChannel).Where(c => c != null).Cast<CanonicalChannelEntity>().ToList();
        var enabledChannelIds = channels.Where(c => c.IsEnabled).Select(c => c.Id).ToHashSet();

        // Carrega TODOS os channel sources activos (incluindo Dead/Unreachable)
        // para distinguir "canal sem nenhuma source" de "canal com sources
        // todas em estado terminal".
        var channelSources = await context.ChannelSources
            .AsNoTracking()
            .Include(cs => cs.Source)
            .Where(cs => enabledChannelIds.Contains(cs.CanonicalChannelId) && cs.IsEnabled)
            .ToListAsync(cancellationToken);

        var allByChannel = channelSources
            .GroupBy(cs => cs.CanonicalChannelId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var globalPolicy = await context.SourcePriorityPolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Scope == "global", cancellationToken);
        var perChannelPolicies = await context.SourcePriorityPolicies
            .AsNoTracking()
            .Where(p => p.CanonicalChannelId != null && enabledChannelIds.Contains(p.CanonicalChannelId.Value))
            .ToDictionaryAsync(p => p.CanonicalChannelId!.Value, cancellationToken);

        var entries = new List<PlaylistEntry>(items.Count);
        var missing = new List<MissingChannel>();

        var compositionPolicy = new SourceSelectionPolicy(
            MaxSourcesPerChannel: 1,
            PreferDistinctProviders: false,
            MaxSourcesPerProvider: 1,
            AllowFallbackToSameProvider: true);

        foreach (var item in items)
        {
            var channel = item.CanonicalChannel;
            if (channel == null || !channel.IsEnabled)
            {
                missing.Add(new MissingChannel(item.CanonicalChannelId, "channel-disabled"));
                continue;
            }
            if (!allByChannel.TryGetValue(channel.Id, out var allCandidates) || allCandidates.Count == 0)
            {
                missing.Add(new MissingChannel(channel.Id, "no-channel-source"));
                continue;
            }

            var priorityPolicy = perChannelPolicies.TryGetValue(channel.Id, out var p) ? p : globalPolicy;
            var criteria = new SourceSelectionCriteria(
                ChannelOverride: overridePolicyScopeChannelId is null
                    ? null
                    : new ChannelSourceOverride(overridePolicyScopeChannelId.Value),
                PreferredQualities: ParsePreferredQualities(priorityPolicy?.PreferredQuality),
                UseValidationFreshness: false);

            var byCandidate = new Dictionary<SelectionCandidate, ChannelSourceEntity>(
                ReferenceEqualityComparer.Instance);
            var candidates = new List<SelectionCandidate>(allCandidates.Count);
            foreach (var channelSource in allCandidates)
            {
                var candidate = ToCandidate(channelSource);
                candidates.Add(candidate);
                byCandidate[candidate] = channelSource;
            }

            var result = _selector.Select(candidates, compositionPolicy, criteria);
            if (result.Selected.Count == 0)
            {
                missing.Add(new MissingChannel(channel.Id, "no-eligible-source"));
                continue;
            }

            var chosen = byCandidate[result.Selected[0].Candidate];
            entries.Add(new PlaylistEntry(
                channel.Id,
                channel.Key,
                channel.DisplayName,
                channel.EditorialGroup.ToString(),
                chosen.StreamUrl,
                chosen.Id,
                chosen.SourceId,
                chosen.Source?.Name ?? "—",
                chosen.Quality.ToString()));
        }

        return new PlaylistComposition(list.Id, list.Name, entries, missing, entries.Count);
    }

    /// <summary>
    /// Projeta um <see cref="ChannelSourceEntity"/> para o candidato DL-101.
    /// <c>IsWorking</c> deriva do estado de disponibilidade (o catálogo não
    /// guarda um flag próprio) e a frescura só conta para streams funcionais.
    /// </summary>
    private static SelectionCandidate ToCandidate(ChannelSourceEntity channelSource)
    {
        var working = channelSource.Availability
            is not AvailabilityState.Dead and not AvailabilityState.Unreachable;

        return new SelectionCandidate(
            StreamUrl: channelSource.StreamUrl,
            SourceId: channelSource.SourceId,
            SourcePriority: channelSource.Source?.Priority ?? 0,
            Quality: channelSource.Quality,
            Epg: channelSource.Epg,
            Availability: channelSource.Availability,
            LastResponseTimeMs: channelSource.LastResponseTimeMs,
            ExternalStreamId: channelSource.ExternalStreamId,
            Provider: ProviderIdentity.Normalize(
                SourceSelectionStage.NormalizeProviderHost(channelSource.StreamUrl)),
            IsWorking: working,
            StreamFingerprint: null,
            LastSuccessfulValidationUtc: working && channelSource.LastTestedAtUtc != default
                ? channelSource.LastTestedAtUtc
                : null);
    }

    /// <summary>
    /// Converte a preferência de qualidade persistida (CSV, ex.
    /// <c>"UHD,FHD,HD,SD"</c>) na ordem usada pelo critério 4 de DL-101.
    /// Tokens desconhecidos são ignorados; vazio significa ordem natural.
    /// </summary>
    private static IReadOnlyList<StreamQuality>? ParsePreferredQualities(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var list = new List<StreamQuality>();
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse<StreamQuality>(part.Trim(), true, out var quality))
            {
                list.Add(quality);
            }
        }
        return list.Count == 0 ? null : list;
    }
}

/// <summary>
/// Resultado de uma composição de playlist.
/// </summary>
public sealed record PlaylistComposition(
    long OrderingListId,
    string OrderingListName,
    IReadOnlyList<PlaylistEntry> Entries,
    IReadOnlyList<MissingChannel> MissingChannels,
    int TotalEntries);

public sealed record PlaylistEntry(
    long CanonicalChannelId,
    string CanonicalKey,
    string DisplayName,
    string Group,
    string StreamUrl,
    long ChosenChannelSourceId,
    long SourceId,
    string SourceName,
    string Quality);

public sealed record MissingChannel(long CanonicalChannelId, string Reason);
