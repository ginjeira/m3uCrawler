using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.Recognition;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W5.6 — MatchMethod/MatchConfidence.
///
/// <para>
/// Contrato normativo: <c>docs/Reestructure/49-W56-MATCH-CONFIDENCE-SPECIFICATION.md</c>
/// (§7 tabela dos 8 métodos; §9 Unknown/Ambiguous; §10 versão <c>msm1</c>;
/// §11 transporte/persistência; §12 validação do endpoint manual).
/// Prova: confiança por método na Recognition, <c>null</c> em
/// Unknown/Ambiguous, separação de <c>FuzzyScore</c> (sem <c>/100</c>),
/// transporte aditivo em <c>CatalogResolution</c>, persistência
/// nullable + <c>MatchSemanticsVersion="msm1"</c> pelo pipeline (sem
/// recálculo nem 1.0 fixo) e validação OD-E do endpoint manual.
/// </para>
/// </summary>
[Collection("DashboardStaticState")]
public class WaveW56MatchConfidenceTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _outputDir;

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;
    private DashboardBootstrapEndpointTests.DashboardHarness? _harness;

    public WaveW56MatchConfidenceTests()
    {
        _root = TestTempDb.SuitePath($"wave-w56-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _outputDir = Path.Combine(_root, "output");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_outputDir);
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        _resolver = new CatalogResolver(_factory, _dbPath);
    }

    public async Task DisposeAsync()
    {
        if (_harness != null)
        {
            await _harness.DisposeAsync();
        }
        WebDashboardService.SetAuth(null, null);
        TestTempDb.CleanupDirectory(_root);
    }

    // ────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────

    private static string Norm(string raw) => ChannelNormalizer.Normalize(raw);

    private Task<CanonicalChannelEntity> Ch(
        string key, string displayName, params string[] aliases)
        => _resolver.CreateCanonicalChannelAsync(
            key, displayName, EditorialCategory.Live, CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy.CreateEligible, true, aliases);

    private static RecognitionPolicy FuzzyPolicy(int threshold, int margin) =>
        new(Enabled: true, FuzzyEnabled: true, FuzzyThreshold: threshold,
            FuzzyAmbiguityMargin: margin, FuzzyWeightsJson: null, Version: 1);

    private static string CountriesDir()
    {
        var cwd = Directory.GetCurrentDirectory();
        var repoRoot = Path.GetFullPath(Path.Combine(cwd, "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "m3uCrawler", "runtime-data", "countries");
    }

    private PipelineIngestionService NewIngestor() =>
        new(_resolver, new CountryChannelValidator(CountriesDir()));

    private static M3uStream MakeStream(string title, string url) => new()
    {
        Title = title,
        Url = url,
        Group = "Portugal",
        Logo = string.Empty,
        IsWorking = true,
        LastTested = DateTime.UtcNow,
        ResponseTime = 100,
        OriginalExtInf = $"#EXTINF:-1 group-title=\"Portugal\",{title}",
    };

    private async Task<long> SeedSourceAsync(string key)
    {
        var source = await _resolver.EnsureSourceAsync(
            key, key, SourceKind.Telegram, $"telegram://{key}", 0);
        return source.Id;
    }

    private DashboardBootstrapEndpointTests.DashboardHarness StartHarness()
    {
        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, _resolver, new PlaylistComposerService(_factory),
            new ImportHistoryService(_outputDir),
            lifecycle: null, auth: null, bootstrap: null, webToken: null, standalone: true);
        return _harness;
    }

    // ────────────────────────────────────────────────────────────────
    // 1. Tabela normativa centralizada (§7)
    // ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RecognitionMatchMethods.ExternalIdentityExact, 1.0)]
    [InlineData(RecognitionMatchMethods.TvgIdExact, 1.0)]
    [InlineData(RecognitionMatchMethods.CanonicalExact, 1.0)]
    [InlineData(RecognitionMatchMethods.NormalizedName, 1.0)]
    [InlineData(RecognitionMatchMethods.KnownAlias, 1.0)]
    [InlineData(RecognitionMatchMethods.ExplicitHeuristic, 0.80)]
    [InlineData(RecognitionMatchMethods.Fuzzy, 0.60)]
    [InlineData(RecognitionMatchMethods.ManualReview, 1.0)]
    public void Table_maps_each_normative_method_to_its_confidence(string method, double expected)
    {
        Assert.True(RecognitionMatchMethods.IsKnownMethod(method));
        Assert.True(RecognitionMatchMethods.TryGetMatchConfidence(method, out var confidence));
        Assert.Equal(expected, confidence);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("Ambiguous")]
    [InlineData("unknown")]
    [InlineData("Bogus")]
    public void Unknown_or_invalid_method_has_no_confidence(string? method)
    {
        Assert.False(RecognitionMatchMethods.IsKnownMethod(method));
        Assert.False(RecognitionMatchMethods.TryGetMatchConfidence(method, out _));
    }

    [Fact]
    public void MatchSemanticsVersion_is_msm1()
        => Assert.Equal("msm1", RecognitionMatchMethods.MatchSemanticsVersion);

    // ────────────────────────────────────────────────────────────────
    // 2. Recognition — confiança por método (§7) e null (§9)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TvgIdExact_confidence_is_1()
    {
        var ch = await Ch("w56-tvg", "W56 Tvg");
        await _resolver.RecordExternalIdentityAsync(
            ch.Id, null, ExternalIdentityNamespaces.TvgId, "w56-tvg-1", "operator", 1.0);

        var r = await _resolver.ResolveAsync(string.Empty, "w56-tvg-1");

        Assert.Equal(RecognitionMatchMethods.TvgIdExact, r.MatchMethod);
        Assert.Equal(1.0, r.MatchConfidence);
    }

    [Fact]
    public async Task ExternalIdentityExact_confidence_is_1()
    {
        var ch = await Ch("w56-prov", "W56 Prov");
        await _resolver.RecordExternalIdentityAsync(
            ch.Id, "w56", "provider:w56", "w56-prov-1", "operator", 1.0);

        var r = await _resolver.ResolveAsync(string.Empty, "w56-prov-1");

        Assert.Equal(RecognitionMatchMethods.ExternalIdentityExact, r.MatchMethod);
        Assert.Equal(1.0, r.MatchConfidence);
    }

    [Fact]
    public async Task CanonicalExact_confidence_is_1()
    {
        await Ch("w56-canon", "Nome Diferente W56");

        var r = await _resolver.ResolveAsync(Norm("w56-canon"), null);

        Assert.Equal(RecognitionMatchMethods.CanonicalExact, r.MatchMethod);
        Assert.Equal(1.0, r.MatchConfidence);
    }

    [Fact]
    public async Task NormalizedName_confidence_is_1()
    {
        await Ch("w56-name-key", "Canal W56 Nome");

        var r = await _resolver.ResolveAsync(Norm("Canal W56 Nome"), null);

        Assert.Equal(RecognitionMatchMethods.NormalizedName, r.MatchMethod);
        Assert.Equal(1.0, r.MatchConfidence);
    }

    [Fact]
    public async Task KnownAlias_confidence_is_1()
    {
        var ch = await Ch("w56-alias-key", "Outro W56");
        await _resolver.AddAliasAsync(ch.Id, "w56 alias passo");

        var r = await _resolver.ResolveAsync(Norm("w56 alias passo"), null);

        Assert.Equal(RecognitionMatchMethods.KnownAlias, r.MatchMethod);
        Assert.Equal(1.0, r.MatchConfidence);
    }

    [Fact]
    public async Task ExplicitHeuristic_confidence_is_080()
    {
        var ch = await Ch("w56-heur", "W56 Heur");
        await _resolver.CreateAffinityGroupAsync(
            "W56 Heur", AffinityKind.Channel, ch.Key, null, new[] { "w56 heuristica" });

        var r = await _resolver.ResolveAsync(Norm("w56 heuristica"), null);

        Assert.Equal(RecognitionMatchMethods.ExplicitHeuristic, r.MatchMethod);
        Assert.Equal(0.80, r.MatchConfidence);
    }

    [Fact]
    public async Task Fuzzy_confidence_is_060_and_is_not_fuzzy_score()
    {
        await Ch("w56-fuzzy", "CNN International");

        var r = await _resolver.ResolveAsync(
            Norm("CNN Internat"), null, FuzzyPolicy(threshold: 95, margin: 0));

        Assert.Equal(CatalogResolutionKind.Canonical, r.Kind);
        Assert.Equal(RecognitionMatchMethods.Fuzzy, r.MatchMethod);
        // W5.6 §7/§8: 0.60 é uma constante ratificada (OD-B), nunca
        // FuzzyScore/100 nem transformação equivalente.
        Assert.Equal(0.60, r.MatchConfidence);
        Assert.Equal(95, r.FuzzyScore);
        Assert.NotEqual(r.FuzzyScore!.Value / 100.0, r.MatchConfidence);
        Assert.NotEqual(r.FuzzyScore!.Value, (int)(r.MatchConfidence!.Value * 100));
    }

    [Fact]
    public async Task ManualReview_confidence_is_1()
    {
        await _resolver.CreateIdentityRuleAsync(
            Norm("w56 regra manual"), RuleDisposition.ReviewOnly, "teste w56");

        var r = await _resolver.ResolveAsync(Norm("w56 regra manual"), null);

        Assert.Equal(CatalogResolutionKind.Rule, r.Kind);
        Assert.Equal(RecognitionMatchMethods.ManualReview, r.MatchMethod);
        Assert.Equal(1.0, r.MatchConfidence);
    }

    [Fact]
    public async Task Unknown_and_Ambiguous_have_null_confidence()
    {
        await Ch("w56-null-present", "Canal Presente W56");

        var unknown = await _resolver.ResolveAsync(Norm("Nada Que Exista W56"), null);
        Assert.Equal(CatalogResolutionKind.Unknown, unknown.Kind);
        Assert.Null(unknown.MatchMethod);
        Assert.Null(unknown.MatchConfidence);

        await Ch("w56-twin-a", "Canal Gémeo W56");
        await Ch("w56-twin-b", "Canal Gémeo W56");
        var ambiguous = await _resolver.ResolveAsync(Norm("Canal Gémeo W56"), null);
        Assert.Equal(CatalogResolutionKind.Ambiguous, ambiguous.Kind);
        Assert.Null(ambiguous.MatchMethod);
        Assert.Null(ambiguous.MatchConfidence);
    }

    // ────────────────────────────────────────────────────────────────
    // 3. CatalogResolution — transporte aditivo (§11)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CatalogResolution_transports_confidence_additively()
    {
        var ch = await Ch("w56-transport", "W56 Transport");

        var byMethod = CatalogResolution.FromCanonical(ch, RecognitionMatchMethods.CanonicalExact);
        Assert.Equal(1.0, byMethod.MatchConfidence);

        var noMethod = CatalogResolution.FromCanonical(ch);
        Assert.Null(noMethod.MatchMethod);
        Assert.Null(noMethod.MatchConfidence);

        // Aditivo: os caminhos sem decisão semântica continuam sem confiança.
        Assert.Null(CatalogResolution.Unknown().MatchConfidence);
        Assert.Null(CatalogResolution.Ambiguous("w56").MatchConfidence);
    }

    // ────────────────────────────────────────────────────────────────
    // 4. Persistência nullable + versão (§10) e compatibilidade legacy
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordChannelSource_persists_nullable_confidence_and_msm1()
    {
        var ch = await Ch("w56-persist", "W56 Persist");
        var sourceId = await SeedSourceAsync("w56-persist-src");

        var withNull = await _resolver.RecordChannelSourceAsync(
            ch.Id, sourceId, "http://host/w56-null", matchMethod: "legacy", matchConfidence: null);
        Assert.Null(withNull.MatchConfidence);
        Assert.Equal("msm1", withNull.MatchSemanticsVersion);

        // Legacy: default 0 preservado (não convertido em null).
        var legacyDefault = await _resolver.RecordChannelSourceAsync(
            ch.Id, sourceId, "http://host/w56-legacy", matchMethod: "test");
        Assert.Equal(0.0, legacyDefault.MatchConfidence);
        Assert.Equal("msm1", legacyDefault.MatchSemanticsVersion);
    }

    // ────────────────────────────────────────────────────────────────
    // 5. Pipeline — transporta o que a Recognition decidiu (§11)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pipeline_persists_exact_method_and_confidence_without_recalculation()
    {
        await Ch("w56-pipe-exact", "Canal W56 Pipeline PT");
        var sourceKey = "w56-pipe-exact-src";

        var result = await NewIngestor().IngestAsync(
            new[] { MakeStream("Canal W56 Pipeline PT", "http://x.example/w56-exact.ts") },
            sourceKey, "Telegram", "pt");

        var entry = Assert.Single(result.Entries);
        Assert.Equal(RecognitionMatchMethods.NormalizedName, entry.MatchMethod);
        Assert.Equal(1.0, entry.MatchConfidence);

        var src = (await _resolver.ListSourcesAsync()).Single(s => s.Key == sourceKey);
        var cs = Assert.Single(await _resolver.ListChannelSourcesAsync(sourceId: src.Id));
        Assert.Equal(RecognitionMatchMethods.NormalizedName, cs.MatchMethod);
        Assert.Equal(1.0, cs.MatchConfidence);
        Assert.Equal("msm1", cs.MatchSemanticsVersion);
    }

    [Fact]
    public async Task Pipeline_persists_non_one_confidence_proving_no_hardcoded_1()
    {
        var ch = await Ch("w56-pipe-heur", "W56 Pipeline Heur");
        await _resolver.CreateAffinityGroupAsync(
            "W56 Pipeline Heur", AffinityKind.Channel, ch.Key, null, new[] { "pipeline heuristica w56 pt" });
        var sourceKey = "w56-pipe-heur-src";

        var result = await NewIngestor().IngestAsync(
            new[] { MakeStream("Pipeline Heuristica W56 PT", "http://x.example/w56-heur.ts") },
            sourceKey, "Telegram", "pt");

        var entry = Assert.Single(result.Entries);
        Assert.Equal(RecognitionMatchMethods.ExplicitHeuristic, entry.MatchMethod);
        Assert.Equal(0.80, entry.MatchConfidence);

        var src = (await _resolver.ListSourcesAsync()).Single(s => s.Key == sourceKey);
        var cs = Assert.Single(await _resolver.ListChannelSourcesAsync(sourceId: src.Id));
        Assert.Equal(RecognitionMatchMethods.ExplicitHeuristic, cs.MatchMethod);
        Assert.Equal(0.80, cs.MatchConfidence);
        Assert.Equal("msm1", cs.MatchSemanticsVersion);
    }

    // ────────────────────────────────────────────────────────────────
    // 6. Endpoint manual — validação OD-E (§12)
    // ────────────────────────────────────────────────────────────────

    private static StringContent Json(object payload)
        => new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static string ChannelSourcesRoute(long sourceId)
        => $"/api/catalog/sources/{sourceId}/streams";

    private async Task<int> ChannelSourceCountAsync(long sourceId)
        => (await _resolver.ListChannelSourcesAsync(sourceId: sourceId)).Count;

    [Fact]
    public async Task Manual_endpoint_accepts_valid_method_and_confidence()
    {
        var harness = StartHarness();
        var ch = await Ch("w56-ep-ok", "W56 Ep Ok");
        var sourceId = await SeedSourceAsync("w56-ep-ok-src");

        var resp = await harness.Client.PostAsync(ChannelSourcesRoute(sourceId), Json(new
        {
            canonicalChannelId = ch.Id,
            streamUrl = "http://host/w56-ep-ok",
            matchMethod = RecognitionMatchMethods.NormalizedName,
            matchConfidence = 1.0,
        }));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var stored = Assert.Single(await _resolver.ListChannelSourcesAsync(sourceId: sourceId));
        Assert.Equal(RecognitionMatchMethods.NormalizedName, stored.MatchMethod);
        Assert.Equal(1.0, stored.MatchConfidence);
        Assert.Equal("msm1", stored.MatchSemanticsVersion);
    }

    [Fact]
    public async Task Manual_endpoint_accepts_valid_combination_fuzzy_060()
    {
        var harness = StartHarness();
        var ch = await Ch("w56-ep-fuzzy", "W56 Ep Fuzzy");
        var sourceId = await SeedSourceAsync("w56-ep-fuzzy-src");

        var resp = await harness.Client.PostAsync(ChannelSourcesRoute(sourceId), Json(new
        {
            canonicalChannelId = ch.Id,
            streamUrl = "http://host/w56-ep-fuzzy",
            matchMethod = RecognitionMatchMethods.Fuzzy,
            matchConfidence = 0.60,
        }));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var stored = Assert.Single(await _resolver.ListChannelSourcesAsync(sourceId: sourceId));
        Assert.Equal(RecognitionMatchMethods.Fuzzy, stored.MatchMethod);
        Assert.Equal(0.60, stored.MatchConfidence);
    }

    [Fact]
    public async Task Manual_endpoint_rejects_invalid_combination_without_persistence()
    {
        var harness = StartHarness();
        var ch = await Ch("w56-ep-badcombo", "W56 Ep BadCombo");
        var sourceId = await SeedSourceAsync("w56-ep-badcombo-src");

        var resp = await harness.Client.PostAsync(ChannelSourcesRoute(sourceId), Json(new
        {
            canonicalChannelId = ch.Id,
            streamUrl = "http://host/w56-ep-badcombo",
            matchMethod = RecognitionMatchMethods.Fuzzy,
            matchConfidence = 0.90,
        }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(0, await ChannelSourceCountAsync(sourceId));
    }

    [Fact]
    public async Task Manual_endpoint_rejects_invalid_method_without_persistence()
    {
        var harness = StartHarness();
        var ch = await Ch("w56-ep-badmethod", "W56 Ep BadMethod");
        var sourceId = await SeedSourceAsync("w56-ep-badmethod-src");

        var resp = await harness.Client.PostAsync(ChannelSourcesRoute(sourceId), Json(new
        {
            canonicalChannelId = ch.Id,
            streamUrl = "http://host/w56-ep-badmethod",
            matchMethod = "NaoNormativo",
        }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
        Assert.Equal(0, await ChannelSourceCountAsync(sourceId));
    }

    [Theory]
    [InlineData(-0.1, "low")]
    [InlineData(1.1, "high")]
    public async Task Manual_endpoint_rejects_confidence_out_of_range_without_persistence(double bad, string label)
    {
        var harness = StartHarness();
        var ch = await Ch($"w56-ep-range-{label}", $"W56 Ep Range {label}");
        var sourceId = await SeedSourceAsync($"w56-ep-range-src-{label}");

        var resp = await harness.Client.PostAsync(ChannelSourcesRoute(sourceId), Json(new
        {
            canonicalChannelId = ch.Id,
            streamUrl = $"http://host/w56-ep-range-{label}",
            matchConfidence = bad,
        }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(0, await ChannelSourceCountAsync(sourceId));
    }

    [Fact]
    public async Task Manual_endpoint_legacy_without_match_method_is_preserved()
    {
        var harness = StartHarness();
        var ch = await Ch("w56-ep-legacy", "W56 Ep Legacy");
        var sourceId = await SeedSourceAsync("w56-ep-legacy-src");

        var resp = await harness.Client.PostAsync(ChannelSourcesRoute(sourceId), Json(new
        {
            canonicalChannelId = ch.Id,
            streamUrl = "http://host/w56-ep-legacy",
        }));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var stored = Assert.Single(await _resolver.ListChannelSourcesAsync(sourceId: sourceId));
        Assert.Equal("unknown", stored.MatchMethod);
        Assert.Equal(0.0, stored.MatchConfidence);
        Assert.Equal("msm1", stored.MatchSemanticsVersion);
    }

    [Fact]
    public async Task Manual_endpoint_legacy_confidence_without_method_is_validated_and_preserved()
    {
        var harness = StartHarness();
        var ch = await Ch("w56-ep-legacyconf", "W56 Ep LegacyConf");
        var sourceId = await SeedSourceAsync("w56-ep-legacyconf-src");

        var resp = await harness.Client.PostAsync(ChannelSourcesRoute(sourceId), Json(new
        {
            canonicalChannelId = ch.Id,
            streamUrl = "http://host/w56-ep-legacyconf",
            matchConfidence = 0.5,
        }));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var stored = Assert.Single(await _resolver.ListChannelSourcesAsync(sourceId: sourceId));
        Assert.Equal("unknown", stored.MatchMethod);
        Assert.Equal(0.5, stored.MatchConfidence);
        Assert.Equal("msm1", stored.MatchSemanticsVersion);
    }

    [Fact]
    public async Task Manual_endpoint_method_without_confidence_uses_legacy_default_zero()
    {
        var harness = StartHarness();
        var ch = await Ch("w56-ep-methodonly", "W56 Ep MethodOnly");
        var sourceId = await SeedSourceAsync("w56-ep-methodonly-src");

        var resp = await harness.Client.PostAsync(ChannelSourcesRoute(sourceId), Json(new
        {
            canonicalChannelId = ch.Id,
            streamUrl = "http://host/w56-ep-methodonly",
            matchMethod = RecognitionMatchMethods.ManualReview,
        }));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var stored = Assert.Single(await _resolver.ListChannelSourcesAsync(sourceId: sourceId));
        Assert.Equal(RecognitionMatchMethods.ManualReview, stored.MatchMethod);
        Assert.Equal(0.0, stored.MatchConfidence);
    }

    [Fact]
    public async Task Manual_endpoint_rejects_arbitrary_legacy_method_when_explicitly_provided()
    {
        var harness = StartHarness();
        var ch = await Ch("w56-ep-testmethod", "W56 Ep TestMethod");
        var sourceId = await SeedSourceAsync("w56-ep-testmethod-src");

        var resp = await harness.Client.PostAsync(ChannelSourcesRoute(sourceId), Json(new
        {
            canonicalChannelId = ch.Id,
            streamUrl = "http://host/w56-ep-testmethod",
            matchMethod = "test",
            matchConfidence = 0.0,
        }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(0, await ChannelSourceCountAsync(sourceId));
    }
}
