using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.SourceSelection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 13 (Wave 13-4) — <see cref="SourceSelectionPolicyResolver"/>:
/// mapeamento persistência→record, fallback para os defaults e ausência de
/// resolução por canal nesta wave (apenas a política global existe).
/// </summary>
public class SourceSelectionPolicyResolverTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;

    public SourceSelectionPolicyResolverTests()
    {
        _dbPath = TestTempDb.SuitePath($"source-selection-policy-resolver-{Guid.NewGuid():N}.db");
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
        TestTempDb.Cleanup(_dbPath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Persisted_values_map_to_the_policy_record()
    {
        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(
            maxSourcesPerChannel: 7,
            preferDistinctProviders: false,
            maxSourcesPerProvider: 3,
            allowFallbackToSameProvider: false);

        var policy = await new SourceSelectionPolicyResolver(_resolver).ResolveGlobalAsync();

        Assert.Equal(7, policy.MaxSourcesPerChannel);
        Assert.False(policy.PreferDistinctProviders);
        Assert.Equal(3, policy.MaxSourcesPerProvider);
        Assert.False(policy.AllowFallbackToSameProvider);
    }

    [Fact]
    public async Task Persisted_null_provider_limit_maps_to_null()
    {
        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(
            maxSourcesPerChannel: 5,
            preferDistinctProviders: true,
            maxSourcesPerProvider: null,
            allowFallbackToSameProvider: true);

        var policy = await new SourceSelectionPolicyResolver(_resolver).ResolveGlobalAsync();

        Assert.Equal(5, policy.MaxSourcesPerChannel);
        Assert.True(policy.PreferDistinctProviders);
        Assert.Null(policy.MaxSourcesPerProvider);
        Assert.True(policy.AllowFallbackToSameProvider);
    }

    [Fact]
    public async Task No_persisted_row_returns_the_default_policy()
    {
        await using (var context = _factory.CreateDbContext())
        {
            Assert.Equal(0, await context.SourceSelectionPolicies.CountAsync());
        }

        var policy = await new SourceSelectionPolicyResolver(_resolver).ResolveGlobalAsync();

        Assert.Equal(SourceSelectionDefaults.DefaultPolicy, policy);
    }

    [Fact]
    public async Task Resolver_exposes_only_global_resolution()
    {
        // Wave 13-4 expõe apenas a resolução global. A resolução por canal
        // ("channel:{key}") está reservada para 13-4b — a asserção é por
        // comportamento: existe um único método público declarado e nenhum
        // aceita uma chave de canal.
        var declared = typeof(SourceSelectionPolicyResolver)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.Equal(
            new[] { nameof(SourceSelectionPolicyResolver.ResolveGlobalAsync) },
            declared.Select(m => m.Name).ToArray());
        Assert.DoesNotContain(
            declared,
            m => m.GetParameters().Any(p =>
                p.Name is not null
                && (p.Name.Contains("Channel", StringComparison.OrdinalIgnoreCase)
                    || p.Name.Contains("Scope", StringComparison.OrdinalIgnoreCase))));

        var policy = await new SourceSelectionPolicyResolver(_resolver).ResolveGlobalAsync();
        Assert.NotNull(policy);
    }
}
