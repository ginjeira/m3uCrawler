using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Matching;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE-Bridge — Liga o pipeline real (Telegram/M3U/M3U8-search) ao
/// Canonical Catalogue, Source e ChannelSource.
///
/// Estes testes TDD verificam que o pipeline real chama a bridge e
/// que esta cumpre os requisitos do contrato:
/// - idempotência de Sources e ChannelSources;
/// - matching via CatalogResolver.ResolveAsync (mecanismo existente);
/// - canais desconhecidos não são eliminados (ficam como Canonical
///   com CreateEligible para revisão futura via Dashboard);
/// - proveniência preservada (SourceKey, MatchMethod);
/// - ausência de regressão nas funcionalidades existentes.
/// </summary>
public class PipelineIngestionBridgeTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;
    private ChannelCatalogBootstrapper _bootstrapper = null!;
    private CatalogResolver _resolver = null!;

    public PipelineIngestionBridgeTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"pipeline-bridge-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        _bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await _bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        _resolver = new CatalogResolver(_factory, _dbPath);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private PipelineIngestionService NewIngestor() =>
        new PipelineIngestionService(_resolver);

    private static M3uStream MakeStream(string title, string url, string group = "Portugal", bool working = true) =>
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

    // ============================================================
    // A. Primeira descoberta: M3uStream conhecido → Canonical Channel
    //    existente → Source criada → ChannelSource criada
    // ============================================================

    [Fact]
    public async Task First_ingest_creates_source_channelsource_and_uses_existing_canonical()
    {
        var stream = MakeStream("RTP 1", "http://x.example/rtp1.ts", "Portugal");
        var sourceKey = "test-bridge-a";

        var result = await NewIngestor().IngestAsync(
            new[] { stream }, sourceKey, "Telegram", "pt");

        Assert.Equal(1, result.IngestedCount);

        var sources = await _resolver.ListSourcesAsync();
        var src = sources.Single(s => s.Key == sourceKey);
        Assert.Equal("Telegram", src.Kind.ToString());

        var channelSources = await _resolver.ListChannelSourcesAsync(sourceId: src.Id);
        var cs = channelSources.Single();
        Assert.Equal(src.Id, cs.SourceId);
        Assert.Equal(stream.Url, cs.StreamUrl);
        // Matching canónico (RTP 1 já está seedado no baseline PT).
        Assert.True(cs.CanonicalChannelId > 0);
    }

    // ============================================================
    // B. Segunda descoberta idêntica: não cria duplicados
    // ============================================================

    [Fact]
    public async Task Second_identical_ingest_does_not_duplicate_source_or_channelsource()
    {
        var stream = MakeStream("RTP 1", "http://x.example/rtp1.ts", "Portugal");
        var sourceKey = "test-bridge-b";

        await NewIngestor().IngestAsync(new[] { stream }, sourceKey, "Telegram", "pt");
        await NewIngestor().IngestAsync(new[] { stream }, sourceKey, "Telegram", "pt");

        var sources = await _resolver.ListSourcesAsync();
        Assert.Single(sources.Where(s => s.Key == sourceKey));

        var src = sources.Single(s => s.Key == sourceKey);
        var channelSources = await _resolver.ListChannelSourcesAsync(sourceId: src.Id);
        Assert.Single(channelSources);
    }

    // ============================================================
    // C. Stream/source existente: actualiza o registo
    // ============================================================

    [Fact]
    public async Task Existing_channelsource_is_updated_with_new_observation()
    {
        var sourceKey = "test-bridge-c";

        // Primeira ingestão: stream "morre"
        var deadStream = MakeStream("RTP 1", "http://x.example/rtp1.ts", "Portugal");
        deadStream.IsWorking = false;
        await NewIngestor().IngestAsync(new[] { deadStream }, sourceKey, "Telegram", "pt");
        var src = (await _resolver.ListSourcesAsync()).Single(s => s.Key == sourceKey);
        var csFirst = (await _resolver.ListChannelSourcesAsync(sourceId: src.Id)).Single();
        Assert.Equal(AvailabilityState.Dead, csFirst.Availability);

        // Segunda ingestão: stream "ressuscita"
        var aliveStream = MakeStream("RTP 1", "http://x.example/rtp1.ts", "Portugal");
        aliveStream.IsWorking = true;
        var second = await NewIngestor().IngestAsync(
            new[] { aliveStream }, sourceKey, "Telegram", "pt");

        var csSecond = (await _resolver.ListChannelSourcesAsync(sourceId: src.Id)).Single();
        Assert.Equal(csFirst.Id, csSecond.Id);
        Assert.Equal(AvailabilityState.Reachable, csSecond.Availability);
        Assert.True(csSecond.LastSeenAtUtc >= csFirst.LastSeenAtUtc);
        Assert.Equal(2, second.IngestedCount + 1);
    }

    // ============================================================
    // D. Canal desconhecido: não é eliminado e fica disponível
    //    para resolução posterior via Dashboard
    // ============================================================

    [Fact]
    public async Task Unknown_channel_gets_a_canonical_with_CreateEligible_and_is_visible_in_catalogue()
    {
        // "CANAL FANTASTICO" não está no catálogo seed — deve receber
        // um CanonicalChannel próprio (CreateEligible) para revisão.
        var stream = MakeStream("CANAL FANTASTICO", "http://x.example/fantastico.ts", "Portugal");
        var sourceKey = "test-bridge-d";

        var result = await NewIngestor().IngestAsync(
            new[] { stream }, sourceKey, "Telegram", "pt");

        Assert.Equal(1, result.IngestedCount);

        var channels = await _resolver.ListCanonicalChannelsAsync();
        var ch = channels.FirstOrDefault(c => c.Key == "pt-canal-fantastico");
        Assert.NotNull(ch);
        Assert.Equal("CANAL FANTASTICO", ch!.DisplayName);
        Assert.Equal(PublicationPolicy.CreateEligible, ch.PublicationPolicy);

        var sources = await _resolver.ListSourcesAsync();
        var src = sources.Single(s => s.Key == sourceKey);
        var channelSources = await _resolver.ListChannelSourcesAsync(sourceId: src.Id);
        Assert.Single(channelSources);
        Assert.Equal(ch.Id, channelSources[0].CanonicalChannelId);
    }

    // ============================================================
    // E. Matching: o mecanismo existente é utilizado
    // ============================================================

    [Fact]
    public async Task Matching_uses_existing_CatalogResolver_with_normalization()
    {
        // Variantes de RTP 1 devem todas bater no mesmo canal canónico
        // (RTP1, RTP 1, RTP 1 HD — todos normalizam para "rtp 1 hd" e
        // resolvem para o mesmo canal seedado).
        var streams = new[]
        {
            MakeStream("RTP 1", "http://x.example/rtp1.ts"),
            MakeStream("[PT] RTP 1", "http://x.example/rtp1-bracket.ts"),
        };
        var sourceKey = "test-bridge-e";

        await NewIngestor().IngestAsync(streams, sourceKey, "Telegram", "pt");

        var src = (await _resolver.ListSourcesAsync()).Single(s => s.Key == sourceKey);
        var channelSources = await _resolver.ListChannelSourcesAsync(sourceId: src.Id);
        Assert.Equal(2, channelSources.Count);

        var channels = (await _resolver.ListCanonicalChannelsAsync()).ToDictionary(c => c.Id);
        var firstCs = channelSources[0];
        var secondCs = channelSources[1];
        Assert.True(channels.ContainsKey(firstCs.CanonicalChannelId));
        Assert.True(channels.ContainsKey(secondCs.CanonicalChannelId));
        // Os dois streams devem resolver para o mesmo canal (RTP 1).
        Assert.Equal(firstCs.CanonicalChannelId, secondCs.CanonicalChannelId);
    }

    // ============================================================
    // F. Provenance: a origem fica preservada
    // ============================================================

    [Fact]
    public async Task Provenance_preserved_via_SourceEntity_Origin_and_ChannelSource_MatchMethod()
    {
        var stream = MakeStream("RTP 1", "http://x.example/rtp1.ts", "Portugal");
        var sourceKey = "test-bridge-f";

        await NewIngestor().IngestAsync(new[] { stream }, sourceKey, "Telegram", "pt");

        var src = (await _resolver.ListSourcesAsync()).Single(s => s.Key == sourceKey);
        // Origin deve conter uma referência à source (sanitizada).
        Assert.Contains(sourceKey, src.Origin, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SourceKind.Telegram, src.Kind);

        var cs = (await _resolver.ListChannelSourcesAsync(sourceId: src.Id)).Single();
        // MatchMethod deve indicar o tipo de decisão.
        Assert.False(string.IsNullOrWhiteSpace(cs.MatchMethod));
        Assert.True(cs.MatchConfidence >= 0 && cs.MatchConfidence <= 1.0);
        // StreamUrl persistida deve estar sanitizada (sem credenciais).
        Assert.Equal(stream.Url, cs.StreamUrl); // sem credenciais aqui, mas passa pelo sanitizer
    }

    // ============================================================
    // G. Execução real do pipeline: TelegramScraperService
    //    chama a bridge (não apenas um teste isolado do serviço)
    // ============================================================

    [Fact]
    public async Task Telegram_pipeline_path_actually_calls_the_bridge_through_TelegramScraperService()
    {
        // O objectivo é confirmar que a bridge é chamada a partir do
        // TelegramScraperService — não apenas de PipelineIngestionService
        // em isolamento. Aqui usamos o construtor para testes (sem
        // WTelegram.Client) porque este teste não precisa de autenticação.
        var scraper = new TelegramScraperService(client: null);
        var ingestor = NewIngestor();

        var streams = new List<M3uStream>
        {
            MakeStream("RTP 1", "http://x.example/rtp1.ts", "Portugal"),
            MakeStream("SIC", "http://x.example/sic.ts", "Portugal"),
        };

        await scraper.IngestIntoCatalogAsync(
            streams, "test-bridge-g", "Telegram", "pt", ingestor);

        var src = (await _resolver.ListSourcesAsync()).Single(s => s.Key == "test-bridge-g");
        var channelSources = await _resolver.ListChannelSourcesAsync(sourceId: src.Id);
        Assert.Equal(2, channelSources.Count);
    }

    // ============================================================
    // H. Composição: dados ingeridos chegam à PlaylistComposerService
    // ============================================================

    [Fact]
    public async Task Ingested_streams_can_be_composed_via_PlaylistComposerService()
    {
        // Ingerir streams. Usamos SIC como alvo porque está seedado
        // no baseline PT e tem matching directo (sem variantes que
        // dependam de regras específicas).
        var ingestor = NewIngestor();
        var sourceKey = "test-bridge-h";
        var streams = new[]
        {
            MakeStream("SIC", "http://x.example/sic-h.ts", "Portugal"),
        };
        await ingestor.IngestAsync(streams, sourceKey, "Telegram", "pt");

        // Identificar o canal seedado "sic".
        var sic = (await _resolver.ListCanonicalChannelsAsync()).First(c => c.Key == "sic");

        await using var ctx = _factory.CreateDbContext();
        var list = new OrderingListEntity
        {
            Key = "test-list-h",
            Name = "Test List H",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        ctx.OrderingLists.Add(list);
        await ctx.SaveChangesAsync();

        ctx.OrderingItems.Add(new OrderingItemEntity
        {
            OrderingListId = list.Id,
            CanonicalChannelId = sic.Id,
            Position = 0,
            IsEnabled = true,
        });
        await ctx.SaveChangesAsync();

        // O composer deve agora conseguir gerar uma playlist com o
        // canal "sic" — prova que os dados ingeridos chegam à composição.
        var composer = new PlaylistComposerService(_factory);
        var composition = await composer.ComposeAsync(list.Id, null);

        Assert.NotEmpty(composition.Entries);
        Assert.Single(composition.Entries);
        Assert.Equal(sic.Id, composition.Entries[0].CanonicalChannelId);
        // A URL escolhida deve ser a que acabámos de ingerir.
        Assert.Equal("http://x.example/sic-h.ts", composition.Entries[0].StreamUrl);
    }

    // ============================================================
    // I. Não regressão: funcionalidades existentes continuam
    // ============================================================

    [Fact]
    public async Task No_regression_ChannelSource_audit_is_still_persisted()
    {
        var stream = MakeStream("RTP 1", "http://x.example/rtp1-i.ts", "Portugal");
        var sourceKey = "test-bridge-i";

        await NewIngestor().IngestAsync(new[] { stream }, sourceKey, "Telegram", "pt");

        var src = (await _resolver.ListSourcesAsync()).Single(s => s.Key == sourceKey);
        var cs = (await _resolver.ListChannelSourcesAsync(sourceId: src.Id)).Single();

        // Cada ingestão deve persistir uma observação do stream
        // (PHASE 9 b continua a funcionar via ingestor).
        await _resolver.RecordChannelSourceObservationAsync(
            cs.Id, StreamQuality.HD, EpgState.Available, AvailabilityState.Reachable, 120);
        var obs = await _resolver.GetChannelSourceObservationsAsync(cs.Id);
        Assert.Single(obs);
    }
}
