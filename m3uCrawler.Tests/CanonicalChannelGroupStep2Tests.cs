using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Matching;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Wave A, step 2 — <c>GroupId</c> como leitura/escrita autoritativa do
/// grupo do canal. Wave D2 remove o enum legado: o grupo é identificado
/// apenas pela <c>Key</c> do <see cref="CanonicalGroupEntity"/> e pela FK
/// <c>GroupId</c>.
/// <list type="bullet">
///   <item>Create/Update/Ensure/ReviewApproval resolvem <c>GroupId</c> a
///         partir da <c>groupKey</c>;</item>
///   <item><see cref="CatalogResolution"/> transporta o grupo
///         (<c>GroupId</c>/<c>GroupKey</c>/<c>GroupName</c>);</item>
///   <item><see cref="PlaylistComposerService"/> prefere o
///         <c>DisplayName</c> do grupo configurável, com fallback para
///         "Other" quando o <c>Group</c> é <c>null</c>.</item>
/// </list>
/// SQLite isolado por teste; sem rede.
/// </summary>
public class CanonicalChannelGroupStep2Tests
{
    private static string NewDbPath() =>
        TestTempDb.SuitePath($"channel-catalog-tests-wavea-{Guid.NewGuid():N}.db");

    private static async Task<(TestDbContextFactory Factory, CatalogResolver Resolver, string DbPath)>
        InitializeAsync()
    {
        var dbPath = NewDbPath();
        var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
        await using (var ctx = await bootstrapper.InitializeAsync())
        {
        }

        var factory = new TestDbContextFactory(dbPath);
        return (factory, new CatalogResolver(factory, dbPath), dbPath);
    }

    private static async Task<long> GroupIdAsync(
        IDbContextFactory<ChannelCatalogDbContext> factory, string key)
    {
        await using var ctx = factory.CreateDbContext();
        return await ctx.CanonicalGroups.Where(g => g.Key == key).Select(g => g.Id).SingleAsync();
    }

    // ================= Create / Update / Ensure =================

