using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W3 — Semântica HTTP transversal. Reproduz os dois bugs confirmados na
/// auditoria runtime:
/// <list type="bullet">
///   <item><b>Status mascarado</b>: <c>WriteJsonAsync</c>/<c>WriteTextAsync</c>
///     repunham sempre <c>200</c>, pelo que um erro determinado pelo endpoint
///     (400/404/409) saía como 200 e o browser interpretava-o como sucesso.</item>
///   <item><b>Routing/fallback/hang</b>: rota não correspondida devolvia o HTML
///     do Dashboard com 200; métodos não suportados em ramos que fixavam 405 sem
///     escrever corpo ficavam pendurados; requests inválidos não terminavam.</item>
/// </list>
/// Cada teste delimita o pedido com um timeout curto, de modo que uma regressão
/// de <i>hang</i> falha de forma determinística em vez de bloquear.
/// </summary>
public sealed class WaveW3HttpSemanticsTests : IAsyncLifetime
{
    private string _root = null!;
    private string _dbPath = null!;
    private string _outputDir = null!;
    private DashboardBootstrapEndpointTests.DashboardHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"w3-http-{Guid.NewGuid():N}");
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

        // Contexto legacy standalone: não exige sessão nem CSRF, o que isola
        // estes testes ao comportamento HTTP (não à autenticação).
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

    /// <summary>Timeout curto: deteta hangs como falha em vez de bloquear a suite.</summary>
    private static CancellationToken ShortToken()
        => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    // ===================== ROUTING =====================

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Unknown_api_route_returns_404_json_and_never_hangs(string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/w3-nonexistent-route")
        {
            Content = method == "GET" ? null : Json("{}"),
        };

        var response = await Client.SendAsync(request, ShortToken());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not-found", body);
    }

    [Fact]
    public async Task Unknown_non_api_path_returns_404_not_dashboard_html()
    {
        var response = await Client.GetAsync("/w3-not-a-page", ShortToken());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        Assert.DoesNotContain("text/html", mediaType);
    }

    [Fact]
    public async Task Root_path_still_serves_dashboard_html()
    {
        var response = await Client.GetAsync("/", ShortToken());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<html", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Valid_api_route_still_returns_json_200()
    {
        var response = await Client.GetAsync("/api/version", ShortToken());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Unsupported_method_on_known_route_returns_405_and_does_not_hang()
    {
        // /api/catalog/affinity-groups suporta GET/POST; DELETE cai no ramo
        // que fixa 405 sem escrever corpo (o caso que antes ficava pendurado).
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/catalog/affinity-groups");

        var response = await Client.SendAsync(request, ShortToken());

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Ordering_put_without_route_returns_404_or_405_not_hang()
    {
        // W3 não implementa o PUT de Ordering (isso é W4); apenas garante que
        // um método/rota sem implementação responde e não fica pendurado.
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/catalog/ordering-lists/1")
        {
            Content = Json("{}"),
        };

        var response = await Client.SendAsync(request, ShortToken());

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"esperado 404 ou 405, obtido {(int)response.StatusCode}");
    }

    // ===================== STATUS (regressão de masking) =====================

    [Fact]
    public async Task Validation_error_is_preserved_as_400_not_masked_as_200()
    {
        // Afinidade com nome vazio → handler fixa 400 e escreve o corpo.
        // Antes: WriteJsonAsync repunha 200.
        var response = await Client.PostAsync(
            "/api/catalog/affinity-groups",
            Json("{\"name\":\"\",\"members\":[]}"),
            ShortToken());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Channel_create_returns_201_and_duplicate_returns_409()
    {
        var key = $"w3-test-{Guid.NewGuid():N}";
        var payload =
            $"{{\"key\":\"{key}\",\"displayName\":\"W3 Test\",\"country\":null," +
            "\"editorialCategory\":\"Live\",\"editorialGroup\":\"PortugalLive\"," +
            "\"publicationPolicy\":\"CreateEligible\",\"isEnabled\":true,\"aliases\":[]}";

        var created = await Client.PostAsync("/api/catalog/channels", Json(payload), ShortToken());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var duplicate = await Client.PostAsync("/api/catalog/channels", Json(payload), ShortToken());
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Missing_channel_returns_404_not_masked_as_200()
    {
        // DELETE de um canal inexistente → handler fixa 404.
        // Antes: WriteJsonAsync repunha 200.
        var response = await Client.DeleteAsync("/api/catalog/channels/999999999", ShortToken());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Known_api_list_returns_json_200()
    {
        var response = await Client.GetAsync("/api/catalog/channels", ShortToken());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }
}
