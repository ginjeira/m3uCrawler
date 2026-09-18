using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.SourceSelection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 13 (Wave 13-5) — Testes determinísticos do
/// <see cref="SourceSelectionPreviewService"/>: preview/dry-run estritamente
/// read-only, paridade com o <see cref="SourceSelectionStage"/> real,
/// métricas agregadas, âmbito de política efectivo, filtro por canal,
/// fontes desactivadas e sanitização de credenciais. Sem rede; SQLite
/// isolado por teste.
/// </summary>
public class SourceSelectionPreviewTests : IAsyncLifetime
{
    private const string XtreamUrl = "http://user:secret@host.example.test/live/USER/PASS/1";

    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;
    private CanonicalChannelEntity _channelA = null!;
    private CanonicalChannelEntity _channelB = null!;

    public SourceSelectionPreviewTests()
    {
        _dbPath = TestTempDb.SuitePath($"source-selection-preview-{Guid.NewGuid():N}.db");
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

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        return Task.CompletedTask;
    }

    // ---------------- helpers ----------------

    private SourceSelectionPreviewService Preview() => new(_resolver);

    private static SourceSelectionPolicy Policy(
        int max = 10,
        bool distinctProviders = true,
        int? maxPerProvider = null,
        bool allowFallback = true)
        => new(max, distinctProviders, maxPerProvider, allowFallback);

    private async Task<SourceEntity> NewSourceAsync(string key)
        => await _resolver.EnsureSourceAsync(key, key, SourceKind.Telegram, $"telegram://{key}", 0);

    private async Task RecordAsync(
        CanonicalChannelEntity channel,
        SourceEntity source,
        string realUrl,
        bool isEnabled = true)
        => await _resolver.RecordChannelSourceAsync(
            channel.Id, source.Id, realUrl, matchMethod: "test", isEnabled: isEnabled);

    private async Task<int> PolicyRowCountAsync()
    {
        await using var context = _factory.CreateDbContext();
        return await context.SourceSelectionPolicies.CountAsync();
    }

    /// <summary>
    /// Reproduz a síntese de stream do preview (uma por ChannelSource) para
    /// comparar decisões com o stage directo usando exactamente os mesmos
    /// inputs.
    /// </summary>
    private static M3uStream ToStream(ChannelSourceEntity channelSource) => new()
    {
        Url = channelSource.StreamUrl,
        Title = channelSource.CanonicalChannel?.DisplayName ?? string.Empty,
        IsWorking = channelSource.Availability
            is not AvailabilityState.Dead and not AvailabilityState.Unreachable,
        ResponseTime = channelSource.LastResponseTimeMs,
    };

    // ---------------- read-only / no mutation ----------------

    [Fact]
    public async Task Preview_does_not_create_a_global_policy_row_or_mutate_the_catalog()
    {
        var source = await NewSourceAsync("preview-no-mutation");
        await RecordAsync(_channelA, source, "http://no-mutation.example.test/1.ts");

        Assert.Null(await _resolver.GetGlobalSourceSelectionPolicyAsync());
        var overridesBefore = await _resolver.ListChannelSourceSelectionPoliciesAsync();
        var channelSourcesBefore = (await _resolver.ListChannelSourcesAsync()).Count;
        Assert.Equal(0, await PolicyRowCountAsync());

        var preview = await Preview().PreviewAsync();

        Assert.Null(await _resolver.GetGlobalSourceSelectionPolicyAsync());
        Assert.Equal(
            overridesBefore.Count,
            (await _resolver.ListChannelSourceSelectionPoliciesAsync()).Count);
        Assert.Equal(
            channelSourcesBefore,
            (await _resolver.ListChannelSourcesAsync()).Count);
        Assert.Equal(0, await PolicyRowCountAsync());
        Assert.Equal("default", preview.Channels.Single().PolicyScope);
    }

    // ---------------- parity with the real stage ----------------

