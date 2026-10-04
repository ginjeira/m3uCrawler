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
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W4 — Ordering Lists: o separador do Dashboard passa a editar os metadados
/// de uma lista existente (<c>PUT /api/catalog/ordering-lists/{id}</c>).
/// A <c>Key</c> é imutável; <c>Name</c>, <c>Country</c>, <c>Description</c>
/// e <c>IsEnabled</c> são actualizáveis. Este teste cobre a rota HTTP real
/// (criação → edição → persistência → validações) no harness standalone.
/// </summary>
[Collection("DashboardStaticState")]
public sealed class WaveW4OrderingHttpTests : IAsyncLifetime
{
    private string _root = null!;
    private string _dbPath = null!;
    private string _outputDir = null!;
    private DashboardBootstrapEndpointTests.DashboardHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"w4-ordering-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _outputDir = Path.Combine(_root, "output");
        Directory.CreateDirectory(_outputDir);

        var factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();

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

    [Fact]
    public async Task Ordering_list_create_put_update_and_get_detail()
    {
        var key = $"w4-ord-{Guid.NewGuid():N}";
        var create = await Client.PostAsync(
            "/api/catalog/ordering-lists",
            Json($"{{\"key\":\"{key}\",\"name\":\"W4 Inicial\",\"country\":\"pt\",\"description\":\"antes\",\"isEnabled\":true}}"),
            ShortToken());
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        long id;
        using (var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync()))
        {
            id = doc.RootElement.GetProperty("id").GetInt64();
            Assert.True(id > 0);
        }

        // PUT actualiza os metadados (Key imutável).
        var update = await Client.PutAsync(
            $"/api/catalog/ordering-lists/{id}",
            Json("{\"name\":\"W4 Editado\",\"country\":\"es\",\"description\":\"depois\",\"isEnabled\":false}"),
            ShortToken());
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using (var doc = JsonDocument.Parse(await update.Content.ReadAsStringAsync()))
        {
            Assert.True(doc.RootElement.GetProperty("updated").GetBoolean());
            Assert.Equal(id, doc.RootElement.GetProperty("id").GetInt64());
        }

        // Persistência reflectida na lista.
        var list = await Client.GetAsync("/api/catalog/ordering-lists", ShortToken());
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            var entry = listDoc.RootElement.EnumerateArray()
                .Single(l => l.GetProperty("id").GetInt64() == id);
            Assert.Equal("W4 Editado", entry.GetProperty("name").GetString());
            Assert.Equal("es", entry.GetProperty("country").GetString());
            Assert.Equal("depois", entry.GetProperty("description").GetString());
            Assert.False(entry.GetProperty("isEnabled").GetBoolean());
            Assert.Equal(key, entry.GetProperty("key").GetString());
        }

        // Detalhe continua a responder 200.
        var detail = await Client.GetAsync($"/api/catalog/ordering-lists/{id}", ShortToken());
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using (var detailDoc = JsonDocument.Parse(await detail.Content.ReadAsStringAsync()))
        {
            Assert.Equal("W4 Editado", detailDoc.RootElement.GetProperty("name").GetString());
        }
    }

    [Fact]
    public async Task Ordering_list_put_missing_returns_404()
    {
        var update = await Client.PutAsync(
            "/api/catalog/ordering-lists/999999999",
            Json("{\"name\":\"Qualquer\",\"isEnabled\":true}"),
            ShortToken());

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
    }

    [Fact]
    public async Task Ordering_list_put_blank_name_returns_400()
    {
        var key = $"w4-ord-blank-{Guid.NewGuid():N}";
        var create = await Client.PostAsync(
            "/api/catalog/ordering-lists",
            Json($"{{\"key\":\"{key}\",\"name\":\"W4 Blank\",\"isEnabled\":true}}"),
            ShortToken());
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        long id;
        using (var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync()))
        {
            id = doc.RootElement.GetProperty("id").GetInt64();
        }

        var update = await Client.PutAsync(
            $"/api/catalog/ordering-lists/{id}",
            Json("{\"name\":\"   \",\"isEnabled\":true}"),
            ShortToken());

        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        using var errDoc = JsonDocument.Parse(await update.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(errDoc.RootElement.GetProperty("error").GetString()));
    }
}
