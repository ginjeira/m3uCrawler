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
/// W-REVIEW-04 (Fase 3 Parte A) — acumulação de candidatos de stream
/// (<see cref="ReviewCandidateEntity"/>) por Review.
///
/// <para>
/// <see cref="ReviewItemEntity.StreamUrl"/> mantém-se como "último
/// vencedor" (retro-compatibilidade do preview <c>review:&lt;id&gt;</c>); os
/// candidatos são aditivos. Enquanto a Review está <c>Open</c>/
/// <c>InReview</c>, cada stream distinto observado (por fingerprint
/// canónico <c>sfp1</c> ou, na sua ausência, por URL exacto) acumula uma
/// row. Após decisão humana (<c>Resolved</c>/<c>Ignored</c>) a evidência e
/// os candidatos ficam intocados.
/// </para>
/// </summary>
public class ReviewCandidatePersistenceTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly string _countriesDir;
    private IDbContextFactory<ChannelCatalogDbContext> _factory = null!;
    private CatalogResolver _resolver = null!;

    public ReviewCandidatePersistenceTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"review-candidates-{Guid.NewGuid():N}.db");
        _countriesDir = Path.Combine(
            Path.GetTempPath(),
            $"review-candidates-countries-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_countriesDir);
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        await using (var ctx = await bootstrapper.InitializeAsync())
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
    /// Escreve um <c>pt.json</c> minimal com os títulos whitelistados
    /// para que o country gate os deixe chegar ao ramo que cria Reviews.
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

    private PipelineIngestionService NewIngestor() =>
        new(_resolver, new CountryChannelValidator(_countriesDir));

    private static M3uStream MakeStream(
        string title, string url, string group = "Portugal", string logo = "", string tvgId = "") =>
        new()
        {
            Title = title,
            Url = url,
            Group = group,
            Logo = logo,
            OriginalTvgId = tvgId,
            IsWorking = true,
            LastTested = DateTime.UtcNow,
            ResponseTime = 100,
            OriginalExtInf = $"#EXTINF:-1 group-title=\"{group}\",{title}",
        };

    private async Task<ReviewItemEntity> ReviewForAsync(string title)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.ReviewItems.AsNoTracking()
            .SingleAsync(r => r.NormalizedIdentity == ChannelNormalizer.Normalize(title));
    }

    private async Task<List<ReviewCandidateEntity>> CandidatesForAsync(long reviewItemId)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.ReviewCandidates.AsNoTracking()
            .Where(c => c.ReviewItemId == reviewItemId)
            .OrderBy(c => c.Id)
            .ToListAsync();
    }

    // ─────────────────────────────────────────────────────────────────
    // Teste 1 — dois streams com a mesma identidade/grupo mas URLs
    // diferentes acumulam 2 candidatos; StreamUrl = última URL.
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_two_streams_same_review_fingerprint_accumulate_two_candidates()
    {
        const string title = "Candidato Misterioso A";
        const string url1 = "http://cand.example/a/first.m3u8";
        const string url2 = "http://cand.example/b/second.m3u8";
        const string logo1 = "http://cand.example/logo1.png";
        const string logo2 = "http://cand.example/logo2.png";

        WriteCountryWhitelist(title);

        await NewIngestor().IngestAsync(
            new[] { MakeStream(title, url1, logo: logo1) },
            "OpCand-A", "Telegram", "pt", default, "run-cand-a-1");

        await NewIngestor().IngestAsync(
            new[] { MakeStream(title, url2, logo: logo2) },
            "OpCand-A", "Telegram", "pt", default, "run-cand-a-2");

        var review = await ReviewForAsync(title);

        // Uma única Review (dedupe por Fingerprint de Review).
        Assert.Equal(url2, review.StreamUrl);

        var candidates = await CandidatesForAsync(review.Id);
        Assert.Equal(2, candidates.Count);

        Assert.Equal(url1, candidates[0].StreamUrl);
        Assert.Equal(url2, candidates[1].StreamUrl);

        Assert.Equal(title, candidates[0].Title);
        Assert.Equal(title, candidates[1].Title);
        Assert.Equal("Portugal", candidates[0].Group);
        Assert.Equal("Portugal", candidates[1].Group);
        Assert.Equal(logo1, candidates[0].Logo);
        Assert.Equal(logo2, candidates[1].Logo);
    }

    // ─────────────────────────────────────────────────────────────────
    // Teste 2 — o mesmo URL ingerido duas vezes dedupa para 1 candidato.
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task B_same_url_twice_dedupes_to_single_candidate()
    {
        const string title = "Candidato Estável B";
        const string url = "http://cand.example/stable.m3u8";

        WriteCountryWhitelist(title);

        await NewIngestor().IngestAsync(
            new[] { MakeStream(title, url) },
            "OpCand-B", "Telegram", "pt", default, "run-cand-b-1");

        var afterFirst = await ReviewForAsync(title);
        var firstCandidates = await CandidatesForAsync(afterFirst.Id);
        Assert.Single(firstCandidates);
        var createdAt = firstCandidates[0].CreatedAtUtc;

        await NewIngestor().IngestAsync(
            new[] { MakeStream(title, url, logo: "http://cand.example/newlogo.png") },
            "OpCand-B", "Telegram", "pt", default, "run-cand-b-2");

        var afterSecond = await ReviewForAsync(title);
        var candidates = await CandidatesForAsync(afterSecond.Id);

        Assert.Single(candidates);
        Assert.Equal(url, candidates[0].StreamUrl);

        // O refresh de apresentação não altera CreatedAtUtc.
        Assert.Equal(createdAt, candidates[0].CreatedAtUtc);
        Assert.Equal("http://cand.example/newlogo.png", candidates[0].Logo);
    }

    // ─────────────────────────────────────────────────────────────────
    // Teste 3 — Review decidida (Resolved/Ignored) não acumula
    // candidatos nem toca na evidência.
    // ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task C_decided_review_does_not_accumulate_candidates(bool ignore)
    {
        var title = ignore ? "Candidato Ignorado C" : "Candidato Resolvido C";
        const string url1 = "http://cand.example/c/before.m3u8";
        const string url2 = "http://cand.example/c/after.m3u8";

        WriteCountryWhitelist(title);

        await NewIngestor().IngestAsync(
            new[] { MakeStream(title, url1) },
            "OpCand-C", "Telegram", "pt", default, "run-cand-c-1");

        var opened = await ReviewForAsync(title);
        Assert.Equal(ReviewItemState.Open, opened.State);

        if (ignore)
        {
            await _resolver.IgnoreReviewAsync(opened.Fingerprint, "decisão humana");
        }
        else
        {
            await _resolver.BeginReviewAsync(opened.Fingerprint);
            await _resolver.ResolveReviewAsync(opened.Fingerprint);
        }

        var decided = await ReviewForAsync(title);
        Assert.Equal(
            ignore ? ReviewItemState.Ignored : ReviewItemState.Resolved,
            decided.State);
        Assert.Equal(url1, decided.StreamUrl);

        // Nova observação (URL diferente) depois da decisão.
        await NewIngestor().IngestAsync(
            new[] { MakeStream(title, url2) },
            "OpCand-C", "Telegram", "pt", default, "run-cand-c-2");

        var after = await ReviewForAsync(title);

        // Decisão humana intocada; evidência intocada.
        Assert.Equal(
            ignore ? ReviewItemState.Ignored : ReviewItemState.Resolved,
            after.State);
        Assert.Equal(url1, after.StreamUrl);

        // Nenhum candidato novo (apenas o da observação pré-decisão).
        var candidates = await CandidatesForAsync(after.Id);
        Assert.Single(candidates);
        Assert.Equal(url1, candidates[0].StreamUrl);
    }

    // ─────────────────────────────────────────────────────────────────
    // Teste 4 — SourceId / StreamFingerprint guardados no candidato.
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task D_candidate_persists_source_id_and_stream_fingerprint()
    {
        const string title = "Candidato Evidência D";
        const string sourceKey = "OpCand-D";
        const string url = "http://cand.example/d/stream.m3u8";

        WriteCountryWhitelist(title);

        await NewIngestor().IngestAsync(
            new[] { MakeStream(title, url, tvgId: "tvg-d") },
            sourceKey, "Telegram", "pt", default, "run-cand-d-1");

        var review = await ReviewForAsync(title);
        Assert.NotNull(review.SourceId);

        var sources = await _resolver.ListSourcesAsync();
        var source = sources.Single(s => s.Id == review.SourceId);
        Assert.Equal(sourceKey, source.Key);

        var candidates = await CandidatesForAsync(review.Id);
        var candidate = Assert.Single(candidates);

        Assert.Equal(review.SourceId, candidate.SourceId);
        Assert.Equal(url, candidate.StreamUrl);
        Assert.Equal("tvg-d", candidate.TvgId);

        var expectedFingerprint = StreamFingerprint.TryComputeFingerprint(url);
        Assert.Equal(expectedFingerprint, candidate.StreamFingerprint);
        Assert.Equal(StreamFingerprint.Version, candidate.StreamFingerprintVersion);
        Assert.Equal("sfp1", candidate.StreamFingerprintVersion);
    }
}
