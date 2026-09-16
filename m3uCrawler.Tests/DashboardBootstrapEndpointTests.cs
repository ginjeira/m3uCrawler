using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Auth;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9C.2 — Integração HTTP real do bootstrap, sessão e gate de
/// autorização. Cobre fresh install, invariantes, CSRF, logout e legacy.
/// </summary>
public class DashboardBootstrapEndpointTests : IAsyncLifetime
{
    private const string ValidPassword = "a-very-strong-password";

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
    private DashboardHarness? _harness;

    public DashboardBootstrapEndpointTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"dash-auth-{Guid.NewGuid():N}");
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
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private DashboardHarness StartHarness(string? webToken = null)
    {
        _harness = DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history,
            _lifecycle, _auth, _bootstrap, webToken);
        return _harness;
    }

    private async Task ReachReadyAsync(DashboardHarness harness)
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

    private static StringContent EmptyJson()
        => new("{}", Encoding.UTF8, "application/json");

    [Fact]
    public async Task Fresh_install_is_bootstrap_only_and_serves_wizard()
    {
        var harness = StartHarness();

        var status = await harness.Client.GetAsync("/api/bootstrap/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using (var doc = JsonDocument.Parse(await status.Content.ReadAsStringAsync()))
        {
            Assert.Equal("NOT_CONFIGURED", doc.RootElement.GetProperty("state").GetString());
            Assert.False(doc.RootElement.GetProperty("hasActiveAdmin").GetBoolean());
            foreach (var check in doc.RootElement.GetProperty("checks").EnumerateArray())
            {
                Assert.True(check.GetProperty("satisfied").GetBoolean(), check.GetProperty("key").GetString());
            }
        }

        // Público mesmo em bootstrap.
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.GetAsync("/api/version")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.GetAsync("/api/configuration/lifecycle")).StatusCode);

        // Operações normais bloqueadas pelo lifecycle.
        Assert.Equal(HttpStatusCode.Forbidden, (await harness.Client.GetAsync("/api/history")).StatusCode);

        // Root encaminha para o wizard; wizard é servido.
        var root = await harness.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Found, root.StatusCode);
        Assert.Equal("/bootstrap", root.Headers.Location?.ToString());

        var page = await harness.Client.GetAsync("/bootstrap");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Configuração inicial", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Bootstrap_creates_admin_reaches_ready_and_closes()
    {
        var harness = StartHarness();

        Assert.Equal(
            HttpStatusCode.OK,
            (await harness.Client.PostAsync("/api/bootstrap/start", EmptyJson())).StatusCode);

        var weak = await harness.Client.PostAsync(
            "/api/bootstrap/admin",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = "short" }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Contains("password-too-short", await weak.Content.ReadAsStringAsync());

        var created = await harness.Client.PostAsync(
            "/api/bootstrap/admin",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        // Retry idempotente: não cria segundo administrador nem substitui.
        var retry = await harness.Client.PostAsync(
            "/api/bootstrap/admin",
            new StringContent(
                JsonSerializer.Serialize(new { username = "intruder", password = ValidPassword }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Contains("AlreadyCreated", await retry.Content.ReadAsStringAsync());

        var complete = await harness.Client.PostAsync("/api/bootstrap/complete", EmptyJson());
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        using (var doc = JsonDocument.Parse(await complete.Content.ReadAsStringAsync()))
        {
            Assert.Equal("READY", doc.RootElement.GetProperty("state").GetString());
            Assert.Equal("Completed", doc.RootElement.GetProperty("outcome").GetString());
        }

        // Endpoints de bootstrap fecham depois de READY.
        var statusAfterReady = await harness.Client.GetAsync("/api/bootstrap/status");
        Assert.Equal(HttpStatusCode.Conflict, statusAfterReady.StatusCode);
        Assert.Contains("bootstrap-closed", await statusAfterReady.Content.ReadAsStringAsync());

        // Bootstrap também fechado (POST).
        var closed = await harness.Client.PostAsync("/api/bootstrap/start", EmptyJson());
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
        Assert.Contains("bootstrap-closed", await closed.Content.ReadAsStringAsync());

        // Já não há admin "intruder".
        await using var context = _factory.CreateDbContext();
        Assert.Equal(1, context.AdminUsers.Count());
        Assert.Equal("admin", context.AdminUsers.Single().Username);
    }

    [Fact]
    public async Task User_auth_requires_session_and_csrf_and_logout_invalidates()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);

        // Sem sessão → 401 nos endpoints normais.
        Assert.Equal(HttpStatusCode.Unauthorized, (await harness.Client.GetAsync("/api/history")).StatusCode);

        var login = await harness.Client.PostAsync(
            "/api/session",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var setCookie = string.Join(";", login.Headers.GetValues("Set-Cookie"));
        Assert.Contains("m3u_session=", setCookie);
        Assert.Contains("HttpOnly", setCookie);
        Assert.Contains("SameSite=Strict", setCookie);
        Assert.DoesNotContain("Secure", setCookie); // HTTP (loopback) → sem Secure

        string csrfToken;
        using (var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync()))
        {
            csrfToken = doc.RootElement.GetProperty("csrfToken").GetString()!;
        }
        Assert.False(string.IsNullOrWhiteSpace(csrfToken));

        // Sessão válida permite endpoints normais.
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.GetAsync("/api/history")).StatusCode);

        // Método mutante sem CSRF → 403.
        var noCsrf = await harness.Client.PostAsync("/api/catalog/scheduled-jobs", EmptyJson());
        Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
        Assert.Contains("csrf-invalid", await noCsrf.Content.ReadAsStringAsync());

        // Com CSRF passa o gate (pode falhar validação do handler, mas não 403).
        var withCsrf = new HttpRequestMessage(HttpMethod.Post, "/api/catalog/scheduled-jobs")
        {
            Content = EmptyJson(),
        };
        withCsrf.Headers.Add("X-CSRF-Token", csrfToken);
        var csrfResponse = await harness.Client.SendAsync(withCsrf);
        Assert.NotEqual(HttpStatusCode.Forbidden, csrfResponse.StatusCode);

        // Logout exige CSRF e invalida a sessão.
        var logout = new HttpRequestMessage(HttpMethod.Delete, "/api/session");
        logout.Headers.Add("X-CSRF-Token", csrfToken);
        var logoutResponse = await harness.Client.SendAsync(logout);
        Assert.Equal(HttpStatusCode.OK, logoutResponse.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await harness.Client.GetAsync("/api/history")).StatusCode);
    }

    [Fact]
    public async Task Login_rejects_wrong_password_generically()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);

        var login = await harness.Client.PostAsync(
            "/api/session",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = "wrong-password-00" }),
                Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        var body = await login.Content.ReadAsStringAsync();
        Assert.Contains("invalid-credentials", body);
        Assert.DoesNotContain("wrong-password-00", body);
    }

    [Fact]
    public async Task Legacy_ready_without_admin_keeps_open_behaviour()
    {
        _lifecycle.SetState(ConfigurationLifecycleState.Ready, "legacy-adoption:sources");
        var harness = StartHarness();

        // Sem admin e sem web-token → comportamento legacy (aberto).
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.GetAsync("/api/history")).StatusCode);

        // Login indisponível: não há administrador.
        var login = await harness.Client.PostAsync(
            "/api/session",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, login.StatusCode);

        await using var context = _factory.CreateDbContext();
        Assert.Empty(context.AdminUsers);
    }

    [Fact]
    public async Task Web_token_remains_required_for_machine_access()
    {
        const string token = "machine-token-value";
        var harness = StartHarness(webToken: token);

        // Sem token → 401 (mesmo em bootstrap).
        Assert.Equal(HttpStatusCode.Unauthorized, (await harness.Client.GetAsync("/api/version")).StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/version");
        request.Headers.Add("Authorization", $"Bearer {token}");
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.SendAsync(request)).StatusCode);
    }

    private sealed class DashboardHarness : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private Task _loop = Task.CompletedTask;

        private DashboardHarness(HttpListener listener, HttpClient client, int port)
        {
            _listener = listener;
            Client = client;
            Port = port;
        }

        public HttpClient Client { get; }

        public int Port { get; }

        public static DashboardHarness Start(
            string outputDir,
            CatalogResolver resolver,
            PlaylistComposerService composer,
            ImportHistoryService history,
            ConfigurationLifecycleService lifecycle,
            AuthService auth,
            BootstrapService bootstrap,
            string? webToken)
        {
            var port = GetFreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            var handler = new HttpClientHandler
            {
                CookieContainer = new CookieContainer(),
                AllowAutoRedirect = false,
                UseCookies = true,
            };
            var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            var harness = new DashboardHarness(listener, client, port);
            harness._loop = Task.Run(async () =>
            {
                while (!harness._cts.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); }
                    catch (Exception) { break; }

                    try
                    {
                        // Handler sequencial: mantém determinismo de cookies/estado.
                        await WebDashboardService.HandleRequestWithAuthOnTestAsync(
                            context, outputDir, resolver, composer, history,
                            lifecycle, auth, bootstrap, webToken);
                    }
                    catch
                    {
                        try { context.Response.StatusCode = 500; context.Response.Close(); }
                        catch { /* closed */ }
                    }
                }
            });

            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* stopped */ }
            try { _listener.Close(); } catch { /* closed */ }
            try { await _loop; } catch { /* best effort */ }
            Client.Dispose();
            _cts.Dispose();
        }

        private static int GetFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
