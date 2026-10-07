using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services.Automation;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9C.1 — Testes do scheduler gate. Prova que, em
/// <c>NOT_CONFIGURED</c>/<c>CONFIGURING</c>, nenhum job automático executa
/// (incluindo a descoberta) e que <c>READY</c> preserva o comportamento
/// existente.
/// </summary>
public class ConfigurationGateSchedulerTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly List<string> _lifecycleDirs = new();
    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;

    public ConfigurationGateSchedulerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cfg-gate-{Guid.NewGuid():N}.db");
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
        try { File.Delete(_dbPath); } catch { /* best effort */ }
        foreach (var dir in _lifecycleDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
        return Task.CompletedTask;
    }

    private sealed class CountingAction : IScheduledAction
    {
        public int Executions;
        public string Name => ScheduledM3uDiscoveryAction.ActionName;

        public Task<string> ExecuteAsync(CancellationToken cancellationToken)
        {
            Executions++;
            return Task.FromResult("ok");
        }
    }

    private sealed class FixedGate : IConfigurationGate
    {
        private readonly bool _ready;

        public FixedGate(ConfigurationLifecycleState state)
        {
            State = state;
            _ready = state == ConfigurationLifecycleState.Ready;
        }

        public ConfigurationLifecycleState State { get; }

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_ready);
    }

    private sealed class FixedReadiness : IOperationalReadinessGate
    {
        private readonly bool _setupComplete;

        public FixedReadiness(bool setupComplete)
        {
            _setupComplete = setupComplete;
        }

        public Task<bool> IsSetupCompleteAsync(CancellationToken ct = default)
            => Task.FromResult(_setupComplete);
    }

    private ConfigurationLifecycleService NewLifecycle(ConfigurationLifecycleState state)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cfg-gate-lifecycle-{Guid.NewGuid():N}");
        _lifecycleDirs.Add(dir);
        var store = new ConfigurationLifecycleStore(Path.Combine(dir, ConfigurationLifecycleStore.FileName));
        store.Save(new ConfigurationLifecycleSnapshot(state, false, null, "test", DateTime.UtcNow));
        return new ConfigurationLifecycleService(store, null, null);
    }

    private ScheduledJobRunner BuildRunner(IConfigurationGate? gate, CountingAction action)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IScheduledAction>(action);
        var provider = services.BuildServiceProvider();
        return new ScheduledJobRunner(_factory, provider, TimeSpan.FromMinutes(5), gate);
    }

    private async Task MakeDiscoveryJobDueAsync()
    {
        var job = await _resolver.UpsertScheduledJobAsync(
            "discover-test", "* * * * *",
            ScheduledM3uDiscoveryAction.ActionName, isEnabled: true);
        await _resolver.MarkScheduledJobRanAsync(
            job.Id, DateTime.UtcNow.AddMinutes(-5), "warmup");
    }

    [Theory]
    [InlineData(ConfigurationLifecycleState.NotConfigured)]
    [InlineData(ConfigurationLifecycleState.Configuring)]
    public async Task Scheduler_and_discovery_are_blocked_when_not_ready(
        ConfigurationLifecycleState state)
    {
        await MakeDiscoveryJobDueAsync();
        var action = new CountingAction();
        using var runner = BuildRunner(new FixedGate(state), action);

        var ran = await runner.TickOnceAsync();

        Assert.Equal(0, ran);
        Assert.Equal(0, action.Executions);

        // Bloqueio registado como tal (não como sucesso).
        var job = Assert.Single(await _resolver.ListScheduledJobsAsync());
        Assert.Equal(ScheduledJobRunner.BlockedResult, job.LastResult);
    }

    [Fact]
    public async Task Scheduler_and_discovery_run_when_ready()
    {
        await MakeDiscoveryJobDueAsync();
        var action = new CountingAction();
        using var runner = BuildRunner(
            new FixedGate(ConfigurationLifecycleState.Ready), action);

        var ran = await runner.TickOnceAsync();

        Assert.Equal(1, ran);
        Assert.Equal(1, action.Executions);

        var job = Assert.Single(await _resolver.ListScheduledJobsAsync());
        Assert.Equal("ok", job.LastResult);
    }

    [Fact]
    public async Task Scheduler_without_gate_preserves_existing_behaviour()
    {
        await MakeDiscoveryJobDueAsync();
        var action = new CountingAction();
        using var runner = BuildRunner(gate: null, action);

        var ran = await runner.TickOnceAsync();

        Assert.Equal(1, ran);
        Assert.Equal(1, action.Executions);
    }

    [Fact]
    public async Task Blocked_job_stays_due_so_it_runs_once_ready()
    {
        await MakeDiscoveryJobDueAsync();
        var action = new CountingAction();

        using (var blocked = BuildRunner(
            new FixedGate(ConfigurationLifecycleState.NotConfigured), action))
        {
            Assert.Equal(0, await blocked.TickOnceAsync());
        }

        using (var allowed = BuildRunner(
            new FixedGate(ConfigurationLifecycleState.Ready), action))
        {
            Assert.Equal(1, await allowed.TickOnceAsync());
        }

        Assert.Equal(1, action.Executions);
    }

    // === Wave 4 (PHASE 9C) — gate composto com prontidão operacional ===

    [Fact]
    public async Task Gate_requires_setup_complete_when_readiness_gate_present()
    {
        var lifecycle = NewLifecycle(ConfigurationLifecycleState.Ready);

        var incomplete = new ConfigurationGate(lifecycle, new FixedReadiness(false));
        Assert.False(await incomplete.IsReadyAsync());
        Assert.False(await incomplete.IsOperationalReadyAsync());

        var complete = new ConfigurationGate(lifecycle, new FixedReadiness(true));
        Assert.True(await complete.IsReadyAsync());
        Assert.True(await complete.IsOperationalReadyAsync());
    }

    [Fact]
    public async Task Gate_without_readiness_gate_preserves_lifecycle_only_behaviour()
    {
        var lifecycle = NewLifecycle(ConfigurationLifecycleState.Ready);
        var gate = new ConfigurationGate(lifecycle);

        Assert.True(await gate.IsReadyAsync());
        Assert.False(await gate.IsOperationalReadyAsync());
        Assert.Empty(await gate.MissingOperationalAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Gate_is_false_when_lifecycle_not_ready_regardless_of_readiness(bool setupComplete)
    {
        var lifecycle = NewLifecycle(ConfigurationLifecycleState.Configuring);
        var gate = new ConfigurationGate(lifecycle, new FixedReadiness(setupComplete));

        Assert.False(await gate.IsReadyAsync());
    }

    [Fact]
    public async Task Scheduler_does_not_run_when_setup_incomplete_and_keeps_job_due()
    {
        await MakeDiscoveryJobDueAsync();
        var before = Assert.Single(await _resolver.ListScheduledJobsAsync());

        var action = new CountingAction();
        var gate = new ConfigurationGate(
            NewLifecycle(ConfigurationLifecycleState.Ready), new FixedReadiness(false));
        using var runner = BuildRunner(gate, action);

        var ran = await runner.TickOnceAsync();

        Assert.Equal(0, ran);
        Assert.Equal(0, action.Executions);

        var after = Assert.Single(await _resolver.ListScheduledJobsAsync());
        Assert.Equal(ScheduledJobRunner.BlockedResult, after.LastResult);
        Assert.Equal(before.NextRunAtUtc, after.NextRunAtUtc);
    }

    [Fact]
    public async Task Scheduler_runs_when_setup_complete()
    {
        await MakeDiscoveryJobDueAsync();

        var action = new CountingAction();
        var gate = new ConfigurationGate(
            NewLifecycle(ConfigurationLifecycleState.Ready), new FixedReadiness(true));
        using var runner = BuildRunner(gate, action);

        var ran = await runner.TickOnceAsync();

        Assert.Equal(1, ran);
        Assert.Equal(1, action.Executions);
    }
}
