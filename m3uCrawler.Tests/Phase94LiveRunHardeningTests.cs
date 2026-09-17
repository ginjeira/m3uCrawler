using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.LiveRun;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9C.4 (subwave 7) — Hardening e regressão.
///
/// <para>
/// Estes testes fixam invariantes transversais que não pertencem a uma
/// única subwave:</para>
/// <list type="bullet">
///   <item>Contrato <see cref="RunReport"/> inalterado (44 propriedades
///         públicas top-level).</item>
///   <item>Sanitização de credenciais no <see cref="LiveRunSanitizer"/>.</item>
///   <item>Janela de 24h também para a lista de execuções recentes.</item>
///   <item>Recuperação pós-restart exposta coerente com o estado do
///         dashboard (<c>failed</c>, não <c>idle</c>).</item>
///   <item>Um crash durante um run nunca deixa o lock preso nem duas
///         execuções em voo.</item>
/// </list>
/// </summary>
public class Phase94LiveRunHardeningTests : IAsyncLifetime
{
    /// <summary>
    /// Contrato congelado do <see cref="RunReport"/>. Qualquer adição,
    /// remoção ou renomeação deve ser uma decisão explícita — o 9C.4 não
    /// altera este contrato.
    /// </summary>
    private static readonly string[] FrozenRunReportProperties =
    {
        "CandidatesFound",
        "ChannelsRecognized",
        "CountryMatches",
        "DialogErrors",
        "DialogsIncomplete",
        "DialogsTotal",
        "DiscoveredPlaylists",
        "DocumentDownloadFailures",
        "DocumentDownloadSuccesses",
        "DocumentsWithFilename",
        "DocumentsWithoutFilename",
        "DurationMs",
        "FinishedAt",
        "HtmlCandidatesCreated",
        "MessagesAnalyzed",
        "MessagesWithDocumentMedia",
        "MessagesWithMedia",
        "MessagesWithPhotoMedia",
        "PlaylistsDownloaded",
        "PlaylistsInvalid",
        "PlaylistsRejected",
        "PublicationsDiscovered",
        "PublicationsRequiresReview",
        "PublicationsResolutionFailed",
        "PublicationsResolved",
        "PublicationsTriageLog",
        "PublicationsUnsupported",
        "RejectionReasons",
        "StartedAt",
        "Status",
        "StreamsAfterCountryFilter",
        "StreamsExtracted",
        "StreamsFailed",
        "StreamsRejectedByCountry",
        "StreamsTested",
        "StreamsWorking",
        "TraceEventsAttachmentDownloadComplete",
        "TraceEventsAttachmentDownloadFailed",
        "TraceEventsAttachmentDownloadStart",
        "TraceEventsCandidateCreated",
        "TraceEventsCandidatePromoted",
        "TraceEventsCandidateRejected",
        "TraceEventsChannelDequeue",
        "TraceEventsChannelEnqueue",
        "TraceEventsHttpRequestEnd",
        "TraceEventsHttpRequestFailed",
        "TraceEventsHttpRequestStart",
        "TraceEventsResolverEnd",
        "TraceEventsResolverStart",
        "TraceEventsWorkerEnd",
        "TraceEventsWorkerStart",
        "TraceEventsXtreamAccount",
        "XtreamAccountsAfterDedup",
        "XtreamAccountsDiscovered",
        "XtreamAccountsForwarded",
    };

    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;

    public Phase94LiveRunHardeningTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"liverun-harden-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static IRunPipeline IdlePipeline() => new AsyncDelegatePipeline(_ => Task.CompletedTask);

    private RunCoordinator NewCoordinator(Func<LiveRunRequest, IRunPipeline>? factory = null)
        => new(_factory, factory ?? (_ => IdlePipeline()), null);

    // ===================== Contrato RunReport =====================

    [Fact]
    public void RunReport_contract_is_frozen()
    {
        var actual = typeof(RunReport)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var expected = FrozenRunReportProperties
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(55, actual.Length);
        Assert.Equal(expected, actual);
    }

    // ===================== Sanitização =====================

