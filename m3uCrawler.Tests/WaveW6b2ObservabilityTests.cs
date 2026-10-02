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
using m3uCrawler.Services.Sync;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE W6b-2 — Observability wires.
///
/// <para>
/// Cobre a ligação de produção dos <see cref="SyncRunEntity"/>/<c>SyncRunStep</c>
/// ao <see cref="DispatcharrSyncService"/> e das
/// <see cref="ChannelSourceObservationEntity"/> ao pipeline de ingestão
/// real, para que o dashboard de degradação e o histórico de observações
/// deixem de estar vazios em execuções reais.
/// </para>
///
/// <para>
/// Sem rede: os clientes Dispatcharr usam um <see cref="JsonHandler"/>
/// local; a base de dados é um ficheiro SQLite temporário.
/// </para>
/// </summary>
[Collection("DashboardStaticState")]
public class WaveW6b2ObservabilityTests : IAsyncLifetime
{
    private static readonly DateTime FixedValidationUtc =
        new(2026, 9, 19, 8, 0, 0, DateTimeKind.Utc);

    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _outputDir;

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;

    public WaveW6b2ObservabilityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sync-run-steps-w6b2-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _outputDir = Path.Combine(_root, "output");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_outputDir);
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        _resolver = new CatalogResolver(_factory, _dbPath);
    }

    public Task DisposeAsync()
    {
        TestTempDb.CleanupDirectory(_root);
        return Task.CompletedTask;
    }

    // ============================================================
    // SyncRun emission
    // ============================================================

    [Fact]
    public async Task RunAsync_normal_apply_persists_single_run_with_steps_and_ok_result()
    {
        var playlist = TempPlaylist(
            "#EXTM3U\n#EXTINF:-1 group-title=\"News\",CNN\nhttps://provider_a.example/cnn\n");
        var writes = new List<string>();
        var (svc, client) = BuildSync(dryRun: false, NewHandler(writes), _resolver);
        using (client)
        {
            await svc.RunAsync(playlist);
        }

        var run = Assert.Single(await _resolver.ListSyncRunsAsync());
        Assert.Equal("ok", run.Result);
        Assert.True(run.FinishedAtUtc >= run.StartedAtUtc);
        Assert.False(string.IsNullOrWhiteSpace(run.AppVersion));

        var steps = await _resolver.GetSyncRunStepsAsync(run.Id);
        Assert.NotEmpty(steps);
        Assert.All(steps, s => Assert.Equal(run.Id, s.SyncRunId));
        Assert.Contains(steps, s => s.Step == "read-plan");
        Assert.Contains(steps, s => s.Step == "selection" && s.Result == "skipped");
        Assert.Contains(steps, s => s.Step == "apply" && s.Result == "ok");
        Assert.Contains(steps, s => s.Step == "apply-create" && s.ItemsSucceeded >= 1);
        Assert.DoesNotContain(steps, s => s.Step == "dry-run");
        // Nenhum passo duplicado dentro do mesmo run.
        Assert.Equal(steps.Count, steps.Select(s => s.Step).Distinct().Count());
    }

    [Fact]
    public async Task RunAsync_dry_run_is_recorded_distinctly_and_writes_nothing()
    {
        var playlist = TempPlaylist(
            "#EXTM3U\n#EXTINF:-1 group-title=\"News\",CNN\nhttps://provider_a.example/cnn\n");
        var writes = new List<string>();
        var (svc, client) = BuildSync(dryRun: true, NewHandler(writes), _resolver);
        using (client)
        {
            var result = await svc.RunAsync(playlist);
            Assert.True(result.DryRun);
        }

        var run = Assert.Single(await _resolver.ListSyncRunsAsync());
        Assert.Equal("dry-run", run.Result);

        var steps = await _resolver.GetSyncRunStepsAsync(run.Id);
        Assert.Contains(steps, s => s.Step == "dry-run" && s.Result == "dry-run");
        Assert.Contains(steps, s => s.Step == "selection" && s.Result == "skipped");
        Assert.DoesNotContain(steps, s => s.Step == "apply");
        Assert.DoesNotContain(steps, s => s.Step.StartsWith("apply-", StringComparison.Ordinal));
        // Dry-run permanece read-only: nenhuma chamada HTTP de escrita.
        Assert.Empty(writes);
    }

    [Fact]
    public async Task RunAsync_partial_failure_is_recorded_as_partial_never_success()
    {
        var playlist = TempPlaylist(
            "#EXTM3U\n" +
            "#EXTINF:-1 group-title=\"Portugal\",RTP 1\nhttps://provider_a.example/rtp1\n" +
            "#EXTINF:-1 group-title=\"Portugal\",SIC\nhttps://provider_a.example/sic\n");
        var writes = new List<string>();
        var (svc, client) = BuildSync(
            dryRun: false, NewHandler(writes, failChannelWithName: "SIC"), _resolver);
        using (client)
        {
            await svc.RunAsync(playlist);
        }

        var run = Assert.Single(await _resolver.ListSyncRunsAsync());
        Assert.Equal("partial", run.Result);
        Assert.NotEqual("ok", run.Result);

        var steps = await _resolver.GetSyncRunStepsAsync(run.Id);
        Assert.Contains(steps, s => s.Step == "apply" && s.Result == "partial" && s.ItemsFailed >= 1);
        Assert.Contains(steps, s => s.Step == "apply-errors" && s.Result == "error");
    }

    [Fact]
    public async Task RunAsync_retry_creates_new_run_without_duplicating_existing_steps()
    {
        var playlist = TempPlaylist(
            "#EXTM3U\n#EXTINF:-1 group-title=\"News\",CNN\nhttps://provider_a.example/cnn\n");
        var writes = new List<string>();
        var (svc, client) = BuildSync(dryRun: false, NewHandler(writes), _resolver);
        using (client)
        {
            await svc.RunAsync(playlist);
            var first = Assert.Single(await _resolver.ListSyncRunsAsync());
            var firstStepCount = (await _resolver.GetSyncRunStepsAsync(first.Id)).Count;

            // Retry (nova execução) => nova row, não novos passos no run anterior.
            await svc.RunAsync(playlist);
            var runs = await _resolver.ListSyncRunsAsync();
            Assert.Equal(2, runs.Count);
            var second = runs.Single(r => r.Id != first.Id);

            var firstStepsAfter = await _resolver.GetSyncRunStepsAsync(first.Id);
            Assert.Equal(firstStepCount, firstStepsAfter.Count);
            Assert.NotEmpty(await _resolver.GetSyncRunStepsAsync(second.Id));
        }
    }

    [Fact]
    public async Task RunAsync_concurrent_runs_are_distinguished_by_their_own_rows_and_steps()
    {
        var playlist = TempPlaylist(
            "#EXTM3U\n#EXTINF:-1 group-title=\"News\",CNN\nhttps://provider_a.example/cnn\n");
        var writes = new List<string>();
        var handler = NewHandler(writes);
        var (svcA, clientA) = BuildSync(
            dryRun: false, handler, _resolver, Path.Combine(_outputDir, "a"));
        var (svcB, clientB) = BuildSync(
            dryRun: false, handler, _resolver, Path.Combine(_outputDir, "b"));
        using (clientA)
        using (clientB)
        {
            await Task.WhenAll(svcA.RunAsync(playlist), svcB.RunAsync(playlist));
        }

        var runs = await _resolver.ListSyncRunsAsync();
        Assert.Equal(2, runs.Count);
        Assert.Equal(2, runs.Select(r => r.Id).Distinct().Count());
        foreach (var run in runs)
        {
            var steps = await _resolver.GetSyncRunStepsAsync(run.Id);
            Assert.NotEmpty(steps);
            Assert.All(steps, s => Assert.Equal(run.Id, s.SyncRunId));
        }
    }

    // ============================================================
    // ChannelSource observations
    // ============================================================

    [Fact]
    public async Task Validation_records_observation_and_dedupes_same_event()
    {
        var key = $"w6b2-obs-{Guid.NewGuid():N}".Substring(0, 32);
        var stream = MakeStream("RTP 1", "http://x.example/rtp1-obs.ts",
            working: true, testedAt: FixedValidationUtc, responseTime: 250);
        var ingestor = NewIngestor();

        await ingestor.IngestAsync(new[] { stream }, key, "Telegram", "pt");
        var cs = await SingleChannelSourceAsync(key);

        var recorded = Assert.Single(await _resolver.GetChannelSourceObservationsAsync(cs.Id));
        Assert.Equal(AvailabilityState.Reachable, recorded.Availability);
        Assert.Equal(250, recorded.ResponseTimeMs);
        Assert.Equal(FixedValidationUtc, recorded.ObservedAtUtc);

        // Re-ingerir o mesmo evento de validação (mesmo LastTested) não duplica.
        await ingestor.IngestAsync(new[] { stream }, key, "Telegram", "pt");
        Assert.Single(await _resolver.GetChannelSourceObservationsAsync(cs.Id));

        // Uma nova validação (timestamp distinto) é uma nova amostra histórica.
        stream.LastTested = FixedValidationUtc.AddMinutes(5);
        await ingestor.IngestAsync(new[] { stream }, key, "Telegram", "pt");
        Assert.Equal(2, (await _resolver.GetChannelSourceObservationsAsync(cs.Id)).Count);
    }

    [Fact]
    public async Task Unvalidated_stream_produces_no_observation()
    {
        var key = $"w6b2-obs-none-{Guid.NewGuid():N}".Substring(0, 32);
        var stream = MakeStream("RTP 1", "http://x.example/rtp1-none.ts", working: true);
        var ingestor = NewIngestor();

        await ingestor.IngestAsync(new[] { stream }, key, "Telegram", "pt");
        var cs = await SingleChannelSourceAsync(key);

        // ChannelSource criada (a identidade resolveu) mas sem observação
        // porque o stream não foi validado (LastTested == default).
        Assert.Empty(await _resolver.GetChannelSourceObservationsAsync(cs.Id));
    }

    // ============================================================
    // Dashboard read endpoints
    // ============================================================

    [Fact]
    public async Task Dashboard_endpoints_return_newly_produced_runs_and_observations()
    {
        var playlist = TempPlaylist(
            "#EXTM3U\n#EXTINF:-1 group-title=\"News\",CNN\nhttps://provider_a.example/cnn\n");
        var writes = new List<string>();
        var (svc, client) = BuildSync(dryRun: false, NewHandler(writes), _resolver);
        using (client)
        {
            await svc.RunAsync(playlist);
        }
        var run = Assert.Single(await _resolver.ListSyncRunsAsync());

        var key = $"w6b2-dash-{Guid.NewGuid():N}".Substring(0, 32);
        var healthyObservedAt = DateTime.UtcNow.AddHours(-1);
        var stream = MakeStream("RTP 1", "http://x.example/rtp1-dash.ts",
            working: true, testedAt: healthyObservedAt, responseTime: 111);
        await NewIngestor().IngestAsync(new[] { stream }, key, "Telegram", "pt");
        var cs = await SingleChannelSourceAsync(key);

        // Amostra terminal mais recente para alimentar a visão de degradação.
        await _resolver.RecordChannelSourceObservationAsync(
            cs.Id, StreamQuality.HD, EpgState.Unknown, AvailabilityState.Dead, 0);

        var harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, _resolver, new PlaylistComposerService(_factory),
            new ImportHistoryService(_outputDir),
            lifecycle: null, auth: null, bootstrap: null, webToken: null, standalone: true);
        try
        {
            var runsResp = await harness.Client.GetAsync("/api/catalog/sync-runs");
            Assert.Equal(HttpStatusCode.OK, runsResp.StatusCode);
            using (var doc = JsonDocument.Parse(await runsResp.Content.ReadAsStringAsync()))
            {
                Assert.Contains(doc.RootElement.EnumerateArray(),
                    e => e.GetProperty("id").GetInt64() == run.Id
                      && e.GetProperty("result").GetString() == "ok");
            }

            var stepsResp = await harness.Client.GetAsync($"/api/catalog/sync-runs/{run.Id}/steps");
            Assert.Equal(HttpStatusCode.OK, stepsResp.StatusCode);
            using (var doc = JsonDocument.Parse(await stepsResp.Content.ReadAsStringAsync()))
            {
                Assert.NotEmpty(doc.RootElement.EnumerateArray());
            }

            var obsResp = await harness.Client.GetAsync(
                $"/api/catalog/channel-sources/{cs.Id}/observations");
            Assert.Equal(HttpStatusCode.OK, obsResp.StatusCode);
            using (var doc = JsonDocument.Parse(await obsResp.Content.ReadAsStringAsync()))
            {
                Assert.Equal(2, doc.RootElement.EnumerateArray().Count());
            }

            var degradedResp = await harness.Client.GetAsync("/api/catalog/degradation/recent");
            Assert.Equal(HttpStatusCode.OK, degradedResp.StatusCode);
            using (var doc = JsonDocument.Parse(await degradedResp.Content.ReadAsStringAsync()))
            {
                Assert.Contains(doc.RootElement.EnumerateArray(),
                    e => e.GetProperty("channelSourceId").GetInt64() == cs.Id);
            }

            var statsResp = await harness.Client.GetAsync("/api/catalog/degradation/stats");
            Assert.Equal(HttpStatusCode.OK, statsResp.StatusCode);
            using (var doc = JsonDocument.Parse(await statsResp.Content.ReadAsStringAsync()))
            {
                Assert.True(doc.RootElement.GetProperty("deadSamples").GetInt32() >= 1);
            }
        }
        finally
        {
            await harness.DisposeAsync();
            WebDashboardService.SetAuth(null, null);
            WebDashboardService.SetConfigurationLifecycle(null);
        }
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static string TempPlaylist(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pl_{Guid.NewGuid():N}.m3u");
        File.WriteAllText(path, body);
        return path;
    }

    private (DispatcharrSyncService Svc, HttpClient Client) BuildSync(
        bool dryRun, JsonHandler handler, CatalogResolver catalog, string? outputDir = null)
    {
        var auth = new DispatcharrAuthState();
        auth.Set("K", null);
        var login = new DispatcharrLoginApi(new HttpClient()) { ApiKey = "K" };
        var authHandler = new DispatcharrAuthHandler(auth, login) { InnerHandler = handler };
        var client = new HttpClient(authHandler)
        {
            BaseAddress = new Uri("http://dispatcharr.local/api/"),
        };
        var svc = new DispatcharrSyncService(
            new DispatcharrConfig
            {
                Enabled = true,
                BaseUrl = "http://dispatcharr.local",
                ApiKey = "K",
                DryRun = dryRun,
                MatchThreshold = 80,
            },
            outputDir ?? _outputDir,
            aliases: new AliasResolver(),
            ordering: new StreamOrderingPolicy(),
            channels: new DispatcharrChannelClient(client),
            streams: new DispatcharrStreamClient(client),
            m3u: new DispatcharrM3UClient(client),
            http: client,
            auth: auth,
            login: login,
            catalog: catalog);
        return (svc, client);
    }

    private static JsonHandler NewHandler(List<string> writeCalls, string? failChannelWithName = null)
    {
        long nextStreamId = 0;
        long nextChannelId = 100;
        var gate = new object();
        return new JsonHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;

            if (req.Method == HttpMethod.Get)
            {
                if (path.EndsWith("/api/core/version/", StringComparison.Ordinal))
                    return JsonResponse(new { version = "0.30.0" });
                if (path.Contains("/api/channels/channels/", StringComparison.Ordinal)
                    && path.Contains("/streams/", StringComparison.Ordinal))
                    return JsonResponse(Array.Empty<object>());
                return JsonResponse(new { count = 0, results = Array.Empty<object>() });
            }

            var body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            lock (gate) { writeCalls.Add($"{req.Method} {path}"); }

            if (req.Method == HttpMethod.Post && path.EndsWith("/api/channels/groups/", StringComparison.Ordinal))
                return JsonResponse(new { id = 5L, name = "News" });

            if (req.Method == HttpMethod.Post && path.EndsWith("/api/channels/streams/", StringComparison.Ordinal))
            {
                lock (gate)
                {
                    nextStreamId++;
                    return JsonResponse(new { id = nextStreamId, name = "s", url = "", is_custom = true });
                }
            }

            if (req.Method == HttpMethod.Post && path.EndsWith("/api/channels/channels/", StringComparison.Ordinal))
            {
                if (failChannelWithName != null
                    && body.Contains(failChannelWithName, StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
                lock (gate)
                {
                    nextChannelId++;
                    return JsonResponse(new { id = nextChannelId, name = "c", streams = Array.Empty<long>() });
                }
            }

            if (req.Method == HttpMethod.Patch)
                return new HttpResponseMessage(HttpStatusCode.OK);
            if (req.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NoContent);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

    private PipelineIngestionService NewIngestor()
        => new(_resolver, new CountryChannelValidator(CountriesDir()));

    private async Task<ChannelSourceEntity> SingleChannelSourceAsync(string sourceKey)
    {
        var src = (await _resolver.ListSourcesAsync()).Single(s => s.Key == sourceKey);
        return (await _resolver.ListChannelSourcesAsync(sourceId: src.Id)).Single();
    }

    private static M3uStream MakeStream(
        string title, string url, bool working, DateTime? testedAt = null, double responseTime = 100,
        string group = "Portugal")
        => new()
        {
            Title = title,
            Url = url,
            Group = group,
            Logo = string.Empty,
            IsWorking = working,
            LastTested = testedAt ?? default,
            ResponseTime = responseTime,
            OriginalExtInf = $"#EXTINF:-1 group-title=\"{group}\",{title}",
        };

    private static string CountriesDir()
    {
        var cwd = Directory.GetCurrentDirectory();
        var repoRoot = Path.GetFullPath(Path.Combine(cwd, "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "m3uCrawler", "runtime-data", "countries");
    }
}
