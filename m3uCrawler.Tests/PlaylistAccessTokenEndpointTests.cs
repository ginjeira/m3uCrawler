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
/// Wave PlaylistToken — token de máquina editável em runtime para as
/// playlists M3U (<c>/api/playlist</c>, <c>/api/playlist_temp</c>,
/// <c>/api/playlist_acquired</c>), com precedência sobre o
/// <c>--web-token</c>. Cobre o gate, as rotas de Setup
/// (<c>/api/playlist-token</c> e <c>/reset</c>) e a validação.
/// </summary>
[Collection("DashboardStaticState")]
public class PlaylistAccessTokenEndpointTests : IAsyncLifetime
{
    private const string ValidPassword = "a-very-strong-password";
    private const string CliToken = "cli-machine-token-value";

    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _outputDir;
    private readonly string _runtimeDataDir;
    private readonly string _storePath;

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;
    private PlaylistComposerService _composer = null!;
    private ImportHistoryService _history = null!;
    private ConfigurationLifecycleService _lifecycle = null!;
    private AuthService _auth = null!;
    private BootstrapService _bootstrap = null!;
    private DashboardBootstrapEndpointTests.DashboardHarness? _harness;

    public PlaylistAccessTokenEndpointTests()
    {
        _root = TestTempDb.SuitePath($"playlist-token-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _outputDir = Path.Combine(_root, "output");
        _runtimeDataDir = Path.Combine(_root, "runtime-data");
        _storePath = Path.Combine(_root, ConfigurationLifecycleStore.FileName);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_outputDir);
        Directory.CreateDirectory(_runtimeDataDir);
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await TestCatalogSeed.ApplyDefaultsAsync(ctx);
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

    private DashboardBootstrapEndpointTests.DashboardHarness StartHarness(string? webToken = CliToken)
    {
        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history, _lifecycle, _auth, _bootstrap,
            webToken: webToken, runtimeDataDir: _runtimeDataDir);
        return _harness;
    }

    private static async Task ReachReadyAsync(DashboardBootstrapEndpointTests.DashboardHarness harness)
    {
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.PostAsync(
            "/api/bootstrap/start", EmptyJson())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.PostAsync(
            "/api/bootstrap/admin",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.PostAsync(
            "/api/bootstrap/complete", EmptyJson())).StatusCode);
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

    private static StringContent EmptyJson()
        => new("{}", Encoding.UTF8, "application/json");

