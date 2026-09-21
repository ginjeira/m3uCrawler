using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.LiveRun;
using m3uCrawler.Services.Recognition;
using m3uCrawler.Services.Sync;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W-E2E-01 — Primeiro teste integrado da pipeline de publicação.
///
/// <para>
/// Componente: <b>Component Integration Test</b> (não Operational E2E).
/// O teste atravessa a pipeline central — <see cref="M3uParserService"/>,
/// <see cref="RecognitionPolicyResolver"/>, <see cref="PipelineIngestionService"/>,
/// <see cref="CatalogResolver"/>, <see cref="RunPublicationService"/> — usando
/// serviços reais e uma base de dados SQLite in-memory por teste.
/// </para>
///
/// <para>
/// Componentes deliberadamente fora do scope: <see cref="RunCoordinator"/>,
/// <see cref="TelegramLiveRunExecutor"/>, <see cref="TelegramScraperService"/>,
/// acquisition (<see cref="M3uTesterService"/>), Dispatcharr sync, e o
/// <see cref="OperationalReadinessService"/>. O snapshot é criado
/// directamente via <see cref="RecognitionPolicyResolver.CreateSnapshotAsync"/>
/// — não passa pelo caminho operacional do <see cref="RunCoordinator"/>.
/// </para>
///
/// <para>
/// <b>Comportamento conhecido fora do scope.</b> A playlist final contém 3
/// streams (RTP 1, Canal Misterioso, RTP 2) porque o
/// <see cref="SourceSelectionStage"/> faz pass-through dos streams sem
/// <see cref="ChannelSourceEntity"/> (divergência DIVERGENT documentada em
/// <c>docs/Reestructure/46-REQUIREMENT-TRACEABILITY.md</c> como
/// "Não listado não é publicado"). Este teste demonstra que a publicação
/// funciona; NÃO ratifica nem corrige a regra divergente. A correcção fica
/// para waves dedicadas.
/// </para>
/// </summary>
public class EndToEndPublicationTests : IAsyncLifetime
{
    private const string RunId = "e2e-run-001";

