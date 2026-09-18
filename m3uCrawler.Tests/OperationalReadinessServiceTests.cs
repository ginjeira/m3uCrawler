using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services.Configuration;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Wave 4 (PHASE 9C) — Prontidão operacional: regras de obrigatoriedade,
/// separação entre <c>SetupComplete</c> e <c>OperationalReady</c>,
/// fail-safe e grandfathering legacy.
/// </summary>
public sealed class OperationalReadinessServiceTests : IDisposable
{
    private readonly string _root;

    public OperationalReadinessServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"op-readiness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private ConfigurationLifecycleService NewLifecycle(
        ConfigurationLifecycleState state = ConfigurationLifecycleState.Ready,
        bool adopted = false)
    {
        var storePath = Path.Combine(
            _root,
            $"lifecycle-{Guid.NewGuid():N}",
            ConfigurationLifecycleStore.FileName);
        var store = new ConfigurationLifecycleStore(storePath);
        store.Save(new ConfigurationLifecycleSnapshot(
            state,
            adopted,
            adopted ? DateTime.UtcNow : null,
            "test",
            DateTime.UtcNow));
        return new ConfigurationLifecycleService(store, null, null);
    }

    private static OperationalReadinessService Build(
        ConfigurationLifecycleService lifecycle,
        bool hasAdmin = true,
        bool telegramAuthenticated = true,
        DispatcharrConfig? dispatcharr = null,
        bool catalogOk = true,
        bool countryDataOk = true,
        bool outputOk = true,
        int sources = 1)
        => new(
            lifecycle,
            _ => Task.FromResult(hasAdmin),
            () => telegramAuthenticated,
            () => dispatcharr ?? DispatcharrConfig.Disabled(),
            _ => Task.FromResult(catalogOk),
            _ => Task.FromResult(countryDataOk),
            () => outputOk,
            _ => Task.FromResult(sources));

    [Fact]
    public async Task All_components_satisfied_yields_setup_and_operational_ready()
    {
        var snapshot = await Build(NewLifecycle()).EvaluateAsync();

        Assert.True(snapshot.BootstrapReady);
        Assert.True(snapshot.HasAdmin);
        Assert.True(snapshot.TelegramAuthenticated);
        Assert.False(snapshot.DispatcharrEnabled);
        Assert.True(snapshot.DispatcharrValid);
        Assert.True(snapshot.CatalogOk);
        Assert.True(snapshot.CountryDataOk);
        Assert.True(snapshot.OutputOk);
        Assert.Equal(1, snapshot.SourcesCount);
        Assert.True(snapshot.SetupComplete);
        Assert.True(snapshot.OperationalReady);
        Assert.Empty(snapshot.MissingRequired);
        Assert.Equal(8, snapshot.Items.Count);
    }

    [Fact]
    public async Task Admin_only_is_not_setup_complete()
    {
        var snapshot = await Build(
            NewLifecycle(),
            hasAdmin: true,
            telegramAuthenticated: false,
            catalogOk: false,
            outputOk: false,
            sources: 0).EvaluateAsync();

        Assert.True(snapshot.HasAdmin);
        Assert.False(snapshot.SetupComplete);
        Assert.False(snapshot.OperationalReady);
        Assert.Contains("telegram", snapshot.MissingRequired);
        Assert.Contains("catalog", snapshot.MissingRequired);
        Assert.Contains("output", snapshot.MissingRequired);
        Assert.DoesNotContain("bootstrap", snapshot.MissingRequired);
    }

    [Fact]
    public async Task Missing_telegram_authentication_blocks_setup()
    {
        var snapshot = await Build(NewLifecycle(), telegramAuthenticated: false).EvaluateAsync();

        Assert.False(snapshot.TelegramAuthenticated);
        Assert.False(snapshot.SetupComplete);
        Assert.Contains("telegram", snapshot.MissingRequired);
    }

    [Fact]
    public async Task Dispatcharr_enabled_without_credentials_blocks_setup()
    {
        var config = new DispatcharrConfig { Enabled = true, BaseUrl = "http://dispatcharr.local" };

        var snapshot = await Build(NewLifecycle(), dispatcharr: config).EvaluateAsync();

        Assert.True(snapshot.DispatcharrEnabled);
        Assert.False(snapshot.DispatcharrValid);
        Assert.False(snapshot.SetupComplete);
        Assert.Contains("dispatcharr", snapshot.MissingRequired);

        var item = snapshot.Items.Single(i => i.Key == OperationalReadinessService.KeyDispatcharr);
        Assert.True(item.Required);
        Assert.False(item.Satisfied);
    }

    [Fact]
    public async Task Dispatcharr_enabled_with_api_key_is_valid()
    {
        var config = new DispatcharrConfig
        {
            Enabled = true,
            BaseUrl = "http://dispatcharr.local",
            ApiKey = "SECRET-API-KEY",
        };

        var snapshot = await Build(NewLifecycle(), dispatcharr: config).EvaluateAsync();

        Assert.True(snapshot.DispatcharrValid);
        Assert.True(snapshot.SetupComplete);
        Assert.DoesNotContain("dispatcharr", snapshot.MissingRequired);
    }

    [Fact]
    public async Task Dispatcharr_enabled_with_user_password_is_valid()
    {
        var config = new DispatcharrConfig
        {
            Enabled = true,
            BaseUrl = "http://dispatcharr.local",
            Username = "operator",
            Password = "SECRET-PASSWORD",
        };

        var snapshot = await Build(NewLifecycle(), dispatcharr: config).EvaluateAsync();

        Assert.True(snapshot.DispatcharrValid);
        Assert.True(snapshot.SetupComplete);
    }

    [Fact]
    public async Task Dispatcharr_disabled_is_not_required()
    {
        var snapshot = await Build(NewLifecycle(), dispatcharr: DispatcharrConfig.Disabled()).EvaluateAsync();

        Assert.False(snapshot.DispatcharrEnabled);
        Assert.True(snapshot.DispatcharrValid);

        var item = snapshot.Items.Single(i => i.Key == OperationalReadinessService.KeyDispatcharr);
        Assert.False(item.Required);
        Assert.True(item.Satisfied);

        Assert.DoesNotContain("dispatcharr", snapshot.MissingRequired);
        Assert.True(snapshot.SetupComplete);
    }

    [Fact]
    public async Task Missing_sources_keeps_setup_complete_but_not_operational_ready()
    {
        var snapshot = await Build(NewLifecycle(), sources: 0).EvaluateAsync();

        Assert.True(snapshot.SetupComplete);
        Assert.False(snapshot.OperationalReady);
        Assert.Equal(0, snapshot.SourcesCount);

        var item = snapshot.Items.Single(i => i.Key == OperationalReadinessService.KeySources);
        Assert.False(item.Required);
        Assert.False(item.Satisfied);

        // Sources nunca entra em MissingRequired: não é obrigatório para SetupComplete.
        Assert.DoesNotContain("sources", snapshot.MissingRequired);
    }

    [Fact]
    public async Task Catalog_missing_blocks_setup()
    {
        var snapshot = await Build(NewLifecycle(), catalogOk: false).EvaluateAsync();

        Assert.False(snapshot.CatalogOk);
        Assert.False(snapshot.SetupComplete);
        Assert.Contains("catalog", snapshot.MissingRequired);
    }

    [Fact]
    public async Task Country_data_present_is_satisfied()
    {
        var snapshot = await Build(NewLifecycle(), countryDataOk: true).EvaluateAsync();

        Assert.True(snapshot.CountryDataOk);
        Assert.True(snapshot.SetupComplete);

        var item = snapshot.Items.Single(i => i.Key == OperationalReadinessService.KeyCountryData);
        Assert.True(item.Required);
        Assert.True(item.Satisfied);
        Assert.DoesNotContain("countryData", snapshot.MissingRequired);
    }

    [Fact]
    public async Task Country_data_absent_blocks_setup_and_appears_in_missing_required()
    {
        var snapshot = await Build(NewLifecycle(), countryDataOk: false).EvaluateAsync();

        Assert.False(snapshot.CountryDataOk);
        Assert.False(snapshot.SetupComplete);
        Assert.Contains("countryData", snapshot.MissingRequired);

        var item = snapshot.Items.Single(i => i.Key == OperationalReadinessService.KeyCountryData);
        Assert.True(item.Required);
        Assert.False(item.Satisfied);
    }

    [Fact]
    public async Task Country_data_absent_is_grandfathered_for_legacy_adoption()
    {
        var snapshot = await Build(
            NewLifecycle(ConfigurationLifecycleState.Ready, adopted: true),
            countryDataOk: false).EvaluateAsync();

        Assert.True(snapshot.AdoptedFromLegacy);
        Assert.False(snapshot.CountryDataOk);
        Assert.True(snapshot.SetupComplete);
        Assert.True(snapshot.OperationalReady);

        // Obrigatório em falta continua a ser reportado (informativo).
        Assert.Contains("countryData", snapshot.MissingRequired);
    }

    [Fact]
    public async Task Output_unavailable_blocks_setup()
    {
        var snapshot = await Build(NewLifecycle(), outputOk: false).EvaluateAsync();

        Assert.False(snapshot.OutputOk);
        Assert.False(snapshot.SetupComplete);
        Assert.Contains("output", snapshot.MissingRequired);
    }

    [Fact]
    public async Task Bootstrap_not_ready_blocks_setup()
    {
        var snapshot = await Build(NewLifecycle(ConfigurationLifecycleState.NotConfigured)).EvaluateAsync();

        Assert.False(snapshot.BootstrapReady);
        Assert.False(snapshot.SetupComplete);
        Assert.Contains("bootstrap", snapshot.MissingRequired);
    }

    [Fact]
    public async Task Legacy_adoption_grandfathers_setup_and_operational_ready()
    {
        var invalidDispatcharr = new DispatcharrConfig { Enabled = true, BaseUrl = "http://dispatcharr.local" };

        var snapshot = await Build(
            NewLifecycle(ConfigurationLifecycleState.Ready, adopted: true),
            hasAdmin: false,
            telegramAuthenticated: false,
            dispatcharr: invalidDispatcharr,
            catalogOk: false,
            outputOk: false,
            sources: 0).EvaluateAsync();

        Assert.True(snapshot.AdoptedFromLegacy);
        Assert.True(snapshot.SetupComplete);
        Assert.True(snapshot.OperationalReady);

        // Itens continuam a ser reportados para diagnóstico.
        Assert.NotEmpty(snapshot.MissingRequired);
    }

    [Fact]
    public async Task Probes_that_throw_fail_closed()
    {
        var service = new OperationalReadinessService(
            NewLifecycle(),
            _ => throw new InvalidOperationException("admin"),
            () => throw new InvalidOperationException("telegram"),
            () => throw new InvalidOperationException("dispatcharr"),
            _ => throw new InvalidOperationException("catalog"),
            _ => throw new InvalidOperationException("countryData"),
            () => throw new InvalidOperationException("output"),
            _ => throw new InvalidOperationException("sources"));

        var snapshot = await service.EvaluateAsync();

        Assert.False(snapshot.HasAdmin);
        Assert.False(snapshot.TelegramAuthenticated);
        Assert.False(snapshot.CatalogOk);
        Assert.False(snapshot.CountryDataOk);
        Assert.False(snapshot.OutputOk);
        Assert.Equal(0, snapshot.SourcesCount);
        Assert.False(snapshot.SetupComplete);
        Assert.False(snapshot.OperationalReady);
    }

    [Fact]
    public async Task Is_setup_complete_matches_snapshot()
    {
        var complete = Build(NewLifecycle());
        Assert.True(await complete.IsSetupCompleteAsync());

        var incomplete = Build(NewLifecycle(), telegramAuthenticated: false);
        Assert.False(await incomplete.IsSetupCompleteAsync());
    }

    [Fact]
    public async Task Details_never_contain_secrets()
    {
        const string apiKey = "SECRET-API-KEY-VALUE";
        const string password = "SECRET-PASSWORD-VALUE";
        const string baseUrl = "http://internal-dispatcharr.local";

        var config = new DispatcharrConfig
        {
            Enabled = true,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            Password = password,
        };

        var snapshot = await Build(NewLifecycle(), dispatcharr: config).EvaluateAsync();

        foreach (var item in snapshot.Items)
        {
            Assert.DoesNotContain(apiKey, item.Detail);
            Assert.DoesNotContain(password, item.Detail);
            Assert.DoesNotContain(baseUrl, item.Detail);
        }
    }
}
