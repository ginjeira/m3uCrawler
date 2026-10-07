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
/// Wave A, step 1 — migration <c>AddCanonicalChannelGroupFk</c>:
/// FK <c>canonical_channels.GroupId</c> → <c>canonical_groups.Id</c>,
/// seed idempotente dos 9 grupos canónicos, backfill por enum
/// (<c>EditorialGroup</c> inteiro), guard contra <c>NULL</c> e
/// reversibilidade do <c>Down</c>. A Wave D2
/// (<c>DropEditorialGroupColumn</c>) remove a coluna editorial legada;
/// ao latest o schema tem apenas <c>GroupId</c>.
/// </summary>
public class CanonicalChannelGroupMigrationTests
{
    private const string PreviousMigration = "20260921220000_AddChannelSourceUniqueOnFingerprint";
    private const string LatestMigration = "20261007180000_DropEditorialGroupColumn";

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
    public async Task Fresh_DB_migrated_to_latest_seeds_nine_canonical_groups()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();

            Assert.Equal(9, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM canonical_groups"));
            Assert.Equal(
                "international,other,pt-desporto,pt-documentarios,pt-entretenimento,pt-filmes-series,pt-generalistas,pt-infantil,pt-ppv",
                string.Join(",", await StringsAsync(
                    ctx, "SELECT Key AS Value FROM canonical_groups ORDER BY Key")));

            // A coluna editorial legada foi removida na Wave D2.
            Assert.DoesNotContain("EditorialGroup", await ColumnsAsync(ctx, "canonical_channels"));
            // A FK nova existe.
            Assert.Contains("GroupId", await ColumnsAsync(ctx, "canonical_channels"));
        }

        TestTempDb.Cleanup(dbPath);
    }

    [Fact]
    public async Task Up_backfills_channel_group_id_from_enum_and_leaves_no_null()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        // EditorialGroup 3 == PortugalDesporto.
        long desportoChannelId;
        await using (var ctx = await MigrateToPreviousAsync(options))
        {
            desportoChannelId = await SeedChannelAsync(ctx, "wavea-desporto", 3);
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();

            var desportoGroupId = await NullableLongAsync(
                ctx, "SELECT Id AS Value FROM canonical_groups WHERE Key='pt-desporto'");
            Assert.True(desportoGroupId.HasValue);
            Assert.Equal(
                desportoGroupId,
                await NullableLongAsync(
                    ctx, "SELECT GroupId AS Value FROM canonical_channels WHERE Id={0}", desportoChannelId));

            // O guard/backfill garante zero canais com GroupId NULL.
            Assert.Equal(0, await CountAsync(
                ctx, "SELECT COUNT(*) AS Value FROM canonical_channels WHERE GroupId IS NULL"));
        }

        TestTempDb.Cleanup(dbPath);
    }

    [Fact]
    public async Task Up_maps_every_enum_value_to_the_expected_group_key()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        var expectedKeys = new[]
        {
            "pt-generalistas", "pt-filmes-series", "pt-entretenimento", "pt-desporto",
            "pt-infantil", "pt-documentarios", "pt-ppv", "international", "other",
        };

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

            for (var enumValue = 0; enumValue <= 8; enumValue++)
            {
                var key = await ctx.Database
                    .SqlQueryRaw<string>(
                        """
                        SELECT g.Key AS Value
                        FROM canonical_channels c
                        JOIN canonical_groups g ON g.Id = c.GroupId
                        WHERE c.Key = {0}
                        """,
                        $"wavea-enum-{enumValue}")
                    .SingleAsync();
                Assert.Equal(expectedKeys[enumValue], key);
            }

            Assert.Equal(0, await CountAsync(
                ctx, "SELECT COUNT(*) AS Value FROM canonical_channels WHERE GroupId IS NULL"));
        }

        TestTempDb.Cleanup(dbPath);
    }

    [Fact]
    public async Task Down_removes_group_id_column_without_error_and_keeps_seeded_groups()
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
            // Down é apenas de schema: os grupos semeados persistem
            // (podem ter sido editados/adoptados pelo operador).
            Assert.Equal(9, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM canonical_groups"));
        }

        TestTempDb.Cleanup(dbPath);
    }
}