    [Fact]
    public async Task Preview_decisions_match_a_direct_stage_run_on_the_same_streams()
    {
        var source = await NewSourceAsync("preview-parity");
        await RecordAsync(_channelA, source, "http://parity-a1.example.test/1.ts");
        await RecordAsync(_channelA, source, "http://parity-a2.example.test/1.ts");
        await RecordAsync(_channelA, source, "http://parity-a3.example.test/1.ts");
        await RecordAsync(_channelA, source, "http://parity-a4.example.test/1.ts", isEnabled: false);
        await RecordAsync(_channelB, source, "http://parity-b1.example.test/1.ts");
        await RecordAsync(_channelB, source, "http://parity-b2.example.test/1.ts");

        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(2, true, null, true);
        await _resolver.UpsertChannelSourceSelectionPolicyAsync(_channelA.Key, 3, false, 2, false);

        var preview = await Preview().PreviewAsync();

        var channelSources = await _resolver.ListChannelSourcesAsync();
        var streams = channelSources.Select(ToStream).ToList();
        var policies = await new SourceSelectionPolicyResolver(_resolver)
            .LoadEffectivePoliciesReadOnlyAsync();
        var direct = await new SourceSelectionStage(_resolver).ApplyAsync(streams, policies);

        Assert.Equal(direct.Applied, preview.Applied);
        Assert.Equal(direct.Channels.Count, preview.Channels.Count);

        foreach (var directChannel in direct.Channels)
        {
            var previewChannel = preview.Channels.Single(
                c => c.CanonicalChannelId == directChannel.CanonicalChannelId);

            Assert.Equal(directChannel.Selected.Count, previewChannel.SelectedCount);
            Assert.Equal(directChannel.Rejected.Count, previewChannel.RejectedCount);
            Assert.Equal(directChannel.Policy, previewChannel.Policy);

            Assert.Equal(
                directChannel.Selected.Select(s => (
                    s.Rank,
                    s.Reason,
                    CredentialSanitizer.SanitizeUrl(s.Candidate.StreamUrl))),
                previewChannel.Selected.Select(c => (
                    c.Rank!.Value,
                    c.Reason,
                    c.StreamUrlSanitized)));

            Assert.Equal(
                directChannel.Rejected
                    .Select(r => (Reason: r.Reason, Url: CredentialSanitizer.SanitizeUrl(r.Candidate.StreamUrl)))
                    .OrderBy(x => x.Reason, StringComparer.Ordinal)
                    .ThenBy(x => x.Url, StringComparer.Ordinal),
                previewChannel.Rejected
                    .Select(c => (Reason: c.Reason, Url: c.StreamUrlSanitized))
                    .OrderBy(x => x.Reason, StringComparer.Ordinal)
                    .ThenBy(x => x.Url, StringComparer.Ordinal));
        }
    }

    // ---------------- selection limits / metrics ----------------

    [Fact]
    public async Task Channel_limit_selects_ten_of_twelve_and_reports_limit_metrics()
    {
        var source = await NewSourceAsync("preview-channel-limit");
        for (var i = 0; i < 12; i++)
        {
            await RecordAsync(_channelA, source, $"http://sel-limit-{i}.example.test/s.ts");
        }
        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(10, true, null, true);

        var preview = await Preview().PreviewAsync();

        var channel = Assert.Single(preview.Channels);
        Assert.Equal(10, channel.SelectedCount);
        Assert.Equal(2, channel.RejectedCount);
        Assert.All(channel.Rejected, r => Assert.Equal(SelectionReasons.LimitReached, r.Reason));
        Assert.Equal(1, preview.Metrics.ChannelsAtChannelLimit);
        Assert.Equal(2, preview.Metrics.ChannelLimitRejectionCount);
        Assert.Equal(10, preview.Metrics.SelectedStreamCount);
        Assert.Equal(2, preview.Metrics.RejectedStreamCount);
    }

    [Fact]
    public async Task Provider_limit_rejects_with_provider_limit_reason_and_counts_it()
    {
        var source = await NewSourceAsync("preview-provider-limit");
        for (var i = 0; i < 5; i++)
        {
            await RecordAsync(_channelA, source, $"http://sel-provider-limit.example.test/{i}.ts");
        }
        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(10, true, 2, true);

        var preview = await Preview().PreviewAsync();

        var channel = Assert.Single(preview.Channels);
        Assert.Equal(2, channel.SelectedCount);
        Assert.Equal(3, channel.RejectedCount);
        Assert.All(channel.Rejected, r => Assert.Equal(SelectionReasons.ProviderLimit, r.Reason));
        Assert.True(preview.Metrics.ProviderLimitRejectionCount > 0);
        Assert.Equal(3, preview.Metrics.ProviderLimitRejectionCount);
    }