    private readonly string _dbPath;
    private readonly string _root;
    private readonly DateTime _fixedLocal = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Local);
    private readonly DateTime _fixedUtc = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    private IDbContextFactory<ChannelCatalogDbContext> _factory = null!;
    private CatalogResolver _catalog = null!;
    private RecognitionPolicyResolver _policyResolver = null!;

    public EndToEndPublicationTests()
    {
        _root = TestTempDb.SuitePath($"e2e-publication-{Guid.NewGuid():N}");
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
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        TestTempDb.CleanupDirectory(_root);
        return Task.CompletedTask;
    }

    // ─────────────────────────────────────────────────────────────────
    // Fixture
    // ─────────────────────────────────────────────────────────────────

    private const string M3UFixture =
        "#EXTM3U\n" +
        "#EXTINF:-1 tvg-id=\"rtp1.pt\" group-title=\"Portugal\",RTP 1\n" +
        "http://test.example/rtp1.m3u8\n" +
        "#EXTINF:-1 tvg-id=\"unknown.pt\" group-title=\"Portugal\",Canal Misterioso\n" +
        "http://test.example/misterio.m3u8\n" +
        "#EXTINF:-1 tvg-id=\"\" group-title=\"Portugal\",RTP 2\n" +
        "http://test.example/rtp2.m3u8\n";

    // ─────────────────────────────────────────────────────────────────
    // Seed
    // ─────────────────────────────────────────────────────────────────
    //
    // A baseline PT (semeadada pelo ChannelCatalogBootstrapper a partir de
    // docs/catalog/m3ucrawler_pt_canonical_catalog.json) já contém canais
    // e aliases suficientes para o fixture:
    //   • "RTP 1" → pt.rtp1 (Key) — match via Step 3 CanonicalExact
    //                (Normalize("rtp1") = "rtp 1"); aliases "rtp 1".
    //   • "RTP 2" → pt.rtp2 (Key) — match via Step 5 KnownAlias
    //                (alias "rtp 2" da baseline).
    //   • "Canal Misterioso" → Unknown (sem match).
    // Não criamos canais/aliases para evitar colisões de UNIQUE com a
    // baseline. O seed limita-se a confirmar a presença dos canais
    // necessários.

    private async Task<(CanonicalChannelEntity Rtp1, CanonicalChannelEntity Rtp2)> SeedCatalogAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();

        var rtp1 = await ctx.CanonicalChannels
            .SingleOrDefaultAsync(c => c.Key == "rtp1");
        Assert.NotNull(rtp1);

        var rtp2 = await ctx.CanonicalChannels
            .SingleOrDefaultAsync(c => c.Key == "rtp2");
        Assert.NotNull(rtp2);

        return (rtp1, rtp2);
    }

    // ─────────────────────────────────────────────────────────────────
    // Validator de país (mínimo, isolado)
    // ─────────────────────────────────────────────────────────────────

    private CountryChannelValidator CreateValidator()
    {
        // O countriesDir real contém aliases curados para PT (RTP, SIC,
        // TVI, etc.). O fixture deste teste tem títulos que NÃO casam
        // nesses aliases ("Canal Misterioso") e por isso seriam
        // correctamente rejeitados. Para reproduzir o cenário do teste
        // — em que "Canal Misterioso" passa o country gate e cai em
        // ReviewItem por falta de match canónico — escrevemos um pt.json
        // minimal no _root que adiciona "CANAL MISTERIOSO" como alias
        // adicional. Propriedades PascalCase — o JsonSerializer do .NET
        // 9 é case-sensitive por defeito em Deserialize<T>(json).
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
    public async Task M3U_text_passes_recognition_and_publication_pipeline()
    {
        // Arrange — confirmação dos canais baseline + snapshot
        var (rtp1, rtp2) = await SeedCatalogAsync();
        var snapshot = await _policyResolver.CreateSnapshotAsync(RunId, CancellationToken.None);

        // Assert snapshot persistido
        Assert.Equal(RunId, snapshot.RunId);
        Assert.Equal("rp1", snapshot.ResolverVersion);

        var snapshotPolicy = await _policyResolver.GetSnapshotPolicyAsync(
            RunId, canonicalChannelKey: null, groupKey: null, CancellationToken.None);
        Assert.NotNull(snapshotPolicy);
        Assert.False(snapshotPolicy!.FuzzyEnabled);   // RecognitionPolicy.Default

        // 1. Parse
        var streams = new M3uParserService().Parse(M3UFixture);
        Assert.Equal(3, streams.Count);
        // O parser produz streams com IsWorking=false (não testados).
        // No pipeline real, M3uTesterService.TestM3u8Stream valida cada
        // stream e marca IsWorking=true. Como este teste bypassa a
        // aquisição, simulamos esse resultado aqui. Sem isto, o filtro
        // `streams.Where(s => s.IsWorking)` em
        // PlaylistManagerService.BuildM3uPlaylistContent removeria
        // todos os streams e a playlist sairia vazia.
        foreach (var s in streams) s.IsWorking = true;

        // 2. Recognition + Ingestion
        var countryValidator = CreateValidator();
        var ingestor = new PipelineIngestionService(_catalog, countryValidator);
        var report = new RunReport();

        var ingestion = await ingestor.IngestAsync(
            streams,
            sourceKey: "E2E",
            sourceKindName: "Test",
            countryCode: "pt",
            cancellationToken: CancellationToken.None,
            runId: RunId,
            policy: snapshotPolicy);

        Assert.Equal(3, ingestion.ReceivedCount);
        Assert.Equal(2, ingestion.IngestedCount);
        Assert.Equal(2, ingestion.MatchedCount);
        Assert.Equal(0, ingestion.AutoCreatedCount);

        // 3. ChannelSources — cada Canonical reconhecido persiste um row
        //    para o seu canal respectivo. Na baseline PT, ambos os canais
        //    (pt.rtp1 e pt.rtp2) têm Keys que normalizam para os títulos
        //    correspondentes ("rtp1" → "rtp 1", "rtp2" → "rtp 2"), pelo
        //    que o match acontece em Step 3 (CanonicalExact) — Step 5
        //    (KnownAlias) só é alcançado se Step 3 falhar.
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
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

            // RTP 2 também faz match via Step 3 (CanonicalExact) porque
            // Normalize("rtp2") = "rtp 2" e o canal pt.rtp2 existe na
            // baseline. Step 5 (KnownAlias) só corre se Step 3 falhar.
            Assert.Single(rtp2Sources);
            Assert.Equal("CanonicalExact", rtp2Sources[0].MatchMethod);
            Assert.Contains("rtp2", rtp2Sources[0].StreamUrl);

            // 4. ReviewItems
            var reviewItems = await ctx.ReviewItems.AsNoTracking().ToListAsync();
            Assert.Single(reviewItems);
            Assert.Equal("canal misterioso", reviewItems[0].NormalizedIdentity);
            Assert.Equal(ReviewItemState.Open, reviewItems[0].State);
            Assert.Equal("unknown-via-pipeline", reviewItems[0].ReasonSignature);
        }

        // 5. Publication
        var outputDir = Path.Combine(_root, "publish");
        Directory.CreateDirectory(outputDir);

        var publication = new RunPublicationService(
            outputDir,
            new PlaylistManagerService(),
            new ImportHistoryService(outputDir),
            countryValidator: null,                  // gate é diagnóstico, não bloqueante
            catalog: null,                           // SourceSelectionStage → NoOp(pass-through)
            dispatcharr: new DispatcharrSyncCoordinator(() => DispatcharrConfig.Disabled()),
            clock: () => _fixedLocal,
            utcClock: () => _fixedUtc);

        var pubResult = await publication.PublishAsync(new RunPublicationRequest
        {
            Streams = streams,
            Report = report,
            CountryCode = "pt",
            RunCountryGate = false,
            HistoryMode = "E2E",
        });

        Assert.True(File.Exists(pubResult.PlaylistPath),       $"playlist: {pubResult.PlaylistPath}");
        Assert.True(File.Exists(pubResult.JsonReportPath),     $"json:     {pubResult.JsonReportPath}");
        Assert.True(File.Exists(pubResult.RunReportPath),      $"run:      {pubResult.RunReportPath}");

        var playlistLines = await File.ReadAllLinesAsync(pubResult.PlaylistPath);
        var httpLines = playlistLines.Where(l => l.StartsWith("http://", StringComparison.Ordinal)).ToList();
        // Ver nota no summary do ficheiro: pass-through de unmatched é DIVERGENT
        // documentado; o teste apenas demonstra que a publicação funciona.
        Assert.Equal(3, httpLines.Count);

        // 6. RunReport invariantes — Status permanece "pending"
        //    (PublishAsync não muta RunReport.Status; terminalidade vive
        //    em LiveRuns.TerminalStatus, que este teste não exercita).
        Assert.Equal("pending", report.Status);
        Assert.NotNull(report.SourceSelection);

        // 7. Snapshot continua persistido e imutável (uma única row)
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var snaps = await ctx.RecognitionPolicySnapshots.AsNoTracking().ToListAsync();
            Assert.Single(snaps);
            Assert.Equal(RunId, snaps[0].RunId);
            Assert.Equal("rp1", snaps[0].ResolverVersion);
        }
    }
}
