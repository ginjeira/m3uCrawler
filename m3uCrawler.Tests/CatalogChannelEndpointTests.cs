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
/// Wave A / finding F-01 — o handler method-less de
/// <c>/api/catalog/channels</c> sombreava o handler de criação, pelo
/// que <c>POST /api/catalog/channels</c> devolvia 200 + a lista sem
/// criar nada. Cobre:
///
/// <list type="bullet">
///   <item><c>POST</c> cria e persiste o canal (201 + <c>id</c>).</item>
///   <item><c>GET</c> continua a listar.</item>
///   <item>Método não suportado na colecção devolve 405.</item>
///   <item>Os restantes handlers de coleção auditados
///         (<c>reviews</c>, <c>sync-runs</c>,
///         <c>pending-country-approvals</c>) também distinguem
///         GET de métodos errados.</item>
/// </list>
/// </summary>
[Collection("DashboardStaticState")]
public class CatalogChannelEndpointTests : IAsyncLifetime
{
    private const string ValidPassword = "a-very-strong-password";
    private const string ChannelsEndpoint = "/api/catalog/channels";

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

    public CatalogChannelEndpointTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"catalog-channel-endpoint-{Guid.NewGuid():N}");
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

    private static HttpRequestMessage WithCsrf(HttpMethod method, string path, string body, string csrf)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CSRF-Token", csrf);
        return request;
    }

    [Fact]
    public async Task Post_creates_channel_persists_it_and_get_still_lists()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        // GET continua a listar (array, 200).
        var initialList = await harness.Client.GetAsync(ChannelsEndpoint);
        Assert.Equal(HttpStatusCode.OK, initialList.StatusCode);
        using (var initialDoc = JsonDocument.Parse(await initialList.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Array, initialDoc.RootElement.ValueKind);
        }

        var key = "wave-a-create-" + Guid.NewGuid().ToString("N")[..8];
        var body = JsonSerializer.Serialize(new
        {
            key,
            displayName = "Wave A Create",
            country = "PT",
            editorialCategory = "Live",
            groupKey = "pt-generalistas",
            publicationPolicy = "CreateEligible",
            isEnabled = true,
            aliases = new[] { "wave a create hd" },
        });

        var created = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, ChannelsEndpoint, body, csrf));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using (var createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
        {
            Assert.True(createdDoc.RootElement.TryGetProperty("id", out var idProp));
            Assert.True(idProp.GetInt64() > 0, "O canal criado deve devolver um 'id' positivo.");
            Assert.Equal(key, createdDoc.RootElement.GetProperty("key").GetString());
            Assert.Equal("Wave A Create", createdDoc.RootElement.GetProperty("displayName").GetString());
        }

        // Persistência confirmada num DbContext novo.
        await using (var context = _factory.CreateDbContext())
        {
            Assert.True(await context.CanonicalChannels.AnyAsync(c => c.Key == key));
        }

        // GET lista e inclui o canal criado.
        var list = await harness.Client.GetAsync(ChannelsEndpoint);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Array, listDoc.RootElement.ValueKind);
            Assert.Contains(
                listDoc.RootElement.EnumerateArray(),
                e => e.GetProperty("key").GetString() == key);
        }
    }

    [Fact]
    public async Task Post_with_groupKey_returns_group_fields()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var key = "wave-c-groupkey-" + Guid.NewGuid().ToString("N")[..8];
        var body = JsonSerializer.Serialize(new
        {
            key,
            displayName = "Wave C GroupKey",
            country = "PT",
            editorialCategory = "Live",
            groupKey = "pt-desporto",
            publicationPolicy = "CreateEligible",
            isEnabled = true,
            aliases = new[] { "wave c groupkey hd" },
        });

        var created = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, ChannelsEndpoint, body, csrf));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("groupId").GetInt64() > 0);
        Assert.Equal("pt-desporto", doc.RootElement.GetProperty("groupKey").GetString());
        Assert.Equal("PortugalDesporto", doc.RootElement.GetProperty("groupName").GetString());
    }

    [Fact]
    public async Task Post_with_unknown_groupKey_returns_400()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var body = JsonSerializer.Serialize(new
        {
            key = "wave-c-bad-groupkey-" + Guid.NewGuid().ToString("N")[..8],
            displayName = "Wave C Bad GroupKey",
            editorialCategory = "Live",
            groupKey = "does-not-exist",
            publicationPolicy = "CreateEligible",
            isEnabled = true,
            aliases = Array.Empty<string>(),
        });

        var response = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, ChannelsEndpoint, body, csrf));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("GroupKey inválido", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Post_without_groupKey_defaults_to_Other()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var key = "wave-c-default-group-" + Guid.NewGuid().ToString("N")[..8];
        var body = JsonSerializer.Serialize(new
        {
            key,
            displayName = "Wave C Default Group",
            editorialCategory = "Live",
            publicationPolicy = "CreateEligible",
            isEnabled = true,
            aliases = Array.Empty<string>(),
        });

        var created = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, ChannelsEndpoint, body, csrf));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        // Sem groupKey → grupo por omissão "other".
        Assert.True(doc.RootElement.GetProperty("groupId").GetInt64() > 0);
        Assert.Equal("other", doc.RootElement.GetProperty("groupKey").GetString());
    }

    [Fact]
    public async Task Unsupported_method_on_channels_collection_returns_405()    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Put, ChannelsEndpoint, "{}", csrf));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/catalog/reviews")]
    [InlineData("/api/catalog/sync-runs")]
    [InlineData("/api/catalog/pending-country-approvals")]
    public async Task Audited_collection_handlers_accept_get_and_reject_other_methods(string path)
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var get = await harness.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var post = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, path, "{}", csrf));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }
}
