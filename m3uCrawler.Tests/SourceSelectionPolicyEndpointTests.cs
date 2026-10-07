using System;
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
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 13 (Wave 13-4) — Endpoint
/// <c>GET/POST /api/catalog/source-selection-policies</c> sobre o
/// <c>DashboardHarness</c> real: defaults, validação, persistência,
/// autenticação por sessão e CSRF. Partilha a colecção
/// <c>DashboardStaticState</c> porque muta o estado estático do dashboard.
/// </summary>
[Collection("DashboardStaticState")]
public class SourceSelectionPolicyEndpointTests : IAsyncLifetime
{
    private const string ValidPassword = "a-very-strong-password";
    private const string Endpoint = "/api/catalog/source-selection-policies";

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
    private DashboardBootstrapEndpointTests.DashboardHarness? _harness;

    public SourceSelectionPolicyEndpointTests()
    {
        _root = TestTempDb.SuitePath($"source-selection-policy-dash-{Guid.NewGuid():N}");
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

    private DashboardBootstrapEndpointTests.DashboardHarness StartHarness()
    {
        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history,
            _lifecycle, _auth, _bootstrap, webToken: null);
        return _harness;
    }

    private static async Task ReachReadyAsync(DashboardBootstrapEndpointTests.DashboardHarness harness)
    {
        var start = await harness.Client.PostAsync("/api/bootstrap/start", EmptyJson());
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);

        var admin = await harness.Client.PostAsync(
            "/api/bootstrap/admin",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);

        var complete = await harness.Client.PostAsync("/api/bootstrap/complete", EmptyJson());
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
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
        var csrf = doc.RootElement.GetProperty("csrfToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(csrf));
        return csrf!;
    }

    private static StringContent EmptyJson()
        => new("{}", Encoding.UTF8, "application/json");

    private static HttpRequestMessage PostJson(string body, string? csrf = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (csrf != null)
        {
            request.Headers.Add("X-CSRF-Token", csrf);
        }
        return request;
    }

    private static string PolicyBody(
        int? maxSourcesPerChannel,
        bool? preferDistinctProviders,
        int? maxSourcesPerProvider,
        bool? allowFallbackToSameProvider)
        => JsonSerializer.Serialize(new
        {
            maxSourcesPerChannel,
            preferDistinctProviders,
            maxSourcesPerProvider,
            allowFallbackToSameProvider,
        });

    private static string ValidPolicyBody(
        int maxSourcesPerChannel = 3,
        bool preferDistinctProviders = false,
        int? maxSourcesPerProvider = null,
        bool allowFallbackToSameProvider = true)
        => PolicyBody(maxSourcesPerChannel, preferDistinctProviders, maxSourcesPerProvider, allowFallbackToSameProvider);

    private async Task<SourceSelectionPolicyEntity> ReadRowAsync()
    {
        await using var context = _factory.CreateDbContext();
        return await context.SourceSelectionPolicies.AsNoTracking().SingleAsync();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Get_returns_defaults_and_creates_the_lazy_row()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        var response = await harness.Client.GetAsync(Endpoint);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await ReadJsonAsync(response);
        Assert.Equal("global", json.GetProperty("scopeKey").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("canonicalChannelKey").ValueKind);
        Assert.Equal(10, json.GetProperty("maxSourcesPerChannel").GetInt32());
        Assert.True(json.GetProperty("preferDistinctProviders").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("maxSourcesPerProvider").ValueKind);
        Assert.True(json.GetProperty("allowFallbackToSameProvider").GetBoolean());

        var row = await ReadRowAsync();
        Assert.Equal("global", row.ScopeKey);
        Assert.Null(row.CanonicalChannelKey);
        Assert.Equal(10, row.MaxSourcesPerChannel);
        Assert.Null(row.MaxSourcesPerProvider);
    }

