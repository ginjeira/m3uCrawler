using System;
using System.Linq;
using System.Threading.Tasks;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.SourceSelection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 13 (Wave 13-4b) — Persistência do override por canal da política
/// de selecção de fontes: chave de âmbito <c>channel:{key}</c>, unicidade,
/// idempotência, CRUD e independência da FK do canal canónico (a chave é a
/// identidade). SQLite isolado por teste; sem rede.
/// </summary>
public class SourceSelectionPolicyChannelPersistenceTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;

    public SourceSelectionPolicyChannelPersistenceTests()
    {
        _dbPath = TestTempDb.SuitePath($"source-selection-policy-channel-{Guid.NewGuid():N}.db");
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

    private async Task<CanonicalChannelEntity> CreateCanonicalAsync(string key)
        => await _resolver.CreateCanonicalChannelAsync(
            key,
            $"Channel {key}",
            EditorialCategory.Live,
            CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy.CreateEligible,
            isEnabled: true,
            normalizedAliases: Array.Empty<string>());

    [Fact]
    public async Task Upsert_channel_override_creates_one_row_and_is_idempotent()
    {
        const string key = "channel-persist-1";

        var saved = await _resolver.UpsertChannelSourceSelectionPolicyAsync(
            key, maxSourcesPerChannel: 3, preferDistinctProviders: false,
            maxSourcesPerProvider: 2, allowFallbackToSameProvider: false);

        Assert.Equal("channel:" + key, saved.ScopeKey);
        Assert.Equal(key, saved.CanonicalChannelKey);
        Assert.Equal(3, saved.MaxSourcesPerChannel);
        Assert.False(saved.PreferDistinctProviders);
        Assert.Equal(2, saved.MaxSourcesPerProvider);
        Assert.False(saved.AllowFallbackToSameProvider);

        var loaded = await _resolver.GetChannelSourceSelectionPolicyAsync(key);
        Assert.NotNull(loaded);
        Assert.Equal(saved.Id, loaded!.Id);
        Assert.Equal("channel:" + key, loaded.ScopeKey);
        Assert.Equal(key, loaded.CanonicalChannelKey);

        var list = await _resolver.ListChannelSourceSelectionPoliciesAsync();
        Assert.Single(list);
        Assert.Equal("channel:" + key, list[0].ScopeKey);

        // Idempotente: mesmo key → uma única linha.
        var again = await _resolver.UpsertChannelSourceSelectionPolicyAsync(
            key, maxSourcesPerChannel: 3, preferDistinctProviders: false,
            maxSourcesPerProvider: 2, allowFallbackToSameProvider: false);
        Assert.Equal(saved.Id, again.Id);

        await using var context = _factory.CreateDbContext();
        Assert.Equal(1, await context.SourceSelectionPolicies.CountAsync());
    }

    [Fact]
    public async Task Update_changes_all_four_values_and_delete_removes_override()
    {
        const string key = "channel-persist-2";

        await _resolver.UpsertChannelSourceSelectionPolicyAsync(key, 3, false, 2, false);

        var updated = await _resolver.UpsertChannelSourceSelectionPolicyAsync(key, 6, true, null, true);
        Assert.Equal(6, updated.MaxSourcesPerChannel);
        Assert.True(updated.PreferDistinctProviders);
        Assert.Null(updated.MaxSourcesPerProvider);
        Assert.True(updated.AllowFallbackToSameProvider);

        var loaded = await _resolver.GetChannelSourceSelectionPolicyAsync(key);
        Assert.NotNull(loaded);
        Assert.Equal(6, loaded!.MaxSourcesPerChannel);
        Assert.Null(loaded.MaxSourcesPerProvider);

        Assert.True(await _resolver.DeleteChannelSourceSelectionPolicyAsync(key));
        Assert.Null(await _resolver.GetChannelSourceSelectionPolicyAsync(key));
        Assert.Empty(await _resolver.ListChannelSourceSelectionPoliciesAsync());

        // Segundo delete é um no-op.
        Assert.False(await _resolver.DeleteChannelSourceSelectionPolicyAsync(key));

        await using var context = _factory.CreateDbContext();
        Assert.Equal(0, await context.SourceSelectionPolicies.CountAsync());
    }

    [Fact]
    public async Task Global_row_remains_intact_across_channel_crud()
    {
        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(8, false, 4, false);
        var globalBefore = await _resolver.GetOrCreateGlobalSourceSelectionPolicyAsync();

        await _resolver.UpsertChannelSourceSelectionPolicyAsync("channel-crud-a", 3, false, 2, false);
        await _resolver.UpsertChannelSourceSelectionPolicyAsync("channel-crud-a", 1, true, null, true);
        await _resolver.UpsertChannelSourceSelectionPolicyAsync("channel-crud-b", 2, true, 5, false);
        Assert.True(await _resolver.DeleteChannelSourceSelectionPolicyAsync("channel-crud-a"));

        var globalAfter = await _resolver.GetOrCreateGlobalSourceSelectionPolicyAsync();
        Assert.Equal(globalBefore.Id, globalAfter.Id);
        Assert.Equal(8, globalAfter.MaxSourcesPerChannel);
        Assert.False(globalAfter.PreferDistinctProviders);
        Assert.Equal(4, globalAfter.MaxSourcesPerProvider);
        Assert.False(globalAfter.AllowFallbackToSameProvider);
        Assert.Null(globalAfter.CanonicalChannelKey);

        await using var context = _factory.CreateDbContext();
        // Global + o override restante (channel-crud-b).
        Assert.Equal(2, await context.SourceSelectionPolicies.CountAsync());
    }

    [Fact]
    public async Task Override_survives_canonical_delete_and_recreate_with_same_key()
    {
        const string key = "channel-identity-1";

        var canonical = await CreateCanonicalAsync(key);
        var oldId = canonical.Id;

        await _resolver.UpsertChannelSourceSelectionPolicyAsync(key, 3, false, 2, false);

        Assert.True(await _resolver.DeleteCanonicalChannelAsync(oldId));

        var recreated = await CreateCanonicalAsync(key);
        Assert.NotEqual(oldId, recreated.Id);
        Assert.Equal(key, recreated.Key);

        // Sem dependência de FK: o override resolve-se pela chave.
        var resolved = await new SourceSelectionPolicyResolver(_resolver).ResolveEffectiveAsync(key);
        Assert.Equal(3, resolved.MaxSourcesPerChannel);
        Assert.False(resolved.PreferDistinctProviders);
        Assert.Equal(2, resolved.MaxSourcesPerProvider);
        Assert.False(resolved.AllowFallbackToSameProvider);

        var row = await _resolver.GetChannelSourceSelectionPolicyAsync(key);
        Assert.NotNull(row);
        Assert.Equal("channel:" + key, row!.ScopeKey);
        Assert.Equal(key, row.CanonicalChannelKey);

        await using var context = _factory.CreateDbContext();
        Assert.Equal(1, await context.SourceSelectionPolicies.CountAsync());
    }
}
