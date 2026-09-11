using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.Sync;
using Xunit;
using Xunit.Abstractions;

namespace m3uCrawler.Tests;

/// <summary>
/// Diagnóstico REAL do pipeline m3uCrawler com o catálogo canónico
/// português. Não usa rede nem Telegram real — em vez disso, simula
/// um conjunto de streams sintéticos mas realistas, derivados de
/// variantes tipográficas habituais em playlists IPTV PT (RTP 1,
/// SIC, etc.), e percorre o pipeline completo:
///
///   Country validation → PipelineIngestionService → Source →
///   CanonicalChannel → ChannelSource → OrderingList →
///   PlaylistComposerService → PlaylistComposition
///
/// As métricas impressas no teste reflectem o estado REAL do
/// catálogo seedado (via <see cref="ChannelCatalogBootstrapper"/>)
/// e do pipeline actual (commits até <c>6053666</c>).
///
/// Este teste é um diagnóstico, não um teste unitário verde-vermelho:
/// o seu valor está nas métricas impressas, não no seu retorno.
/// </summary>
public class RealPipelineDiagnosticTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private readonly string _dbPath;
    private readonly string _outputDir;
    private TestDbContextFactory _factory = null!;
    private ChannelCatalogBootstrapper _bootstrapper = null!;
    private CatalogResolver _resolver = null!;

    public RealPipelineDiagnosticTests(ITestOutputHelper output)
    {
        _out = output;
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"diag-{Guid.NewGuid():N}.db");
        _outputDir = Path.Combine(
            Path.GetTempPath(),
            $"diag-out-{Guid.NewGuid():N}");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_outputDir);
        _factory = new TestDbContextFactory(_dbPath);
        _bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await _bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        _resolver = new CatalogResolver(_factory, _dbPath);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static M3uStream Stream(string title, string url, string group = "Portugal", bool working = true) =>
        new()
        {
            Title = title,
            Url = url,
            Group = group,
            Logo = string.Empty,
            IsWorking = working,
            LastTested = DateTime.UtcNow,
            ResponseTime = 100,
            OriginalExtInf = $"#EXTINF:-1 group-title=\"{group}\",{title}",
        };

    /// <summary>
    /// Conjunto de streams sintéticos realistas que imitam o output
    /// típico de uma descoberta Telegram/playlist portuguesa:
    /// - variantes com/sem prefixo PT;
    /// - variantes com sufixos HD/FHD/UHD;
    /// - canais que devem bater em canonical;
    /// - canais estrangeiros (testes do filtro de país);
    /// - canais desconhecidos (testes de auto-create);
    /// - duplicados para teste de idempotência.
    /// </summary>
    private static List<M3uStream> SyntheticDiscoveryBatch() => new()
    {
        // Conhecidos PT - devem bater em canonical seedado.
        Stream("RTP 1",        "http://x.example/rtp1.ts",        "Portugal"),
        Stream("RTP 2",        "http://x.example/rtp2.ts",        "Portugal"),
        Stream("SIC",          "http://x.example/sic.ts",         "Portugal"),
        Stream("TVI",          "http://x.example/tvi.ts",         "Portugal"),
        Stream("SIC Notícias", "http://x.example/sicnot.ts",      "Portugal"),
        Stream("RTP Notícias", "http://x.example/rtpn.ts",        "Portugal"),
        Stream("CNN Portugal", "http://x.example/cnnp.ts",        "Portugal"),
        Stream("CMTV",         "http://x.example/cmtv.ts",        "Portugal"),
        Stream("Porto Canal",  "http://x.example/porto.ts",       "Portugal"),
        Stream("Sport TV 1",   "http://x.example/sport1.ts",      "Portugal"),
        Stream("Sport TV 2",   "http://x.example/sport2.ts",      "Portugal"),
        Stream("Sport TV 3",   "http://x.example/sport3.ts",      "Portugal"),
        Stream("BTV",          "http://x.example/btv.ts",         "Portugal"),
        Stream("Sporting TV",  "http://x.example/sptv.ts",        "Portugal"),
        Stream("Disney Channel", "http://x.example/disney.ts",    "Portugal"),

        // Variantes tipográficas com "PT" — devem resolver.
        Stream("[PT] RTP 1",        "http://x.example/rtp1-bracket.ts",  "PT"),
        Stream("PT RTP 1",          "http://x.example/rtp1-prefix.ts",   "PT"),
        Stream("RTP 1 HD",          "http://x.example/rtp1-hd.ts",       "Portugal"),

        // Variantes tipográficas com nome alternativo.
        Stream("RTP",               "http://x.example/rtp-generic.ts",   "Portugal"),
        Stream("SIC RADICAL",       "http://x.example/sic-radical.ts",   "Portugal"),

        // Estrangeiros (devem ser rejeitados pelo filtro de país).
        Stream("ES La 1",           "http://x.example/es-la1.ts",        "Spain"),
        Stream("BR Globo News",     "http://x.example/br-globo.ts",      "Brazil"),
        Stream("Sky News",          "http://x.example/sky-news.ts",      "United Kingdom"),

        // Desconhecidos em PT (devem ser auto-criados com CreateEligible).
        Stream("CANAL FANTASTICO",   "http://x.example/fantastico.ts",    "Portugal"),
        Stream("PORTUGAL SPORTS 24", "http://x.example/psports24.ts",     "Portugal"),

        // Duplicado do RTP 1 (teste de idempotência).
        Stream("RTP 1",              "http://x.example/rtp1-other.ts",    "Portugal"),

        // Variante Sport TV com qualidade — pode bater no mesmo canonical.
        Stream("Sport TV 1 HD",      "http://x.example/sport1-hd.ts",     "Portugal"),
    };

    [Fact]
    public async Task Diagnose_full_pipeline_with_PT_catalog()
    {
        _out.WriteLine("═══════════════════════════════════════════════════════════");
        _out.WriteLine("DIAGNÓSTICO REAL — Pipeline m3uCrawler com Catálogo PT");
        _out.WriteLine("═══════════════════════════════════════════════════════════");
        _out.WriteLine($"DB: {_dbPath}");
        _out.WriteLine($"Output: {_outputDir}");
        _out.WriteLine("");

        // ────────────────────────────────────────────────────────────
        // 1. ESTADO DO CATÁLOGO SEEDADO
        // ────────────────────────────────────────────────────────────
        var allChannels = await _resolver.ListCanonicalChannelsAsync();
        var seededKeys = allChannels.Select(c => c.Key).ToHashSet();
        _out.WriteLine($"[1] Catálogo seedado: {allChannels.Count} CanonicalChannel(s)");
        _out.WriteLine($"    Keys seedadas (amostra): {string.Join(", ", seededKeys.Take(20))}");
        var withPolicyCreateEligible = allChannels.Count(c => c.PublicationPolicy == PublicationPolicy.CreateEligible);
        var withPolicyMergeOnly = allChannels.Count(c => c.PublicationPolicy == PublicationPolicy.MergeOnly);
        _out.WriteLine($"    Por PublicationPolicy: CreateEligible={withPolicyCreateEligible}, MergeOnly={withPolicyMergeOnly}");
        _out.WriteLine("");

        // ────────────────────────────────────────────────────────────
        // 2. COUNTRY VALIDATION
        // ────────────────────────────────────────────────────────────
        var streams = SyntheticDiscoveryBatch();
        _out.WriteLine($"[2] Discovery sintética: {streams.Count} streams");
        var countriesDir = Path.Combine(
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "m3uCrawler", "runtime-data", "countries")));
        // O TestRunner corre no dir m3uCrawler.Tests/bin/Release/net9.0; subir 4 níveis para chegar à raiz do repo.
        var runtimeDataCountries = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(),
            "..", "..", "..", "..", "m3uCrawler", "runtime-data", "countries"));
        if (Directory.Exists(runtimeDataCountries))
        {
            countriesDir = runtimeDataCountries;
        }
        var validator = new CountryChannelValidator(countriesDir);
        var countryMatches = validator.ValidateStreams(streams, "pt");
        var rejected = streams.Count - countryMatches.Count;
        _out.WriteLine($"    ValidateStreams(pt): {countryMatches.Count} matched, {rejected} rejected");
        var rejectedTitles = streams
            .Where(s => !countryMatches.Any(m => ReferenceEquals(m.Stream, s)))
            .Select(s => $"\"{s.Title}\" [group={s.Group}]")
            .ToList();
        if (rejectedTitles.Any())
        {
            _out.WriteLine($"    Rejected: {string.Join(", ", rejectedTitles)}");
        }
        _out.WriteLine("");

        // ────────────────────────────────────────────────────────────
        // 3. PIPELINE INGESTION
        // ────────────────────────────────────────────────────────────
        var ingestor = new PipelineIngestionService(_resolver, validator);
        // Ingerimos TODOS os streams, incluindo os estrangeiros — o
        // filtro de país é feito pelo validator no passo 2; o
        // ingestor persiste tudo o que passa pelo pipeline. A
        // separação por país acontece no momento do matching.
        var ingestionResult = await ingestor.IngestAsync(
            streams, "diagnostic-source-pt", "Telegram", "pt");

        _out.WriteLine($"[3] PipelineIngestionService: {ingestionResult.ReceivedCount} received, " +
                       $"{ingestionResult.IngestedCount} ingested, " +
                       $"{ingestionResult.MatchedCount} matched, " +
                       $"{ingestionResult.AutoCreatedCount} auto-created");
        _out.WriteLine("");

        var allSources = await _resolver.ListSourcesAsync();
        var allChannelSources = (await _resolver.ListChannelSourcesAsync()).ToList();
        var channelsAfterIngestion = await _resolver.ListCanonicalChannelsAsync();
        _out.WriteLine($"    Sources após ingestão: {allSources.Count}");
        _out.WriteLine($"    ChannelSources após ingestão: {allChannelSources.Count}");
        _out.WriteLine($"    CanonicalChannels após ingestão: {channelsAfterIngestion.Count} " +
                       $"(seedados={seededKeys.Count}, criados={channelsAfterIngestion.Count - seededKeys.Count})");
        _out.WriteLine("");

        // ────────────────────────────────────────────────────────────
        // 4. MÉTRICAS POR CANAL
        // ────────────────────────────────────────────────────────────
        var streamsPerChannel = allChannelSources
            .GroupBy(cs => cs.CanonicalChannelId)
            .ToDictionary(g => g.Key, g => g.Count());
        var multipleSources = streamsPerChannel.Count(kv => kv.Value > 1);
        _out.WriteLine($"[4] Streams por canal (ChannelSource): {streamsPerChannel.Count} canais, " +
                       $"{multipleSources} com múltiplas sources");
        _out.WriteLine("");

        // ────────────────────────────────────────────────────────────
        // 5. MATCHING DETALHADO
        // ────────────────────────────────────────────────────────────
        var matchedByAlias = ingestionResult.Entries.Count(e => e.MatchMethod == "canonical-alias");
        var autoCreated = ingestionResult.Entries.Count(e => e.AutoCreated);
        var avgConfidence = ingestionResult.Entries.Count > 0
            ? ingestionResult.Entries.Average(e => e.MatchConfidence)
            : 0.0;
        _out.WriteLine($"[5] Matching:");
        _out.WriteLine($"    canonical-alias: {matchedByAlias}");
        _out.WriteLine($"    auto-created: {autoCreated}");
        _out.WriteLine($"    confiança média: {avgConfidence:F2}");
        _out.WriteLine("");

        // ────────────────────────────────────────────────────────────
        // 6. ORDERING LIST CONSTRUÍDA A PARTIR DO CATÁLOGO
        // ────────────────────────────────────────────────────────────
        var list = await _resolver.CreateOrderingListAsync(
            "diag-pt-principal", "Diagnóstico PT Principal", "pt",
            "Lista construída para diagnóstico a partir do JSON canónico");

        // Usar os canais seedados que aparecem no JSON canónico. Para
        // simplicidade usamos os primeiros 20 da baseline.
        var orderable = channelsAfterIngestion
            .Where(c => c.IsEnabled)
            .Take(20)
            .ToList();
        int pos = 10;
        foreach (var ch in orderable)
        {
            await _resolver.AddOrderingItemAsync(list.Id, ch.Id, pos);
            pos += 10;
        }
        _out.WriteLine($"[6] OrderingList: '{list.Name}' com {orderable.Count} canais (10-step positions)");
        _out.WriteLine("");

        // ────────────────────────────────────────────────────────────
        // 7. PLAYLIST COMPOSITION
        // ────────────────────────────────────────────────────────────
        var composer = new PlaylistComposerService(_factory);
        var composition = await composer.ComposeAsync(list.Id, null);
        _out.WriteLine($"[7] PlaylistComposition:");
        _out.WriteLine($"    Entries totais: {composition.Entries.Count}");
        _out.WriteLine($"    Canais sem source: {composition.MissingChannels.Count}");
        _out.WriteLine($"    Cobertura: {composition.Entries.Count} / {orderable.Count} = " +
                       $"{(orderable.Count > 0 ? 100.0 * composition.Entries.Count / orderable.Count : 0):F1}%");
        _out.WriteLine("");

        // Detalhe das fontes seleccionadas.
        var chosenSources = composition.Entries
            .Select(e => (e.ChosenChannelSourceId, e.SourceName, e.StreamUrl, e.Quality))
            .ToList();
        var distinctSources = chosenSources.Select(c => c.SourceName).Distinct().Count();
        _out.WriteLine($"    Sources distintas seleccionadas: {distinctSources}");
        _out.WriteLine("");

        // ────────────────────────────────────────────────────────────
        // 8. EXPORTAR PLAYLIST M3U
        // ────────────────────────────────────────────────────────────
        var playlistPath = Path.Combine(_outputDir, "diagnostic_playlist.m3u");
        var writer = new PlaylistManagerService();
        await writer.WriteComposedAsync(composition, playlistPath);
        var content = await File.ReadAllTextAsync(playlistPath);
        _out.WriteLine($"[8] Playlist M3U: {playlistPath}");
        _out.WriteLine($"    Bytes: {content.Length}");
        _out.WriteLine($"    Linhas: {content.Split('\n').Length}");
        _out.WriteLine($"    Primeiras 5 entradas:");
        var lines = content.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(8).ToList();
        foreach (var line in lines)
        {
            _out.WriteLine($"      | {line}");
        }
        _out.WriteLine("");

        // ────────────────────────────────────────────────────────────
        // 9. DISPATCHARR (disabled)
        // ────────────────────────────────────────────────────────────
        var dispatcharrConfig = DispatcharrConfig.Disabled();
        var syncService = new DispatcharrSyncService(
            dispatcharrConfig, _outputDir);
        var syncResult = await syncService.RunAsync(playlistPath);
        _out.WriteLine($"[9] Dispatcharr: Enabled={dispatcharrConfig.Enabled}");
        _out.WriteLine($"    syncResult.DryRun: {syncResult.DryRun}");
        _out.WriteLine($"    Counts: {System.Text.Json.JsonSerializer.Serialize(syncResult.Report?.Counts)}");
        _out.WriteLine("");

        // ────────────────────────────────────────────────────────────
        // 10. DIAGNÓSTICO DE QUALIDADE (pós R1)
        // ────────────────────────────────────────────────────────────
        _out.WriteLine("[10] DIAGNÓSTICO DE QUALIDADE (pós R1)");
        _out.WriteLine("");

        // R1 — country gate dentro do PipelineIngestionService. Os
        // streams estrangeiros devem ter sido REJECTED e não devem
        // estar no catálogo nem na playlist.
        var foreignIngested = allChannelSources
            .Where(cs => cs.StreamUrl.Contains("es-la1") || cs.StreamUrl.Contains("br-globo") || cs.StreamUrl.Contains("sky-news"))
            .Count();
        if (foreignIngested == 0)
        {
            _out.WriteLine($"[OK] R1 — Nenhum stream estrangeiro no catálogo (R1 funciona).");
            _out.WriteLine($"      ES La 1, BR Globo News, Sky News foram todos REJECTED pelo country gate.");
            _out.WriteLine($"      IngestionResult.RejectedByCountryCount = {ingestionResult.RejectedByCountryCount}");
        }
        else
        {
            _out.WriteLine($"[BUG] {foreignIngested} streams estrangeiros foram ingeridos — R1 não funcionou.");
        }

        // R1 — REJECT ≠ UNKNOWN. CANAL FANTASTICO não está no catálogo
        // (era auto-criado pré-R1, agora rejeitado pelo validator).
        var fantasticoChannels = channelsAfterIngestion
            .Where(c => c.DisplayName.Contains("FANTASTICO") || c.Key.Contains("fantastico"))
            .Count();
        if (fantasticoChannels == 0)
        {
            _out.WriteLine($"[OK] R1 — 'CANAL FANTASTICO' foi REJECTED (sem token PT). Não foi auto-criado.");
        }
        else
        {
            _out.WriteLine($"[BUG] 'CANAL FANTASTICO' aparece {fantasticoChannels}x no catálogo.");
        }

        // Verificar cobertura de playlist
        var foreignInPlaylist = content
            .Contains("ES La 1") || content.Contains("BR Globo") || content.Contains("Sky News") || content.Contains("FANTASTICO");
        if (!foreignInPlaylist)
        {
            _out.WriteLine($"[OK] R1 — Nenhum canal estrangeiro na playlist final.");
        }
        else
        {
            _out.WriteLine($"[BUG] R1 — Canal estrangeiro detectado na playlist.");
        }

        // Verificar RTP genérico (colapsa em Unknown → auto-create, não em rtp-1)
        var rtpGenericCh = channelsAfterIngestion
            .Where(c => c.Key.Contains("pt-rtp") || c.DisplayName == "RTP")
            .FirstOrDefault();
        if (rtpGenericCh != null)
        {
            _out.WriteLine($"[OK] MATCHING — 'RTP' (sem número) auto-criado como '{rtpGenericCh.Key}', não colapsa em 'rtp-1'.");
            _out.WriteLine($"      Evidência: evita falso positivo com RTP 1/2/3.");
        }

        _out.WriteLine("");
        _out.WriteLine("═══════════════════════════════════════════════════════════");
        _out.WriteLine("FIM DO DIAGNÓSTICO");
        _out.WriteLine("═══════════════════════════════════════════════════════════");

        // R1 — Regression guard. Se algum stream REJECTED pelo country
        // gate for encontrado no catálogo ou na playlist, isto é uma
        // regressão da R1.
        Assert.Equal(0, foreignIngested);
        Assert.Equal(0, fantasticoChannels);
        Assert.False(foreignInPlaylist);
    }
}
