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

    private async Task<ChannelSourceEntity> RecordAsync(
        CanonicalChannelEntity channel,
        SourceEntity source,
        string realUrl,
        bool isEnabled = true,
        AvailabilityState availability = AvailabilityState.Discovered)
        => await _resolver.RecordChannelSourceAsync(
            channel.Id, source.Id, realUrl,
            availability: availability, matchMethod: "test", isEnabled: isEnabled);

    private async Task<int> PolicyRowCountAsync()
    {
        await using var context = _factory.CreateDbContext();
        return await context.SourceSelectionPolicies.CountAsync();
    }

    /// <summary>
    /// Insere um <see cref="ChannelSourceEntity"/> directamente via
    /// <see cref="ChannelCatalogDbContext"/>, contornando o
    /// <c>CatalogResolver</c> (que sanitiza sempre a URL e escreve
    /// <c>LastResponseTimeMs=0</c>). Usado para provar que o preview
    /// sanitiza defensivamente o catálogo e propaga o response time.
    /// </summary>
    private async Task<ChannelSourceEntity> InsertChannelSourceRawAsync(
        CanonicalChannelEntity channel,
        SourceEntity source,
        string rawUrl,
        bool isEnabled = true,
        AvailabilityState availability = AvailabilityState.Discovered,
        long lastResponseTimeMs = 0)
    {
        await using var context = _factory.CreateDbContext();
        var now = DateTime.UtcNow;
        var entity = new ChannelSourceEntity
        {
            CanonicalChannelId = channel.Id,
            SourceId = source.Id,
            StreamUrl = rawUrl,
            Availability = availability,
            IsEnabled = isEnabled,
            MatchMethod = "test-raw",
            MatchConfidence = 0,
            FirstSeenAtUtc = now,
            LastSeenAtUtc = now,
            LastTestedAtUtc = now,
            LastResponseTimeMs = lastResponseTimeMs,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.ChannelSources.Add(entity);
        await context.SaveChangesAsync();
        return entity;
    }

    private async Task SetResponseTimeAsync(long channelSourceId, long responseTimeMs)
    {
        await using var context = _factory.CreateDbContext();
        var entity = await context.ChannelSources.SingleAsync(cs => cs.Id == channelSourceId);
        entity.LastResponseTimeMs = responseTimeMs;
        await context.SaveChangesAsync();
    }

    private static void AssertAllMetricsZero(SourceSelectionPreviewMetrics m)
        => AssertAllMetricsZeroExceptChannelsProcessed(m, expectedChannelsProcessed: 0);

    /// <summary>
    /// Invariante de <c>no-input</c>: todos os escalares a zero EXCEPTO
    /// <c>ChannelsProcessed</c>, que reflecte os canais canónicos no âmbito
    /// (não é zerado; ver contrato Wave 13-5 / MAJOR-1).
    /// </summary>
    private static void AssertAllMetricsZeroExceptChannelsProcessed(
        SourceSelectionPreviewMetrics m, int expectedChannelsProcessed)
    {
        Assert.Equal(expectedChannelsProcessed, m.ChannelsProcessed);
        Assert.Equal(0, m.ChannelsWithSources);
        Assert.Equal(0, m.CandidateStreamCount);
        Assert.Equal(0, m.SelectedStreamCount);
        Assert.Equal(0, m.RejectedStreamCount);
        Assert.Equal(0, m.UnmatchedStreamCount);
        Assert.Equal(0, m.AmbiguousStreamCount);
        Assert.Equal(0, m.TotalUnmatchedStreamCount);
        Assert.Equal(0, m.ChannelsAtChannelLimit);
        Assert.Equal(0, m.ChannelLimitRejectionCount);
        Assert.Equal(0, m.ProviderLimitRejectionCount);
        Assert.Equal(0, m.DiversitySelectionCount);
        Assert.Equal(0, m.DistinctProviderCount);
        Assert.Equal(0, m.FillSelectionCount);
        Assert.Equal(0, m.FallbackDisabledRejectionCount);
        Assert.Equal(0, m.SourceDisabledRejectionCount);
        Assert.Empty(m.RejectionCounts);
        Assert.Empty(m.ProviderDistribution);
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
        Assert.True(preview.Metrics.DiversitySelectionCount > 0);
        Assert.Equal(4, preview.Metrics.DiversitySelectionCount);
        Assert.Equal(4, preview.Metrics.DistinctProviderCount);
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

    // ---------------- Wave 13-5 corrections ----------------

    [Fact]
    public async Task Metrics_are_complete_and_self_consistent()
    {
        // Catálogo determinístico: 2 streams do fornecedor metrics-a, 1 do
        // metrics-b, 1 desactivado, 1 dead e 1 linha raw (inserida
        // directamente, logo sem hit → unmatched).
        var a = await NewSourceAsync("preview-metrics-a");
        await RecordAsync(_channelA, a, "http://metrics-a.example.test/1.ts");
        await RecordAsync(_channelA, a, "http://metrics-a.example.test/2.ts");
        var b = await NewSourceAsync("preview-metrics-b");
        await RecordAsync(_channelA, b, "http://metrics-b.example.test/1.ts");
        var disabled = await NewSourceAsync("preview-metrics-disabled");
        await RecordAsync(
            _channelA, disabled, "http://metrics-disabled.example.test/1.ts", isEnabled: false);
        var dead = await NewSourceAsync("preview-metrics-dead");
        await RecordAsync(
            _channelA, dead, "http://metrics-dead.example.test/1.ts",
            availability: AvailabilityState.Dead);
        var raw = await NewSourceAsync("preview-metrics-raw");
        await InsertChannelSourceRawAsync(
            _channelA, raw, "http://user:secret@metrics-raw.example.test/live/USER/PASS/1");

        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(3, true, null, true);

        var preview = await Preview().PreviewAsync(_channelA.Key);
        var m = preview.Metrics;

        Assert.Equal(1, m.ChannelsProcessed);
        Assert.Equal(1, m.ChannelsWithSources);
        Assert.Equal(5, m.CandidateStreamCount);
        Assert.Equal(3, m.SelectedStreamCount);
        Assert.Equal(2, m.RejectedStreamCount);
        Assert.Equal(1, m.UnmatchedStreamCount);
        Assert.Equal(0, m.AmbiguousStreamCount);
        Assert.Equal(1, m.TotalUnmatchedStreamCount);
        Assert.Equal(0, m.ChannelsAtChannelLimit);
        Assert.Equal(0, m.ChannelLimitRejectionCount);
        Assert.Equal(0, m.ProviderLimitRejectionCount);
        Assert.Equal(2, m.DiversitySelectionCount);
        Assert.Equal(2, m.DistinctProviderCount);
        Assert.Equal(1, m.FillSelectionCount);
        Assert.Equal(0, m.FallbackDisabledRejectionCount);
        Assert.Equal(1, m.SourceDisabledRejectionCount);

        Assert.Equal(2, m.RejectionCounts.Count);
        Assert.Equal(1, m.RejectionCounts[SelectionReasons.NotWorking]);
        Assert.Equal(1, m.RejectionCounts[SourceSelectionStage.SourceDisabledReason]);

        Assert.Equal(2, m.ProviderDistribution.Count);
        var providerA = m.ProviderDistribution.Single(p => p.Provider == "metrics-a.example.test");
        Assert.Equal(2, providerA.SelectedCount);
        Assert.Equal(1, providerA.ChannelCount);
        var providerB = m.ProviderDistribution.Single(p => p.Provider == "metrics-b.example.test");
        Assert.Equal(1, providerB.SelectedCount);
        Assert.Equal(1, providerB.ChannelCount);

        Assert.Equal(6, preview.InputStreamCount);

        // Invariantes de consistência (frozen contract Wave 13-5).
        Assert.Equal(m.CandidateStreamCount, m.SelectedStreamCount + m.RejectedStreamCount);
        Assert.Equal(
            preview.InputStreamCount,
            m.CandidateStreamCount + m.UnmatchedStreamCount + m.AmbiguousStreamCount);
        Assert.Equal(m.TotalUnmatchedStreamCount, m.UnmatchedStreamCount + m.AmbiguousStreamCount);
    }

    [Fact]
    public async Task Ambiguous_url_is_reported_as_ambiguous_and_not_unmatched()
    {
        // A mesma URL sanitizada mapeada para dois canais canónicos distintos.
        var sourceA = await NewSourceAsync("preview-ambiguous-1");
        var sourceB = await NewSourceAsync("preview-ambiguous-2");
        const string shared = "http://ambiguous.example.test/stream/abc/1.ts";
        await RecordAsync(_channelA, sourceA, shared);
        await RecordAsync(_channelB, sourceB, shared);

        // Filtro a um único canal: o preview sintetiza 1 stream para essa
        // linha, mas o stage continua a ver as duas linhas do catálogo
        // (A e B) → mapeamento ambíguo. Sem filtro haveria 2 streams (uma
        // por linha) e AmbiguousStreamCount seria 2.
        var preview = await Preview().PreviewAsync(_channelA.Key);

        Assert.True(preview.Applied);
        var ambiguous = Assert.Single(preview.Ambiguous);
        Assert.Equal(SourceSelectionPreviewService.AmbiguousReason, ambiguous.Reason);
        Assert.Equal(CredentialSanitizer.SanitizeUrl(shared), ambiguous.StreamUrlSanitized);
        Assert.Empty(preview.Unmatched);
        Assert.Empty(preview.Channels);
        Assert.Equal(1, preview.InputStreamCount);
        Assert.Equal(0, preview.Metrics.UnmatchedStreamCount);
        Assert.Equal(1, preview.Metrics.AmbiguousStreamCount);
        Assert.Equal(1, preview.Metrics.TotalUnmatchedStreamCount);

        // Semântica de produção inalterada: o stage mantém a stream ambígua
        // em Unmatched (pass-through) e expõe a mesma referência em
        // AmbiguousStreams.
        var streams = (await _resolver.ListChannelSourcesAsync())
            .Where(cs => cs.CanonicalChannelId == _channelA.Id)
            .Select(ToStream)
            .ToList();
        var direct = await new SourceSelectionStage(_resolver)
            .ApplyAsync(streams, SourceSelectionDefaults.DefaultPolicy);

        Assert.True(direct.Applied);
        Assert.Equal(1, direct.AmbiguousCount);
        var ambiguousStream = Assert.Single(direct.AmbiguousStreams);
        Assert.Contains(direct.Unmatched, u => ReferenceEquals(u, ambiguousStream));
        Assert.Contains(direct.Published, p => ReferenceEquals(p, ambiguousStream));
        Assert.Single(direct.Unmatched);
    }

    [Fact]
    public async Task Response_time_plumbing_selects_lower_value_and_emits_it_non_zero()
    {
        // NOTA: a produção nunca mantém ChannelSourceEntity.LastResponseTimeMs
        // (CatalogResolver escreve sempre 0); este teste insere valores
        // não-zero directamente via DbContext para provar que o plumbing
        // do preview (ChannelSourceEntity → M3uStream → SelectionCandidate →
        // SourceSelectionPreviewCandidate) propaga o valor quando existe.
        var source = await NewSourceAsync("preview-response-time");
        // A URL lexicalmente menor tem o response time PIOR, para que a
        // selecção só possa ser explicada pelo critério de response time.
        var slow = await RecordAsync(_channelA, source, "http://rt.example.test/a-slow.ts");
        var fast = await RecordAsync(_channelA, source, "http://rt.example.test/z-fast.ts");
        await SetResponseTimeAsync(slow.Id, 500);
        await SetResponseTimeAsync(fast.Id, 40);

        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(1, true, null, true);

        var preview = await Preview().PreviewAsync(_channelA.Key);
        var channel = Assert.Single(preview.Channels);

        var selected = Assert.Single(channel.Selected);
        Assert.Equal("http://rt.example.test/z-fast.ts", selected.StreamUrlSanitized);
        Assert.Equal(40, selected.LastResponseTimeMs);
        Assert.Equal(SelectionReasons.Diversity, selected.Reason);

        var rejected = Assert.Single(channel.Rejected);
        Assert.Equal("http://rt.example.test/a-slow.ts", rejected.StreamUrlSanitized);
        Assert.Equal(500, rejected.LastResponseTimeMs);
    }

    [Fact]
    public async Task Dead_and_disabled_sources_are_rejected_with_exact_reasons()
    {
        var unreachable = await NewSourceAsync("preview-unreachable-exact");
        await RecordAsync(
            _channelA, unreachable, "http://sel-unreachable.example.test/1.ts",
            availability: AvailabilityState.Unreachable);
        var disabled = await NewSourceAsync("preview-disabled-exact");
        await RecordAsync(
            _channelA, disabled, "http://sel-disabled-exact.example.test/1.ts", isEnabled: false);

        var preview = await Preview().PreviewAsync(_channelA.Key);
        var channel = Assert.Single(preview.Channels);

        Assert.Equal(0, channel.SelectedCount);
        Assert.Equal(2, channel.RejectedCount);

        var unreachableRejected = channel.Rejected.Single(
            r => r.StreamUrlSanitized.Contains("sel-unreachable", StringComparison.Ordinal));
        Assert.Equal(SelectionReasons.NotWorking, unreachableRejected.Reason);
        Assert.Equal("rejected", unreachableRejected.Decision);
        Assert.Null(unreachableRejected.Rank);

        var disabledRejected = channel.Rejected.Single(
            r => r.StreamUrlSanitized.Contains("sel-disabled-exact", StringComparison.Ordinal));
        Assert.Equal(SourceSelectionStage.SourceDisabledReason, disabledRejected.Reason);
        Assert.Equal("rejected", disabledRejected.Decision);
        Assert.Null(disabledRejected.Rank);

        Assert.Equal(1, preview.Metrics.SourceDisabledRejectionCount);
        Assert.Equal(1, preview.Metrics.RejectionCounts[SelectionReasons.NotWorking]);
        Assert.Equal(1, preview.Metrics.RejectionCounts[SourceSelectionStage.SourceDisabledReason]);
        Assert.Equal(2, preview.Metrics.CandidateStreamCount);
        Assert.Equal(0, preview.Metrics.SelectedStreamCount);
        Assert.Equal(2, preview.Metrics.RejectedStreamCount);
    }

    [Fact]
    public async Task Unknown_channel_key_is_not_applied_with_zeroed_metrics_and_empty_lists()
    {
        var source = await NewSourceAsync("preview-unknown-key");
        await RecordAsync(_channelA, source, "http://unknown-key.example.test/1.ts");

        var preview = await Preview().PreviewAsync("does-not-exist");

        Assert.False(preview.Applied);
        Assert.Equal(0, preview.InputStreamCount);
        Assert.False(string.IsNullOrEmpty(preview.Source.ChannelKeyFilter));
        Assert.Equal("does-not-exist", preview.Source.ChannelKeyFilter);
        Assert.Empty(preview.Channels);
        Assert.Empty(preview.Unmatched);
        Assert.Empty(preview.Ambiguous);
        AssertAllMetricsZero(preview.Metrics);

        // Nada foi mutado pela tentativa falhada.
        Assert.Equal(0, await PolicyRowCountAsync());
        Assert.Single(await _resolver.ListChannelSourcesAsync());
    }

    [Fact]
    public async Task Raw_catalog_url_is_sanitized_in_unmatched_output()
    {
        // O catálogo normal está pré-sanitizado; só uma linha inserida
        // directamente (com credenciais reais) prova que a sanitização
        // defensiva do preview é efectivamente aplicada. Se o
        // CredentialSanitizer fosse removido do caminho unmatched, este
        // teste falharia com a URL raw no JSON.
        var source = await NewSourceAsync("preview-raw-sanitize");
        const string raw = "http://user:secret@raw.example.test/live/USER/PASS/1";
        await InsertChannelSourceRawAsync(_channelA, source, raw);

        var preview = await Preview().PreviewAsync();

        var unmatched = Assert.Single(preview.Unmatched);
        Assert.Equal(SourceSelectionPreviewService.UnmatchedReason, unmatched.Reason);
        Assert.Contains("***", unmatched.StreamUrlSanitized);
        Assert.Equal(CredentialSanitizer.SanitizeUrl(raw), unmatched.StreamUrlSanitized);
        Assert.Empty(preview.Ambiguous);

        var json = JsonSerializer.Serialize(preview);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PASS", json, StringComparison.Ordinal);
        Assert.DoesNotContain("USER", json, StringComparison.Ordinal);
        Assert.DoesNotContain(raw, json, StringComparison.Ordinal);
        Assert.Contains("***", json);
    }

    [Fact]
    public async Task Preview_decisions_match_hand_computed_expectations_and_direct_stage()
    {
        // 3 fontes, 2 fornecedores, max=2, diversidade ligada:
        // Fase A → a/1 (diversity), b/1 (diversity); a/2 perde por limite.
        var source = await NewSourceAsync("preview-hand-computed");
        await RecordAsync(_channelA, source, "http://parity-a.example.test/1.ts");
        await RecordAsync(_channelA, source, "http://parity-a.example.test/2.ts");
        await RecordAsync(_channelA, source, "http://parity-b.example.test/1.ts");
        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(2, true, null, true);

        var preview = await Preview().PreviewAsync(_channelA.Key);
        var channel = Assert.Single(preview.Channels);

        Assert.Equal(2, channel.SelectedCount);
        Assert.Equal(1, channel.RejectedCount);

        var selected = channel.Selected
            .Select(c => (c.Rank!.Value, c.Reason, c.Provider, c.StreamUrlSanitized))
            .ToArray();
        Assert.Equal(
            new[]
            {
                (0, SelectionReasons.Diversity, "parity-a.example.test", "http://parity-a.example.test/1.ts"),
                (1, SelectionReasons.Diversity, "parity-b.example.test", "http://parity-b.example.test/1.ts"),
            },
            selected);

        var rejected = Assert.Single(channel.Rejected);
        Assert.Equal(SelectionReasons.LimitReached, rejected.Reason);
        Assert.Equal("parity-a.example.test", rejected.Provider);
        Assert.Equal("http://parity-a.example.test/2.ts", rejected.StreamUrlSanitized);
        Assert.Null(rejected.Rank);
        Assert.Equal("rejected", rejected.Decision);

        Assert.Equal(2, preview.Metrics.DiversitySelectionCount);
        Assert.Equal(0, preview.Metrics.FillSelectionCount);
        Assert.Equal(2, preview.Metrics.DistinctProviderCount);

        // De-tautologização: além do cálculo à mão, o preview continua a
        // bater certo com o stage directo sobre os mesmos inputs.
        var streams = (await _resolver.ListChannelSourcesAsync()).Select(ToStream).ToList();
        var policies = await new SourceSelectionPolicyResolver(_resolver)
            .LoadEffectivePoliciesReadOnlyAsync();
        var direct = await new SourceSelectionStage(_resolver).ApplyAsync(streams, policies);
        var directGroup = direct.Channels.Single(c => c.CanonicalChannelId == _channelA.Id);

        Assert.Equal(
            directGroup.Selected.Select(s => (
                s.Rank,
                s.Reason,
                s.Candidate.Provider.Key,
                CredentialSanitizer.SanitizeUrl(s.Candidate.StreamUrl))),
            channel.Selected.Select(c => (c.Rank!.Value, c.Reason, c.Provider, c.StreamUrlSanitized)));
        Assert.Equal(
            directGroup.Rejected.Select(r => (
                r.Reason,
                r.Candidate.Provider.Key,
                CredentialSanitizer.SanitizeUrl(r.Candidate.StreamUrl))),
            channel.Rejected.Select(c => (c.Reason, c.Provider, c.StreamUrlSanitized)));
    }

    [Fact]
    public async Task Prefer_distinct_providers_false_selects_fill_only_and_zero_diversity()
    {
        var source = await NewSourceAsync("preview-no-diversity");
        await RecordAsync(_channelA, source, "http://nodiv-a.example.test/1.ts");
        await RecordAsync(_channelA, source, "http://nodiv-a.example.test/2.ts");
        await RecordAsync(_channelA, source, "http://nodiv-b.example.test/1.ts");
        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(2, false, null, true);

        var preview = await Preview().PreviewAsync(_channelA.Key);
        var channel = Assert.Single(preview.Channels);

        Assert.Equal(2, channel.SelectedCount);
        Assert.Equal(1, channel.RejectedCount);
        Assert.All(channel.Selected, c => Assert.Equal(SelectionReasons.Fill, c.Reason));
        Assert.Equal(
            new[] { "http://nodiv-a.example.test/1.ts", "http://nodiv-a.example.test/2.ts" },
            channel.Selected.Select(c => c.StreamUrlSanitized).ToArray());

        var rejected = Assert.Single(channel.Rejected);
        Assert.Equal(SelectionReasons.LimitReached, rejected.Reason);
        Assert.Equal("http://nodiv-b.example.test/1.ts", rejected.StreamUrlSanitized);

        Assert.Equal(0, preview.Metrics.DiversitySelectionCount);
        Assert.Equal(2, preview.Metrics.FillSelectionCount);
        Assert.Equal(1, preview.Metrics.DistinctProviderCount);
        var provider = Assert.Single(preview.Metrics.ProviderDistribution);
        Assert.Equal("nodiv-a.example.test", provider.Provider);
        Assert.Equal(2, provider.SelectedCount);
        Assert.Equal(1, provider.ChannelCount);
    }

    // ---------------- Wave 13-5 final hardening — MAJOR-1 status precedence ----------------

    [Fact]
    public async Task Preview_matched_channel_without_sources_reports_no_input()
    {
        // A tem fontes; B (filtro) não tem nenhuma ChannelSource. O filtro
        // corresponde a um canal canónico, logo não é `channel-not-found`;
        // o âmbito tem 1 canal mas 0 streams → `no-input`.
        var source = await NewSourceAsync("preview-no-input-filter");
        await RecordAsync(_channelA, source, "http://no-input-a.example.test/1.ts");

        var preview = await Preview().PreviewAsync(_channelB.Key);

        Assert.False(preview.Applied);
        Assert.Equal(SourceSelectionPreviewStatuses.NoInput, preview.Status);
        Assert.Equal(_channelB.Key, preview.Source.ChannelKeyFilter);
        Assert.Equal(1, preview.Metrics.ChannelsProcessed);
        Assert.Equal(0, preview.InputStreamCount);
        AssertAllMetricsZeroExceptChannelsProcessed(preview.Metrics, expectedChannelsProcessed: 1);
        Assert.Empty(preview.Channels);
        Assert.Empty(preview.Unmatched);
        Assert.Empty(preview.Ambiguous);
    }

    [Fact]
    public async Task Preview_catalog_without_channel_sources_reports_no_input()
    {
        // Catálogo com canais canónicos (seed) mas zero ChannelSources: é o
        // caso global de `no-input`, distinto de `no-channels`.
        Assert.True((await _resolver.ListCanonicalChannelsAsync()).Count >= 1);
        Assert.Empty(await _resolver.ListChannelSourcesAsync());

        var preview = await Preview().PreviewAsync();

        Assert.False(preview.Applied);
        Assert.Equal(SourceSelectionPreviewStatuses.NoInput, preview.Status);
        Assert.True(preview.Metrics.ChannelsProcessed >= 1);
        Assert.True(preview.Source.CanonicalChannelCount >= 1);
        Assert.Equal(0, preview.Source.CatalogChannelSourceCount);
        Assert.Equal(0, preview.InputStreamCount);
        AssertAllMetricsZeroExceptChannelsProcessed(
            preview.Metrics, expectedChannelsProcessed: preview.Metrics.ChannelsProcessed);
        Assert.Empty(preview.Channels);
        Assert.Empty(preview.Unmatched);
        Assert.Empty(preview.Ambiguous);
    }

    [Fact]
    public async Task Preview_no_channels_reports_no_channels()
    {
        // Migra a BD para zero canais canónicos removendo os canais do seed.
        var seeded = await _resolver.ListCanonicalChannelsAsync();
        Assert.True(seeded.Count >= 1);
        foreach (var channel in seeded)
        {
            Assert.True(await _resolver.DeleteCanonicalChannelAsync(channel.Id));
        }
        Assert.Empty(await _resolver.ListCanonicalChannelsAsync());

        var preview = await Preview().PreviewAsync();

        Assert.False(preview.Applied);
        Assert.Equal(SourceSelectionPreviewStatuses.NoChannels, preview.Status);
        Assert.Equal(0, preview.Source.CanonicalChannelCount);
        Assert.Equal(0, preview.Metrics.ChannelsProcessed);
        Assert.Equal(0, preview.InputStreamCount);
        AssertAllMetricsZero(preview.Metrics);
        Assert.Empty(preview.Channels);
        Assert.Empty(preview.Unmatched);
        Assert.Empty(preview.Ambiguous);
    }

    [Fact]
    public async Task Preview_unknown_channel_reports_channel_not_found()
    {
        var source = await NewSourceAsync("preview-channel-not-found");
        await RecordAsync(_channelA, source, "http://channel-not-found.example.test/1.ts");

        var unknown = await Preview().PreviewAsync("does-not-exist");

        Assert.False(unknown.Applied);
        Assert.Equal(SourceSelectionPreviewStatuses.ChannelNotFound, unknown.Status);
        Assert.Equal("does-not-exist", unknown.Source.ChannelKeyFilter);
        Assert.Equal(0, unknown.Metrics.ChannelsProcessed);
        Assert.Equal(0, unknown.InputStreamCount);
        AssertAllMetricsZero(unknown.Metrics);
        Assert.Empty(unknown.Channels);
        Assert.Empty(unknown.Unmatched);
        Assert.Empty(unknown.Ambiguous);

        // Distinção semântica face a `no-input` (filtro correspondido a um
        // canal sem fontes): mesma forma zerada, status diferente. Não é
        // possível confundir os dois casos pelo JSON.
        var noInput = await Preview().PreviewAsync(_channelB.Key);
        Assert.Equal(SourceSelectionPreviewStatuses.NoInput, noInput.Status);
        Assert.False(noInput.Applied);
        Assert.Equal(0, noInput.InputStreamCount);
        Assert.Equal(unknown.InputStreamCount, noInput.InputStreamCount);
        Assert.NotEqual(unknown.Status, noInput.Status);
    }

    // ---------------- Wave 13-5 final hardening — MINOR partitions / case ----------------

    [Fact]
    public async Task Preview_partitions_unmatched_and_ambiguous_in_one_run()
    {
        // 1 URL ambígua (mesma URL sanitizada em A e B) + 1 linha raw sem
        // hit no catálogo (credenciais guardadas cruas → a chave sanitizada
        // da stream sintetizada nunca coincide com a chave armazenada).
        var ambA = await NewSourceAsync("preview-partition-amb-a");
        var ambB = await NewSourceAsync("preview-partition-amb-b");
        const string shared = "http://partition-ambiguous.example.test/stream/1.ts";
        await RecordAsync(_channelA, ambA, shared);
        await RecordAsync(_channelB, ambB, shared);

        var rawSource = await NewSourceAsync("preview-partition-raw");
        const string raw = "http://user:secret@partition-raw.example.test/live/USER/PASS/1";
        await InsertChannelSourceRawAsync(_channelA, rawSource, raw);

        var preview = await Preview().PreviewAsync();

        Assert.True(preview.Applied);
        Assert.NotEmpty(preview.Unmatched);
        Assert.NotEmpty(preview.Ambiguous);

        // Sem filtro, cada linha do catálogo sintetiza uma stream: as duas
        // linhas da URL partilhada são ambíguas (2) e a linha raw é
        // unmatched (1).
        Assert.Equal(1, preview.Metrics.UnmatchedStreamCount);
        Assert.Equal(2, preview.Metrics.AmbiguousStreamCount);
        Assert.Equal(3, preview.Metrics.TotalUnmatchedStreamCount);
        Assert.Equal(3, preview.InputStreamCount);
        Assert.Equal(
            preview.Metrics.TotalUnmatchedStreamCount,
            preview.Metrics.UnmatchedStreamCount + preview.Metrics.AmbiguousStreamCount);

        // Cada lista usa um motivo exclusivo; a partição é disjunta.
        Assert.All(preview.Unmatched, u =>
            Assert.Equal(SourceSelectionPreviewService.UnmatchedReason, u.Reason));
        Assert.All(preview.Ambiguous, a =>
            Assert.Equal(SourceSelectionPreviewService.AmbiguousReason, a.Reason));

        var unmatchedKeys = preview.Unmatched
            .Select(u => (u.StreamUrlSanitized, u.Reason))
            .ToHashSet();
        var ambiguousKeys = preview.Ambiguous
            .Select(a => (a.StreamUrlSanitized, a.Reason))
            .ToHashSet();
        Assert.Empty(unmatchedKeys.Intersect(ambiguousKeys));

        // A URL ambígua não aparece em Unmatched e a raw não aparece em
        // Ambiguous; nenhuma referência é partilhada entre as duas listas.
        Assert.DoesNotContain(
            preview.Unmatched,
            u => u.StreamUrlSanitized == CredentialSanitizer.SanitizeUrl(shared));
        Assert.DoesNotContain(
            preview.Ambiguous,
            a => a.StreamUrlSanitized == CredentialSanitizer.SanitizeUrl(raw));
        Assert.DoesNotContain(
            preview.Unmatched,
            u => preview.Ambiguous.Any(a => ReferenceEquals(a, u)));
    }

    [Fact]
    public async Task Preview_channel_key_is_case_sensitive()
    {
        var channel = await _resolver.CreateCanonicalChannelAsync(
            "preview-case-sensitive",
            "Preview Case Sensitive",
            EditorialCategory.Live,
            CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy.CreateEligible,
            isEnabled: true,
            normalizedAliases: Array.Empty<string>());
        var source = await NewSourceAsync("preview-case-sensitive-source");
        await RecordAsync(channel, source, "http://case-sensitive.example.test/1.ts");

        var exact = await Preview().PreviewAsync(channel.Key);
        Assert.True(exact.Applied);
        Assert.Equal(SourceSelectionPreviewStatuses.Applied, exact.Status);
        Assert.Equal(1, exact.Metrics.ChannelsProcessed);

        var upper = channel.Key.ToUpperInvariant();
        Assert.NotEqual(channel.Key, upper);

        var upperResult = await Preview().PreviewAsync(upper);
        Assert.False(upperResult.Applied);
        Assert.Equal(SourceSelectionPreviewStatuses.ChannelNotFound, upperResult.Status);
        Assert.Equal(upper, upperResult.Source.ChannelKeyFilter);
        Assert.Equal(0, upperResult.Metrics.ChannelsProcessed);
        Assert.Equal(0, upperResult.InputStreamCount);
        AssertAllMetricsZero(upperResult.Metrics);
        Assert.Empty(upperResult.Channels);
        Assert.Empty(upperResult.Unmatched);
        Assert.Empty(upperResult.Ambiguous);
    }
}
