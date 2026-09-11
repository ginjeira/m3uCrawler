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
/// PHASE-Bridge — R1 (correcção): garante que um stream REJECTED pela
/// política de país (via <see cref="CountryChannelValidator"/>) NUNCA
/// é persistido como CanonicalChannel/ChannelSource pela
/// <see cref="PipelineIngestionService"/>.
///
/// Estes testes são a fronteira de segurança: se algum deles passar
/// enquanto outro falha, há inconsistência arquitectural. Se o
/// construtor sem validator voltar a permitir ingestão silenciosa,
/// isto é uma regressão.
/// </summary>
public class PipelineIngestionCountryGateTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;
    private ChannelCatalogBootstrapper _bootstrapper = null!;
    private CatalogResolver _resolver = null!;

    public PipelineIngestionCountryGateTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"r1-{Guid.NewGuid():N}.db");
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

    private static string CountriesDir()
    {
        var cwd = Directory.GetCurrentDirectory();
        // cwd = m3uCrawler.Tests/bin/Release/net9.0; subir até à raiz do repo.
        var repoRoot = Path.GetFullPath(Path.Combine(cwd, "..", "..", "..", ".."));
        var path = Path.Combine(repoRoot, "m3uCrawler", "runtime-data", "countries");
        return path;
    }

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

    // ════════════════════════════════════════════════════════════════
    // Construtor sem validator → ingestion falha (nunca passa silenciosa)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Ingest_without_country_validator_throws_and_does_not_persist_anything()
    {
        // O construtor sem validator é agora proibido. A excepção é
        // lançada imediatamente, não em runtime durante IngestAsync.
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            Task.Run(() => new PipelineIngestionService(_resolver, null!)));

        // Catálogo permanece inalterado.
        var sources = await _resolver.ListSourcesAsync();
        var channelSources = await _resolver.ListChannelSourcesAsync();
        Assert.Empty(sources.Where(s => s.Key == "r1-no-gate"));
        Assert.Empty(channelSources);
    }

    // ════════════════════════════════════════════════════════════════
    // A. Stream estrangeiro não é ingerido numa ingestion PT
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Foreign_stream_is_not_ingested_in_PT_ingestion()
    {
        var validator = new CountryChannelValidator(CountriesDir());
        var ingestor = new PipelineIngestionService(_resolver, validator);
        var stream = Stream("ES La 1", "http://x.example/es-la1.ts", "Spain");

        var result = await ingestor.IngestAsync(
            new[] { stream }, "r1-a", "Telegram", "pt");

        Assert.Equal(0, result.IngestedCount);
        Assert.Equal(0, result.MatchedCount);
        Assert.Equal(0, result.AutoCreatedCount);
        Assert.Equal(1, result.RejectedByCountryCount);

        var sources = await _resolver.ListSourcesAsync();
        var src = sources.FirstOrDefault(s => s.Key == "r1-a");
        // A Source pode ser criada (registando a tentativa), mas
        // sem ChannelSources — o stream foi REJECTED.
        if (src != null)
        {
            var channelSources = await _resolver.ListChannelSourcesAsync(sourceId: src.Id);
            Assert.Empty(channelSources);
        }
    }

    // ════════════════════════════════════════════════════════════════
    // B. ES La 1 não cria CanonicalChannel
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ES_La_1_does_not_create_CanonicalChannel()
    {
        var validator = new CountryChannelValidator(CountriesDir());
        var ingestor = new PipelineIngestionService(_resolver, validator);

        await ingestor.IngestAsync(
            new[] { Stream("ES La 1", "http://x.example/es-la1.ts", "Spain") },
            "r1-b", "Telegram", "pt");

        var channels = await _resolver.ListCanonicalChannelsAsync();
        Assert.DoesNotContain(channels, c => c.Key.Contains("la-1") || c.DisplayName == "ES La 1");
    }

    // ════════════════════════════════════════════════════════════════
    // C. BR Globo News não cria CanonicalChannel nem ChannelSource
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BR_Globo_News_creates_neither_CanonicalChannel_nor_ChannelSource()
    {
        var validator = new CountryChannelValidator(CountriesDir());
        var ingestor = new PipelineIngestionService(_resolver, validator);

        var channelsBefore = (await _resolver.ListCanonicalChannelsAsync()).Count;

        await ingestor.IngestAsync(
            new[] { Stream("BR Globo News", "http://x.example/br-globo.ts", "Brazil") },
            "r1-c", "Telegram", "pt");

        var channels = await _resolver.ListCanonicalChannelsAsync();
        // Não deve ter criado nenhum CanonicalChannel adicional durante
        // esta ingestão. O catálogo seedado já contém "Globo" como
        // canal PT (da baseline JSON), mas nenhum canal NOVO deve
        // aparecer com "BR Globo News" como DisplayName ou key
        // contendo "br-globo-news".
        Assert.Equal(channelsBefore, channels.Count);
        Assert.DoesNotContain(channels, c => c.Key.Contains("br-globo-news") || c.DisplayName == "BR Globo News");

        var sources = await _resolver.ListSourcesAsync();
        var src = sources.FirstOrDefault(s => s.Key == "r1-c");
        if (src != null)
        {
            var channelSources = await _resolver.ListChannelSourcesAsync(sourceId: src.Id);
            Assert.Empty(channelSources);
        }
    }

    // ════════════════════════════════════════════════════════════════
    // D. Sky News não cria CanonicalChannel nem ChannelSource
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sky_News_creates_neither_CanonicalChannel_nor_ChannelSource()
    {
        var validator = new CountryChannelValidator(CountriesDir());
        var ingestor = new PipelineIngestionService(_resolver, validator);

        await ingestor.IngestAsync(
            new[] { Stream("Sky News", "http://x.example/sky-news.ts", "United Kingdom") },
            "r1-d", "Telegram", "pt");

        var channels = await _resolver.ListCanonicalChannelsAsync();
        Assert.DoesNotContain(channels, c => c.DisplayName == "Sky News");

        var sources = await _resolver.ListSourcesAsync();
        var src = sources.FirstOrDefault(s => s.Key == "r1-d");
        if (src != null)
        {
            var channelSources = await _resolver.ListChannelSourcesAsync(sourceId: src.Id);
            Assert.Empty(channelSources);
        }
    }

    // ════════════════════════════════════════════════════════════════
    // E. CANAL FANTASTICO, rejeitado, não é auto-criado
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Canal_Fantastico_rejected_by_country_policy_is_not_auto_created()
    {
        var validator = new CountryChannelValidator(CountriesDir());
        var ingestor = new PipelineIngestionService(_resolver, validator);

        // O título "CANAL FANTASTICO" não tem token PT, group=Portugal
        // não tem token PT no título, e o título não bate em nenhum
        // alias PT. ValidateStreams rejeita-o.
        await ingestor.IngestAsync(
            new[] { Stream("CANAL FANTASTICO", "http://x.example/fantastico.ts", "Portugal") },
            "r1-e", "Telegram", "pt");

        var channels = await _resolver.ListCanonicalChannelsAsync();
        Assert.DoesNotContain(channels, c => c.Key.Contains("fantastico"));
    }

    // ════════════════════════════════════════════════════════════════
    // F. Stream PT conhecido continua a ser ingerido normalmente
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Known_PT_stream_is_still_ingested_normally()
    {
        var validator = new CountryChannelValidator(CountriesDir());
        var ingestor = new PipelineIngestionService(_resolver, validator);

        var result = await ingestor.IngestAsync(
            new[] { Stream("RTP 1", "http://x.example/rtp1.ts", "Portugal") },
            "r1-f", "Telegram", "pt");

        Assert.Equal(1, result.IngestedCount);
        Assert.Equal(1, result.MatchedCount);
        Assert.Equal(0, result.AutoCreatedCount);

        var src = (await _resolver.ListSourcesAsync()).Single(s => s.Key == "r1-f");
        var channelSources = await _resolver.ListChannelSourcesAsync(sourceId: src.Id);
        Assert.Single(channelSources);
    }

    // ════════════════════════════════════════════════════════════════
    // G. Stream PT desconhecido continua a resultar em CreateEligible
    //    (mas só se passar pelo country gate)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Unknown_PT_stream_with_country_token_still_results_in_CreateEligible()
    {
        var validator = new CountryChannelValidator(CountriesDir());
        var ingestor = new PipelineIngestionService(_resolver, validator);

        // "PT Canal Mistério" tem token PT no título, vai passar pelo
        // gate, e cai em Unknown → auto-create CreateEligible.
        var stream = Stream("PT Canal Misterio", "http://x.example/misterio.ts", "Portugal");
        var result = await ingestor.IngestAsync(
            new[] { stream }, "r1-g", "Telegram", "pt");

        Assert.Equal(1, result.IngestedCount);
        Assert.Equal(0, result.MatchedCount);
        Assert.Equal(1, result.AutoCreatedCount);

        var channels = await _resolver.ListCanonicalChannelsAsync();
        Assert.Contains(channels, c => c.Key.Contains("canal-misterio") && c.PublicationPolicy == PublicationPolicy.CreateEligible);
    }

    // ════════════════════════════════════════════════════════════════
    // H. Segunda ingestão continua idempotente
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Second_identical_PT_ingestion_is_idempotent()
    {
        var validator = new CountryChannelValidator(CountriesDir());
        var ingestor = new PipelineIngestionService(_resolver, validator);
        var streams = new[] { Stream("SIC", "http://x.example/sic.ts", "Portugal") };

        await ingestor.IngestAsync(streams, "r1-h", "Telegram", "pt");
        await ingestor.IngestAsync(streams, "r1-h", "Telegram", "pt");

        var src = (await _resolver.ListSourcesAsync()).Single(s => s.Key == "r1-h");
        var channelSources = await _resolver.ListChannelSourcesAsync(sourceId: src.Id);
        Assert.Single(channelSources);
    }

    // ════════════════════════════════════════════════════════════════
    // I. Mistura: alguns PT passam, outros estrangeiros são rejeitados
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Mixed_batch_only_PT_streams_are_ingested()
    {
        var validator = new CountryChannelValidator(CountriesDir());
        var ingestor = new PipelineIngestionService(_resolver, validator);

        var batch = new[]
        {
            Stream("RTP 1",       "http://x.example/rtp1.ts",  "Portugal"),  // OK
            Stream("ES La 1",     "http://x.example/es-la1.ts", "Spain"),    // REJECT
            Stream("SIC",         "http://x.example/sic.ts",    "Portugal"), // OK
            Stream("Sky News",    "http://x.example/sky.ts",    "United Kingdom"), // REJECT
            Stream("BR Globo News","http://x.example/globo.ts", "Brazil"),   // REJECT
        };

        var result = await ingestor.IngestAsync(batch, "r1-i", "Telegram", "pt");

        Assert.Equal(2, result.IngestedCount);
        Assert.Equal(2, result.MatchedCount);

        var src = (await _resolver.ListSourcesAsync()).Single(s => s.Key == "r1-i");
        var channelSources = await _resolver.ListChannelSourcesAsync(sourceId: src.Id);
        Assert.Equal(2, channelSources.Count);

        var channels = await _resolver.ListCanonicalChannelsAsync();
        Assert.DoesNotContain(channels, c => c.DisplayName == "ES La 1");
        Assert.DoesNotContain(channels, c => c.DisplayName == "Sky News");
        Assert.DoesNotContain(channels, c => c.DisplayName == "BR Globo News");
    }

    // ════════════════════════════════════════════════════════════════
    // J. Caller sem contexto → REJECT (não ingere silenciosamente)
    //    Isto é J. da lista do utilizador: outros callers sem contexto
    //    devem ter comportamento explícito.
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Caller_without_country_validator_cannot_ingest_anything()
    {
        // Este teste documenta o comportamento explícito: callers sem
        // country validator não podem construir o PipelineIngestionService.
        // A excepção é lançada no construtor, não em runtime — isto é
        // fail-fast (não há janela em que a ingestão pudesse ocorrer
        // sem gate).
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            Task.Run(() => new PipelineIngestionService(_resolver, null!)));
    }

    // ════════════════════════════════════════════════════════════════
    // REJECT ≠ UNKNOWN (boundary explícito)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Reject_is_never_converted_to_Unknown()
    {
        var validator = new CountryChannelValidator(CountriesDir());
        var ingestor = new PipelineIngestionService(_resolver, validator);

        // Stream rejeitado pelo country gate (sem token PT, sem alias
        // PT no título, sem group-token PT). ValidateStreams descarta-o.
        var stream = Stream("Random Channel XYZ", "http://x.example/xyz.ts", "Unknown");

        var result = await ingestor.IngestAsync(
            new[] { stream }, "r1-reject-unknown", "Telegram", "pt");

        // O stream NÃO conta como Ingested, Matched, AutoCreated.
        Assert.Equal(0, result.IngestedCount);
        Assert.Equal(0, result.MatchedCount);
        Assert.Equal(0, result.AutoCreatedCount);

        var channels = await _resolver.ListCanonicalChannelsAsync();
        Assert.DoesNotContain(channels, c => c.DisplayName == "Random Channel XYZ");
    }
}