    [Fact]
    public async Task Prefer_distinct_providers_emits_diversity_and_counts_distinct_selections()
    {
        var source = await NewSourceAsync("preview-diversity");
        for (var i = 0; i < 4; i++)
        {
            await RecordAsync(_channelA, source, $"http://sel-diversity-{i}.example.test/s.ts");
        }

        var preview = await Preview().PreviewAsync();

        var channel = Assert.Single(preview.Channels);
        Assert.Equal(4, channel.SelectedCount);
        Assert.All(channel.Selected, s => Assert.Equal(SelectionReasons.Diversity, s.Reason));
        Assert.True(preview.Metrics.DistinctProviderSelectionCount > 0);
        Assert.Equal(4, preview.Metrics.DistinctProviderSelectionCount);
    }

    [Fact]
    public async Task Fallback_disabled_selects_two_of_four_and_rejects_fallback_disabled()
    {
        var source = await NewSourceAsync("preview-fallback-disabled");
        await RecordAsync(_channelA, source, "http://sel-fb-alpha.example.test/1.ts");
        await RecordAsync(_channelA, source, "http://sel-fb-alpha.example.test/2.ts");
        await RecordAsync(_channelA, source, "http://sel-fb-beta.example.test/1.ts");
        await RecordAsync(_channelA, source, "http://sel-fb-beta.example.test/2.ts");
        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(3, true, null, false);

        var preview = await Preview().PreviewAsync();

        var channel = Assert.Single(preview.Channels);
        Assert.Equal(2, channel.SelectedCount);
        Assert.Equal(2, channel.RejectedCount);
        Assert.All(channel.Rejected, r => Assert.Equal(SelectionReasons.FallbackDisabled, r.Reason));
        Assert.Equal(2, preview.Metrics.FallbackDisabledRejectionCount);
    }

    // ---------------- effective policy scope ----------------

    [Fact]
    public async Task Channel_override_wins_over_global_and_labels_scope_override()
    {
        var source = await NewSourceAsync("preview-override");
        for (var i = 0; i < 3; i++)
        {
            await RecordAsync(_channelA, source, $"http://sel-override-{i}.example.test/s.ts");
        }
        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(10, true, null, true);
        await _resolver.UpsertChannelSourceSelectionPolicyAsync(_channelA.Key, 1, false, 4, false);

        var preview = await Preview().PreviewAsync();

        var channel = Assert.Single(preview.Channels);
        Assert.Equal("override", channel.PolicyScope);
        Assert.Equal(1, channel.Policy.MaxSourcesPerChannel);
        Assert.False(channel.Policy.PreferDistinctProviders);
        Assert.Equal(4, channel.Policy.MaxSourcesPerProvider);
        Assert.False(channel.Policy.AllowFallbackToSameProvider);
        Assert.Equal(1, channel.SelectedCount);
        Assert.Equal(2, channel.RejectedCount);
    }

    [Fact]
    public async Task Explicit_global_row_labels_scope_global()
    {
        var source = await NewSourceAsync("preview-global");
        await RecordAsync(_channelA, source, "http://sel-global.example.test/1.ts");
        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(7, false, 3, false);

        var preview = await Preview().PreviewAsync();

        var channel = Assert.Single(preview.Channels);
        Assert.Equal("global", channel.PolicyScope);
        Assert.Equal(7, channel.Policy.MaxSourcesPerChannel);
        Assert.False(channel.Policy.PreferDistinctProviders);
        Assert.Equal(3, channel.Policy.MaxSourcesPerProvider);
        Assert.False(channel.Policy.AllowFallbackToSameProvider);
    }

    [Fact]
    public async Task Without_policy_rows_labels_scope_default_and_uses_default_limits()
    {
        var source = await NewSourceAsync("preview-default");
        await RecordAsync(_channelA, source, "http://sel-default.example.test/1.ts");

        var preview = await Preview().PreviewAsync();

        var channel = Assert.Single(preview.Channels);
        Assert.Equal("default", channel.PolicyScope);
        Assert.Equal(10, channel.Policy.MaxSourcesPerChannel);
        Assert.Equal(SourceSelectionDefaults.DefaultPolicy, channel.Policy);
        Assert.Null(channel.Policy.MaxSourcesPerProvider);
        Assert.True(channel.Policy.PreferDistinctProviders);
        Assert.True(channel.Policy.AllowFallbackToSameProvider);
    }

    // ---------------- scoping / filter / disabled ----------------

