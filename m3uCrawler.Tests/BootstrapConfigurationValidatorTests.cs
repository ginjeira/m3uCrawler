using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services.Auth;
using m3uCrawler.Services.Catalog;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9C.2 — Validação L2 (configuração mínima para READY).
/// </summary>
public class BootstrapConfigurationValidatorTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _outputDir;
    private TestDbContextFactory _factory = null!;

    public BootstrapConfigurationValidatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"l2-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _outputDir = Path.Combine(_root, "output");
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

    private BootstrapCheck Check(string key, BootstrapConfigurationValidator validator)
        => validator.Evaluate().Single(c => c.Key == key);

    [Fact]
    public void Catalog_is_usable_with_a_bootstrapped_database()
    {
        var validator = new BootstrapConfigurationValidator(_factory, _outputDir);
        Assert.True(Check("catalog", validator).Satisfied);
    }

    [Fact]
    public void Catalog_is_unusable_without_a_database()
    {
        var validator = new BootstrapConfigurationValidator(null, _outputDir);
        Assert.False(Check("catalog", validator).Satisfied);
    }

    [Fact]
    public void Catalog_is_unusable_when_schema_is_absent()
    {
        var emptyFactory = new TestDbContextFactory(Path.Combine(_root, "empty.db"));
        var validator = new BootstrapConfigurationValidator(emptyFactory, _outputDir);
        Assert.False(Check("catalog", validator).Satisfied);
    }

    [Fact]
    public void Output_is_usable_when_directory_is_writable()
    {
        var validator = new BootstrapConfigurationValidator(_factory, _outputDir);
        Assert.True(Check("output", validator).Satisfied);
    }

    [Fact]
    public async Task Output_is_unusable_when_path_is_a_file()
    {
        var fileAsDir = Path.Combine(_root, "not-a-directory");
        await File.WriteAllTextAsync(fileAsDir, "x");

        var validator = new BootstrapConfigurationValidator(_factory, fileAsDir);

        Assert.False(Check("output", validator).Satisfied);
    }

    [Fact]
    public void Dispatcharr_disabled_does_not_block()
    {
        var validator = new BootstrapConfigurationValidator(_factory, _outputDir, DispatcharrConfig.Disabled());
        Assert.True(Check("dispatcharr", validator).Satisfied);
    }

    [Fact]
    public void Dispatcharr_enabled_with_api_key_is_valid()
    {
        var config = new DispatcharrConfig
        {
            Enabled = true,
            BaseUrl = "http://dispatcharr.local",
            ApiKey = "secret-key",
        };
        var validator = new BootstrapConfigurationValidator(_factory, _outputDir, config);

        Assert.True(Check("dispatcharr", validator).Satisfied);
    }

    [Fact]
    public void Dispatcharr_enabled_with_user_password_is_valid()
    {
        var config = new DispatcharrConfig
        {
            Enabled = true,
            BaseUrl = "http://dispatcharr.local",
            Username = "operator",
            Password = "secret",
        };
        var validator = new BootstrapConfigurationValidator(_factory, _outputDir, config);

        Assert.True(Check("dispatcharr", validator).Satisfied);
    }

    [Fact]
    public void Dispatcharr_enabled_without_credentials_is_invalid()
    {
        var config = new DispatcharrConfig
        {
            Enabled = true,
            BaseUrl = "http://dispatcharr.local",
        };
        var validator = new BootstrapConfigurationValidator(_factory, _outputDir, config);

        Assert.False(Check("dispatcharr", validator).Satisfied);
    }
}
