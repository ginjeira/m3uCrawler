using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using m3uCrawler.Services.Catalog;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Wave A, step 1 — migration <c>AddCanonicalChannelGroupFk</c>: adiciona a
/// FK <c>canonical_channels.GroupId</c> → <c>canonical_groups.Id</c>, o
/// índice e a reversibilidade do <c>Down</c>. <b>Wave A/H1:</b> a migration
/// deixou de semear os 9 grupos canónicos e de fazer o backfill por enum —
/// nenhum grupo é criado automaticamente. A Wave D2
/// (<c>DropEditorialGroupColumn</c>) remove a coluna editorial legada; ao
/// latest o schema tem apenas <c>GroupId</c>.
/// </summary>
public class CanonicalChannelGroupMigrationTests
{
    private const string PreviousMigration = "20260921220000_AddChannelSourceUniqueOnFingerprint";

    private static string NewDbPath() =>
        TestTempDb.SuitePath($"channel-catalog-tests-wavea-{Guid.NewGuid():N}.db");

    private static DbContextOptions<ChannelCatalogDbContext> OptionsFor(string dbPath, bool foreignKeys = true) =>
        new DbContextOptionsBuilder<ChannelCatalogDbContext>()
            .UseSqlite($"Data Source={dbPath};Cache=Private;Foreign Keys={(foreignKeys ? "True" : "False")}")
            .Options;

    private static async Task<ChannelCatalogDbContext> MigrateToPreviousAsync(
        DbContextOptions<ChannelCatalogDbContext> options)
    {
        var ctx = new ChannelCatalogDbContext(options);
        await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        return ctx;
    }

    private static async Task<long> SeedChannelAsync(ChannelCatalogDbContext ctx, string key, int editorialGroup)
    {
        var now = DateTime.UtcNow.ToString("o");
        await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO canonical_channels (Key, DisplayName, EditorialCategory, EditorialGroup, PublicationPolicy, IsEnabled, CreatedAtUtc, UpdatedAtUtc) VALUES ({key}, 'Legacy', 0, {editorialGroup}, 0, 1, {now}, {now});");
        return await ctx.Database
            .SqlQueryRaw<long>("SELECT Id AS Value FROM canonical_channels WHERE Key = {0}", key)
            .SingleAsync();
    }

    private static Task<long> CountAsync(ChannelCatalogDbContext ctx, string sql, params object[] args) =>
        ctx.Database.SqlQueryRaw<long>(sql, args).SingleAsync();

    private static Task<long?> NullableLongAsync(ChannelCatalogDbContext ctx, string sql, params object[] args) =>
        ctx.Database.SqlQueryRaw<long?>(sql, args).SingleOrDefaultAsync();

    private static Task<List<string>> StringsAsync(ChannelCatalogDbContext ctx, string sql, params object[] args) =>
        ctx.Database.SqlQueryRaw<string>(sql, args).ToListAsync();

    private static Task<List<string>> ColumnsAsync(ChannelCatalogDbContext ctx, string table) =>
        StringsAsync(ctx, "SELECT name AS Value FROM pragma_table_info({0})", table);

    [Fact]
    public async Task Fresh_DB_migrated_to_latest_creates_no_canonical_groups()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();

            // Wave A/H1 — a migration deixou de semear os 9 grupos.
            Assert.Equal(0, await CountAsync(
                ctx, "SELECT COUNT(*) AS Value FROM canonical_groups"));

            // A coluna editorial legada foi removida na Wave D2.
            Assert.DoesNotContain("EditorialGroup", await ColumnsAsync(ctx, "canonical_channels"));
            // A FK nova existe.
            Assert.Contains("GroupId", await ColumnsAsync(ctx, "canonical_channels"));
        }

        TestTempDb.Cleanup(dbPath);
    }

    [Fact]
    public async Task Up_no_longer_backfills_group_id_from_enum()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        // EditorialGroup 3 == PortugalDesporto (valor histórico do enum).
        long desportoChannelId;
        await using (var ctx = await MigrateToPreviousAsync(options))
        {
            desportoChannelId = await SeedChannelAsync(ctx, "wavea-desporto", 3);
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();

            // Wave A/H1 — sem grupos semeados, não há backfill: GroupId fica NULL.
            Assert.Equal(0, await CountAsync(
                ctx, "SELECT COUNT(*) AS Value FROM canonical_groups"));
            Assert.Null(await NullableLongAsync(
                ctx, "SELECT GroupId AS Value FROM canonical_channels WHERE Id={0}", desportoChannelId));
        }

        TestTempDb.Cleanup(dbPath);
    }

    [Fact]
    public async Task Up_does_not_map_enum_values_to_group_keys()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        await using (var ctx = await MigrateToPreviousAsync(options))
        {
            for (var enumValue = 0; enumValue <= 8; enumValue++)
            {
                await SeedChannelAsync(ctx, $"wavea-enum-{enumValue}", enumValue);
            }
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();

            // Wave A/H1 — nenhum grupo é semeado e nenhum canal é mapeado.
            Assert.Equal(0, await CountAsync(
                ctx, "SELECT COUNT(*) AS Value FROM canonical_groups"));
            Assert.Equal(9, await CountAsync(
                ctx, "SELECT COUNT(*) AS Value FROM canonical_channels WHERE GroupId IS NULL"));
        }

        TestTempDb.Cleanup(dbPath);
    }

    [Fact]
    public async Task Down_removes_group_id_column_without_error_and_creates_no_groups()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        await using (var ctx = await MigrateToPreviousAsync(options))
        {
            await SeedChannelAsync(ctx, "wavea-roundtrip", 3);
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();
            Assert.Contains("GroupId", await ColumnsAsync(ctx, "canonical_channels"));
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            Assert.DoesNotContain("GroupId", await ColumnsAsync(ctx, "canonical_channels"));
            Assert.Contains("EditorialGroup", await ColumnsAsync(ctx, "canonical_channels"));
            // Down é apenas de schema: nenhum grupo foi criado (Wave A/H1).
            Assert.Equal(0, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM canonical_groups"));
        }

        TestTempDb.Cleanup(dbPath);
    }
}
