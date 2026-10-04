using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Audit;
using m3uCrawler.Services.Auth;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using m3uCrawler.Services.Dispatcharr;
using m3uCrawler.Services.Sync;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W6 — Wiring de produção dos endpoints HTTP
/// <c>POST /api/dispatcharr/dry-run</c> e <c>POST /api/dispatcharr/sync</c>
/// através de <see cref="WebDashboardService.SetDispatcharrSync"/>.
/// Determinístico: transporte HTTP falso, nunca toca no Dispatcharr real.
/// Cobre:
/// <list type="bullet">
///   <item>dry-run → <c>mode=dry-run</c>;</item>
///   <item>sync → <c>mode=sync</c>;</item>
///   <item>config desactivada → 503;</item>
///   <item>coordenador/gate ausentes → 503;</item>
///   <item>playlistPath relativo ("playlist.m3u") resolvido sob outputDir.</item>
/// </list>
/// </summary>
[Collection("DashboardStaticState")]
public class WaveW6DispatcharrHttpTests : IAsyncLifetime
{
    private const string ValidPassword = "a-very-strong-password";
    private const string PlaylistBody =
        "#EXTM3U\n#EXTINF:-1 group-title=\"SPORT TV CHANNELS\",PT: SPORT TV NBA\n" +
        "https://crawler.example/sporttvnba\n";

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
    private AuditService _audit = null!;
    private DashboardBootstrapEndpointTests.DashboardHarness? _harness;

    public WaveW6DispatcharrHttpTests()
    {
        _root = TestTempDb.SuitePath($"w6-dispatcharr-http-{Guid.NewGuid():N}");
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

        _audit = new AuditService(_factory, () => new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc));
    }

    public async Task DisposeAsync()
    {
        if (_harness != null) await _harness.DisposeAsync();
        WebDashboardService.SetAuth(null, null);
        WebDashboardService.SetConfigurationLifecycle(null);
        WebDashboardService.SetAuditService(null);
        WebDashboardService.SetDispatcharrSync(null, null);
        TestTempDb.CleanupDirectory(_root);
    }

    private DashboardBootstrapEndpointTests.DashboardHarness StartHarness(
        DispatcharrSyncCoordinator? coordinator,
        DispatcharrConcurrencyGate? gate)
    {
        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history, _lifecycle, _auth, _bootstrap,
            webToken: null, auditService: _audit,
            dispatcharrSyncCoordinator: coordinator,
            dispatcharrConcurrencyGate: gate);
        return _harness;
    }

    private DispatcharrSyncCoordinator NewCoordinator(HttpMessageHandler transport, bool enabled = true, bool baseDryRun = true)
    {
        var cfg = new DispatcharrConfig
        {
            Enabled = enabled,
            BaseUrl = "http://dispatcharr.local",
            ApiKey = "PLACEHOLDER-API-KEY",
            DryRun = baseDryRun,
            MatchThreshold = 80,
            AliasFile = null,
        };
        return new DispatcharrSyncCoordinator(
            configLoader: () => cfg,
            catalogFactory: _ => Task.FromResult(_resolver),
            transport: transport);
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

    private static HttpRequestMessage Json(HttpMethod method, string path, object body, string? csrf)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (csrf != null) request.Headers.Add("X-CSRF-Token", csrf);
        return request;
    }

    private void WritePlaylist() =>
        File.WriteAllText(Path.Combine(_outputDir, "playlist.m3u"), PlaylistBody);

    [Fact]
    public async Task Dry_run_through_wired_coordinator_returns_dry_run_mode()
    {
        var harness = StartHarness(NewCoordinator(new EmptyDispatcharrHandler()), new DispatcharrConcurrencyGate());
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        WritePlaylist();

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/dispatcharr/dry-run",
            new { playlistPath = "playlist.m3u" }, csrf));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("dry-run", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("dry-run", doc.RootElement.GetProperty("status").GetString());
        Assert.True(doc.RootElement.TryGetProperty("counts", out _));
    }

    [Fact]
    public async Task Sync_through_wired_coordinator_returns_sync_mode()
    {
        var harness = StartHarness(NewCoordinator(new EmptyDispatcharrHandler()), new DispatcharrConcurrencyGate());
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        WritePlaylist();

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/dispatcharr/sync",
            new { playlistPath = "playlist.m3u" }, csrf));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("sync", doc.RootElement.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Relative_playlistPath_outside_output_dir_is_rejected()
    {
        var harness = StartHarness(NewCoordinator(new EmptyDispatcharrHandler()), new DispatcharrConcurrencyGate());
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/dispatcharr/dry-run",
            new { playlistPath = "../outside.m3u" }, csrf));

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
    }

    [Fact]
    public async Task Disabled_config_returns_503()
    {
        var harness = StartHarness(
            NewCoordinator(new EmptyDispatcharrHandler(), enabled: false),
            new DispatcharrConcurrencyGate());
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        WritePlaylist();

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/dispatcharr/dry-run",
            new { playlistPath = "playlist.m3u" }, csrf));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("dispatcharr-unavailable", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Missing_coordinator_returns_503()
    {
        WebDashboardService.SetDispatcharrSync(null, null);
        var harness = StartHarness(null, null);
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/dispatcharr/dry-run",
            new { playlistPath = "playlist.m3u" }, csrf));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("dispatcharr-unavailable", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Coordinator_without_gate_returns_503()
    {
        var harness = StartHarness(NewCoordinator(new EmptyDispatcharrHandler()), gate: null);
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/dispatcharr/dry-run",
            new { playlistPath = "playlist.m3u" }, csrf));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    /// <summary>
    /// Transporte falso: universo vazio no Dispatcharr, sem rede real.
    /// </summary>
    private sealed class EmptyDispatcharrHandler : HttpMessageHandler
    {
        public List<string> WriteTraces { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            var method = req.Method.ToString().ToUpperInvariant();
            if (method != "GET") WriteTraces.Add($"{method} {path}");

            if (method == "GET" && path.EndsWith("/api/core/version/"))
                return Task.FromResult(Json(new { version = "0.30.0" }));
            if (method == "GET" && path.EndsWith("/api/channels/channels/"))
                return Task.FromResult(Json(new { count = 0, results = Array.Empty<object>() }));
            if (method == "GET" && path.EndsWith("/api/channels/streams/"))
                return Task.FromResult(Json(new { count = 0, results = Array.Empty<object>() }));
            if (method == "GET" && path.EndsWith("/api/channels/groups/"))
                return Task.FromResult(Json(new { count = 0, results = Array.Empty<object>() }));
            if (method == "POST" && path.EndsWith("/api/channels/streams/"))
                return Task.FromResult(Json(new { id = 1L, name = "n", url = "x", is_custom = true }));
            if (method == "POST" && path.EndsWith("/api/channels/channels/"))
                return Task.FromResult(Json(new { id = 100L, name = "c", channel_number = 1.0, streams = new long[] { 1 } }));
            if (method == "POST" && path.EndsWith("/api/channels/groups/"))
                return Task.FromResult(Json(new { id = 5L, name = "g" }));
            if (method == "PATCH" || method == "DELETE")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(object payload) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
    }
}
