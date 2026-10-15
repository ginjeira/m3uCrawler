using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Auth;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using m3uCrawler.Services.Preview;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Fase 1b — Rotas HTTP de preview (<c>/api/preview/*</c>) e de entries de
/// playlist (<c>/api/playlist-entries</c>). Wire de um
/// <see cref="PreviewSessionManager"/> com fakes (sem ffmpeg/rede) e
/// isolamento de runtime-data. Confirma ainda que estas rotas são
/// exclusivas de sessão: o token de máquina das playlists NÃO as autoriza, e
/// que <c>/api/playlist</c> continua a autorizar por token (regressão).
/// </summary>
[Collection("DashboardStaticState")]
public sealed class PreviewEndpointTests : IAsyncLifetime
{
    private const string ValidPassword = "a-very-strong-password";
    private const string CliToken = "cli-machine-token-value";
    private const string StreamUrl = "http://host.example.com:8080/live/user/pass/1.ts";

    private const string H264AacJson =
        "{\"streams\":[" +
        "{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":1280,\"height\":720}," +
        "{\"codec_type\":\"audio\",\"codec_name\":\"aac\"}" +
        "],\"format\":{\"format_name\":\"mpegts\"}}";

    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _outputDir;
    private readonly string _previewRoot;
    private readonly string _storePath;

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;
    private PlaylistComposerService _composer = null!;
    private ImportHistoryService _history = null!;
    private ConfigurationLifecycleService _lifecycle = null!;
    private AuthService _auth = null!;
    private BootstrapService _bootstrap = null!;
    private FakeProcessRunner _runner = null!;
    private PreviewSessionManager _manager = null!;
    private DashboardBootstrapEndpointTests.DashboardHarness? _harness;

    public PreviewEndpointTests()
    {
        _root = TestTempDb.SuitePath($"preview-endpoint-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _outputDir = Path.Combine(_root, "output");
        _previewRoot = Path.Combine(_root, "preview");
        _storePath = Path.Combine(_root, ConfigurationLifecycleStore.FileName);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_outputDir);

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

        _runner = new FakeProcessRunner
        {
            RunHandler = _ => new ProcessResult(0, H264AacJson, string.Empty, false),
        };
        var options = new PreviewOptions
        {
            MaxConcurrentSessions = 1,
            IdleTimeout = TimeSpan.FromMinutes(5),
            MaxDuration = TimeSpan.FromMinutes(5),
        };
        var probe = new StreamProbeService(_runner, options);
        Func<string, CancellationToken, Task<StreamReference?>> resolve = (reference, _) => reference switch
        {
            "known" => Task.FromResult<StreamReference?>(new StreamReference("review", "1", StreamUrl, "Canal")),
            "known2" => Task.FromResult<StreamReference?>(new StreamReference("review", "2", StreamUrl, "Canal 2")),
            _ => Task.FromResult<StreamReference?>(null),
        };

        _manager = new PreviewSessionManager(options, _runner, probe, resolve, _previewRoot);
        WebDashboardService.SetPreviewSessionManager(_manager);
    }

    public async Task DisposeAsync()
    {
        if (_harness != null)
        {
            await _harness.DisposeAsync();
        }

        WebDashboardService.SetPreviewSessionManager(null);
        await _manager.DisposeAsync();
        WebDashboardService.SetAuth(null, null);
        WebDashboardService.SetConfigurationLifecycle(null);
        TestTempDb.Cleanup(_dbPath);
        TestTempDb.CleanupDirectory(_root);
    }

    private DashboardBootstrapEndpointTests.DashboardHarness StartHarness(string? webToken = null)
    {
        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history, _lifecycle, _auth, _bootstrap,
            webToken: webToken);
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
        var csrf = doc.RootElement.GetProperty("csrfToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(csrf));
        return csrf!;
    }

    private static StringContent EmptyJson() => new("{}", Encoding.UTF8, "application/json");

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

    private async Task<string> StartSessionAsync(
        DashboardBootstrapEndpointTests.DashboardHarness harness, string csrf, string reference)
    {
        var response = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, "/api/preview/start", csrf,
                JsonSerializer.Serialize(new { @ref = reference })));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var sessionId = doc.RootElement.GetProperty("sessionId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(sessionId));
        return sessionId!;
    }

    // ════════════════════════════════════════════════════════════════
    // Gate: sessão-only (o token de máquina não autoriza)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Start_without_session_is_unauthorized()
    {
        var harness = StartHarness(webToken: CliToken);
        await ReachReadyAsync(harness);

        using var anon = AnonymousClient(harness);
        var response = await anon.PostAsync(
            "/api/preview/start",
            new StringContent("{\"ref\":\"known\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Machine_token_does_not_authorize_preview_or_entries()
    {
        var harness = StartHarness(webToken: CliToken);
        await ReachReadyAsync(harness);

        await File.WriteAllTextAsync(
            Path.Combine(_outputDir, "playlist.m3u"), "#EXTM3U\n", Encoding.UTF8);

        using var anon = AnonymousClient(harness);

        var start = WithBearer(HttpMethod.Post, "/api/preview/start", CliToken);
        start.Content = new StringContent("{\"ref\":\"known\"}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(start)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.SendAsync(
                WithBearer(HttpMethod.Get, "/api/playlist-entries?which=playlist", CliToken))).StatusCode);
    }

    [Fact]
    public async Task Playlist_entries_without_session_is_unauthorized()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);

        using var anon = AnonymousClient(harness);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync("/api/playlist-entries?which=playlist")).StatusCode);
    }

    // ════════════════════════════════════════════════════════════════
    // POST /api/preview/start
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Start_with_unknown_reference_returns_404()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, "/api/preview/start", csrf, "{\"ref\":\"missing\"}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("preview-unknown-reference", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Start_with_valid_reference_returns_session()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, "/api/preview/start", csrf, "{\"ref\":\"known\"}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("sessionId").GetString()));
        Assert.Equal("hls", doc.RootElement.GetProperty("kind").GetString());
        Assert.True(doc.RootElement.GetProperty("playable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("reason").ValueKind);
    }

    [Fact]
    public async Task Start_when_capacity_reached_returns_429_busy()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        await StartSessionAsync(harness, csrf, "known");

        var second = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, "/api/preview/start", csrf, "{\"ref\":\"known2\"}"));

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Contains("preview-busy", await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Start_when_start_times_out_returns_502_preview_start_timeout()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        // Manager "fake" (motor real com runner falso): o processo nunca materializa o
        // artefacto de arranque e o ReadyTimeout é curtíssimo, pelo que StartAsync
        // degrada HLS → snapshot e devolve Reason="preview-start-timeout".
        var timeoutRunner = new FakeProcessRunner { AutoCreateArtifacts = false };
        var timeoutOptions = new PreviewOptions
        {
            MaxConcurrentSessions = 1,
            IdleTimeout = TimeSpan.FromMinutes(5),
            MaxDuration = TimeSpan.FromMinutes(5),
            ReadyTimeout = TimeSpan.FromMilliseconds(50),
        };
        var timeoutProbe = new StreamProbeService(timeoutRunner, timeoutOptions);
        Func<string, CancellationToken, Task<StreamReference?>> resolve = (_, _) =>
            Task.FromResult<StreamReference?>(new StreamReference("review", "1", StreamUrl, "Canal"));
        var timeoutManager = new PreviewSessionManager(
            timeoutOptions, timeoutRunner, timeoutProbe, resolve, _previewRoot);

        WebDashboardService.SetPreviewSessionManager(timeoutManager);
        try
        {
            var response = await harness.Client.SendAsync(
                WithCsrf(HttpMethod.Post, "/api/preview/start", csrf, "{\"ref\":\"known\"}"));

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("preview-start-timeout", body);
            // Nem a URL do stream nem credenciais chegam ao cliente.
            Assert.DoesNotContain(StreamUrl, body);
        }
        finally
        {
            WebDashboardService.SetPreviewSessionManager(_manager);
            await timeoutManager.DisposeAsync();
        }
    }

    [Fact]
    public async Task Start_when_stream_unreachable_returns_502_preview_stream_unreachable()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        // O processo nunca materializa o artefacto e o stderr (só visível após o exit)
        // indica falha de rede: o manager classifica o motivo como stream inalcançável.
        var unreachableRunner = new FakeProcessRunner
        {
            AutoCreateArtifacts = false,
            RunHandler = _ => new ProcessResult(1, string.Empty, "ffprobe: Connection timed out", false),
            HandleConfigurator = (_, handle) => handle.StderrTail = "Connection timed out",
        };
        var unreachableOptions = new PreviewOptions
        {
            MaxConcurrentSessions = 1,
            IdleTimeout = TimeSpan.FromMinutes(5),
            MaxDuration = TimeSpan.FromMinutes(5),
            ReadyTimeout = TimeSpan.FromMilliseconds(50),
        };
        var unreachableProbe = new StreamProbeService(unreachableRunner, unreachableOptions);
        Func<string, CancellationToken, Task<StreamReference?>> resolve = (_, _) =>
            Task.FromResult<StreamReference?>(new StreamReference("review", "1", StreamUrl, "Canal"));
        var unreachableManager = new PreviewSessionManager(
            unreachableOptions, unreachableRunner, unreachableProbe, resolve, _previewRoot);

        WebDashboardService.SetPreviewSessionManager(unreachableManager);
        try
        {
            var response = await harness.Client.SendAsync(
                WithCsrf(HttpMethod.Post, "/api/preview/start", csrf, "{\"ref\":\"known\"}"));

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("preview-stream-unreachable", body);
            Assert.DoesNotContain("preview-start-timeout", body);
            // Nem a URL do stream nem credenciais chegam ao cliente.
            Assert.DoesNotContain(StreamUrl, body);
        }
        finally
        {
            WebDashboardService.SetPreviewSessionManager(_manager);
            await unreachableManager.DisposeAsync();
        }
    }

    // ════════════════════════════════════════════════════════════════
    // GET /api/preview/<id>/<file> e POST /api/preview/<id>/stop
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Manifest_is_served_and_path_traversal_is_blocked()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        var sessionId = await StartSessionAsync(harness, csrf, "known");

        var sessionDir = Path.Combine(_previewRoot, sessionId);
        await File.WriteAllTextAsync(Path.Combine(sessionDir, "index.m3u8"), "#EXTM3U\n#EXT-X-VERSION:3\n");
        await File.WriteAllTextAsync(Path.Combine(_previewRoot, "secret.txt"), "top-secret");

        var manifest = await harness.Client.GetAsync($"/api/preview/{sessionId}/index.m3u8");
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        Assert.Equal("application/vnd.apple.mpegurl", manifest.Content.Headers.ContentType?.MediaType);
        Assert.Contains("#EXTM3U", await manifest.Content.ReadAsStringAsync());
        Assert.Equal("no-store", manifest.Headers.CacheControl?.ToString());

        Assert.Equal(HttpStatusCode.NotFound,
            (await harness.Client.GetAsync($"/api/preview/{sessionId}/../secret.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await harness.Client.GetAsync($"/api/preview/{sessionId}/..%2Fsecret.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await harness.Client.GetAsync($"/api/preview/{sessionId}/does-not-exist.ts")).StatusCode);
    }

    [Fact]
    public async Task Stop_returns_204()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        var sessionId = await StartSessionAsync(harness, csrf, "known");

        var stop = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, $"/api/preview/{sessionId}/stop", csrf, "{}"));

        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
    }

    // ════════════════════════════════════════════════════════════════
    // GET /api/playlist-entries
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Playlist_entries_returns_metadata_without_url()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        await File.WriteAllTextAsync(
            Path.Combine(_outputDir, "playlist.m3u"),
            "#EXTM3U\n" +
            "#EXTINF:-1 tvg-logo=\"http://logo.example/l.png\" group-title=\"News\",Canal Um\n" +
            StreamUrl + "\n",
            Encoding.UTF8);

        var response = await harness.Client.GetAsync("/api/playlist-entries?which=playlist");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var row = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal(StreamReferenceResolver.PlaylistEntryId(StreamUrl), row.GetProperty("id").GetString());
        Assert.Equal("Canal Um", row.GetProperty("title").GetString());
        Assert.Equal("News", row.GetProperty("group").GetString());
        Assert.Equal("http://logo.example/l.png", row.GetProperty("logo").GetString());
        Assert.False(row.TryGetProperty("url", out _));
        Assert.DoesNotContain(StreamUrl, body);
    }

    [Fact]
    public async Task Playlist_entries_with_invalid_which_returns_400()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        var response = await harness.Client.GetAsync("/api/playlist-entries?which=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid-which", await response.Content.ReadAsStringAsync());
    }

    // ════════════════════════════════════════════════════════════════
    // GET /api/playlist-entry (download de 1 entrada)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Playlist_entry_without_session_is_unauthorized()
    {
        var harness = StartHarness(webToken: CliToken);
        await ReachReadyAsync(harness);

        await File.WriteAllTextAsync(
            Path.Combine(_outputDir, "playlist.m3u"), "#EXTM3U\n", Encoding.UTF8);

        using var anon = AnonymousClient(harness);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync("/api/playlist-entry?which=playlist&id=deadbeef")).StatusCode);

        // O token de máquina das playlists não autoriza esta rota de sessão.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.SendAsync(
                WithBearer(HttpMethod.Get, "/api/playlist-entry?which=playlist&id=deadbeef", CliToken))).StatusCode);
    }

    [Fact]
    public async Task Playlist_entry_returns_single_entry_m3u()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        await File.WriteAllTextAsync(
            Path.Combine(_outputDir, "playlist.m3u"),
            "#EXTM3U\n" +
            "#EXTINF:-1 tvg-logo=\"http://logo.example/l.png\" group-title=\"News\",Canal Um\n" +
            StreamUrl + "\n",
            Encoding.UTF8);

        var id = StreamReferenceResolver.PlaylistEntryId(StreamUrl);
        var response = await harness.Client.GetAsync(
            $"/api/playlist-entry?which=playlist&id={id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/x-mpegurl", response.Content.Headers.ContentType?.MediaType);
        var disposition = response.Content.Headers.ContentDisposition?.ToString() ?? string.Empty;
        Assert.Contains("attachment", disposition);
        Assert.Contains($"{id}.m3u", disposition);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("#EXTM3U", body);
        Assert.Contains("#EXTINF:-1,Canal Um", body);
        Assert.Contains(StreamUrl, body);
    }

    [Fact]
    public async Task Playlist_entry_with_unknown_id_returns_404()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        await File.WriteAllTextAsync(
            Path.Combine(_outputDir, "playlist.m3u"),
            "#EXTM3U\n" + StreamUrl + "\n",
            Encoding.UTF8);

        var response = await harness.Client.GetAsync(
            "/api/playlist-entry?which=playlist&id=0000000000000000");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("entry-not-found", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Playlist_entry_with_invalid_which_returns_400()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        var response = await harness.Client.GetAsync(
            "/api/playlist-entry?which=bogus&id=deadbeef");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid-which", await response.Content.ReadAsStringAsync());
    }

    // ════════════════════════════════════════════════════════════════
    // GET /assets/hls.min.js (hls.js vendorizado)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Hls_asset_without_session_is_unauthorized()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);

        using var anon = AnonymousClient(harness);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.GetAsync("/assets/hls.min.js")).StatusCode);
    }

    [Fact]
    public async Task Hls_asset_is_served_from_embedded_resource()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        var response = await harness.Client.GetAsync("/assets/hls.min.js");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("max-age", response.Headers.CacheControl?.ToString() ?? string.Empty);

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(body.Length > 150 * 1024, $"Bundle inesperadamente pequeno: {body.Length} bytes.");
        Assert.Contains("Hls", body);
        Assert.Contains("Apache License", body);
        Assert.DoesNotContain("<!doctype html", body, StringComparison.OrdinalIgnoreCase);
    }

    // ════════════════════════════════════════════════════════════════
    // Regressão: /api/playlist continua autorizável por token de máquina
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Machine_playlist_still_authorized_by_token()
    {
        var harness = StartHarness(webToken: CliToken);
        await ReachReadyAsync(harness);

        await File.WriteAllTextAsync(
            Path.Combine(_outputDir, "playlist.m3u"), "#EXTM3U\n", Encoding.UTF8);

        using var anon = AnonymousClient(harness);
        var response = await anon.SendAsync(WithBearer(HttpMethod.Get, "/api/playlist", CliToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ════════════════════════════════════════════════════════════════
    // CSP pragmática (apenas respostas HTML)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Html_response_carries_pragmatic_csp_header()
    {
        var harness = StartHarness();

        var html = await harness.Client.GetAsync("/bootstrap");

        Assert.Equal(HttpStatusCode.OK, html.StatusCode);
        Assert.Equal("text/html", html.Content.Headers.ContentType?.MediaType);
        Assert.True(
            html.Headers.TryGetValues("Content-Security-Policy", out var values),
            "A resposta HTML deve incluir o cabeçalho Content-Security-Policy.");
        var csp = string.Join("; ", values!);
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("frame-ancestors 'self'", csp);
        Assert.Contains("script-src 'self' 'unsafe-inline'", csp);
        Assert.Contains("img-src 'self' data: http: https:", csp);
        Assert.Contains("media-src 'self' blob:", csp);
        Assert.Contains("object-src 'none'", csp);
    }

    [Fact]
    public async Task Api_response_does_not_carry_csp_header()
    {
        var harness = StartHarness();

        var api = await harness.Client.GetAsync("/api/version");

        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
        Assert.False(api.Headers.Contains("Content-Security-Policy"));
    }
}
