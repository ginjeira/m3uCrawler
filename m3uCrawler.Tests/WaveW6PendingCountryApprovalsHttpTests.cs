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
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W6 — Regressão da rota morta
/// <c>POST /api/catalog/pending-country-approvals/{id}/approve|reject</c>.
/// O parser anterior fazia <c>TryParse("5/approve")</c> e devolvia 400 antes
/// do ramo de decisão. Aqui valida-se o parsing por segmentos, as
/// transições de estado e o efeito em IdentityRules.
/// </summary>
[Collection("DashboardStaticState")]
public class WaveW6PendingCountryApprovalsHttpTests : IAsyncLifetime
{
    private const string ValidPassword = "a-very-strong-password";
    private const string BaseRoute = "/api/catalog/pending-country-approvals";

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

    public WaveW6PendingCountryApprovalsHttpTests()
    {
        _root = TestTempDb.SuitePath($"w6-pending-approvals-{Guid.NewGuid():N}");
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
        if (_harness != null) await _harness.DisposeAsync();
        WebDashboardService.SetAuth(null, null);
        WebDashboardService.SetConfigurationLifecycle(null);
        TestTempDb.CleanupDirectory(_root);
    }

    private DashboardBootstrapEndpointTests.DashboardHarness StartHarness()
    {
        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history, _lifecycle, _auth, _bootstrap, webToken: null);
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

    private static HttpRequestMessage Post(string path, string csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CSRF-Token", csrf);
        return request;
    }

    private async Task<long> SeedPendingAsync(string identity, string reason)
    {
        var entity = await _resolver.UpsertPendingCountryApprovalAsync(
            identity, identity.ToUpperInvariant(), "pt",
            "http://x/live/u/p/1", "Portugal", reason);
        return entity.Id;
    }

    [Fact]
    public async Task Approve_returns_200_and_creates_review_only_identity_rule()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        // Seed APÓS o bootstrap: dados pré-existentes mudariam a evidência
        // legacy e o bootstrap passaria a 409.
        var id = await SeedPendingAsync("w6 approve " + Guid.NewGuid().ToString("N")[..8], "weak_country_match");

        var response = await harness.Client.SendAsync(Post($"{BaseRoute}/{id}/approve", csrf));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(id, doc.RootElement.GetProperty("id").GetInt64());
        Assert.Equal("Approved", doc.RootElement.GetProperty("state").GetString());

        await using var ctx = _factory.CreateDbContext();
        var item = await ctx.PendingCountryApprovals.SingleAsync(r => r.Id == id);
        Assert.Equal(PendingApprovalState.Approved, item.State);
        Assert.True(await ctx.IdentityRules.AnyAsync(r =>
            r.NormalizedIdentity == item.NormalizedIdentity
            && r.Disposition == RuleDisposition.ReviewOnly));
    }

    [Fact]
    public async Task Reject_returns_200_and_creates_excluded_identity_rule()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        var id = await SeedPendingAsync("w6 reject " + Guid.NewGuid().ToString("N")[..8], "affinity_no_channel");

        var response = await harness.Client.SendAsync(Post($"{BaseRoute}/{id}/reject", csrf));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Rejected", doc.RootElement.GetProperty("state").GetString());

        await using var ctx = _factory.CreateDbContext();
        var item = await ctx.PendingCountryApprovals.SingleAsync(r => r.Id == id);
        Assert.Equal(PendingApprovalState.Rejected, item.State);
        Assert.True(await ctx.IdentityRules.AnyAsync(r =>
            r.NormalizedIdentity == item.NormalizedIdentity
            && r.Disposition == RuleDisposition.Excluded));
    }

    [Fact]
    public async Task Invalid_id_returns_400()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await harness.Client.SendAsync(Post($"{BaseRoute}/not-a-number/approve", csrf));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Missing_pending_returns_404()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await harness.Client.SendAsync(Post($"{BaseRoute}/999999/approve", csrf));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unsupported_method_on_decision_route_returns_405()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        var response = await harness.Client.GetAsync($"{BaseRoute}/5/approve");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
