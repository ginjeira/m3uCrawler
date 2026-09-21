using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using m3uCrawler.Services.LiveRun;
using m3uCrawler.Services.Recognition;
using m3uCrawler.Services.Sync;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W-E2E-02 — Operational E2E (componente <b>Integration Test</b>).
///
/// <para>
/// Percorre o caminho operacional real do <see cref="RunCoordinator"/>:
/// </para>
/// <code>
/// RunCoordinator.StartAsync
///   → LiveRunEntity persistido
///   → RecognitionPolicySnapshot persistido (D-M4-02)
///   → TelegramLiveRunExecutor REAL
///   → TelegramDiscoveryDelegate controlado
///   → PipelineIngestionService REAL
///   → CatalogResolver REAL
///   → ChannelSource / ReviewItem
///   → RunPublicationService REAL
///   → artefactos de publicação (playlist / report / run report / history)
///   → LiveRun Completed
/// </code>
///
/// <para>
/// Substituições controladas (escopo <b>deliberadamente limitado</b>):
/// </para>
/// <list type="bullet">
///   <item>
///     <b>TelegramDiscoveryDelegate</b> — substitui
///     <c>TelegramScraperService.SearchAndTestM3UInTelegramAsync</c>.
///     Replica o contrato interno do scraper: parse da fixture M3U,
///     marca <c>IsWorking = true</c> (acquisition bypass — fora do
/// scope), obtém a snapshot policy por RunId, chama
/// <see cref="PipelineIngestionService.IngestAsync"/> com
/// <c>runId = progress.RunId</c> e <c>policy = snapshotPolicy</c>, e
/// devolve <see cref="TelegramDiscoveryResult"/> com os mesmos
/// streams e um <see cref="RunReport"/>.
///   </item>
/// </list>
///
/// <para>
/// <b>Componentes reais exercitados.</b> RunCoordinator (com CAS lock,
/// persistência de LiveRunEntity, monitor injectado, ILiveRunProgressAware),
/// LiveRunMonitor, RecognitionPolicyResolver (CreateSnapshot + GetSnapshot),
/// TelegramLiveRunExecutor (resolve discovery, invoca delegate, chama
/// publicação), PipelineIngestionService (country gate, source ensure,
/// ingestion REAL com runId + snapshot policy), CatalogResolver (matching
/// real, persistence de ChannelSource, ReviewItem, DiscoveryCandidate),
/// RunPublicationService (SourceSelection, M3U + JSON + RunReport + history),
/// ImportHistoryService, LiveRunEntity persistence, Snapshot policy
/// persistence. Tudo sem rede, sem WTelegram, sem Dispatcharr HTTP, sem
/// acquisition HTTP.
/// </para>
///
/// <para>
/// <b>Divergências conhecidas.</b>
/// </para>
/// <list type="bullet">
///   <item>
///     <b>ChannelSourceEntity</b> e <b>ReviewItemEntity</b> não têm
///     coluna `RunId`. A propagação do RunId à camada de catálogo
///     é feita via <c>DiscoveryCandidateEntity.RunId</c> (única fonte
///     autoritativa de RunId em entidades não-LiveRun). O teste
///     afirma RunId nessa entidade.
///   </item>
///   <item>
///     <c>telegram_run_report.json</c> é uma serialização directa de
///     <see cref="RunReport"/> (camelCase) e o DTO não tem
///     propriedade <c>RunId</c>. O terminal é demonstrado por
///     <see cref="LiveRunEntity"/> e <see cref="LiveRunSnapshot"/>.
///   </item>
///   <item>
///     <see cref="RunReport.Status"/> permanece <c>"pending"</c> por
///     design — a terminalidade é <see cref="LiveRunEntity.TerminalStatus"/>.
///   </item>
/// </list>
/// </summary>
public class OperationalEndToEndRunTests : IAsyncLifetime
{
    private const string OpE2ERunPrefix = "op-e2e-";

