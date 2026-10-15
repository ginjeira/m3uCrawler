using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using m3uCrawler.Services.Catalog;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Migration <c>AddAppSecrets</c> (2026-10-15) — cria a tabela
/// <c>app_secrets</c> usada para persistir segredos cifrados (ex.: token
/// de playlists). Verifica o <c>Up</c> (tabela criada com as colunas e PK
/// esperadas), o <c>Down</c> (tabela removida) e a idempotência.
/// </summary>
public class AppSecretsMigrationTests
{
    private const string PreviousMigration = "20261008190640_AddUniqueOrderingListCountry";
    private const string LatestMigration = "20261015132022_AddAppSecrets";

    private static string NewDbPath() =>
        TestTempDb.SuitePath($"channel-catalog-tests-appsecrets-{Guid.NewGuid():N}.db");

    private static DbContextOptions<ChannelCatalogDbContext> OptionsFor(string dbPath) =>
        new DbContextOptionsBuilder<ChannelCatalogDbContext>()
            .UseSqlite($"Data Source={dbPath};Cache=Private;Foreign Keys=True")
            .Options;

    private static Task<long> CountAsync(ChannelCatalogDbContext ctx, string sql, params object[] args) =>
        ctx.Database.SqlQueryRaw<long>(sql, args).SingleAsync();

    private static Task<List<string>> StringsAsync(ChannelCatalogDbContext ctx, string sql, params object[] args) =>
        ctx.Database.SqlQueryRaw<string>(sql, args).ToListAsync();

    private static Task<List<string>> ColumnsAsync(ChannelCatalogDbContext ctx, string table) =>
        StringsAsync(ctx, "SELECT name AS Value FROM pragma_table_info({0})", table);

    private static async Task<bool> TableExistsAsync(ChannelCatalogDbContext ctx, string table) =>
        await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='table' AND name={0}", table) > 0;

    [Fact]
    public async Task Up_creates_app_secrets_table()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            Assert.False(await TableExistsAsync(ctx, "app_secrets"));
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();

            Assert.True(await TableExistsAsync(ctx, "app_secrets"));
            var columns = await ColumnsAsync(ctx, "app_secrets");
            Assert.Contains("Key", columns);
            Assert.Contains("Value", columns);
            Assert.Contains("UpdatedAtUtc", columns);

            var pkColumns = await StringsAsync(
                ctx, "SELECT name AS Value FROM pragma_table_info('app_secrets') WHERE pk > 0");
            Assert.Equal(new[] { "Key" }, pkColumns);

            var notNullColumns = await StringsAsync(
                ctx, "SELECT name AS Value FROM pragma_table_info('app_secrets') WHERE \"notnull\" = 1");
            Assert.Contains("Value", notNullColumns);
            Assert.Contains("UpdatedAtUtc", notNullColumns);

            Assert.Equal(0, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM app_secrets"));
        }

        TestTempDb.Cleanup(dbPath);
    }

    [Fact]
    public async Task Down_drops_app_secrets_table()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();
            Assert.True(await TableExistsAsync(ctx, "app_secrets"));
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            Assert.False(await TableExistsAsync(ctx, "app_secrets"));
        }

        TestTempDb.Cleanup(dbPath);
    }

    [Fact]
    public async Task Migration_is_recorded_and_rerunnable()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();
            await ctx.Database.MigrateAsync();

            Assert.Contains(
                LatestMigration,
                await StringsAsync(ctx, "SELECT MigrationId AS Value FROM __EFMigrationsHistory"));
            Assert.True(await TableExistsAsync(ctx, "app_secrets"));
        }

        TestTempDb.Cleanup(dbPath);
    }
}
