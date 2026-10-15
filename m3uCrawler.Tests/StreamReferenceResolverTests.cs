using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Preview;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Fase 1b — <see cref="StreamReferenceResolver"/>: gramática
/// (<c>review:&lt;id&gt;</c> / <c>playlist:&lt;which&gt;:&lt;entryId&gt;</c>),
/// rejeição de tudo o resto (incluindo URLs cruas) e estabilidade do
/// <c>PlaylistEntryId</c>. A BD e o directório de output são temporários.
/// </summary>
public sealed class StreamReferenceResolverTests : IAsyncLifetime
{
    private const string ReviewUrl = "http://host.example.com:8080/live/user/pass/42.ts";

    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _outputDir;

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _catalog = null!;
    private StreamReferenceResolver _references = null!;

    public StreamReferenceResolverTests()
    {
        _root = TestTempDb.SuitePath($"preview-resolver-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _outputDir = Path.Combine(_root, "output");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outputDir);

        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await TestCatalogSeed.ApplyDefaultsAsync(ctx);
        await ctx.DisposeAsync();

        _catalog = new CatalogResolver(_factory, _dbPath);
        _references = new StreamReferenceResolver(_catalog, _outputDir, new M3uParserService());
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        TestTempDb.CleanupDirectory(_root);
        return Task.CompletedTask;
    }

    private Task<ReviewItemEntity> SeedReviewAsync(string identity, string? streamUrl)
        => _catalog.UpsertReviewItemAsync(
            identity, "grp", "sig", "nota", CancellationToken.None, streamUrl: streamUrl);

    private async Task<(ReviewItemEntity Item, long CandidateId)> SeedReviewWithCandidateAsync(
        string identity, string candidateUrl, string candidateTitle)
    {
        var item = await _catalog.UpsertReviewItemAsync(
            identity, "grp", "sig", "nota", CancellationToken.None,
            streamUrl: candidateUrl, candidateTitle: candidateTitle);
        var reloaded = await _catalog.GetReviewItemWithCandidatesAsync(item.Id);
        var candidate = Assert.Single(reloaded!.Candidates);
        Assert.Equal(candidateTitle, candidate.Title);
        return (item, candidate.Id);
    }

    [Fact]
    public async Task Review_candidate_reference_resolves_candidate_stream_url()
    {
        const string candidateUrl = "http://host.example.com:8080/live/user/pass/99.ts";
        var (item, candidateId) = await SeedReviewWithCandidateAsync("canal-cand", candidateUrl, "Candidato Um");

        var reference = await _references.ResolveAsync($"review:{item.Id}:{candidateId}", CancellationToken.None);

        Assert.NotNull(reference);
        Assert.Equal("review", reference!.Kind);
        Assert.Equal(item.Id.ToString(), reference.Id);
        Assert.Equal(candidateUrl, reference.Url);
        Assert.Equal("Candidato Um", reference.DisplayName);
    }

    [Fact]
    public async Task Review_candidate_reference_with_unknown_candidate_resolves_to_null()
    {
        var (item, _) = await SeedReviewWithCandidateAsync("canal-cand-miss", ReviewUrl, "Cand");

        Assert.Null(await _references.ResolveAsync($"review:{item.Id}:999999", CancellationToken.None));
    }

    [Fact]
    public async Task Review_candidate_reference_with_non_numeric_candidate_resolves_to_null()
    {
        var (item, _) = await SeedReviewWithCandidateAsync("canal-cand-bad", ReviewUrl, "Cand");

        Assert.Null(await _references.ResolveAsync($"review:{item.Id}:abc", CancellationToken.None));
        Assert.Null(await _references.ResolveAsync($"review:{item.Id}:", CancellationToken.None));
    }

    [Fact]
    public async Task Review_candidate_reference_from_another_review_resolves_to_null()
    {
        var (first, _) = await SeedReviewWithCandidateAsync("canal-cand-a", ReviewUrl, "A");
        var (second, secondCandidateId) = await SeedReviewWithCandidateAsync(
            "canal-cand-b", "http://host.example.com:8080/live/user/pass/7.ts", "B");

        // O candidato pertence à segunda review: não deve resolver para a primeira.
        Assert.Null(await _references.ResolveAsync(
            $"review:{first.Id}:{secondCandidateId}", CancellationToken.None));
        Assert.NotNull(await _references.ResolveAsync(
            $"review:{second.Id}:{secondCandidateId}", CancellationToken.None));
    }

    [Fact]
    public async Task Review_reference_resolves_stream_url()
    {
        var item = await SeedReviewAsync("canal-um", ReviewUrl);

        var reference = await _references.ResolveAsync($"review:{item.Id}", CancellationToken.None);

        Assert.NotNull(reference);
        Assert.Equal("review", reference!.Kind);
        Assert.Equal(item.Id.ToString(), reference.Id);
        Assert.Equal(ReviewUrl, reference.Url);
        Assert.Equal("canal-um", reference.DisplayName);
    }

    [Fact]
    public async Task Review_without_stream_url_resolves_to_null()
    {
        var item = await SeedReviewAsync("sem-url", streamUrl: null);

        Assert.Null(await _references.ResolveAsync($"review:{item.Id}", CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_review_id_resolves_to_null()
    {
        Assert.Null(await _references.ResolveAsync("review:999999", CancellationToken.None));
    }

    [Fact]
    public async Task Playlist_reference_resolves_entry_by_id()
    {
        const string url = "http://host.example.com:8080/live/user/pass/7.ts";
        await File.WriteAllTextAsync(
            Path.Combine(_outputDir, "playlist.m3u"),
            "#EXTM3U\n" +
            "#EXTINF:-1 tvg-logo=\"http://logo.example/l.png\" group-title=\"Noticias\",Canal Sete\n" +
            url + "\n",
            System.Text.Encoding.UTF8);

        var id = StreamReferenceResolver.PlaylistEntryId(url);
        var reference = await _references.ResolveAsync($"playlist:playlist:{id}", CancellationToken.None);

        Assert.NotNull(reference);
        Assert.Equal("playlist", reference!.Kind);
        Assert.Equal(id, reference.Id);
        Assert.Equal(url, reference.Url);
        Assert.Equal("Canal Sete", reference.DisplayName);
    }

    [Fact]
    public async Task Playlist_reference_with_unknown_entry_resolves_to_null()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_outputDir, "playlist.m3u"),
            "#EXTM3U\n#EXTINF:-1,Canal\nhttp://host.example.com:8080/live/user/pass/7.ts\n",
            System.Text.Encoding.UTF8);

        Assert.Null(await _references.ResolveAsync("playlist:playlist:0000000000000000", CancellationToken.None));
    }

    [Fact]
    public async Task Missing_playlist_file_resolves_to_null()
    {
        var id = StreamReferenceResolver.PlaylistEntryId(ReviewUrl);
        Assert.Null(await _references.ResolveAsync($"playlist:playlist_temp:{id}", CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_grammar_and_raw_urls_are_rejected()
    {
        Assert.Null(await _references.ResolveAsync("http://host.example.com/x.ts", CancellationToken.None));
        Assert.Null(await _references.ResolveAsync("https://host.example.com/x.ts", CancellationToken.None));
        Assert.Null(await _references.ResolveAsync("nope:foo", CancellationToken.None));
        Assert.Null(await _references.ResolveAsync("playlist:bogus:abc", CancellationToken.None));
        Assert.Null(await _references.ResolveAsync("playlist:playlist:", CancellationToken.None));
        Assert.Null(await _references.ResolveAsync("review:not-a-number", CancellationToken.None));
        Assert.Null(await _references.ResolveAsync(string.Empty, CancellationToken.None));
    }

    [Fact]
    public void PlaylistEntryId_is_deterministic_hex16()
    {
        var first = StreamReferenceResolver.PlaylistEntryId("http://host.example.com/x.ts");
        var second = StreamReferenceResolver.PlaylistEntryId("http://host.example.com/x.ts");

        Assert.Equal(first, second);
        Assert.Equal(16, first.Length);
        Assert.Matches("^[0-9a-f]{16}$", first);
        Assert.NotEqual(first, StreamReferenceResolver.PlaylistEntryId("http://host.example.com/y.ts"));
    }
}
