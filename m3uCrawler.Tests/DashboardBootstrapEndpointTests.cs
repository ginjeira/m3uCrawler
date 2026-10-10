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
using m3uCrawler.Services.Audit;
using m3uCrawler.Services.Auth;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using m3uCrawler.Services.Dispatcharr;
using m3uCrawler.Services.Sync;
using m3uCrawler.Services.Telegram;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9C.2 — Integração HTTP real do bootstrap, sessão e gate de
/// autorização. Cobre fresh install, invariantes, CSRF, logout e legacy.
/// </summary>
[Collection("DashboardStaticState")]
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

    /// <summary>
    /// PHASE 9C.2 (S1) — Harness com lifecycle mas SEM auth/bootstrap, para
    /// simular falha de wiring do serviço de autenticação.
    /// </summary>
    private DashboardHarness StartHarnessWithoutAuth()
    {
        _harness = DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history,
            _lifecycle, auth: null, bootstrap: null, webToken: null);
        return _harness;
    }

    /// <summary>
    /// PHASE 9C.2 (S1-E) — Harness sem lifecycle e sem auth em contexto de
    /// produção (não-standalone), equivalente a falha de inicialização do
    /// catálogo no arranque.
    /// </summary>
    private DashboardHarness StartHarnessUnwired()
    {
        _harness = DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history,
            lifecycle: null, auth: null, bootstrap: null, webToken: null);
        return _harness;
    }

    /// <summary>
    /// PHASE 9C.2 (S1-E) — Harness em contexto explicitamente standalone/testes,
    /// onde a ausência de lifecycle/auth mantém o comportamento legacy.
    /// </summary>
    private DashboardHarness StartHarnessStandalone()
    {
        _harness = DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history,
            lifecycle: null, auth: null, bootstrap: null, webToken: null,
            standalone: true);
        return _harness;
    }

    private static HttpRequestMessage WithBearer(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Authorization", $"Bearer {token}");
        return request;
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

    /// <summary>
    /// PHASE 9C.5 — Instalação legacy adoptada READY sem administrador
    /// (<c>BOOTSTRAP_REQUIRED</c>): o Dashboard deixa de estar aberto, passa a
    /// servir o wizard e permite criar o primeiro administrador sem
    /// reconfigurar nem alterar o estado; a partir daí o modo é UserAuth.
    /// </summary>
    [Fact]
    public async Task Legacy_ready_without_admin_serves_wizard_and_creates_first_admin()
    {
        new ConfigurationLifecycleStore(_storePath).Save(new ConfigurationLifecycleSnapshot(
            ConfigurationLifecycleState.Ready,
            AdoptedFromLegacy: true,
            AdoptedAtUtc: DateTime.UtcNow,
            LastReason: "legacy-adoption:sources",
            UpdatedAtUtc: DateTime.UtcNow));

        var harness = StartHarness();

        // READY sem admin já não é modo Legacy aberto: os endpoints normais
        // ficam bloqueados pelo gate de bootstrap (403).
        Assert.Equal(HttpStatusCode.Forbidden, (await harness.Client.GetAsync("/api/history")).StatusCode);

        // Root encaminha para o wizard; wizard é servido.
        var root = await harness.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Found, root.StatusCode);
        Assert.Equal("/bootstrap", root.Headers.Location?.ToString());

        var page = await harness.Client.GetAsync("/bootstrap");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Configuração inicial", await page.Content.ReadAsStringAsync());

        // Status reporta READY (configuração preservada) e sem administrador.
        var status = await harness.Client.GetAsync("/api/bootstrap/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using (var doc = JsonDocument.Parse(await status.Content.ReadAsStringAsync()))
        {
            Assert.Equal("READY", doc.RootElement.GetProperty("state").GetString());
            Assert.False(doc.RootElement.GetProperty("hasActiveAdmin").GetBoolean());
        }

        // Cria o primeiro administrador sem passar por /start e sem alterar
        // o estado persistido.
        var created = await harness.Client.PostAsync(
            "/api/bootstrap/admin",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Contains("Created", await created.Content.ReadAsStringAsync());

        // Transição para UserAuth: sem sessão, os endpoints normais exigem
        // autenticação (401) e o bootstrap fecha.
        Assert.Equal(HttpStatusCode.Unauthorized, (await harness.Client.GetAsync("/api/history")).StatusCode);
        var closed = await harness.Client.GetAsync("/api/bootstrap/status");
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
        Assert.Contains("bootstrap-closed", await closed.Content.ReadAsStringAsync());

        // Login humano passa a estar disponível.
        var login = await harness.Client.PostAsync(
            "/api/session",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // O estado persistido permanece READY — nada foi recriado.
        Assert.Equal(ConfigurationLifecycleState.Ready, (await _lifecycle.GetStateAsync()).State);

        await using var context = _factory.CreateDbContext();
        Assert.Equal(1, context.AdminUsers.Count());
        Assert.Equal("admin", context.AdminUsers.Single().Username);
    }

    /// <summary>
    /// PHASE 9C.5 (revisão / decisão de scope) — READY com administrador
    /// existente mas DESACTIVADO. Estado administrativo <b>não suportado</b>:
    /// o modo é <c>Bootstrap</c> (não há admin activo) mas a criação está
    /// fechada (existe registo em <c>admin_users</c>), o login humano está
    /// indisponível e os endpoints normais ficam em 403. Não há recuperação
    /// in-app (decisão de projecto); uma instalação inconsistente pode ser
    /// reinicializada de raiz. Teste de caracterização.
    /// </summary>
    [Fact]
    public async Task Ready_with_disabled_admin_is_bootstrap_locked_out()
    {
        await SeedReadyWithDisabledAdminAsync();

        var harness = StartHarness();

        var root = await harness.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Found, root.StatusCode);
        Assert.Equal("/bootstrap", root.Headers.Location?.ToString());

        var status = await harness.Client.GetAsync("/api/bootstrap/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using (var doc = JsonDocument.Parse(await status.Content.ReadAsStringAsync()))
        {
            Assert.Equal("READY", doc.RootElement.GetProperty("state").GetString());
            Assert.False(doc.RootElement.GetProperty("hasActiveAdmin").GetBoolean());
        }

        var created = await harness.Client.PostAsync(
            "/api/bootstrap/admin",
            new StringContent(
                JsonSerializer.Serialize(new { username = "replacement", password = ValidPassword }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, created.StatusCode);
        Assert.Contains("AlreadyReady", await created.Content.ReadAsStringAsync());

        var login = await harness.Client.PostAsync(
            "/api/session",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, login.StatusCode);
        Assert.Contains("login-unavailable", await login.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Forbidden, (await harness.Client.GetAsync("/api/history")).StatusCode);

        await using var context = _factory.CreateDbContext();
        Assert.Equal(1, context.AdminUsers.Count());
        Assert.Equal("admin", context.AdminUsers.Single().Username);
        Assert.False(context.AdminUsers.Single().IsEnabled);
    }

    /// <summary>
    /// PHASE 9C.5 (revisão / decisão de scope) — READY + admin desactivado com
    /// <c>--web-token</c>: a credencial de máquina autoriza <b>apenas</b> as
    /// playlists de máquina (não as restantes APIs). O lockout humano continua a
    /// ser um estado não suportado — não existe — nem é pretendido — endpoint que
    /// reactive o administrador ou crie um substituto.
    /// </summary>
    [Fact]
    public async Task Ready_with_disabled_admin_and_web_token_grants_only_playlist_access()
    {
        await SeedReadyWithDisabledAdminAsync();
        await SeedPlaylistsAsync();

        const string token = "machine-token-value";
        var harness = StartHarness(webToken: token);

        // Playlist de máquina autorizada por token, mesmo em BOOTSTRAP_REQUIRED.
        var playlist = await harness.Client.SendAsync(WithBearer(HttpMethod.Get, "/api/playlist", token));
        Assert.Equal(HttpStatusCode.OK, playlist.StatusCode);

        // As restantes APIs continuam bloqueadas pelo gate de bootstrap (o token
        // já não autoriza endpoints fora das playlists).
        var history = await harness.Client.SendAsync(WithBearer(HttpMethod.Get, "/api/history", token));
        Assert.Equal(HttpStatusCode.Forbidden, history.StatusCode);

        var created = await harness.Client.SendAsync(WithBearerJson(
            HttpMethod.Post, "/api/bootstrap/admin", token,
            JsonSerializer.Serialize(new { username = "replacement", password = ValidPassword })));
        Assert.Equal(HttpStatusCode.Conflict, created.StatusCode);

        var login = await harness.Client.SendAsync(WithBearerJson(
            HttpMethod.Post, "/api/session", token,
            JsonSerializer.Serialize(new { username = "admin", password = ValidPassword })));
        Assert.Equal(HttpStatusCode.Conflict, login.StatusCode);

        await using var context = _factory.CreateDbContext();
        Assert.Equal(1, context.AdminUsers.Count());
        Assert.False(context.AdminUsers.Single().IsEnabled);
    }

    // === F6 — a resposta de bootstrap-required não mente sobre o estado ===

    [Fact]
    public async Task Bootstrap_required_reports_not_configured_for_fresh_install()
    {
        var harness = StartHarness();

        var response = await harness.Client.GetAsync("/api/history");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("bootstrap-required", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal("NOT_CONFIGURED", doc.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Bootstrap_required_reports_ready_when_ready_without_admin()
    {
        new ConfigurationLifecycleStore(_storePath).Save(new ConfigurationLifecycleSnapshot(
            ConfigurationLifecycleState.Ready,
            AdoptedFromLegacy: true,
            AdoptedAtUtc: DateTime.UtcNow,
            LastReason: "legacy-adoption:sources",
            UpdatedAtUtc: DateTime.UtcNow));

        var harness = StartHarness();

        var response = await harness.Client.GetAsync("/api/history");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("bootstrap-required", doc.RootElement.GetProperty("error").GetString());
        // O estado real é READY (BOOTSTRAP_REQUIRED), não NOT_CONFIGURED.
        Assert.Equal("READY", doc.RootElement.GetProperty("state").GetString());
    }

    private async Task SeedReadyWithDisabledAdminAsync()
    {
        new ConfigurationLifecycleStore(_storePath).Save(new ConfigurationLifecycleSnapshot(
            ConfigurationLifecycleState.Ready,
            AdoptedFromLegacy: true,
            AdoptedAtUtc: DateTime.UtcNow,
            LastReason: "legacy-adoption:sources",
            UpdatedAtUtc: DateTime.UtcNow));

        var users = new AdminUserStore(_factory);
        Assert.Equal(CreateAdminResult.Created, await users.CreateFirstAdminAsync("admin", ValidPassword));

        await using var context = _factory.CreateDbContext();
        var user = context.AdminUsers.Single();
        user.IsEnabled = false;
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Web_token_is_ignored_for_public_and_non_playlist_paths()
    {
        const string token = "machine-token-value";
        var harness = StartHarness(webToken: token);

        // Endpoint público: acessível sem token (já não existe 401 global do token).
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.GetAsync("/api/version")).StatusCode);

        // Endpoints de bootstrap: acessíveis sem token (o token já não os protege).
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.GetAsync("/api/bootstrap/status")).StatusCode);
    }

    private static HttpRequestMessage WithBearerJson(HttpMethod method, string path, string token, string json)
    {
        var request = WithBearer(method, path, token);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private async Task SeedPlaylistsAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(_outputDir, "playlist.m3u"), "#EXTM3U\n");
        await File.WriteAllTextAsync(Path.Combine(_outputDir, "playlist_temp.m3u"), "#EXTM3U\n");
        await File.WriteAllTextAsync(Path.Combine(_outputDir, "playlist_acquired.m3u"), "#EXTM3U\n");
    }

    // === B1 / T2 — credencial de máquina em READY + admin ===

    [Fact]
    public async Task Machine_token_authorizes_only_playlist_endpoints()
    {
        const string token = "machine-token-value";
        var harness = StartHarness(webToken: token);
        await ReachReadyAsync(harness);
        await SeedPlaylistsAsync();

        // Token válido + sem sessão: as três playlists de máquina são servidas.
        Assert.Equal(HttpStatusCode.OK,
            (await harness.Client.SendAsync(WithBearer(HttpMethod.Get, "/api/playlist", token))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await harness.Client.SendAsync(WithBearer(HttpMethod.Get, "/api/playlist_temp", token))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await harness.Client.SendAsync(WithBearer(HttpMethod.Get, "/api/playlist_acquired", token))).StatusCode);

        // Query ?token= é equivalente ao Bearer.
        Assert.Equal(HttpStatusCode.OK,
            (await harness.Client.GetAsync($"/api/playlist?token={token}")).StatusCode);

        // Token inválido → não autoriza.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await harness.Client.SendAsync(WithBearer(HttpMethod.Get, "/api/playlist", "wrong-token"))).StatusCode);

        // Sem token e sem sessão → 401 na playlist.
        Assert.Equal(HttpStatusCode.Unauthorized, (await harness.Client.GetAsync("/api/playlist")).StatusCode);

        // O token NÃO autoriza endpoints fora das playlists: cai no check de
        // sessão humana → 401.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await harness.Client.SendAsync(WithBearer(HttpMethod.Get, "/api/history", token))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await harness.Client.SendAsync(WithBearer(HttpMethod.Get, "/api/countries", token))).StatusCode);

        // Os previews ficam no fluxo de sessão: o token não os autoriza.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await harness.Client.SendAsync(WithBearer(HttpMethod.Get, "/api/playlist/preview", token))).StatusCode);

        // A credencial de máquina não cria utilizador nem sessão humana.
        await using var context = _factory.CreateDbContext();
        Assert.Equal(1, context.AdminUsers.Count());
        Assert.Empty(context.AdminSessions);
    }

    [Fact]
    public async Task User_session_authorizes_playlists_and_dashboard_without_token()
    {
        const string token = "machine-token-value";
        var harness = StartHarness(webToken: token);
        await ReachReadyAsync(harness);
        await SeedPlaylistsAsync();

        // Login humano já não exige o token.
        var login = await harness.Client.PostAsync(
            "/api/session",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        // Sessão (cookie), sem token: as playlists e o dashboard são acessíveis.
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.GetAsync("/api/playlist")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.GetAsync("/api/history")).StatusCode);
    }

    [Fact]
    public async Task Root_without_token_is_not_gated_by_web_token()
    {
        const string token = "machine-token-value";
        var harness = StartHarness(webToken: token);
        await ReachReadyAsync(harness);

        // Com token configurado, GET / sem sessão segue o fluxo normal (login
        // HTML), nunca o 401 global do token.
        var root = await harness.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Contains("Entrar", await root.Content.ReadAsStringAsync());
    }

    // === T3 — legacy + credencial de máquina ===

    [Fact]
    public async Task Ready_without_admin_with_web_token_denies_non_playlist_access()
    {
        const string token = "machine-token-value";
        _lifecycle.SetState(ConfigurationLifecycleState.Ready, "legacy-adoption:sources");
        await SeedPlaylistsAsync();
        var harness = StartHarness(webToken: token);

        // Playlist de máquina autorizada por token.
        Assert.Equal(HttpStatusCode.OK,
            (await harness.Client.SendAsync(WithBearer(HttpMethod.Get, "/api/playlist", token))).StatusCode);

        // As restantes APIs ficam bloqueadas pelo gate de bootstrap, mesmo com token.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await harness.Client.SendAsync(WithBearer(HttpMethod.Get, "/api/history", token))).StatusCode);

        await using var context = _factory.CreateDbContext();
        Assert.Empty(context.AdminUsers);
    }

    // === B2 / T1 — CSRF real no Dashboard autenticado ===

    [Fact]
    public async Task Authenticated_dashboard_receives_csrf_and_real_mutation_succeeds()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);

        var login = await harness.Client.PostAsync(
            "/api/session",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        string csrf;
        using (var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync()))
        {
            csrf = doc.RootElement.GetProperty("csrfToken").GetString()!;
        }

        // A página autenticada recebe o token (em memória) e o helper de fetch.
        var page = await harness.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains(csrf, html);
        Assert.Contains("X-CSRF-Token", html);

        // Operação mutável REAL (cria um job agendado) com o header que o helper envia.
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/catalog/scheduled-jobs")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    name = "csrf-test",
                    cronExpression = "*/5 * * * *",
                    actionName = "discoverM3u",
                    isEnabled = true,
                }),
                Encoding.UTF8, "application/json"),
        };
        create.Headers.Add("X-CSRF-Token", csrf);

        var response = await harness.Client.SendAsync(create);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Prova de que o handler real executou e persistiu a alteração.
        await using var context = _factory.CreateDbContext();
        Assert.True(context.ScheduledJobs.Any(j => j.Name == "csrf-test"));
    }

    // === S1 — falha de wiring de auth não pode ser fail-open ===

    [Fact]
    public async Task Auth_wiring_failure_in_ready_fails_closed()
    {
        _lifecycle.SetState(ConfigurationLifecycleState.Ready, "test-ready-without-auth");
        var harness = StartHarnessWithoutAuth();

        // Fail-closed: não há acesso administrativo sem autenticação.
        Assert.Equal(HttpStatusCode.Unauthorized, (await harness.Client.GetAsync("/api/history")).StatusCode);

        // Diagnóstico permanece disponível.
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.GetAsync("/api/version")).StatusCode);
    }

    // === S1-E — ausência simultânea de lifecycle e auth ===

    [Fact]
    public async Task Unwired_production_dashboard_is_not_open()
    {
        // Contexto de produção (não-standalone): lifecycle == null e auth == null
        // (equivalente a falha de inicialização do catálogo no arranque).
        var harness = StartHarnessUnwired();

        // Endpoint administrativo NÃO fica aberto.
        Assert.Equal(HttpStatusCode.Unauthorized, (await harness.Client.GetAsync("/api/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await harness.Client.GetAsync("/api/catalog/channels")).StatusCode);

        // Diagnóstico explicitamente público continua acessível.
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.GetAsync("/api/version")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.GetAsync("/api/configuration/lifecycle")).StatusCode);
    }

    [Fact]
    public async Task Explicit_standalone_context_keeps_legacy_behaviour()
    {
        // Contexto explicitamente standalone/testes: comportamento legacy suportado.
        var harness = StartHarnessStandalone();

        Assert.Equal(HttpStatusCode.OK, (await harness.Client.GetAsync("/api/history")).StatusCode);
    }

    internal sealed class DashboardHarness : IAsyncDisposable
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
            ConfigurationLifecycleService? lifecycle,
            AuthService? auth,
            BootstrapService? bootstrap,
            string? webToken,
            bool standalone = false,
            TelegramAuthService? telegramAuth = null,
            DispatcharrConfigurationService? dispatcharrConfig = null,
            DispatcharrConnectionTester? dispatcharrTester = null,
            OperationalReadinessService? readiness = null,
            IAuditService? auditService = null,
            DispatcharrConnectionTestStore? dispatcharrTestStore = null,
            DispatcharrSyncCoordinator? dispatcharrSyncCoordinator = null,
            DispatcharrConcurrencyGate? dispatcharrConcurrencyGate = null,
            string? runtimeDataDir = null)
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
                        // W6 — Isolar o directório de país por harness, sem
                        // tocar no runtime-data real (o scope repõe o valor
                        // anterior no fim de cada pedido).
                        using var runtimeDataScope = runtimeDataDir is null
                            ? null
                            : new WebDashboardService.StaticRuntimeDataDirScope(runtimeDataDir);

                        // Handler sequencial: mantém determinismo de cookies/estado.
                        if (standalone)
                        {
                            await WebDashboardService.HandleRequestOnTestAsync(
                                context, outputDir, resolver, composer, history, webToken);
                        }
                        else
                        {
                            await WebDashboardService.HandleRequestWithAuthOnTestAsync(
                                context, outputDir, resolver, composer, history,
                                lifecycle, auth, bootstrap, webToken,
                                telegramAuth, dispatcharrConfig, dispatcharrTester, readiness,
                                auditService, dispatcharrTestStore,
                                dispatcharrSyncCoordinator, dispatcharrConcurrencyGate);
                        }
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
