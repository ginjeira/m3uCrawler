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
/// W10b — Integração HTTP de <c>POST /api/session/password</c> sobre o
/// <c>DashboardHarness</c> real: reautenticação, CSRF, política de password,
/// método errado e revogação de sessões. Partilha a colecção
/// <c>DashboardStaticState</c> porque muta o estado estático do dashboard.
/// </summary>
[Collection("DashboardStaticState")]
public class DashboardPasswordChangeEndpointTests : IAsyncLifetime
{
    private const string ValidPassword = "a-very-strong-password";
    private const string NewPassword = "a-brand-new-strong-password";

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

    public DashboardPasswordChangeEndpointTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"dash-password-{Guid.NewGuid():N}");
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

    private static async Task<string> LoginAsync(
        DashboardBootstrapEndpointTests.DashboardHarness harness,
        string password)
    {
        var login = await harness.Client.PostAsync(
            "/api/session",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var csrf = doc.RootElement.GetProperty("csrfToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(csrf));
        return csrf!;
    }

    private static StringContent EmptyJson()
        => new("{}", Encoding.UTF8, "application/json");

    private static HttpRequestMessage PasswordRequest(
        string currentPassword,
        string newPassword,
        string? csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/session/password")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { currentPassword, newPassword }),
                Encoding.UTF8, "application/json"),
        };
        if (csrf != null)
        {
            request.Headers.Add("X-CSRF-Token", csrf);
        }
        return request;
    }

    [Fact]
    public async Task Password_change_requires_authentication()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);

        var response = await harness.Client.SendAsync(
            PasswordRequest(ValidPassword, NewPassword, csrf: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("authentication-required", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Password_change_without_csrf_is_forbidden()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness, ValidPassword);

        var response = await harness.Client.SendAsync(
            PasswordRequest(ValidPassword, NewPassword, csrf: null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("csrf-invalid", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    public async Task Password_change_rejects_wrong_methods_with_allow_post(string method)
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);

        var request = new HttpRequestMessage(new HttpMethod(method), "/api/session/password");
        var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        var allow = response.Content.Headers.Allow.Count > 0
            ? string.Join(",", response.Content.Headers.Allow)
            : response.Headers.TryGetValues("Allow", out var values)
                ? string.Join(",", values)
                : string.Empty;
        Assert.Equal("POST", allow);
        Assert.Contains("method-not-allowed", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Wrong_current_password_is_rejected_and_leaves_password_unchanged()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness, ValidPassword);

        var response = await harness.Client.SendAsync(
            PasswordRequest("wrong-current-password", NewPassword, csrf));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid-current-password", body);
        Assert.DoesNotContain("wrong-current-password", body);
        Assert.DoesNotContain(NewPassword, body);

        // A password antiga continua a funcionar (nada foi alterado).
        Assert.Equal(HttpStatusCode.OK,
            (await harness.Client.PostAsync(
                "/api/session",
                new StringContent(
                    JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                    Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task Invalid_new_password_is_rejected_with_stable_error()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness, ValidPassword);

        var response = await harness.Client.SendAsync(
            PasswordRequest(ValidPassword, "short", csrf));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid-new-password", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Successful_change_rotates_password_and_revokes_current_session()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness, ValidPassword);

        var response = await harness.Client.SendAsync(
            PasswordRequest(ValidPassword, NewPassword, csrf));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("password-changed", body);
        Assert.Contains("reloginRequired", body);
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("reloginRequired").GetBoolean());

        // Nunca expor password, hash ou material de derivação.
        Assert.DoesNotContain(ValidPassword, body);
        Assert.DoesNotContain(NewPassword, body);
        Assert.DoesNotContain("PasswordHash", body);
        Assert.DoesNotContain("pbkdf2", body);

        // A sessão usada para a alteração foi revogada.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await harness.Client.GetAsync("/api/history")).StatusCode);

        // Nenhuma sessão do utilizador sobrevive na BD.
        await using (var context = _factory.CreateDbContext())
        {
            Assert.Empty(context.AdminSessions);
        }

        // A password antiga já não autentica; a nova autentica.
        var oldLogin = await harness.Client.PostAsync(
            "/api/session",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        var newLogin = await harness.Client.PostAsync(
            "/api/session",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = NewPassword }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
    }

    [Fact]
    public async Task Authenticated_dashboard_exposes_password_change_ui()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness, ValidPassword);

        var page = await harness.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();

        Assert.Contains("Alterar password", html);
        Assert.Contains("accountCurrentPassword", html);
        Assert.Contains("accountNewPassword", html);
        Assert.Contains("accountConfirmPassword", html);
        Assert.Contains("/api/session/password", html);
        Assert.Contains("changePassword", html);
    }
}
