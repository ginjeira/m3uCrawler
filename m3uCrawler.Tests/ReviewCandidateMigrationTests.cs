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
/// Migration <c>AddReviewCandidates</c> (2026-10-15) — cria a tabela
/// <c>review_candidates</c> (candidatos de stream acumulados por Review,
/// W-REVIEW-04 Fase 3 Parte A). Verifica o <c>Up</c> (tabela, colunas, PK,
/// FK cascade e índices), o <c>Down</c> (tabela removida) e a idempotência.
/// </summary>
public class ReviewCandidateMigrationTests
{
    private const string PreviousMigration = "20261015132022_AddAppSecrets";
    private const string LatestMigration = "20261015151244_AddReviewCandidates";

    private static string NewDbPath() =>
        TestTempDb.SuitePath($"channel-catalog-tests-reviewcandidates-{Guid.NewGuid():N}.db");

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

    private static Task<List<string>> IndexesAsync(ChannelCatalogDbContext ctx, string table) =>
        StringsAsync(ctx, "SELECT name AS Value FROM pragma_index_list({0})", table);

    private static async Task<bool> TableExistsAsync(ChannelCatalogDbContext ctx, string table) =>
        await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='table' AND name={0}", table) > 0;

    [Fact]
    public async Task Up_creates_review_candidates_table()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            Assert.False(await TableExistsAsync(ctx, "review_candidates"));
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();

            Assert.True(await TableExistsAsync(ctx, "review_candidates"));
            var columns = await ColumnsAsync(ctx, "review_candidates");
            foreach (var expected in new[]
                     {
                         "Id", "ReviewItemId", "SourceId", "StreamUrl", "Title", "Group",
                         "Logo", "TvgId", "StreamFingerprint", "StreamFingerprintVersion", "CreatedAtUtc",
                     })
            {
                Assert.Contains(expected, columns);
            }

            var pkColumns = await StringsAsync(
                ctx, "SELECT name AS Value FROM pragma_table_info('review_candidates') WHERE pk > 0");
            Assert.Equal(new[] { "Id" }, pkColumns);

            var notNullColumns = await StringsAsync(
                ctx, "SELECT name AS Value FROM pragma_table_info('review_candidates') WHERE \"notnull\" = 1");
            Assert.Contains("ReviewItemId", notNullColumns);
            Assert.Contains("StreamUrl", notNullColumns);
            Assert.Contains("CreatedAtUtc", notNullColumns);

            // FK cascade para review_items.
            var fkTables = await StringsAsync(
                ctx, "SELECT \"table\" AS Value FROM pragma_foreign_key_list('review_candidates')");
            Assert.Contains("review_items", fkTables);

            var indexes = await IndexesAsync(ctx, "review_candidates");
            Assert.Contains("IX_review_candidates_ReviewItemId", indexes);
            Assert.Contains("IX_review_candidates_ReviewItemId_StreamFingerprint", indexes);
            Assert.Contains("IX_review_candidates_ReviewItemId_StreamUrl", indexes);

            Assert.Equal(0, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM review_candidates"));
        }

        TestTempDb.Cleanup(dbPath);
    }

    [Fact]
    public async Task Down_drops_review_candidates_table()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();
            Assert.True(await TableExistsAsync(ctx, "review_candidates"));
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            Assert.False(await TableExistsAsync(ctx, "review_candidates"));
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
            Assert.True(await TableExistsAsync(ctx, "review_candidates"));
        }

        TestTempDb.Cleanup(dbPath);
    }
}