    [Theory]
    [InlineData("http://iptvuser:hunter2@example.com/get.php?username=u&password=p", "hunter2")]
    [InlineData("GET http://example.com/live/joao/segredo123/12.ts", "segredo123")]
    [InlineData("login com Bearer abcDEF123456token", "abcDEF123456token")]
    [InlineData("cookie: session=abcdef123456", "abcdef123456")]
    public void Sanitizer_removes_credentials_from_messages(string raw, string secret)
    {
        var sanitized = LiveRunSanitizer.Message(raw);

        Assert.DoesNotContain(secret, sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitizer_truncates_to_the_schema_limit_and_never_returns_null()
    {
        var sanitized = LiveRunSanitizer.Message(new string('x', 5000));

        Assert.NotNull(sanitized);
        Assert.Equal(LiveRunSanitizer.MaxMessageLength, sanitized.Length);
        Assert.Equal(string.Empty, LiveRunSanitizer.Message(null));
        Assert.Equal(string.Empty, LiveRunSanitizer.Message("   "));
    }

    [Fact]
    public void Sanitizer_metadata_drops_credentials_and_caps_entries()
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["url"] = "http://user:hunter2@example.com/playlist.m3u",
            ["token"] = "Bearer abcDEF123456token",
        };
        for (var i = 0; i < 50; i++)
        {
            metadata[$"k{i}"] = "v";
        }

        var sanitized = LiveRunSanitizer.Metadata(metadata);

        Assert.NotNull(sanitized);
        Assert.True(sanitized!.Count <= 16);
        var joined = string.Join("|", sanitized.Select(p => $"{p.Key}={p.Value}"));
        Assert.DoesNotContain("hunter2", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("abcDEF123456token", joined, StringComparison.Ordinal);
    }

    // ===================== Janela de 24h (lista) =====================

    [Fact]
    public async Task Recent_runs_list_excludes_runs_older_than_24h()
    {
        var coordinator = NewCoordinator();
        await coordinator.StartAsync(
            new LiveRunRequest { Mode = LiveRunMode.Telegram, Source = LiveRunSource.Cli },
            CancellationToken.None);

        // Envelhecer o run para 25h atrás.
        await using (var ctx = _factory.CreateDbContext())
        {
            var run = await ctx.LiveRuns.FirstAsync();
            run.StartedAtUtc = DateTime.UtcNow.AddHours(-25);
            run.FinishedAtUtc = DateTime.UtcNow.AddHours(-25).AddMinutes(1);
            await ctx.SaveChangesAsync();
        }

        var recent = await coordinator.GetRecentFinishedSnapshotsAsync(10, CancellationToken.None);

        Assert.Empty(recent);
    }

    [Fact]
    public async Task Recent_runs_list_returns_newest_first_within_the_window()
    {
        var coordinator = NewCoordinator();
        for (var i = 0; i < 3; i++)
        {
            await coordinator.StartAsync(
                new LiveRunRequest { Mode = LiveRunMode.Telegram, Source = LiveRunSource.Cli },
                CancellationToken.None);
            await Task.Delay(15);
        }

        var recent = await coordinator.GetRecentFinishedSnapshotsAsync(10, CancellationToken.None);

        Assert.Equal(3, recent.Count);
        for (var i = 1; i < recent.Count; i++)
        {
            Assert.True(recent[i - 1].FinishedAtUtc >= recent[i].FinishedAtUtc);
        }
    }

    // ===================== Restart / recuperação =====================

    [Fact]
    public async Task Interrupted_run_is_recovered_as_failed_and_surfaces_as_recent_failed()
    {
        var firstProcess = NewCoordinator();
        await using (var ctx = _factory.CreateDbContext())
        {
            // Simula um run que ficou a meio quando o processo morreu:
            // StartedAtUtc definido, FinishedAtUtc null, Unknown.
            ctx.LiveRuns.Add(new LiveRunEntity
            {
                RunId = Guid.NewGuid().ToString(),
                Mode = LiveRunWireNames.ModeTelegram,
                Source = LiveRunWireNames.SourceCli,
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                FinishedAtUtc = null,
                TerminalStatus = LiveRunTerminalStatus.Unknown,
                CountsJson = "{}",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            });
            await ctx.SaveChangesAsync();
        }

        var recovered = await firstProcess.RecoverInterruptedRunsAsync(CancellationToken.None);
        Assert.Equal(1, recovered);

        // Um "novo processo" (mesmo DB, novo coordinator) vê o run como
        // failed — nunca idle/unknown.
        var secondProcess = NewCoordinator();
        Assert.False(secondProcess.IsRunning);
        var recent = await secondProcess.GetRecentFinishedSnapshotsAsync(10, CancellationToken.None);
        var run = Assert.Single(recent);
        Assert.Equal(LiveRunTerminalStatus.Failed, run.TerminalStatus);
        Assert.NotNull(run.FinishedAtUtc);
        Assert.Contains("recovered after restart", run.LastMessage ?? string.Empty, StringComparison.Ordinal);

        // Recuperação é idempotente.
        Assert.Equal(0, await secondProcess.RecoverInterruptedRunsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Coordinator_lock_is_released_even_when_the_pipeline_crashes()
    {
        var attempts = 0;
        var coordinator = NewCoordinator(_ => new DelegatePipeline(_ =>
        {
            attempts++;
            throw new InvalidOperationException("crash");
        }));

        for (var i = 0; i < 3; i++)
        {
            var outcome = await coordinator.StartAsync(
                new LiveRunRequest { Mode = LiveRunMode.Telegram, Source = LiveRunSource.Cli },
                CancellationToken.None);
            Assert.False(outcome.Succeeded);
            Assert.False(coordinator.IsRunning);
        }

        Assert.Equal(3, attempts);

        await using var ctx = _factory.CreateDbContext();
        var runs = await ctx.LiveRuns.AsNoTracking().ToListAsync();
        Assert.Equal(3, runs.Count);
        Assert.All(runs, r => Assert.Equal(LiveRunTerminalStatus.Failed, r.TerminalStatus));
    }

    [Fact]
    public async Task Exactly_one_execution_is_recorded_for_a_burst_of_manual_callers()
    {
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = NewCoordinator(_ => new AsyncDelegatePipeline(async _ =>
        {
            blocker.TrySetResult();
            await release.Task;
        }));

        var request = new LiveRunRequest { Mode = LiveRunMode.Telegram, Source = LiveRunSource.Manual };

        var winner = coordinator.KickStartAsync(request, CancellationToken.None);
        await blocker.Task;

        var rejected = 0;
        var losers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            try
            {
                await coordinator.KickStartAsync(request, CancellationToken.None);
            }
            catch (RunAlreadyInProgressException)
            {
                Interlocked.Increment(ref rejected);
            }
        })).ToArray();
        await Task.WhenAll(losers);

        Assert.Equal(8, Volatile.Read(ref rejected));

        release.TrySetResult();
        await winner;

        await using var ctx = _factory.CreateDbContext();
        Assert.Equal(1, await ctx.LiveRuns.CountAsync());
    }

