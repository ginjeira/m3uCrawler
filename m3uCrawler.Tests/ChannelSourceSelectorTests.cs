using System;
using System.Collections.Generic;
using System.Linq;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.SourceSelection;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 13 (Wave 13-1) — Testes da política pura de selecção de
/// <c>ChannelSource</c>. Sem rede, sem ficheiros, sem base de dados,
/// sem Dispatcharr: apenas <c>Candidates + Policy → SourceSelectionResult</c>.
/// </summary>
public class ChannelSourceSelectorTests
{
    private readonly ChannelSourceSelector _selector = new();

    // ---------------- helpers ----------------

    private static SelectionCandidate Candidate(
        string url,
        long sourceId,
        string? provider = "prov-a",
        int priority = 0,
        StreamQuality quality = StreamQuality.Unknown,
        EpgState epg = EpgState.Unknown,
        AvailabilityState availability = AvailabilityState.Reachable,
        long responseTimeMs = 0,
        string? externalStreamId = null,
        bool isWorking = true)
        => new(
            url,
            sourceId,
            priority,
            quality,
            epg,
            availability,
            responseTimeMs,
            externalStreamId,
            ProviderIdentity.Normalize(provider),
            isWorking);

    private static SourceSelectionPolicy Policy(
        int max = 10,
        bool distinctProviders = true,
        int? maxPerProvider = null,
        bool allowFallback = true)
        => new(max, distinctProviders, maxPerProvider, allowFallback);

    private static List<SelectionCandidate> Many(int count, int providers = 1)
    {
        var list = new List<SelectionCandidate>(count);
        for (var i = 0; i < count; i++)
        {
            var bucket = i % providers;
            list.Add(Candidate(
                url: $"http://host{bucket:D3}.example/s{i:D3}.ts",
                sourceId: i + 1,
                provider: $"prov-{bucket:D3}"));
        }
        return list;
    }

    private static string Signature(SourceSelectionResult r) =>
        string.Join("|", r.Selected.Select(s => $"{s.Rank}:{s.Candidate.StreamUrl}:{s.Reason}"))
        + "||"
        + string.Join("|", r.Rejected.Select(x => $"{x.Candidate.StreamUrl}:{x.Reason}"));

    /// <summary>
    /// Permutações determinísticas de uma lista (original, inversa,
    /// duas rotações e uma permutação fixa) para provar independência da
    /// ordem de entrada sem usar aleatoriedade.
    /// </summary>
    private static IEnumerable<IReadOnlyList<SelectionCandidate>> Permutations(
        IReadOnlyList<SelectionCandidate> list)
    {
        yield return list;
        yield return list.Reverse().ToList();
        yield return list.Skip(1).Concat(list.Take(1)).ToList();
        yield return list.Skip(3).Concat(list.Take(3)).ToList();

        var permuted = new List<SelectionCandidate>(list.Count);
        var order = new[] { 4, 1, 5, 0, 3, 2 };
        foreach (var i in order)
        {
            if (i < list.Count) permuted.Add(list[i]);
        }
        for (var i = 6; i < list.Count; i++) permuted.Add(list[i]);
        yield return permuted;
    }

    // ---------------- volume ----------------

    [Fact]
    public void Limit_1_with_100_candidates_selects_exactly_one()
    {
        var result = _selector.Select(Many(100, 100), Policy(max: 1));

        Assert.Single(result.Selected);
        Assert.Equal(99, result.Rejected.Count);
        Assert.Equal(100, result.TotalCandidates);
    }

    [Fact]
    public void Limit_10_with_100_candidates_selects_at_most_10()
    {
        // Teste crítico: 100 fontes NÃO implicam 100 associações.
        var result = _selector.Select(Many(100, 100), Policy(max: 10));

        Assert.Equal(10, result.Selected.Count);
        Assert.True(result.Selected.Count < result.TotalCandidates);
        Assert.Equal(90, result.Rejected.Count);
        Assert.Equal(Enumerable.Range(0, 10), result.Selected.Select(s => s.Rank));
    }

