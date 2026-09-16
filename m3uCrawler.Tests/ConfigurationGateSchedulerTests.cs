using System;
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
}
