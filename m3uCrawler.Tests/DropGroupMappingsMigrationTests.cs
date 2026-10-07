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
/// Wave D1 (2026-10-07) — migration <c>DropGroupMappings</c>: remove a
/// tabela <c>group_mappings</c> (feature GroupMapping obsoleta). Verifica
/// o <c>Up</c> (tabela deixa de existir), o <c>Down</c> (schema recriado
/// com as mesmas colunas/FK/índices) e que <c>canonical_groups</c> e a
/// coluna <c>canonical_channels.GroupId</c> permanecem intactos (esta
/// migration não toca no modelo de grupos; a remoção do enum/coluna
/// editorial é a Wave D2).
/// </summary>
public class DropGroupMappingsMigrationTests
{
    private const string PreviousMigration = "20261007150000_AddCanonicalChannelGroupFk";
    private const string LatestMigration = "20261007170000_DropGroupMappings";

    private static string NewDbPath() =>
        TestTempDb.SuitePath($"channel-catalog-tests-d1-{Guid.NewGuid():N}.db");

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
    public async Task Up_drops_group_mappings_table_and_keeps_canonical_groups()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            Assert.True(await TableExistsAsync(ctx, "group_mappings"));
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();

            Assert.False(await TableExistsAsync(ctx, "group_mappings"));
            // canonical_groups e a FK GroupId não são tocados por esta
            // migration (a remoção da coluna editorial é a Wave D2).
            Assert.True(await TableExistsAsync(ctx, "canonical_groups"));
            Assert.Contains("GroupId", await ColumnsAsync(ctx, "canonical_channels"));
            Assert.Equal(9, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM canonical_groups"));
        }

        TestTempDb.Cleanup(dbPath);
    }

    [Fact]
    public async Task Down_recreates_group_mappings_schema()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();
            Assert.False(await TableExistsAsync(ctx, "group_mappings"));
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            Assert.True(await TableExistsAsync(ctx, "group_mappings"));
            var columns = await ColumnsAsync(ctx, "group_mappings");
            Assert.Contains("Id", columns);
            Assert.Contains("SourceKind", columns);
            Assert.Contains("SourceGroupTitle", columns);
            Assert.Contains("CanonicalGroupId", columns);
            Assert.Contains("IsEnabled", columns);
            Assert.Contains("CreatedAtUtc", columns);
            Assert.Contains("UpdatedAtUtc", columns);

            var indexes = await StringsAsync(
                ctx,
                "SELECT name AS Value FROM sqlite_master WHERE type='index' AND tbl_name='group_mappings' AND name NOT LIKE 'sqlite_%'");
            Assert.Contains("IX_group_mappings_CanonicalGroupId", indexes);
            Assert.Contains("IX_group_mappings_SourceKind_SourceGroupTitle", indexes);

            var fkTables = await StringsAsync(
                ctx, "SELECT \"table\" AS Value FROM pragma_foreign_key_list('group_mappings')");
            Assert.Contains("canonical_groups", fkTables);
        }

        TestTempDb.Cleanup(dbPath);
    }

    [Fact]
    public async Task Up_is_idempotent_when_table_already_absent()
    {
        // A guarda DROP TABLE IF EXISTS permite correr o Up numa BD onde a
        // tabela já não existe (ex.: instalação criada após a remoção).
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            await ctx.Database.ExecuteSqlRawAsync("DROP TABLE group_mappings;");
            Assert.False(await TableExistsAsync(ctx, "group_mappings"));
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();
            Assert.False(await TableExistsAsync(ctx, "group_mappings"));
            Assert.Contains(
                LatestMigration,
                await StringsAsync(ctx, "SELECT MigrationId AS Value FROM __EFMigrationsHistory"));
        }

        TestTempDb.Cleanup(dbPath);
    }
}
