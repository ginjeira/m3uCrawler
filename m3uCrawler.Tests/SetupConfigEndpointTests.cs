using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Auth;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using m3uCrawler.Services.Dispatcharr;
using m3uCrawler.Services.Telegram;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Wave 5 (PHASE 9C) — Integração HTTP dos endpoints de setup
/// (Telegram config/login, Dispatcharr config/teste e prontidão
/// operacional) sobre o <c>DashboardHarness</c> real. Cobre o gate
/// (sessão, CSRF, bootstrap), a máscara de segredos e o método errado.
/// Partilha a colecção <c>DashboardStaticState</c> porque muta o estado
/// estático do dashboard.
/// </summary>
[Collection("DashboardStaticState")]
public class SetupConfigEndpointTests : IAsyncLifetime
{
    private const string ValidPassword = "a-very-strong-password";
    private const string ApiHash = "SECRET-HASH-VALUE";

    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _outputDir;
    private readonly string _storePath;
    private readonly string _wtelegramPath;

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;
    private PlaylistComposerService _composer = null!;
    private ImportHistoryService _history = null!;
    private ConfigurationLifecycleService _lifecycle = null!;
    private AuthService _auth = null!;
    private BootstrapService _bootstrap = null!;
    private WtelegramConfigStore _wtelegramStore = null!;
    private TelegramAuthService _telegramAuth = null!;
    private DispatcharrConfigurationService _dispatcharrConfig = null!;
    private DashboardBootstrapEndpointTests.DashboardHarness? _harness;

    public SetupConfigEndpointTests()
    {
        _root = TestTempDb.SuitePath($"setup-endpoints-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _outputDir = Path.Combine(_root, "output");
        _storePath = Path.Combine(_root, ConfigurationLifecycleStore.FileName);
        _wtelegramPath = Path.Combine(_root, "wtelegram.config");
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

        _wtelegramStore = new WtelegramConfigStore(_wtelegramPath);
        _telegramAuth = new TelegramAuthService(
            _wtelegramStore,
            _ => new FakeTelegramBackend(),
            sessionFileExists: _ => false);
        _dispatcharrConfig = new DispatcharrConfigurationService(_wtelegramStore);
    }

    public async Task DisposeAsync()
    {
        if (_harness != null)
        {
            await _harness.DisposeAsync();
        }
        WebDashboardService.SetAuth(null, null);
        WebDashboardService.SetConfigurationLifecycle(null);
        WebDashboardService.SetSetupServices(null, null, null, null);
        TestTempDb.Cleanup(_dbPath);
        TestTempDb.Cleanup(_storePath);
        TestTempDb.CleanupDirectory(_root);
    }

    private DashboardBootstrapEndpointTests.DashboardHarness StartHarness(
        DispatcharrConnectionTester? dispatcharrTester = null,
        OperationalReadinessService? readiness = null)
    {
        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history,
            _lifecycle, _auth, _bootstrap, webToken: null,
            telegramAuth: _telegramAuth,
            dispatcharrConfig: _dispatcharrConfig,
            dispatcharrTester: dispatcharrTester,
            readiness: readiness);
        return _harness;
    }

