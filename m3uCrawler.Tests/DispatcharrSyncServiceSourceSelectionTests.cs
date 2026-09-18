using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Dispatcharr;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using m3uCrawler.Services.SourceSelection;
using m3uCrawler.Services.Sync;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 13 (Wave 13-6) — Testes de integração da selecção de fontes no
/// apply Dispatcharr: filtragem por <see cref="DispatcharrSourceSelection"/>,
/// remoção apenas de streams comprovadamente CrawlerManaged, registo de
/// ownership das streams criadas, idempotência de <c>RunAsync</c> e
/// dry-run sanitizado.
///
/// <para>
/// Todos os testes usam um handler HTTP falso (sem rede) e SQLite isolado
/// por teste. As harnesses privadas são copiadas de
/// <c>DispatcharrSyncServiceGlobalPhase4Tests</c> /
/// <c>DispatcharrSyncServiceOwnershipGuardTests</c>, como é convenção da suite.
/// </para>
/// </summary>
public class DispatcharrSyncServiceSourceSelectionTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;

    public DispatcharrSyncServiceSourceSelectionTests()
    {
        _dbPath = TestTempDb.SuitePath($"source-selection-stage-dispatcharr-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        _resolver = new CatalogResolver(_factory, _dbPath);
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        return Task.CompletedTask;
    }

    // ---------------- primary: 100 -> 10 ----------------

    [Fact]
    public async Task Stage_limit_10_of_100_is_enforced_by_apply_and_only_selected_urls_are_posted()
    {
        var channel = await CreateCanonicalAsync("bulk", "Bulk");
        var urls = await RecordChannelSourcesAsync(channel, count: 100, host: "one.example", priorityStart: 1000);

        var streams = (await _resolver.ListChannelSourcesAsync(channel.Id)).Select(ToStream).ToList();
        var policies = new SourceSelectionPolicySet(
            new SourceSelectionPolicy(10, true, null, true),
            hasExplicitGlobal: true);
        var stage = await new SourceSelectionStage(_resolver).ApplyAsync(streams, policies);
        var selection = DispatcharrSourceSelectionFactory.FromStageResult(stage, policies, DateTime.UtcNow);

        // Conjunto esperado escrito à mão: as 10 prioridades mais altas
        // (prioridade 1000-i). Independe do resultado do selector.
        var expected = urls.Take(10).ToArray();

        Assert.Equal(1, selection.Counts.Channels);
        Assert.Equal(100, selection.Counts.Candidates);
        Assert.Equal(10, selection.Counts.Selected);
        Assert.Equal(90, selection.Counts.Rejected);
        Assert.Equal(0, selection.Counts.Unmatched);
        Assert.Equal(0, selection.Counts.Ambiguous);
        Assert.Equal(expected, selection.Channels.Single().Selected.Select(s => s.StreamUrl).ToArray());

        var handler = new SourceSelectionRecordingHandler { NextNewStreamId = 5000 };
        var (svc, _, state) = BuildApplySvc(handler: handler);

        var decision = NewChannelDecision(
            channel.Key, channel.Id,
            urls.Select((u, i) => NewStream(u, i)).ToArray());
        await svc.ApplyAsync(Plan(decision), state, selection, new List<FailedReportEntry>(), CancellationToken.None);

        Assert.Equal(10, handler.StreamPostBodies.Count);
        var postedUrls = handler.StreamPostBodies.Select(ParseUrl).ToArray();
        Assert.Equal(expected, postedUrls);
        Assert.All(postedUrls, u => Assert.Contains(u, expected));

        // Associação = exactamente as 10 seleccionadas (nunca as 90 restantes).
        var associatedIds = ParseStreams(handler.ChannelPostBodies.Single());
        Assert.Equal(10, associatedIds.Length);
        Assert.Equal(handler.PostedStreamIds.OrderBy(x => x), associatedIds.OrderBy(x => x));
    }

    // ---------------- variants: diversity / provider limit ----------------

    [Fact]
    public async Task PreferDistinctProviders_selection_respects_diversity_and_matches_association()
    {
        var channel = await CreateCanonicalAsync("div", "Div");
        var urls = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            var host = ((char)('a' + (i % 3))) + ".example";
            var url = $"http://{host}/s{i:000}.ts";
            var source = await _resolver.EnsureSourceAsync(
                $"div-src-{i:000}", $"div-src-{i:000}", SourceKind.Telegram, $"telegram://div-{i}", 1000 - i);
            await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, url, matchMethod: "test");
            urls.Add(url);
        }

        var streams = (await _resolver.ListChannelSourcesAsync(channel.Id)).Select(ToStream).ToList();
        var policies = new SourceSelectionPolicySet(
            new SourceSelectionPolicy(6, true, null, true),
            hasExplicitGlobal: true);
        var stage = await new SourceSelectionStage(_resolver).ApplyAsync(streams, policies);
        var selection = DispatcharrSourceSelectionFactory.FromStageResult(stage, policies, DateTime.UtcNow);

        var selected = selection.Channels.Single().Selected;
        Assert.Equal(6, selected.Count);
        Assert.Equal(3, selected.Select(s => s.Provider).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, selected.Count(s => s.Reason == SelectionReasons.Diversity));
        Assert.Equal(urls.Take(6), selected.Select(s => s.StreamUrl));

        var handler = new SourceSelectionRecordingHandler { NextNewStreamId = 5100 };
        var (svc, _, state) = BuildApplySvc(handler: handler);
        var decision = NewChannelDecision(
            channel.Key, channel.Id,
            urls.Select((u, i) => NewStream(u, i)).ToArray());
        await svc.ApplyAsync(Plan(decision), state, selection, new List<FailedReportEntry>(), CancellationToken.None);

        Assert.Equal(6, handler.StreamPostBodies.Count);
        Assert.Equal(selected.Select(s => s.StreamUrl), handler.StreamPostBodies.Select(ParseUrl));
        Assert.Equal(6, ParseStreams(handler.ChannelPostBodies.Single()).Length);
    }

    [Fact]
    public async Task MaxSourcesPerProvider_2_is_respected_by_apply_and_matches_association()
    {
        var channel = await CreateCanonicalAsync("mpp", "Mpp");
        var urls = new List<string>();
        for (var i = 0; i < 9; i++)
        {
            var host = ((char)('a' + (i % 3))) + ".example";
            var url = $"http://{host}/s{i:000}.ts";
            var source = await _resolver.EnsureSourceAsync(
                $"mpp-src-{i:000}", $"mpp-src-{i:000}", SourceKind.Telegram, $"telegram://mpp-{i}", 1000 - i);
            await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, url, matchMethod: "test");
            urls.Add(url);
        }

        var streams = (await _resolver.ListChannelSourcesAsync(channel.Id)).Select(ToStream).ToList();
        var policies = new SourceSelectionPolicySet(
            new SourceSelectionPolicy(10, true, 2, true),
            hasExplicitGlobal: true);
        var stage = await new SourceSelectionStage(_resolver).ApplyAsync(streams, policies);
        var selection = DispatcharrSourceSelectionFactory.FromStageResult(stage, policies, DateTime.UtcNow);

        var selected = selection.Channels.Single().Selected;
        Assert.Equal(6, selected.Count);
        Assert.All(
            selected.GroupBy(s => s.Provider, StringComparer.Ordinal),
            g => Assert.Equal(2, g.Count()));

        var handler = new SourceSelectionRecordingHandler { NextNewStreamId = 5200 };
        var (svc, _, state) = BuildApplySvc(handler: handler);
        var decision = NewChannelDecision(
            channel.Key, channel.Id,
            urls.Select((u, i) => NewStream(u, i)).ToArray());
        await svc.ApplyAsync(Plan(decision), state, selection, new List<FailedReportEntry>(), CancellationToken.None);

        Assert.Equal(6, handler.StreamPostBodies.Count);
        Assert.Equal(selected.Select(s => s.StreamUrl), handler.StreamPostBodies.Select(ParseUrl));
    }

    // ---------------- cleanup (unselected crawler-managed) ----------------

    [Fact]
    public async Task Cleanup_drops_only_unselected_crawler_managed_streams_and_protects_unknown_and_external()
    {
        await SeedOwnershipAsync(streamIds: Enumerable.Range(1, 100).Select(i => (long)i).ToList(), channelId: 100);

        var handler = new SourceSelectionRecordingHandler();
        handler.ChannelStreamIds[100] = Enumerable.Range(1, 100).Select(i => (long)i).ToList();
        var (svc, _, state) = BuildApplySvc(catalog: _resolver, handler: handler);

        var urls = Enumerable.Range(1, 100).Select(i => $"http://one.example/s{i:000}.ts").ToList();
        var streams = urls.Select((u, idx) => ExistingUnchanged(u, idx + 1, idx)).ToArray();
        var decision = ExistingChannelDecision(100, "cnn", streams);
        var selection = Selection("cnn", urls.Take(10).ToArray());

        await svc.ApplyAsync(Plan(decision), state, selection, new List<FailedReportEntry>(), CancellationToken.None);

        Assert.Single(handler.PatchBodies);
        var patchIds = ParseStreams(handler.PatchBodies.Single()).ToHashSet();
        var expectedPatch = Enumerable.Range(1, 10).Select(i => (long)i)
            .Concat(Enumerable.Range(91, 10).Select(i => (long)i))
            .ToHashSet();
        Assert.Equal(expectedPatch, patchIds);

        // DELETE apenas dos CrawlerManaged (11..90). Unknown (91..95) e
        // External (96..100) ficam protegidas e continuam associadas.
        var expectedDelete = Enumerable.Range(11, 80).Select(i => (long)i).OrderBy(x => x).ToArray();
        Assert.Equal(expectedDelete, handler.DeleteStreamIds.OrderBy(x => x).ToArray());
        Assert.All(handler.DeleteStreamIds, id => Assert.InRange(id, 11, 90));
        Assert.All(Enumerable.Range(91, 10).Select(i => (long)i), id => Assert.Contains(id, patchIds));
        Assert.All(Enumerable.Range(11, 80).Select(i => (long)i), id => Assert.DoesNotContain(id, patchIds));
    }

    // ---------------- ownership registration ----------------

    [Fact]
    public async Task New_streams_created_under_selection_are_registered_as_crawler_managed()
    {
        var channel = await CreateCanonicalAsync("own", "Own");
        var handler = new SourceSelectionRecordingHandler { NextNewStreamId = 5300, NewChannelId = 9300 };
        var (svc, _, state) = BuildApplySvc(catalog: _resolver, handler: handler);

        var urls = new[]
        {
            "http://one.example/o1.ts",
            "http://one.example/o2.ts",
            "http://one.example/o3.ts",
        };
        var decision = NewChannelDecision(channel.Key, channel.Id, urls.Select((u, i) => NewStream(u, i)).ToArray());
        var selection = Selection(channel.Key, urls);

        await svc.ApplyAsync(Plan(decision), state, selection, new List<FailedReportEntry>(), CancellationToken.None);

        Assert.Equal(3, handler.StreamPostBodies.Count);
        var createdIds = handler.PostedStreamIds.ToArray();
        Assert.Equal(new long[] { 5300, 5301, 5302 }, createdIds);

        var streamOwnership = await _resolver.GetStreamOwnershipMapAsync(createdIds);
        Assert.Equal(3, streamOwnership.Count);
        Assert.All(streamOwnership.Values, o => Assert.Equal(StreamOwnership.CrawlerManaged, o));

        var channelOwnership = await _resolver.GetChannelOwnershipMapAsync(new long[] { 9300 });
        Assert.Equal(ChannelOwnership.CrawlerManaged, channelOwnership[9300]);
    }

    // ---------------- legacy: selection == null ----------------

    [Fact]
    public async Task Null_selection_keeps_legacy_behaviour_and_posts_all_streams()
    {
        var handler = new SourceSelectionRecordingHandler { NextNewStreamId = 5400 };
        var (svc, _, state) = BuildApplySvc(handler: handler);

        var urls = new[]
        {
            "http://one.example/l1.ts",
            "http://one.example/l2.ts",
            "http://one.example/l3.ts",
        };
        var decision = NewChannelDecision("legacy", null, urls.Select((u, i) => NewStream(u, i)).ToArray());

        await svc.ApplyAsync(Plan(decision), state, new List<FailedReportEntry>(), CancellationToken.None);

        Assert.Equal(3, handler.StreamPostBodies.Count);
        Assert.Equal(3, ParseStreams(handler.ChannelPostBodies.Single()).Length);
    }

    // ---------------- unknown / empty selection entry ----------------

    [Fact]
    public async Task Selection_entry_absent_or_empty_drops_all_new_streams_and_creates_no_channel()
    {
        var urls = new[]
        {
            "http://one.example/x1.ts",
            "http://one.example/x2.ts",
        };

        // (a) entrada ausente (chave diferente).
        var handlerAbsent = new SourceSelectionRecordingHandler();
        var (svcAbsent, _, stateAbsent) = BuildApplySvc(handler: handlerAbsent);
        var decision = NewChannelDecision("missing", null, urls.Select((u, i) => NewStream(u, i)).ToArray());
        await svcAbsent.ApplyAsync(
            Plan(decision), stateAbsent, Selection("other-key", urls[0]),
            new List<FailedReportEntry>(), CancellationToken.None);

        Assert.Empty(handlerAbsent.StreamPostBodies);
        Assert.Empty(handlerAbsent.ChannelPostBodies);

        // (b) entrada presente mas sem seleccionadas.
        var handlerEmpty = new SourceSelectionRecordingHandler();
        var (svcEmpty, _, stateEmpty) = BuildApplySvc(handler: handlerEmpty);
        await svcEmpty.ApplyAsync(
            Plan(decision), stateEmpty, Selection("missing"),
            new List<FailedReportEntry>(), CancellationToken.None);

        Assert.Empty(handlerEmpty.StreamPostBodies);
        Assert.Empty(handlerEmpty.ChannelPostBodies);
    }

    [Fact]
    public async Task Existing_channel_with_only_non_selected_crawler_streams_is_patched_empty_and_deleted()
    {
        await SeedOwnershipAsync(new List<long> { 1, 2, 3 }, channelId: 700);

        var handler = new SourceSelectionRecordingHandler();
        handler.ChannelStreamIds[700] = new List<long> { 1, 2, 3 };
        var (svc, _, state) = BuildApplySvc(catalog: _resolver, handler: handler);

        var urls = new[]
        {
            "http://one.example/e1.ts",
            "http://one.example/e2.ts",
            "http://one.example/e3.ts",
        };
        var decision = ExistingChannelDecision(
            700, "cnn",
            ExistingUnchanged(urls[0], 1, 0),
            ExistingUnchanged(urls[1], 2, 1),
            ExistingUnchanged(urls[2], 3, 2));

        await svc.ApplyAsync(
            Plan(decision), state, Selection("cnn"), new List<FailedReportEntry>(), CancellationToken.None);

        Assert.Single(handler.PatchBodies);
        Assert.Empty(ParseStreams(handler.PatchBodies.Single()));
        Assert.Equal(new long[] { 1, 2, 3 }, handler.DeleteStreamIds.OrderBy(x => x).ToArray());
    }

    // ---------------- selected + unmatched extras ----------------

    [Fact]
    public async Task Unselected_extra_urls_are_not_posted_and_not_associated()
    {
        var handler = new SourceSelectionRecordingHandler { NextNewStreamId = 5500 };
        var (svc, _, state) = BuildApplySvc(handler: handler);

        var urls = new[]
        {
            "http://one.example/m1.ts",
            "http://one.example/m2.ts",
            "http://one.example/m3.ts",
        };
        var selection = Selection("mixed", urls[0], urls[1]);
        var decision = NewChannelDecision("mixed", null, urls.Select((u, i) => NewStream(u, i)).ToArray());

        await svc.ApplyAsync(Plan(decision), state, selection, new List<FailedReportEntry>(), CancellationToken.None);

        Assert.Equal(2, handler.StreamPostBodies.Count);
        Assert.Equal(new[] { urls[0], urls[1] }, handler.StreamPostBodies.Select(ParseUrl).ToArray());
        Assert.DoesNotContain(urls[2], handler.StreamPostBodies.Select(ParseUrl));
        Assert.Equal(2, ParseStreams(handler.ChannelPostBodies.Single()).Length);
    }

    // ---------------- idempotency ----------------

    [Fact]
    public async Task RunAsync_with_selection_is_idempotent_on_second_run()
    {
        // Key, DisplayName e alias coincidem (token único sem dígitos) para
        // que o matching curado do matcher seja exacto sem depender do seed.
        var channel = await CreateCanonicalAsync("zulucluster", "zulucluster", aliases: new[] { "zulucluster" });
        var urls = await RecordChannelSourcesAsync(channel, count: 12, host: "one.example", priorityStart: 1000);

        var streams = (await _resolver.ListChannelSourcesAsync(channel.Id)).Select(ToStream).ToList();
        var policies = new SourceSelectionPolicySet(
            new SourceSelectionPolicy(10, true, null, true),
            hasExplicitGlobal: true);
        var stage = await new SourceSelectionStage(_resolver).ApplyAsync(streams, policies);
        var selection = DispatcharrSourceSelectionFactory.FromStageResult(stage, policies, DateTime.UtcNow);
        Assert.Equal(10, selection.Channels.Single().Selected.Count);

        var playlistPath = Path.Combine(Path.GetTempPath(), $"idem-{Guid.NewGuid():N}.m3u");
        var outputDir = TempOutputDir();
        File.WriteAllText(playlistPath, BuildPlaylist(urls, "zulucluster"));
        try
        {
            var handler = new StatefulDispatcharrHandler { NewStreamIdSeed = 6000, NewChannelId = 9100 };
            var svc = BuildRunSvc(handler, outputDir, _resolver);

            var first = await svc.RunAsync(playlistPath, selection, CancellationToken.None);
            Assert.NotNull(first.PlanPath);
            Assert.Equal(10, handler.StreamPostBodies.Count);
            Assert.Single(handler.ChannelPostBodies);

            handler.ResetWrites();

            var second = await svc.RunAsync(playlistPath, selection, CancellationToken.None);
            Assert.NotNull(second.PlanPath);

            Assert.Empty(handler.StreamPostBodies);
            Assert.Empty(handler.PatchBodies);
            Assert.Empty(handler.DeleteStreamIds);
            Assert.DoesNotContain(handler.Traces, t =>
                t.StartsWith("POST", StringComparison.Ordinal)
                || t.StartsWith("PATCH", StringComparison.Ordinal)
                || t.StartsWith("DELETE", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(playlistPath);
            TestTempDb.CleanupDirectory(outputDir);
        }
    }

    // ---------------- dry-run ----------------

    [Fact]
    public async Task DryRun_with_selection_writes_sanitized_artifacts_and_no_write_http()
    {
        const string raw = "http://user:secret@host/live/USER/PASS/1";
        var playlistPath = Path.Combine(Path.GetTempPath(), $"dry-{Guid.NewGuid():N}.m3u");
        var outputDir = TempOutputDir();
        File.WriteAllText(playlistPath, BuildPlaylist(new[] { raw, "http://one.example/plain.ts" }, "Dry"));
        try
        {
            var handler = new SourceSelectionRecordingHandler();
            var svc = BuildService(
                new DispatcharrConfig
                {
                    Enabled = true,
                    BaseUrl = "http://dispatcharr.local",
                    ApiKey = "PLACEHOLDER-API-KEY",
                    DryRun = true,
                    MatchThreshold = 80,
                },
                outputDir,
                handler,
                catalog: null,
                matcher: null);

            var selection = Selection("dry", raw, "http://one.example/plain.ts");
            var result = await svc.RunAsync(playlistPath, selection, CancellationToken.None);

            Assert.True(result.DryRun);
            Assert.NotNull(result.PlanPath);
            Assert.NotNull(result.ReportPath);
            Assert.True(File.Exists(result.PlanPath));
            Assert.True(File.Exists(result.ReportPath));

            Assert.DoesNotContain(handler.Traces, t =>
                t.StartsWith("POST", StringComparison.Ordinal)
                || t.StartsWith("PATCH", StringComparison.Ordinal)
                || t.StartsWith("DELETE", StringComparison.Ordinal));

            var selectionFiles = Directory.GetFiles(outputDir, "dispatcharr_selection_*.json");
            var selectionFile = Assert.Single(selectionFiles);
            var onDisk = await File.ReadAllTextAsync(selectionFile);
            Assert.DoesNotContain("secret", onDisk, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("USER", onDisk, StringComparison.Ordinal);
            Assert.DoesNotContain("PASS", onDisk, StringComparison.Ordinal);
            Assert.Contains("***", onDisk, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(playlistPath);
            TestTempDb.CleanupDirectory(outputDir);
        }
    }

    // ---------------- helpers ----------------

    private static string TempOutputDir()
        => Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid():N}");

    private async Task<CanonicalChannelEntity> CreateCanonicalAsync(
        string key,
        string? displayName = null,
        string[]? aliases = null)
        => await _resolver.CreateCanonicalChannelAsync(
            key,
            displayName ?? key,
            EditorialCategory.Live,
            CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy.CreateEligible,
            isEnabled: true,
            normalizedAliases: aliases ?? Array.Empty<string>());

    /// <summary>
    /// Cria <paramref name="count"/> <c>SourceEntity</c> + <c>ChannelSource</c>
    /// para o mesmo canal, com prioridades distintas (<c>priorityStart - i</c>),
    /// o que torna o ranking do selector inequívoco.
    /// </summary>
    private async Task<List<string>> RecordChannelSourcesAsync(
        CanonicalChannelEntity channel, int count, string host, int priorityStart)
    {
        var urls = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var url = $"http://{host}/s{i:000}.ts";
            var source = await _resolver.EnsureSourceAsync(
                $"src-{channel.Key}-{i:000}",
                $"src-{channel.Key}-{i:000}",
                SourceKind.Telegram,
                $"telegram://{channel.Key}-{i}",
                priorityStart - i);
            await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, url, matchMethod: "test");
            urls.Add(url);
        }
        return urls;
    }

    private static M3uStream ToStream(ChannelSourceEntity channelSource) => new()
    {
        Url = channelSource.StreamUrl,
        Title = channelSource.CanonicalChannel?.DisplayName ?? "Canal",
        Group = "PT",
        IsWorking = true,
        ResponseTime = channelSource.LastResponseTimeMs,
    };

    private async Task SeedOwnershipAsync(IReadOnlyList<long> streamIds, long channelId)
    {
        await using var ctx = _factory.CreateDbContext();
        var now = DateTime.UtcNow;
        foreach (var id in streamIds)
        {
            // 1..90 CrawlerManaged, 91..95 Unknown, 96..100 External.
            var ownership = id <= 90
                ? StreamOwnership.CrawlerManaged
                : id <= 95 ? StreamOwnership.Unknown : StreamOwnership.External;
            ctx.DispatcharrStreamOwnerships.Add(new DispatcharrStreamOwnershipEntity
            {
                DispatcharrStreamId = id,
                DispatcharrChannelId = channelId,
                Ownership = ownership,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        }
        await ctx.SaveChangesAsync();
    }

    private static ChannelDecision NewChannelDecision(string key, long? canonicalId, params StreamMatchDecision[] streams)
        => new()
        {
            Identity = key,
            CanonicalName = key,
            CanonicalChannelKey = key,
            CanonicalChannelId = canonicalId,
            Outcome = SyncOutcome.NewChannel,
            ExistingChannelId = null,
            ChannelGroupName = null,
            MatchReason = "no-match",
            MatchScore = 0,
            Streams = streams,
            AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
        };

    private static ChannelDecision ExistingChannelDecision(long channelId, string key, params StreamMatchDecision[] streams)
        => new()
        {
            Identity = key,
            CanonicalName = key,
            CanonicalChannelKey = key,
            Outcome = SyncOutcome.ExistingReassigned,
            ExistingChannelId = channelId,
            ChannelGroupName = null,
            MatchReason = "exact",
            MatchScore = 100,
            Streams = streams,
            AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
        };

    private static StreamMatchDecision NewStream(string url, int order)
        => new()
        {
            Provider = "p",
            StreamUrl = url,
            StreamName = "n",
            Outcome = SyncOutcome.NewStream,
            ExistingStreamId = null,
            ProposedOrder = order,
            IsWorking = true,
            GroupName = null,
        };

    private static StreamMatchDecision ExistingUnchanged(string url, long streamId, int order)
        => new()
        {
            Provider = "p",
            StreamUrl = url,
            StreamName = "n",
            Outcome = SyncOutcome.ExistingUnchanged,
            ExistingStreamId = streamId,
            ProposedOrder = order,
            IsWorking = true,
            GroupName = null,
        };

    private static MatchPlan Plan(params ChannelDecision[] channels)
        => new()
        {
            GeneratedAtUtc = "2026-01-01T00:00:00Z",
            SourcePlaylistPath = "x.m3u",
            DispatcharrBaseUrl = "http://dispatcharr.local",
            DryRun = false,
            MatchThreshold = 80,
            Channels = channels,
            AmbiguousGroups = Array.Empty<AmbiguousGroupEntry>(),
            ClassifiedExclusions = Array.Empty<ClassifiedExclusion>(),
            UnknownReviewRequired = Array.Empty<ClassifiedExclusion>(),
            Counts = new SyncReportCounts { Matched = channels.Length },
        };

    private static DispatcharrSourceSelection Selection(string key, params string[] urls)
        => new()
        {
            GeneratedAtUtc = "2026-01-01T00:00:00Z",
            Channels = new[]
            {
                new ChannelSourceSelection
                {
                    CanonicalChannelKey = key,
                    CanonicalChannelId = null,
                    PolicyScope = "override",
                    CandidateCount = urls.Length,
                    RejectedCount = 0,
                    Selected = urls
                        .Select((u, i) => new SelectedStreamSelection
                        {
                            StreamUrl = u,
                            Rank = i,
                            Provider = "p",
                            Reason = SelectionReasons.Fill,
                            SourceId = i + 1,
                        })
                        .ToList(),
                },
            },
            Counts = new SelectionCounts
            {
                Channels = 1,
                Candidates = urls.Length,
                Selected = urls.Length,
            },
        };

    private static DispatcharrState EmptyState()
        => new(
            Channels: Array.Empty<DispatcharrChannel>(),
            Streams: Array.Empty<DispatcharrStream>(),
            Groups: Array.Empty<DispatcharrChannelGroup>(),
            Version: "0.30.0");

    private (DispatcharrSyncService Svc, SourceSelectionRecordingHandler Handler, DispatcharrState State)
        BuildApplySvc(CatalogResolver? catalog = null, SourceSelectionRecordingHandler? handler = null)
    {
        handler ??= new SourceSelectionRecordingHandler();
        var cfg = new DispatcharrConfig
        {
            Enabled = true,
            BaseUrl = "http://dispatcharr.local",
            ApiKey = "PLACEHOLDER-API-KEY",
            DryRun = false,
            MatchThreshold = 80,
        };
        var svc = BuildService(cfg, TempOutputDir(), handler, catalog, matcher: null);
        return (svc, handler, EmptyState());
    }

    private DispatcharrSyncService BuildRunSvc(
        HttpMessageHandler handler, string outputDir, CatalogResolver catalog)
    {
        var cfg = new DispatcharrConfig
        {
            Enabled = true,
            BaseUrl = "http://dispatcharr.local",
            ApiKey = "PLACEHOLDER-API-KEY",
            DryRun = false,
            MatchThreshold = 80,
        };
        var matcher = new ChannelMatcher(new AliasResolver(), resolutionPolicy: null, catalog: catalog);
        return BuildService(cfg, outputDir, handler, catalog, matcher);
    }

    private static DispatcharrSyncService BuildService(
        DispatcharrConfig cfg,
        string outputDir,
        HttpMessageHandler inner,
        CatalogResolver? catalog,
        IChannelMatcher? matcher)
    {
        var auth = new DispatcharrAuthState();
        auth.Set("PLACEHOLDER-API-KEY", null);
        var login = new DispatcharrLoginApi(new HttpClient()) { ApiKey = "PLACEHOLDER-API-KEY" };
        var authHandler = new DispatcharrAuthHandler(auth, login) { InnerHandler = inner };
        var client = new HttpClient(authHandler) { BaseAddress = new Uri("http://dispatcharr.local/api/") };

        return new DispatcharrSyncService(
            cfg,
            outputDir,
            aliases: new AliasResolver(),
            ordering: new StreamOrderingPolicy(),
            matcher: matcher,
            channels: new DispatcharrChannelClient(client),
            streams: new DispatcharrStreamClient(client),
            m3u: new DispatcharrM3UClient(client),
            http: client,
            auth: auth,
            login: login,
            catalog: catalog);
    }

    private static string BuildPlaylist(IEnumerable<string> urls, string title)
        => "#EXTM3U\n" +
           string.Join("\n", urls.Select(u => $"#EXTINF:-1 group-title=\"News\",{title}\n{u}")) +
           "\n";

    private static string ParseUrl(string postBody)
        => JsonDocument.Parse(postBody).RootElement.GetProperty("url").GetString()!;

    private static long[] ParseStreams(string body)
    {
        var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("streams", out var streams))
        {
            return Array.Empty<long>();
        }
        return streams.EnumerateArray().Select(e => e.GetInt64()).ToArray();
    }

    private static long ExtractId(string path, string marker)
    {
        var idx = path.LastIndexOf(marker, StringComparison.Ordinal);
        var rest = path[(idx + marker.Length)..];
        var segment = rest.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "0";
        return long.TryParse(segment, out var id) ? id : 0;
    }

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

    // ---------------- fakes ----------------

    private sealed class SourceSelectionRecordingHandler : HttpMessageHandler
    {
        public List<string> Traces { get; } = new();
        public List<string> PatchBodies { get; } = new();
        public List<long> DeleteStreamIds { get; } = new();
        public List<string> StreamPostBodies { get; } = new();
        public List<string> ChannelPostBodies { get; } = new();
        public List<long> PostedStreamIds { get; } = new();
        public Dictionary<long, List<long>> ChannelStreamIds { get; } = new();
        public long? NextNewStreamId { get; set; }
        public long NewChannelId { get; set; } = 9001;
        public bool PatchShouldFail { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            var method = req.Method.ToString().ToUpperInvariant();
            Traces.Add($"{method} {path}");

            if (method == "GET" && path.EndsWith("/api/channels/channels/"))
                return Task.FromResult(JsonResponse(new { count = 0, results = Array.Empty<object>() }));
            if (method == "GET" && path.EndsWith("/api/channels/streams/"))
                return Task.FromResult(JsonResponse(new { count = 0, results = Array.Empty<object>() }));
            if (method == "GET" && path.EndsWith("/api/channels/groups/"))
                return Task.FromResult(JsonResponse(new { count = 0, results = Array.Empty<object>() }));
            if (method == "GET" && path.EndsWith("/api/core/version/"))
                return Task.FromResult(JsonResponse(new { version = "0.30.0" }));
            if (method == "GET" && path.Contains("/api/channels/channels/") && path.EndsWith("/streams/"))
            {
                var channelId = ExtractId(path, "/channels/channels/");
                var ids = ChannelStreamIds.TryGetValue(channelId, out var list) ? list : new List<long>();
                return Task.FromResult(JsonResponse(
                    ids.Select(id => new { id, name = "s", url = (string?)null }).ToArray()));
            }

            if (method == "POST" && path.EndsWith("/api/channels/streams/"))
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                StreamPostBodies.Add(body);
                var id = NextNewStreamId ?? new Random().Next(1_000_000, 10_000_000);
                if (NextNewStreamId.HasValue) NextNewStreamId = id + 1;
                PostedStreamIds.Add(id);
                return Task.FromResult(JsonResponse(new { id, name = "n", url = "x", is_custom = true }));
            }
            if (method == "POST" && path.EndsWith("/api/channels/groups/"))
                return Task.FromResult(JsonResponse(new { id = 5L, name = "News" }));
            if (method == "POST" && path.EndsWith("/api/channels/channels/"))
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                ChannelPostBodies.Add(body);
                return Task.FromResult(JsonResponse(new
                {
                    id = NewChannelId,
                    name = "c",
                    channel_number = 1.0,
                    streams = ParseStreams(body),
                }));
            }

            if (method == "PATCH" && path.Contains("/api/channels/channels/"))
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                PatchBodies.Add(body);
                if (PatchShouldFail)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            if (method == "DELETE" && path.Contains("/api/channels/streams/") && !path.EndsWith("/streams/"))
            {
                DeleteStreamIds.Add(ExtractId(path, "/streams/"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>
    /// Handler com estado em memória que simula o Dispatcharr (canal +
    /// streams criados persistem entre chamadas), necessário para provar
    /// idempotência da segunda execução de <see cref="DispatcharrSyncService.RunAsync(string,DispatcharrSourceSelection?,CancellationToken)"/>.
    /// </summary>
    private sealed class StatefulDispatcharrHandler : HttpMessageHandler
    {
        public long NewStreamIdSeed { get; set; } = 6000;
        public long NewChannelId { get; set; } = 9100;
        public List<string> Traces { get; } = new();
        public List<string> StreamPostBodies { get; } = new();
        public List<string> ChannelPostBodies { get; } = new();
        public List<string> PatchBodies { get; } = new();
        public List<long> DeleteStreamIds { get; } = new();
        public List<long> PostedStreamIds { get; } = new();

        private readonly List<StreamRow> _streams = new();
        private readonly List<ChannelRow> _channels = new();
        private readonly List<GroupRow> _groups = new();
        private long _nextGroupId = 5;

        public void ResetWrites()
        {
            StreamPostBodies.Clear();
            ChannelPostBodies.Clear();
            PatchBodies.Clear();
            DeleteStreamIds.Clear();
            PostedStreamIds.Clear();
            Traces.Clear();
        }

        private sealed class StreamRow
        {
            public long Id;
            public string Name = string.Empty;
            public string Url = string.Empty;
        }

        private sealed class ChannelRow
        {
            public long Id;
            public string Name = string.Empty;
            public List<long> StreamIds = new();
        }

        private sealed class GroupRow
        {
            public long Id;
            public string Name = string.Empty;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            var method = req.Method.ToString().ToUpperInvariant();
            Traces.Add($"{method} {path}");

            if (method == "GET" && path.EndsWith("/api/core/version/"))
                return Task.FromResult(JsonResponse(new { version = "0.30.0" }));
            if (method == "GET" && path.EndsWith("/api/channels/groups/"))
                return Task.FromResult(JsonResponse(new
                {
                    count = _groups.Count,
                    results = _groups.Select(g => new { id = g.Id, name = g.Name }).ToArray(),
                }));
            if (method == "GET" && path.EndsWith("/api/channels/channels/"))
                return Task.FromResult(JsonResponse(new
                {
                    count = _channels.Count,
                    results = _channels.Select(c => new
                    {
                        id = c.Id,
                        name = c.Name,
                        channel_number = 1.0,
                        tvg_id = (string?)null,
                        streams = c.StreamIds.ToArray(),
                    }).ToArray(),
                }));
            if (method == "GET" && path.EndsWith("/api/channels/streams/"))
                return Task.FromResult(JsonResponse(new
                {
                    count = _streams.Count,
                    results = _streams.Select(s => new
                    {
                        id = s.Id,
                        name = s.Name,
                        url = s.Url,
                        tvg_id = (string?)null,
                        channel_group = (long?)null,
                        m3u_account = (long?)null,
                        m3u_account_name = (string?)null,
                        is_custom = true,
                    }).ToArray(),
                }));
            if (method == "GET" && path.Contains("/api/channels/channels/") && path.EndsWith("/streams/"))
            {
                var channelId = ExtractId(path, "/channels/channels/");
                var row = _channels.FirstOrDefault(c => c.Id == channelId);
                var ids = row?.StreamIds ?? new List<long>();
                var ordered = ids
                    .Select(id => _streams.FirstOrDefault(s => s.Id == id))
                    .Where(s => s != null)
                    .Select(s => new { id = s!.Id, name = s.Name, url = s.Url })
                    .ToArray();
                return Task.FromResult(JsonResponse(ordered));
            }

            if (method == "POST" && path.EndsWith("/api/channels/groups/"))
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                var doc = JsonDocument.Parse(body).RootElement;
                var name = doc.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                var row = new GroupRow { Id = _nextGroupId++, Name = name };
                _groups.Add(row);
                return Task.FromResult(JsonResponse(new { id = row.Id, name = row.Name }));
            }
            if (method == "POST" && path.EndsWith("/api/channels/streams/"))
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                StreamPostBodies.Add(body);
                var doc = JsonDocument.Parse(body).RootElement;
                var id = NewStreamIdSeed++;
                var row = new StreamRow
                {
                    Id = id,
                    Name = doc.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                    Url = doc.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty,
                };
                _streams.Add(row);
                PostedStreamIds.Add(id);
                return Task.FromResult(JsonResponse(new { id, name = row.Name, url = row.Url, is_custom = true }));
            }
            if (method == "POST" && path.EndsWith("/api/channels/channels/"))
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                ChannelPostBodies.Add(body);
                var doc = JsonDocument.Parse(body).RootElement;
                var name = doc.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                var streamIds = doc.TryGetProperty("streams", out var s)
                    ? s.EnumerateArray().Select(e => e.GetInt64()).ToList()
                    : new List<long>();
                var row = new ChannelRow { Id = NewChannelId, Name = name, StreamIds = streamIds };
                _channels.Add(row);
                return Task.FromResult(JsonResponse(new
                {
                    id = row.Id,
                    name = row.Name,
                    channel_number = 1.0,
                    streams = row.StreamIds.ToArray(),
                }));
            }

            if (method == "PATCH" && path.Contains("/api/channels/channels/"))
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                PatchBodies.Add(body);
                var channelId = ExtractId(path, "/channels/channels/");
                var row = _channels.FirstOrDefault(c => c.Id == channelId);
                if (row != null)
                {
                    var doc = JsonDocument.Parse(body).RootElement;
                    if (doc.TryGetProperty("streams", out var s))
                    {
                        row.StreamIds = s.EnumerateArray().Select(e => e.GetInt64()).ToList();
                    }
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            if (method == "DELETE" && path.Contains("/api/channels/streams/") && !path.EndsWith("/streams/"))
            {
                var streamId = ExtractId(path, "/streams/");
                DeleteStreamIds.Add(streamId);
                _streams.RemoveAll(s => s.Id == streamId);
                foreach (var channel in _channels)
                {
                    channel.StreamIds.RemoveAll(id => id == streamId);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
