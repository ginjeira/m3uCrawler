using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.LiveRun;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9C.4 — subwave 3: instrumentação da pipeline, progresso e
/// feed de actividades. Cobre transições de fase, persistência de
/// <see cref="LiveRunStepEntity"/>, contadores tipados, ring buffer de
/// actividades, sanitização, snapshot seguro, excepção e cancelamento.
/// Não cobre endpoints HTTP (subwave 4) nem dashboard (subwave 5).
/// </summary>
public class Phase94LiveRunInstrumentationTests : IClassFixture<LiveRunInstrumentationFixture>
{
    private readonly LiveRunInstrumentationFixture _fixture;

    public Phase94LiveRunInstrumentationTests(LiveRunInstrumentationFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IDbContextFactory<ChannelCatalogDbContext>> NewCatalogAsync()
    {
        await _fixture.ResetAsync();
        return _fixture.Factory;
    }

    private static LiveRunRequest CliTelegramRequest() => new()
    {
        Mode = LiveRunMode.Telegram,
        Source = LiveRunSource.Cli,
    };

    private static RunCoordinator BuildCoordinator(
        IDbContextFactory<ChannelCatalogDbContext> factory,
        IRunPipeline pipeline) => new(factory, _ => pipeline);

    private static async Task<List<LiveRunStepEntity>> StepsAsync(
        IDbContextFactory<ChannelCatalogDbContext> factory)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.LiveRunSteps.OrderBy(s => s.PhaseIndex).ToListAsync();
    }

    // ===================== Fases / steps =====================

    [Fact]
    public async Task Idle_is_persisted_as_the_first_step()
    {
        var factory = await NewCatalogAsync();
        var pipeline = new RecordingPipeline((_, _) => Task.CompletedTask);

        await BuildCoordinator(factory, pipeline).StartAsync(CliTelegramRequest(), CancellationToken.None);

        var steps = await StepsAsync(factory);
        Assert.Equal(LiveRunPhase.Idle, steps[0].Phase);
        Assert.Equal(0, steps[0].PhaseIndex);
        Assert.True(steps[0].PhaseStartedAtUtc > DateTime.MinValue);
    }

