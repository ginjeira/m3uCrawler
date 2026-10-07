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
/// PHASE 13 (Wave 13-4b) — Endpoints de override por canal
/// <c>GET/POST /api/catalog/source-selection-policies/channels</c> e
/// <c>GET/DELETE /api/catalog/source-selection-policies/channels/{key}</c>
/// sobre o <c>DashboardHarness</c> real: ciclo CRUD, validação, persistência,
/// autenticação por sessão e CSRF. A identidade é a chave canónica pública;
/// o <c>canonicalChannelId</c> nunca é exposto. Partilha a colecção
/// <c>DashboardStaticState</c> porque muta o estado estático do dashboard.
/// </summary>
[Collection("DashboardStaticState")]
public class SourceSelectionPolicyChannelEndpointTests : IAsyncLifetime
{
    private const string ValidPassword = "a-very-strong-password";
    private const string ChannelsEndpoint = "/api/catalog/source-selection-policies/channels";

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

    public SourceSelectionPolicyChannelEndpointTests()
    {
        _root = TestTempDb.SuitePath($"source-selection-policy-channel-dash-{Guid.NewGuid():N}");
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

    private static HttpRequestMessage PostJson(string path, string body, string? csrf = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (csrf != null)
        {
            request.Headers.Add("X-CSRF-Token", csrf);
        }
        return request;
    }

    private static string ChannelBody(
        string? canonicalChannelKey,
        int? maxSourcesPerChannel,
        bool? preferDistinctProviders,
        int? maxSourcesPerProvider,
        bool? allowFallbackToSameProvider)
        => JsonSerializer.Serialize(new
        {
            canonicalChannelKey,
            maxSourcesPerChannel,
            preferDistinctProviders,
            maxSourcesPerProvider,
            allowFallbackToSameProvider,
        });

    private static string ValidChannelBody(
        string key = "channel-dash-1",
        int maxSourcesPerChannel = 3,
        bool preferDistinctProviders = false,
        int? maxSourcesPerProvider = null,
        bool allowFallbackToSameProvider = true)
        => ChannelBody(key, maxSourcesPerChannel, preferDistinctProviders, maxSourcesPerProvider, allowFallbackToSameProvider);

    private static string OneEndpoint(string key)
        => ChannelsEndpoint + "/" + Uri.EscapeDataString(key);

    private async Task<SourceSelectionPolicyEntity?> ReadChannelRowAsync(string key)
    {
        var scope = "channel:" + key;
        await using var context = _factory.CreateDbContext();
        return await context.SourceSelectionPolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.ScopeKey == scope);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Full_crud_lifecycle_over_the_channel_override_endpoints()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        // Lista vazia antes de qualquer override.
        var initialList = await harness.Client.GetAsync(ChannelsEndpoint);
        Assert.Equal(HttpStatusCode.OK, initialList.StatusCode);
        var initialJson = await ReadJsonAsync(initialList);
        Assert.Empty(initialJson.GetProperty("overrides").EnumerateArray());

        // POST cria.
        var created = await harness.Client.SendAsync(
            PostJson(ChannelsEndpoint, ValidChannelBody("channel-dash-crud"), csrf));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var createdJson = await ReadJsonAsync(created);
        Assert.Equal("channel:channel-dash-crud", createdJson.GetProperty("scopeKey").GetString());
        Assert.Equal("channel-dash-crud", createdJson.GetProperty("canonicalChannelKey").GetString());

        // GET one devolve o override.
        var getOne = await harness.Client.GetAsync(OneEndpoint("channel-dash-crud"));
        Assert.Equal(HttpStatusCode.OK, getOne.StatusCode);
        var getOneJson = await ReadJsonAsync(getOne);
        Assert.Equal(3, getOneJson.GetProperty("maxSourcesPerChannel").GetInt32());

        // GET list contém-o.
        var list = await harness.Client.GetAsync(ChannelsEndpoint);
        var listJson = await ReadJsonAsync(list);
        Assert.Single(listJson.GetProperty("overrides").EnumerateArray());

        // POST update no mesmo key → valores actualizados, uma única linha.
        var updated = await harness.Client.SendAsync(
            PostJson(ChannelsEndpoint, ValidChannelBody("channel-dash-crud", 7, true, 2, false), csrf));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var row = await ReadChannelRowAsync("channel-dash-crud");
        Assert.NotNull(row);
        Assert.Equal(7, row!.MaxSourcesPerChannel);
        Assert.True(row.PreferDistinctProviders);
        Assert.Equal(2, row.MaxSourcesPerProvider);
        Assert.False(row.AllowFallbackToSameProvider);

        await using (var context = _factory.CreateDbContext())
        {
            Assert.Equal(1, await context.SourceSelectionPolicies.CountAsync());
        }

        // DELETE → {deleted:true}; GET passa a 404. Mutação exige CSRF.
        var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, OneEndpoint("channel-dash-crud"));
        deleteRequest.Headers.Add("X-CSRF-Token", csrf);
        var deleted = await harness.Client.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var deletedJson = await ReadJsonAsync(deleted);
        Assert.True(deletedJson.GetProperty("deleted").GetBoolean());