    [Fact]
    public void Limit_100_with_100_distinct_candidates_selects_all()
    {
        var result = _selector.Select(Many(100, 100), Policy(max: 100));

        Assert.Equal(100, result.Selected.Count);
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public void Fewer_than_limit_selects_all()
    {
        var result = _selector.Select(Many(7, 7), Policy(max: 10));

        Assert.Equal(7, result.Selected.Count);
    }

    [Fact]
    public void Exactly_limit_selects_all()
    {
        var result = _selector.Select(Many(10, 10), Policy(max: 10));

        Assert.Equal(10, result.Selected.Count);
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public void More_than_limit_selects_limit()
    {
        var result = _selector.Select(Many(25, 25), Policy(max: 10));

        Assert.Equal(10, result.Selected.Count);
    }

    // ---------------- deduplicação ----------------

    [Fact]
    public void Identical_urls_are_deduplicated()
    {
        var candidates = new List<SelectionCandidate>
        {
            Candidate("http://same.example/live.ts", 1, "prov-a"),
            Candidate("http://same.example/live.ts", 2, "prov-b"),
        };

        var result = _selector.Select(candidates, Policy(max: 10));

        Assert.Single(result.Selected);
        Assert.Single(result.Rejected);
        Assert.Equal(SelectionReasons.DuplicateUrl, result.Rejected[0].Reason);
    }

    [Fact]
    public void Equivalent_urls_after_normalization_are_deduplicated()
    {
        var candidates = new List<SelectionCandidate>
        {
            Candidate("HTTP://Example.com:80/live/a.ts", 1, "prov-a"),
            Candidate("http://example.com/live/a.ts", 2, "prov-b"),
        };

        var result = _selector.Select(candidates, Policy(max: 10));

        Assert.Single(result.Selected);
        Assert.Equal(SelectionReasons.DuplicateUrl, result.Rejected.Single().Reason);
    }

    [Fact]
    public void Different_urls_are_not_deduplicated()
    {
        var candidates = new List<SelectionCandidate>
        {
            Candidate("http://a.example/live.ts", 1, "prov-a"),
            Candidate("http://b.example/live.ts", 2, "prov-b"),
        };

        var result = _selector.Select(candidates, Policy(max: 10));

        Assert.Equal(2, result.Selected.Count);
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public void Urls_differing_only_by_fragment_are_equivalent()
    {
        var result = _selector.Select(
            new[]
            {
                Candidate("http://frag.example/x#one", 1, "prov-a"),
                Candidate("http://frag.example/x#two", 2, "prov-b"),
            },
            Policy(max: 10));

        Assert.Single(result.Selected);
        Assert.Equal(SelectionReasons.DuplicateUrl, result.Rejected.Single().Reason);
    }

    [Fact]
    public void Urls_differing_only_by_scheme_are_not_equivalent()
    {
        var result = _selector.Select(
            new[]
            {
                Candidate("http://scheme.example/x", 1, "prov-a"),
                Candidate("https://scheme.example/x", 2, "prov-b"),
            },
            Policy(max: 10));

        Assert.Equal(2, result.Selected.Count);
    }

    [Fact]
    public void Urls_differing_only_by_query_are_not_equivalent()
    {
        var result = _selector.Select(
            new[]
            {
                Candidate("http://query.example/x?a=1", 1, "prov-a"),
                Candidate("http://query.example/x?a=2", 2, "prov-b"),
            },
            Policy(max: 10));

        Assert.Equal(2, result.Selected.Count);
    }

    [Fact]
    public void Urls_differing_only_by_userinfo_are_not_equivalent()
    {
        var result = _selector.Select(
            new[]
            {
                Candidate("http://user:pass@auth.example/x", 1, "prov-a"),
                Candidate("http://auth.example/x", 2, "prov-b"),
            },
            Policy(max: 10));

        Assert.Equal(2, result.Selected.Count);
    }

    [Fact]
    public void Multiple_candidates_same_provider_are_all_selected_with_fallback()
    {
        var candidates = new List<SelectionCandidate>
        {
            Candidate("http://a.example/1.ts", 1, "prov-a"),
            Candidate("http://a.example/2.ts", 2, "prov-a"),
            Candidate("http://a.example/3.ts", 3, "prov-a"),
        };

        var result = _selector.Select(candidates, Policy(max: 10, allowFallback: true));

        Assert.Equal(3, result.Selected.Count);
        Assert.Equal(SelectionReasons.Diversity, result.Selected[0].Reason);
        Assert.Equal(new[] { SelectionReasons.Fill, SelectionReasons.Fill },
            result.Selected.Skip(1).Select(s => s.Reason));
    }

    // ---------------- providers ----------------

    [Fact]
    public void Distinct_providers_are_favored_in_phase_a()
    {
        var candidates = new List<SelectionCandidate>
        {
            Candidate("http://a1.example/x.ts", 1, "prov-a", priority: 9),
            Candidate("http://a2.example/x.ts", 2, "prov-a", priority: 8),
            Candidate("http://b1.example/x.ts", 3, "prov-b", priority: 1),
            Candidate("http://b2.example/x.ts", 4, "prov-b", priority: 1),
            Candidate("http://c1.example/x.ts", 5, "prov-c", priority: 1),
            Candidate("http://c2.example/x.ts", 6, "prov-c", priority: 1),
        };

        var result = _selector.Select(candidates, Policy(max: 3, distinctProviders: true));

        Assert.Equal(3, result.Selected.Count);
        Assert.Equal(3, result.Selected.Select(s => s.Candidate.Provider.Key).Distinct().Count());
    }

    [Fact]
    public void All_same_provider_with_fallback_fills_up_to_limit()
    {
        var candidates = new List<SelectionCandidate>
        {
            Candidate("http://s.example/1.ts", 1, "prov-a"),
            Candidate("http://s.example/2.ts", 2, "prov-a"),
            Candidate("http://s.example/3.ts", 3, "prov-a"),
            Candidate("http://s.example/4.ts", 4, "prov-a"),
            Candidate("http://s.example/5.ts", 5, "prov-a"),
        };

        var result = _selector.Select(candidates, Policy(max: 3, allowFallback: true));

        Assert.Equal(3, result.Selected.Count);
    }

    [Fact]
    public void All_same_provider_without_fallback_selects_one()
    {
        var candidates = new List<SelectionCandidate>
        {
            Candidate("http://s.example/1.ts", 1, "prov-a"),
            Candidate("http://s.example/2.ts", 2, "prov-a"),
            Candidate("http://s.example/3.ts", 3, "prov-a"),
        };

        var result = _selector.Select(candidates, Policy(max: 3, allowFallback: false));

        Assert.Single(result.Selected);
        Assert.All(result.Rejected, r => Assert.Equal(SelectionReasons.FallbackDisabled, r.Reason));
    }

    [Fact]
    public void Unknown_provider_collapses_to_single_identity()
    {
        // Todos desconhecidos → uma única identidade → não ganham diversidade.
        var candidates = new List<SelectionCandidate>
        {
            Candidate("http://u1.example/x.ts", 1, provider: null),
            Candidate("http://u2.example/x.ts", 2, provider: "(unknown)"),
            Candidate("http://u3.example/x.ts", 3, provider: "unknown"),
        };

        var result = _selector.Select(candidates, Policy(max: 3, distinctProviders: true, maxPerProvider: 1, allowFallback: true));

        Assert.Single(result.Selected);
        Assert.True(result.Selected[0].Candidate.Provider.IsUnknown);
    }

    [Fact]
    public void Known_and_unknown_provider_are_distinct()
    {
        var candidates = new List<SelectionCandidate>
        {
            Candidate("http://k.example/x.ts", 1, "prov-a"),
            Candidate("http://u.example/x.ts", 2, provider: null),
        };

        var result = _selector.Select(candidates, Policy(max: 2, distinctProviders: true, maxPerProvider: 1));

        Assert.Equal(2, result.Selected.Count);
        Assert.Contains(result.Selected, s => s.Candidate.Provider.IsUnknown);
        Assert.Contains(result.Selected, s => !s.Candidate.Provider.IsUnknown);
    }

    [Fact]
    public void Mixed_known_and_unknown_providers_respect_diversity()
    {
        var candidates = new List<SelectionCandidate>
        {
            Candidate("http://k1.example/x.ts", 1, "prov-a"),
            Candidate("http://k2.example/x.ts", 2, "prov-b"),
            Candidate("http://u1.example/x.ts", 3, provider: null),
            Candidate("http://u2.example/x.ts", 4, provider: "unknown"),
        };

        var result = _selector.Select(candidates, Policy(max: 3, distinctProviders: true));

        Assert.Equal(3, result.Selected.Count);
        Assert.Single(result.Selected, s => s.Candidate.Provider.IsUnknown);
    }

    // ---------------- diversity on/off ----------------

    [Fact]
    public void PreferDistinctProviders_true_prioritizes_distinct_providers()
    {
        var candidates = new List<SelectionCandidate>
        {
            Candidate("http://a1.example/x.ts", 1, "prov-a", priority: 10),
            Candidate("http://a2.example/x.ts", 2, "prov-a", priority: 9),
            Candidate("http://b1.example/x.ts", 3, "prov-b", priority: 1),
        };

        var result = _selector.Select(candidates, Policy(max: 2, distinctProviders: true));

        Assert.Equal(2, result.Selected.Count);
        Assert.Equal(2, result.Selected.Select(s => s.Candidate.Provider.Key).Distinct().Count());
        Assert.Contains(result.Selected, s => s.Candidate.Provider.Key == "prov-b");
    }

    [Fact]
    public void PreferDistinctProviders_false_takes_best_by_rank()
    {
        var candidates = new List<SelectionCandidate>
        {
            Candidate("http://a1.example/x.ts", 1, "prov-a", priority: 10),
            Candidate("http://a2.example/x.ts", 2, "prov-a", priority: 9),
            Candidate("http://b1.example/x.ts", 3, "prov-b", priority: 1),
        };

        var result = _selector.Select(candidates, Policy(max: 2, distinctProviders: false));

        Assert.Equal(2, result.Selected.Count);
        Assert.All(result.Selected, s => Assert.Equal("prov-a", s.Candidate.Provider.Key));
    }

    // ---------------- provider limits ----------------

    [Fact]
    public void MaxPerProvider_1_limits_each_provider()
    {
        var result = _selector.Select(Many(9, 3), Policy(max: 10, maxPerProvider: 1));

        Assert.Equal(3, result.Selected.Count);
        Assert.All(
            result.Selected.GroupBy(s => s.Candidate.Provider.Key),
            g => Assert.Single(g));
    }

    [Fact]
    public void MaxPerProvider_2_allows_two_each()
    {
        var result = _selector.Select(Many(9, 3), Policy(max: 10, maxPerProvider: 2));

        Assert.Equal(6, result.Selected.Count);
        Assert.All(
            result.Selected.GroupBy(s => s.Candidate.Provider.Key),
            g => Assert.Equal(2, g.Count()));
    }

    [Fact]
    public void MaxPerProvider_greater_than_available_selects_all()
    {
        var result = _selector.Select(Many(4, 2), Policy(max: 10, maxPerProvider: 5));

        Assert.Equal(4, result.Selected.Count);
    }

    [Fact]
    public void Channel_limit_is_absolute_when_smaller_than_provider_limit()
    {
        var candidates = Enumerable.Range(1, 10)
            .Select(i => Candidate($"http://one.example/{i}.ts", i, "prov-a"))
            .ToList();

        var result = _selector.Select(
            candidates,
            Policy(max: 2, distinctProviders: true, maxPerProvider: 5, allowFallback: true));

        Assert.Equal(2, result.Selected.Count);
        Assert.Equal(8, result.Rejected.Count);
        Assert.All(result.Rejected, r => Assert.Equal(SelectionReasons.LimitReached, r.Reason));
    }

    // ---------------- fallback ----------------

    [Fact]
    public void AllowFallback_true_fills_remaining_slots()
    {
        var result = _selector.Select(Many(3, 1), Policy(max: 3, allowFallback: true));

        Assert.Equal(3, result.Selected.Count);
    }

    [Fact]
    public void AllowFallback_false_does_not_repeat_provider()
    {
        var result = _selector.Select(Many(6, 2), Policy(max: 5, allowFallback: false));

        Assert.Equal(2, result.Selected.Count);
        Assert.Equal(2, result.Selected.Select(s => s.Candidate.Provider.Key).Distinct().Count());
    }

    // ---------------- edge cases ----------------

    [Fact]
    public void Empty_set_returns_empty()
    {
        var result = _selector.Select(Array.Empty<SelectionCandidate>(), Policy());

        Assert.Empty(result.Selected);
        Assert.Empty(result.Rejected);
        Assert.Equal(0, result.TotalCandidates);
    }

    [Fact]
    public void Single_candidate_is_selected()
    {
        var result = _selector.Select(
            new[] { Candidate("http://only.example/x.ts", 1, "prov-a") }, Policy());

        Assert.Single(result.Selected);
        Assert.Equal(0, result.Selected[0].Rank);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://host/x.ts")]
    public void Invalid_or_empty_url_is_rejected(string url)
    {
        var result = _selector.Select(
            new[] { Candidate(url, 1, "prov-a") }, Policy());

        Assert.Empty(result.Selected);
        Assert.Equal(SelectionReasons.InvalidUrl, result.Rejected.Single().Reason);
    }

    [Fact]
    public void Not_working_candidate_is_rejected()
    {
        var result = _selector.Select(
            new[] { Candidate("http://x.example/a.ts", 1, "prov-a", isWorking: false) }, Policy());

        Assert.Empty(result.Selected);
        Assert.Equal(SelectionReasons.NotWorking, result.Rejected.Single().Reason);
    }

    [Theory]
    [InlineData(AvailabilityState.Dead)]
    [InlineData(AvailabilityState.Unreachable)]
    public void Terminal_availability_is_rejected(AvailabilityState state)
    {
        var result = _selector.Select(
            new[] { Candidate("http://x.example/a.ts", 1, "prov-a", availability: state) }, Policy());

        Assert.Empty(result.Selected);
        Assert.Equal(SelectionReasons.Unavailable, result.Rejected.Single().Reason);
    }

    [Fact]
    public void Unknown_availability_is_eligible()
    {
        var result = _selector.Select(
            new[] { Candidate("http://x.example/a.ts", 1, "prov-a", availability: AvailabilityState.Discovered) },
            Policy(max: 1));

        Assert.Single(result.Selected);
    }

    [Fact]
    public void Unknown_quality_and_epg_rank_last_but_are_eligible()
    {
        var candidates = new List<SelectionCandidate>
        {
            Candidate("http://unknown.example/a.ts", 1, "prov-a"),
            Candidate("http://fhd.example/a.ts", 2, "prov-a", quality: StreamQuality.FHD, epg: EpgState.Available),
        };

        var result = _selector.Select(candidates, Policy(max: 1));

        Assert.Equal("http://fhd.example/a.ts", result.Selected.Single().Candidate.StreamUrl);
        Assert.Equal(SelectionReasons.LimitReached, result.Rejected.Single().Reason);
    }

    [Fact]
    public void Total_tie_is_broken_deterministically_by_url()
    {
        var candidates = new List<SelectionCandidate>
        {
            Candidate("http://b.example/x.ts", 1, "prov-a"),
            Candidate("http://a.example/x.ts", 2, "prov-a"),
        };

        var result = _selector.Select(candidates, Policy(max: 1));

        Assert.Equal("http://a.example/x.ts", result.Selected.Single().Candidate.StreamUrl);
    }

    // ---------------- ranking ----------------

    [Fact]
    public void Validated_availability_beats_reachable()
    {
        var candidates = new List<SelectionCandidate>
        {
            Candidate("http://r.example/x.ts", 1, "prov-a", availability: AvailabilityState.Reachable),
            Candidate("http://v.example/x.ts", 2, "prov-a", availability: AvailabilityState.Validated),
        };

        var result = _selector.Select(candidates, Policy(max: 1));

        Assert.Equal("http://v.example/x.ts", result.Selected.Single().Candidate.StreamUrl);
    }

    [Fact]
    public void Higher_source_priority_wins()
    {
        var candidates = new List<SelectionCandidate>
        {
            Candidate("http://low.example/x.ts", 1, "prov-a", priority: 1),
            Candidate("http://high.example/x.ts", 2, "prov-a", priority: 9),
        };

        var result = _selector.Select(candidates, Policy(max: 1));

        Assert.Equal("http://high.example/x.ts", result.Selected.Single().Candidate.StreamUrl);
    }

    [Fact]
    public void Higher_quality_wins()
    {
        var candidates = new List<SelectionCandidate>
        {
            Candidate("http://hd.example/x.ts", 1, "prov-a", quality: StreamQuality.HD),
            Candidate("http://4k.example/x.ts", 2, "prov-a", quality: StreamQuality.FourK),
        };

        var result = _selector.Select(candidates, Policy(max: 1));

        Assert.Equal("http://4k.example/x.ts", result.Selected.Single().Candidate.StreamUrl);
    }

    [Fact]
    public void Lower_response_time_wins_and_unknown_is_last()
    {
        var candidates = new List<SelectionCandidate>
        {
            Candidate("http://unknown-rt.example/x.ts", 1, "prov-a", responseTimeMs: 0),
            Candidate("http://slow.example/x.ts", 2, "prov-a", responseTimeMs: 900),
            Candidate("http://fast.example/x.ts", 3, "prov-a", responseTimeMs: 120),
        };

        var ordered = new[]
        {
            _selector.Select(candidates, Policy(max: 3)).Selected[0].Candidate.StreamUrl,
            _selector.Select(candidates, Policy(max: 3)).Selected[1].Candidate.StreamUrl,
            _selector.Select(candidates, Policy(max: 3)).Selected[2].Candidate.StreamUrl,
        };

        Assert.Equal(new[]
        {
            "http://fast.example/x.ts",
            "http://slow.example/x.ts",
            "http://unknown-rt.example/x.ts",
        }, ordered);
    }

    [Fact]
    public void Epg_available_wins()
    {
        var candidates = new List<SelectionCandidate>
        {
            Candidate("http://no-epg.example/x.ts", 1, "prov-a", epg: EpgState.Unavailable),
            Candidate("http://epg.example/x.ts", 2, "prov-a", epg: EpgState.Available),
        };

        var result = _selector.Select(candidates, Policy(max: 1));

        Assert.Equal("http://epg.example/x.ts", result.Selected.Single().Candidate.StreamUrl);
    }

    // ---------------- reasons ----------------

    [Fact]
    public void Rejection_reasons_are_observable()
    {
        // limit-reached
        var limited = _selector.Select(Many(3, 3), Policy(max: 1));
        Assert.Contains(limited.Rejected, r => r.Reason == SelectionReasons.LimitReached);

        // provider-limit
        var providerLimited = _selector.Select(
            new[]
            {
                Candidate("http://p1.example/x.ts", 1, "prov-a"),
                Candidate("http://p2.example/x.ts", 2, "prov-a"),
            },
            Policy(max: 10, maxPerProvider: 1));
        Assert.Contains(providerLimited.Rejected, r => r.Reason == SelectionReasons.ProviderLimit);

        // fallback-disabled
        var noFallback = _selector.Select(
            new[]
            {
                Candidate("http://f1.example/x.ts", 1, "prov-a"),
                Candidate("http://f2.example/x.ts", 2, "prov-a"),
            },
            Policy(max: 10, allowFallback: false));
        Assert.Contains(noFallback.Rejected, r => r.Reason == SelectionReasons.FallbackDisabled);

        // duplicate-url + invalid-url
        var mixed = _selector.Select(
            new[]
            {
                Candidate("http://dup.example/x.ts", 1, "prov-a"),
                Candidate("http://dup.example/x.ts", 2, "prov-b"),
                Candidate("not-a-url", 3, "prov-c"),
            },
            Policy(max: 10));
        Assert.Contains(mixed.Rejected, r => r.Reason == SelectionReasons.DuplicateUrl);
        Assert.Contains(mixed.Rejected, r => r.Reason == SelectionReasons.InvalidUrl);
    }

    [Fact]
    public void Limit_reached_takes_precedence_over_provider_reasons()
    {
        // Max=1 com 2 providers × 2 candidatos: o limite do canal domina
        // mesmo para candidatos que também seriam provider/fallback-limited.
        var candidates = new[]
        {
            Candidate("http://a1.example/x.ts", 1, "prov-a"),
            Candidate("http://a2.example/x.ts", 2, "prov-a"),
            Candidate("http://b1.example/x.ts", 3, "prov-b"),
            Candidate("http://b2.example/x.ts", 4, "prov-b"),
        };

        var result = _selector.Select(candidates, Policy(max: 1, maxPerProvider: 1, allowFallback: false));

        Assert.Single(result.Selected);
        Assert.All(result.Rejected, r => Assert.Equal(SelectionReasons.LimitReached, r.Reason));
    }

    [Fact]
    public void Provider_limit_takes_precedence_over_fallback_disabled()
    {
        var candidates = new[]
        {
            Candidate("http://p1.example/x.ts", 1, "prov-a"),
            Candidate("http://p2.example/x.ts", 2, "prov-a"),
        };

        var result = _selector.Select(candidates, Policy(max: 10, maxPerProvider: 1, allowFallback: false));

        Assert.Single(result.Selected);
        Assert.Equal(SelectionReasons.ProviderLimit, result.Rejected.Single().Reason);
    }

    [Fact]
    public void Not_selected_reason_is_never_emitted()
    {
        var scenarios = new[]
        {
            _selector.Select(Many(20, 3), Policy(max: 5, maxPerProvider: 2)),
            _selector.Select(Many(20, 3), Policy(max: 5, maxPerProvider: 1, allowFallback: false)),
            _selector.Select(Many(20, 1), Policy(max: 5, allowFallback: false)),
            _selector.Select(
                new[]
                {
                    Candidate("http://dup.example/x.ts", 1, "prov-a"),
                    Candidate("http://dup.example/x.ts", 2, "prov-b"),
                    Candidate("not-a-url", 3, "prov-c"),
                    Candidate("http://off.example/x.ts", 4, "prov-d", isWorking: false),
                    Candidate("http://dead.example/x.ts", 5, "prov-e", availability: AvailabilityState.Dead),
                },
                Policy(max: 2)),
        };

        Assert.All(scenarios, s =>
            Assert.DoesNotContain(s.Rejected, r => r.Reason == SelectionReasons.NotSelected));
    }

    // ---------------- determinism ----------------

    [Fact]
    public void Same_input_produces_identical_result()
    {
        var candidates = Many(50, 12);
        var policy = Policy(max: 10, maxPerProvider: 2);

        var first = _selector.Select(candidates, policy);
        var second = _selector.Select(candidates, policy);

        Assert.Equal(Signature(first), Signature(second));
    }

    [Fact]
    public void Selection_is_identical_across_permutations()
    {
        var candidates = Many(30, 9);
        var policy = Policy(max: 8, maxPerProvider: 2);

        var signatures = Permutations(candidates)
            .Select(order => Signature(_selector.Select(order, policy)))
            .Distinct()
            .ToList();

        Assert.Single(signatures);
    }

    [Fact]
    public void Tied_candidates_are_deterministic_across_input_orders()
    {
        // Cada par empata em TODAS as chaves de ranking actuais: a URL difere
        // só onde `NormalizeUrl` colapsa (scheme/host), e provider, SourceId,
        // ExternalStreamId, prioridade, qualidade, EPG, disponibilidade e
        // response time são iguais. Sem o tie-break total (R1), a dedup e a
        // ordem dos rejeitados dependeriam da ordem de entrada.
        var all = new List<SelectionCandidate>
        {
            Candidate("http://tie-a.example/x", 1, "prov-x"),
            Candidate("HTTP://TIE-A.EXAMPLE/x", 1, "prov-x"),
            Candidate("http://tie-b.example/x", 2, "prov-y"),
            Candidate("HTTP://TIE-B.EXAMPLE/x", 2, "prov-y"),
            Candidate("http://tie-c.example/x", 3, "prov-z"),
            Candidate("HTTP://TIE-C.EXAMPLE/x", 3, "prov-z"),
        };
        var policy = Policy(max: 3, distinctProviders: true, allowFallback: true);

        var signatures = Permutations(all)
            .Select(order => Signature(_selector.Select(order, policy)))
            .Distinct()
            .ToList();

        Assert.Single(signatures);
    }

    [Fact]
    public void Invalid_policy_is_rejected()
    {
        // Wave 13-4 — 0 é um contrato válido (não publicar); só negativos são inválidos.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _selector.Select(Array.Empty<SelectionCandidate>(), Policy(max: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _selector.Select(Array.Empty<SelectionCandidate>(), Policy(max: 10, maxPerProvider: 0)));
    }

    [Fact]
    public void Zero_max_sources_selects_none()
    {
        var candidates = Many(5, 2);

        var result = _selector.Select(candidates, Policy(max: 0));

        Assert.Empty(result.Selected);
        Assert.Equal(candidates.Count, result.Rejected.Count);
        Assert.All(result.Rejected, r => Assert.Equal(SelectionReasons.LimitReached, r.Reason));
    }
}