    /// <summary>Arranca sem os serviços de setup ligados (503 esperado).</summary>
    private DashboardBootstrapEndpointTests.DashboardHarness StartHarnessUnwired()
    {
        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history,
            _lifecycle, _auth, _bootstrap, webToken: null);
        return _harness;
    }

    private OperationalReadinessService BuildReadiness(
        bool hasAdmin = true,
        bool telegramAuthenticated = true,
        bool catalogOk = true,
        bool countryDataOk = true,
        bool outputOk = true,
        int sources = 1,
        DispatcharrConfig? dispatcharr = null)
        => new(
            _lifecycle,
            _ => Task.FromResult(hasAdmin),
            () => telegramAuthenticated,
            () => dispatcharr ?? DispatcharrConfig.Disabled(),
            _ => Task.FromResult(catalogOk),
            _ => Task.FromResult(countryDataOk),
            () => outputOk,
            _ => Task.FromResult(sources));

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

    private static Task<HttpResponseMessage> PostJsonAsync(
        DashboardBootstrapEndpointTests.DashboardHarness harness,
        string path,
        string body,
        string? csrf = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (csrf != null)
        {
            request.Headers.Add("X-CSRF-Token", csrf);
        }
        return harness.Client.SendAsync(request);
    }

    [Fact]
    public async Task Setup_endpoints_require_authentication()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);

        var response = await harness.Client.GetAsync("/api/telegram/config");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Setup_endpoints_in_bootstrap_mode_are_forbidden()
    {
        var harness = StartHarness();

        var response = await harness.Client.GetAsync("/api/telegram/config");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("bootstrap-required", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Mutating_setup_endpoint_without_csrf_is_forbidden()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        var response = await PostJsonAsync(
            harness,
            "/api/dispatcharr/config",
            JsonSerializer.Serialize(new
            {
                enabled = true,
                baseUrl = "http://dispatcharr.example.test",
                dryRun = true,
            }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("csrf-invalid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_telegram_config_is_masked_and_never_exposes_api_hash()
    {
        _wtelegramStore.Upsert(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["api_id"] = "12345",
            ["api_hash"] = ApiHash,
            ["phone_number"] = "+351911111111",
            ["session_pathname"] = "custom.session",
        });

        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        var response = await harness.Client.GetAsync("/api/telegram/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ApiHash, body);
        Assert.DoesNotContain("\"apiHash\"", body);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("12345", root.GetProperty("apiId").GetString());
        Assert.Equal("+351911111111", root.GetProperty("phoneNumber").GetString());
        Assert.True(root.GetProperty("hasApiHash").GetBoolean());
        Assert.Equal("custom.session", root.GetProperty("sessionPath").GetString());
    }

    [Fact]
    public async Task Telegram_auth_code_drives_state_machine_and_never_echoes_code()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var start = await PostJsonAsync(
            harness,
            "/api/telegram/auth/start",
            JsonSerializer.Serialize(new
            {
                apiId = "12345",
                apiHash = ApiHash,
                phoneNumber = "+351900000000",
            }),
            csrf);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        using (var doc = JsonDocument.Parse(await start.Content.ReadAsStringAsync()))
        {
            Assert.Equal("WaitingCode", doc.RootElement.GetProperty("state").GetString());
        }

        const string code = "654321";
        var codeResponse = await PostJsonAsync(
            harness, "/api/telegram/auth/code",
            JsonSerializer.Serialize(new { code }), csrf);

        Assert.Equal(HttpStatusCode.OK, codeResponse.StatusCode);
        var body = await codeResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(code, body);
        using (var doc = JsonDocument.Parse(body))
        {
            Assert.Equal("Authenticated", doc.RootElement.GetProperty("state").GetString());
            Assert.Equal("fake-user", doc.RootElement.GetProperty("userName").GetString());
        }
    }

    [Fact]
    public async Task Dispatcharr_config_post_persists_and_never_returns_key()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        const string apiKey = "SECRET-DISPATCHARR-KEY";
        var post = await PostJsonAsync(
            harness,
            "/api/dispatcharr/config",
            JsonSerializer.Serialize(new
            {
                enabled = true,
                baseUrl = "http://dispatcharr.example.test",
                dryRun = true,
                apiKey,
                username = "operator",
                password = "super-secret-password",
            }),
            csrf);

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var postBody = await post.Content.ReadAsStringAsync();
        Assert.DoesNotContain(apiKey, postBody);
        Assert.DoesNotContain("super-secret-password", postBody);
        using (var doc = JsonDocument.Parse(postBody))
        {
            Assert.True(doc.RootElement.GetProperty("hasApiKey").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("hasUsername").GetBoolean());
        }

        var get = await harness.Client.GetAsync("/api/dispatcharr/config");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var getBody = await get.Content.ReadAsStringAsync();
        Assert.DoesNotContain(apiKey, getBody);
        using (var doc = JsonDocument.Parse(getBody))
        {
            Assert.True(doc.RootElement.GetProperty("enabled").GetBoolean());
            Assert.Equal("http://dispatcharr.example.test", doc.RootElement.GetProperty("baseUrl").GetString());
            Assert.True(doc.RootElement.GetProperty("dryRun").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("hasApiKey").GetBoolean());
        }
    }

    [Fact]
    public async Task Dispatcharr_test_uses_get_only_and_maps_status()
    {
        var methods = new List<HttpMethod>();
        var tester = new DispatcharrConnectionTester(
            () => new RecordingHandler(
                methods,
                _ => Json(HttpStatusCode.OK, "{\"version\":\"9.9.9\"}")));

        _dispatcharrConfig.Save(new DispatcharrConfigurationWrite(
            Enabled: true,
            BaseUrl: "http://dispatcharr.example.test",
            DryRun: true,
            ApiKey: "SECRET-API-KEY",
            Username: null,
            Password: null));

        var harness = StartHarness(dispatcharrTester: tester);
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await PostJsonAsync(harness, "/api/dispatcharr/test", "{}", csrf);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET-API-KEY", body);
        using (var doc = JsonDocument.Parse(body))
        {
            Assert.Equal("Connected", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal("9.9.9", doc.RootElement.GetProperty("version").GetString());
            Assert.Equal(200, doc.RootElement.GetProperty("httpStatusCode").GetInt32());
        }

        Assert.NotEmpty(methods);
        Assert.All(methods, m => Assert.Equal(HttpMethod.Get, m));
        Assert.DoesNotContain(HttpMethod.Post, methods);
        Assert.DoesNotContain(HttpMethod.Patch, methods);
        Assert.DoesNotContain(HttpMethod.Delete, methods);
    }

    [Fact]
    public async Task Readiness_endpoint_reports_admin_only_as_not_setup_complete()
    {
        var readiness = BuildReadiness(
            hasAdmin: true,
            telegramAuthenticated: false,
            catalogOk: false,
            outputOk: false,
            sources: 0);

        var harness = StartHarness(readiness: readiness);
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        var response = await harness.Client.GetAsync("/api/configuration/readiness");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.GetProperty("hasAdmin").GetBoolean());
        Assert.False(root.GetProperty("setupComplete").GetBoolean());
        Assert.False(root.GetProperty("operationalReady").GetBoolean());
        Assert.Equal(0, root.GetProperty("sourcesCount").GetInt32());
        Assert.True(root.GetProperty("countryDataOk").GetBoolean());
        Assert.Equal(8, root.GetProperty("items").GetArrayLength());

        var missing = root.GetProperty("missingRequired").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Contains("telegram", missing);
        Assert.Contains("catalog", missing);
        Assert.Contains("output", missing);
    }

    [Fact]
    public async Task Readiness_endpoint_reports_all_satisfied_as_ready()
    {
        var harness = StartHarness(readiness: BuildReadiness());
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        var response = await harness.Client.GetAsync("/api/configuration/readiness");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.GetProperty("bootstrapReady").GetBoolean());
        Assert.True(root.GetProperty("setupComplete").GetBoolean());
        Assert.True(root.GetProperty("operationalReady").GetBoolean());
        Assert.Equal(1, root.GetProperty("sourcesCount").GetInt32());
        Assert.Equal(0, root.GetProperty("missingRequired").GetArrayLength());
        Assert.Equal(8, root.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Wrong_method_on_setup_paths_returns_405()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        // GET sobre um path POST-only.
        var get = await harness.Client.GetAsync("/api/dispatcharr/test");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);

        // POST sobre um path GET-only.
        var post = await PostJsonAsync(harness, "/api/telegram/auth/status", "{}", csrf);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }

    [Fact]
    public async Task Setup_services_absent_return_503()
    {
        var harness = StartHarnessUnwired();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        var response = await harness.Client.GetAsync("/api/telegram/config");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("telegram-unavailable", await response.Content.ReadAsStringAsync());
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<HttpMethod> _methods;
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RecordingHandler(
            List<HttpMethod> methods,
            Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _methods = methods;
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _methods.Add(request.Method);
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class FakeTelegramBackend : ITelegramAuthBackend
    {
        public bool IsAuthenticated { get; private set; }

        public string? UserName { get; set; } = "fake-user";

        public Task<string?> BeginLoginAsync(TelegramBackendOptions options)
            => Task.FromResult<string?>("verification_code");

        public Task<string?> SubmitAsync(string value)
        {
            IsAuthenticated = true;
            return Task.FromResult<string?>(null);
        }

        public void Dispose()
        {
        }
    }
}