    [Fact]
    public async Task Phase_transitions_persist_one_step_per_phase_in_monotonic_order()
    {
        var factory = await NewCatalogAsync();
        var pipeline = new RecordingPipeline(async (progress, ct) =>
        {
            await progress!.EnterPhaseAsync(LiveRunPhase.ReadingTelegram, "reading telegram", ct);
            await progress.EnterPhaseAsync(LiveRunPhase.Discovering, "discovered candidates", ct);
            await progress.EnterPhaseAsync(LiveRunPhase.Downloading, "downloading playlists", ct);
            await progress.EnterPhaseAsync(LiveRunPhase.Analyzing, "analyzing content", ct);
            await progress.EnterPhaseAsync(LiveRunPhase.Validating, "validating streams", ct);
        });

        var outcome = await BuildCoordinator(factory, pipeline)
            .StartAsync(CliTelegramRequest(), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        var steps = await StepsAsync(factory);
        Assert.Equal(
            new[]
            {
                LiveRunPhase.Idle,
                LiveRunPhase.ReadingTelegram,
                LiveRunPhase.Discovering,
                LiveRunPhase.Downloading,
                LiveRunPhase.Analyzing,
                LiveRunPhase.Validating,
                LiveRunPhase.Completed,
            },
            steps.Select(s => s.Phase).ToArray());

        for (var i = 0; i < steps.Count; i++)
        {
            // PhaseIndex coincide com o valor da fase e é estritamente crescente.
            Assert.Equal((int)steps[i].Phase, steps[i].PhaseIndex);
            if (i > 0) Assert.True(steps[i].PhaseIndex > steps[i - 1].PhaseIndex);
        }

        // Cada step é finalizado: o anterior fecha quando a fase seguinte começa.
        Assert.All(steps, s => Assert.NotNull(s.PhaseFinishedAtUtc));
        Assert.All(steps, s => Assert.Equal("ok", s.Result));
    }

    [Fact]
    public async Task Previous_step_is_finalized_when_next_phase_starts()
    {
        var factory = await NewCatalogAsync();
        var pipeline = new RecordingPipeline(async (progress, ct) =>
        {
            await progress!.EnterPhaseAsync(LiveRunPhase.ReadingTelegram, "reading", ct);
            await progress.EnterPhaseAsync(LiveRunPhase.Discovering, "discovering", ct);
        });

        await BuildCoordinator(factory, pipeline)
            .StartAsync(CliTelegramRequest(), CancellationToken.None);

        var steps = await StepsAsync(factory);
        var reading = steps.Single(s => s.Phase == LiveRunPhase.ReadingTelegram);
        var discovering = steps.Single(s => s.Phase == LiveRunPhase.Discovering);

        Assert.NotNull(reading.PhaseFinishedAtUtc);
        Assert.True(reading.PhaseFinishedAtUtc <= discovering.PhaseStartedAtUtc);
    }

    [Fact]
    public async Task Backward_or_repeated_phase_transitions_are_ignored()
    {
        var factory = await NewCatalogAsync();
        var pipeline = new RecordingPipeline(async (progress, ct) =>
        {
            await progress!.EnterPhaseAsync(LiveRunPhase.Validating, "validating", ct);
            await progress.EnterPhaseAsync(LiveRunPhase.Downloading, "stale download", ct);
            await progress.EnterPhaseAsync(LiveRunPhase.Validating, "validating again", ct);
        });

        await BuildCoordinator(factory, pipeline)
            .StartAsync(CliTelegramRequest(), CancellationToken.None);

        var steps = await StepsAsync(factory);
        Assert.DoesNotContain(steps, s => s.Phase == LiveRunPhase.Downloading);
        Assert.Single(steps, s => s.Phase == LiveRunPhase.Validating);
        Assert.Equal(3, steps.Count); // Idle, Validating, Completed
    }

    [Fact]
    public async Task LastMessage_is_persisted_on_phase_transition_and_sanitized()
    {
        var factory = await NewCatalogAsync();
        LiveRunSnapshot? live = null;
        var pipeline = new RecordingPipeline(async (progress, ct) =>
        {
            var monitor = (LiveRunMonitor)progress!;
            await monitor.EnterPhaseAsync(LiveRunPhase.ReadingTelegram, "reading telegram messages", ct);
            monitor.ReportMessage("tested 5/10 streams");
            live = monitor.BuildSnapshot();
        });

        await BuildCoordinator(factory, pipeline)
            .StartAsync(CliTelegramRequest(), CancellationToken.None);

        Assert.Equal("tested 5/10 streams", live!.LastMessage);

        await using var ctx = await factory.CreateDbContextAsync();
        var run = await ctx.LiveRuns.SingleAsync();
        Assert.True(run.UpdatedAtUtc >= run.StartedAtUtc);
        var step = await ctx.LiveRunSteps.SingleAsync(s => s.Phase == LiveRunPhase.ReadingTelegram);
        Assert.Equal("reading telegram messages", step.Message);
    }

    // ===================== Contadores =====================

    [Fact]
    public async Task Counters_mirror_RunReport_for_telegram_playlists_xtream_and_streams()
    {
        var factory = await NewCatalogAsync();
        var report = new RunReport
        {
            DialogsTotal = 4,
            MessagesAnalyzed = 7,
            CandidatesFound = 3,
            PlaylistsDownloaded = 2,
            PlaylistsInvalid = 1,
            PlaylistsRejected = 1,
            CountryMatches = 1,
            PublicationsDiscovered = 4,
            PublicationsResolved = 3,
            XtreamAccountsDiscovered = 5,
            XtreamAccountsForwarded = 2,
            StreamsExtracted = 40,
            StreamsAfterCountryFilter = 30,
            StreamsTested = 12,
            StreamsWorking = 9,
            StreamsFailed = 3,
        };

        LiveRunSnapshot? live = null;
        var pipeline = new RecordingPipeline(async (progress, ct) =>
        {
            var monitor = (LiveRunMonitor)progress!;
            monitor.ReportCounts(report);
            await monitor.EnterPhaseAsync(LiveRunPhase.Validating, "validating", ct);
            live = monitor.BuildSnapshot();
        });

        await BuildCoordinator(factory, pipeline)
            .StartAsync(CliTelegramRequest(), CancellationToken.None);

        var counts = live!.Counts!;
        Assert.Equal(7, counts.MessagesAnalyzed);        // Telegram
        Assert.Equal(3, counts.CandidatesFound);         // Playlists
        Assert.Equal(2, counts.PlaylistsDownloaded);     // Playlists
        Assert.Equal(5, counts.XtreamAccountsDiscovered); // Xtream
        Assert.Equal(30, counts.StreamsAfterCountryFilter); // Streams / lista alvo
        Assert.Equal(9, counts.StreamsWorking);          // Streams

        await using var ctx = await factory.CreateDbContextAsync();
        var run = await ctx.LiveRuns.SingleAsync();
        Assert.Contains("\"messagesAnalyzed\":7", run.CountsJson);
        Assert.Contains("\"xtreamAccountsDiscovered\":5", run.CountsJson);
        Assert.Contains("\"streamsWorking\":9", run.CountsJson);
    }

    [Fact]
    public async Task Dispatcharr_and_target_list_counters_round_trip()
    {
        var factory = await NewCatalogAsync();
        LiveRunSnapshot? live = null;
        var pipeline = new RecordingPipeline(async (progress, ct) =>
        {
            var monitor = (LiveRunMonitor)progress!;
            monitor.ReportCounts(c =>
            {
                c.DispatcharrSyncAttempted++;
                c.DispatcharrSyncCompleted++;
                c.TargetPlaylistEntries = 142;
                c.ExistingPlaylistRetested = 200;
            });
            await monitor.EnterPhaseAsync(LiveRunPhase.Composing, "composing", ct);
            live = monitor.BuildSnapshot();
        });

        await BuildCoordinator(factory, pipeline)
            .StartAsync(CliTelegramRequest(), CancellationToken.None);

        var counts = live!.Counts!;
        Assert.Equal(1, counts.DispatcharrSyncAttempted);
        Assert.Equal(1, counts.DispatcharrSyncCompleted);
        Assert.Equal(142, counts.TargetPlaylistEntries);
        Assert.Equal(200, counts.ExistingPlaylistRetested);

        await using var ctx = await factory.CreateDbContextAsync();
        var run = await ctx.LiveRuns.SingleAsync();
        Assert.Contains("\"targetPlaylistEntries\":142", run.CountsJson);
        Assert.Contains("\"dispatcharrSyncCompleted\":1", run.CountsJson);
    }

    [Fact]
    public async Task Counts_update_incrementally_before_completion()
    {
        var factory = await NewCatalogAsync();
        var observed = new List<int>();
        var pipeline = new RecordingPipeline(async (progress, ct) =>
        {
            var monitor = (LiveRunMonitor)progress!;
            for (var i = 1; i <= 3; i++)
            {
                monitor.ReportCounts(new RunReport { CandidatesFound = i });
                observed.Add(monitor.BuildSnapshot().Counts!.CandidatesFound);
            }
            await monitor.EnterPhaseAsync(LiveRunPhase.Discovering, "discovering", ct);
        });

        await BuildCoordinator(factory, pipeline)
            .StartAsync(CliTelegramRequest(), CancellationToken.None);

        Assert.Equal(new[] { 1, 2, 3 }, observed);
    }

    [Fact]
    public void Mirroring_counts_does_not_mutate_the_source_RunReport()
    {
        var report = new RunReport { MessagesAnalyzed = 5, StreamsWorking = 3 };
        var counts = new LiveRunCounts();
        counts.MirrorFrom(report);

        Assert.Equal(5, counts.MessagesAnalyzed);
        Assert.Equal(3, counts.StreamsWorking);
        Assert.Equal(5, report.MessagesAnalyzed);
        Assert.Equal(3, report.StreamsWorking);

        // Campos não presentes no relatório são preservados entre mirrors.
        counts.TargetPlaylistEntries = 10;
        counts.MirrorFrom(report);
        Assert.Equal(10, counts.TargetPlaylistEntries);
    }

    [Fact]
    public void Counts_json_round_trips_and_tolerates_corruption()
    {
        var counts = new LiveRunCounts
        {
            MessagesAnalyzed = 4,
            DispatcharrSyncSkipped = 2,
            TargetPlaylistEntries = 7,
        };

        var restored = LiveRunCounts.FromJson(counts.ToJson());
        Assert.Equal(4, restored.MessagesAnalyzed);
        Assert.Equal(2, restored.DispatcharrSyncSkipped);
        Assert.Equal(7, restored.TargetPlaylistEntries);

        Assert.Equal(0, LiveRunCounts.FromJson("{not json").MessagesAnalyzed);
        Assert.Equal(0, LiveRunCounts.FromJson(null).MessagesAnalyzed);
    }

    // ===================== Activity feed =====================

    [Fact]
    public void Ring_buffer_keeps_only_the_most_recent_entries()
    {
        var feed = new LiveRunActivityFeed(capacity: 5);
        for (var i = 1; i <= 12; i++)
        {
            feed.Add(new LiveRunActivity(
                DateTime.UtcNow, LiveRunActivityCategory.System, LiveRunActivityLevel.Info,
                i.ToString(System.Globalization.CultureInfo.InvariantCulture), null));
        }

        Assert.Equal(5, feed.Count);
        Assert.Equal(12L, feed.TotalAdded);
        Assert.Equal(
            new[] { "12", "11", "10", "9", "8" },
            feed.Snapshot().Select(a => a.Message).ToArray());
    }

    [Fact]
    public void Ring_buffer_rejects_non_positive_capacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiveRunActivityFeed(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiveRunActivityFeed(-3));
    }

