using System;
using System.Linq;
using System.Threading.Tasks;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.SourceSelection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 13 (Wave 13-4) — Persistência da política global de selecção de
/// fontes: migration aditiva, criação lazy com defaults, round-trip,
/// unicidade de <c>ScopeKey</c> e sobrevivência a re-inicializações.
/// SQLite isolado por teste; sem rede.
/// </summary>
public class SourceSelectionPolicyPersistenceTests : IAsyncLifetime
{
    /// <summary>Última migration anterior a <c>AddSourceSelectionPolicies</c>.</summary>
    private const string MigrationBeforeSourceSelectionPolicies = "20260916220058_AddLiveRuns";

    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;

    public SourceSelectionPolicyPersistenceTests()
    {
        _dbPath = TestTempDb.SuitePath($"source-selection-policy-{Guid.NewGuid():N}.db");
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
    public async Task Additive_migration_creates_empty_source_selection_policies_table()
    {
        await using var context = _factory.CreateDbContext();

        // Consultar a tabela prova que existe (a migration é puramente aditiva).
        Assert.Equal(0, await context.SourceSelectionPolicies.CountAsync());
    }

    [Fact]
    public async Task Get_or_create_global_creates_one_default_row_and_is_idempotent()
    {
        var created = await _resolver.GetOrCreateGlobalSourceSelectionPolicyAsync();

        Assert.Equal("global", created.ScopeKey);
        Assert.Null(created.CanonicalChannelKey);
        Assert.Equal(10, created.MaxSourcesPerChannel);
        Assert.True(created.PreferDistinctProviders);
        Assert.Null(created.MaxSourcesPerProvider);
        Assert.True(created.AllowFallbackToSameProvider);

        var again = await _resolver.GetOrCreateGlobalSourceSelectionPolicyAsync();
        Assert.Equal(created.Id, again.Id);

        await using var context = _factory.CreateDbContext();
        var rows = await context.SourceSelectionPolicies.AsNoTracking().ToListAsync();
        Assert.Single(rows);
        Assert.Equal("global", rows[0].ScopeKey);
        Assert.Null(rows[0].CanonicalChannelKey);
        Assert.Equal(10, rows[0].MaxSourcesPerChannel);
    }

    [Fact]
    public async Task Upsert_round_trips_all_values_and_timestamps()
    {
        var saved = await _resolver.UpsertGlobalSourceSelectionPolicyAsync(
            maxSourcesPerChannel: 0,
            preferDistinctProviders: false,
            maxSourcesPerProvider: 5,
            allowFallbackToSameProvider: true);

        Assert.Equal(0, saved.MaxSourcesPerChannel);
        Assert.False(saved.PreferDistinctProviders);
        Assert.Equal(5, saved.MaxSourcesPerProvider);
        Assert.True(saved.AllowFallbackToSameProvider);
        Assert.True(saved.UpdatedAtUtc >= saved.CreatedAtUtc);

        var loaded = await _resolver.GetOrCreateGlobalSourceSelectionPolicyAsync();
        Assert.Equal(saved.Id, loaded.Id);
        Assert.Equal(0, loaded.MaxSourcesPerChannel);
        Assert.False(loaded.PreferDistinctProviders);
        Assert.Equal(5, loaded.MaxSourcesPerProvider);
        Assert.True(loaded.AllowFallbackToSameProvider);
        Assert.Null(loaded.CanonicalChannelKey);

        await using var context = _factory.CreateDbContext();
        Assert.Equal(1, await context.SourceSelectionPolicies.CountAsync());
    }

    [Fact]
    public async Task Nullable_provider_limit_round_trips_null_and_non_null()
    {
        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(4, true, maxSourcesPerProvider: null, allowFallbackToSameProvider: false);
        var withNull = await _resolver.GetOrCreateGlobalSourceSelectionPolicyAsync();
        Assert.Null(withNull.MaxSourcesPerProvider);

        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(4, true, maxSourcesPerProvider: 2, allowFallbackToSameProvider: false);
        var withValue = await _resolver.GetOrCreateGlobalSourceSelectionPolicyAsync();
        Assert.Equal(2, withValue.MaxSourcesPerProvider);
        Assert.False(withValue.AllowFallbackToSameProvider);

        await using var context = _factory.CreateDbContext();
        Assert.Equal(1, await context.SourceSelectionPolicies.CountAsync());
    }

    [Fact]
    public async Task Duplicate_scope_key_is_rejected_by_the_database()
    {
        await using (var seed = _factory.CreateDbContext())
        {
            seed.SourceSelectionPolicies.Add(NewRow(maxSourcesPerChannel: 1));
            await seed.SaveChangesAsync();
        }

        await using var context = _factory.CreateDbContext();
        context.SourceSelectionPolicies.Add(NewRow(maxSourcesPerChannel: 2));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Reinitialize_applies_additive_migration_and_preserves_preexisting_rows()
    {
        // Simula uma BD criada ANTES da wave 13-4: desce até à última
        // migration anterior, onde a tabela ainda não existe.
        await using (var downgrade = _factory.CreateDbContext())
        {
            await downgrade.GetService<IMigrator>()
                .MigrateAsync(MigrationBeforeSourceSelectionPolicies);
            Assert.False(await TableExistsAsync(downgrade, "source_selection_policies"));
        }

        // Pré-existente: um job agendado inserido antes de re-inicializar.
        var jobName = $"pre-13-4-{Guid.NewGuid():N}";
        await using (var seed = _factory.CreateDbContext())
        {
            seed.ScheduledJobs.Add(new ScheduledJobEntity
            {
                Name = jobName,
                CronExpression = "0 * * * *",
                ActionName = "discoverTelegram",
                IsEnabled = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();

        await using var context = _factory.CreateDbContext();
        Assert.True(await TableExistsAsync(context, "source_selection_policies"));
        Assert.Equal(1, await context.ScheduledJobs.CountAsync(j => j.Name == jobName));
        Assert.Equal(0, await context.SourceSelectionPolicies.CountAsync());
    }

    [Fact]
    public async Task Resolver_without_persisted_row_returns_defaults()
    {
        await using (var context = _factory.CreateDbContext())
        {
            Assert.Equal(0, await context.SourceSelectionPolicies.CountAsync());
        }

        var policy = await new SourceSelectionPolicyResolver(_resolver).ResolveGlobalAsync();

        Assert.Equal(
            SourceSelectionDefaults.DefaultPolicy.MaxSourcesPerChannel,
            policy.MaxSourcesPerChannel);
        Assert.Equal(
            SourceSelectionDefaults.DefaultPolicy.PreferDistinctProviders,
            policy.PreferDistinctProviders);
        Assert.Equal(
            SourceSelectionDefaults.DefaultPolicy.MaxSourcesPerProvider,
            policy.MaxSourcesPerProvider);
        Assert.Equal(
            SourceSelectionDefaults.DefaultPolicy.AllowFallbackToSameProvider,
            policy.AllowFallbackToSameProvider);
        Assert.Equal(10, policy.MaxSourcesPerChannel);
        Assert.Null(policy.MaxSourcesPerProvider);
    }

    private static SourceSelectionPolicyEntity NewRow(int maxSourcesPerChannel) => new()
    {
        ScopeKey = "global",
        CanonicalChannelKey = null,
        MaxSourcesPerChannel = maxSourcesPerChannel,
        PreferDistinctProviders = true,
        MaxSourcesPerProvider = null,
        AllowFallbackToSameProvider = true,
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow,
    };

    private static async Task<bool> TableExistsAsync(ChannelCatalogDbContext context, string tableName)
    {
        var count = await context.Database
            .SqlQueryRaw<long>(
                "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = {0}",
                tableName)
            .SingleAsync();
        return count > 0;
    }
}
