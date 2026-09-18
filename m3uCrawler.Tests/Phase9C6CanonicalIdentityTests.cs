using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Services.Catalog;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9C.6 — identidade canónica autoritativa.
///
/// <para>
/// <see cref="CatalogResolver.ResolveAsync"/> resolve afinidades de
/// canal pela <see cref="AffinityGroupEntity.CanonicalChannelKey"/>
/// estável. Quando a Key existe mas não resolve (inexistente ou
/// desactivada), NÃO cai para o <c>CanonicalChannelId</c> obsoleto.
/// Quando a Key está ausente (null/vazia), mantém-se o fallback
/// legado via FK/nav <c>CanonicalChannel</c>.
/// </para>
///
/// <para>
/// <see cref="CatalogResolver.ListChannelSourcesAsync"/> publica a
/// navegação <c>CanonicalChannel</c> (Include), permitindo que os
/// consumidores leiam a Key sem queries adicionais.
/// </para>
/// </summary>
public class Phase9C6CanonicalIdentityTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private IDbContextFactory<ChannelCatalogDbContext> _factory = null!;
    private CatalogResolver _resolver = null!;

    public Phase9C6CanonicalIdentityTests()
    {
        _dbPath = TestTempDb.SuitePath($"phase9c6-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        _resolver = new CatalogResolver(_factory, _dbPath);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<CanonicalChannelEntity> CreateCanonicalAsync(string key)
    {
        return await _resolver.CreateCanonicalChannelAsync(
            key,
            $"Phase 9C6 {key}",
            EditorialCategory.Live,
            CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy.CreateEligible,
            isEnabled: true,
            normalizedAliases: Array.Empty<string>());
    }

    private async Task UpdateAffinityGroupRowAsync(long groupId, Action<AffinityGroupEntity> mutate)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var group = await ctx.AffinityGroups.FirstAsync(g => g.Id == groupId);
        mutate(group);
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Affinity_survives_canonical_delete_and_recreate_with_same_key()
    {
        const string key = "phase9c6-rtp1";
        const string member = "phase9c6 rtp um variante";

        var canonical = await CreateCanonicalAsync(key);
        var oldId = canonical.Id;

        await _resolver.CreateAffinityGroupAsync(
            "Phase 9C6 RTP 1", AffinityKind.Channel, key, null, new[] { member });

        var before = await _resolver.ResolveAsync(member);
        Assert.Equal(CatalogResolutionKind.Canonical, before.Kind);
        Assert.Equal(key, before.CanonicalKey);
        Assert.Equal(oldId, before.CanonicalChannelId);

        Assert.True(await _resolver.DeleteCanonicalChannelAsync(oldId));

        var recreated = await CreateCanonicalAsync(key);
        var newId = recreated.Id;

        Assert.NotEqual(oldId, newId);

        var after = await _resolver.ResolveAsync(member);
        Assert.Equal(CatalogResolutionKind.Canonical, after.Kind);
        Assert.Equal(key, after.CanonicalKey);
        Assert.Equal(newId, after.CanonicalChannelId);
    }

    [Fact]
    public async Task Affinity_resolves_when_canonical_channel_id_is_null_but_key_valid()
    {
        const string key = "phase9c6-null-id";
        const string member = "phase9c6 null id variante";

        var canonical = await CreateCanonicalAsync(key);
        var group = await _resolver.CreateAffinityGroupAsync(
            "Phase 9C6 Null Id", AffinityKind.Channel, key, null, new[] { member });

        await UpdateAffinityGroupRowAsync(group.Id, g => g.CanonicalChannelId = null);

        var resolution = await _resolver.ResolveAsync(member);
        Assert.Equal(CatalogResolutionKind.Canonical, resolution.Kind);
        Assert.Equal(key, resolution.CanonicalKey);
        Assert.Equal(canonical.Id, resolution.CanonicalChannelId);
    }

    [Fact]
    public async Task Affinity_key_is_authoritative_over_stale_canonical_channel_id()
    {
        const string keyA = "phase9c6-key-a";
        const string keyB = "phase9c6-key-b";
        const string member = "phase9c6 stale fk variante";

        var a = await CreateCanonicalAsync(keyA);
        var b = await CreateCanonicalAsync(keyB);

        var group = await _resolver.CreateAffinityGroupAsync(
            "Phase 9C6 Stale FK", AffinityKind.Channel, keyA, null, new[] { member });

        await UpdateAffinityGroupRowAsync(group.Id, g => g.CanonicalChannelId = b.Id);

        var resolution = await _resolver.ResolveAsync(member);
        Assert.Equal(CatalogResolutionKind.Canonical, resolution.Kind);
        Assert.Equal(keyA, resolution.CanonicalKey);
        Assert.Equal(a.Id, resolution.CanonicalChannelId);
        Assert.NotEqual(b.Id, resolution.CanonicalChannelId);
    }

    [Fact]
    public async Task Affinity_with_unknown_key_does_not_fall_back_to_stale_id()
    {
        const string keyA = "phase9c6-unknown-key-a";
        const string keyB = "phase9c6-unknown-key-b";
        const string member = "phase9c6 unknown key variante";

        await CreateCanonicalAsync(keyA);
        var b = await CreateCanonicalAsync(keyB);

        var group = await _resolver.CreateAffinityGroupAsync(
            "Phase 9C6 Unknown Key", AffinityKind.Channel, keyA, null, new[] { member });

        await UpdateAffinityGroupRowAsync(group.Id, g =>
        {
            g.CanonicalChannelKey = "does-not-exist";
            g.CanonicalChannelId = b.Id;
        });

        var resolution = await _resolver.ResolveAsync(member);
        Assert.Equal(CatalogResolutionKind.Unknown, resolution.Kind);
        Assert.Null(resolution.CanonicalKey);
        Assert.Null(resolution.CanonicalChannelId);
        Assert.NotEqual(b.Id, resolution.CanonicalChannelId);
    }

    [Fact]
    public async Task Legacy_affinity_without_key_still_resolves_via_fk()
    {
        const string key = "phase9c6-legacy-key";
        const string member = "phase9c6 legacy variante";

        var canonical = await CreateCanonicalAsync(key);
        var group = await _resolver.CreateAffinityGroupAsync(
            "Phase 9C6 Legacy", AffinityKind.Channel, key, null, new[] { member });

        await UpdateAffinityGroupRowAsync(group.Id, g => g.CanonicalChannelKey = null);

        var resolution = await _resolver.ResolveAsync(member);
        Assert.Equal(CatalogResolutionKind.Canonical, resolution.Kind);
        Assert.Equal(key, resolution.CanonicalKey);
        Assert.Equal(canonical.Id, resolution.CanonicalChannelId);
    }

    [Fact]
    public async Task ListChannelSources_publishes_canonical_key()
    {
        const string key = "phase9c6-list-key";

        var canonical = await CreateCanonicalAsync(key);
        var source = await _resolver.EnsureSourceAsync(
            "phase9c6-source",
            "Phase 9C6 Source",
            SourceKind.M3U,
            "http://example.invalid/playlist.m3u",
            priority: 0);

        await _resolver.RecordChannelSourceAsync(
            canonical.Id, source.Id, "http://example.invalid/stream.m3u8");

        var sources = await _resolver.ListChannelSourcesAsync();
        var entry = Assert.Single(sources);
        Assert.NotNull(entry.CanonicalChannel);
        Assert.Equal(canonical.Id, entry.CanonicalChannel!.Id);
        Assert.Equal(key, entry.CanonicalChannel.Key);
    }
}