    [Fact]
    public async Task Multiple_channels_are_all_present_in_one_preview()
    {
        var source = await NewSourceAsync("preview-multi");
        await RecordAsync(_channelA, source, "http://sel-multi-a.example.test/1.ts");
        await RecordAsync(_channelB, source, "http://sel-multi-b.example.test/1.ts");

        var preview = await Preview().PreviewAsync();

        Assert.True(preview.Applied);
        Assert.True(preview.Channels.Count >= 2);
        Assert.True(preview.Metrics.ChannelsProcessed >= 2);
        Assert.Contains(preview.Channels, c => c.CanonicalChannelId == _channelA.Id);
        Assert.Contains(preview.Channels, c => c.CanonicalChannelId == _channelB.Id);
    }

    [Fact]
    public async Task Channel_filter_restricts_to_one_channel_and_records_the_filter()
    {
        var source = await NewSourceAsync("preview-filter");
        await RecordAsync(_channelA, source, "http://sel-filter-a.example.test/1.ts");
        await RecordAsync(_channelB, source, "http://sel-filter-b.example.test/1.ts");

        var preview = await Preview().PreviewAsync(_channelA.Key);

        var channel = Assert.Single(preview.Channels);
        Assert.Equal(_channelA.Id, channel.CanonicalChannelId);
        Assert.Equal(_channelA.Key, preview.Source.ChannelKeyFilter);
        Assert.Equal(1, preview.Metrics.ChannelsProcessed);
        Assert.Equal("catalog", preview.Source.Origin);
    }

    [Fact]
    public async Task Disabled_channel_source_is_reported_with_source_disabled_reason()
    {
        var source = await NewSourceAsync("preview-disabled");
        await RecordAsync(_channelA, source, "http://sel-disabled.example.test/1.ts", isEnabled: false);

        var preview = await Preview().PreviewAsync();

        var channel = Assert.Single(preview.Channels);
        Assert.Equal(0, channel.SelectedCount);
        Assert.Equal(1, channel.RejectedCount);
        var rejected = Assert.Single(channel.Rejected);
        Assert.Equal(SourceSelectionStage.SourceDisabledReason, rejected.Reason);
        Assert.Equal("rejected", rejected.Decision);
        Assert.Null(rejected.Rank);
        Assert.Equal(1, preview.Metrics.SourceDisabledRejectionCount);
    }

    // ---------------- security / sanitization ----------------

    [Fact]
    public async Task Serialized_preview_never_contains_raw_credentials_and_masks_emitted_urls()
    {
        var source = await NewSourceAsync("preview-sanitize");
        await RecordAsync(_channelA, source, XtreamUrl);

        var preview = await Preview().PreviewAsync();
        var json = JsonSerializer.Serialize(preview);

        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PASS", json, StringComparison.Ordinal);
        Assert.DoesNotContain("USER", json, StringComparison.Ordinal);

        var emitted = preview.Channels
            .SelectMany(c => c.Selected.Concat(c.Rejected))
            .Select(c => c.StreamUrlSanitized)
            .Concat(preview.Unmatched.Select(u => u.StreamUrlSanitized))
            .ToList();

        Assert.NotEmpty(emitted);
        Assert.All(emitted, url => Assert.Contains("***", url));
        Assert.All(emitted, url => Assert.Equal(url, CredentialSanitizer.SanitizeUrl(url)));
    }

    // ---------------- grouped stage result ----------------

    [Fact]
    public async Task Stage_groups_per_channel_and_missing_catalog_yields_empty_groups()
    {
        var source = await NewSourceAsync("preview-grouped");
        await RecordAsync(_channelA, source, "http://grouped-a1.example.test/1.ts");
        await RecordAsync(_channelA, source, "http://grouped-a2.example.test/1.ts");
        await RecordAsync(_channelA, source, "http://grouped-a3.example.test/1.ts", isEnabled: false);

        var policy = Policy();
        var streams = (await _resolver.ListChannelSourcesAsync()).Select(ToStream).ToList();

        var result = await new SourceSelectionStage(_resolver).ApplyAsync(streams, policy);

        Assert.True(result.Applied);
        var group = Assert.Single(result.Channels);
        Assert.Equal(_channelA.Id, group.CanonicalChannelId);
        Assert.Equal(_channelA.Key, group.CanonicalChannelKey);
        Assert.Equal(3, group.CandidateCount);
        Assert.Equal(2, group.Selected.Count);
        var rejected = Assert.Single(group.Rejected);
        Assert.Equal(SourceSelectionStage.SourceDisabledReason, rejected.Reason);

        var noop = await new SourceSelectionStage(null).ApplyAsync(streams, policy);
        Assert.False(noop.Applied);
        Assert.Empty(noop.Channels);
        Assert.Equal(streams.Count, noop.Published.Count);
    }
}
