using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.SourceSelection;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 13 (Wave 13-3) — Testes do <see cref="SourceSelectionStage"/>:
/// junção exacta catálogo↔pipeline, projecção, selecção, pass-through e
/// segurança de credenciais. Sem rede; SQLite isolado por teste.
/// </summary>
public class SourceSelectionStageTests : IAsyncLifetime
{
    private const string XtreamUrl = "http://user:password@example.test/live/user/password/123";

    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;
    private CanonicalChannelEntity _channelA = null!;
    private CanonicalChannelEntity _channelB = null!;

    public SourceSelectionStageTests()
    {
        _dbPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"source-selection-stage-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        _resolver = new CatalogResolver(_factory, _dbPath);

        var channels = await _resolver.ListCanonicalChannelsAsync();
        Assert.True(channels.Count >= 2, "seed canónico deve ter pelo menos 2 canais");
        _channelA = channels[0];
        _channelB = channels[1];
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------------- helpers ----------------

    private static M3uStream Stream(
        string url,
        string title = "Canal",
        double responseTime = 0,
        bool isWorking = true,
        string group = "PT")
        => new()
        {
            Url = url,
            Title = title,
            Group = group,
            IsWorking = isWorking,
            ResponseTime = responseTime,
        };

    private static SourceSelectionPolicy Policy(
        int max = 10,
        bool distinctProviders = true,
        int? maxPerProvider = null,
        bool allowFallback = true)
        => new(max, distinctProviders, maxPerProvider, allowFallback);

    private async Task<SourceEntity> NewSourceAsync(string key = "src-test")
        => await _resolver.EnsureSourceAsync(key, key, SourceKind.Telegram, $"telegram://{key}", 0);

    private async Task RecordAsync(
        CanonicalChannelEntity channel,
        SourceEntity source,
        string realUrl,
        bool isEnabled = true,
        string method = "test")
        => await _resolver.RecordChannelSourceAsync(
            channel.Id, source.Id, realUrl, matchMethod: method, isEnabled: isEnabled);

    private static string[] PublishedUrls(SourceSelectionStageResult r)
        => r.Published.Select(s => s.Url).ToArray();

    // ---------------- exact join / projection ----------------

    [Fact]
    public async Task Exact_sanitized_url_match_selects_the_stream()
    {
        var source = await NewSourceAsync();
        await RecordAsync(_channelA, source, "http://provider.example/a.ts");
        var input = Stream("http://provider.example/a.ts", responseTime: 120);

        var result = await new SourceSelectionStage(_resolver)
            .ApplyAsync(new[] { input }, Policy());

        Assert.True(result.Applied);
        Assert.Single(result.Selected);
        Assert.Equal(1, result.MatchedChannelCount);
        Assert.Equal(0, result.AmbiguousCount);
        // Preserva a instância original (URL real, não reconstruída).
        Assert.Same(input, result.Published.Single());
    }

    [Fact]
    public async Task Real_xtream_url_is_preserved_and_never_sanitized_in_publication()
    {
        var source = await NewSourceAsync();
        await RecordAsync(_channelA, source, XtreamUrl);
        var input = Stream(XtreamUrl, responseTime: 50);

        var result = await new SourceSelectionStage(_resolver)
            .ApplyAsync(new[] { input }, Policy());

        Assert.Single(result.Selected);
        Assert.Equal(XtreamUrl, result.Published.Single().Url);
        Assert.DoesNotContain("***", result.Published.Single().Url);
        Assert.Equal(source.Id, result.Selected[0].Candidate.SourceId);
    }

    [Fact]
    public async Task Runtime_metrics_reach_the_candidate()
    {
        var source = await NewSourceAsync();
        await RecordAsync(_channelA, source, "http://provider.example/a.ts");
        var input = Stream("http://provider.example/a.ts", responseTime: 321);

        var result = await new SourceSelectionStage(_resolver)
            .ApplyAsync(new[] { input }, Policy());

        var candidate = result.Selected.Single().Candidate;
        Assert.Equal(321, candidate.LastResponseTimeMs);
        Assert.Equal(AvailabilityState.Reachable, candidate.Availability);
        Assert.True(candidate.IsWorking);
        Assert.Equal(StreamQuality.Unknown, candidate.Quality);
        Assert.Equal(EpgState.Unknown, candidate.Epg);
        Assert.Equal(0, candidate.SourcePriority);
    }

    [Fact]
    public async Task Non_working_stream_maps_to_dead_and_is_not_published()
    {
        var source = await NewSourceAsync();
        await RecordAsync(_channelA, source, "http://provider.example/a.ts");
        var input = Stream("http://provider.example/a.ts", isWorking: false);

        var result = await new SourceSelectionStage(_resolver)
            .ApplyAsync(new[] { input }, Policy());

        Assert.Empty(result.Selected);
        Assert.Equal(AvailabilityState.Dead, result.Rejected.Single().Candidate.Availability);
        Assert.DoesNotContain(input, result.Published);
    }

    [Theory]
    [InlineData("http://Example.COM/x", "example.com")]
    [InlineData("http://www.Example.com/x", "example.com")]
    [InlineData("http://example.com./x", "example.com")]
    [InlineData("http://example.com:8080/x", "example.com")]
    [InlineData("http://192.168.0.1/x", "192.168.0.1")]
    public void Provider_host_is_normalized(string url, string expected)
    {
        Assert.Equal(expected, SourceSelectionStage.NormalizeProviderHost(url));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("")]
    public void Provider_host_is_null_for_invalid_url(string url)
    {
        Assert.Null(SourceSelectionStage.NormalizeProviderHost(url));
    }

    [Fact]
    public async Task Provider_identity_is_derived_from_host()
    {
        var source = await NewSourceAsync();
        await RecordAsync(_channelA, source, "http://www.Provider.Example:8080/a.ts");
        var input = Stream("http://www.Provider.Example:8080/a.ts");

        var result = await new SourceSelectionStage(_resolver)
            .ApplyAsync(new[] { input }, Policy());

        Assert.Equal("provider.example", result.Selected.Single().Candidate.Provider.Key);
    }

    // ---------------- matching ----------------

    [Fact]
    public async Task Zero_matches_passes_through()
    {
        var source = await NewSourceAsync();
        await RecordAsync(_channelA, source, "http://provider.example/known.ts");
        var unmatched = Stream("http://provider.example/unknown.ts");

        var result = await new SourceSelectionStage(_resolver)
            .ApplyAsync(new[] { unmatched }, Policy());

        Assert.True(result.Applied);
        Assert.Empty(result.Selected);
        Assert.Single(result.Unmatched);
        Assert.Same(unmatched, result.Published.Single());
    }

    [Fact]
    public async Task Ambiguous_mapping_passes_through()
    {
        var source = await NewSourceAsync();
        await RecordAsync(_channelA, source, "http://provider.example/shared.ts");
        await RecordAsync(_channelB, source, "http://provider.example/shared.ts");
        var input = Stream("http://provider.example/shared.ts");

        var result = await new SourceSelectionStage(_resolver)
            .ApplyAsync(new[] { input }, Policy());

        Assert.True(result.Applied);
        Assert.Empty(result.Selected);
        Assert.Single(result.Unmatched);
        Assert.Equal(1, result.AmbiguousCount);
        Assert.Same(input, result.Published.Single());
    }

    [Fact]
    public async Task Missing_catalog_is_a_noop()
    {
        var a = Stream("http://provider.example/a.ts");
        var b = Stream("http://provider.example/b.ts");

        var result = await new SourceSelectionStage(catalog: null)
            .ApplyAsync(new[] { a, b }, Policy());

        Assert.False(result.Applied);
        Assert.Equal(new[] { a, b }, result.Published);
        Assert.Empty(result.Selected);
    }

    [Fact]
    public async Task Empty_catalog_is_a_noop()
    {
        var a = Stream("http://provider.example/a.ts");

        var result = await new SourceSelectionStage(_resolver)
            .ApplyAsync(new[] { a }, Policy());

        Assert.False(result.Applied);
        Assert.Same(a, result.Published.Single());
    }

    // ---------------- disabled ----------------

    [Fact]
    public async Task Disabled_channel_source_is_rejected_and_not_published()
    {
        var source = await NewSourceAsync();
        await RecordAsync(_channelA, source, "http://provider.example/a.ts", isEnabled: false);
        var input = Stream("http://provider.example/a.ts");

        var result = await new SourceSelectionStage(_resolver)
            .ApplyAsync(new[] { input }, Policy());

        Assert.Empty(result.Selected);
        Assert.Equal(SourceSelectionStage.SourceDisabledReason, result.Rejected.Single().Reason);
        Assert.DoesNotContain(input, result.Published);
    }

    // ---------------- selection ----------------

    [Fact]
    public async Task Channel_limit_is_enforced_across_providers()
    {
        var source = await NewSourceAsync();
        for (var i = 0; i < 5; i++)
        {
            await RecordAsync(_channelA, source, $"http://host{i}.example/s.ts");
        }

        var inputs = Enumerable.Range(0, 5)
            .Select(i => Stream($"http://host{i}.example/s.ts", responseTime: 100 + i))
            .ToArray();

        var result = await new SourceSelectionStage(_resolver)
            .ApplyAsync(inputs, Policy(max: 2));

        Assert.Equal(2, result.Selected.Count);
        Assert.Equal(3, result.Rejected.Count);
        Assert.All(result.Rejected, r => Assert.Equal(SelectionReasons.LimitReached, r.Reason));
    }

    [Fact]
    public async Task Prefer_distinct_providers_is_applied_when_enabled()
    {
        var source = await NewSourceAsync();
        await RecordAsync(_channelA, source, "http://alpha.example/1.ts");
        await RecordAsync(_channelA, source, "http://alpha.example/2.ts");
        await RecordAsync(_channelA, source, "http://beta.example/1.ts");

        var inputs = new[]
        {
            Stream("http://alpha.example/1.ts", responseTime: 10),
            Stream("http://alpha.example/2.ts", responseTime: 20),
            Stream("http://beta.example/1.ts", responseTime: 900),
        };

        var result = await new SourceSelectionStage(_resolver)
            .ApplyAsync(inputs, Policy(max: 2, distinctProviders: true));

        Assert.Equal(2, result.Selected.Count);
        Assert.Equal(
            2,
            result.Selected.Select(s => s.Candidate.Provider.Key).Distinct().Count());
        Assert.Contains(result.Selected, s => s.Candidate.Provider.Key == "beta.example");
    }

    [Fact]
    public async Task Fallback_disabled_limits_each_provider_to_one()
    {
        var source = await NewSourceAsync();
        await RecordAsync(_channelA, source, "http://alpha.example/1.ts");
        await RecordAsync(_channelA, source, "http://alpha.example/2.ts");

        var inputs = new[]
        {
            Stream("http://alpha.example/1.ts", responseTime: 10),
            Stream("http://alpha.example/2.ts", responseTime: 20),
        };

        var result = await new SourceSelectionStage(_resolver)
            .ApplyAsync(inputs, Policy(max: 10, allowFallback: false));

        Assert.Single(result.Selected);
        Assert.Equal(SelectionReasons.FallbackDisabled, result.Rejected.Single().Reason);
    }

    [Fact]
    public async Task Duplicate_urls_are_deduplicated()
    {
        var source = await NewSourceAsync();
        await RecordAsync(_channelA, source, "http://provider.example/a.ts");

        var first = Stream("http://provider.example/a.ts", responseTime: 10);
        var second = Stream("http://provider.example/a.ts", responseTime: 20);

        var result = await new SourceSelectionStage(_resolver)
            .ApplyAsync(new[] { first, second }, Policy());

        Assert.Single(result.Selected);
        Assert.Equal(SelectionReasons.DuplicateUrl, result.Rejected.Single().Reason);
    }

    // ---------------- publication ----------------

    [Fact]
    public async Task Published_is_selected_in_rank_order_then_unmatched_in_input_order()
    {
        var source = await NewSourceAsync();
        await RecordAsync(_channelA, source, "http://alpha.example/slow.ts");
        await RecordAsync(_channelA, source, "http://beta.example/fast.ts");

        var slow = Stream("http://alpha.example/slow.ts", responseTime: 900);
        var unmatched1 = Stream("http://other.example/u1.ts");
        var fast = Stream("http://beta.example/fast.ts", responseTime: 50);
        var unmatched2 = Stream("http://other.example/u2.ts");

        var result = await new SourceSelectionStage(_resolver)
            .ApplyAsync(new[] { slow, unmatched1, fast, unmatched2 }, Policy(max: 2));

        Assert.Equal(
            new[] { fast.Url, slow.Url, unmatched1.Url, unmatched2.Url },
            PublishedUrls(result));
    }

    [Fact]
    public async Task Matched_selection_is_deterministic_across_permutations()
    {
        var source = await NewSourceAsync();
        for (var i = 0; i < 6; i++)
        {
            await RecordAsync(_channelA, source, $"http://host{i}.example/s.ts");
        }

        var inputs = Enumerable.Range(0, 6)
            .Select(i => Stream($"http://host{i}.example/s.ts", responseTime: 100 + i))
            .ToList();

        var policy = Policy(max: 3);
        string Signature(IReadOnlyList<M3uStream> list)
        {
            var result = new SourceSelectionStage(_resolver).ApplyAsync(list, policy).GetAwaiter().GetResult();
            return string.Join("|", result.Selected.Select(s => $"{s.Rank}:{s.Candidate.StreamUrl}"));
        }

        var reference = Signature(inputs);
        Assert.Equal(reference, Signature(inputs.AsEnumerable().Reverse().ToList()));
        Assert.Equal(reference, Signature(inputs.Skip(2).Concat(inputs.Take(2)).ToList()));
    }

    // ---------------- security ----------------

    [Fact]
    public async Task Diagnostics_report_contains_only_aggregate_counts()
    {
        var source = await NewSourceAsync();
        await RecordAsync(_channelA, source, XtreamUrl);
        await RecordAsync(_channelA, source, "http://limit-b.example/x.ts");

        var inputs = new[]
        {
            Stream(XtreamUrl, responseTime: 10),
            Stream("http://limit-b.example/x.ts", responseTime: 20),
            Stream("http://unmatched-1.example/x.ts", responseTime: 30),
            Stream("http://unmatched-2.example/x.ts", responseTime: 40),
        };

        var result = await new SourceSelectionStage(_resolver)
            .ApplyAsync(inputs, Policy(max: 1));

        var report = result.ToReport();
        Assert.True(report.Applied);
        Assert.Equal(1, report.SelectedCount);
        Assert.Equal(1, report.RejectedCount);
        Assert.Equal(2, report.UnmatchedCount);
        Assert.Equal(1, report.MatchedChannelCount);

        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.test", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_published_url_contains_sanitization_markers()
    {
        var source = await NewSourceAsync();
        await RecordAsync(_channelA, source, XtreamUrl);

        var result = await new SourceSelectionStage(_resolver)
            .ApplyAsync(new[] { Stream(XtreamUrl) }, Policy());

        Assert.All(result.Published, s => Assert.DoesNotContain("***", s.Url));
        Assert.All(result.Published, s => Assert.Equal(XtreamUrl, s.Url));
    }
}