    // ===================== Dados / índices =====================

    [Fact]
    public async Task Steps_and_counts_are_persisted_together_with_the_run()
    {
        var coordinator = NewCoordinator(_ => new ProgressReportingPipeline());

        await coordinator.StartAsync(
            new LiveRunRequest { Mode = LiveRunMode.Telegram, Source = LiveRunSource.Cli },
            CancellationToken.None);

        await using var ctx = _factory.CreateDbContext();
        var run = await ctx.LiveRuns.AsNoTracking().SingleAsync();
        var steps = await ctx.LiveRunSteps.AsNoTracking()
            .Where(s => s.LiveRunId == run.Id)
            .OrderBy(s => s.PhaseIndex)
            .ToListAsync();

        Assert.NotEmpty(steps);
        Assert.Equal(steps.Select(s => s.PhaseIndex).Distinct().Count(), steps.Count);
        Assert.Contains(steps, s => s.Phase == LiveRunPhase.ReadingTelegram);
        Assert.True(steps[0].PhaseIndex <= steps[^1].PhaseIndex);

        // CountsJson é a representação persistente tipada.
        var counts = LiveRunCounts.FromJson(run.CountsJson);
        Assert.NotNull(counts);
        Assert.Equal(3, counts!.StreamsTested);
        Assert.Equal(2, counts.StreamsWorking);
    }

    private sealed class DelegatePipeline : IRunPipeline
    {
        private readonly Action<LiveRunRequest> _onExecute;
        public DelegatePipeline(Action<LiveRunRequest> onExecute) => _onExecute = onExecute;
        public Task ExecuteAsync(LiveRunRequest request, CancellationToken cancellationToken)
        {
            _onExecute(request);
            return Task.CompletedTask;
        }
    }

    private sealed class AsyncDelegatePipeline : IRunPipeline
    {
        private readonly Func<LiveRunRequest, Task> _onExecute;
        public AsyncDelegatePipeline(Func<LiveRunRequest, Task> onExecute) => _onExecute = onExecute;
        public Task ExecuteAsync(LiveRunRequest request, CancellationToken cancellationToken)
            => _onExecute(request);
    }

    /// <summary>
    /// Pipeline mínima que reporta progresso e conta streams, para
    /// verificar que passos + contadores ficam persistidos com o run.
    /// </summary>
    private sealed class ProgressReportingPipeline : IRunPipeline, ILiveRunProgressAware
    {
        public ILiveRunProgress? Progress { get; set; }

        public async Task ExecuteAsync(LiveRunRequest request, CancellationToken cancellationToken)
        {
            if (Progress is null) return;
            await Progress.EnterPhaseAsync(LiveRunPhase.ReadingTelegram, "a ler", cancellationToken);
            Progress.ReportCounts(c =>
            {
                c.StreamsTested = 3;
                c.StreamsWorking = 2;
            });
        }
    }
}
