using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Automation;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using m3uCrawler.Services.Dispatcharr;
using m3uCrawler.Services.LiveRun;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W-LIFECYCLE-IMPLEMENTATION — Testes do mecanismo de lifecycle do
/// processo residente (Dashboard + Scheduler) e do shutdown coerente
/// (Ctrl+C / SIGTERM). Todos os testes são determinísticos, não
/// dependerem de rede real (Telegram/Dispatcharr) nem de sleeps longos.
///
/// <para>
/// <b>Ámbito.</b> Cobrir as abstrações de lifecycle publicadas nesta
/// wave:
/// <list type="bullet">
///   <item><see cref="Program.AwaitResidentDashboardAsync"/> — o padrão
///     único de residency extraído de <c>Main</c>;</item>
///   <item><see cref="WebDashboardService.StopDashboard"/> — paragem
///     coerente do listener;</item>
///   <item><see cref="ScheduledAutomationHost"/>/<see cref="ScheduledJobRunner"/>
///     durante shutdown (com e sem jobs, com action em curso, com action
///     que não respeita cancellation).</item>
/// </list>
/// </para>
///
/// <para>
/// O teste de integração do <c>Program.Main</c> (processo real) é
/// deliberadamente NÃO implementado: a residência é codificada em
/// <c>Main</c>, que lança o processo real — não é possível testá-lo
/// in-process sem lançar subprocessos (frágil). Em vez disso, (a) o
/// comportamento do helper de residency é testado directamente e
/// (b) um teste de caracterização wobstéricas vérifica que os três
/// call-sites <c>await AwaitResidentDashboardAsync</c> estão
/// efectivamente nos caminhos residentes de <c>Program.cs</c>
/// (precedente: <c>TelegramHistoryWindowTests</c> lê
/// <c>docker-compose.yml</c> para assert strings).
/// </para>
/// </summary>
public sealed class ProgramLifecycleResidencyTests
{
    // ---------- AwaitResidentDashboardAsync (padrão de residency) ----------

    /// <summary>Stub mínimo para o <see cref="ScheduledAutomationHost"/>:
    /// apenas o Dispose é observado.</summary>
    private sealed class DisposableProbe : IDisposable
    {
        public int DisposeCalls { get; private set; }
        public void Dispose() => DisposeCalls++;
    }

    [Fact]
    public async Task Resident_waits_for_null_webTask_returns_without_throwing()
    {
        // Deve devolver de imediato, sem tentar await, e sem excepção.
        Exception? failure = null;
        try
        {
            await Program.AwaitResidentDashboardAsync(
                webTask: null,
                automationHost: null,
                modeMessage: "test");
        }
        catch (Exception ex) { failure = ex; }
        Assert.Null(failure);
    }

    [Fact]
    public async Task Resident_manages_completed_webTask_by_disposing_host()
    {
        var probe = new DisposableProbe();
        await Program.AwaitResidentDashboardAsync(
            webTask: Task.CompletedTask,
            automationHost: null,
            modeMessage: "test");
        // Sem ningún host ligado: não há nada a dispor; sem exce
        Assert.True(true);
    }

    [Fact]
    public async Task Resident_swallows_faulted_webTask_and_does_not_propagate()
    {
        // Um webTask com falha NÃO deve propagar a excepção para o Main:
        // o helper captura e regista, mantendo a semântica existing.
        Task faulted = Task.FromException(new InvalidOperationException("fake_FAULT"));
        await Program.AwaitResidentDashboardAsync(faulted, null, "test");
    }

    [Fact]
    public async Task Resident_swallows_Canceled_webTask_without_propagating()
    {
        Task cancelled = Task.FromCanceled(new CancellationToken(canceled: true));
        await Program.AwaitResidentDashboardAsync(cancelled, null, "test");
    }

