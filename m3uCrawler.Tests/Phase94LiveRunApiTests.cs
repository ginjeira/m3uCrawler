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
using m3uCrawler.Services.LiveRun;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9C.4 — subwave 4: tests HTTP do dashboard para os endpoints
/// <c>GET /api/run/status</c> e <c>POST /api/run/start</c>.
///
/// <para>
/// Cobre:</para>
/// <list type="bullet">
///   <item>GET em Bootstrap (não-standalone), Legacy (READY sem admin),
///         UserAuth (com/sem sessão), machine (<c>--web-token</c>).</item>
///   <item>POST nas mesmas condições, incluindo gate CSRF.</item>
///   <item>Contratos: 200/202/401/403/409/503 <c>pipeline-not-configured</c>
///         e 503 <c>web-allow-trigger-disabled</c>.</item>
///   <item>Concorrência: 409 <c>already-running</c> num segundo POST.</item>
///   <item>Standalone: <c>--web</c> sem Telegram configurado ⇒
///         503 <c>pipeline-not-configured</c> em ambos endpoints.</item>
///   <item>Não-exposição de segredos em qualquer resposta.</item>
/// </list>
/// </summary>
[Collection("DashboardStaticState")]
public class Phase94LiveRunApiTests : IAsyncLifetime
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
    private Phase94RunApiHarness? _harness;

    public Phase94LiveRunApiTests()
    {
        _root = TestTempDb.SuitePath($"dash-liverun-{Guid.NewGuid():N}");
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

        WebDashboardService.SetLiveRunHost(null);
        WebDashboardService.SetWebAllowTrigger(false);
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
        WebDashboardService.SetLiveRunHost(null);
        WebDashboardService.SetAuth(null, null);
        WebDashboardService.SetConfigurationLifecycle(null);
        // Limpeza das BDs temporárias (evita esgotar o disco da CI/dev).
        TestTempDb.Cleanup(_dbPath);
        TestTempDb.Cleanup(_tempDbPaths.ToArray());
        TestTempDb.CleanupDirectory(_root);
    }

    private readonly List<string> _tempDbPaths = new();

    private string TrackDb(string path)
    {
        _tempDbPaths.Add(path);
        return path;
    }

    private Phase94RunApiHarness StartHarness(
        LiveRunHost? liveRunHost,
        bool webAllowTrigger,
        string? webToken = null,
        bool standalone = false)
    {
        _harness = Phase94RunApiHarness.Start(
            _outputDir, _resolver, _composer, _history,
            standalone ? null : _lifecycle,
            standalone ? null : _auth,
            standalone ? null : _bootstrap,
            liveRunHost, webAllowTrigger,
            webToken, standalone);
        return _harness;
    }

    private async Task<string> ReachReadyAndLoginAsync(Phase94RunApiHarness harness)
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

        var login = await harness.Client.PostAsync(
            "/api/session",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var csrfToken = doc.RootElement.GetProperty("csrfToken").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(csrfToken));
        return csrfToken;
    }

    private static StringContent EmptyJson() => new("{}", Encoding.UTF8, "application/json");
    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<LiveRunHost> BuildHostAsync(Func<LiveRunRequest, IRunPipeline>? executor = null)
    {
        var path = TrackDb(TestTempDb.SuitePath($"phase94-host-{Guid.NewGuid():N}.db"));
        var bootstrapper = new ChannelCatalogBootstrapper(path);
        await using (var bootstrapCtx = await bootstrapper.InitializeAsync())
        {
            // Bootstrap fecha o contexto.
        }
        var host = new LiveRunHost(new TestDbContextFactory(path));
        if (executor is not null)
        {
            host.ConfigureExecutor(executor);
        }
        return host;
    }

    private static IRunPipeline IdlePipeline() => new Phase94RunApiHarness.DelegatePipeline(_ => { });

    // ================ GET /api/run/status ================

    [Fact]
    public async Task Get_status_bootstrap_without_machine_token_returns_403()
    {
        // Em bootstrap o gate 9C.2 bloqueia antes do handler dos runs.
        // Resultado: 403 bootstrap-required.
        var harness = StartHarness(null, webAllowTrigger: false);
        var response = await harness.Client.GetAsync("/api/run/status");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("bootstrap-required", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_status_bootstrap_with_machine_token_returns_503()
    {
        // Em bootstrap, machine token passa o gate; sem host configurado
        // o handler responde 503 pipeline-not-configured.
        const string token = "bootstrap-machine-token";
        var harness = StartHarness(null, webAllowTrigger: false, webToken: token);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/run/status");
        request.Headers.Add("Authorization", $"Bearer {token}");
        var response = await harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("pipeline-not-configured", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_status_ready_without_admin_requires_bootstrap()
    {
        // PHASE 9C.5 — READY sem administrador activo é BOOTSTRAP_REQUIRED:
        // o gate bloqueia o handler dos runs (403 bootstrap-required) e o
        // Dashboard encaminha para o wizard, onde o primeiro administrador
        // é criado.
        _lifecycle.SetState(ConfigurationLifecycleState.Ready, "legacy-adoption:sources");
        var host = await BuildHostAsync(executor: _ => IdlePipeline());
        var harness = StartHarness(host, webAllowTrigger: false);

        var response = await harness.Client.GetAsync("/api/run/status");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("bootstrap-required", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_status_user_auth_requires_session()
    {
        var host = await BuildHostAsync(executor: _ => IdlePipeline());
        var harness = StartHarness(host, webAllowTrigger: false);
        await ReachReadyAndLoginAsync(harness);

        // Novo client sem cookies de sessão.
        using var fresh = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{harness.Port}") };
        var response = await fresh.GetAsync("/api/run/status");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_status_user_auth_with_session_returns_idle()
    {
        var host = await BuildHostAsync(executor: _ => IdlePipeline());
        var harness = StartHarness(host, webAllowTrigger: false);
        await ReachReadyAndLoginAsync(harness);

        var response = await harness.Client.GetAsync("/api/run/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("idle", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Get_status_machine_token_authorizes_when_no_user_session()
    {
        const string token = "machine-liverun-token";
        var host = await BuildHostAsync(executor: _ => IdlePipeline());
        var harness = StartHarness(host, webAllowTrigger: true, webToken: token);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/run/status");
        request.Headers.Add("Authorization", $"Bearer {token}");
        var response = await harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("webAllowTrigger").GetBoolean());
    }

    [Fact]
    public async Task Get_status_standalone_no_telegram_returns_503()
    {
        // Standalone = --web sem auth wiring; gate bypassed e sem host
        // configurado. O handler responde 503 pipeline-not-configured.
        var host = await BuildHostAsync(executor: null);
        var harness = StartHarness(host, webAllowTrigger: false, standalone: true);

        var response = await harness.Client.GetAsync("/api/run/status");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("pipeline-not-configured", await response.Content.ReadAsStringAsync());
    }

    // ================ POST /api/run/start ================

    [Fact]
    public async Task Post_start_bootstrap_without_machine_token_returns_403()
    {
        var host = await BuildHostAsync(executor: _ => IdlePipeline());
        var harness = StartHarness(host, webAllowTrigger: true);
        var response = await harness.Client.PostAsync("/api/run/start", EmptyJson());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("bootstrap-required", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_start_ready_without_admin_requires_bootstrap()
    {
        // PHASE 9C.5 — READY sem administrador activo deixa de ser Legacy
        // aberto: o POST é bloqueado pelo gate de bootstrap.
        _lifecycle.SetState(ConfigurationLifecycleState.Ready, "legacy-adoption:sources");
        var host = await BuildHostAsync(executor: _ => IdlePipeline());
        var harness = StartHarness(host, webAllowTrigger: true);

        var response = await harness.Client.PostAsync("/api/run/start", EmptyJson());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("bootstrap-required", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_start_user_auth_without_csrf_returns_403()
    {
        var host = await BuildHostAsync(executor: _ => IdlePipeline());
        var harness = StartHarness(host, webAllowTrigger: true);
        await ReachReadyAndLoginAsync(harness);

        var response = await harness.Client.PostAsync("/api/run/start", EmptyJson());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("csrf-invalid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_start_user_auth_with_csrf_starts_run()
    {
        var host = await BuildHostAsync(executor: _ => IdlePipeline());
        var harness = StartHarness(host, webAllowTrigger: true);
        var csrf = await ReachReadyAndLoginAsync(harness);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/run/start")
        {
            Content = EmptyJson(),
        };
        request.Headers.Add("X-CSRF-Token", csrf);
        var response = await harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("runId").GetString()));
    }

    [Fact]
    public async Task Post_start_machine_token_authorizes_when_no_user_session()
    {
        const string token = "machine-start-token";
        var host = await BuildHostAsync(executor: _ => IdlePipeline());
        var harness = StartHarness(host, webAllowTrigger: true, webToken: token);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/run/start")
        {
            Content = EmptyJson(),
        };
        request.Headers.Add("Authorization", $"Bearer {token}");
        var response = await harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Post_start_when_trigger_disabled_returns_503()
    {
        var host = await BuildHostAsync(executor: _ => IdlePipeline());
        var harness = StartHarness(host, webAllowTrigger: false, standalone: true);

        var response = await harness.Client.PostAsync("/api/run/start", EmptyJson());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("web-allow-trigger-disabled", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_start_standalone_no_telegram_returns_503_pipeline_not_configured()
    {
        var host = await BuildHostAsync(executor: null);
        var harness = StartHarness(host, webAllowTrigger: true, standalone: true);

        var response = await harness.Client.PostAsync("/api/run/start", EmptyJson());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("pipeline-not-configured", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_start_concurrent_returns_409_already_running()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new Phase94RunApiHarness.AsyncDelegatePipeline(async _ =>
        {
            started.TrySetResult();
            await release.Task;
        });

        var host = await BuildHostAsync(executor: _ => pipeline);
        var harness = StartHarness(host, webAllowTrigger: true, standalone: true);

        var first = harness.Client.PostAsync("/api/run/start", EmptyJson());
        await started.Task;

        var second = await harness.Client.PostAsync("/api/run/start", EmptyJson());
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("already-running", await second.Content.ReadAsStringAsync());

        release.TrySetResult();
        var firstResponse = await first;
        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
    }

    [Fact]
    public async Task Post_start_with_invalid_mode_returns_400()
    {
        var host = await BuildHostAsync(executor: _ => IdlePipeline());
        var harness = StartHarness(host, webAllowTrigger: true, standalone: true);

        var response = await harness.Client.PostAsync(
            "/api/run/start",
            JsonBody(JsonSerializer.Serialize(new { mode = "bogus" })));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid payload", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Status_during_run_reports_running_then_completed()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new Phase94RunApiHarness.AsyncDelegatePipeline(async _ =>
        {
            started.TrySetResult();
            await release.Task;
        });

        var host = await BuildHostAsync(executor: _ => pipeline);
        var harness = StartHarness(host, webAllowTrigger: true, standalone: true);

        var first = harness.Client.PostAsync("/api/run/start", EmptyJson());
        await started.Task;

        var status = await harness.Client.GetAsync("/api/run/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using (var doc = JsonDocument.Parse(await status.Content.ReadAsStringAsync()))
        {
            Assert.True(doc.RootElement.GetProperty("isRunning").GetBoolean());
            Assert.Equal("running", doc.RootElement.GetProperty("status").GetString());
        }

        release.TrySetResult();
        await first;

        // O contrato nunca regride para "idle": durante o fecho reporta
        // "running" (ou já "completed"); no fim reporta "completed".
        for (var i = 0; i < 100; i++)
        {
            var after = await harness.Client.GetAsync("/api/run/status");
            using var doc = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
            var statusText = doc.RootElement.GetProperty("status").GetString();
            if (statusText == "completed")
            {
                return;
            }
            Assert.NotEqual("idle", statusText);
            await Task.Delay(50);
        }
        Assert.Fail("Run did not transition to completed within timeout.");
    }

    [Fact]
    public async Task Status_after_run_failure_reports_failed()
    {
        var pipeline = new Phase94RunApiHarness.AsyncDelegatePipeline(async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("synthetic-pipeline-failure");
        });

        var host = await BuildHostAsync(executor: _ => pipeline);
        var harness = StartHarness(host, webAllowTrigger: true, standalone: true);

        var first = harness.Client.PostAsync("/api/run/start", EmptyJson());
        var firstResponse = await first;
        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);

        // O contrato nunca regride para "idle": durante o fecho reporta
        // "running" (ou já "failed"); no fim reporta "failed".
        for (var i = 0; i < 100; i++)
        {
            var status = await harness.Client.GetAsync("/api/run/status");
            using var doc = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            var statusText = doc.RootElement.GetProperty("status").GetString();
            if (statusText == "failed")
            {
                return;
            }
            Assert.NotEqual("idle", statusText);
            await Task.Delay(50);
        }
        Assert.Fail("Run did not transition to failed within timeout.");
    }

    [Fact]
    public async Task KickStartAsync_direct_failure_persists_terminal_status()
    {
        // Diagnóstico directo: KickStartAsync + pipeline que lança.
        // Verifica se a DB é actualizada sem passar pelo handler HTTP.
        var path = TrackDb(TestTempDb.SuitePath($"phase94-direct-{Guid.NewGuid():N}.db"));
        var bootstrapper = new ChannelCatalogBootstrapper(path);
        await using (var bootstrapCtx = await bootstrapper.InitializeAsync()) { }

        var factory = new TestDbContextFactory(path);
        var coordinator = new RunCoordinator(factory, _ => new Phase94RunApiHarness.AsyncDelegatePipeline(async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("direct-failure");
        }), null);

        var request = new LiveRunRequest { Mode = LiveRunMode.Telegram, Source = LiveRunSource.Manual };
        var outcome = await coordinator.KickStartAsync(request, CancellationToken.None);
        Assert.True(outcome.Succeeded);

        // Esperar pelo fecho do background task.
        for (var i = 0; i < 50; i++)
        {
            if (!coordinator.IsRunning) break;
            await Task.Delay(100);
        }

        Assert.False(coordinator.IsRunning);

        await using var readCtx = factory.CreateDbContext();
        var loaded = await readCtx.LiveRuns.SingleAsync();
        Assert.Equal(LiveRunTerminalStatus.Failed, loaded.TerminalStatus);
        Assert.NotNull(loaded.FinishedAtUtc);
    }

    [Fact]
    public async Task Responses_never_expose_secrets()
    {
        var host = await BuildHostAsync(executor: _ => IdlePipeline());
        var harness = StartHarness(host, webAllowTrigger: true, standalone: true);

        var status = await harness.Client.GetAsync("/api/run/status");
        var statusBody = await status.Content.ReadAsStringAsync();

        var start = await harness.Client.PostAsync("/api/run/start", EmptyJson());
        var startBody = await start.Content.ReadAsStringAsync();

        var combined = statusBody + "\n" + startBody;
        Assert.DoesNotContain("password", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", combined, StringComparison.OrdinalIgnoreCase);
    }

    // ================ Subwave 6 — dashboard/UI + polling ================

    [Fact]
    public async Task Dashboard_page_exposes_live_run_view_with_light_polling()
    {
        var host = await BuildHostAsync(executor: _ => IdlePipeline());
        var harness = StartHarness(host, webAllowTrigger: true, standalone: true);

        var response = await harness.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // Vista + controlos mínimos.
        Assert.Contains("view-liverun", html, StringComparison.Ordinal);
        Assert.Contains("loadLiveRun", html, StringComparison.Ordinal);
        Assert.Contains("startLiveRun", html, StringComparison.Ordinal);
        Assert.Contains("Run now", html, StringComparison.Ordinal);
        Assert.Contains("/api/run/status", html, StringComparison.Ordinal);
        Assert.Contains("/api/run/start", html, StringComparison.Ordinal);
        // Polling leve (setInterval), sem SSE nem WebSocket.
        Assert.Contains("setInterval", html, StringComparison.Ordinal);
        Assert.DoesNotContain("EventSource", html, StringComparison.Ordinal);
        Assert.DoesNotContain("new WebSocket", html, StringComparison.Ordinal);
        // Nunca faz parsing de logs Docker.
        Assert.DoesNotContain("docker logs", html, StringComparison.OrdinalIgnoreCase);
        // Mostra as secções exigidas pelo plano.
        Assert.Contains("Últimas actividades", html, StringComparison.Ordinal);
        Assert.Contains("Últimas execuções", html, StringComparison.Ordinal);
        Assert.Contains("telegramRun", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_payload_running_shape_exposes_counts_activities_and_recent_runs()
    {
        var running = new LiveRunSnapshot
        {
            RunId = "run-123",
            Mode = LiveRunMode.Telegram,
            Source = LiveRunSource.Manual,
            StartedAtUtc = DateTime.UtcNow.AddSeconds(-10),
            CurrentPhase = LiveRunPhase.Validating,
            IsRunning = true,
            PhaseIndex = 5,
            PhaseStartedAtUtc = DateTime.UtcNow.AddSeconds(-2),
            UpdatedAtUtc = DateTime.UtcNow,
            Counts = new LiveRunCounts { StreamsTested = 7 },
            RecentActivities = new[]
            {
                new LiveRunActivity(
                    DateTime.UtcNow,
                    LiveRunActivityCategory.Phase,
                    LiveRunActivityLevel.Info,
                    "fase validando iniciada",
                    null),
            },
            Sanitized = true,
        };
        var previous = new LiveRunSnapshot
        {
            RunId = "run-old",
            Mode = LiveRunMode.Telegram,
            Source = LiveRunSource.Scheduler,
            StartedAtUtc = DateTime.UtcNow.AddHours(-1),
            FinishedAtUtc = DateTime.UtcNow.AddMinutes(-50),
            TerminalStatus = LiveRunTerminalStatus.Completed,
            Sanitized = true,
        };

        var payload = LiveRunApiMappings.ToStatusPayload(
            live: running,
            recentFinished: null,
            pipelineConfigured: true,
            webAllowTrigger: true,
            coordinatorRunning: true,
            recentRuns: new[] { previous });

        var json = JsonSerializer.Serialize(payload);

        Assert.Contains("\"status\":\"running\"", json, StringComparison.Ordinal);
        Assert.Contains("run-123", json, StringComparison.Ordinal);
        Assert.Contains("\"phase\":\"validating\"", json, StringComparison.Ordinal);
        Assert.Contains("streamsTested", json, StringComparison.Ordinal);
        Assert.Contains("recentActivities", json, StringComparison.Ordinal);
        Assert.Contains("recentRuns", json, StringComparison.Ordinal);
        Assert.Contains("run-old", json, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"scheduler\"", json, StringComparison.Ordinal);
        Assert.Contains("\"webAllowTrigger\":true", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_payload_finished_shape_reports_terminal_status()
    {
        var finished = new LiveRunSnapshot
        {
            RunId = "run-456",
            Mode = LiveRunMode.TelegramMaintain,
            Source = LiveRunSource.Cli,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            FinishedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            TerminalStatus = LiveRunTerminalStatus.Failed,
            IsRunning = false,
            LastMessage = "failed: pipeline exception",
            Sanitized = true,
        };

        var payload = LiveRunApiMappings.ToStatusPayload(
            live: null,
            recentFinished: finished,
            pipelineConfigured: true,
            webAllowTrigger: false);

        var json = JsonSerializer.Serialize(payload);

        Assert.Contains("\"status\":\"failed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"terminalStatus\":\"failed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"mode\":\"telegram-maintain\"", json, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"cli\"", json, StringComparison.Ordinal);
        Assert.Contains("\"webAllowTrigger\":false", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_and_status_never_expose_secret_markers()
    {
        var host = await BuildHostAsync(executor: _ => IdlePipeline());
        var harness = StartHarness(host, webAllowTrigger: true, standalone: true);

        var page = await harness.Client.GetAsync("/");
        var html = await page.Content.ReadAsStringAsync();
        var status = await harness.Client.GetAsync("/api/run/status");
        var statusBody = await status.Content.ReadAsStringAsync();
        var combined = html + "\n" + statusBody;

        Assert.DoesNotContain("password", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Begin ", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization: Bearer", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password=", combined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Coordinator_lists_recent_finished_runs_ordered_and_capped()
    {
        var path = TrackDb(TestTempDb.SuitePath($"phase94-recent-{Guid.NewGuid():N}.db"));
        var bootstrapper = new ChannelCatalogBootstrapper(path);
        await using (var bootstrapCtx = await bootstrapper.InitializeAsync()) { }

        var factory = new TestDbContextFactory(path);
        var coordinator = new RunCoordinator(factory, _ => IdlePipeline(), null);

        for (var i = 0; i < 3; i++)
        {
            await coordinator.StartAsync(
                new LiveRunRequest { Mode = LiveRunMode.Telegram, Source = LiveRunSource.Cli },
                CancellationToken.None);
        }

        var recent = await coordinator.GetRecentFinishedSnapshotsAsync(2, CancellationToken.None);

        Assert.Equal(2, recent.Count);
        Assert.True(recent[0].FinishedAtUtc >= recent[1].FinishedAtUtc);
        Assert.All(recent, r => Assert.Equal(LiveRunTerminalStatus.Completed, r.TerminalStatus));

        var all = await coordinator.GetRecentFinishedSnapshotsAsync(10, CancellationToken.None);
        Assert.Equal(3, all.Count);
    }

    /// <summary>
    /// Harness isolado: regista explicitamente o <see cref="LiveRunHost"/>
    /// e o flag <c>--web-allow-trigger</c> antes de cada request.
    /// </summary>
    private sealed class Phase94RunApiHarness : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private Task _loop = Task.CompletedTask;

        private Phase94RunApiHarness(HttpListener listener, HttpClient client, int port)
        {
            _listener = listener;
            Client = client;
            Port = port;
        }

        public HttpClient Client { get; }
        public int Port { get; }

        public static Phase94RunApiHarness Start(
            string outputDir,
            CatalogResolver resolver,
            PlaylistComposerService composer,
            ImportHistoryService history,
            ConfigurationLifecycleService? lifecycle,
            AuthService? auth,
            BootstrapService? bootstrap,
            LiveRunHost? liveRunHost,
            bool webAllowTrigger,
            string? webToken,
            bool standalone)
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

            var harness = new Phase94RunApiHarness(listener, client, port);
            harness._loop = Task.Run(async () =>
            {
                while (!harness._cts.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); }
                    catch (Exception) { break; }

                    using var liveScope = new WebDashboardService.StaticLiveRunHostScope(
                        liveRunHost, webAllowTrigger);
                    try
                    {
                        if (standalone)
                        {
                            await WebDashboardService.HandleRequestOnTestAsync(
                                context, outputDir, resolver, composer, history, webToken);
                        }
                        else
                        {
                            await WebDashboardService.HandleRequestWithAuthOnTestAsync(
                                context, outputDir, resolver, composer, history,
                                lifecycle, auth, bootstrap, webToken);
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

        internal sealed class DelegatePipeline : IRunPipeline
        {
            private readonly Action<LiveRunRequest> _onExecute;
            public DelegatePipeline(Action<LiveRunRequest> onExecute) => _onExecute = onExecute;
            public Task ExecuteAsync(LiveRunRequest request, CancellationToken cancellationToken)
            {
                _onExecute(request);
                return Task.CompletedTask;
            }
        }

        internal sealed class AsyncDelegatePipeline : IRunPipeline
        {
            private readonly Func<LiveRunRequest, Task> _onExecute;
            public AsyncDelegatePipeline(Func<LiveRunRequest, Task> onExecute) => _onExecute = onExecute;
            public Task ExecuteAsync(LiveRunRequest request, CancellationToken cancellationToken)
                => _onExecute(request);
        }
    }
}
