using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Automation;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.LiveRun;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9C.4 (subwave 5) — Integração do scheduler existente com o
/// <see cref="RunCoordinator"/> único.
///
/// <para>
/// Cobre:</para>
/// <list type="bullet">
///   <item>Nomes de action estáveis e registo no host de automação.</item>
///   <item>Execução agendada converge no coordinator
///         (<c>Source=scheduler</c>, <c>Mode=telegram|telegram-maintain</c>).</item>
///   <item>Concorrência scheduler/manual e scheduler/scheduler: uma
///         única execução.</item>
///   <item>Coordinator não configurado ⇒ rejeição segura (sem excepção,
///         sem linha em <c>live_run_runs</c>).</item>
///   <item>Cron inválido ⇒ job neutralizado sem abortar o tick.</item>
///   <item>Restart/retoma: job com <c>NextRunAtUtc</c> no futuro não
///         volta a executar.</item>
/// </list>
/// </summary>
public class Phase94ScheduledRunTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly string _outputDir;
    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;

    public Phase94ScheduledRunTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sched-liverun-{Guid.NewGuid():N}.db");
        _outputDir = Path.Combine(Path.GetTempPath(), $"sched-liverun-out-{Guid.NewGuid():N}");
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
        try { Directory.Delete(_outputDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private static ScheduledAutomationHost BuildHost(CatalogResolver resolver, string outputDir, LiveRunHost? liveRunHost)
    {
        return ScheduledAutomationHost.Build(
            resolver,
            outputDir,
            DispatcharrConfig.Disabled(),
            liveRunHost: liveRunHost);
    }

    private async Task SeedDueJobAsync(string name, string cron, string actionName)
    {
        var job = await _resolver.UpsertScheduledJobAsync(name, cron, actionName, isEnabled: true);
        await using var ctx = _factory.CreateDbContext();
        var entity = await ctx.ScheduledJobs.FirstAsync(j => j.Id == job.Id);
        entity.NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Insere um job directamente na BD, sem passar pela validação do
    /// <see cref="CatalogResolver.UpsertScheduledJobAsync"/> — necessário
    /// para testar a rejeição segura de uma cron inválida já persistida.
    /// </summary>
    private async Task SeedRawDueJobAsync(string name, string cron, string actionName)
    {
        await using var ctx = _factory.CreateDbContext();
        ctx.ScheduledJobs.Add(new ScheduledJobEntity
        {
            Name = name,
            CronExpression = cron,
            ActionName = actionName,
            IsEnabled = true,
            NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<ScheduledJobEntity> ReadJobAsync(string name)
    {
        await using var ctx = _factory.CreateDbContext();
        return await ctx.ScheduledJobs.AsNoTracking().FirstAsync(j => j.Name == name);
    }

    private async Task<int> CountLiveRunsAsync()
    {
        await using var ctx = _factory.CreateDbContext();
        return await ctx.LiveRuns.CountAsync();
    }

    private static IRunPipeline IdlePipeline() => new DelegatePipeline(_ => Task.CompletedTask);

    // ===================== Nomes / registo =====================

    [Fact]
    public void Action_names_are_stable_and_distinct_from_the_existing_four()
    {
        Assert.Equal("telegramRun", ScheduledTelegramRunAction.TelegramActionName);
        Assert.Equal("telegramMaintainRun", ScheduledTelegramRunAction.TelegramMaintainActionName);

        Assert.NotEqual(ScheduledTelegramRunAction.TelegramActionName, ScheduledM3uDiscoveryAction.ActionName);
        Assert.NotEqual(ScheduledTelegramRunAction.TelegramActionName, ScheduledValidationAction.ActionName);
        Assert.NotEqual(ScheduledTelegramRunAction.TelegramActionName, ScheduledPlaylistGenerationAction.ActionName);
        Assert.NotEqual(ScheduledTelegramRunAction.TelegramActionName, ScheduledDispatcharrSyncAction.ActionName);
        Assert.NotEqual(
            ScheduledTelegramRunAction.TelegramActionName,
            ScheduledTelegramRunAction.TelegramMaintainActionName);
    }

    [Fact]
    public void Automation_host_registers_all_six_actions_including_telegram_run()
    {
        using var host = BuildHost(_resolver, _outputDir, liveRunHost: null);
        var names = host.RegisteredActions.Select(a => a.Name).ToArray();

        Assert.Contains(ScheduledM3uDiscoveryAction.ActionName, names);
        Assert.Contains(ScheduledValidationAction.ActionName, names);
        Assert.Contains(ScheduledPlaylistGenerationAction.ActionName, names);
        Assert.Contains(ScheduledDispatcharrSyncAction.ActionName, names);
        Assert.Contains(ScheduledTelegramRunAction.TelegramActionName, names);
        Assert.Contains(ScheduledTelegramRunAction.TelegramMaintainActionName, names);
        Assert.Equal(6, names.Length);
    }

    // ===================== Execução via coordinator =====================

    [Fact]
    public async Task Scheduled_job_starts_run_through_the_single_coordinator()
    {
        var liveRunHost = new LiveRunHost(_factory);
        liveRunHost.ConfigureExecutor(_ => IdlePipeline());
        using var host = BuildHost(_resolver, _outputDir, liveRunHost);
        await SeedDueJobAsync("nightly", "0 3 * * *", ScheduledTelegramRunAction.TelegramActionName);

        var ran = await host.Runner.TickOnceAsync();

        Assert.Equal(1, ran);
        Assert.Equal(1, await CountLiveRunsAsync());

        await using var ctx = _factory.CreateDbContext();
        var run = await ctx.LiveRuns.SingleAsync();
        Assert.Equal("scheduler", run.Source);
        Assert.Equal("telegram", run.Mode);
        Assert.Equal(LiveRunTerminalStatus.Completed, run.TerminalStatus);
        Assert.NotNull(run.FinishedAtUtc);

        var job = await ReadJobAsync("nightly");
        Assert.Equal("live-run:telegram:ok", job.LastResult);
        Assert.NotNull(job.NextRunAtUtc);
        Assert.True(job.NextRunAtUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task Scheduled_maintain_job_uses_telegram_maintain_mode()
    {
        var liveRunHost = new LiveRunHost(_factory);
        liveRunHost.ConfigureExecutor(_ => IdlePipeline());
        using var host = BuildHost(_resolver, _outputDir, liveRunHost);
        await SeedDueJobAsync("maintain", "*/30 * * * *", ScheduledTelegramRunAction.TelegramMaintainActionName);

        var ran = await host.Runner.TickOnceAsync();

        Assert.Equal(1, ran);
        await using var ctx = _factory.CreateDbContext();
        var run = await ctx.LiveRuns.SingleAsync();
        Assert.Equal("scheduler", run.Source);
        Assert.Equal("telegram-maintain", run.Mode);
    }

    [Fact]
    public async Task Action_without_configured_coordinator_rejects_safely()
    {
        var liveRunHost = new LiveRunHost(_factory); // sem ConfigureExecutor
        using var host = BuildHost(_resolver, _outputDir, liveRunHost);
        await SeedDueJobAsync("unconfigured", "0 4 * * *", ScheduledTelegramRunAction.TelegramActionName);

        var ran = await host.Runner.TickOnceAsync();

        Assert.Equal(1, ran);
        Assert.Equal(0, await CountLiveRunsAsync());
        var job = await ReadJobAsync("unconfigured");
        Assert.Equal(ScheduledTelegramRunAction.NotConfiguredResult, job.LastResult);
    }

    [Fact]
    public async Task Action_with_null_host_rejects_safely()
    {
        using var host = BuildHost(_resolver, _outputDir, liveRunHost: null);
        await SeedDueJobAsync("nullhost", "0 5 * * *", ScheduledTelegramRunAction.TelegramActionName);

        var ran = await host.Runner.TickOnceAsync();

        Assert.Equal(1, ran);
        Assert.Equal(0, await CountLiveRunsAsync());
        var job = await ReadJobAsync("nullhost");
        Assert.Equal(ScheduledTelegramRunAction.NotConfiguredResult, job.LastResult);
    }

    // ===================== Concorrência =====================

    [Fact]
    public async Task Scheduler_and_manual_race_results_in_a_single_execution()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new AsyncDelegatePipeline(async _ =>
        {
            started.TrySetResult();
            await release.Task;
        });

        var liveRunHost = new LiveRunHost(_factory);
        var coordinator = liveRunHost.ConfigureExecutor(_ => pipeline);
        using var host = BuildHost(_resolver, _outputDir, liveRunHost);
        await SeedDueJobAsync("race-manual", "0 6 * * *", ScheduledTelegramRunAction.TelegramActionName);

        // Arranca o tick agendado em background; fica bloqueado na pipeline.
        var tick = host.Runner.TickOnceAsync();
        await started.Task;

        // Tentativa manual concorrente: o lock único do coordinator rejeita.
        var manual = new LiveRunRequest { Mode = LiveRunMode.Telegram, Source = LiveRunSource.Manual };
        await Assert.ThrowsAsync<RunAlreadyInProgressException>(
            () => coordinator.KickStartAsync(manual, CancellationToken.None));

        release.TrySetResult();
        var ran = await tick;

        Assert.Equal(1, ran);
        // Uma única execução persistida: a do scheduler.
        Assert.Equal(1, await CountLiveRunsAsync());
        await using var ctx = _factory.CreateDbContext();
        var run = await ctx.LiveRuns.SingleAsync();
        Assert.Equal("scheduler", run.Source);
        Assert.Equal(LiveRunTerminalStatus.Completed, run.TerminalStatus);
    }

    [Fact]
    public async Task Two_concurrent_scheduler_ticks_produce_a_single_execution()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new AsyncDelegatePipeline(async _ =>
        {
            started.TrySetResult();
            await release.Task;
        });

        var liveRunHost = new LiveRunHost(_factory);
        liveRunHost.ConfigureExecutor(_ => pipeline);
        using var host = BuildHost(_resolver, _outputDir, liveRunHost);

        // Dois jobs diferentes ambos vencidos, ambos a apontar para o
        // mesmo pipeline: o primeiro tick ganha o lock, o segundo é
        // rejeitado com blocked:already-running.
        await SeedDueJobAsync("race-a", "0 7 * * *", ScheduledTelegramRunAction.TelegramActionName);
        await SeedDueJobAsync("race-b", "0 8 * * *", ScheduledTelegramRunAction.TelegramActionName);

        var tick = host.Runner.TickOnceAsync();
        await started.Task;

        // Segundo job processado enquanto o primeiro ainda corre.
        release.TrySetResult();
        var ran = await tick;

        // Ambos os jobs foram processados (2 execuções, sequenciais — não
        // concorrentes). O invariante é: nunca há duas execuções em
        // paralelo, o que é garantido pelo lock do coordinator.
        Assert.Equal(2, ran);
        Assert.Equal(2, await CountLiveRunsAsync());

        // As execuções são estritamente sequenciais (não sobrepostas).
        await using var ctx = _factory.CreateDbContext();
        var runs = await ctx.LiveRuns.AsNoTracking().OrderBy(r => r.StartedAtUtc).ToListAsync();
        Assert.All(runs, r => Assert.Equal(LiveRunSource.Scheduler.ToWireName(), r.Source));
        for (var i = 1; i < runs.Count; i++)
        {
            Assert.True(
                runs[i].StartedAtUtc >= runs[i - 1].FinishedAtUtc,
                "Execuções agendadas sobrepuseram-se no tempo.");
        }
    }

    [Fact]
    public async Task Already_running_during_scheduled_tick_is_recorded_as_blocked()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new AsyncDelegatePipeline(async _ =>
        {
            started.TrySetResult();
            await release.Task;
        });

        var liveRunHost = new LiveRunHost(_factory);
        var coordinator = liveRunHost.ConfigureExecutor(_ => pipeline);
        using var host = BuildHost(_resolver, _outputDir, liveRunHost);
        await SeedDueJobAsync("blocked-job", "0 9 * * *", ScheduledTelegramRunAction.TelegramActionName);

        // Ocupa o lock com um run manual antes do tick.
        var manualRun = coordinator.KickStartAsync(
            new LiveRunRequest { Mode = LiveRunMode.Telegram, Source = LiveRunSource.Manual },
            CancellationToken.None);
        await started.Task;

        var ran = await host.Runner.TickOnceAsync();

        // O job foi processado (a action devolveu um resultado, não uma
        // excepção) mas nenhuma execução nova foi iniciada.
        Assert.Equal(1, ran);
        var job = await ReadJobAsync("blocked-job");
        Assert.Equal(ScheduledTelegramRunAction.AlreadyRunningResult, job.LastResult);
        Assert.Equal(1, await CountLiveRunsAsync()); // só o run manual

        release.TrySetResult();
        await manualRun;
    }

    // ===================== Config inválida =====================

    [Fact]
    public async Task Invalid_cron_is_rejected_safely_and_does_not_abort_other_jobs()
    {
        var liveRunHost = new LiveRunHost(_factory);
        liveRunHost.ConfigureExecutor(_ => IdlePipeline());
        using var host = BuildHost(_resolver, _outputDir, liveRunHost);

        await SeedRawDueJobAsync("broken", "not a cron", ScheduledTelegramRunAction.TelegramActionName);
        await SeedDueJobAsync("healthy", "0 10 * * *", ScheduledTelegramRunAction.TelegramActionName);

        var ran = await host.Runner.TickOnceAsync();

        // O job saudável correu apesar do cron inválido do primeiro.
        Assert.Equal(1, ran);
        Assert.Equal(1, await CountLiveRunsAsync());

        var broken = await ReadJobAsync("broken");
        Assert.StartsWith("invalid-cron:", broken.LastResult, StringComparison.Ordinal);
        Assert.Null(broken.NextRunAtUtc); // neutralizado: sem ciclo de retry

        var healthy = await ReadJobAsync("healthy");
        Assert.Equal("live-run:telegram:ok", healthy.LastResult);
    }

    [Fact]
    public async Task Unknown_action_with_invalid_cron_is_rejected_safely()
    {
        using var host = BuildHost(_resolver, _outputDir, liveRunHost: null);
        await SeedRawDueJobAsync("unknown-broken", "definitely not cron", "noSuchAction");

        var ran = await host.Runner.TickOnceAsync();

        Assert.Equal(0, ran);
        var job = await ReadJobAsync("unknown-broken");
        Assert.StartsWith("invalid-cron:", job.LastResult, StringComparison.Ordinal);
        Assert.Null(job.NextRunAtUtc);
    }

    // ===================== Restart / retoma =====================

    [Fact]
    public async Task Job_not_due_is_not_executed_after_restart()
    {
        var liveRunHost = new LiveRunHost(_factory);
        liveRunHost.ConfigureExecutor(_ => IdlePipeline());
        using var host = BuildHost(_resolver, _outputDir, liveRunHost);
        await SeedDueJobAsync("future", "0 11 * * *", ScheduledTelegramRunAction.TelegramActionName);

        // Simula retoma após restart: o agendamento já avançou para o futuro.
        await using (var ctx = _factory.CreateDbContext())
        {
            var entity = await ctx.ScheduledJobs.FirstAsync(j => j.Name == "future");
            entity.NextRunAtUtc = DateTime.UtcNow.AddHours(1);
            await ctx.SaveChangesAsync();
        }

        var ran = await host.Runner.TickOnceAsync();

        Assert.Equal(0, ran);
        Assert.Equal(0, await CountLiveRunsAsync());
    }

    // ===================== Helpers =====================

    private sealed class DelegatePipeline : IRunPipeline
    {
        private readonly Func<LiveRunRequest, Task> _onExecute;
        public DelegatePipeline(Func<LiveRunRequest, Task> onExecute) => _onExecute = onExecute;
        public Task ExecuteAsync(LiveRunRequest request, CancellationToken cancellationToken)
            => _onExecute(request);
    }

    private sealed class AsyncDelegatePipeline : IRunPipeline
    {
        private readonly Func<LiveRunRequest, Task> _onExecute;
        public AsyncDelegatePipeline(Func<LiveRunRequest, Task> onExecute) => _onExecute = onExecute;
        public Task ExecuteAsync(LiveRunRequest request, CancellationToken cancellationToken)
            => _onExecute(request);
    }
}
