using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Models;

namespace m3uCrawler.Services.Catalog;

/// <summary>
/// PHASE 7 — Compõe uma playlist M3U a partir de:
///   <list type="bullet">
///     <item>uma <see cref="OrderingListEntity"/> (ordem dos canais);</item>
///     <item>os <see cref="ChannelSourceEntity"/> disponíveis para cada
///             <see cref="CanonicalChannelEntity"/>;</item>
///     <item>uma <see cref="SourcePriorityPolicyEntity"/> (global ou
///             por canal) que escolhe qual a stream a usar.</item>
///   </list>
///
/// A playlist gerada preserva proveniência interna: o stream
/// escolhido fica registado em <see cref="PlaylistEntry.ChosenChannelSourceId"/>.
/// </summary>
public sealed class PlaylistComposerService
{
    private readonly IDbContextFactory<ChannelCatalogDbContext> _factory;

    public PlaylistComposerService(IDbContextFactory<ChannelCatalogDbContext> factory)
    {
        _factory = factory;
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

        var eligibleByChannel = channelSources
            .Where(cs => cs.Availability != AvailabilityState.Dead
                      && cs.Availability != AvailabilityState.Unreachable)
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
            if (!eligibleByChannel.TryGetValue(channel.Id, out var candidates) || candidates.Count == 0)
            {
                missing.Add(new MissingChannel(channel.Id, "no-eligible-source"));
                continue;
            }

            var policy = perChannelPolicies.TryGetValue(channel.Id, out var p) ? p : globalPolicy;
            var chosen = SourceSelector.Select(candidates, policy, overridePolicyScopeChannelId);
            if (chosen == null)
            {
                missing.Add(new MissingChannel(channel.Id, "no-eligible-source"));
                continue;
            }

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

/// <summary>
/// Aplica a política de prioridade para escolher a melhor stream
/// para um canal. Estratégia (PHASE 6) — pura, sem I/O.
/// </summary>
public static class SourceSelector
{
    public static ChannelSourceEntity? Select(
        IReadOnlyList<ChannelSourceEntity> candidates,
        SourcePriorityPolicyEntity? policy,
        long? preferredSourceId = null)
    {
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        var order = ParseCriteria(policy?.CriteriaJson);
        var preferredQuality = ParsePreferredQualities(policy?.PreferredQuality);

        // 1. Manual (preferredSourceId).
        if (preferredSourceId.HasValue && order.Contains("Manual"))
        {
            var manual = candidates.FirstOrDefault(c => c.SourceId == preferredSourceId.Value);
            if (manual != null) return manual;
        }

        IEnumerable<ChannelSourceEntity> work = candidates;

        // 2. Quality (preferred order).
        if (order.Contains("Quality") && preferredQuality.Count > 0)
        {
            work = work
                .OrderBy(c => QualityRank(c.Quality, preferredQuality))
                .ThenByDescending(c => c.Source?.Priority ?? 0);
            var first = work.FirstOrDefault();
            if (first != null) return first;
        }

        // 3. Reliability (channels que responderam, com menor response time).
        if (order.Contains("Reliability"))
        {
            return work
                .OrderBy(c => c.LastResponseTimeMs == 0 ? long.MaxValue : c.LastResponseTimeMs)
                .ThenByDescending(c => c.Source?.Priority ?? 0)
                .FirstOrDefault();
        }

        // 4. EPG preferida.
        if (order.Contains("EPG"))
        {
            return work
                .OrderBy(c => c.Epg == EpgState.Available ? 0 : 1)
                .ThenByDescending(c => c.Source?.Priority ?? 0)
                .FirstOrDefault();
        }

        // 5. Availability (Validated > Reachable > Discovered > Timeout > Unreachable > Dead).
        if (order.Contains("Availability"))
        {
            return work
                .OrderBy(c => AvailabilityRank(c.Availability))
                .ThenByDescending(c => c.Source?.Priority ?? 0)
                .FirstOrDefault();
        }

        // Default: source priority desc.
        return candidates
            .OrderByDescending(c => c.Source?.Priority ?? 0)
            .ThenBy(c => c.Id)
            .FirstOrDefault();
    }

    private static int QualityRank(StreamQuality q, List<StreamQuality> preferred)
    {
        var idx = preferred.IndexOf(q);
        return idx < 0 ? preferred.Count : idx;
    }

    private static int AvailabilityRank(AvailabilityState s) => s switch
    {
        AvailabilityState.Validated => 0,
        AvailabilityState.Reachable => 1,
        AvailabilityState.Discovered => 2,
        AvailabilityState.Timeout => 3,
        AvailabilityState.Unreachable => 4,
        AvailabilityState.Dead => 5,
        _ => 6,
    };

    public static IReadOnlyList<string> ParseCriteria(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static List<StreamQuality> ParsePreferredQualities(string? csv)
    {
        var list = new List<StreamQuality>();
        if (string.IsNullOrWhiteSpace(csv)) return list;
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse<StreamQuality>(part.Trim(), true, out var q))
            {
                list.Add(q);
            }
        }
        return list;
    }
}
