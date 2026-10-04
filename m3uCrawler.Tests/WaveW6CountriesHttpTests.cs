using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Auth;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W6 — Decisão Countries (Option B: configuração/validação, sem entidade
/// de domínio). Testes HTTP determinísticos sobre
/// <c>GET /api/countries</c>, <c>GET /api/country</c>,
/// <c>POST /api/country/save</c>, <c>DELETE /api/country</c> e
/// <c>GET /api/country/validate</c>, com o directório de país isolado por
/// runtime-data temporário (nunca toca no runtime-data real).
/// </summary>
[Collection("DashboardStaticState")]
public class WaveW6CountriesHttpTests : IAsyncLifetime
{
    private const string ValidPassword = "a-very-strong-password";

    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _outputDir;
    private readonly string _runtimeDataDir;
    private readonly string _countriesDir;
    private readonly string _storePath;

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;
    private PlaylistComposerService _composer = null!;
    private ImportHistoryService _history = null!;
    private ConfigurationLifecycleService _lifecycle = null!;
    private AuthService _auth = null!;
    private BootstrapService _bootstrap = null!;
    private DashboardBootstrapEndpointTests.DashboardHarness? _harness;

    public WaveW6CountriesHttpTests()
    {
        _root = TestTempDb.SuitePath($"w6-countries-http-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _outputDir = Path.Combine(_root, "output");
        _runtimeDataDir = Path.Combine(_root, "runtime-data");
        _countriesDir = Path.Combine(_runtimeDataDir, "countries");
        _storePath = Path.Combine(_root, ConfigurationLifecycleStore.FileName);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_outputDir);
        Directory.CreateDirectory(_countriesDir);
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();

        _resolver = new CatalogResolver(_factory, _dbPath);
        _composer = new PlaylistComposerService(_factory);
        _history = new ImportHistoryService(_outputDir);
        _lifecycle = new ConfigurationLifecycleService(
            new ConfigurationLifecycleStore(_storePath), _factory, _outputDir);

        var users = new AdminUserStore(_factory);
        _auth = new AuthService(users, new SessionStore(_factory));
        _bootstrap = new BootstrapService(
            _lifecycle, users,
            new BootstrapConfigurationValidator(_factory, _outputDir));
    }

    public async Task DisposeAsync()
    {
        if (_harness != null) await _harness.DisposeAsync();
        WebDashboardService.SetAuth(null, null);
        WebDashboardService.SetConfigurationLifecycle(null);
        WebDashboardService.SetDispatcharrSync(null, null);
        TestTempDb.CleanupDirectory(_root);
    }

    private DashboardBootstrapEndpointTests.DashboardHarness StartHarness()
    {
        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history, _lifecycle, _auth, _bootstrap,
            webToken: null, runtimeDataDir: _runtimeDataDir);
        return _harness;
    }

    private static async Task ReachReadyAsync(DashboardBootstrapEndpointTests.DashboardHarness harness)
    {
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.PostAsync(
            "/api/bootstrap/start", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.PostAsync(
            "/api/bootstrap/admin",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.PostAsync(
            "/api/bootstrap/complete", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
    }

    private static async Task<string> LoginAsync(DashboardBootstrapEndpointTests.DashboardHarness harness)
    {
        var login = await harness.Client.PostAsync(
            "/api/session",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("csrfToken").GetString()!;
    }

    private static HttpRequestMessage Json(HttpMethod method, string path, object body, string csrf)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CSRF-Token", csrf);
        return request;
    }

    private static HttpRequestMessage Delete(string path, string csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Add("X-CSRF-Token", csrf);
        return request;
    }

    [Fact]
    public async Task Save_preserves_the_provided_display_name()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/country/save",
            new { country = "pt", displayName = "Portugal", channels = new[] { "RTP1", "SIC" } }, csrf));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var get = await harness.Client.GetAsync("/api/country?country=pt");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal("Portugal", doc.RootElement.GetProperty("displayName").GetString());

        // Persistido no directório isolado, não no runtime-data do repo.
        Assert.True(File.Exists(Path.Combine(_countriesDir, "pt.json")));
    }

    [Fact]
    public async Task Create_country_via_save_appears_in_listing()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/country/save",
            new { country = "es", displayName = "Espanha", channels = Array.Empty<string>() }, csrf));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var list = await harness.Client.GetAsync("/api/countries");
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Contains(
            doc.RootElement.EnumerateArray(),
            e => e.GetProperty("country").GetString() == "es"
                 && e.GetProperty("displayName").GetString() == "Espanha");
    }

    [Fact]
    public async Task Delete_country_returns_200_then_404()
    {
        new CountryChannelListService(_countriesDir).SaveCountry(new CountryChannelList
        {
            Country = "es",
            DisplayName = "Espanha",
            Channels = new List<string> { "La 1" },
        });

        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var first = await harness.Client.SendAsync(Delete("/api/country?country=es", csrf));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.False(File.Exists(Path.Combine(_countriesDir, "es.json")));

        var second = await harness.Client.SendAsync(Delete("/api/country?country=es", csrf));
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task Validate_still_works_with_country_config()
    {
        new CountryChannelListService(_countriesDir).SaveCountry(new CountryChannelList
        {
            Country = "pt",
            DisplayName = "Portugal",
            Channels = new List<string> { "RTP1", "SIC", "TVI" },
        });

        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        var response = await harness.Client.GetAsync("/api/country/validate?country=pt");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Portugal", doc.RootElement.GetProperty("displayName").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("totalChannels").GetInt32());
        Assert.False(doc.RootElement.GetProperty("isMatch").GetBoolean());
    }

    [Fact]
    public async Task Delete_without_country_param_returns_400()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await harness.Client.SendAsync(Delete("/api/country", csrf));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
