using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using m3uCrawler.Services.Security;
using Xunit;

namespace m3uCrawler.Tests;

[Collection("SecretProtection")]
public sealed class AppSecretStoreTests : IAsyncLifetime
{
    private readonly string _dir;
    private readonly string _dbPath;
    private RuntimeChannelCatalogDbContextFactory _factory = null!;
    private AppSecretStore _store = null!;

    public AppSecretStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"channel-catalog-tests-appsecret-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "channel-catalog.db");
    }

    public async Task InitializeAsync()
    {
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        await using (var ctx = await bootstrapper.InitializeAsync())
        {
        }

        _factory = new RuntimeChannelCatalogDbContextFactory(_dbPath);
        var protector = new SecretProtector(_dir);
        _store = new AppSecretStore(_factory, protector);
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Set_get_exists_delete_round_trip()
    {
        Assert.False(await _store.ExistsAsync("playlist.token"));
        Assert.Null(await _store.GetAsync("playlist.token"));

        await _store.SetAsync("playlist.token", "s3cr3t-value");

        Assert.True(await _store.ExistsAsync("playlist.token"));
        Assert.Equal("s3cr3t-value", await _store.GetAsync("playlist.token"));

        await _store.DeleteAsync("playlist.token");

        Assert.False(await _store.ExistsAsync("playlist.token"));
        Assert.Null(await _store.GetAsync("playlist.token"));
    }

    [Fact]
    public async Task Stored_value_is_encrypted_and_not_plaintext()
    {
        await _store.SetAsync("playlist.token", "cleartext-token");

        await using var ctx = _factory.CreateDbContext();
        var row = await ctx.AppSecrets.SingleAsync(s => s.Key == "playlist.token");

        Assert.NotEqual("cleartext-token", row.Value);
        Assert.DoesNotContain("cleartext-token", row.Value);
        Assert.True(DateTime.TryParse(
            row.UpdatedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _));
    }

    [Fact]
    public async Task Set_is_an_upsert()
    {
        await _store.SetAsync("k", "first");
        await _store.SetAsync("k", "second");

        Assert.Equal("second", await _store.GetAsync("k"));

        await using var ctx = _factory.CreateDbContext();
        Assert.Equal(1, await ctx.AppSecrets.CountAsync(s => s.Key == "k"));
    }

    [Fact]
    public async Task Delete_is_idempotent()
    {
        await _store.SetAsync("k", "v");

        await _store.DeleteAsync("k");
        await _store.DeleteAsync("k");

        Assert.False(await _store.ExistsAsync("k"));
    }

    [Fact]
    public async Task Empty_value_round_trips()
    {
        await _store.SetAsync("empty", string.Empty);

        Assert.True(await _store.ExistsAsync("empty"));
        Assert.Equal(string.Empty, await _store.GetAsync("empty"));
    }
}