    private static HttpRequestMessage WithCsrf(
        HttpMethod method, string path, string csrf, string? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-Token", csrf);
        if (json != null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        return request;
    }

    private static HttpClient AnonymousClient(DashboardBootstrapEndpointTests.DashboardHarness harness)
        => new() { BaseAddress = new Uri($"http://127.0.0.1:{harness.Port}") };

    private static HttpRequestMessage WithBearer(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Authorization", $"Bearer {token}");
        return request;
    }

    private async Task SeedPlaylistsAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(_outputDir, "playlist.m3u"), "#EXTM3U\n");
        await File.WriteAllTextAsync(Path.Combine(_outputDir, "playlist_temp.m3u"), "#EXTM3U\n");
        await File.WriteAllTextAsync(Path.Combine(_outputDir, "playlist_acquired.m3u"), "#EXTM3U\n");
    }

    private static string SetBody(string token)
        => JsonSerializer.Serialize(new { token });

    [Fact]
    public async Task Get_without_any_token_reports_none_and_never_returns_value()
    {
        var harness = StartHarness(webToken: null);
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        var response = await harness.Client.GetAsync("/api/playlist-token");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("hasToken").GetBoolean());
        Assert.Equal("none", doc.RootElement.GetProperty("source").GetString());
        Assert.DoesNotContain("Bearer", body);
    }

    [Fact]
    public async Task Set_valid_token_overrides_cli_token_for_playlists()
    {
        const string newToken = "dashboard-machine-token-1";
        var harness = StartHarness(webToken: CliToken);
        await ReachReadyAsync(harness);
        await SeedPlaylistsAsync();
        var csrf = await LoginAsync(harness);

        var set = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, "/api/playlist-token", csrf, SetBody(newToken)));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        using var anon = AnonymousClient(harness);
        Assert.Equal(HttpStatusCode.OK,
            (await anon.SendAsync(WithBearer(HttpMethod.Get, "/api/playlist", newToken))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await anon.SendAsync(WithBearer(HttpMethod.Get, "/api/playlist_temp", newToken))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await anon.SendAsync(WithBearer(HttpMethod.Get, "/api/playlist_acquired", newToken))).StatusCode);

        // O token CLI antigo deixa de autorizar (o store sobrepõe).
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.SendAsync(WithBearer(HttpMethod.Get, "/api/playlist", CliToken))).StatusCode);
    }

    [Fact]
    public async Task Get_after_set_reports_settings_and_omits_value()
    {
        const string newToken = "dashboard-machine-token-2";
        var harness = StartHarness(webToken: CliToken);
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, "/api/playlist-token", csrf, SetBody(newToken)));

        var response = await harness.Client.GetAsync("/api/playlist-token");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("hasToken").GetBoolean());
        Assert.Equal("settings", doc.RootElement.GetProperty("source").GetString());
        Assert.DoesNotContain(newToken, body);
    }

    [Fact]
    public async Task Reset_returns_token_once_and_invalidates_previous()
    {
        const string firstToken = "dashboard-machine-token-3";
        var harness = StartHarness(webToken: CliToken);
        await ReachReadyAsync(harness);
        await SeedPlaylistsAsync();
        var csrf = await LoginAsync(harness);

        await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, "/api/playlist-token", csrf, SetBody(firstToken)));

        var reset = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, "/api/playlist-token/reset", csrf, "{}"));
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        string generated;
        using (var doc = JsonDocument.Parse(await reset.Content.ReadAsStringAsync()))
        {
            generated = doc.RootElement.GetProperty("token").GetString()!;
            Assert.True(doc.RootElement.GetProperty("hasToken").GetBoolean());
            Assert.Equal("settings", doc.RootElement.GetProperty("source").GetString());
        }
        Assert.False(string.IsNullOrWhiteSpace(generated));
        Assert.NotEqual(firstToken, generated);

        using var anon = AnonymousClient(harness);
        Assert.Equal(HttpStatusCode.OK,
            (await anon.SendAsync(WithBearer(HttpMethod.Get, "/api/playlist", generated))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.SendAsync(WithBearer(HttpMethod.Get, "/api/playlist", firstToken))).StatusCode);
    }

    [Fact]
    public async Task Delete_restores_cli_source_and_cli_token()
    {
        const string newToken = "dashboard-machine-token-4";
        var harness = StartHarness(webToken: CliToken);
        await ReachReadyAsync(harness);
        await SeedPlaylistsAsync();
        var csrf = await LoginAsync(harness);

        await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, "/api/playlist-token", csrf, SetBody(newToken)));

        var delete = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Delete, "/api/playlist-token", csrf));
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        using (var doc = JsonDocument.Parse(await delete.Content.ReadAsStringAsync()))
        {
            Assert.False(doc.RootElement.GetProperty("hasToken").GetBoolean());
            Assert.Equal("cli", doc.RootElement.GetProperty("source").GetString());
        }

        using var anon = AnonymousClient(harness);
        Assert.Equal(HttpStatusCode.OK,
            (await anon.SendAsync(WithBearer(HttpMethod.Get, "/api/playlist", CliToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.SendAsync(WithBearer(HttpMethod.Get, "/api/playlist", newToken))).StatusCode);
    }

    [Fact]
    public async Task Set_without_csrf_is_forbidden()
    {
        var harness = StartHarness(webToken: null);
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        var response = await harness.Client.PostAsync(
            "/api/playlist-token",
            new StringContent(SetBody("dashboard-machine-token-5"), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("csrf-invalid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Set_with_invalid_token_is_rejected()
    {
        var harness = StartHarness(webToken: null);
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var tooShort = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, "/api/playlist-token", csrf, SetBody("short")));
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        var withWhitespace = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, "/api/playlist-token", csrf, SetBody("has space inside token")));
        Assert.Equal(HttpStatusCode.BadRequest, withWhitespace.StatusCode);
    }

    [Fact]
    public async Task Previews_remain_unauthorizable_by_machine_token()
    {
        const string newToken = "dashboard-machine-token-6";
        var harness = StartHarness(webToken: CliToken);
        await ReachReadyAsync(harness);
        await SeedPlaylistsAsync();
        var csrf = await LoginAsync(harness);

        await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, "/api/playlist-token", csrf, SetBody(newToken)));

        using var anon = AnonymousClient(harness);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.SendAsync(WithBearer(HttpMethod.Get, "/api/playlist/preview", newToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.SendAsync(WithBearer(HttpMethod.Get, "/api/playlist_temp/preview", newToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.SendAsync(WithBearer(HttpMethod.Get, "/api/playlist_acquired/preview", newToken))).StatusCode);
    }
}
