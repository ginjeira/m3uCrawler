using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Services.Catalog;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// DC-11a / DC-D4 — no máximo uma <see cref="OrderingListEntity"/> por país
/// (quando <c>Country</c> não é nulo). Cobre a validação no resolver
/// (create/update/duplicate) e a imposição ao nível do schema (índice
/// parcial único da migração <c>AddUniqueOrderingListCountry</c>).
/// </summary>
public class OrderingListCountryUniquenessTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly string _root;

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;

    public OrderingListCountryUniquenessTests()
    {
        _root = TestTempDb.SuitePath($"ordering-country-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "channel-catalog.db");
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        await using (var ctx = await bootstrapper.InitializeAsync())
        {
        }
        _resolver = new CatalogResolver(_factory, _dbPath);
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        TestTempDb.CleanupDirectory(_root);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Create_two_lists_with_same_country_conflicts()
    {
        await _resolver.CreateOrderingListAsync("first", "First", "pt", null);

        var ex = await Assert.ThrowsAsync<ChannelAdministrationException>(
            () => _resolver.CreateOrderingListAsync("second", "Second", "pt", null));
        Assert.Equal(ChannelAdministrationError.CountryConflict, ex.Error);
    }

    [Theory]
    [InlineData("PT", "pt")]
    [InlineData("pt", "PT")]
    [InlineData("  Pt  ", "pT")]
    public async Task Create_two_lists_with_same_country_differing_in_case_conflicts(
        string first, string second)
    {
        // DC-11a — a identidade de país é normalizada para minúsculas
        // invariantes (trim), pelo que PT/pt/Pt colidem.
        var created = await _resolver.CreateOrderingListAsync("first", "First", first, null);
        Assert.Equal(first.Trim().ToLowerInvariant(), created.Country);

        var ex = await Assert.ThrowsAsync<ChannelAdministrationException>(
            () => _resolver.CreateOrderingListAsync("second", "Second", second, null));
        Assert.Equal(ChannelAdministrationError.CountryConflict, ex.Error);
    }

    [Fact]
    public async Task Create_two_lists_with_null_country_is_allowed()
    {
        var first = await _resolver.CreateOrderingListAsync("a", "A", null, null);
        var second = await _resolver.CreateOrderingListAsync("b", "B", "   ", null);

        Assert.Null(first.Country);
        Assert.Null(second.Country);
    }

    [Fact]
    public async Task Update_to_country_used_by_another_list_conflicts_but_keeping_own_is_allowed()
    {
        var pt = await _resolver.CreateOrderingListAsync("pt-list", "PT", "pt", null);
        var other = await _resolver.CreateOrderingListAsync("other-list", "Other", "es", null);

        // Manter o próprio país é permitido.
        var kept = await _resolver.UpdateOrderingListAsync(pt.Id, "PT renomeado", "pt", null, true);
        Assert.NotNull(kept);
        Assert.Equal("pt", kept!.Country);

        // Mover para um país já usado por outra lista é rejeitado.
        var ex = await Assert.ThrowsAsync<ChannelAdministrationException>(
            () => _resolver.UpdateOrderingListAsync(other.Id, "Other", "pt", null, true));
        Assert.Equal(ChannelAdministrationError.CountryConflict, ex.Error);

        // Limpar o país é sempre permitido.
        var cleared = await _resolver.UpdateOrderingListAsync(other.Id, "Other", null, null, true);
        Assert.NotNull(cleared);
        Assert.Null(cleared!.Country);
    }

    [Fact]
    public async Task Duplicate_clears_country_and_does_not_violate_uniqueness()
    {
        var source = await _resolver.CreateOrderingListAsync("source", "Source", "pt", null);

        var clone = await _resolver.DuplicateOrderingListAsync(source.Id, "source-copy");

        Assert.Null(clone.Country);
        // A origem mantém o país.
        var reloaded = await _resolver.GetOrderingListAsync(source.Id);
        Assert.Equal("pt", reloaded!.Country);
    }

    [Fact]
    public async Task Migration_creates_partial_unique_index_and_db_rejects_duplicate_country()
    {
        var dbPath = TestTempDb.SuitePath($"ordering-country-migration-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ChannelCatalogDbContext>()
            .UseSqlite($"Data Source={dbPath};Cache=Private")
            .Options;

        try
        {
            await using var ctx = new ChannelCatalogDbContext(options);
            await ctx.Database.MigrateAsync();

            // O índice parcial único existe.
            var indexCount = await ctx.Database.SqlQueryRaw<long>(
                "SELECT COUNT(*) AS Value FROM pragma_index_list('ordering_lists') " +
                "WHERE name = 'IX_ordering_lists_Country' AND \"unique\" = 1 AND partial = 1")
                .SingleAsync();
            Assert.Equal(1L, indexCount);

            var now = DateTime.UtcNow.ToString("o");
            await ctx.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO ordering_lists (Key, Name, Country, IsEnabled, CreatedAtUtc, UpdatedAtUtc) VALUES ('db-a', 'A', 'pt', 1, {now}, {now})");

            // Segundo país 'pt' é rejeitado pela BD.
            await Assert.ThrowsAsync<SqliteException>(() =>
                ctx.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO ordering_lists (Key, Name, Country, IsEnabled, CreatedAtUtc, UpdatedAtUtc) VALUES ('db-b', 'B', 'pt', 1, {now}, {now})"));

            // DC-11a — a colação NOCASE do índice torna a unicidade
            // case-insensitive: 'PT' colide com o 'pt' já inserido.
            await Assert.ThrowsAsync<SqliteException>(() =>
                ctx.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO ordering_lists (Key, Name, Country, IsEnabled, CreatedAtUtc, UpdatedAtUtc) VALUES ('db-upper', 'U', 'PT', 1, {now}, {now})"));

            // Nulos continuam a coexistir (o filtro exclui NULL).
            await ctx.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO ordering_lists (Key, Name, Country, IsEnabled, CreatedAtUtc, UpdatedAtUtc) VALUES ('db-null-1', 'N1', NULL, 1, {now}, {now})");
            await ctx.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO ordering_lists (Key, Name, Country, IsEnabled, CreatedAtUtc, UpdatedAtUtc) VALUES ('db-null-2', 'N2', NULL, 1, {now}, {now})");
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }
}
