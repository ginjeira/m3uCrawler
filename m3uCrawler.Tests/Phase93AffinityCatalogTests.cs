using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9C.3 — afinidades com discriminator <see cref="AffinityKind"/>,
/// identidade estável (<c>CanonicalChannel.Key</c>), país no canal
/// canónico e delimiter global. Cobre modelo, migration (backfill +
/// split de mixed + órfãos) e resolução.
/// </summary>
public class Phase93AffinityCatalogTests
{
    private const string PreviousMigration = "20260916183214_AddAdminUsersAndSessions";

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), $"phase93-affinity-{Guid.NewGuid():N}.db");

    private static DbContextOptions<ChannelCatalogDbContext> OptionsFor(string dbPath) =>
        new DbContextOptionsBuilder<ChannelCatalogDbContext>()
            .UseSqlite($"Data Source={dbPath};Cache=Private")
            .Options;

    private static async Task<string> InitializeCatalogAsync(string dbPath)
    {
        var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        return dbPath;
    }

    private static async Task<string> SeedCanonicalChannelAsync(CatalogResolver resolver, string key, string displayName, string? country = null)
    {
        var channel = await resolver.CreateCanonicalChannelAsync(
            key, displayName,
            EditorialCategory.Live, CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy.CreateEligible, isEnabled: true,
            normalizedAliases: new List<string>(),
            country: country);
        return channel.Key;
    }

    // ===================== R2: cardinalidade e unicidade =====================

    [Fact]
    public async Task Channel_affinity_is_limited_to_one_per_canonical_key()
    {
        var dbPath = await InitializeCatalogAsync(NewDbPath());
        var resolver = new CatalogResolver(new TestDbContextFactory(dbPath), dbPath);
        var key = await SeedCanonicalChannelAsync(resolver, "p93-rtp1", "RTP 1", "pt");

        await resolver.CreateAffinityGroupAsync(
            "RTP 1", AffinityKind.Channel, key, null, new[] { "rtp um" });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.CreateAffinityGroupAsync(
                "RTP 1 duplicado", AffinityKind.Channel, key, null, new[] { "rtp numero um" }));
    }

    [Fact]
    public async Task Channel_affinity_requires_existing_canonical_key()
    {
        var dbPath = await InitializeCatalogAsync(NewDbPath());
        var resolver = new CatalogResolver(new TestDbContextFactory(dbPath), dbPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.CreateAffinityGroupAsync(
                "Fantasma", AffinityKind.Channel, "nao-existe", null, new[] { "ghost" }));
    }

    [Fact]
    public async Task Country_affinity_requires_country_code_and_allows_shared_member_with_channel()
    {
        var dbPath = await InitializeCatalogAsync(NewDbPath());
        var resolver = new CatalogResolver(new TestDbContextFactory(dbPath), dbPath);
        var key = await SeedCanonicalChannelAsync(resolver, "x", "X", "pt");

        await resolver.CreateAffinityGroupAsync(
            "Canal X", AffinityKind.Channel, key, null, new[] { "variante partilhada" });

        // Mesma variante numa Country affinity é permitida (usos
        // semânticos diferentes). O índice único aplica-se só a Channel.
        var country = await resolver.CreateAffinityGroupAsync(
            "País PT extra", AffinityKind.Country, null, "pt", new[] { "variante partilhada" });
        Assert.Equal(AffinityKind.Country, country.Kind);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.CreateAffinityGroupAsync(
                "País inválido", AffinityKind.Country, null, "", new[] { "sem país" }));
    }

    [Fact]
    public async Task Filtered_unique_index_rejects_two_channel_affinities_with_same_member()
    {
        var dbPath = await InitializeCatalogAsync(NewDbPath());
        var resolver = new CatalogResolver(new TestDbContextFactory(dbPath), dbPath);
        var keyA = await SeedCanonicalChannelAsync(resolver, "a", "A");
        var keyB = await SeedCanonicalChannelAsync(resolver, "b", "B");

        IDbContextFactory<ChannelCatalogDbContext> factory = new TestDbContextFactory(dbPath);
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            var now = DateTime.UtcNow;
            ctx.AffinityGroups.Add(new AffinityGroupEntity
            {
                Name = "A", Kind = AffinityKind.Channel, CanonicalChannelKey = keyA,
                CreatedAtUtc = now, UpdatedAtUtc = now,
                Members = { new AffinityMemberEntity { NormalizedMember = "partilhado", Kind = AffinityKind.Channel, CreatedAtUtc = now } },
            });
            ctx.AffinityGroups.Add(new AffinityGroupEntity
            {
                Name = "B", Kind = AffinityKind.Channel, CanonicalChannelKey = keyB,
                CreatedAtUtc = now, UpdatedAtUtc = now,
                Members = { new AffinityMemberEntity { NormalizedMember = "partilhado", Kind = AffinityKind.Channel, CreatedAtUtc = now } },
            });

            await Assert.ThrowsAnyAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Channel_member_can_coexist_with_country_member()
    {
        var dbPath = await InitializeCatalogAsync(NewDbPath());
        var resolver = new CatalogResolver(new TestDbContextFactory(dbPath), dbPath);
        var key = await SeedCanonicalChannelAsync(resolver, "c", "C");

        await resolver.CreateAffinityGroupAsync("C", AffinityKind.Channel, key, null, new[] { "coexistente" });
        await resolver.CreateAffinityGroupAsync("PT", AffinityKind.Country, null, "pt", new[] { "coexistente" });

        var groups = await resolver.ListAffinityGroupsAsync();
        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.Kind == AffinityKind.Channel);
        Assert.Contains(groups, g => g.Kind == AffinityKind.Country);
    }

    [Fact]
    public async Task Update_rejects_changing_the_canonical_channel()
    {
        var dbPath = await InitializeCatalogAsync(NewDbPath());
        var resolver = new CatalogResolver(new TestDbContextFactory(dbPath), dbPath);
        var keyA = await SeedCanonicalChannelAsync(resolver, "a", "A");
        var keyB = await SeedCanonicalChannelAsync(resolver, "b", "B");

        var group = await resolver.CreateAffinityGroupAsync("A", AffinityKind.Channel, keyA, null, new[] { "a var" });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.UpdateAffinityGroupAsync(group.Id, "A", AffinityKind.Channel, keyB, null, new[] { "a var" }));

        var updated = await resolver.UpdateAffinityGroupAsync(
            group.Id, "A renomeada", AffinityKind.Channel, keyA, null, new[] { "a var", "a var 2" });
        Assert.NotNull(updated);
        Assert.Equal(keyA, updated!.CanonicalChannelKey);
        Assert.Equal(2, updated.Members.Count);
    }

    // ===================== Identity / rename stability =====================

    [Fact]
    public async Task Resolution_uses_channel_affinity_and_survives_display_name_rename()
    {
        var dbPath = await InitializeCatalogAsync(NewDbPath());
        var resolver = new CatalogResolver(new TestDbContextFactory(dbPath), dbPath);
        var key = await SeedCanonicalChannelAsync(resolver, "p93-rtp1", "RTP 1", "pt");
        await resolver.CreateAffinityGroupAsync("RTP 1", AffinityKind.Channel, key, null, new[] { "variante rtp" });

        var before = await resolver.ResolveAsync("variante rtp");
        Assert.Equal(CatalogResolutionKind.Canonical, before.Kind);
        Assert.Equal(key, before.CanonicalKey);
        Assert.Equal("RTP 1", before.DisplayName);

        var channel = await resolver.GetCanonicalChannelAsync(before.CanonicalChannelId!.Value);
        await resolver.UpdateCanonicalChannelAsync(
            channel!.Id, "RTP 1 renomeado",
            channel.EditorialCategory, channel.EditorialGroup, channel.PublicationPolicy,
            channel.IsEnabled, channel.Country);

        var after = await resolver.ResolveAsync("variante rtp");
        Assert.Equal(CatalogResolutionKind.Canonical, after.Kind);
        Assert.Equal(key, after.CanonicalKey);
        Assert.Equal("RTP 1 renomeado", after.DisplayName);
    }

    [Fact]
    public async Task Country_affinity_does_not_resolve_to_a_canonical_channel()
    {
        var dbPath = await InitializeCatalogAsync(NewDbPath());
        var resolver = new CatalogResolver(new TestDbContextFactory(dbPath), dbPath);
        await resolver.CreateAffinityGroupAsync("PT extra", AffinityKind.Country, null, "pt", new[] { "indicador pt" });

        var resolution = await resolver.ResolveAsync("indicador pt");
        Assert.Equal(CatalogResolutionKind.Unknown, resolution.Kind);
        Assert.Null(resolution.CanonicalKey);
    }

    [Fact]
    public async Task Identity_rule_keeps_precedence_over_affinity()
    {
        var dbPath = await InitializeCatalogAsync(NewDbPath());
        var resolver = new CatalogResolver(new TestDbContextFactory(dbPath), dbPath);
        var key = await SeedCanonicalChannelAsync(resolver, "p93-rtp1", "RTP 1");
        await resolver.CreateAffinityGroupAsync("RTP 1", AffinityKind.Channel, key, null, new[] { "precedencia" });
        await resolver.CreateIdentityRuleAsync("precedencia", RuleDisposition.ReviewOnly, "teste");

        var resolution = await resolver.ResolveAsync("precedencia");
        Assert.Equal(CatalogResolutionKind.Rule, resolution.Kind);
    }

    [Fact]
    public async Task ListChannelAffinityKeys_exposes_keys_in_use()
    {
        var dbPath = await InitializeCatalogAsync(NewDbPath());
        var resolver = new CatalogResolver(new TestDbContextFactory(dbPath), dbPath);
        var key = await SeedCanonicalChannelAsync(resolver, "key-in-use", "Em uso");
        await resolver.CreateAffinityGroupAsync("Em uso", AffinityKind.Channel, key, null, new[] { "k var" });
        await resolver.CreateAffinityGroupAsync("PT", AffinityKind.Country, null, "pt", new[] { "outra" });

        var keys = await resolver.ListChannelAffinityKeysAsync();
        Assert.Equal(new[] { key }, keys.ToArray());
    }

    // ===================== R1: canonical country =====================

    [Fact]
    public async Task Canonical_channel_country_is_optional_and_does_not_change_key()
    {
        var dbPath = await InitializeCatalogAsync(NewDbPath());
        var resolver = new CatalogResolver(new TestDbContextFactory(dbPath), dbPath);

        var withCountry = await resolver.CreateCanonicalChannelAsync(
            "com-pais", "Com País", EditorialCategory.Live, CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy.CreateEligible, true, new List<string>(), country: "pt");
        Assert.Equal("pt", withCountry.Country);
        Assert.Equal("com-pais", withCountry.Key);

        var without = await resolver.CreateCanonicalChannelAsync(
            "sem-pais", "Sem País", EditorialCategory.Live, CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy.CreateEligible, true, new List<string>());
        Assert.Null(without.Country);
    }

    // ===================== R3: global delimiter =====================

    [Fact]
    public void AppSettingsStore_defaults_to_comma_and_sanitizes_invalid_values()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"phase93-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new AppSettingsStore(dir);
            Assert.Equal(",", store.Load().AffinityVariantDelimiter);

            store.Save(new AppSettings { AffinityVariantDelimiter = "" });
            Assert.Equal(",", store.Load().AffinityVariantDelimiter);

            store.Save(new AppSettings { AffinityVariantDelimiter = ";;" });
            Assert.Equal(";;", store.Load().AffinityVariantDelimiter);

            store.Save(new AppSettings { AffinityVariantDelimiter = "a\nb\nc\nd" });
            Assert.Equal(",", store.Load().AffinityVariantDelimiter);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ===================== Migration: backfill + split + orphans =====================

    [Fact]
    public async Task Migration_backfills_key_and_splits_mixed_groups_without_losing_members()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        long mixedId;
        long countryOnlyId;
        long orphanId;
        string channelKey;

        // 1) Migrar apenas até à migration anterior e inserir dados legacy.
        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            // O seed é aplicado pelo ChannelCatalogBootstrapper após a
            // migration, não pela migration em si; criar aqui um canal
            // canónico legacy. Nesta versão do schema ainda não existe
            // Country — inserir por SQL bruto.
            channelKey = "p93-legacy";
            var now = DateTime.UtcNow.ToString("o");
            await ctx.Database.ExecuteSqlRawAsync(
                "INSERT INTO canonical_channels (Key, DisplayName, EditorialCategory, EditorialGroup, PublicationPolicy, IsEnabled, CreatedAtUtc, UpdatedAtUtc) VALUES ('p93-legacy', 'Legacy', 0, 0, 0, 1, {0}, {0});",
                now);
            var channelId = await ctx.Database
                .SqlQueryRaw<long>("SELECT Id AS Value FROM canonical_channels WHERE Key = 'p93-legacy'")
                .SingleAsync();
            await ctx.Database.ExecuteSqlRawAsync(
                "INSERT INTO affinity_groups (Name, CountryCode, CanonicalChannelId, CreatedAtUtc, UpdatedAtUtc) VALUES ('mixed', 'pt', {0}, {1}, {1});",
                channelId, now);
            await ctx.Database.ExecuteSqlRawAsync(
                "INSERT INTO affinity_groups (Name, CountryCode, CanonicalChannelId, CreatedAtUtc, UpdatedAtUtc) VALUES ('country only', 'es', NULL, {0}, {0});",
                now);
            await ctx.Database.ExecuteSqlRawAsync(
                "INSERT INTO affinity_groups (Name, CountryCode, CanonicalChannelId, CreatedAtUtc, UpdatedAtUtc) VALUES ('orphan', NULL, NULL, {0}, {0});",
                now);

            mixedId = await ctx.Database.SqlQueryRaw<long>("SELECT Id AS Value FROM affinity_groups WHERE Name = 'mixed'").SingleAsync();
            countryOnlyId = await ctx.Database.SqlQueryRaw<long>("SELECT Id AS Value FROM affinity_groups WHERE Name = 'country only'").SingleAsync();
            orphanId = await ctx.Database.SqlQueryRaw<long>("SELECT Id AS Value FROM affinity_groups WHERE Name = 'orphan'").SingleAsync();

            await ctx.Database.ExecuteSqlRawAsync(
                "INSERT INTO affinity_members (NormalizedMember, AffinityGroupId, CreatedAtUtc) VALUES ('mix a', {0}, {1}), ('mix b', {0}, {1});",
                mixedId, now);
            await ctx.Database.ExecuteSqlRawAsync(
                "INSERT INTO affinity_members (NormalizedMember, AffinityGroupId, CreatedAtUtc) VALUES ('esp a', {0}, {1});",
                countryOnlyId, now);
            await ctx.Database.ExecuteSqlRawAsync(
                "INSERT INTO affinity_members (NormalizedMember, AffinityGroupId, CreatedAtUtc) VALUES ('orph a', {0}, {1});",
                orphanId, now);
        }

        // 2) Migrar até à última: backfill + split.
        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();

            var mixed = await ctx.AffinityGroups
                .Include(g => g.Members)
                .SingleAsync(g => g.Id == mixedId);
            Assert.Equal(AffinityKind.Channel, mixed.Kind);
            Assert.Equal(channelKey, mixed.CanonicalChannelKey);
            Assert.Null(mixed.CountryCode);
            Assert.Equal(new[] { "mix a", "mix b" }, mixed.Members.Select(m => m.NormalizedMember).OrderBy(x => x).ToArray());
            Assert.All(mixed.Members, m => Assert.Equal(AffinityKind.Channel, m.Kind));

            var countryCopy = await ctx.AffinityGroups
                .Include(g => g.Members)
                .SingleAsync(g => g.Name == "mixed #split-" + mixedId);
            Assert.Equal(AffinityKind.Country, countryCopy.Kind);
            Assert.Equal("pt", countryCopy.CountryCode);
            Assert.Null(countryCopy.CanonicalChannelKey);
            Assert.Equal(new[] { "mix a", "mix b" }, countryCopy.Members.Select(m => m.NormalizedMember).OrderBy(x => x).ToArray());
            Assert.All(countryCopy.Members, m => Assert.Equal(AffinityKind.Country, m.Kind));

            var countryOnly = await ctx.AffinityGroups
                .Include(g => g.Members)
                .SingleAsync(g => g.Id == countryOnlyId);
            Assert.Equal(AffinityKind.Country, countryOnly.Kind);
            Assert.Equal("es", countryOnly.CountryCode);
            Assert.Single(countryOnly.Members);

            var orphan = await ctx.AffinityGroups
                .Include(g => g.Members)
                .SingleAsync(g => g.Id == orphanId);
            Assert.Equal(AffinityKind.Country, orphan.Kind);
            Assert.Null(orphan.CountryCode);
            Assert.Single(orphan.Members);
        }
    }

    [Fact]
    public async Task Migration_is_idempotent_when_no_legacy_mixed_groups_exist()
    {
        var dbPath = await InitializeCatalogAsync(NewDbPath());
        // Já migrada. Migrar de novo é no-op.
        var options = OptionsFor(dbPath);
        await using var ctx = new ChannelCatalogDbContext(options);
        await ctx.Database.MigrateAsync();
        Assert.Empty(await ctx.AffinityGroups.ToListAsync());
    }
}
