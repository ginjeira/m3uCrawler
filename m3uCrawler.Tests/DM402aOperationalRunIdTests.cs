using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.LiveRun;
using m3uCrawler.Services.Validation;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// D-M4-02a — identidade de Run na ocorrência de descoberta (W1).
///
/// Decisão ratificada:
///   DiscoveryCandidate.RunId = RunCoordinator.RunId (= ILiveRunProgress.RunId);
///   PipelineTrace.RunId      = diagnóstico (nunca identidade de Run).
///
/// Sem Run operacional => RunId = null: não fabrica identidade, não usa o
/// trace como fallback e não aplica dedup baseada em Run (D-M4-01 B1).
/// </summary>
public class DM402aOperationalRunIdTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;
    private ChannelCatalogBootstrapper _bootstrapper = null!;
    private CatalogResolver _resolver = null!;

    public DM402aOperationalRunIdTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"dm402a-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        _bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await _bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        _resolver = new CatalogResolver(_factory, _dbPath);
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        return Task.CompletedTask;
    }

    private sealed class FakeProgress : ILiveRunProgress
    {
        public FakeProgress(string runId) => RunId = runId;

        public string RunId { get; }

        public Task EnterPhaseAsync(
            LiveRunPhase phase, string? message = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public void ReportCounts(Action<LiveRunCounts> mutate) { }
        public void ReportCounts(RunReport report) { }
        public void ReportMessage(string message) { }
        public void ReportActivity(
            LiveRunActivityCategory category,
            LiveRunActivityLevel level,
            string message,
            IReadOnlyDictionary<string, string>? metadata = null) { }
    }

    private static string CountriesDir()
    {
        var cwd = Directory.GetCurrentDirectory();
        var repoRoot = Path.GetFullPath(Path.Combine(cwd, "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "m3uCrawler", "runtime-data", "countries");
    }

    private PipelineIngestionService NewIngestor() =>
        new PipelineIngestionService(_resolver, new CountryChannelValidator(CountriesDir()));

    private static M3uStream XtreamStream() =>
        new()
        {
            Title = "RTP 1",
            Url = "http://host.example:8080/get.php?username=u1&password=p",
            Group = "Portugal",
            Logo = string.Empty,
            IsWorking = true,
            LastTested = DateTime.UtcNow,
            ResponseTime = 100,
            OriginalExtInf = "#EXTINF:-1 group-title=\"Portugal\",RTP 1",
        };

    private async Task<List<DiscoveryCandidateEntity>> CandidatesAsync()
        => (await _resolver.ListDiscoveryCandidatesAsync()).ToList();

    // ── Seleção: operacional vs diagnóstico ──────────────────────────

    [Fact]
    public void Operational_run_id_is_preferred_and_trace_is_never_used()
    {
        var progress = new FakeProgress("run-A");
        var scraper = new TelegramScraperService();
        scraper.SetTrace(new PipelineTrace("trace-X")); // tracing activo

        var runId = TelegramScraperService.ResolveOperationalRunId(progress);

        Assert.Equal("run-A", runId);
        Assert.NotEqual("trace-X", runId);
    }

    [Fact]
    public void No_operational_run_yields_null()
    {
        Assert.Null(TelegramScraperService.ResolveOperationalRunId(null));
        Assert.Null(TelegramScraperService.ResolveOperationalRunId(NullLiveRunProgress.Instance));
        Assert.Null(TelegramScraperService.ResolveOperationalRunId(new FakeProgress("   ")));
    }

    [Fact]
    public void Operational_run_id_works_when_tracing_is_disabled()
    {
        var progress = new FakeProgress("run-A");
        Assert.Equal("run-A", TelegramScraperService.ResolveOperationalRunId(progress));
    }

    [Fact]
    public void Two_runs_remain_separate_even_with_a_constant_trace()
    {
        var trace = new PipelineTrace("trace-X"); // constante / process-scoped
        Assert.Equal("trace-X", trace.RunId);
        Assert.Equal("run-A", TelegramScraperService.ResolveOperationalRunId(new FakeProgress("run-A")));
        Assert.Equal("run-B", TelegramScraperService.ResolveOperationalRunId(new FakeProgress("run-B")));
    }

    // ── Wiring persistente: ingestão recebe o RunId operacional ──────

    [Fact]
    public async Task Ingestion_persists_operational_run_id_and_not_trace()
    {
        var progress = new FakeProgress("run-A");
        var scraper = new TelegramScraperService();
        scraper.SetTrace(new PipelineTrace("trace-X"));

        var runId = TelegramScraperService.ResolveOperationalRunId(progress);
        await NewIngestor().IngestAsync(
            new[] { XtreamStream() }, "dm402a-wire", "Telegram", "pt", default, runId);

        var candidates = await CandidatesAsync();
        Assert.Contains(candidates, c => c.RunId == "run-A");
        Assert.DoesNotContain(candidates, c => c.RunId == "trace-X");
    }

    [Fact]
    public async Task Two_runs_same_account_yield_distinct_candidates_with_trace_active()
    {
        var scraper = new TelegramScraperService();
        scraper.SetTrace(new PipelineTrace("trace-X"));

        var runIdA = TelegramScraperService.ResolveOperationalRunId(new FakeProgress("run-A"));
        var runIdB = TelegramScraperService.ResolveOperationalRunId(new FakeProgress("run-B"));

        await NewIngestor().IngestAsync(
            new[] { XtreamStream() }, "dm402a-two", "Telegram", "pt", default, runIdA);
        await NewIngestor().IngestAsync(
            new[] { XtreamStream() }, "dm402a-two", "Telegram", "pt", default, runIdB);

        var candidates = await CandidatesAsync();
        Assert.Contains(candidates, c => c.RunId == "run-A");
        Assert.Contains(candidates, c => c.RunId == "run-B");
        Assert.DoesNotContain(candidates, c => c.RunId == "trace-X");
        Assert.Equal(2, candidates.Count(c => c.RunId == "run-A" || c.RunId == "run-B"));
    }

    [Fact]
    public async Task Without_operational_run_id_is_null_and_dedup_is_not_applied()
    {
        var runId = TelegramScraperService.ResolveOperationalRunId(null);
        Assert.Null(runId);

        await NewIngestor().IngestAsync(
            new[] { XtreamStream() }, "dm402a-none", "Telegram", "pt", default, runId);
        await NewIngestor().IngestAsync(
            new[] { XtreamStream() }, "dm402a-none", "Telegram", "pt", default, runId);

        var candidates = await CandidatesAsync();
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, c => Assert.Null(c.RunId));
    }
}
