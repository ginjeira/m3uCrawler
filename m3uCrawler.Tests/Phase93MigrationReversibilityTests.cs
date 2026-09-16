using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using m3uCrawler.Services.Catalog;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9C.3 — reversibilidade da migration
/// <c>AddCanonicalCountryAndAffinityKind</c>: proveniência explícita
/// (sem heurísticas de nome, sem <c>MIN(Id)</c>, sem deduplicação
/// arbitrária) e rollback transacional verificável.
/// </summary>
public class Phase93MigrationReversibilityTests
{
    private const string PreviousMigration = "20260916183214_AddAdminUsersAndSessions";
    private const string LatestMigration = "20260916202838_AddCanonicalCountryAndAffinityKind";
    private const string BackupTable = "affinity_migration_backup";

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), $"phase93-rev-{Guid.NewGuid():N}.db");

    private static DbContextOptions<ChannelCatalogDbContext> OptionsFor(string dbPath, bool foreignKeys = true) =>
        new DbContextOptionsBuilder<ChannelCatalogDbContext>()
            .UseSqlite($"Data Source={dbPath};Cache=Private;Foreign Keys={(foreignKeys ? "True" : "False")}")
            .Options;

    private static async Task<ChannelCatalogDbContext> MigrateToPreviousAsync(DbContextOptions<ChannelCatalogDbContext> options)
    {
        var ctx = new ChannelCatalogDbContext(options);
        await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        return ctx;
    }

    private static async Task<long> SeedLegacyChannelAsync(ChannelCatalogDbContext ctx, string key = "p93-legacy")
    {
        var now = DateTime.UtcNow.ToString("o");
        await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO canonical_channels (Key, DisplayName, EditorialCategory, EditorialGroup, PublicationPolicy, IsEnabled, CreatedAtUtc, UpdatedAtUtc) VALUES ({key}, 'Legacy', 0, 0, 0, 1, {now}, {now});");
        return await ctx.Database
            .SqlQueryRaw<long>("SELECT Id AS Value FROM canonical_channels WHERE Key = {0}", key)
            .SingleAsync();
    }

    private static async Task<long> InsertGroupAsync(
        ChannelCatalogDbContext ctx, string name, long? canonicalChannelId, string? countryCode)
    {
        var now = DateTime.UtcNow.ToString("o");
        await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO affinity_groups (Name, CountryCode, CanonicalChannelId, CreatedAtUtc, UpdatedAtUtc) VALUES ({name}, {countryCode}, {canonicalChannelId}, {now}, {now});");
        return await ctx.Database
            .SqlQueryRaw<long>("SELECT Id AS Value FROM affinity_groups WHERE Name = {0}", name)
            .SingleAsync();
    }

    private static async Task InsertMembersAsync(ChannelCatalogDbContext ctx, long groupId, params string[] members)
    {
        var now = DateTime.UtcNow.ToString("o");
        foreach (var m in members)
        {
            await ctx.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO affinity_members (NormalizedMember, AffinityGroupId, CreatedAtUtc) VALUES ({m}, {groupId}, {now});");
        }
    }

    // ---- raw readers (funcionam em qualquer schema) ----

    private static Task<long> CountAsync(ChannelCatalogDbContext ctx, string sql, params object[] args) =>
        ctx.Database.SqlQueryRaw<long>(sql, args).SingleAsync();

    private static Task<string?> StrAsync(ChannelCatalogDbContext ctx, string sql, params object[] args) =>
        ctx.Database.SqlQueryRaw<string>(sql, args).SingleOrDefaultAsync();

    private static Task<long?> NullableLongAsync(ChannelCatalogDbContext ctx, string sql, params object[] args) =>
        ctx.Database.SqlQueryRaw<long?>(sql, args).SingleOrDefaultAsync();

    private static Task<List<string>> StringsAsync(ChannelCatalogDbContext ctx, string sql, params object[] args) =>
        ctx.Database.SqlQueryRaw<string>(sql, args).ToListAsync();

    private static Task<List<string>> ColumnsAsync(ChannelCatalogDbContext ctx, string table) =>
        table switch
        {
            "affinity_members" => StringsAsync(
                ctx, "SELECT name AS Value FROM pragma_table_info('affinity_members')"),
            "affinity_groups" => StringsAsync(
                ctx, "SELECT name AS Value FROM pragma_table_info('affinity_groups')"),
            "canonical_channels" => StringsAsync(
                ctx, "SELECT name AS Value FROM pragma_table_info('canonical_channels')"),
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Tabela não suportada no teste."),
        };

    private static async Task<bool> TableExistsAsync(ChannelCatalogDbContext ctx, string table) =>
        await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='table' AND name={0}", table) > 0;

    private static Task<List<string>> AppliedMigrationsAsync(ChannelCatalogDbContext ctx) =>
        StringsAsync(ctx, "SELECT MigrationId AS Value FROM __EFMigrationsHistory");

    // =====================================================================
    // 1. Up→Down mixed lossless
    // 2. Down remove apenas artefactos do Up
    // =====================================================================

    [Fact]
    public async Task Mixed_group_is_split_on_Up_and_restored_exactly_on_Down()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        long groupId;
        long channelId;
        await using (var ctx = await MigrateToPreviousAsync(options))
        {
            channelId = await SeedLegacyChannelAsync(ctx);
            groupId = await InsertGroupAsync(ctx, "Mixed", channelId, "pt");
            await InsertMembersAsync(ctx, groupId, "mix a", "mix b", "mix c");
        }

        long generatedId;
        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();

            Assert.Equal(0, await NullableLongAsync(ctx, "SELECT Kind AS Value FROM affinity_groups WHERE Id={0}", groupId)); // Channel
            Assert.Equal("p93-legacy", await StrAsync(ctx, "SELECT CanonicalChannelKey AS Value FROM affinity_groups WHERE Id={0}", groupId));
            Assert.Null(await StrAsync(ctx, "SELECT CountryCode AS Value FROM affinity_groups WHERE Id={0}", groupId));
            Assert.Equal("mix a,mix b,mix c", string.Join(",", await StringsAsync(
                ctx, "SELECT NormalizedMember AS Value FROM affinity_members WHERE AffinityGroupId={0} ORDER BY NormalizedMember", groupId)));

            generatedId = await CountAsync(ctx, "SELECT Id AS Value FROM affinity_groups WHERE Name={0}", "Mixed #split-" + groupId);
            Assert.Equal(1, await NullableLongAsync(ctx, "SELECT Kind AS Value FROM affinity_groups WHERE Id={0}", generatedId));
            Assert.Equal("pt", await StrAsync(ctx, "SELECT CountryCode AS Value FROM affinity_groups WHERE Id={0}", generatedId));
            Assert.Equal("mix a,mix b,mix c", string.Join(",", await StringsAsync(
                ctx, "SELECT NormalizedMember AS Value FROM affinity_members WHERE AffinityGroupId={0} ORDER BY NormalizedMember", generatedId)));

            Assert.Equal(generatedId, await NullableLongAsync(
                ctx, "SELECT GeneratedCountryGroupId AS Value FROM affinity_migration_backup WHERE OriginalGroupId={0}", groupId));
            Assert.Equal("pt", await StrAsync(
                ctx, "SELECT OriginalCountryCode AS Value FROM affinity_migration_backup WHERE OriginalGroupId={0}", groupId));
        }

        // Down
        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            Assert.Equal(1, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_groups"));
            Assert.Equal(0, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_groups WHERE Name={0}", "Mixed #split-" + groupId));
            Assert.Equal(channelId, await NullableLongAsync(ctx, "SELECT CanonicalChannelId AS Value FROM affinity_groups WHERE Id={0}", groupId));
            Assert.Equal("pt", await StrAsync(ctx, "SELECT CountryCode AS Value FROM affinity_groups WHERE Id={0}", groupId));
            Assert.Equal("mix a,mix b,mix c", string.Join(",", await StringsAsync(
                ctx, "SELECT NormalizedMember AS Value FROM affinity_members WHERE AffinityGroupId={0} ORDER BY NormalizedMember", groupId)));
            Assert.False(await TableExistsAsync(ctx, BackupTable));
        }
    }

    [Fact]
    public async Task Down_removes_only_up_artifacts_and_preserves_ids_members_and_country()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        long channelOnlyId, countryOnlyId, orphanId, channelId;
        await using (var ctx = await MigrateToPreviousAsync(options))
        {
            channelId = await SeedLegacyChannelAsync(ctx);
            channelOnlyId = await InsertGroupAsync(ctx, "ChannelOnly", channelId, null);
            countryOnlyId = await InsertGroupAsync(ctx, "CountryOnly", null, "es");
            orphanId = await InsertGroupAsync(ctx, "Orphan", null, null);
            await InsertMembersAsync(ctx, channelOnlyId, "c1", "c2");
            await InsertMembersAsync(ctx, countryOnlyId, "e1");
            await InsertMembersAsync(ctx, orphanId, "o1");
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            Assert.Equal(3, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_groups"));
            Assert.Equal(channelId, await NullableLongAsync(ctx, "SELECT CanonicalChannelId AS Value FROM affinity_groups WHERE Id={0}", channelOnlyId));
            Assert.Equal("es", await StrAsync(ctx, "SELECT CountryCode AS Value FROM affinity_groups WHERE Id={0}", countryOnlyId));
            Assert.Null(await StrAsync(ctx, "SELECT CountryCode AS Value FROM affinity_groups WHERE Id={0}", orphanId));
            Assert.Equal(2, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_members WHERE AffinityGroupId={0}", channelOnlyId));
            Assert.Equal(1, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_members WHERE AffinityGroupId={0}", countryOnlyId));
            Assert.Equal(1, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_members WHERE AffinityGroupId={0}", orphanId));
        }
    }

    // =====================================================================
    // 3. Manual names never removed
    // =====================================================================

    [Fact]
    public async Task Manual_groups_with_split_or_country_names_are_never_removed_by_Down()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        long manualCountryNameId, manualSplitNameId;
        await using (var ctx = await MigrateToPreviousAsync(options))
        {
            _ = await SeedLegacyChannelAsync(ctx);
            manualCountryNameId = await InsertGroupAsync(ctx, "Manual #country-42", null, "pt");
            manualSplitNameId = await InsertGroupAsync(ctx, "Manual #split-99", null, "es");
            await InsertMembersAsync(ctx, manualCountryNameId, "mc1");
            await InsertMembersAsync(ctx, manualSplitNameId, "ms1");
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();
            // Sem grupos mixed, a proveniência fica vazia.
            Assert.Equal(0, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_migration_backup"));
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            Assert.Equal(2, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_groups"));
            Assert.Equal("pt", await StrAsync(ctx, "SELECT CountryCode AS Value FROM affinity_groups WHERE Id={0}", manualCountryNameId));
            Assert.Equal("es", await StrAsync(ctx, "SELECT CountryCode AS Value FROM affinity_groups WHERE Id={0}", manualSplitNameId));
            Assert.Equal(1, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_members WHERE AffinityGroupId={0}", manualCountryNameId));
            Assert.Equal(1, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_members WHERE AffinityGroupId={0}", manualSplitNameId));
        }
    }

    // =====================================================================
    // 4. Channel + Country coexistence: sem conflito → Down ok;
    //    conflito → Down aborta transaccionalmente.
    // =====================================================================

    [Fact]
    public async Task Down_succeeds_when_post_up_channel_and_country_have_distinct_members()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        await using (var ctx = await MigrateToPreviousAsync(options))
        {
            _ = await SeedLegacyChannelAsync(ctx);
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();
        }

        var resolver = new CatalogResolver(new TestDbContextFactory(dbPath), dbPath);
        var channel = await resolver.CreateCanonicalChannelAsync(
            "p93-coexist", "Coexist", EditorialCategory.Live, CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy.CreateEligible, true, new List<string>(), country: "pt");
        await resolver.CreateAffinityGroupAsync("Coexist", AffinityKind.Channel, channel.Key, null, new[] { "coexist" });
        await resolver.CreateAffinityGroupAsync("PT distinct", AffinityKind.Country, null, "pt", new[] { "pt distinct" });

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            Assert.False(await TableExistsAsync(ctx, BackupTable));
            Assert.Equal(2, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_groups"));
            Assert.Equal(2, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_members"));
        }
    }

    [Fact]
    public async Task Down_aborts_transactionally_when_remaining_duplicates_violate_global_uniqueness()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        await using (var ctx = await MigrateToPreviousAsync(options))
        {
            _ = await SeedLegacyChannelAsync(ctx);
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();
        }

        var resolver = new CatalogResolver(new TestDbContextFactory(dbPath), dbPath);
        var channel = await resolver.CreateCanonicalChannelAsync(
            "p93-dup", "Dup", EditorialCategory.Live, CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy.CreateEligible, true, new List<string>(), country: "pt");
        // Mesma variante em Channel e Country (permitido pelo índice filtrado).
        await resolver.CreateAffinityGroupAsync("Dup", AffinityKind.Channel, channel.Key, null, new[] { "dup shared" });
        await resolver.CreateAffinityGroupAsync("PT dup", AffinityKind.Country, null, "pt", new[] { "dup shared" });

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
                await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration));
            Assert.Contains("PHASE93", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // Transacção revertida: a migration continua aplicada e os dados intactos.
        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            var applied = await AppliedMigrationsAsync(ctx);
            Assert.Contains(LatestMigration, applied);
            Assert.Equal(2, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_groups"));
            Assert.Equal(2, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_members"));
            Assert.True(await TableExistsAsync(ctx, BackupTable));
            Assert.Contains("Kind", await ColumnsAsync(ctx, "affinity_members"));
        }
    }

    // =====================================================================
    // 6 / 12 / 13. FK inválida e falha pós-mutação → rollback total
    // (dados + schema + proveniência).
    // =====================================================================

    [Fact]
    public async Task Up_aborts_transactionally_on_invalid_fk_before_any_mutation()
    {
        var dbPath = NewDbPath();
        var fkOff = OptionsFor(dbPath, foreignKeys: false);

        await using (var ctx = await MigrateToPreviousAsync(fkOff))
        {
            await SeedLegacyChannelAsync(ctx);
            var bogusGroupId = await InsertGroupAsync(ctx, "BogusFk", 999999, null);
            await InsertMembersAsync(ctx, bogusGroupId, "bogus");
        }

        await using (var ctx = new ChannelCatalogDbContext(fkOff))
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await ctx.Database.MigrateAsync());
            Assert.Contains("PHASE93", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        await using (var ctx = new ChannelCatalogDbContext(fkOff))
        {
            // Schema inalterado (sem colunas novas, sem proveniência).
            Assert.DoesNotContain("Country", await ColumnsAsync(ctx, "canonical_channels"));
            Assert.DoesNotContain("Kind", await ColumnsAsync(ctx, "affinity_members"));
            Assert.DoesNotContain("CanonicalChannelKey", await ColumnsAsync(ctx, "affinity_groups"));
            Assert.False(await TableExistsAsync(ctx, BackupTable));

            // Dados inalterados.
            Assert.Equal(1, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_groups"));
            Assert.Equal(1, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_members"));

            // Migration não registada.
            Assert.DoesNotContain(LatestMigration, await AppliedMigrationsAsync(ctx));
        }
    }

    [Fact]
    public async Task Up_aborts_transactionally_after_provenance_when_generated_name_collides()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        long mixedId;
        await using (var ctx = await MigrateToPreviousAsync(options))
        {
            var channelId = await SeedLegacyChannelAsync(ctx);
            mixedId = await InsertGroupAsync(ctx, "Collide", channelId, "pt");
            await InsertMembersAsync(ctx, mixedId, "col a");
            // Grupo manual cujo nome colide exactamente com o nome gerado.
            _ = await InsertGroupAsync(ctx, "Collide #split-" + mixedId, null, "es");
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await Assert.ThrowsAnyAsync<Exception>(async () => await ctx.Database.MigrateAsync());
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            // Rollback total: proveniência removida, schema intacto, dados intactos.
            Assert.False(await TableExistsAsync(ctx, BackupTable));
            Assert.DoesNotContain("Kind", await ColumnsAsync(ctx, "affinity_members"));
            Assert.DoesNotContain("CanonicalChannelKey", await ColumnsAsync(ctx, "affinity_groups"));
            Assert.Equal(2, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_groups"));
            Assert.Equal(1, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_members"));
            Assert.Equal("pt", await StrAsync(ctx, "SELECT CountryCode AS Value FROM affinity_groups WHERE Id={0}", mixedId));
            Assert.DoesNotContain(LatestMigration, await AppliedMigrationsAsync(ctx));
        }
    }

    // =====================================================================
    // 7 / 8. Orphan e round-trip sem mixed
    // =====================================================================

    [Fact]
    public async Task Orphan_is_preserved_as_country_kind_and_survives_roundtrip()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        long orphanId;
        await using (var ctx = await MigrateToPreviousAsync(options))
        {
            _ = await SeedLegacyChannelAsync(ctx);
            orphanId = await InsertGroupAsync(ctx, "Orphan", null, null);
            await InsertMembersAsync(ctx, orphanId, "orphan one");
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();
            Assert.Equal(1, await NullableLongAsync(ctx, "SELECT Kind AS Value FROM affinity_groups WHERE Id={0}", orphanId));
            Assert.Null(await StrAsync(ctx, "SELECT CountryCode AS Value FROM affinity_groups WHERE Id={0}", orphanId));
            Assert.Equal(0, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_migration_backup"));
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            Assert.Equal(1, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_groups WHERE Id={0}", orphanId));
            Assert.Null(await StrAsync(ctx, "SELECT CountryCode AS Value FROM affinity_groups WHERE Id={0}", orphanId));
            Assert.Equal("orphan one", await StrAsync(ctx, "SELECT NormalizedMember AS Value FROM affinity_members WHERE AffinityGroupId={0}", orphanId));
        }
    }

    [Fact]
    public async Task Up_Down_without_mixed_is_lossless()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        long channelOnlyId, countryOnlyId, orphanId, channelId;
        await using (var ctx = await MigrateToPreviousAsync(options))
        {
            channelId = await SeedLegacyChannelAsync(ctx);
            channelOnlyId = await InsertGroupAsync(ctx, "OnlyChannel", channelId, null);
            countryOnlyId = await InsertGroupAsync(ctx, "OnlyCountry", null, "es");
            orphanId = await InsertGroupAsync(ctx, "OnlyOrphan", null, null);
            await InsertMembersAsync(ctx, channelOnlyId, "oc1");
            await InsertMembersAsync(ctx, countryOnlyId, "ec1");
            await InsertMembersAsync(ctx, orphanId, "or1");
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            Assert.Equal(3, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_groups"));
            Assert.Equal(channelId, await NullableLongAsync(ctx, "SELECT CanonicalChannelId AS Value FROM affinity_groups WHERE Id={0}", channelOnlyId));
            Assert.Equal("es", await StrAsync(ctx, "SELECT CountryCode AS Value FROM affinity_groups WHERE Id={0}", countryOnlyId));
            Assert.Null(await StrAsync(ctx, "SELECT CountryCode AS Value FROM affinity_groups WHERE Id={0}", orphanId));
            Assert.Equal(3, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_members"));
            Assert.Equal(0, await CountAsync(
                ctx,
                "SELECT COUNT(*) AS Value FROM (SELECT 1 FROM affinity_members GROUP BY NormalizedMember HAVING COUNT(*) > 1)"));
        }
    }

    // =====================================================================
    // 9. Provenance lifecycle
    // =====================================================================

    [Fact]
    public async Task Provenance_table_is_created_empty_without_mixed_and_dropped_on_Down()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        await using (var ctx = await MigrateToPreviousAsync(options))
        {
            var channelId = await SeedLegacyChannelAsync(ctx);
            var g = await InsertGroupAsync(ctx, "Plain", channelId, null);
            await InsertMembersAsync(ctx, g, "p1");
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();
            Assert.True(await TableExistsAsync(ctx, BackupTable));
            Assert.Equal(0, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_migration_backup"));
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            Assert.False(await TableExistsAsync(ctx, BackupTable));
        }
    }

    // =====================================================================
    // 10. Idempotência do latest
    // =====================================================================

    [Fact]
    public async Task Migrating_to_latest_twice_is_a_noop()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        await using (var ctx = await MigrateToPreviousAsync(options))
        {
            var channelId = await SeedLegacyChannelAsync(ctx);
            var g = await InsertGroupAsync(ctx, "Idem", channelId, "pt");
            await InsertMembersAsync(ctx, g, "id1", "id2");
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync(); // no-op
            var applied = await AppliedMigrationsAsync(ctx);
            Assert.Equal(1, applied.Count(m => m == LatestMigration));
            Assert.Equal(2, await CountAsync(ctx, "SELECT COUNT(*) AS Value FROM affinity_groups"));
        }
    }
}