    [Fact]
    public void Ring_buffer_is_thread_safe_under_concurrent_writers()
    {
        var feed = new LiveRunActivityFeed(capacity: 64);
        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 500; i++)
            {
                feed.Add(new LiveRunActivity(
                    DateTime.UtcNow, LiveRunActivityCategory.System, LiveRunActivityLevel.Info, "x", null));
            }
        });

        Assert.Equal(4000L, feed.TotalAdded);
        Assert.Equal(64, feed.Count);
        Assert.Equal(64, feed.Snapshot().Count);
        Assert.All(feed.Snapshot(), a => Assert.Equal("x", a.Message));
    }

    [Fact]
    public async Task Activity_feed_is_not_persisted_and_is_limited_while_run_lives()
    {
        var factory = await NewCatalogAsync();
        var pipeline = new RecordingPipeline(async (progress, ct) =>
        {
            var monitor = (LiveRunMonitor)progress!;
            for (var i = 0; i < 300; i++)
            {
                monitor.ReportActivity(
                    LiveRunActivityCategory.Stream, LiveRunActivityLevel.Info, $"stream {i}");
            }
            await monitor.EnterPhaseAsync(LiveRunPhase.Validating, "validating", ct);
        });

        await BuildCoordinator(factory, pipeline)
            .StartAsync(CliTelegramRequest(), CancellationToken.None);

        // Não existe tabela de actividades — apenas runs/steps.
        await using var ctx = await factory.CreateDbContextAsync();
        var tableNames = await ctx.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='table'")
            .ToListAsync();
        Assert.DoesNotContain(tableNames, n => n.Contains("activit", StringComparison.OrdinalIgnoreCase));
    }

    // ===================== Sanitização / snapshot =====================

    [Fact]
    public async Task Activity_messages_and_metadata_never_leak_credentials()
    {
        var factory = await NewCatalogAsync();
        LiveRunActivity? captured = null;
        var pipeline = new RecordingPipeline(async (progress, ct) =>
        {
            var monitor = (LiveRunMonitor)progress!;
            monitor.ReportActivity(
                LiveRunActivityCategory.Playlist,
                LiveRunActivityLevel.Warning,
                "download failed http://alice:hunter2@example.com/get?username=bob&password=secret",
                new Dictionary<string, string>
                {
                    ["url"] = "http://alice:hunter2@example.com/x?token=abc",
                });
            captured = monitor.ActivitiesSnapshot.First();
            await monitor.EnterPhaseAsync(LiveRunPhase.Analyzing, "analyzing", ct);
        });

        var outcome = await BuildCoordinator(factory, pipeline)
            .StartAsync(CliTelegramRequest(), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.DoesNotContain("hunter2", captured!.Message);
        Assert.DoesNotContain("secret", captured.Message);
        Assert.NotNull(captured.Metadata);
        Assert.DoesNotContain("hunter2", captured.Metadata!["url"]);
        Assert.DoesNotContain("token=abc", captured.Metadata["url"]);

        Assert.DoesNotContain("hunter2", outcome.Snapshot.LastMessage ?? string.Empty);
        foreach (var activity in outcome.Snapshot.RecentActivities)
        {
            Assert.DoesNotContain("hunter2", activity.Message);
        }
    }

    [Fact]
    public async Task Snapshot_last_message_and_step_messages_are_sanitized()
    {
        var factory = await NewCatalogAsync();
        LiveRunSnapshot? live = null;
        var pipeline = new RecordingPipeline(async (progress, ct) =>
        {
            var monitor = (LiveRunMonitor)progress!;
            await monitor.EnterPhaseAsync(
                LiveRunPhase.Validating,
                "playlist http://bob:secret@host/live/user/pass/1.ts failed",
                ct);
            live = monitor.BuildSnapshot();
        });

        await BuildCoordinator(factory, pipeline)
            .StartAsync(CliTelegramRequest(), CancellationToken.None);

        Assert.True(live!.Sanitized);
        Assert.DoesNotContain("secret", live.LastMessage ?? string.Empty);
        Assert.Contains("/***/***", live.LastMessage ?? string.Empty);

        var steps = await StepsAsync(factory);
        var validating = steps.Single(s => s.Phase == LiveRunPhase.Validating);
        Assert.DoesNotContain("secret", validating.Message ?? string.Empty);
    }

    [Fact]
    public async Task Snapshot_has_no_raw_urls_titles_or_secrets()
    {
        var factory = await NewCatalogAsync();
        var pipeline = new RecordingPipeline(async (progress, ct) =>
        {
            await progress!.EnterPhaseAsync(LiveRunPhase.Validating, "validating streams", ct);
            progress.ReportCounts(new RunReport { StreamsTested = 4, StreamsWorking = 2 });
        });

        var outcome = await BuildCoordinator(factory, pipeline)
            .StartAsync(CliTelegramRequest(), CancellationToken.None);

        var snapshot = outcome.Snapshot;
        Assert.True(snapshot.Sanitized);
        Assert.NotNull(snapshot.Counts);
        Assert.NotNull(snapshot.LastMessage);
        Assert.DoesNotContain("http://", snapshot.LastMessage!);
        Assert.All(snapshot.RecentActivities, a =>
        {
            Assert.DoesNotContain("http://", a.Message);
        });
    }

    // ===================== Falha / cancelamento =====================

    [Fact]
    public async Task Pipeline_exception_creates_error_step_and_sanitized_last_message()
    {
        var factory = await NewCatalogAsync();
        var leaky = new InvalidOperationException(
            "boom http://user:hunter2@example.com/p?username=bob&password=secret");
        var pipeline = new RecordingPipeline(async (progress, ct) =>
        {
            await progress!.EnterPhaseAsync(LiveRunPhase.Validating, "validating", ct);
            throw leaky;
        });

        var outcome = await BuildCoordinator(factory, pipeline)
            .StartAsync(CliTelegramRequest(), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Snapshot.IsRunning);

        var steps = await StepsAsync(factory);
        var error = steps.Single(s => s.Phase == LiveRunPhase.Error);
        Assert.Equal("error", error.Result);
        Assert.NotNull(error.PhaseFinishedAtUtc);

        await using var ctx = await factory.CreateDbContextAsync();
        var run = await ctx.LiveRuns.SingleAsync();
        Assert.Equal(LiveRunTerminalStatus.Failed, run.TerminalStatus);
        Assert.NotNull(run.FinishedAtUtc);
        Assert.DoesNotContain("hunter2", run.LastMessage ?? string.Empty);
    }

    [Fact]
    public async Task Cancellation_persists_finished_error_run_and_releases_lock()
    {
        var factory = await NewCatalogAsync();
        var entered = new TaskCompletionSource();
        var invocation = 0;
        var pipeline = new RecordingPipeline(async (progress, ct) =>
        {
            if (Interlocked.Increment(ref invocation) == 1)
            {
                await progress!.EnterPhaseAsync(LiveRunPhase.Validating, "validating", ct);
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            else
            {
                await progress!.EnterPhaseAsync(LiveRunPhase.ReadingTelegram, "second run", ct);
            }
        });

        var coordinator = BuildCoordinator(factory, pipeline);
        using var cts = new CancellationTokenSource();
        var task = coordinator.StartAsync(CliTelegramRequest(), cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Assert.False(coordinator.IsRunning);
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            var run = await ctx.LiveRuns.SingleAsync();
            Assert.Equal(LiveRunTerminalStatus.Failed, run.TerminalStatus);
            Assert.NotNull(run.FinishedAtUtc);
            Assert.Equal("cancelled", run.LastMessage);
        }

        var steps = await StepsAsync(factory);
        Assert.Contains(steps, s => s.Phase == LiveRunPhase.Error && s.Result == "error");

        // Lock libertado: um novo run arranca.
        var second = await coordinator.StartAsync(CliTelegramRequest(), CancellationToken.None);
        Assert.True(second.Succeeded);
    }

    // ===================== Compatibilidade / regressão =====================

    [Fact]
    public async Task Plain_pipeline_without_progress_support_still_completes()
    {
        var factory = await NewCatalogAsync();
        var coordinator = BuildCoordinator(factory, new PlainPipeline());

        var outcome = await coordinator.StartAsync(CliTelegramRequest(), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        var steps = await StepsAsync(factory);
        Assert.Equal(new[] { LiveRunPhase.Idle, LiveRunPhase.Completed }, steps.Select(s => s.Phase).ToArray());
    }

    [Fact]
    public async Task Progress_aware_pipeline_is_invoked_once_per_run()
    {
        var factory = await NewCatalogAsync();
        var pipeline = new RecordingPipeline((_, _) => Task.CompletedTask);
        var coordinator = BuildCoordinator(factory, pipeline);

        for (var i = 0; i < 3; i++)
        {
            await coordinator.StartAsync(CliTelegramRequest(), CancellationToken.None);
        }

        Assert.Equal(3, pipeline.Invocations);
    }

    [Fact]
    public async Task TelegramRunPipeline_passes_injected_progress_to_invoker()
    {
        ILiveRunProgress? received = null;
        var pipeline = new TelegramRunPipeline((request, progress, ct) =>
        {
            received = progress;
            return Task.CompletedTask;
        });

        var fake = NullLiveRunProgress.Instance;
        pipeline.Progress = fake;
        await pipeline.ExecuteAsync(CliTelegramRequest(), CancellationToken.None);

        Assert.Same(fake, received);
    }

    [Fact]
    public async Task Null_progress_is_a_no_op()
    {
        await NullLiveRunProgress.Instance.EnterPhaseAsync(LiveRunPhase.Validating);
        NullLiveRunProgress.Instance.ReportCounts(new RunReport { MessagesAnalyzed = 9 });
        NullLiveRunProgress.Instance.ReportCounts(c => c.TargetPlaylistEntries++);
        NullLiveRunProgress.Instance.ReportMessage("ignored");
        NullLiveRunProgress.Instance.ReportActivity(
            LiveRunActivityCategory.System, LiveRunActivityLevel.Info, "ignored");

        Assert.Equal(string.Empty, NullLiveRunProgress.Instance.RunId);
    }

    // ===================== Helpers =====================

    private sealed class RecordingPipeline : IRunPipeline, ILiveRunProgressAware
    {
        private readonly Func<ILiveRunProgress?, CancellationToken, Task> _body;

        public RecordingPipeline(Func<ILiveRunProgress?, CancellationToken, Task> body) => _body = body;

        public ILiveRunProgress? Progress { get; set; }

        public int Invocations { get; private set; }

        public Task ExecuteAsync(LiveRunRequest request, CancellationToken cancellationToken)
        {
            Invocations++;
            return _body(Progress, cancellationToken);
        }
    }

    private sealed class PlainPipeline : IRunPipeline
    {
        public Task ExecuteAsync(LiveRunRequest request, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}

/// <summary>
/// Fixture partilhada pela classe de testes da subwave 3: cria o
/// schema uma única vez (EnsureCreated) e limpa as tabelas de Live Run
/// entre testes. Evita reconstruir o catálogo completo por teste, que
/// adicionaria carga desnecessária à suite.
/// </summary>
public sealed class LiveRunInstrumentationFixture : IAsyncLifetime
{
    private string _dbPath = string.Empty;

    public IDbContextFactory<ChannelCatalogDbContext> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"phase94-instr-fixture-{Guid.NewGuid():N}.db");
        Factory = new TestDbContextFactory(_dbPath);
        await using var ctx = await Factory.CreateDbContextAsync();
        await ctx.Database.EnsureCreatedAsync();
    }

    public async Task ResetAsync()
    {
        await using var ctx = await Factory.CreateDbContextAsync();
        await ctx.Database.ExecuteSqlRawAsync("DELETE FROM live_run_steps");
        await ctx.Database.ExecuteSqlRawAsync("DELETE FROM live_run_runs");
    }

    public Task DisposeAsync()
    {
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
        catch
        {
            // Ficheiro temporário; a limpeza é best-effort.
        }
        return Task.CompletedTask;
    }
}
