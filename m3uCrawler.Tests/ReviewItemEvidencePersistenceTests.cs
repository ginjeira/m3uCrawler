using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Matching;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W-REVIEW-01 — Regressão da persistência de evidência no
/// <see cref="ReviewItemEntity"/>.
///
/// <para>
/// Adiciona 5 colunas nullable ao <c>review_items</c>:
/// <c>StreamUrl</c>, <c>SourceId</c>, <c>StreamFingerprint</c>,
/// <c>StreamFingerprintVersion</c>, <c>RunId</c>. Esta suite
/// certifica que:
/// </para>
/// <list type="bullet">
///   <item>Uma nova Review criada durante <see cref="PipelineIngestionService.IngestAsync"/>
///   contém a evidência da ocorrência que a originou (Teste A).</item>
///   <item>A identidade da Review permanece estável (hash do
///   <c>normalizedIdentity|sourceGroup|reasonSignature</c>) — o
///   fingerprint da Review NÃO passa a depender de URL/SourceId/RunId
///   (Testes B, D).</item>
///   <item>Múltiplas invções do mesmo stream em Runs diferentes
///   consolidam numa única Review (Teste C).</item>
///   <item>Reviews legadas (criadas sem os novos campos) continuam
///   legíveis (Teste E).</item>
/// </list>
///
/// <para>
/// Não cobre a propagação desta evidência ao <c>ChannelSource</c>;
/// esse trabalho pertence a waves posteriores.
/// </para>
/// </summary>
public class ReviewItemEvidencePersistenceTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private IDbContextFactory<ChannelCatalogDbContext> _factory = null!;
    private ChannelCatalogBootstrapper _bootstrapper = null!;
    private CatalogResolver _resolver = null!;
    private string _countriesDir = string.Empty;

    public ReviewItemEvidencePersistenceTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"review-evidence-{Guid.NewGuid():N}.db");
        _countriesDir = Path.Combine(
            Path.GetTempPath(),
            $"review-evidence-countries-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_countriesDir);
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        _bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        await using (var ctx = await _bootstrapper.InitializeAsync())
        {
        }
        _resolver = new CatalogResolver(_factory, _dbPath);
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        TestTempDb.CleanupDirectory(_countriesDir);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Escreve um <c>pt.json</c> minimal com os títulos de teste
    /// whitelistados, para que o country gate os deixe entrar na
    /// ingestion. Sem isto, os streams seriam rejeitados antes de
    /// chegar ao ramo que cria ReviewItems.
    /// </summary>
    private void WriteCountryWhitelist(params string[] titles)
    {
        var channels = string.Join(",\n        ", titles.Select(t => $"\"{t}\""));
        var ptPath = Path.Combine(_countriesDir, "pt.json");
        File.WriteAllText(ptPath, $$"""
            {
              "Country": "pt",
              "Channels": [
                {{channels}}
              ]
            }
            """);
    }

    /// <summary>
    /// O validator é construído sobre o directório temporário
    /// <c>_countriesDir</c>. Para os testes, esse directório contém
    /// apenas um <c>pt.json</c> com os títulos whitelistados pelos
    /// testes individuais.
    /// </summary>
    private PipelineIngestionService NewIngestor() =>
        new PipelineIngestionService(_resolver, new CountryChannelValidator(_countriesDir));

    private static M3uStream MakeUnrecognizedStream(string title, string url, string group = "Portugal") =>
        new()
        {
            Title = title,
            Url = url,
            Group = group,
            Logo = string.Empty,
            IsWorking = true,
            LastTested = DateTime.UtcNow,
            ResponseTime = 100,
            OriginalExtInf = $"#EXTINF:-1 group-title=\"{group}\",{title}",
        };

    private async Task<List<ReviewItemEntity>> AllReviewsAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.ReviewItems.AsNoTracking().ToListAsync();
    }

    // ─────────────────────────────────────────────────────────────────
    // Teste A — nova Review guarda evidência completa
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_new_review_from_ingestion_persists_full_evidence()
    {
        const string runId = "op-e2e-run-001";
        const string sourceKey = "OpE2E-A";
        const string url = "http://stream.example/op-e2e-a/playlist.m3u8";
        const string title = "Stream Misterioso A";

        // Whitelist do country gate: o título tem de estar nos aliases
        // PT para passar o ValidateStreams e chegar ao ramo que cria
        // ReviewItem.
        WriteCountryWhitelist(title);

        var stream = MakeUnrecognizedStream(title, url);

        // Ingestion cria a Review no ramo "não-canonical".
        var result = await NewIngestor().IngestAsync(
            new[] { stream },
            sourceKey,
            "Telegram",
            "pt",
            default,
            runId);

        Assert.True(result.ReceivedCount >= 1);

        var reviews = await AllReviewsAsync();
        Assert.NotEmpty(reviews);

        // A Review do título desconhecido deve existir com evidência completa.
        var review = reviews.SingleOrDefault(r => r.NormalizedIdentity ==
            ChannelNormalizer.Normalize(title));
        Assert.NotNull(review);

        // ── RunId ──
        Assert.Equal(runId, review!.RunId);

        // ── SourceId ──
        Assert.NotNull(review.SourceId);
        var sources = await _resolver.ListSourcesAsync();
        var src = sources.Single(s => s.Id == review.SourceId);
        Assert.Equal(sourceKey, src.Key);

        // ── StreamUrl ──
        Assert.Equal(url, review.StreamUrl);

        // ── StreamFingerprint ──
        Assert.NotNull(review.StreamFingerprint);
        Assert.Equal(64, review.StreamFingerprint!.Length);
        var expectedFingerprint = StreamFingerprint.TryComputeFingerprint(url);
        Assert.Equal(expectedFingerprint, review.StreamFingerprint);

        // ── StreamFingerprintVersion ──
        Assert.Equal(StreamFingerprint.Version, review.StreamFingerprintVersion);
        Assert.Equal("sfp1", review.StreamFingerprintVersion);

        // ── Fingerprint (Review) NÃO é o StreamFingerprint ──
        // A identidade da Review (ReviewFingerprint) e a identidade do
        // stream (StreamFingerprint) são domínios distintos e usam
        // hashes diferentes sobre inputs diferentes.
        Assert.NotEqual(review.Fingerprint, review.StreamFingerprint);
        Assert.Equal(64, review.Fingerprint.Length);
    }

    // ─────────────────────────────────────────────────────────────────
    // Teste B — identidade da Review permanece estável
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task B_review_fingerprint_remains_review_fingerprint_not_stream_fingerprint()
    {
        // Cria a mesma Review duas vezes com URLs diferentes e SourceIds
        // diferentes. O Fingerprint da Review NÃO pode variar (caso
        // contrário, UpsertReviewItemAsync criaria duas rows distintas
        // em vez de consolidar uma única).
        const string url1 = "http://a.example/x.ts";
        const string url2 = "http://b.example/y.ts";
        const string title = "Mesmo Subject B";

        WriteCountryWhitelist(title);

        await NewIngestor().IngestAsync(
            new[] { MakeUnrecognizedStream(title, url1) },
            "OpE2E-B1", "Telegram", "pt", default, "run-B1");

        await NewIngestor().IngestAsync(
            new[] { MakeUnrecognizedStream(title, url2) },
            "OpE2E-B2", "Telegram", "pt", default, "run-B2");

        var reviews = await AllReviewsAsync();
        var subjectReviews = reviews
            .Where(r => r.NormalizedIdentity == ChannelNormalizer.Normalize(title))
            .ToList();

        // Uma única Review lógica (dedupe por Fingerprint).
        Assert.Single(subjectReviews);
        var review = subjectReviews[0];

        // O Fingerprint da Review é calculado por ReviewFingerprint.Of — não
        // inclui URL nem RunId nem SourceId. Esta propriedade é o que
        // sustenta o dedupe.
        var expectedFingerprint = ReviewFingerprint.Of(
            review.NormalizedIdentity,
            review.SourceGroup,
            review.ReasonSignature);
        Assert.Equal(expectedFingerprint, review.Fingerprint);

        // Verificação negativa: o Fingerprint da Review NÃO corresponde
        // a nenhum dos URLs observados.
        Assert.NotEqual(StreamFingerprint.TryComputeFingerprint(url1), review.Fingerprint);
        Assert.NotEqual(StreamFingerprint.TryComputeFingerprint(url2), review.Fingerprint);

        // A evidência foi refrescada pelo segundo ingestion (última
        // observação é a vencedora — o Review ainda está Open).
        Assert.Equal(url2, review.StreamUrl);
        Assert.Equal("run-B2", review.RunId);
    }

    // ─────────────────────────────────────────────────────────────────
    // Teste C — mesma Review em Runs diferentes
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task C_same_review_consolidates_across_runs_and_preserves_stream_fingerprint_semantics()
    {
        const string title = "Mesmo Subject C";
        const string url = "http://c.example/c.ts";

        var f1 = StreamFingerprint.TryComputeFingerprint(url);
        Assert.NotNull(f1);
        var expectedFingerprintValue = f1!;

        WriteCountryWhitelist(title);

        // Run 1
        await NewIngestor().IngestAsync(
            new[] { MakeUnrecognizedStream(title, url) },
            "OpE2E-C", "Telegram", "pt", default, "run-C-1");

        var reviewAfterRun1 = (await AllReviewsAsync())
            .Single(r => r.NormalizedIdentity == ChannelNormalizer.Normalize(title));
        Assert.Equal("run-C-1", reviewAfterRun1.RunId);
        Assert.Equal(expectedFingerprintValue, reviewAfterRun1.StreamFingerprint);

        // Run 2 (mesmo sourceKey, mesmo stream, RunId diferente)
        await NewIngestor().IngestAsync(
            new[] { MakeUnrecognizedStream(title, url) },
            "OpE2E-C", "Telegram", "pt", default, "run-C-2");

        var reviews = await AllReviewsAsync();
        var subjectReviews = reviews
            .Where(r => r.NormalizedIdentity == ChannelNormalizer.Normalize(title))
            .ToList();

        // Uma única Review lógica após dois Runs.
        Assert.Single(subjectReviews);
        var reviewAfterRun2 = subjectReviews[0];

        // RunId refrescado para o último Run (não criar row nova).
        Assert.Equal("run-C-2", reviewAfterRun2.RunId);

        // StreamFingerprint mantém a semântica de stream: é o mesmo
        // fingerprint do URL, calculado por sfp1.
        Assert.Equal(expectedFingerprintValue, reviewAfterRun2.StreamFingerprint);
        Assert.Equal("sfp1", reviewAfterRun2.StreamFingerprintVersion);

        // Sanidade: o mesmo fingerprint pode ser recalculado a partir do
        // URL actual.
        Assert.Equal(StreamFingerprint.TryComputeFingerprint(url), reviewAfterRun2.StreamFingerprint);
    }

    // ─────────────────────────────────────────────────────────────────
    // Teste D — URL diferente entre Runs (mesma identidade normalizada)
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task D_url_change_between_runs_does_not_create_new_review_and_updates_stream_url()
    {
        // Esta é a situação que mais risco traz: o mesmo título
        // "Canal Dinâmico" aparece com URLs diferentes em Runs
        // diferentes. O comportamento esperado:
        //   - O Review Fingerprint NÃO muda (dedupe preserva a row);
        //   - StreamUrl passa a reflectir a última observação;
        //   - StreamFingerprint muda (é derivado do URL).
        //
        // A decisão arquitectural sobre QUAL ocorrência fica
        // associada à Review é deliberada: a última observação
        // refresca os campos de evidência enquanto a Review está Open.
        // Waves futuras podem querer mudar para "preservar primeira
        // observação" ou "lista de ocorrências"; ver W-REVIEW-02+.
        const string title = "Canal Dinâmico D";
        const string urlOld = "http://d.example/old.ts";
        const string urlNew = "http://d.example/new.ts";

        WriteCountryWhitelist(title);

        await NewIngestor().IngestAsync(
            new[] { MakeUnrecognizedStream(title, urlOld) },
            "OpE2E-D", "Telegram", "pt", default, "run-D-1");

        var afterRun1 = (await AllReviewsAsync())
            .Single(r => r.NormalizedIdentity == ChannelNormalizer.Normalize(title));
        var fpOld = StreamFingerprint.TryComputeFingerprint(urlOld);
        Assert.Equal(fpOld, afterRun1.StreamFingerprint);

        await NewIngestor().IngestAsync(
            new[] { MakeUnrecognizedStream(title, urlNew) },
            "OpE2E-D", "Telegram", "pt", default, "run-D-2");

        var reviews = await AllReviewsAsync();
        var subjectReviews = reviews
            .Where(r => r.NormalizedIdentity == ChannelNormalizer.Normalize(title))
            .ToList();

        // Dedupe preserva uma única Review mesmo com URL diferente.
        Assert.Single(subjectReviews);
        var afterRun2 = subjectReviews[0];

        // Fingerprint da Review NÃO muda.
        Assert.Equal(afterRun1.Fingerprint, afterRun2.Fingerprint);

        // StreamUrl refrescado.
        Assert.Equal(urlNew, afterRun2.StreamUrl);

        // StreamFingerprint refrescado (porque é derivado do URL).
        var fpNew = StreamFingerprint.TryComputeFingerprint(urlNew);
        Assert.Equal(fpNew, afterRun2.StreamFingerprint);
        Assert.NotEqual(fpOld, afterRun2.StreamFingerprint);

        // RunId refrescado.
        Assert.Equal("run-D-2", afterRun2.RunId);
    }

    // ─────────────────────────────────────────────────────────────────
    // Teste E — Review legada (criada sem evidência) continua legível
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task E_legacy_review_without_evidence_remains_readable_with_nulls()
    {
        // Simula uma review legada: criada directamente via
        // UpsertReviewItemAsync sem os novos parâmetros (assinatura
        // antiga) — todos os novos campos ficam null.
        var review = await _resolver.UpsertReviewItemAsync(
            normalizedIdentity: "legacy-identity",
            sourceGroup: "legacy-group",
            reasonSignature: "legacy-sig",
            reasonText: "legacy note");

        // PK + Fingerprint existentes.
        Assert.True(review.Id > 0);
        Assert.NotEmpty(review.Fingerprint);

        // Novos campos: todos null.
        Assert.Null(review.StreamUrl);
        Assert.Null(review.SourceId);
        Assert.Null(review.StreamFingerprint);
        Assert.Null(review.StreamFingerprintVersion);
        Assert.Null(review.RunId);

        // State machine intacto.
        Assert.Equal(ReviewItemState.Open, review.State);

        // Re-ler da BD para garantir que os nulls são persistidos (e não
        // apenas defaults C# em memória).
        await using var ctx = await _factory.CreateDbContextAsync();
        var persisted = await ctx.ReviewItems
            .AsNoTracking()
            .SingleAsync(r => r.Id == review.Id);

        Assert.Null(persisted.StreamUrl);
        Assert.Null(persisted.SourceId);
        Assert.Null(persisted.StreamFingerprint);
        Assert.Null(persisted.StreamFingerprintVersion);
        Assert.Null(persisted.RunId);
        Assert.Equal("legacy-identity", persisted.NormalizedIdentity);
        Assert.Equal("legacy-group", persisted.SourceGroup);
        Assert.Equal("legacy-sig", persisted.ReasonSignature);
    }
}