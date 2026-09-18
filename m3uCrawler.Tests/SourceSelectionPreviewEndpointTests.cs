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
/// PHASE 13 (Wave 13-5) — Endpoint HTTP
/// <c>GET /api/catalog/source-selection-policies/preview</c> sobre o
/// <c>DashboardHarness</c> real: gate de sessão, resposta JSON read-only,
/// sanitização de credenciais e rejeição de métodos não-GET. Partilha a
/// colecção <c>DashboardStaticState</c> porque muta o estado estático do
/// dashboard.
/// </summary>
[Collection("DashboardStaticState")]
public class SourceSelectionPreviewEndpointTests : IAsyncLifetime
{
    private const string ValidPassword = "a-very-strong-password";
    private const string PreviewEndpoint = "/api/catalog/source-selection-policies/preview";
    private const string CredentialUrl = "http://user:secret@preview-dash.example.test/live/USER/PASS/1";

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

    public SourceSelectionPreviewEndpointTests()
    {
        _root = TestTempDb.SuitePath($"source-selection-preview-dash-{Guid.NewGuid():N}");
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
        TestTempDb.Cleanup(_dbPath);
        TestTempDb.Cleanup(_storePath);
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

    private async Task SeedChannelSourceAsync(string url)
    {
        var source = await _resolver.EnsureSourceAsync(
            "preview-dash", "preview-dash", SourceKind.Telegram, "telegram://preview-dash", 0);
        var channels = await _resolver.ListCanonicalChannelsAsync();
        await _resolver.RecordChannelSourceAsync(
            channels[0].Id, source.Id, url, matchMethod: "test");
    }

    private async Task<int> PolicyRowCountAsync()
    {
        await using var context = _factory.CreateDbContext();
        return await context.SourceSelectionPolicies.CountAsync();
    }

    [Fact]
    public async Task Preview_without_session_is_unauthorized()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);

        var response = await harness.Client.GetAsync(PreviewEndpoint);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_preview_returns_json_and_does_not_create_a_global_policy_row()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        // Semear depois do bootstrap: um catálogo já povoado antes do arranque
        // seria interpretado como evidência legacy e adoptaria READY.
        await SeedChannelSourceAsync("http://preview-ok.example.test/1.ts");

        var response = await harness.Client.GetAsync(PreviewEndpoint);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("applied", out var applied));
        Assert.True(applied.GetBoolean());
        Assert.True(root.TryGetProperty("metrics", out _));
        Assert.True(root.TryGetProperty("channels", out var channels));
        Assert.True(channels.GetArrayLength() >= 1);

        Assert.Equal(0, await PolicyRowCountAsync());
    }

    [Fact]
    public async Task Authenticated_preview_response_is_sanitized()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        await SeedChannelSourceAsync(CredentialUrl);

        var response = await harness.Client.GetAsync(PreviewEndpoint);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PASS", body, StringComparison.Ordinal);
        Assert.DoesNotContain("USER", body, StringComparison.Ordinal);
        Assert.Contains("***", body);
    }

    [Fact]
    public async Task Preview_rejects_non_get_methods_with_405()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await harness.Client.SendAsync(
            PostJson(PreviewEndpoint, "{}", csrf));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
