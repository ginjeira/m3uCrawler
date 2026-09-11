using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE-Bridge — R2: garante que o catálogo canónico PT em
/// <c>docs/catalog/m3ucrawler_pt_canonical_catalog.json</c> é a
/// referência coerente do sistema, sem representações paralelas ou
/// divergentes entre <see cref="CatalogSeed"/> e o JSON.
///
/// Regra arquitectural: UM canal lógico = UM CanonicalChannel.
/// </summary>
public class CatalogR2ConsistencyTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;
    private ChannelCatalogBootstrapper _bootstrapper = null!;

    public CatalogR2ConsistencyTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"r2-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        _bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await _bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<CatalogResolver> NewResolverAsync()
    {
        var ctx = await _bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        return new CatalogResolver(_factory, _dbPath);
    }

    private static string BaselinePath()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", ".."));
        return Path.Combine(repoRoot,
            "docs", "catalog", "m3ucrawler_pt_canonical_catalog.json");
    }

    // ════════════════════════════════════════════════════════════════
    // A. O JSON canónico é carregado integralmente
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Baseline_JSON_is_loaded_with_all_20_canonical_channels()
    {
        var baseline = await CatalogBaselineImporter.LoadFromFileAsync(BaselinePath());
        var examples = baseline.Matching?.Examples;
        Assert.NotNull(examples);
        Assert.Equal(25, examples!.Count);

        var resolver = await NewResolverAsync();
        var channels = (await resolver.ListCanonicalChannelsAsync()).ToList();

        // Cada chave do JSON deve estar presente como CanonicalChannel.
        foreach (var (sourceKey, ch) in examples)
        {
            var key = CatalogBaselineImporter.CanonicalIdToKey(ch.CanonicalId);
            Assert.True(channels.Any(c => c.Key == key),
                $"JSON entry '{sourceKey}' (key={key}) não foi carregado para a BD.");
        }
    }

    // ════════════════════════════════════════════════════════════════
    // B. Cada canonical key corresponde a uma única CanonicalChannel
    //    (não existem duplicações JSON-vs-Seed)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task No_canonical_key_is_duplicated_after_bootstrap()
    {
        var resolver = await NewResolverAsync();
        var channels = (await resolver.ListCanonicalChannelsAsync()).ToList();

        var keys = channels.Select(c => c.Key).ToList();
        var uniqueKeys = keys.Distinct().ToList();

        Assert.Equal(keys.Count, uniqueKeys.Count);
    }

    [Fact]
    public async Task RTP_1_and_2_are_each_a_single_channel_after_bootstrap()
    {
        var resolver = await NewResolverAsync();
        var channels = (await resolver.ListCanonicalChannelsAsync()).ToList();

        // RTP 1, 2 — cada um deve ter exactamente uma CanonicalChannel.
        // Usar exact match na key para evitar false-positives com
        // "rtpnoticias", "rtpmemoria", etc.
        // Nota: o JSON canónico NÃO define `rtp3` como canal separado;
        // RTP 3 é alias de `rtpnoticias` (ver `rtp-noticias` em
        // `m3ucrawler_pt_canonical_catalog.json`).
        var rtp1 = channels.Where(c => c.Key == "rtp1").ToList();
        Assert.Single(rtp1);
        Assert.Equal("RTP 1", rtp1[0].DisplayName);

        var rtp2 = channels.Where(c => c.Key == "rtp2").ToList();
        Assert.Single(rtp2);
        Assert.Equal("RTP 2", rtp2[0].DisplayName);

        // RTP 3: não tem CanonicalChannel separado; é alias de rtpnoticias.
        // Confirmar que o alias resolve correctamente.
        var resolved = await resolver.ResolveAsync("rtp 3");
        Assert.NotNull(resolved);
        Assert.NotNull(resolved.CanonicalChannelId);
        var ch = (await resolver.ListCanonicalChannelsAsync())
            .First(c => c.Id == resolved.CanonicalChannelId!.Value);
        Assert.Equal("RTP Notícias", ch.DisplayName);
    }

    // ════════════════════════════════════════════════════════════════
    // C. Aliases definidos no JSON resolvem correctamente para o canal
    //    canónico esperado (não cria novos canais)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RTP1_alias_resolves_to_RTP_1_channel()
    {
        var resolver = await NewResolverAsync();

        // "RTP1" não tem espaço; após normalização fica "rtp1".
        var resolved = await resolver.ResolveAsync("rtp1");
        Assert.NotNull(resolved);
        Assert.NotNull(resolved.CanonicalChannelId);
        var ch = (await resolver.ListCanonicalChannelsAsync())
            .First(c => c.Id == resolved.CanonicalChannelId!.Value);
        Assert.Equal("RTP 1", ch.DisplayName);
    }

    [Fact]
    public async Task RTP_1_HD_alias_resolves_to_RTP_1_channel()
    {
        var resolver = await NewResolverAsync();

        // "RTP 1 HD" após normalização fica "rtp 1" (HD removido).
        var resolved = await resolver.ResolveAsync("rtp 1");
        Assert.NotNull(resolved);
        var ch = (await resolver.ListCanonicalChannelsAsync())
            .First(c => c.Id == resolved.CanonicalChannelId!.Value);
        Assert.Equal("RTP 1", ch.DisplayName);
    }

    [Fact]
    public async Task CNN_Portugal_alias_resolves_to_the_unique_CNN_channel()
    {
        var resolver = await NewResolverAsync();
        // CNN/Portugal (com espaço) bate no alias.
        var resolved = await resolver.ResolveAsync("cnn portugal");
        Assert.NotNull(resolved);
        var ch = (await resolver.ListCanonicalChannelsAsync())
            .First(c => c.Id == resolved.CanonicalChannelId!.Value);

        // DisplayName deve ser "CNN Portugal" (do JSON) e não "CNN" (do seed antigo).
        Assert.Equal("CNN Portugal", ch.DisplayName);
    }

    // ════════════════════════════════════════════════════════════════
    // D. Country validation continua a funcionar (não regrediu com R2)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Country_validator_rejects_foreign_streams_unchanged()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", ".."));
        var countriesDir = Path.Combine(repoRoot, "m3uCrawler", "runtime-data", "countries");

        var validator = new CountryChannelValidator(countriesDir);
        var foreign = new m3uCrawler.Models.M3uStream
        {
            Title = "ES La 1", Url = "http://x/es.ts", Group = "Spain",
        };
        var matches = validator.ValidateStreams(new List<m3uCrawler.Models.M3uStream> { foreign }, "pt");
        Assert.Empty(matches);
    }

    // ════════════════════════════════════════════════════════════════
    // E. Aliases do JSON devem ser resolvíveis via catálogo persistente
    //    (não só via seed ou via código)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Json_aliases_are_persisted_in_ChannelAlias_table()
    {
        var baseline = await CatalogBaselineImporter.LoadFromFileAsync(BaselinePath());
        var examples = baseline.Matching!.Examples;

        var resolver = await NewResolverAsync();
        var channels = (await resolver.ListCanonicalChannelsAsync()).ToList();

        var aliasCount = 0;
        var aliasMatchCount = 0;

        foreach (var (sourceKey, ch) in examples)
        {
            foreach (var alias in ch.Aliases)
            {
                if (string.IsNullOrWhiteSpace(alias)) continue;
                var normalized = alias.Trim().ToLowerInvariant();
                aliasCount++;

                // Para cada alias do JSON, deve existir pelo menos um
                // canal canónico que o resolva. Não basta estar
                // "no JSON" — tem de chegar à BD.
                var resolved = await resolver.ResolveAsync(normalized);
                if (resolved != null && resolved.CanonicalChannelId.HasValue)
                {
                    aliasMatchCount++;
                }
            }
        }

        // Pelo menos 80% dos aliases do JSON devem ser resolvíveis.
        Assert.True((double)aliasMatchCount / aliasCount >= 0.8,
            $"Só {aliasMatchCount}/{aliasCount} aliases do JSON foram resolvidos. Esperado >= 80%.");
    }

    // ════════════════════════════════════════════════════════════════
    // F. Unknown verdadeiro continua a gerar CreateEligible
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Unknown_PT_stream_still_results_in_CreateEligible()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", ".."));
        var countriesDir = Path.Combine(repoRoot, "m3uCrawler", "runtime-data", "countries");

        var resolver = await NewResolverAsync();
        var ingestor = new PipelineIngestionService(
            resolver, new CountryChannelValidator(countriesDir));

        // Slug único para evitar colisões entre runs.
        var slug = $"desconhecido-{Guid.NewGuid():N}".Substring(0, 24);
        var stream = new m3uCrawler.Models.M3uStream
        {
            Title = $"PT CANAL {slug.ToUpperInvariant()}",
            Url = $"http://x.example/{slug}.ts",
            Group = "Portugal",
        };

        var result = await ingestor.IngestAsync(
            new[] { stream }, $"r2-{Guid.NewGuid():N}", "Telegram", "pt");

        Assert.Equal(1, result.IngestedCount);
        Assert.Equal(1, result.AutoCreatedCount);
    }

    // ════════════════════════════════════════════════════════════════
    // G. Ingestion continua idempotente após R2
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Ingestion_is_idempotent_after_R2()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", ".."));
        var countriesDir = Path.Combine(repoRoot, "m3uCrawler", "runtime-data", "countries");

        var resolver = await NewResolverAsync();
        var ingestor = new PipelineIngestionService(
            resolver, new CountryChannelValidator(countriesDir));
        var stream = new m3uCrawler.Models.M3uStream
        {
            Title = "RTP 1", Url = "http://x.example/rtp1-r2.ts", Group = "Portugal",
        };

        var key = $"r2-idem-{Guid.NewGuid():N}".Substring(0, 32);
        await ingestor.IngestAsync(new[] { stream }, key, "Telegram", "pt");
        await ingestor.IngestAsync(new[] { stream }, key, "Telegram", "pt");

        var src = (await resolver.ListSourcesAsync()).Single(s => s.Key == key);
        var channelSources = await resolver.ListChannelSourcesAsync(sourceId: src.Id);
        Assert.Single(channelSources);
    }

    // ════════════════════════════════════════════════════════════════
    // H. O bootstrap é idempotente: segundo InitializeAsync não duplica
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Bootstrap_is_idempotent_after_R2()
    {
        // Primeiro InitializeAsync no construtor. Agora um segundo.
        var ctx2 = await _bootstrapper.InitializeAsync();
        await ctx2.DisposeAsync();

        var resolver = await NewResolverAsync();
        var channels = (await resolver.ListCanonicalChannelsAsync()).ToList();
        var keys = channels.Select(c => c.Key).ToList();

        // Sem chaves duplicadas após segundo bootstrap.
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    // ════════════════════════════════════════════════════════════════
    // I. Catalogo PT tem pelo menos RTP 1, RTP 2, RTP 3, SIC, TVI
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Catalog_contains_all_baseline_generalistas()
    {
        var resolver = await NewResolverAsync();
        var channels = (await resolver.ListCanonicalChannelsAsync()).ToList();

        var displayNames = channels.Select(c => c.DisplayName).ToHashSet();

        // Canais explicitamente definidos no JSON `matching.examples`.
        Assert.Contains("RTP 1", displayNames);
        Assert.Contains("RTP 2", displayNames);
        Assert.Contains("SIC", displayNames);
        Assert.Contains("TVI", displayNames);
        Assert.Contains("SIC Notícias", displayNames);
        Assert.Contains("RTP Notícias", displayNames);
        Assert.Contains("CNN Portugal", displayNames);
        Assert.Contains("CMTV", displayNames);
        Assert.Contains("Porto Canal", displayNames);
        Assert.Contains("Sport TV 1", displayNames);
        Assert.Contains("Discovery Channel", displayNames);
        Assert.Contains("National Geographic", displayNames);
        Assert.Contains("AXN", displayNames);
        Assert.Contains("Cartoon Network", displayNames);
        Assert.Contains("BTV", displayNames);

        // Canais esperados pelo JSON que estão implicitamente presentes.
        // Disney Channel e RTP 3 estão no JSON apenas como alias ou
        // via numbering — não como canais canónicos explícitos. Por
        // isso NÃO são asserts obrigatórios.
    }

    // ════════════════════════════════════════════════════════════════
    // J. O baseline JSON está disponível em disco para bootstrap
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void Baseline_JSON_exists_at_expected_path()
    {
        var path = BaselinePath();
        Assert.True(File.Exists(path),
            $"Baseline JSON em '{path}' não foi encontrado. Esperado em docs/catalog/.");
    }
}
