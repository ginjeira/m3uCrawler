using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W4 — Canonical Channels: o formulário de edição do Dashboard expõe todos
/// os campos suportados por <c>PUT /api/catalog/channels/{id}</c>
/// (DisplayName, Country, EditorialCategory, GroupKey,
/// PublicationPolicy, IsEnabled). Este teste prova o contrato HTTP real
/// no harness standalone: actualização, validação de enum, duplicado e delete.
/// </summary>
[Collection("DashboardStaticState")]
public sealed class WaveW4ChannelAdminHttpTests : IAsyncLifetime
{
    private string _root = null!;
    private string _dbPath = null!;
    private string _outputDir = null!;
    private DashboardBootstrapEndpointTests.DashboardHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"w4-channels-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _outputDir = Path.Combine(_root, "output");
        Directory.CreateDirectory(_outputDir);

        var factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        await using (var ctx = await bootstrapper.InitializeAsync())
        {
            // Wave A/H1 — a migration já não semeia grupos. Provisiona os
            // grupos por omissão explicitamente (como um utilizador faria).
            await ChannelCatalogBootstrapper.EnsureCanonicalGroupsAsync(ctx, DateTime.UtcNow);
        }

        var resolver = new CatalogResolver(factory, _dbPath);
        var composer = new PlaylistComposerService(factory);
        var history = new ImportHistoryService(_outputDir);

        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, resolver, composer, history,
            lifecycle: null, auth: null, bootstrap: null, webToken: null,
            standalone: true);
    }

    public async Task DisposeAsync()
    {
        await _harness.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private HttpClient Client => _harness.Client;

    private static CancellationToken ShortToken()
        => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<long> CreateChannelAsync(HttpClient client, string key)
    {
        var body =
            $"{{\"key\":\"{key}\",\"displayName\":\"W4 Canal\",\"country\":\"pt\"," +
            "\"editorialCategory\":\"Live\",\"groupKey\":\"pt-generalistas\"," +
            "\"publicationPolicy\":\"CreateEligible\",\"isEnabled\":true,\"aliases\":[]}";
        var created = await client.PostAsync("/api/catalog/channels", Json(body), ShortToken());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetInt64();
    }

    private async Task AddChannelOwnershipAsync(long canonicalChannelId, long dispatcharrChannelId)
    {
        await using var ctx = new TestDbContextFactory(_dbPath).CreateDbContext();
        var now = DateTime.UtcNow;
        ctx.DispatcharrChannelOwnerships.Add(new DispatcharrChannelOwnershipEntity
        {
            DispatcharrChannelId = dispatcharrChannelId,
            Ownership = ChannelOwnership.CrawlerManaged,
            CanonicalChannelId = canonicalChannelId,
            FirstObservedAtUtc = now,
            LastObservedAtUtc = now,
            Evidence = "test",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Channel_put_updates_all_supported_fields()
    {
        var key = $"w4-chan-{Guid.NewGuid():N}";
        var id = await CreateChannelAsync(Client, key);

        var update = await Client.PutAsync(
            $"/api/catalog/channels/{id}",
            Json("{\"displayName\":\"W4 Editado\",\"country\":\"es\"," +
                 "\"editorialCategory\":\"Desporto\",\"groupKey\":\"pt-desporto\"," +
                 "\"publicationPolicy\":\"ReviewOnly\",\"isEnabled\":false}"),
            ShortToken());
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var list = await Client.GetAsync("/api/catalog/channels", ShortToken());
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var entry = listDoc.RootElement.EnumerateArray()
            .Single(c => c.GetProperty("id").GetInt64() == id);
        Assert.Equal("W4 Editado", entry.GetProperty("displayName").GetString());
        Assert.Equal("es", entry.GetProperty("country").GetString());
        Assert.Equal("Desporto", entry.GetProperty("editorialCategory").GetString());
        Assert.Equal("pt-desporto", entry.GetProperty("groupKey").GetString());
        Assert.Equal("ReviewOnly", entry.GetProperty("publicationPolicy").GetString());
        Assert.False(entry.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(key, entry.GetProperty("key").GetString());
    }

    [Fact]
    public async Task Channel_put_invalid_enum_returns_400()
    {
        var key = $"w4-chan-bad-{Guid.NewGuid():N}";
        var id = await CreateChannelAsync(Client, key);

        var update = await Client.PutAsync(
            $"/api/catalog/channels/{id}",
            Json("{\"displayName\":\"W4\",\"country\":null," +
                 "\"editorialCategory\":\"NaoExiste\",\"groupKey\":\"pt-generalistas\"," +
                 "\"publicationPolicy\":\"CreateEligible\",\"isEnabled\":true}"),
            ShortToken());

        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
    }

    [Fact]
    public async Task Channel_duplicate_key_returns_409_and_delete_returns_200()
    {
        var key = $"w4-chan-dup-{Guid.NewGuid():N}";
        var id = await CreateChannelAsync(Client, key);

        var duplicateBody = JsonSerializer.Serialize(new
        {
            key,
            displayName = "Outro",
            country = (string?)null,
            editorialCategory = "Live",
            groupKey = "pt-generalistas",
            publicationPolicy = "CreateEligible",
            isEnabled = true,
            aliases = Array.Empty<string>(),
        });
        var duplicate = await Client.PostAsync(
            "/api/catalog/channels",
            Json(duplicateBody),
            ShortToken());
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var delete = await Client.DeleteAsync($"/api/catalog/channels/{id}", ShortToken());
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);

        var list = await Client.GetAsync("/api/catalog/channels", ShortToken());
        using var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.DoesNotContain(
            listDoc.RootElement.EnumerateArray(),
            c => c.GetProperty("id").GetInt64() == id);
    }

    [Fact]
    public async Task Channel_delete_with_ownership_returns_409_kind_and_force_deletes()
    {
        var key = $"w4-chan-own-{Guid.NewGuid():N}";
        var id = await CreateChannelAsync(Client, key);
        await AddChannelOwnershipAsync(id, 7001);

        // Sem `force` mantém-se o 409 com o discriminador HasOwnership.
        var blocked = await Client.DeleteAsync($"/api/catalog/channels/{id}", ShortToken());
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        using (var doc = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync()))
        {
            Assert.Equal("HasOwnership", doc.RootElement.GetProperty("kind").GetString());
        }

        // O canal ainda existe após o 409.
        var list = await Client.GetAsync("/api/catalog/channels", ShortToken());
        using (var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            Assert.Contains(
                listDoc.RootElement.EnumerateArray(),
                c => c.GetProperty("id").GetInt64() == id);
        }

        // Com `?force=true` o canal desaparece (200/deleted:true).
        var forced = await Client.DeleteAsync($"/api/catalog/channels/{id}?force=true", ShortToken());
        Assert.Equal(HttpStatusCode.OK, forced.StatusCode);
        using (var forcedDoc = JsonDocument.Parse(await forced.Content.ReadAsStringAsync()))
        {
            Assert.True(forcedDoc.RootElement.GetProperty("deleted").GetBoolean());
        }

        var after = await Client.GetAsync("/api/catalog/channels", ShortToken());
        using (var afterDoc = JsonDocument.Parse(await after.Content.ReadAsStringAsync()))
        {
            Assert.DoesNotContain(
                afterDoc.RootElement.EnumerateArray(),
                c => c.GetProperty("id").GetInt64() == id);
        }

        // O registo de ownership mantém-se, com CanonicalChannelId NULL.
        await using var ctx = new TestDbContextFactory(_dbPath).CreateDbContext();
        var ownership = await ctx.DispatcharrChannelOwnerships
            .SingleAsync(o => o.DispatcharrChannelId == 7001);
        Assert.Null(ownership.CanonicalChannelId);
    }
}
