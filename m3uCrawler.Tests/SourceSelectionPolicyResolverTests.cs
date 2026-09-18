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
    public async Task Resolver_exposes_channel_effective_and_batch_resolution_surface()
    {
        // Wave 13-4b substitui o guard reflexivo "apenas global": passa a
        // existir resolução efectiva por canal e leitura em lote.
        var declared = typeof(SourceSelectionPolicyResolver)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(nameof(SourceSelectionPolicyResolver.ResolveGlobalAsync), declared);
        Assert.Contains(nameof(SourceSelectionPolicyResolver.ResolveEffectiveAsync), declared);
        Assert.Contains(nameof(SourceSelectionPolicyResolver.LoadEffectivePoliciesAsync), declared);

        // Precedência: override por canal > global > defaults.
        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(9, true, null, true);

        var globalOnly = await new SourceSelectionPolicyResolver(_resolver)
            .ResolveEffectiveAsync("nao-existe");
        Assert.Equal(9, globalOnly.MaxSourcesPerChannel);
        Assert.True(globalOnly.PreferDistinctProviders);
        Assert.Null(globalOnly.MaxSourcesPerProvider);
        Assert.True(globalOnly.AllowFallbackToSameProvider);

        await _resolver.UpsertChannelSourceSelectionPolicyAsync("precedence-k", 3, false, 2, false);
        var overridden = await new SourceSelectionPolicyResolver(_resolver)
            .ResolveEffectiveAsync("precedence-k");
        Assert.Equal(3, overridden.MaxSourcesPerChannel);
        Assert.False(overridden.PreferDistinctProviders);
        Assert.Equal(2, overridden.MaxSourcesPerProvider);
        Assert.False(overridden.AllowFallbackToSameProvider);
    }

    [Fact]
    public async Task Resolve_effective_returns_channel_override_as_complete_replacement()
    {
        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(10, true, null, true);
        await _resolver.UpsertChannelSourceSelectionPolicyAsync(
            "complete-k", maxSourcesPerChannel: 3, preferDistinctProviders: false,
            maxSourcesPerProvider: 2, allowFallbackToSameProvider: false);

        var policy = await new SourceSelectionPolicyResolver(_resolver).ResolveEffectiveAsync("complete-k");

        // Substituição integral: todos os quatro campos vêm do override.
        Assert.Equal(3, policy.MaxSourcesPerChannel);
        Assert.False(policy.PreferDistinctProviders);
        Assert.Equal(2, policy.MaxSourcesPerProvider);
        Assert.False(policy.AllowFallbackToSameProvider);
        Assert.NotEqual(
            SourceSelectionDefaults.DefaultPolicy.MaxSourcesPerChannel,
            policy.MaxSourcesPerChannel);
    }

    [Fact]
    public async Task Resolve_effective_without_override_falls_back_to_the_global_rows()
    {
        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(
            maxSourcesPerChannel: 7, preferDistinctProviders: false,
            maxSourcesPerProvider: 3, allowFallbackToSameProvider: false);

        var policy = await new SourceSelectionPolicyResolver(_resolver).ResolveEffectiveAsync("sem-override");

        Assert.Equal(7, policy.MaxSourcesPerChannel);
        Assert.False(policy.PreferDistinctProviders);
        Assert.Equal(3, policy.MaxSourcesPerProvider);
        Assert.False(policy.AllowFallbackToSameProvider);
    }

    [Fact]
    public async Task Resolve_effective_on_fresh_db_returns_defaults()
    {
        var policy = await new SourceSelectionPolicyResolver(_resolver).ResolveEffectiveAsync("historico");

        Assert.Equal(10, policy.MaxSourcesPerChannel);
        Assert.True(policy.PreferDistinctProviders);
        Assert.Null(policy.MaxSourcesPerProvider);
        Assert.True(policy.AllowFallbackToSameProvider);
    }

    [Fact]
    public async Task Load_effective_policies_returns_global_and_all_overrides_in_scope()
    {
        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(8, false, 4, false);
        await _resolver.UpsertChannelSourceSelectionPolicyAsync("batch-k", 2, true, null, true);

        var set = await new SourceSelectionPolicyResolver(_resolver).LoadEffectivePoliciesAsync();

        Assert.Equal(new SourceSelectionPolicy(8, false, 4, false), set.Global);
        Assert.Equal(1, set.OverrideCount);

        var overridden = set.Resolve("batch-k");
        Assert.Equal(2, overridden.MaxSourcesPerChannel);
        Assert.True(overridden.PreferDistinctProviders);
        Assert.Null(overridden.MaxSourcesPerProvider);
        Assert.True(overridden.AllowFallbackToSameProvider);

        // Chave sem override resolve para a global (identical à persistida).
        Assert.Same(set.Global, set.Resolve("outro"));
        Assert.Same(set.Global, set.Resolve(null));
    }
}