    private readonly string _root;
    private readonly string _dbPath;
    private readonly DateTime _fixedLocal = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Local);
    private readonly DateTime _fixedUtc = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    private IDbContextFactory<ChannelCatalogDbContext> _factory = null!;
    private CatalogResolver _catalog = null!;
    private RecognitionPolicyResolver _policyResolver = null!;
    private CountryChannelValidator _countryValidator = null!;

    public OperationalEndToEndRunTests()
    {
        _root = TestTempDb.SuitePath($"{OpE2ERunPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "channel-catalog.db");
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        await using (var ctx = await bootstrapper.InitializeAsync())
        {
        }
        _catalog = new CatalogResolver(_factory, _dbPath);
        _policyResolver = new RecognitionPolicyResolver(_catalog);
        _countryValidator = CreateCountryValidator();
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        TestTempDb.CleanupDirectory(_root);
        return Task.CompletedTask;
    }

    // ─────────────────────────────────────────────────────────────────
    // Fixture (reutilizada de EndToEndPublicationTests)
    // ─────────────────────────────────────────────────────────────────

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
        // Igual a EndToEndPublicationTests: pt.json minimal com
        // "CANAL MISTERIOSO" para que o country gate o deixe passar
        // (caso contrário ficaria REJECTED antes do recognition).
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

    // ─────────────────────────────────────────────────────────────────
    // Pipeline
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OperationalRunCoordinator_completes_run_with_persisted_RunId_across_all_layers()
    {
        // ── Arrange: infra-estrutura operacional ─────────────────────────
        var outputDir = Path.Combine(_root, "operational");
        Directory.CreateDirectory(outputDir);

        var publicationService = new RunPublicationService(
            outputDir,
            new PlaylistManagerService(),
            new ImportHistoryService(outputDir),
            countryValidator: null,                  // gate diagnóstico; null = no-op
            catalog: null,                           // SourceSelection → NoOp pass-through
            dispatcharr: new DispatcharrSyncCoordinator(() => DispatcharrConfig.Disabled()),
            clock: () => _fixedLocal,
            utcClock: () => _fixedUtc);

        // O delegate substitui apenas o TelegramScraperService —
        // replica o contrato interno (IngestAsync com runId + policy).
        string? delegateObservedRunId = null;
        TelegramDiscoveryDelegate discover = async (_, progress, ct) =>
        {
            Assert.NotNull(progress);                                // coordinator injectou o monitor
            delegateObservedRunId = progress!.RunId;

            var streams = new M3uParserService().Parse(M3UFixture);
            Assert.Equal(3, streams.Count);
            foreach (var s in streams) s.IsWorking = true;           // acquisition bypass (fora do scope)

            var snapshotPolicy = await _policyResolver.GetSnapshotPolicyAsync(
                progress.RunId,
                canonicalChannelKey: null,
                groupKey: null,
                ct);

            var ingestor = new PipelineIngestionService(_catalog, _countryValidator);
            var ingestion = await ingestor.IngestAsync(
                streams,
                sourceKey: "OpE2E",
                sourceKindName: "Test",
                countryCode: "pt",
                cancellationToken: ct,
                runId: progress.RunId,
                policy: snapshotPolicy);

            Assert.Equal(3, ingestion.ReceivedCount);
            Assert.Equal(2, ingestion.IngestedCount);
            Assert.Equal(2, ingestion.MatchedCount);

            var report = new RunReport
            {
                StartedAt = _fixedUtc,
                FinishedAt = _fixedUtc.AddSeconds(2),
                DurationMs = 2000,
                Status = "pending",                                  // terminalidade vive em LiveRun, não aqui
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

        var executor = new TelegramLiveRunExecutor(
            discover: discover,
            maintain: null,
            publication: publicationService,
            discoverySettings: null,
            countryCode: "pt");

        var host = new LiveRunHost(_factory);
        host.ConfigureExecutor(_ => executor);
        var coordinator = host.Coordinator!;

        // O LiveRunHost cria o coordinator sem resolver (a CLI injecta-o
        // separadamente). Aqui usamos o setter público D-M4-02 para
        // activar o snapshot lifecycle neste teste.
        coordinator.SetRecognitionPolicyResolver(_policyResolver);

        // ── Act ─────────────────────────────────────────────────────────
        var outcome = await coordinator.StartAsync(
            new LiveRunRequest
            {
                Mode = LiveRunMode.Telegram,
                Source = LiveRunSource.Manual,
                Keyword = "fixture",
                HistoryHours = 24,
                MaxStreams = 50,
            },
            CancellationToken.None);

        // ── Assert: RunId (passa por todas as camadas) ──────────────────
        Assert.True(outcome.Succeeded);
        Assert.NotNull(outcome.Snapshot);

        // LiveRunSnapshot.RunId é GUID.
        Assert.False(string.IsNullOrWhiteSpace(outcome.Snapshot.RunId));
        Assert.True(Guid.TryParse(outcome.Snapshot.RunId, out _),
            $"LiveRunSnapshot.RunId '{outcome.Snapshot.RunId}' deve ser GUID.");

        // Delegate observou o mesmo RunId injectado pelo coordinator.
        Assert.Equal(outcome.Snapshot.RunId, delegateObservedRunId);

        // ── Assert: LiveRun terminal state ──────────────────────────────
        Assert.Equal(LiveRunTerminalStatus.Completed, outcome.Snapshot.TerminalStatus);
        Assert.Equal("completed", outcome.Snapshot.LastMessage);
        Assert.NotNull(outcome.Snapshot.FinishedAtUtc);
        Assert.False(outcome.Snapshot.IsRunning);

        Assert.False(coordinator.IsRunning);

        // ── Assert: LiveRunEntity persistido com mesmo RunId ────────────
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var liveRun = await ctx.LiveRuns.AsNoTracking().SingleAsync();
            Assert.Equal(outcome.Snapshot.RunId, liveRun.RunId);
            Assert.Equal(LiveRunTerminalStatus.Completed, liveRun.TerminalStatus);
            Assert.Equal("completed", liveRun.LastMessage);
            Assert.NotNull(liveRun.FinishedAtUtc);

            // ── Assert: RecognitionPolicySnapshot ──────────────────────
            var snaps = await ctx.RecognitionPolicySnapshots.AsNoTracking().ToListAsync();
            Assert.Single(snaps);
            Assert.Equal(outcome.Snapshot.RunId, snaps[0].RunId);
            Assert.Equal("rp1", snaps[0].ResolverVersion);

            // ── Assert: Recognition (ChannelSource + ReviewItem) ────────
            var rtp1 = await ctx.CanonicalChannels.AsNoTracking().SingleAsync(c => c.Key == "rtp1");
            var rtp2 = await ctx.CanonicalChannels.AsNoTracking().SingleAsync(c => c.Key == "rtp2");

            var rtp1Sources = await ctx.ChannelSources
                .AsNoTracking()
                .Where(s => s.CanonicalChannelId == rtp1.Id)
                .ToListAsync();
            var rtp2Sources = await ctx.ChannelSources
                .AsNoTracking()
                .Where(s => s.CanonicalChannelId == rtp2.Id)
                .ToListAsync();

            Assert.Single(rtp1Sources);
            Assert.Equal("CanonicalExact", rtp1Sources[0].MatchMethod);
            Assert.Contains("rtp1", rtp1Sources[0].StreamUrl);

            Assert.Single(rtp2Sources);
            Assert.Equal("CanonicalExact", rtp2Sources[0].MatchMethod);
            Assert.Contains("rtp2", rtp2Sources[0].StreamUrl);

            var reviewItems = await ctx.ReviewItems.AsNoTracking().ToListAsync();
            Assert.Single(reviewItems);
            Assert.Equal("canal misterioso", reviewItems[0].NormalizedIdentity);
            Assert.Equal(ReviewItemState.Open, reviewItems[0].State);
            Assert.Equal("unknown-via-pipeline", reviewItems[0].ReasonSignature);

            // ── Assert: RunId propagado via DiscoveryCandidate ──────────
            // ChannelSourceEntity e ReviewItemEntity não têm coluna
            // RunId por design (ver W1). A propagação é feita via
            // DiscoveryCandidateEntity.RunId — esta é a única fonte
            // autoritativa de RunId nas entidades não-LiveRun.
            var candidates = await ctx.DiscoveryCandidates.AsNoTracking().ToListAsync();
            Assert.NotEmpty(candidates);
            Assert.All(candidates, c => Assert.Equal(outcome.Snapshot.RunId, c.RunId));

            // ── Assert: RunReport.Status permanece "pending" (intencional)
            // (terminalidade vive em LiveRun.TerminalStatus, não em RunReport).
            // A verificação directa no ficheiro corre abaixo (Assert sobre
            // telegram_run_report.json), depois de fechar este bloco.
        }

        // ── Assert: artefactos de publicação existem ────────────────────
        var playlistFiles = Directory.GetFiles(outputDir, "telegram_playlist_*.m3u");
        var jsonReportFiles = Directory.GetFiles(outputDir, "telegram_report_*.json");
        var runReportFile = Path.Combine(outputDir, "telegram_run_report.json");
        var importHistoryFile = Path.Combine(outputDir, "import_history.json");

        Assert.Single(playlistFiles);
        Assert.Single(jsonReportFiles);
        Assert.True(File.Exists(runReportFile));
        Assert.True(File.Exists(importHistoryFile));

        var playlistLines = await File.ReadAllLinesAsync(playlistFiles[0]);
        var httpLines = playlistLines.Where(l => l.StartsWith("http://", StringComparison.Ordinal)).ToList();
        // 3 streams no M3U (RTP 1, Canal Misterioso, RTP 2) — pass-through
        // do SourceSelection sem catalog (NoOp) preserva todas as 3.
        Assert.Equal(3, httpLines.Count);

        // RunReport gravado tem o status "pending" — RunId não é
        // persistido neste ficheiro por design (ver RunReport.cs).
        var runReportJson = await File.ReadAllTextAsync(runReportFile);
        Assert.Contains("\"status\"", runReportJson);
        Assert.Contains("\"pending\"", runReportJson);

        // ── Assert: outcome.Snapshot coerente com entidade persistida ───
        await using (var verifyCtx = await _factory.CreateDbContextAsync())
        {
            var liveRun = await verifyCtx.LiveRuns.AsNoTracking().SingleAsync();
            Assert.Equal(liveRun.RunId, outcome.Snapshot.RunId);
        }
    }
}