    [Fact]
    public async Task Post_valid_policy_persists_and_is_reflected_by_get()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await harness.Client.SendAsync(
            PostJson(ValidPolicyBody(3, preferDistinctProviders: false, maxSourcesPerProvider: null), csrf));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = await ReadRowAsync();
        Assert.Equal(3, row.MaxSourcesPerChannel);
        Assert.False(row.PreferDistinctProviders);
        Assert.Null(row.MaxSourcesPerProvider);
        Assert.True(row.AllowFallbackToSameProvider);

        var get = await harness.Client.GetAsync(Endpoint);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var json = await ReadJsonAsync(get);
        Assert.Equal(3, json.GetProperty("maxSourcesPerChannel").GetInt32());
        Assert.False(json.GetProperty("preferDistinctProviders").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("maxSourcesPerProvider").ValueKind);
        Assert.True(json.GetProperty("allowFallbackToSameProvider").GetBoolean());
    }

    [Fact]
    public async Task Post_zero_max_sources_per_channel_is_accepted_and_persisted()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await harness.Client.SendAsync(
            PostJson(ValidPolicyBody(maxSourcesPerChannel: 0), csrf));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = await ReadRowAsync();
        Assert.Equal(0, row.MaxSourcesPerChannel);
    }

    [Theory]
    [InlineData("{\"maxSourcesPerChannel\":-1,\"preferDistinctProviders\":true,\"allowFallbackToSameProvider\":true}")]
    [InlineData("{\"maxSourcesPerChannel\":1,\"preferDistinctProviders\":true,\"maxSourcesPerProvider\":0,\"allowFallbackToSameProvider\":true}")]
    [InlineData("{\"maxSourcesPerChannel\":1,\"preferDistinctProviders\":true,\"maxSourcesPerProvider\":-1,\"allowFallbackToSameProvider\":true}")]
    [InlineData("{\"preferDistinctProviders\":true,\"allowFallbackToSameProvider\":true}")]
    [InlineData("{\"maxSourcesPerChannel\":1,\"allowFallbackToSameProvider\":true}")]
    [InlineData("{\"maxSourcesPerChannel\":1,\"preferDistinctProviders\":true}")]
    public async Task Invalid_payload_is_rejected_and_not_persisted(string body)
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await harness.Client.SendAsync(PostJson(body, csrf));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var context = _factory.CreateDbContext();
        Assert.Equal(0, await context.SourceSelectionPolicies.CountAsync());
    }

    [Fact]
    public async Task Mutation_requires_session_then_csrf()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);

        // Sem sessão → 401.
        var noSession = await harness.Client.SendAsync(PostJson(ValidPolicyBody()));
        Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);

        var csrf = await LoginAsync(harness);

        // Sessão sem CSRF → 403 csrf-invalid.
        var noCsrf = await harness.Client.SendAsync(PostJson(ValidPolicyBody()));
        Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
        Assert.Contains("csrf-invalid", await noCsrf.Content.ReadAsStringAsync());

        // Com CSRF passa o gate (não 403).
        var withCsrf = await harness.Client.SendAsync(PostJson(ValidPolicyBody(), csrf));
        Assert.NotEqual(HttpStatusCode.Forbidden, withCsrf.StatusCode);
    }

    [Fact]
    public async Task Successful_post_is_read_back_from_a_fresh_context()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await harness.Client.SendAsync(
            PostJson(PolicyBody(6, true, 2, false), csrf));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // O corpo da resposta devolve o que foi persistido…
        var echoed = await ReadJsonAsync(response);
        Assert.Equal(6, echoed.GetProperty("maxSourcesPerChannel").GetInt32());
        Assert.Equal(2, echoed.GetProperty("maxSourcesPerProvider").GetInt32());
        Assert.False(echoed.GetProperty("allowFallbackToSameProvider").GetBoolean());

        // …e um DbContext novo confirma-o na BD.
        await using var context = _factory.CreateDbContext();
        var row = await context.SourceSelectionPolicies.AsNoTracking().SingleAsync();
        Assert.Equal(6, row.MaxSourcesPerChannel);
        Assert.True(row.PreferDistinctProviders);
        Assert.Equal(2, row.MaxSourcesPerProvider);
        Assert.False(row.AllowFallbackToSameProvider);
    }
}