    [Fact]
    public async Task CreateCanonicalChannelAsync_sets_GroupId_from_groupKey()
    {
        var (factory, resolver, dbPath) = await InitializeAsync();
        try
        {
            var channel = await resolver.CreateCanonicalChannelAsync(
                "wavea-created", "Criado",
                EditorialCategory.Desporto, CanonicalGroupKeys.PortugalDesporto,
                PublicationPolicy.CreateEligible, isEnabled: true,
                normalizedAliases: new List<string>());

            var expected = await GroupIdAsync(factory, "pt-desporto");
            Assert.Equal(expected, channel.GroupId);

            await using var ctx = factory.CreateDbContext();
            var persisted = await ctx.CanonicalChannels.SingleAsync(c => c.Id == channel.Id);
            Assert.Equal(expected, persisted.GroupId);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task EnsureCanonicalChannelAsync_sets_GroupId_from_groupKey()
    {
        var (factory, resolver, dbPath) = await InitializeAsync();
        try
        {
            var (channel, created) = await resolver.EnsureCanonicalChannelAsync(
                "wavea-ensured", "Garantido",
                EditorialCategory.Infantil, CanonicalGroupKeys.PortugalInfantil,
                PublicationPolicy.CreateEligible, isEnabled: true, normalizedAlias: null);

            Assert.True(created);
            Assert.Equal(await GroupIdAsync(factory, "pt-infantil"), channel.GroupId);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task UpdateCanonicalChannelAsync_resyncs_GroupId_from_groupKey()
    {
        var (factory, resolver, dbPath) = await InitializeAsync();
        try
        {
            var channel = await resolver.CreateCanonicalChannelAsync(
                "wavea-updated", "Antes",
                EditorialCategory.Live, CanonicalGroupKeys.International,
                PublicationPolicy.CreateEligible, isEnabled: true,
                normalizedAliases: new List<string>());
            Assert.Equal(await GroupIdAsync(factory, "international"), channel.GroupId);

            var updated = await resolver.UpdateCanonicalChannelAsync(
                channel.Id, "Depois",
                EditorialCategory.Live, CanonicalGroupKeys.PortugalFilmesSeries,
                PublicationPolicy.CreateEligible, isEnabled: true);

            Assert.NotNull(updated);
            Assert.Equal(await GroupIdAsync(factory, "pt-filmes-series"), updated!.GroupId);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    // ================= groupKey (Key estável) =================

    [Fact]
    public async Task CreateCanonicalChannelAsync_groupKey_sets_matching_group()
    {
        var (factory, resolver, dbPath) = await InitializeAsync();
        try
        {
            var channel = await resolver.CreateCanonicalChannelAsync(
                "wavec-create-groupkey", "Criado por Key",
                EditorialCategory.Live, CanonicalGroupKeys.PortugalDesporto,
                PublicationPolicy.CreateEligible, isEnabled: true,
                normalizedAliases: new List<string>());

            Assert.Equal(await GroupIdAsync(factory, "pt-desporto"), channel.GroupId);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task CreateCanonicalChannelAsync_custom_groupKey_uses_custom_group()
    {
        var (factory, resolver, dbPath) = await InitializeAsync();
        try
        {
            await resolver.UpsertCanonicalGroupAsync(
                "wavec-custom", "Wave C Custom", "pt", order: 99,
                isEnabled: true, isDefault: false);

            var channel = await resolver.CreateCanonicalChannelAsync(
                "wavec-create-custom", "Criado custom",
                EditorialCategory.Live, "wavec-custom",
                PublicationPolicy.CreateEligible, isEnabled: true,
                normalizedAliases: new List<string>());

            Assert.Equal(await GroupIdAsync(factory, "wavec-custom"), channel.GroupId);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task CreateCanonicalChannelAsync_unknown_groupKey_falls_back_to_Other()
    {
        var (factory, resolver, dbPath) = await InitializeAsync();
        try
        {
            var channel = await resolver.CreateCanonicalChannelAsync(
                "wavec-create-unknown", "Criado unknown",
                EditorialCategory.Live, "nao-existe",
                PublicationPolicy.CreateEligible, isEnabled: true,
                normalizedAliases: new List<string>());

            Assert.Equal(await GroupIdAsync(factory, CanonicalGroupKeys.Other), channel.GroupId);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task UpdateCanonicalChannelAsync_groupKey_resyncs_group()
    {
        var (factory, resolver, dbPath) = await InitializeAsync();
        try
        {
            var channel = await resolver.CreateCanonicalChannelAsync(
                "wavec-update-groupkey", "Antes",
                EditorialCategory.Live, CanonicalGroupKeys.International,
                PublicationPolicy.CreateEligible, isEnabled: true,
                normalizedAliases: new List<string>());
            Assert.Equal(await GroupIdAsync(factory, "international"), channel.GroupId);

            var updated = await resolver.UpdateCanonicalChannelAsync(
                channel.Id, "Depois",
                EditorialCategory.Live, CanonicalGroupKeys.PortugalDesporto,
                PublicationPolicy.CreateEligible, isEnabled: true);

            Assert.NotNull(updated);
            Assert.Equal(await GroupIdAsync(factory, "pt-desporto"), updated!.GroupId);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task UpdateCanonicalChannelAsync_custom_groupKey_uses_custom_group()
    {
        var (factory, resolver, dbPath) = await InitializeAsync();
        try
        {
            await resolver.UpsertCanonicalGroupAsync(
                "wavec-custom-update", "Wave C Custom Update", "pt", order: 98,
                isEnabled: true, isDefault: false);

            var channel = await resolver.CreateCanonicalChannelAsync(
                "wavec-update-custom", "Antes",
                EditorialCategory.Live, CanonicalGroupKeys.PortugalFilmesSeries,
                PublicationPolicy.CreateEligible, isEnabled: true,
                normalizedAliases: new List<string>());

            var updated = await resolver.UpdateCanonicalChannelAsync(
                channel.Id, "Depois",
                EditorialCategory.Live, "wavec-custom-update",
                PublicationPolicy.CreateEligible, isEnabled: true);

            Assert.NotNull(updated);
            Assert.Equal(await GroupIdAsync(factory, "wavec-custom-update"), updated!.GroupId);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task CreateCanonicalChannelAsync_null_groupKey_defaults_to_Other()
    {
        var (factory, resolver, dbPath) = await InitializeAsync();
        try
        {
            var channel = await resolver.CreateCanonicalChannelAsync(
                "wavec-null-groupkey", "Legacy",
                EditorialCategory.Desporto, null,
                PublicationPolicy.CreateEligible, isEnabled: true,
                normalizedAliases: new List<string>());

            Assert.Equal(await GroupIdAsync(factory, CanonicalGroupKeys.Other), channel.GroupId);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    // ================= CatalogResolution transport =================

    [Fact]
    public async Task ResolveAsync_transports_group_id_key_and_name()
    {
        var (factory, resolver, dbPath) = await InitializeAsync();
        try
        {
            var channel = await resolver.CreateCanonicalChannelAsync(
                "wavea-resolve-alvo", "Canal Wave A Resolve",
                EditorialCategory.Desporto, CanonicalGroupKeys.PortugalDesporto,
                PublicationPolicy.CreateEligible, isEnabled: true,
                normalizedAliases: new List<string>());

            var expectedGroupId = await GroupIdAsync(factory, "pt-desporto");

            var resolution = await resolver.ResolveAsync(ChannelNormalizer.Normalize(channel.Key));

            Assert.Equal(CatalogResolutionKind.Canonical, resolution.Kind);
            Assert.Equal(channel.Id, resolution.CanonicalChannelId);
            Assert.Equal(expectedGroupId, resolution.GroupId);
            Assert.Equal("pt-desporto", resolution.GroupKey);
            Assert.Equal(
                CanonicalGroupDefaults.All.Single(g => g.Key == CanonicalGroupKeys.PortugalDesporto).DisplayName,
                resolution.GroupName);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    // ================= PlaylistComposerService =================

    [Fact]
    public async Task Composer_prefers_group_display_name()
    {
        var (factory, resolver, dbPath) = await InitializeAsync();
        try
        {
            var channel = await resolver.CreateCanonicalChannelAsync(
                "wavea-compose", "Canal Compose",
                EditorialCategory.Desporto, CanonicalGroupKeys.PortugalDesporto,
                PublicationPolicy.CreateEligible, isEnabled: true,
                normalizedAliases: new List<string>());

            // Renomear o grupo configurável: a playlist deve reflectir o
            // novo DisplayName.
            await using (var ctx = factory.CreateDbContext())
            {
                await ctx.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE canonical_groups SET DisplayName = {"Desporto Custom"} WHERE Key = {"pt-desporto"}");
            }

            var source = await resolver.EnsureSourceAsync(
                "wavea-src", "wavea-src", SourceKind.Telegram, "telegram://wavea-src", 0);
            await resolver.RecordChannelSourceAsync(
                channel.Id, source.Id, "http://wavea.example/1.ts",
                availability: AvailabilityState.Discovered, matchMethod: "test");

            var list = await resolver.CreateOrderingListAsync("wavea-list", "Wave A", "pt", null);
            await resolver.AddOrderingItemAsync(list.Id, channel.Id, position: 0);

            var composition = await new PlaylistComposerService(factory).ComposeAsync(list.Id);

            var entry = Assert.Single(composition.Entries);
            Assert.Equal("Desporto Custom", entry.Group);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task Composer_falls_back_to_Other_when_group_is_null()
    {
        var (factory, resolver, dbPath) = await InitializeAsync();
        try
        {
            var channel = await resolver.CreateCanonicalChannelAsync(
                "wavea-compose-null", "Canal Compose Null",
                EditorialCategory.Desporto, CanonicalGroupKeys.PortugalDesporto,
                PublicationPolicy.CreateEligible, isEnabled: true,
                normalizedAliases: new List<string>());

            // Simular um canal histórico ainda não migrado (GroupId null).
            await using (var ctx = factory.CreateDbContext())
            {
                var tracked = await ctx.CanonicalChannels.SingleAsync(c => c.Id == channel.Id);
                tracked.GroupId = null;
                await ctx.SaveChangesAsync();
            }

            var source = await resolver.EnsureSourceAsync(
                "wavea-src-null", "wavea-src-null", SourceKind.Telegram, "telegram://wavea-src-null", 0);
            await resolver.RecordChannelSourceAsync(
                channel.Id, source.Id, "http://wavea.example/null.ts",
                availability: AvailabilityState.Discovered, matchMethod: "test");

            var list = await resolver.CreateOrderingListAsync("wavea-list-null", "Wave A Null", "pt", null);
            await resolver.AddOrderingItemAsync(list.Id, channel.Id, position: 0);

            var composition = await new PlaylistComposerService(factory).ComposeAsync(list.Id);

            var entry = Assert.Single(composition.Entries);
            Assert.Equal("Other", entry.Group);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    // ================= Wave C2 — guarda de eliminação de grupo =================

    [Fact]
    public async Task DeleteCanonicalGroupAsync_referenced_by_channel_throws_and_does_not_delete()
    {
        var (factory, resolver, dbPath) = await InitializeAsync();
        try
        {
            var group = await resolver.UpsertCanonicalGroupAsync(
                "wavec-delete-in-use", "Wave C Delete In Use", "pt", order: 97,
                isEnabled: true, isDefault: false);

            await resolver.CreateCanonicalChannelAsync(
                "wavec-delete-channel", "Canal em uso",
                EditorialCategory.Live, "wavec-delete-in-use",
                PublicationPolicy.CreateEligible, isEnabled: true,
                normalizedAliases: new List<string>());

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => resolver.DeleteCanonicalGroupAsync(group.Id));
            Assert.Contains("em uso", ex.Message);
            Assert.Contains("canal", ex.Message);

            // O grupo continua presente (não foi eliminado).
            await using var ctx = factory.CreateDbContext();
            Assert.True(await ctx.CanonicalGroups.AnyAsync(g => g.Id == group.Id));
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

}
