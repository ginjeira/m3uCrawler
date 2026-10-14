using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using m3uCrawler.Services.Catalog;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Eliminação de um canal canónico com registos de ownership do Dispatcharr.
/// Sem <c>force</c> mantém-se a guarda de aplicação (409 /
/// <see cref="ChannelAdministrationError.HasOwnership"/>); com
/// <c>force: true</c> o canal é eliminado e o FK
/// <c>OnDelete(SetNull)</c> deixa os registos de ownership com
/// <c>CanonicalChannelId = NULL</c> — os registos <b>não</b> são apagados e
/// o canal no Dispatcharr <b>não</b> é tocado. Um canal sem ownership
/// continua a ser eliminado com e sem <c>force</c>.
/// </summary>
public sealed class CanonicalChannelForceDeleteTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;

    public CanonicalChannelForceDeleteTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"canonical-force-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "channel-catalog.db");
    }

    public void Dispose()
    {
        TestTempDb.Cleanup(_dbPath);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private async Task<CatalogResolver> CreateResolverAsync()
    {
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        await using (await bootstrapper.InitializeAsync())
        {
            // sem seed: nenhum canal/grupo é criado automaticamente
        }
        return new CatalogResolver(new TestDbContextFactory(_dbPath), _dbPath);
    }

    private static Task<CanonicalChannelEntity> CreateChannelAsync(CatalogResolver resolver, string key)
        => resolver.CreateCanonicalChannelAsync(
            key, $"Canal {key}", EditorialCategory.Live,
            CanonicalGroupKeys.PortugalGeneralistas, PublicationPolicy.CreateEligible,
            isEnabled: true, normalizedAliases: new List<string>());

    private async Task<(long OwnershipId, long DispatcharrChannelId)> AddOwnershipAsync(
        long canonicalChannelId, long dispatcharrChannelId)
    {
        await using var ctx = new TestDbContextFactory(_dbPath).CreateDbContext();
        var now = DateTime.UtcNow;
        var ownership = new DispatcharrChannelOwnershipEntity
        {
            DispatcharrChannelId = dispatcharrChannelId,
            Ownership = ChannelOwnership.CrawlerManaged,
            CanonicalChannelId = canonicalChannelId,
            FirstObservedAtUtc = now,
            LastObservedAtUtc = now,
            Evidence = "test",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        ctx.DispatcharrChannelOwnerships.Add(ownership);
        await ctx.SaveChangesAsync();
        return (ownership.Id, dispatcharrChannelId);
    }

    [Fact]
    public async Task Delete_without_force_with_ownership_throws_HasOwnership()
    {
        var resolver = await CreateResolverAsync();
        var channel = await CreateChannelAsync(resolver, "force-no-flag");
        await AddOwnershipAsync(channel.Id, 2001);

        var ex = await Assert.ThrowsAsync<ChannelAdministrationException>(
            () => resolver.DeleteCanonicalChannelAsync(channel.Id));
        Assert.Equal(ChannelAdministrationError.HasOwnership, ex.Error);
        // O canal continua lá: a eliminação não foi parcial.
        Assert.NotNull(await resolver.GetCanonicalChannelAsync(channel.Id));
    }

    [Fact]
    public async Task Delete_with_force_removes_channel_and_nulls_ownership_fk()
    {
        var resolver = await CreateResolverAsync();
        var channel = await CreateChannelAsync(resolver, "force-flag");
        await AddOwnershipAsync(channel.Id, 2002);
        await AddOwnershipAsync(channel.Id, 2003);

        Assert.True(await resolver.DeleteCanonicalChannelAsync(channel.Id, force: true));

        Assert.Null(await resolver.GetCanonicalChannelAsync(channel.Id));

        await using var ctx = new TestDbContextFactory(_dbPath).CreateDbContext();
        // Os registos de ownership mantêm-se (contagem inalterada) mas
        // ficam desassociados do canal eliminado (FK → NULL).
        var ownerships = await ctx.DispatcharrChannelOwnerships.ToListAsync();
        Assert.Equal(2, ownerships.Count);
        Assert.All(ownerships, o => Assert.Null(o.CanonicalChannelId));
    }

    [Fact]
    public async Task Delete_without_ownership_succeeds_with_and_without_force()
    {
        var resolver = await CreateResolverAsync();
        var plain = await CreateChannelAsync(resolver, "no-ownership-plain");
        var forced = await CreateChannelAsync(resolver, "no-ownership-forced");

        Assert.True(await resolver.DeleteCanonicalChannelAsync(plain.Id));
        Assert.Null(await resolver.GetCanonicalChannelAsync(plain.Id));

        Assert.True(await resolver.DeleteCanonicalChannelAsync(forced.Id, force: true));
        Assert.Null(await resolver.GetCanonicalChannelAsync(forced.Id));
    }
}
