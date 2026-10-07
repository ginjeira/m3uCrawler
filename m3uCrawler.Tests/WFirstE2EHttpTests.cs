using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Auth;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using m3uCrawler.Services.LiveRun;
using m3uCrawler.Services.Recognition;
using m3uCrawler.Services.Sync;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

// W-FIRST-E2E-TEST — prova E2E HTTP do primeiro Run (W-PRE-FIRST-E2E follow-up).
//
// Esta classe comprova que, partindo da superfície HTTP real (HttpListener +
// WebDashboardService) e sem Telegram/Dispatcharr externos, o caminho
// completo do botão "Run now" atravessa o pipeline real e termina com um
// LiveRunEntity persistido em estado Completed, artefactos de publicação no
// filesystem, e o cursor publicationPending do DL-130 actualizado.
//
// Substituições deliberadas (escopo limitado à fronteira externa):
//
//   * TelegramDiscoveryDelegate — substitui TelegramScraperService. Replica o
//     contrato interno do scraper (parse M3U, IsWorking=true como acquisition
//     bypass — fora do scope, documentado), PipelineIngestionService REAL com
//     runId + snapshot policy, RunReport autoritativo.
//
//   * DispatcharrSyncCoordinator — recebe DispatcharrConfig.Disabled() (zero
//     HTTP, retorna DispatcharrSyncStatus.Disabled). Esta wave não testa o
//     caminho Dispatcharr activo — esse é objecto da wave W6c-1/DL-130.
//
// Componentes reais exercitados:
//   HttpListener, WebDashboardService (Bootstrap + Session + CSRF + Run),
//   RunCoordinator (com CAS lock, ILiveRunProgressAware, monitor injectado),
//   LiveRunMonitor, LiveRunEntity persistido,
//   TelegramLiveRunExecutor (resolve discovery → invoca delegate → chama
//   publicação), PipelineIngestionService (country gate, source ensure,
//   matching real, persistência de ChannelSource/ReviewItem/DiscoveryCandidate),
//   CatalogResolver, RecognitionPolicyResolver (snapshot por RunId),
//   RunPublicationService (SourceSelection, playlist + JSON + RunReport +
//   import history), ImportHistoryService, PublicationStatusService (DL-130
//   cursors), PlaylistManagerService.SaveToM3uPlaylistAtomic (DL-019).
//
// Tudo sem rede, sem WTelegram, sem Dispatcharr HTTP, sem acquisition HTTP.
[Collection("DashboardStaticState")]
public class WFirstE2EHttpTests : IAsyncLifetime
{
    private const string OpPrefix = "e2e-first-";
    private const string ValidPassword = "a-very-strong-password";

    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _outputDir;
    private readonly string _storePath;
    private readonly DateTime _fixedUtc = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);
    private readonly DateTime _fixedLocal = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Local);

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _catalog = null!;
    private PlaylistComposerService _composer = null!;
    private ImportHistoryService _history = null!;
    private ConfigurationLifecycleService _lifecycle = null!;
    private AuthService _auth = null!;
    private BootstrapService _bootstrap = null!;
    private RecognitionPolicyResolver _policyResolver = null!;
    private CountryChannelValidator _countryValidator = null!;
    private E2EFirstRunHarness? _harness;

    public WFirstE2EHttpTests()
    {
        _root = TestTempDb.SuitePath($"{OpPrefix}{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _outputDir = Path.Combine(_root, "output");
        _storePath = Path.Combine(_root, ConfigurationLifecycleStore.FileName);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outputDir);
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        await using (var ctx = await bootstrapper.InitializeAsync())
        {
        }

        _catalog = new CatalogResolver(_factory, _dbPath);
        _composer = new PlaylistComposerService(_factory);
        _history = new ImportHistoryService(_outputDir);
        _lifecycle = new ConfigurationLifecycleService(
            new ConfigurationLifecycleStore(_storePath), _factory, _outputDir);

        var users = new AdminUserStore(_factory);
        _auth = new AuthService(users, new SessionStore(_factory));
        _bootstrap = new BootstrapService(
            _lifecycle, users,
            new BootstrapConfigurationValidator(_factory, _outputDir));

        _policyResolver = new RecognitionPolicyResolver(_catalog);
        _countryValidator = CreateCountryValidator();

        // Garantir que outros testes não deixam statics sujos.
        WebDashboardService.SetLiveRunHost(null);
        WebDashboardService.SetWebAllowTrigger(false);
        WebDashboardService.SetAuth(null, null);
        WebDashboardService.SetConfigurationLifecycle(null);
        WebDashboardService.SetDispatcharrSync(null, null);
        // SetPublicationStatusService é fail-closed: não aceita null. Os
        // testes que precisam dele chamam-no localmente; os outros ficam
        // sem publication-status (503, que é o fail-closed correcto).
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
            _harness = null;
        }
        WebDashboardService.SetLiveRunHost(null);
        WebDashboardService.SetAuth(null, null);
        WebDashboardService.SetConfigurationLifecycle(null);
        WebDashboardService.SetDispatcharrSync(null, null);
        TestTempDb.Cleanup(_dbPath);
        TestTempDb.CleanupDirectory(_root);
    }

    // ────────────────────────────────────────────────────────────────────
    // Fixture M3U (idêntica a OperationalEndToEndRunTests/EndToEndPublicationTests)
    // ────────────────────────────────────────────────────────────────────

    private const string M3UFixture =
        "#EXTM3U\n" +
        "#EXTINF:-1 tvg-id=\"rtp1.pt\" group-title=\"Portugal\",RTP 1\n" +
        "http://test.example/rtp1.m3u8\n" +
        "#EXTINF:-1 tvg-id=\"unknown.pt\" group-title=\"Portugal\",Canal Misterioso\n" +
        "http://test.example/misterio.m3u8\n" +
        "#EXTINF:-1 tvg-id=\"\" group-title=\"Portugal\",RTP 2\n" +
        "http://test.example/rtp2.m3u8\n";

    private CountryChannelValidator CreateCountryValidator()
    {
        // pt.json mínimo: 2 canais conhecidos (RTP 1, RTP 2) + 1 alias que
        // aceita o Canal Misterioso para que o country gate o deixe passar
        // (caso contrário seria REJECTED antes do recognition).
        var ptPath = Path.Combine(_root, "pt.json");
        File.WriteAllText(ptPath, """
            {
              "Country": "pt",
              "Channels": [
                "RTP 1",
                "RTP 2",
                "CANAL MISTERIOSO"
              ]
            }
            """);
        return new CountryChannelValidator(_root);
    }

    // ────────────────────────────────────────────────────────────────────
    // Happy path: HTTP → RunCoordinator → TelegramLiveRunExecutor → publicação
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_run_start_walks_real_pipeline_to_completed_terminal_state()
    {
        // BuildPublicationService com Dispatcharr desactivado (não é o
        // objectivo desta wave validar Dispatcharr activo; W6c-1 cobre).
        var publicationService = new RunPublicationService(
            _outputDir,
            new PlaylistManagerService(),
            new ImportHistoryService(_outputDir),
            countryValidator: null,
            catalog: null,
            dispatcharr: new DispatcharrSyncCoordinator(() => DispatcharrConfig.Disabled()),
            clock: () => _fixedLocal,
            utcClock: () => _fixedUtc);

        // DiscoverySettingsProvider: null é aceitável porque o delegate
        // (a fronteira) não o consulta — é o scraper real que o faz.
        var executor = new TelegramLiveRunExecutor(
            discover: BuildDiscoveryDelegate(),
            maintain: null,
            publication: publicationService,
            discoverySettings: null,
            countryCode: "pt");

        var host = new LiveRunHost(_factory);
        host.ConfigureExecutor(_ => executor);
        host.Coordinator!.SetRecognitionPolicyResolver(_policyResolver);

        // PublicationStatusService para que GET /api/publication/status
        // responda 200 em vez de 503 (não é estritamente necessário para
        // o happy path, mas queremos validar o cursor DL-130 depois).
        WebDashboardService.SetPublicationStatusService(new PublicationStatusService(_factory));

        _harness = E2EFirstRunHarness.Start(
            _outputDir, _catalog, _composer, _history,
            _lifecycle, _auth, _bootstrap,
            host, webAllowTrigger: true,
            webToken: null, standalone: false);

        var csrf = await ReachReadyAndLoginAsync(_harness);

        // ── Act: POST /api/run/start com CSRF ────────────────────────────
        var startRequest = new HttpRequestMessage(HttpMethod.Post, "/api/run/start")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        startRequest.Headers.Add("X-CSRF-Token", csrf);
        var startResponse = await _harness.Client.SendAsync(startRequest);
        Assert.Equal(HttpStatusCode.Accepted, startResponse.StatusCode);

        using (var startDoc = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync()))
        {
            var runId = startDoc.RootElement.GetProperty("runId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(runId));
            Assert.True(Guid.TryParse(runId, out _), $"RunId '{runId}' deve ser GUID.");
        }

        // ── Poll: GET /api/run/status até terminal (CI-friendly timeout) ─
        var (finalStatus, terminalStatus) = await PollUntilTerminalAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("completed", finalStatus);
        Assert.Equal("completed", terminalStatus);

        // ── Assert: terminal state propagado a /api/run/status ───────────
        var statusResponse = await _harness.Client.GetAsync("/api/run/status");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        using (var statusDoc = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync()))
        {
            Assert.False(statusDoc.RootElement.GetProperty("isRunning").GetBoolean());
            Assert.Equal("completed", statusDoc.RootElement.GetProperty("status").GetString());
            Assert.True(statusDoc.RootElement.GetProperty("webAllowTrigger").GetBoolean());
            // runId no payload terminal está aninhado em lastRun.runId
            // (ver LiveRunApiContracts.SnapshotToFinishedPayload).
            Assert.True(statusDoc.RootElement.TryGetProperty("lastRun", out var lastRun)
                && lastRun.ValueKind == JsonValueKind.Object
                && lastRun.TryGetProperty("runId", out var lastRunId)
                && !string.IsNullOrWhiteSpace(lastRunId.GetString()),
                "lastRun.runId deve estar presente no payload terminal.");
        }

        // ── Assert: LiveRunEntity persistido em Completed ────────────────
        await using (var verifyCtx = _factory.CreateDbContext())
        {
            var liveRun = await verifyCtx.LiveRuns.AsNoTracking().SingleAsync();
            Assert.Equal(LiveRunTerminalStatus.Completed, liveRun.TerminalStatus);
            Assert.Equal("completed", liveRun.LastMessage);
            Assert.NotNull(liveRun.FinishedAtUtc);

            // Catalog persistido: 2 ChannelSource exactos + 1 ReviewItem.
            var sources = await verifyCtx.ChannelSources.AsNoTracking().ToListAsync();
            Assert.Equal(2, sources.Count);
            Assert.All(sources, s => Assert.Equal("CanonicalExact", s.MatchMethod));

            var reviews = await verifyCtx.ReviewItems.AsNoTracking().ToListAsync();
            Assert.Single(reviews);
            Assert.Equal("canal misterioso", reviews[0].NormalizedIdentity);
            Assert.Equal(ReviewItemState.Open, reviews[0].State);

            // RunId propagado via DiscoveryCandidate (única fonte autoritativa
            // de RunId em entidades não-LiveRun por design).
            var candidates = await verifyCtx.DiscoveryCandidates.AsNoTracking().ToListAsync();
            Assert.NotEmpty(candidates);
            Assert.All(candidates, c => Assert.Equal(liveRun.RunId, c.RunId));
        }

        // ── Assert: artefactos de publicação reais no disco ──────────────
        var playlistFiles = Directory.GetFiles(_outputDir, "telegram_playlist_*.m3u");
        var jsonReportFiles = Directory.GetFiles(_outputDir, "telegram_report_*.json");
        var runReportFile = Path.Combine(_outputDir, "telegram_run_report.json");
        var importHistoryFile = Path.Combine(_outputDir, "import_history.json");

        Assert.Single(playlistFiles);
        Assert.Single(jsonReportFiles);
        Assert.True(File.Exists(runReportFile));
        Assert.True(File.Exists(importHistoryFile));

        var playlistLines = await File.ReadAllLinesAsync(playlistFiles[0]);
        var httpLines = playlistLines
            .Where(l => l.StartsWith("http://", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(3, httpLines.Count); // SourceSelection NoOp ⇒ pass-through das 3

        // ── Assert: DL-130 cursor publicationPending = false após Run ───
        var pubResponse = await _harness.Client.GetAsync("/api/publication/status");
        Assert.Equal(HttpStatusCode.OK, pubResponse.StatusCode);
        using (var pubDoc = JsonDocument.Parse(await pubResponse.Content.ReadAsStringAsync()))
        {
            Assert.False(pubDoc.RootElement.GetProperty("publicationPending").GetBoolean(),
                "Após um Run Completed com RunPublicationService, publicationPending deve ser false.");
            Assert.True(pubDoc.RootElement.TryGetProperty("lastSuccessfulPublicationAtUtc", out var lastPub)
                && lastPub.ValueKind != JsonValueKind.Null,
                "lastSuccessfulPublicationAtUtc deve estar preenchido após publicação.");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // Failure path: Telegram delegate lança → Run termina Failed
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_run_start_with_failing_acquisition_terminates_failed()
    {
        var publicationService = new RunPublicationService(
            _outputDir,
            new PlaylistManagerService(),
            new ImportHistoryService(_outputDir),
            countryValidator: null,
            catalog: null,
            dispatcharr: new DispatcharrSyncCoordinator(() => DispatcharrConfig.Disabled()),
            clock: () => _fixedLocal,
            utcClock: () => _fixedUtc);

        TelegramDiscoveryDelegate failingDiscover = (_, _, _) =>
            throw new InvalidOperationException("W-FIRST-E2E-TEST simulated Telegram failure");

        var executor = new TelegramLiveRunExecutor(
            discover: failingDiscover,
            maintain: null,
            publication: publicationService,
            discoverySettings: null,
            countryCode: "pt");

        var host = new LiveRunHost(_factory);
        host.ConfigureExecutor(_ => executor);
        host.Coordinator!.SetRecognitionPolicyResolver(_policyResolver);

        _harness = E2EFirstRunHarness.Start(
            _outputDir, _catalog, _composer, _history,
            _lifecycle, _auth, _bootstrap,
            host, webAllowTrigger: true,
            webToken: null, standalone: false);

        var csrf = await ReachReadyAndLoginAsync(_harness);

        var startRequest = new HttpRequestMessage(HttpMethod.Post, "/api/run/start")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        startRequest.Headers.Add("X-CSRF-Token", csrf);
        var startResponse = await _harness.Client.SendAsync(startRequest);
        Assert.Equal(HttpStatusCode.Accepted, startResponse.StatusCode);

        // Poll até terminal: deve terminar como failed, não completed.
        var (finalStatus, terminalStatus) = await PollUntilTerminalAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("failed", finalStatus);
        Assert.Equal("failed", terminalStatus);

        // LiveRunEntity persistido como Failed.
        await using (var verifyCtx = _factory.CreateDbContext())
        {
            var liveRun = await verifyCtx.LiveRuns.AsNoTracking().SingleAsync();
            Assert.Equal(LiveRunTerminalStatus.Failed, liveRun.TerminalStatus);
            Assert.NotNull(liveRun.FinishedAtUtc);
        }

        // Sem publicação (o Run falhou antes de chegar a RunPublicationService).
        var playlistFiles = Directory.GetFiles(_outputDir, "telegram_playlist_*.m3u");
        Assert.Empty(playlistFiles);
    }

    // ────────────────────────────────────────────────────────────────────
    // Gates obrigatórios: garantir que o E2E não contorna os gates existentes
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_run_start_without_csrf_returns_403_and_does_not_trigger_run()
    {
        var publicationService = new RunPublicationService(
            _outputDir, new PlaylistManagerService(), new ImportHistoryService(_outputDir),
            countryValidator: null, catalog: null,
            dispatcharr: new DispatcharrSyncCoordinator(() => DispatcharrConfig.Disabled()),
            clock: () => _fixedLocal, utcClock: () => _fixedUtc);

        var executor = new TelegramLiveRunExecutor(
            discover: BuildDiscoveryDelegate(), maintain: null, publication: publicationService,
            discoverySettings: null, countryCode: "pt");

        var host = new LiveRunHost(_factory);
        host.ConfigureExecutor(_ => executor);
        host.Coordinator!.SetRecognitionPolicyResolver(_policyResolver);

        _harness = E2EFirstRunHarness.Start(
            _outputDir, _catalog, _composer, _history,
            _lifecycle, _auth, _bootstrap,
            host, webAllowTrigger: true, webToken: null, standalone: false);

        // Bootstrap + login mas NÃO usamos o csrf devolvido.
        await ReachReadyAndLoginAsync(_harness);

        var response = await _harness.Client.PostAsync("/api/run/start",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("csrf-invalid", await response.Content.ReadAsStringAsync());

        // Nenhum LiveRun persistido.
        await using var ctx = _factory.CreateDbContext();
        Assert.Empty(await ctx.LiveRuns.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Post_run_start_with_trigger_disabled_returns_503_and_does_not_trigger_run()
    {
        // Host configurado (pipeline OK), mas webAllowTrigger=false.
        // Importante: gate CSRF/Bootstrap roda ANTES do gate do trigger
        // (ver HandleRequestWithAuthOnTestAsync linhas ~595-650). Por isso
        // é necessário bootstrap+login primeiro para chegarmos ao gate
        // do trigger — caso contrário receberíamos 403 csrf-invalid /
        // bootstrap-required, não o 503 web-allow-trigger-disabled.
        var publicationService = new RunPublicationService(
            _outputDir, new PlaylistManagerService(), new ImportHistoryService(_outputDir),
            countryValidator: null, catalog: null,
            dispatcharr: new DispatcharrSyncCoordinator(() => DispatcharrConfig.Disabled()),
            clock: () => _fixedLocal, utcClock: () => _fixedUtc);

        var executor = new TelegramLiveRunExecutor(
            discover: BuildDiscoveryDelegate(), maintain: null, publication: publicationService,
            discoverySettings: null, countryCode: "pt");

        var host = new LiveRunHost(_factory);
        host.ConfigureExecutor(_ => executor);
        host.Coordinator!.SetRecognitionPolicyResolver(_policyResolver);

        _harness = E2EFirstRunHarness.Start(
            _outputDir, _catalog, _composer, _history,
            _lifecycle, _auth, _bootstrap,
            host, webAllowTrigger: false, webToken: null, standalone: false);

        var csrf = await ReachReadyAndLoginAsync(_harness);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/run/start")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CSRF-Token", csrf);
        var response = await _harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("web-allow-trigger-disabled", await response.Content.ReadAsStringAsync());

        // Nenhum LiveRun persistido.
        await using var ctx = _factory.CreateDbContext();
        Assert.Empty(await ctx.LiveRuns.AsNoTracking().ToListAsync());
    }

    // ────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────

    private TelegramDiscoveryDelegate BuildDiscoveryDelegate()
    {
        return async (_, progress, ct) =>
        {
            Assert.NotNull(progress);
            var runId = progress!.RunId;

            var streams = new M3uParserService().Parse(M3UFixture);
            Assert.Equal(3, streams.Count);
            foreach (var s in streams)
            {
                s.IsWorking = true; // acquisition bypass (rede não é objectivo desta wave)
            }

            var snapshotPolicy = await _policyResolver.GetSnapshotPolicyAsync(
                runId,
                canonicalChannelKey: null,
                groupKey: null,
                ct);

            var ingestor = new PipelineIngestionService(_catalog, _countryValidator);
            var ingestion = await ingestor.IngestAsync(
                streams,
                sourceKey: "E2EFirstRun",
                sourceKindName: "Test",
                countryCode: "pt",
                cancellationToken: ct,
                runId: runId,
                policy: snapshotPolicy);

            Assert.Equal(3, ingestion.ReceivedCount);
            Assert.Equal(2, ingestion.IngestedCount);
            Assert.Equal(2, ingestion.MatchedCount);

            var report = new RunReport
            {
                StartedAt = _fixedUtc,
                FinishedAt = _fixedUtc.AddSeconds(2),
                DurationMs = 2000,
                Status = "pending",
                MessagesAnalyzed = 3,
                CandidatesFound = 1,
                PlaylistsDownloaded = 1,
                CountryMatches = 2,
                StreamsExtracted = 3,
                StreamsTested = 3,
                StreamsWorking = 3,
            };

            return new TelegramDiscoveryResult(streams, report);
        };
    }

    private async Task<string> ReachReadyAndLoginAsync(E2EFirstRunHarness harness)
    {
        var start = await harness.Client.PostAsync("/api/bootstrap/start",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);

        var admin = await harness.Client.PostAsync("/api/bootstrap/admin",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);

        var complete = await harness.Client.PostAsync("/api/bootstrap/complete",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        var login = await harness.Client.PostAsync("/api/session",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var csrfToken = doc.RootElement.GetProperty("csrfToken").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(csrfToken));
        return csrfToken;
    }

    /// <summary>
    /// Faz poll de <c>GET /api/run/status</c> até o Run atingir estado terminal
    /// (<c>completed</c> ou <c>failed</c>), ou até <paramref name="timeout"/>.
    /// Retorna (status, terminalStatus) — <c>status</c> é o campo top-level e
    /// <c>terminalStatus</c> vem de <c>lastRun.terminalStatus</c> (aninhado
    /// em <see cref="LiveRunApiContracts.SnapshotToFinishedPayload"/>).
    /// </summary>
    private async Task<(string Status, string TerminalStatus)> PollUntilTerminalAsync(
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string lastStatus = "unknown";
        string lastTerminal = "unknown";
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            attempt++;
            var response = await _harness!.Client.GetAsync("/api/run/status");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            lastStatus = doc.RootElement.GetProperty("status").GetString() ?? "unknown";

            if (lastStatus == "completed" || lastStatus == "failed")
            {
                // terminalStatus é um campo de lastRun no payload terminal.
                if (doc.RootElement.TryGetProperty("lastRun", out var lastRun)
                    && lastRun.ValueKind == JsonValueKind.Object
                    && lastRun.TryGetProperty("terminalStatus", out var ts))
                {
                    lastTerminal = ts.GetString() ?? "unknown";
                }
                return (lastStatus, lastTerminal);
            }
            // O contrato nunca regride para "idle" durante o fecho (ver
            // Phase94LiveRunApiTests.Status_during_run_reports_running_then_completed).
            Assert.NotEqual("idle", lastStatus);

            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException(
            $"Run did not reach terminal state within {timeout.TotalSeconds:N0}s. " +
            $"Last status='{lastStatus}', terminalStatus='{lastTerminal}', attempts={attempt}.");
    }

    // ────────────────────────────────────────────────────────────────────
    // HTTP harness (mesmo padrão de Phase94RunApiHarness; cópia deliberada
    // para manter isolamento entre classes de teste, conforme convenção do
    // projecto — ver AGENTS.md §4 "Não inventar comportamento").
    // ────────────────────────────────────────────────────────────────────

    private sealed class E2EFirstRunHarness : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private Task _loop = Task.CompletedTask;

        private E2EFirstRunHarness(HttpListener listener, HttpClient client, int port)
        {
            _listener = listener;
            Client = client;
            Port = port;
        }

        public HttpClient Client { get; }
        public int Port { get; }

        public static E2EFirstRunHarness Start(
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

            var harness = new E2EFirstRunHarness(listener, client, port);
            harness._loop = Task.Run(async () =>
            {
                while (!harness._cts.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); }
                    catch { break; }

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
    }
}