        var afterDelete = await harness.Client.GetAsync(OneEndpoint("channel-dash-crud"));
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
        Assert.Null(await ReadChannelRowAsync("channel-dash-crud"));
    }

    [Theory]
    [InlineData("{\"canonicalChannelKey\":\"k\",\"maxSourcesPerChannel\":-1,\"preferDistinctProviders\":true,\"allowFallbackToSameProvider\":true}")]
    [InlineData("{\"canonicalChannelKey\":\"k\",\"maxSourcesPerChannel\":1,\"preferDistinctProviders\":true,\"maxSourcesPerProvider\":0,\"allowFallbackToSameProvider\":true}")]
    [InlineData("{\"canonicalChannelKey\":\"k\",\"maxSourcesPerChannel\":1,\"preferDistinctProviders\":true,\"maxSourcesPerProvider\":-1,\"allowFallbackToSameProvider\":true}")]
    [InlineData("{\"maxSourcesPerChannel\":1,\"preferDistinctProviders\":true,\"allowFallbackToSameProvider\":true}")]
    [InlineData("{\"canonicalChannelKey\":\"k\",\"maxSourcesPerChannel\":1,\"allowFallbackToSameProvider\":true}")]
    [InlineData("{\"canonicalChannelKey\":\"k\",\"maxSourcesPerChannel\":1,\"preferDistinctProviders\":true}")]
    public async Task Invalid_payload_is_rejected_and_not_persisted(string body)
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await harness.Client.SendAsync(PostJson(ChannelsEndpoint, body, csrf));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var context = _factory.CreateDbContext();
        Assert.Equal(0, await context.SourceSelectionPolicies.CountAsync());
    }

    [Fact]
    public async Task Zero_max_sources_per_channel_is_accepted_and_persisted()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await harness.Client.SendAsync(
            PostJson(ChannelsEndpoint, ValidChannelBody("channel-dash-zero", maxSourcesPerChannel: 0), csrf));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = await ReadChannelRowAsync("channel-dash-zero");
        Assert.NotNull(row);
        Assert.Equal(0, row!.MaxSourcesPerChannel);
    }

    [Fact]
    public async Task Mutation_requires_session_then_csrf()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);

        // Sem sessão → 401.
        var noSession = await harness.Client.SendAsync(
            PostJson(ChannelsEndpoint, ValidChannelBody()));
        Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);

        var csrf = await LoginAsync(harness);

        // Sessão sem CSRF → 403 csrf-invalid.
        var noCsrf = await harness.Client.SendAsync(
            PostJson(ChannelsEndpoint, ValidChannelBody()));
        Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
        Assert.Contains("csrf-invalid", await noCsrf.Content.ReadAsStringAsync());

        // Com CSRF passa o gate (não 403).
        var withCsrf = await harness.Client.SendAsync(
            PostJson(ChannelsEndpoint, ValidChannelBody(), csrf));
        Assert.NotEqual(HttpStatusCode.Forbidden, withCsrf.StatusCode);
        Assert.True(
            withCsrf.StatusCode == HttpStatusCode.OK
            || withCsrf.StatusCode == HttpStatusCode.BadRequest,
            $"esperado 200/400, obtido {(int)withCsrf.StatusCode}");
    }

    [Fact]
    public async Task Successful_post_is_read_back_from_a_fresh_context_and_omits_canonical_channel_id()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await harness.Client.SendAsync(
            PostJson(ChannelsEndpoint, ChannelBody("channel-dash-fresh", 6, true, 2, false), csrf));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("canonicalChannelId", body);

        // DbContext novo confirma a persistência.
        var row = await ReadChannelRowAsync("channel-dash-fresh");
        Assert.NotNull(row);
        Assert.Equal("channel:channel-dash-fresh", row!.ScopeKey);
        Assert.Equal("channel-dash-fresh", row.CanonicalChannelKey);
        Assert.Equal(6, row.MaxSourcesPerChannel);
        Assert.True(row.PreferDistinctProviders);
        Assert.Equal(2, row.MaxSourcesPerProvider);
        Assert.False(row.AllowFallbackToSameProvider);

        var list = await harness.Client.GetAsync(ChannelsEndpoint);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listBody = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("canonicalChannelId", listBody);
    }
}
