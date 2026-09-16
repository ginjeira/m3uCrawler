using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services.Auth;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9C.2 — Fluxo de bootstrap, invariantes e retomada.
/// </summary>
public class BootstrapServiceTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _outputDir;
    private readonly string _storePath;
    private TestDbContextFactory _factory = null!;

    public BootstrapServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"bootstrap-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _outputDir = Path.Combine(_root, "output");
        _storePath = Path.Combine(_root, ConfigurationLifecycleStore.FileName);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_outputDir);
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private ConfigurationLifecycleService NewLifecycle(string? outputDir = null)
        => new(new ConfigurationLifecycleStore(_storePath), _factory, outputDir ?? _outputDir);

    private BootstrapService NewBootstrap(
        ConfigurationLifecycleService? lifecycle = null,
        string? outputDir = null,
        DispatcharrConfig? dispatcharr = null)
    {
        var lc = lifecycle ?? NewLifecycle(outputDir);
        var users = new AdminUserStore(_factory);
        var validator = new BootstrapConfigurationValidator(
            _factory, outputDir ?? _outputDir, dispatcharr);
        return new BootstrapService(lc, users, validator);
    }

    [Fact]
    public async Task Fresh_install_starts_not_configured_without_admin()
    {
        var service = NewBootstrap();

        var status = await service.GetStatusAsync();

        Assert.Equal(ConfigurationLifecycleState.NotConfigured, status.State);
        Assert.False(status.HasActiveAdmin);
        Assert.All(status.Checks, c => Assert.True(c.Satisfied));
    }

    [Fact]
    public async Task Start_moves_to_configuring_and_is_idempotent()
    {
        var lifecycle = NewLifecycle();
        var service = NewBootstrap(lifecycle);

        Assert.Equal(BootstrapStartOutcome.Started, await service.StartAsync());
        Assert.Equal(BootstrapStartOutcome.AlreadyConfiguring, await service.StartAsync());
        Assert.Equal(ConfigurationLifecycleState.Configuring, (await lifecycle.GetStateAsync()).State);
    }

    [Fact]
    public async Task Create_admin_requires_started_bootstrap()
    {
        var service = NewBootstrap();

        var (outcome, error) = await service.CreateAdminAsync("admin", "a-strong-password-12");

        Assert.Equal(BootstrapAdminOutcome.NotStarted, outcome);
        Assert.Equal("bootstrap-not-started", error);
    }

    [Fact]
    public async Task Create_admin_rejects_invalid_credentials()
    {
        var service = NewBootstrap();
        await service.StartAsync();

        var (outcome, error) = await service.CreateAdminAsync("admin", "short");

        Assert.Equal(BootstrapAdminOutcome.Invalid, outcome);
        Assert.Equal("password-too-short", error);
    }

    [Fact]
    public async Task Create_admin_is_idempotent_and_never_creates_two()
    {
        var service = NewBootstrap();
        await service.StartAsync();

        var first = await service.CreateAdminAsync("admin", "a-strong-password-12");
        var second = await service.CreateAdminAsync("other", "another-strong-pw-12");

        Assert.Equal(BootstrapAdminOutcome.Created, first.Outcome);
        Assert.Equal(BootstrapAdminOutcome.AlreadyCreated, second.Outcome);

        await using var context = _factory.CreateDbContext();
        Assert.Equal(1, context.AdminUsers.Count());
    }

    [Fact]
    public async Task Concurrent_admin_creation_never_creates_two()
    {
        var service = NewBootstrap();
        await service.StartAsync();

        var results = await Task.WhenAll(
            service.CreateAdminAsync("admin-a", "a-strong-password-12"),
            service.CreateAdminAsync("admin-b", "a-strong-password-12"));

        await using var context = _factory.CreateDbContext();
        Assert.Equal(1, context.AdminUsers.Count());
        Assert.Contains(results, r => r.Outcome == BootstrapAdminOutcome.Created);
        Assert.DoesNotContain(results, r => r.Outcome == BootstrapAdminOutcome.Invalid);
    }

    [Fact]
    public async Task Complete_without_admin_is_rejected_and_never_ready()
    {
        var lifecycle = NewLifecycle();
        var service = NewBootstrap(lifecycle);
        await service.StartAsync();

        var validation = await service.CompleteAsync();

        Assert.Equal(BootstrapCompleteOutcome.MissingAdmin, validation.Outcome);
        Assert.Equal(ConfigurationLifecycleState.Configuring, (await lifecycle.GetStateAsync()).State);
    }

    [Fact]
    public async Task Complete_with_invalid_L2_is_rejected_and_never_ready()
    {
        // Output dir aponta para um ficheiro: não é utilizável.
        var blockedOutput = Path.Combine(_root, "output-is-a-file");
        await File.WriteAllTextAsync(blockedOutput, "x");

        var lifecycle = NewLifecycle(blockedOutput);
        var service = NewBootstrap(lifecycle, blockedOutput);
        await service.StartAsync();
        await service.CreateAdminAsync("admin", "a-strong-password-12");

        var validation = await service.CompleteAsync();

        Assert.Equal(BootstrapCompleteOutcome.InvalidConfiguration, validation.Outcome);
        Assert.Contains(validation.Checks, c => c.Key == "output" && !c.Satisfied);
        Assert.Equal(ConfigurationLifecycleState.Configuring, (await lifecycle.GetStateAsync()).State);
    }

    [Fact]
    public async Task Complete_with_admin_and_valid_L2_goes_ready()
    {
        var lifecycle = NewLifecycle();
        var service = NewBootstrap(lifecycle);
        await service.StartAsync();
        await service.CreateAdminAsync("admin", "a-strong-password-12");

        var validation = await service.CompleteAsync();

        Assert.Equal(BootstrapCompleteOutcome.Completed, validation.Outcome);
        Assert.Equal(ConfigurationLifecycleState.Ready, (await lifecycle.GetStateAsync()).State);
    }

    [Fact]
    public async Task Bootstrap_is_closed_after_ready()
    {
        var lifecycle = NewLifecycle();
        var service = NewBootstrap(lifecycle);
        await service.StartAsync();
        await service.CreateAdminAsync("admin", "a-strong-password-12");
        await service.CompleteAsync();

        Assert.Equal(BootstrapStartOutcome.AlreadyReady, await service.StartAsync());
        Assert.Equal(
            BootstrapAdminOutcome.AlreadyReady,
            (await service.CreateAdminAsync("intruder", "a-strong-password-12")).Outcome);
        Assert.Equal(
            BootstrapCompleteOutcome.AlreadyReady,
            (await service.CompleteAsync()).Outcome);

        await using var context = _factory.CreateDbContext();
        Assert.Equal(1, context.AdminUsers.Count());
        Assert.Equal("admin", context.AdminUsers.Single().Username);
    }

    [Fact]
    public async Task Restart_during_configuring_can_resume_and_complete()
    {
        var firstRun = NewBootstrap();
        await firstRun.StartAsync();
        await firstRun.CreateAdminAsync("admin", "a-strong-password-12");

        // Simula restart: novas instâncias sobre os mesmos stores persistentes.
        var lifecycle = NewLifecycle();
        var secondRun = NewBootstrap(lifecycle);

        var status = await secondRun.GetStatusAsync();
        Assert.Equal(ConfigurationLifecycleState.Configuring, status.State);
        Assert.True(status.HasActiveAdmin);

        var validation = await secondRun.CompleteAsync();
        Assert.Equal(BootstrapCompleteOutcome.Completed, validation.Outcome);
        Assert.Equal(ConfigurationLifecycleState.Ready, (await lifecycle.GetStateAsync()).State);
    }

    [Fact]
    public async Task Ready_state_persists_across_reload()
    {
        var service = NewBootstrap();
        await service.StartAsync();
        await service.CreateAdminAsync("admin", "a-strong-password-12");
        await service.CompleteAsync();

        var reloaded = await NewLifecycle().GetStateAsync();

        Assert.Equal(ConfigurationLifecycleState.Ready, reloaded.State);
    }

    [Fact]
    public async Task Legacy_ready_installation_cannot_bootstrap()
    {
        var lifecycle = NewLifecycle();
        lifecycle.SetState(ConfigurationLifecycleState.Ready, "legacy-adoption:sources");
        var service = NewBootstrap(lifecycle);

        Assert.Equal(BootstrapStartOutcome.AlreadyReady, await service.StartAsync());

        await using var context = _factory.CreateDbContext();
        Assert.Empty(context.AdminUsers);
    }

    [Fact]
    public async Task Dispatcharr_enabled_without_credentials_blocks_ready()
    {
        var invalidDispatcharr = new DispatcharrConfig
        {
            Enabled = true,
            BaseUrl = "http://dispatcharr.invalid",
        };
        var lifecycle = NewLifecycle();
        var service = NewBootstrap(lifecycle, dispatcharr: invalidDispatcharr);
        await service.StartAsync();
        await service.CreateAdminAsync("admin", "a-strong-password-12");

        var validation = await service.CompleteAsync();

        Assert.Equal(BootstrapCompleteOutcome.InvalidConfiguration, validation.Outcome);
        Assert.Contains(validation.Checks, c => c.Key == "dispatcharr" && !c.Satisfied);
        Assert.Equal(ConfigurationLifecycleState.Configuring, (await lifecycle.GetStateAsync()).State);
    }

    [Fact]
    public async Task Dispatcharr_disabled_does_not_block_ready()
    {
        var lifecycle = NewLifecycle();
        var service = NewBootstrap(lifecycle, dispatcharr: DispatcharrConfig.Disabled());
        await service.StartAsync();
        await service.CreateAdminAsync("admin", "a-strong-password-12");

        var validation = await service.CompleteAsync();

        Assert.Equal(BootstrapCompleteOutcome.Completed, validation.Outcome);
    }
}
