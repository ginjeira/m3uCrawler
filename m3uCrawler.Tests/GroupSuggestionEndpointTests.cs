using System;
using System.IO;
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
/// Wave D3 — testes HTTP de <c>GET /api/catalog/group-suggestion</c>.
/// A sugestão é apenas pré-selecção: um grupo conhecido devolve a
/// <c>groupKey</c> + <c>groupName</c>; input desconhecido devolve 200 com
/// <c>groupKey</c> nulo.
/// </summary>
[Collection("DashboardStaticState")]
public class GroupSuggestionEndpointTests : IAsyncLifetime
{
    private const string ValidPassword = "a-very-strong-password";
    private const string SuggestionEndpoint = "/api/catalog/group-suggestion";

    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _outputDir;
    private readonly string _storePath;

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;
    private PlaylistComposerService _composer = null!;
    private ImportHistoryService _history = null!;
    private ConfigurationLifecycleService _lifecycle = null!;
    private AuthService _auth = null!;
    private BootstrapService _bootstrap = null!;
    private string _csrf = string.Empty;
    private DashboardBootstrapEndpointTests.DashboardHarness? _harness;

    public GroupSuggestionEndpointTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"group-suggestion-endpoint-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _outputDir = Path.Combine(_root, "output");
        _storePath = Path.Combine(_root, ConfigurationLifecycleStore.FileName);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_outputDir);
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
        if (_harness != null)
        {
            await _harness.DisposeAsync();
        }
        WebDashboardService.SetAuth(null, null);
        WebDashboardService.SetConfigurationLifecycle(null);
        TestTempDb.CleanupDirectory(_root);
    }

    private async Task<DashboardBootstrapEndpointTests.DashboardHarness> StartAuthorizedHarnessAsync()
    {
        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history,
            _lifecycle, _auth, _bootstrap, webToken: null);

        var start = await _harness.Client.PostAsync("/api/bootstrap/start", EmptyJson());
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);

        var admin = await _harness.Client.PostAsync(
            "/api/bootstrap/admin",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);

        var complete = await _harness.Client.PostAsync("/api/bootstrap/complete", EmptyJson());
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        var login = await _harness.Client.PostAsync(
            "/api/session",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var loginDoc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        _csrf = loginDoc.RootElement.GetProperty("csrfToken").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(_csrf));

        return _harness;
    }

    private static StringContent EmptyJson()
        => new("{}", Encoding.UTF8, "application/json");

    private static HttpRequestMessage WithCsrf(HttpMethod method, string path, string body, string csrf)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CSRF-Token", csrf);
        return request;
    }

    private static async Task<JsonDocument> GetSuggestionAsync(
        DashboardBootstrapEndpointTests.DashboardHarness harness, string group, string title)
    {
        var url = SuggestionEndpoint
            + "?group=" + Uri.EscapeDataString(group)
            + "&title=" + Uri.EscapeDataString(title);
        var response = await harness.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Known_group_returns_group_key_and_display_name()
    {
        var harness = await StartAuthorizedHarnessAsync();

        using var doc = await GetSuggestionAsync(harness, "eu | pt | esportes", "RTP1");
        Assert.Equal(
            CanonicalGroupKeys.PortugalDesporto,
            doc.RootElement.GetProperty("groupKey").GetString());
        Assert.Equal("PortugalDesporto", doc.RootElement.GetProperty("groupName").GetString());
    }

    [Fact]
    public async Task Title_only_fallback_returns_group_key()
    {
        var harness = await StartAuthorizedHarnessAsync();

        using var doc = await GetSuggestionAsync(harness, string.Empty, "Canal Desporto HD");
        Assert.Equal(
            CanonicalGroupKeys.PortugalDesporto,
            doc.RootElement.GetProperty("groupKey").GetString());
    }

    [Fact]
    public async Task Unknown_input_returns_null_group_key_with_200()
    {
        var harness = await StartAuthorizedHarnessAsync();

        using var doc = await GetSuggestionAsync(harness, "canal aleatorio", "titulo desconhecido");
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("groupKey").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("groupName").ValueKind);
    }

    [Fact]
    public async Task Post_is_not_allowed()
    {
        var harness = await StartAuthorizedHarnessAsync();

        var response = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, SuggestionEndpoint, "{}", _csrf));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
