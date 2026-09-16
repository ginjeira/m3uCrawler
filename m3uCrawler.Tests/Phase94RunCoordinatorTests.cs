using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.LiveRun;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9C.4 — subwave 2: tests do <see cref="RunCoordinator"/>.
/// Cobre lifecycle, lock atómico, persistência de Source/Mode,
/// recuperação de runs interrompidos e janela de 24h. Não cobre
/// instrumentação da pipeline (subwave 3) nem API (subwave 4).
/// </summary>
public class Phase94RunCoordinatorTests
{
    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), $"phase94-coord-{Guid.NewGuid():N}.db");

    private static async Task<IDbContextFactory<ChannelCatalogDbContext>> NewCatalogAsync()
    {
        var path = NewDbPath();
        var bootstrapper = new ChannelCatalogBootstrapper(path);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        return new TestDbContextFactory(path);
    }

    private static IRunPipeline Pipeline(Action? onExecute = null, Exception? throwOnExecute = null)
    {
        if (throwOnExecute is not null)
        {
            return new ThrowingPipeline(throwOnExecute);
        }
        return new DelegatePipeline(_ => onExecute?.Invoke());
    }

    private static RunCoordinator BuildCoordinator(
        IDbContextFactory<ChannelCatalogDbContext> factory,
        IRunPipeline pipeline) =>
        new(factory, _ => pipeline);

    private static LiveRunRequest CliTelegramRequest() => new()
    {
        Mode = LiveRunMode.Telegram,
        Source = LiveRunSource.Cli,
    };

    // ===================== Lifecycle básico =====================

    [Fact]
    public async Task StartAsync_persists_live_run_with_unknown_terminal_status()
    {
        var factory = await NewCatalogAsync();
        var coordinator = BuildCoordinator(factory, Pipeline());

        var outcome = await coordinator.StartAsync(CliTelegramRequest(), CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.False(outcome.Snapshot.IsRunning);
        Assert.Equal(LiveRunTerminalStatus.Completed, outcome.Snapshot.TerminalStatus);
        Assert.NotNull(outcome.Snapshot.FinishedAtUtc);
        Assert.False(coordinator.IsRunning);

        await using var ctx = await factory.CreateDbContextAsync();
        var loaded = await ctx.LiveRuns.SingleAsync();
        Assert.Equal(outcome.Snapshot.RunId, loaded.RunId);
        Assert.Equal("telegram", loaded.Mode);
        Assert.Equal("cli", loaded.Source);
        Assert.Equal(LiveRunTerminalStatus.Completed, loaded.TerminalStatus);
    }

    [Fact]
    public async Task StartAsync_records_cli_source_for_cli_request()
    {
        var factory = await NewCatalogAsync();
        var coordinator = BuildCoordinator(factory, Pipeline());

        await coordinator.StartAsync(CliTelegramRequest(), CancellationToken.None);

        await using var ctx = await factory.CreateDbContextAsync();
        var loaded = await ctx.LiveRuns.SingleAsync();
        Assert.Equal("cli", loaded.Source);
        Assert.Equal("telegram", loaded.Mode);
    }

    [Fact]
    public async Task StartAsync_records_scheduler_source_for_scheduled_request()
    {
        var factory = await NewCatalogAsync();
        var coordinator = BuildCoordinator(factory, Pipeline());

        await coordinator.StartAsync(new LiveRunRequest
        {
            Mode = LiveRunMode.TelegramMaintain,
            Source = LiveRunSource.Scheduler,
        }, CancellationToken.None);

        await using var ctx = await factory.CreateDbContextAsync();
        var loaded = await ctx.LiveRuns.SingleAsync();
        Assert.Equal("scheduler", loaded.Source);
        Assert.Equal("telegram-maintain", loaded.Mode);
    }

    [Fact]
    public async Task StartAsync_records_manual_source_for_manual_request()
    {
        var factory = await NewCatalogAsync();
        var coordinator = BuildCoordinator(factory, Pipeline());

        await coordinator.StartAsync(new LiveRunRequest
        {
            Mode = LiveRunMode.Telegram,
            Source = LiveRunSource.Manual,
        }, CancellationToken.None);

        await using var ctx = await factory.CreateDbContextAsync();
        var loaded = await ctx.LiveRuns.SingleAsync();
        Assert.Equal("manual", loaded.Source);
    }

    [Fact]
    public async Task Each_run_gets_a_unique_RunId()
    {
        var factory = await NewCatalogAsync();
        var coordinator = BuildCoordinator(factory, Pipeline());

        var ids = new HashSet<string>();
        for (int i = 0; i < 5; i++)
        {
            var outcome = await coordinator.StartAsync(CliTelegramRequest(), CancellationToken.None);
            Assert.True(ids.Add(outcome.Snapshot.RunId));
        }

        await using var ctx = await factory.CreateDbContextAsync();
        Assert.Equal(5, await ctx.LiveRuns.CountAsync());
        Assert.Equal(5, await ctx.LiveRuns.Select(r => r.RunId).Distinct().CountAsync());
    }

    // ===================== Lock / concorrência =====================

    [Fact]
    public async Task Second_concurrent_call_throws_RunAlreadyInProgressException()
    {
        var factory = await NewCatalogAsync();
        var release = new TaskCompletionSource();
        var pipeline = new AsyncDelegatePipeline(async _ => await release.Task);
        var coordinator = BuildCoordinator(factory, pipeline);

        var first = coordinator.StartAsync(CliTelegramRequest(), CancellationToken.None);
        // Dá tempo à primeira execução para entrar no pipeline e
        // para o flag CAS estar definitivamente em "running".
        await WaitUntilAsync(() => coordinator.IsRunning, TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<RunAlreadyInProgressException>(
            () => coordinator.StartAsync(CliTelegramRequest(), CancellationToken.None));

        release.SetResult();
        var firstOutcome = await first;
        Assert.True(firstOutcome.Succeeded);
    }

    [Fact]
    public async Task After_first_run_finishes_lock_is_released()
    {
        var factory = await NewCatalogAsync();
        var coordinator = BuildCoordinator(factory, Pipeline());

        var first = await coordinator.StartAsync(CliTelegramRequest(), CancellationToken.None);
        Assert.True(first.Succeeded);

        var second = await coordinator.StartAsync(CliTelegramRequest(), CancellationToken.None);
        Assert.True(second.Succeeded);

        await using var ctx = await factory.CreateDbContextAsync();
        Assert.Equal(2, await ctx.LiveRuns.CountAsync());
    }

    [Fact]
    public async Task Scheduler_and_manual_concurrent_call_only_one_wins()
    {
        var factory = await NewCatalogAsync();
        var release = new TaskCompletionSource();
        var pipeline = new AsyncDelegatePipeline(async _ => await release.Task);
        var coordinator = BuildCoordinator(factory, pipeline);

        var schedulerTask = coordinator.StartAsync(new LiveRunRequest
        {
            Mode = LiveRunMode.Telegram,
            Source = LiveRunSource.Scheduler,
        }, CancellationToken.None);

        var manualTask = coordinator.StartAsync(new LiveRunRequest
        {
            Mode = LiveRunMode.Telegram,
            Source = LiveRunSource.Manual,
        }, CancellationToken.None);

        // Um dos dois perde imediatamente a corrida pelo lock
        // (o outro mantém-se dentro do pipeline à espera do release).
        await WaitUntilAsync(
            () => schedulerTask.IsFaulted || manualTask.IsFaulted,
            TimeSpan.FromSeconds(5));

        Assert.True(
            schedulerTask.IsFaulted ^ manualTask.IsFaulted,
            "exactamente uma das chamadas concorrentes deve falhar com RunAlreadyInProgressException");

        var loserTask = schedulerTask.IsFaulted ? schedulerTask : manualTask;
        var loserException = await Record.ExceptionAsync(() => loserTask);
        Assert.IsType<RunAlreadyInProgressException>(loserException);

        // Libertar a vencedora e esperar ambas sem propagar a falha
        // previsível da perdedora.
        release.SetResult();
        await Record.ExceptionAsync(() => schedulerTask);
        await Record.ExceptionAsync(() => manualTask);

        await using var ctx = await factory.CreateDbContextAsync();
        // Só 1 run persistido (o vencedor).
        Assert.Equal(1, await ctx.LiveRuns.CountAsync());
    }

    [Fact]
    public async Task Pipeline_invoked_exactly_once_per_run()
    {
        var factory = await NewCatalogAsync();
        var counter = 0;
        var pipeline = new DelegatePipeline(_ => Interlocked.Increment(ref counter));
        var coordinator = BuildCoordinator(factory, pipeline);

        for (int i = 0; i < 3; i++)
        {
            await coordinator.StartAsync(CliTelegramRequest(), CancellationToken.None);
        }
        Assert.Equal(3, counter);
    }

    // ===================== Falhas / exception =====================

    [Fact]
    public async Task Pipeline_exception_marks_run_as_failed_and_releases_lock()
    {
        var factory = await NewCatalogAsync();
        var shouldThrow = true;
        var pipeline = new AsyncDelegatePipeline(_ =>
            shouldThrow
                ? Task.FromException(new InvalidOperationException("boom"))
                : Task.CompletedTask);
        var coordinator = BuildCoordinator(factory, pipeline);

        var outcome = await coordinator.StartAsync(CliTelegramRequest(), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(LiveRunTerminalStatus.Failed, outcome.Snapshot.TerminalStatus);
        Assert.False(coordinator.IsRunning);

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            var loaded = await ctx.LiveRuns.SingleAsync();
            Assert.Equal(LiveRunTerminalStatus.Failed, loaded.TerminalStatus);
            Assert.NotNull(loaded.FinishedAtUtc);
            Assert.Equal("failed: pipeline exception", loaded.LastMessage);
        }

        // O lock foi efectivamente libertado: uma nova execução
        // (agora bem-sucedida) tem de arrancar.
        shouldThrow = false;
        var second = await coordinator.StartAsync(CliTelegramRequest(), CancellationToken.None);
        Assert.True(second.Succeeded);

        await using var read = await factory.CreateDbContextAsync();
        Assert.Equal(2, await read.LiveRuns.CountAsync());
    }

    [Fact]
    public async Task Pipeline_exception_does_not_leak_credentials_in_lastMessage()
    {
        var factory = await NewCatalogAsync();
        // Simula uma excepção de pipeline que poderia conter URLs.
        var leakyException = new InvalidOperationException(
            "GET http://user:hunter2@example.com/playlist.m3u returned 500");
        var coordinator = BuildCoordinator(factory, Pipeline(throwOnExecute: leakyException));

        var outcome = await coordinator.StartAsync(CliTelegramRequest(), CancellationToken.None);

        await using var ctx = await factory.CreateDbContextAsync();
        var loaded = await ctx.LiveRuns.SingleAsync();
        // A mensagem persistida NÃO deve conter a URL com credenciais.
        Assert.DoesNotContain("hunter2", loaded.LastMessage ?? string.Empty);
        Assert.DoesNotContain("example.com", loaded.LastMessage ?? string.Empty);
        Assert.Equal(LiveRunTerminalStatus.Failed, outcome.Snapshot.TerminalStatus);
    }

    // ===================== Recovery (restart/crash) =====================

    [Fact]
    public async Task RecoverInterruptedRunsAsync_marks_unfinished_runs_as_failed()
    {
        var factory = await NewCatalogAsync();
        var now = DateTime.UtcNow;

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.LiveRuns.Add(new LiveRunEntity
            {
                RunId = Guid.NewGuid().ToString(),
                Mode = "telegram",
                Source = "cli",
                StartedAtUtc = now.AddMinutes(-30),
                FinishedAtUtc = null,
                TerminalStatus = LiveRunTerminalStatus.Unknown,
                CountsJson = "{}",
                CreatedAtUtc = now.AddMinutes(-30),
                UpdatedAtUtc = now.AddMinutes(-30),
            });
            await ctx.SaveChangesAsync();
        }

        var coordinator = BuildCoordinator(factory, Pipeline());
        var recovered = await coordinator.RecoverInterruptedRunsAsync(CancellationToken.None);

        Assert.Equal(1, recovered);

        await using var read = await factory.CreateDbContextAsync();
        var loaded = await read.LiveRuns.SingleAsync();
        Assert.Equal(LiveRunTerminalStatus.Failed, loaded.TerminalStatus);
        Assert.NotNull(loaded.FinishedAtUtc);
        Assert.Contains("recovered after restart", loaded.LastMessage ?? string.Empty);
    }

    [Fact]
    public async Task RecoverInterruptedRunsAsync_does_not_touch_finished_runs()
    {
        var factory = await NewCatalogAsync();
        var completedAt = DateTime.UtcNow.AddMinutes(-5);

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.LiveRuns.Add(new LiveRunEntity
            {
                RunId = Guid.NewGuid().ToString(),
                Mode = "telegram",
                Source = "cli",
                StartedAtUtc = completedAt.AddSeconds(-30),
                FinishedAtUtc = completedAt,
                TerminalStatus = LiveRunTerminalStatus.Completed,
                LastMessage = "ok",
                CountsJson = "{}",
                CreatedAtUtc = completedAt.AddSeconds(-30),
                UpdatedAtUtc = completedAt,
            });
            await ctx.SaveChangesAsync();
        }

        var coordinator = BuildCoordinator(factory, Pipeline());
        var recovered = await coordinator.RecoverInterruptedRunsAsync(CancellationToken.None);

        Assert.Equal(0, recovered);

        await using var read = await factory.CreateDbContextAsync();
        var loaded = await read.LiveRuns.SingleAsync();
        Assert.Equal(LiveRunTerminalStatus.Completed, loaded.TerminalStatus);
        Assert.Equal("ok", loaded.LastMessage);
    }

    [Fact]
    public async Task RecoverInterruptedRunsAsync_preserves_previous_history()
    {
        var factory = await NewCatalogAsync();
        var coordinator = BuildCoordinator(factory, Pipeline());

        // Duas execuções concluídas.
        await coordinator.StartAsync(CliTelegramRequest(), CancellationToken.None);
        await coordinator.StartAsync(CliTelegramRequest(), CancellationToken.None);

        // Simular uma interrupção (entrada órfã directa na BD).
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.LiveRuns.Add(new LiveRunEntity
            {
                RunId = Guid.NewGuid().ToString(),
                Mode = "telegram",
                Source = "manual",
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-60),
                FinishedAtUtc = null,
                TerminalStatus = LiveRunTerminalStatus.Unknown,
                CountsJson = "{}",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-60),
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-60),
            });
            await ctx.SaveChangesAsync();
        }

        await coordinator.RecoverInterruptedRunsAsync(CancellationToken.None);

        await using var read = await factory.CreateDbContextAsync();
        Assert.Equal(3, await read.LiveRuns.CountAsync());
        Assert.Equal(2, await read.LiveRuns.CountAsync(r => r.TerminalStatus == LiveRunTerminalStatus.Completed));
        Assert.Equal(1, await read.LiveRuns.CountAsync(r => r.TerminalStatus == LiveRunTerminalStatus.Failed));
    }

    [Fact]
    public async Task RecoverInterruptedRunsAsync_is_idempotent()
    {
        var factory = await NewCatalogAsync();
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.LiveRuns.Add(new LiveRunEntity
            {
                RunId = Guid.NewGuid().ToString(),
                Mode = "telegram",
                Source = "cli",
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                FinishedAtUtc = null,
                TerminalStatus = LiveRunTerminalStatus.Unknown,
                CountsJson = "{}",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            });
            await ctx.SaveChangesAsync();
        }

        var coordinator = BuildCoordinator(factory, Pipeline());
        var first = await coordinator.RecoverInterruptedRunsAsync(CancellationToken.None);
        var second = await coordinator.RecoverInterruptedRunsAsync(CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(0, second); // Já não há nada por recuperar.
    }

    // ===================== Janela de 24h =====================

    [Fact]
    public async Task GetRecentFinishedSnapshotAsync_returns_recent_runs()
    {
        var factory = await NewCatalogAsync();
        var coordinator = BuildCoordinator(factory, Pipeline());

        await coordinator.StartAsync(CliTelegramRequest(), CancellationToken.None);

        var snapshot = await coordinator.GetRecentFinishedSnapshotAsync(CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Equal(LiveRunTerminalStatus.Completed, snapshot!.TerminalStatus);
        Assert.False(snapshot.IsRunning);
    }

    [Fact]
    public async Task GetRecentFinishedSnapshotAsync_returns_null_when_last_run_is_older_than_24h()
    {
        var factory = await NewCatalogAsync();
        var longAgo = DateTime.UtcNow.AddHours(-25);

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.LiveRuns.Add(new LiveRunEntity
            {
                RunId = Guid.NewGuid().ToString(),
                Mode = "telegram",
                Source = "cli",
                StartedAtUtc = longAgo,
                FinishedAtUtc = longAgo.AddSeconds(10),
                TerminalStatus = LiveRunTerminalStatus.Completed,
                LastMessage = "ok",
                CountsJson = "{}",
                CreatedAtUtc = longAgo,
                UpdatedAtUtc = longAgo.AddSeconds(10),
            });
            await ctx.SaveChangesAsync();
        }

        var coordinator = BuildCoordinator(factory, Pipeline());
        var snapshot = await coordinator.GetRecentFinishedSnapshotAsync(CancellationToken.None);

        Assert.Null(snapshot); // >24h → não conta como "recent".
    }

    // ===================== Concorrência real (burst) =====================

    [Fact]
    public async Task Concurrent_burst_of_StartAsync_only_one_wins()
    {
        var factory = await NewCatalogAsync();
        var release = new TaskCompletionSource();
        var entered = 0;
        var pipeline = new AsyncDelegatePipeline(async _ =>
        {
            Interlocked.Increment(ref entered);
            await release.Task;
        });
        var coordinator = BuildCoordinator(factory, pipeline);

        const int callers = 16;
        var tasks = new List<Task<LiveRunOutcome>>();
        for (int i = 0; i < callers; i++)
        {
            // Source alternado para provar que a origem não influencia
            // o direito ao lock.
            var source = i % 2 == 0 ? LiveRunSource.Scheduler : LiveRunSource.Manual;
            tasks.Add(coordinator.StartAsync(new LiveRunRequest
            {
                Mode = LiveRunMode.Telegram,
                Source = source,
            }, CancellationToken.None));
        }

        // Esperar que exactamente uma execução tenha entrado no pipeline.
        await WaitUntilAsync(() => Volatile.Read(ref entered) == 1, TimeSpan.FromSeconds(5));

        // As restantes 15 já falharam com RunAlreadyInProgressException.
        var faulted = 0;
        foreach (var t in tasks)
        {
            if (t.IsFaulted)
            {
                var ex = await Record.ExceptionAsync(() => t);
                Assert.IsType<RunAlreadyInProgressException>(ex);
                faulted++;
            }
        }
        Assert.Equal(callers - 1, faulted);

        release.SetResult();
        foreach (var t in tasks)
        {
            await Record.ExceptionAsync(() => t);
        }

        Assert.Equal(1, Volatile.Read(ref entered));

        await using var ctx = await factory.CreateDbContextAsync();
        Assert.Equal(1, await ctx.LiveRuns.CountAsync());
    }

    // ===================== Wire names (Source/Mode) =====================

    [Fact]
    public void Wire_names_match_the_frozen_contract()
    {
        Assert.Equal("telegram", LiveRunMode.Telegram.ToWireName());
        Assert.Equal("telegram-maintain", LiveRunMode.TelegramMaintain.ToWireName());
        Assert.Equal("cli", LiveRunSource.Cli.ToWireName());
        Assert.Equal("scheduler", LiveRunSource.Scheduler.ToWireName());
        Assert.Equal("manual", LiveRunSource.Manual.ToWireName());
    }

    [Fact]
    public void Wire_name_parsing_normalises_unknown_values_to_defaults()
    {
        Assert.Equal(LiveRunMode.TelegramMaintain, LiveRunWireNames.ParseMode("telegram-maintain"));
        Assert.Equal(LiveRunMode.TelegramMaintain, LiveRunWireNames.ParseMode("TELEGRAM-MAINTAIN"));
        Assert.Equal(LiveRunMode.Telegram, LiveRunWireNames.ParseMode("telegram"));
        // Desconhecidos e null caem no default documentado.
        Assert.Equal(LiveRunMode.Telegram, LiveRunWireNames.ParseMode("running"));
        Assert.Equal(LiveRunMode.Telegram, LiveRunWireNames.ParseMode(null));

        Assert.Equal(LiveRunSource.Scheduler, LiveRunWireNames.ParseSource("scheduler"));
        Assert.Equal(LiveRunSource.Manual, LiveRunWireNames.ParseSource("Manual"));
        Assert.Equal(LiveRunSource.Cli, LiveRunWireNames.ParseSource("cli"));
        Assert.Equal(LiveRunSource.Cli, LiveRunWireNames.ParseSource("stale"));
        Assert.Equal(LiveRunSource.Cli, LiveRunWireNames.ParseSource(null));
    }

    [Fact]
    public async Task Snapshot_round_trips_mode_and_source_through_persistence()
    {
        var factory = await NewCatalogAsync();
        var coordinator = BuildCoordinator(factory, Pipeline());

        var outcome = await coordinator.StartAsync(new LiveRunRequest
        {
            Mode = LiveRunMode.TelegramMaintain,
            Source = LiveRunSource.Scheduler,
        }, CancellationToken.None);

        Assert.Equal(LiveRunMode.TelegramMaintain, outcome.Snapshot.Mode);
        Assert.Equal(LiveRunSource.Scheduler, outcome.Snapshot.Source);

        var recent = await coordinator.GetRecentFinishedSnapshotAsync(CancellationToken.None);
        Assert.NotNull(recent);
        Assert.Equal(LiveRunMode.TelegramMaintain, recent!.Mode);
        Assert.Equal(LiveRunSource.Scheduler, recent.Source);
    }

    // ===================== Helpers =====================

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Predicate did not become true within {timeout}.");
            await Task.Delay(10);
        }
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

    private sealed class ThrowingPipeline : IRunPipeline
    {
        private readonly Exception _exception;
        public ThrowingPipeline(Exception exception) => _exception = exception;
        public Task ExecuteAsync(LiveRunRequest request, CancellationToken cancellationToken)
            => throw _exception;
    }
}
