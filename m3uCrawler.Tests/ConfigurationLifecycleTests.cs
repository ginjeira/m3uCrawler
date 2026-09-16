using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9C.1 — Testes do ciclo de vida de configuração
/// (<c>NOT_CONFIGURED</c> / <c>CONFIGURING</c> / <c>READY</c>),
/// da autoridade do estado persistido, do bootstrap de instalação nova
/// e da adopção legacy.
/// </summary>
public class ConfigurationLifecycleTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _outputDir;
    private readonly string _storePath;
    private TestDbContextFactory _factory = null!;

    public ConfigurationLifecycleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cfg-9c1-{Guid.NewGuid():N}");
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

    private ConfigurationLifecycleService NewService()
        => new(new ConfigurationLifecycleStore(_storePath), _factory, _outputDir);

    private async Task AddLegacySourceAsync()
    {
        var resolver = new CatalogResolver(_factory, _dbPath);
        await resolver.EnsureSourceAsync(
            "legacy-src", "legacy-src", SourceKind.M3U,
            "https://example.invalid/list.m3u", 50);
    }

    [Fact]
    public async Task Fresh_install_with_seeded_catalog_only_is_not_configured()
    {
        var service = NewService();

        var snapshot = await service.EnsureInitializedAsync();

        Assert.Equal(ConfigurationLifecycleState.NotConfigured, snapshot.State);
        Assert.False(snapshot.AdoptedFromLegacy);
        Assert.False(snapshot.IsReady);
        Assert.True(File.Exists(_storePath), "O estado deve ser persistido no primeiro arranque.");
        Assert.Contains("NOT_CONFIGURED", await File.ReadAllTextAsync(_storePath));
    }

    [Fact]
    public async Task Persisted_state_is_authoritative_over_new_evidence()
    {
        var first = await NewService().EnsureInitializedAsync();
        Assert.Equal(ConfigurationLifecycleState.NotConfigured, first.State);

        // Aparece evidência legacy depois do primeiro bootstrap (ex.: a
        // instalação começou a operar). O estado persistido continua a
        // mandar: não há re-avaliação.
        await AddLegacySourceAsync();

        var reloaded = await NewService().GetStateAsync();

        Assert.Equal(ConfigurationLifecycleState.NotConfigured, reloaded.State);
        Assert.False(reloaded.AdoptedFromLegacy);
    }

    [Fact]
    public async Task State_transitions_not_configured_configuring_ready_persist_across_reload()
    {
        var service = NewService();
        Assert.Equal(ConfigurationLifecycleState.NotConfigured, (await service.EnsureInitializedAsync()).State);

        service.SetState(ConfigurationLifecycleState.Configuring, "wizard-started");
        Assert.Equal(ConfigurationLifecycleState.Configuring, service.Current!.State);

        // Reload a partir do disco (nova instância, mesmo ficheiro).
        var reloaded = await NewService().GetStateAsync();
        Assert.Equal(ConfigurationLifecycleState.Configuring, reloaded.State);

        service.SetState(ConfigurationLifecycleState.Ready, "wizard-complete");
        var ready = await NewService().GetStateAsync();
        Assert.Equal(ConfigurationLifecycleState.Ready, ready.State);
        Assert.True(ready.IsReady);
    }

    [Fact]
    public async Task Legacy_installation_with_catalog_evidence_is_adopted_as_ready()
    {
        await AddLegacySourceAsync();

        var snapshot = await NewService().EnsureInitializedAsync();

        Assert.Equal(ConfigurationLifecycleState.Ready, snapshot.State);
        Assert.True(snapshot.AdoptedFromLegacy);
        Assert.NotNull(snapshot.AdoptedAtUtc);
        Assert.Contains("sources", snapshot.LastReason);
    }

    [Fact]
    public async Task Legacy_installation_with_output_artifacts_is_adopted_as_ready()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_outputDir, "playlist.m3u"), "#EXTM3U\n");

        var snapshot = await NewService().EnsureInitializedAsync();

        Assert.Equal(ConfigurationLifecycleState.Ready, snapshot.State);
        Assert.True(snapshot.AdoptedFromLegacy);
        Assert.Contains("playlist", snapshot.LastReason);
    }

    [Fact]
    public async Task Legacy_installation_without_evidence_stays_not_configured()
    {
        // BD semeada (canais canónicos + aliases) não conta como evidência,
        // e o output está vazio.
        var snapshot = await NewService().EnsureInitializedAsync();

        Assert.Equal(ConfigurationLifecycleState.NotConfigured, snapshot.State);
        Assert.False(snapshot.AdoptedFromLegacy);
    }

    [Fact]
    public async Task Advisory_requirements_do_not_block_ready()
    {
        var service = NewService();
        await service.EnsureInitializedAsync();
        service.SetState(ConfigurationLifecycleState.Ready, "operator-declared");

        var advisory = await service.EvaluateAdvisoryAsync();

        // Numa instalação só com seed, vários requisitos §5 estão por
        // satisfazer (ex.: ordering list). Isso é advisory: não muda o estado.
        Assert.Contains(advisory, a => a.Key == "ordering-list" && !a.Satisfied);
        Assert.Equal(ConfigurationLifecycleState.Ready, (await service.GetStateAsync()).State);
    }

    [Theory]
    [InlineData(ConfigurationLifecycleState.NotConfigured, false)]
    [InlineData(ConfigurationLifecycleState.Configuring, false)]
    [InlineData(ConfigurationLifecycleState.Ready, true)]
    public async Task Gate_allows_only_ready(ConfigurationLifecycleState state, bool expectedReady)
    {
        var service = NewService();
        await service.EnsureInitializedAsync();
        service.SetState(state, "test");

        var gate = new ConfigurationGate(service);

        Assert.Equal(expectedReady, await gate.IsReadyAsync());
        Assert.Equal(state, gate.State);
    }

    [Fact]
    public void Wire_names_are_stable()
    {
        Assert.Equal("NOT_CONFIGURED", ConfigurationLifecycleState.NotConfigured.ToWireName());
        Assert.Equal("CONFIGURING", ConfigurationLifecycleState.Configuring.ToWireName());
        Assert.Equal("READY", ConfigurationLifecycleState.Ready.ToWireName());
        Assert.True(ConfigurationLifecycleStateNames.TryParse("ready", out var parsed));
        Assert.Equal(ConfigurationLifecycleState.Ready, parsed);
    }
}
