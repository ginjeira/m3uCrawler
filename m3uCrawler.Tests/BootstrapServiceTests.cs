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

    /// <summary>
    /// PHASE 9C.5 — Garantia na camada de persistência: duas instâncias do
    /// store a tentar criar o primeiro administrador em simultâneo, sem a
    /// serialização do serviço, nunca produzem duas linhas (transacção +
    /// verificação de tabela vazia; a unique de username é salvaguarda
    /// adicional quando os nomes coincidem).
    /// </summary>
    [Fact]
    public async Task Admin_user_store_concurrent_first_admin_persists_single_row()
    {
        var first = new AdminUserStore(_factory);
        var second = new AdminUserStore(_factory);

        var results = await Task.WhenAll(
            first.CreateFirstAdminAsync("admin-a", "a-strong-password-12"),
            second.CreateFirstAdminAsync("admin-b", "a-strong-password-12"));

        await using var context = _factory.CreateDbContext();
        Assert.Equal(1, context.AdminUsers.Count());
        Assert.Contains(CreateAdminResult.Created, results);
        Assert.Contains(CreateAdminResult.AlreadyExists, results);
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
    public async Task Legacy_ready_start_is_already_ready_and_does_not_create_admin()
    {
        var lifecycle = NewLifecycle();
        lifecycle.SetState(ConfigurationLifecycleState.Ready, "legacy-adoption:sources");
        var service = NewBootstrap(lifecycle);

        // O estado já está READY (legacy adoption): iniciar o bootstrap é
        // idempotente e não cria administrador por si só.
        Assert.Equal(BootstrapStartOutcome.AlreadyReady, await service.StartAsync());

        await using var context = _factory.CreateDbContext();
        Assert.Empty(context.AdminUsers);
    }

    /// <summary>
    /// PHASE 9C.5 — Cenário A/D: instalação legacy adoptada READY sem admin
    /// (BOOTSTRAP_REQUIRED) permite criar o primeiro administrador sem alterar
    /// o estado nem reconfigurar nada; a partir daí o modo passa a UserAuth.
    /// </summary>
    [Fact]
    public async Task Legacy_ready_without_admin_creates_first_admin_and_keeps_ready()
    {
        var lifecycle = NewLifecycle();
        lifecycle.SetState(ConfigurationLifecycleState.Ready, "legacy-adoption:sources");
        var service = NewBootstrap(lifecycle);

        var before = await service.GetStatusAsync();
        Assert.Equal(ConfigurationLifecycleState.Ready, before.State);
        Assert.False(before.HasActiveAdmin);

        var (outcome, error) = await service.CreateAdminAsync("admin", "a-strong-password-12");

        Assert.Equal(BootstrapAdminOutcome.Created, outcome);
        Assert.Null(error);

        var after = await service.GetStatusAsync();
        Assert.Equal(ConfigurationLifecycleState.Ready, after.State);
        Assert.True(after.HasActiveAdmin);

        // O estado persistido permanece READY — nunca desce para CONFIGURING.
        Assert.Equal(ConfigurationLifecycleState.Ready, (await lifecycle.GetStateAsync()).State);
        Assert.Equal(
            AuthMode.UserAuth,
            AuthModeResolver.Resolve(after.State, after.HasActiveAdmin));
    }

    /// <summary>
    /// PHASE 9C.5 — Preservação: numa instalação legacy adoptada sem admin,
    /// apenas o administrador é acrescentado; a marca de adopção e a razão
    /// originais permanecem intactas (sem reset/recriação da configuração).
    /// </summary>
    [Fact]
    public async Task Legacy_ready_admin_creation_preserves_adoption_metadata()
    {
        var adoptedAt = new DateTime(2026, 9, 10, 8, 30, 0, DateTimeKind.Utc);
        new ConfigurationLifecycleStore(_storePath).Save(new ConfigurationLifecycleSnapshot(
            ConfigurationLifecycleState.Ready,
            AdoptedFromLegacy: true,
            AdoptedAtUtc: adoptedAt,
            LastReason: "legacy-adoption:sources,ordering_lists",
            UpdatedAtUtc: adoptedAt));

        var lifecycle = NewLifecycle();
        var service = NewBootstrap(lifecycle);

        var (outcome, _) = await service.CreateAdminAsync("admin", "a-strong-password-12");
        Assert.Equal(BootstrapAdminOutcome.Created, outcome);

        var snapshot = await lifecycle.GetStateAsync();
        Assert.Equal(ConfigurationLifecycleState.Ready, snapshot.State);
        Assert.True(snapshot.AdoptedFromLegacy);
        Assert.Equal(adoptedAt, snapshot.AdoptedAtUtc);
        Assert.Equal("legacy-adoption:sources,ordering_lists", snapshot.LastReason);
    }

    /// <summary>
    /// PHASE 9C.5 — Cenário E: depois de existir administrador, um segundo
    /// bootstrap é rejeitado e não cria nem substitui nada.
    /// </summary>
    [Fact]
    public async Task Legacy_ready_second_admin_after_creation_is_rejected()
    {
        var lifecycle = NewLifecycle();
        lifecycle.SetState(ConfigurationLifecycleState.Ready, "legacy-adoption:sources");
        var service = NewBootstrap(lifecycle);

        Assert.Equal(
            BootstrapAdminOutcome.Created,
            (await service.CreateAdminAsync("admin", "a-strong-password-12")).Outcome);

        var second = await service.CreateAdminAsync("intruder", "another-strong-pw-12");

        Assert.Equal(BootstrapAdminOutcome.AlreadyReady, second.Outcome);

        await using var context = _factory.CreateDbContext();
        Assert.Equal(1, context.AdminUsers.Count());
        Assert.Equal("admin", context.AdminUsers.Single().Username);
    }

    /// <summary>
    /// PHASE 9C.5 — Cenário F: duas tentativas concorrentes de primeiro
    /// bootstrap numa instalação legacy READY criam apenas um administrador.
    /// A garantia combina a serialização do serviço com a transacção e a
    /// unique constraint de username na persistência.
    /// </summary>
    [Fact]
    public async Task Legacy_ready_concurrent_admin_creation_creates_only_one()
    {
        var lifecycle = NewLifecycle();
        lifecycle.SetState(ConfigurationLifecycleState.Ready, "legacy-adoption:sources");
        var service = NewBootstrap(lifecycle);

        var results = await Task.WhenAll(
            service.CreateAdminAsync("admin-a", "a-strong-password-12"),
            service.CreateAdminAsync("admin-b", "a-strong-password-12"));

        await using var context = _factory.CreateDbContext();
        Assert.Equal(1, context.AdminUsers.Count());
        Assert.Contains(results, r => r.Outcome == BootstrapAdminOutcome.Created);
        Assert.DoesNotContain(results, r => r.Outcome == BootstrapAdminOutcome.Invalid);
        Assert.Equal(ConfigurationLifecycleState.Ready, (await lifecycle.GetStateAsync()).State);
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