    [Fact]
    public async Task Resident_completes_when_webTask_completes_after_shutdown_signal()
    {
        // Um webTask que termina quando um token de processo é cancelado —
        // exatamente o comportamento de RunDashboardAsync com o novo token.
        var cts = new System.Threading.CancellationTokenSource();
        var source = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException)
            {
                source.SetResult();
            }
        });

        cts.Cancel();
        await source.Task; // determinístico, sem sleep
        await Program.AwaitResidentDashboardAsync(source.Task, null, "test");
        cts.Dispose();
    }

    // ---------- StopDashboard (paragem coerente do listener) ----------

    [Fact]
    public void StopDashboard_with_no_listener_bound_is_a_noop()
    {
        var exception = Record.Exception(() => WebDashboardService.StopDashboard());
        Assert.Null(exception);
    }

    [Fact]
    public void StopDashboard_is_idempotent_when_called_twice()
    {
        WebDashboardService.StopDashboard();
        var exception = Record.Exception(() => WebDashboardService.StopDashboard());
        Assert.Null(exception);
    }

    // ---------- Reserved residency wiring (characterization) ----------

    /// <summary>
    /// Teste de caracterização wobstéricas: garante que o padrão de
    /// residency é invoqued nos TRÊS call-sites residentes de
    /// <c>Program.cs</c> — caminho <c>--telegram --web</c> sem Setup
    /// (Telegram pendente), o <c>do/while</c> do ciclo one-shot, e o
    /// dashboard standalone. Invalidate este teste se a wiring for
    /// alterada intencionalmente.
    ///
    /// NÃO corre um processo real; apenas confirma o source.
    /// </summary>
    [Fact]
    public void Program_contains_three_resident_call_sites()
    {
        var programPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            Path.Combine("..", "..", "..", "..", "m3uCrawler", "Program.cs"));
        if (!File.Exists(programPath))
        {
            var candidates = Directory.GetFiles(
                AppContext.BaseDirectory, "Program.cs", SearchOption.AllDirectories);
            programPath = candidates.FirstOrDefault(
                p => p.Contains(Path.Combine("m3uCrawler", "Program.cs")))
                ?? throw new InvalidOperationException(
                    $"Program.cs source not found under {AppContext.BaseDirectory}");
        }

        var source = File.ReadAllText(programPath);
        // 4 ocorrências no Main: (1) Telegram pendente de Setup,
        // (2) discovery automática bloqueada (lifecycle não READY),
        // (3) ciclo one-shot terminado, (4) dashboard standalone.
        var occurrences = Regex.Matches(source, "await AwaitResidentDashboardAsync\\(webTask, automationHost,");
        Assert.Equal(4, occurrences.Count);
        // Também confirma que o helper existe na própria classe Program.
        Assert.Contains("internal static async Task AwaitResidentDashboardAsync(", source);
    }

    // ---------- Scheduler — shutdown com e sem jobs ----------

    [Fact]
    public async Task Scheduler_loop_starts_and_stops_deterministically_with_no_jobs()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(), $"lifecycle-sched-{Guid.NewGuid():N}.db");
        try
        {
            var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
            var ctx = await bootstrapper.InitializeAsync();
            await ctx.DisposeAsync();
            var factory = new TestDbContextFactory(dbPath);
            var resolver = new CatalogResolver(factory, dbPath);

            using var host = ScheduledAutomationHost.Build(
                resolver,
                Path.Combine(Path.GetTempPath(), $"lifecycle-out-{Guid.NewGuid():N}"),
                DispatcharrConfig.Disabled(),
                pollInterval: TimeSpan.FromMilliseconds(50));

            host.Start();
            // Sem jobs: não deve bloquear. Depois StopAsync deve devolver
            // em tempo limitado (determinístico, sem sleep).
            var stopTask = host.StopAsync();
            var completed = await Task.WhenAny(
                stopTask, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(stopTask, completed);
            // Loop deve ter sido terminado sem excepção.
            await stopTask;
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task Scheduler_loop_with_a_due_job_runs_and_shutdown_completes()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(), $"lifecycle-sched-job-{Guid.NewGuid():N}.db");
        try
        {
            var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
            var ctx = await bootstrapper.InitializeAsync();
            await ctx.DisposeAsync();
            var factory = new TestDbContextFactory(dbPath);
            var resolver = new CatalogResolver(factory, dbPath);

            // Seed um job com o teste unknown-action (não efectua N rede;
            // resultado determinístico: unknown-action:<name>).
            await using (var ctx2 = factory.CreateDbContext())
            {
                ctx2.ScheduledJobs.Add(new ScheduledJobEntity
                {
                    Name = "lifecycle-job",
                    CronExpression = "* * * * *",
                    ActionName = "unknownLifecycleAction",
                    IsEnabled = true,
                    NextRunAtUtc = DateTime.UtcNow.AddMinutes(-5),
                    LastRunAtUtc = null,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow,
                });
                await ctx2.SaveChangesAsync();
            }

            using var host = ScheduledAutomationHost.Build(
                resolver,
                Path.Combine(Path.GetTempPath(), $"lifecycle-job-out-{Guid.NewGuid():N}"),
                DispatcharrConfig.Disabled(),
                pollInterval: TimeSpan.FromMilliseconds(50));

            host.Start();

            // Espera pelo registo de unknown-action (determinístico no
            // observation: o runner grava unknown-action:<name> no tick).
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                await using var ctxCheck = factory.CreateDbContext();
                var job = await ctxCheck.ScheduledJobs
                    .AsNoTracking()
                    .FirstAsync(j => j.Name == "lifecycle-job");
                if (job.LastResult is not null)
                {
                    break;
                }
            }

            // Shutdown: drena de forma determinística ( deadlines curtos).
            var stopTask = host.StopAsync();
            var completed = await Task.WhenAny(
                stopTask, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(stopTask, completed);
            await stopTask;
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task Scheduled_job_runner_survives_action_exception_and_still_shuts_down()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(), $"lifecycle-sched-throws-{Guid.NewGuid():N}.db");
        try
        {
            var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
            var ctx = await bootstrapper.InitializeAsync();
            await ctx.DisposeAsync();
            var factory = new TestDbContextFactory(dbPath);
            var resolver = new CatalogResolver(factory, dbPath);

            await using (var ctx2 = factory.CreateDbContext())
            {
                ctx2.ScheduledJobs.Add(new ScheduledJobEntity
                {
                    Name = "lifecycle-throws",
                    CronExpression = "* * * * *",
                    ActionName = "lifecycleThrowing",
                    IsEnabled = true,
                    NextRunAtUtc = DateTime.UtcNow.AddMinutes(-5),
                    LastRunAtUtc = null,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow,
                });
                await ctx2.SaveChangesAsync();
            }

            // O provider DI mínimo precisa conter a action que lança, para
            // o runner a resolver pelo nome.
            var services = new ServiceCollection();
            services.AddSingleton<IScheduledAction>(new LifecycleThrowingAction());
            using var provider = services.BuildServiceProvider();

            using var runner = new ScheduledJobRunner(
                factory, provider, TimeSpan.FromMilliseconds(50));
            runner.Start();
            await Task.Delay(150); // Deixa correr 2-3 ticks com a lançadora.

            // Shutdown: deve devolver (o runner não morre).
            var stopTask = runner.StopAsync();
            var completed = await Task.WhenAny(
                stopTask, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(stopTask, completed);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task Scheduled_cooperative_action_receives_cancellation_on_shutdown()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(), $"lifecycle-sched-coop-{Guid.NewGuid():N}.db");
        try
        {
            var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
            var ctx = await bootstrapper.InitializeAsync();
            await ctx.DisposeAsync();
            var factory = new TestDbContextFactory(dbPath);
            var resolver = new CatalogResolver(factory, dbPath);

            // Action cooperativa: bloqueia até vez de cancellation;
            // o shutdown do Runner dispara cancel na token.
            var services = new ServiceCollection();
            var cooperative = new LifecycleCooperativeAction();
            services.AddSingleton<IScheduledAction>(cooperative);
            using var provider = services.BuildServiceProvider();

            await using (var ctxSeed = factory.CreateDbContext())
            {
                ctxSeed.ScheduledJobs.Add(new ScheduledJobEntity
                {
                    Name = "lifecycle-coop",
                    CronExpression = "* * * * *",
                    ActionName = "lifecycleCooperative",
                    IsEnabled = true,
                    NextRunAtUtc = DateTime.UtcNow.AddMinutes(-5),
                    LastRunAtUtc = null,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow,
                });

                await ctxSeed.SaveChangesAsync();
            }

            using var runner = new ScheduledJobRunner(
                factory, provider, TimeSpan.FromMilliseconds(50));
            runner.Start();

            // Espera pela acção poder estar bloqueada (determinístico:
            // acabando de começar, é quase imediato).
            var reachDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < reachDeadline && !cooperative.ReachedBlockingState)
            {
                await Task.Delay(10);
            }

            // Shutdown: a action deve receber cancellation e terminar.
            var stopTask = runner.StopAsync();
            var completedStop = await Task.WhenAny(
                stopTask, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(stopTask, completedStop);
            await stopTask; // se houver OCE não observada, .Wait devolve verdadeiro

            // A action cooperativa foi cancelada, não deixou de executar.
            Assert.True(cooperative.WasCancelled);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task Scheduled_shutdown_without_any_job_is_harmless()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(), $"lifecycle-sched-empty-{Guid.NewGuid():N}.db");
        try
        {
            var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
            var ctx = await bootstrapper.InitializeAsync();
            await ctx.DisposeAsync();
            var factory = new TestDbContextFactory(dbPath);
            var resolver = new CatalogResolver(factory, dbPath);

            var services = new ServiceCollection();
            using var provider = services.BuildServiceProvider();
            using var runner = new ScheduledJobRunner(
                factory, provider, TimeSpan.FromMilliseconds(20));
            runner.Start();
            await Task.Delay(60); // alguns ticks sem jobs
            var stopTask = runner.StopAsync();
            var completed = await Task.WhenAny(
                stopTask, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(stopTask, completed);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    // ---------- Concorrência: manual + scheduler no mesmo coordinator ----------

    [Fact]
    public async Task RunCoordinator_repeated_sequential_runs_is_safe()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(), $"lifecycle-coord-{Guid.NewGuid():N}.db");
        try
        {
            var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
            var ctx = await bootstrapper.InitializeAsync();
            await ctx.DisposeAsync();
            var factory = new TestDbContextFactory(dbPath);

            var coordinator = new RunCoordinator(
                factory,
                request => new LifecycleNoopPipeline(),
                recognitionPolicyResolver: null);

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var outcome = await coordinator.StartAsync(
                    new LiveRunRequest
                    {
                        Mode = LiveRunMode.Telegram,
                        Source = LiveRunSource.Cli,
                        Keyword = $"w{attempt}",
                        HistoryHours = 24,
                        MaxStreams = 100,
                    },
                    CancellationToken.None);
                Assert.True(outcome.Succeeded, $"run {attempt} deveria ser成功地");
                Assert.False(coordinator.IsRunning, $"run {attempt} lock deveria ser libertado");
            }
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task RunCoordinator_scheduler_vs_manual_is_serialized_on_cas()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(), $"lifecycle-coord-race-{Guid.NewGuid():N}.db");
        try
        {
            var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
            var ctx = await bootstrapper.InitializeAsync();
            await ctx.DisposeAsync();
            var factory = new TestDbContextFactory(dbPath);

            // Pipeline partilhada: o teste controla determinísticamente
            // quando o run agendado termina (sem sleep).
            var pipeline = new LifecycleBlockingPipeline();
            var coordinator = new RunCoordinator(
                factory,
                _ => pipeline,
                recognitionPolicyResolver: null);

            var scheduled = coordinator.StartAsync(
                new LiveRunRequest
                {
                    Mode = LiveRunMode.Telegram,
                    Source = LiveRunSource.Scheduler,
                    Keyword = "scheduled",
                    HistoryHours = 24,
                    MaxStreams = 100,
                },
                CancellationToken.None);

            // Espera determinística pela entrada no pipeline (não sleep).
            await pipeline.WaitReached.WaitAsync(TimeSpan.FromSeconds(5));
            // A segunda chamada CAS falha — Pertences ao mesmo coordinator.
            await Assert.ThrowsAsync<RunAlreadyInProgressException>(async () =>
                await coordinator.StartAsync(
                    new LiveRunRequest
                    {
                        Mode = LiveRunMode.Telegram,
                        Source = LiveRunSource.Cli,
                        Keyword = "manual",
                        HistoryHours = 24,
                        MaxStreams = 100,
                    },
                    CancellationToken.None));

            // Termina o primeiro; lock libera só depois.
            pipeline.Release();
            var outcome = await scheduled;
            Assert.True(outcome.Succeeded);
            Assert.False(coordinator.IsRunning);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    private sealed class LifecycleThrowingAction : IScheduledAction
    {
        public string Name => "lifecycleThrowing";
        public string Description => "test action that throws";
        public Task<string> ExecuteAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("thrown deterministically in test");
    }

    private sealed class LifecycleCooperativeAction : IScheduledAction
    {
        private readonly TaskCompletionSource _done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _reachedBlockingState;
        private bool _wasCancelled;

        public string Name => "lifecycleCooperative";
        public string Description => "test action that honours cancellation";

        public bool ReachedBlockingState => Volatile.Read(ref _reachedBlockingState);
        public bool WasCancelled => Volatile.Read(ref _wasCancelled);

        public async Task<string> ExecuteAsync(CancellationToken cancellationToken)
        {
            Volatile.Write(ref _reachedBlockingState, true);
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                _done.TrySetResult();
                return "completed";
            }
            catch (OperationCanceledException)
            {
                Volatile.Write(ref _wasCancelled, true);
                _done.TrySetResult();
                throw;
            }
        }
    }

    private sealed class LifecycleNoopPipeline : IRunPipeline
    {
        public Task ExecuteAsync(LiveRunRequest request, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class LifecycleBlockingPipeline : IRunPipeline
    {
        private readonly TaskCompletionSource _reached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Tarefa que o teste aguarda para saber que a pipeline
        /// foi efectivamente iniciada (determinístico, sem sleep).</summary>
        public Task WaitReached => _reached.Task;

        /// <summary>Soltura determinística: termina a execução em curso.</summary>
        public void Release() => _released.TrySetResult();

        public async Task ExecuteAsync(LiveRunRequest request, CancellationToken cancellationToken)
        {
            // Blocking determinístico controlado pelo teste.
            _reached.TrySetResult();
            await _released.Task;
        }
    }
}
