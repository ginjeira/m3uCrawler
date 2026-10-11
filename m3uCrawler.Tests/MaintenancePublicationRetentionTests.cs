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
using m3uCrawler.Services.Sync;
using m3uCrawler.Services.Validation;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Regressão da manutenção: um stream retido (preservado por falha
/// <b>retryable</b> após reteste) que corresponde a um canal canónico tem de
/// sobreviver à <see cref="m3uCrawler.Services.SourceSelection.SourceSelectionStage"/>
/// e ser publicado em <c>playlist.m3u</c>, apesar de <c>IsWorking=false</c>.
///
/// Sem a correcção, a selecção rejeita os retidos (motivo <c>not-working</c>)
/// e o writer M3U volta a filtrar <c>IsWorking</c>, fazendo a playlist
/// encolher a cada ciclo sob instabilidade transitória das fontes
/// (sintoma de produção 1519→477→203).
/// </summary>
public class MaintenancePublicationRetentionTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly DateTime _fixedLocal = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Local);
    private readonly DateTime _fixedUtc = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;
    private List<CanonicalChannelEntity> _channels = null!;

    public MaintenancePublicationRetentionTests()
    {
        _root = TestTempDb.SuitePath($"maintenance-retention-{Guid.NewGuid():N}");
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
        _resolver = new CatalogResolver(_factory, _dbPath);
        _channels = (await _resolver.ListCanonicalChannelsAsync()).Take(3).ToList();
        Assert.True(_channels.Count >= 3, "seed canónico deve ter pelo menos 3 canais");
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        TestTempDb.CleanupDirectory(_root);
        return Task.CompletedTask;
    }

    // ─────────────────────────────────────────────────────────────────
    // Fixture / helpers
    // ─────────────────────────────────────────────────────────────────

    private async Task<SourceEntity> NewSourceAsync() =>
        await _resolver.EnsureSourceAsync("maintenance-src", "maintenance-src", SourceKind.Telegram, "telegram://maintenance", 0);

    private async Task<List<M3uStream>> SeedPlaylistAsync()
    {
        var source = await NewSourceAsync();
        var streams = new List<M3uStream>();
        for (var i = 0; i < _channels.Count; i++)
        {
            var url = $"http://provider.example/ch{i}.ts";
            await _resolver.RecordChannelSourceAsync(
                _channels[i].Id, source.Id, url,
                availability: AvailabilityState.Reachable, matchMethod: "test");
            streams.Add(new M3uStream
            {
                Url = url,
                Title = _channels[i].DisplayName,
                Group = "PT",
                IsWorking = true,
            });
        }
        return streams;
    }

    private IReadOnlyList<(M3uStream Stream, StreamFailureKind Kind)> Retest(
        IEnumerable<M3uStream> previous, StreamFailureKind kind) =>
        previous
            .Select(s => (new M3uStream
            {
                Url = s.Url,
                Title = s.Title,
                Group = s.Group,
                IsWorking = kind == StreamFailureKind.None,
            }, kind))
            .ToList();

    private RunPublicationService NewService(string outputDir) => new(
        outputDir,
        new PlaylistManagerService(),
        new ImportHistoryService(outputDir),
        countryValidator: null,
        catalog: _resolver,
        dispatcharr: new DispatcharrSyncCoordinator(() => DispatcharrConfig.Disabled()),
        clock: () => _fixedLocal,
        utcClock: () => _fixedUtc);

    private async Task<RunPublicationResult> PublishAsync(string outputDir, IReadOnlyList<M3uStream> streams) =>
        await NewService(outputDir).PublishAsync(new RunPublicationRequest
        {
            Streams = streams.ToList(),
            Report = new RunReport(),
            CountryCode = "pt",
            RunCountryGate = false,
            HistoryMode = "TelegramMaintenance",
            PlaylistFileName = "playlist.m3u",
            JsonReportFileName = "telegram_maintain_report.json",
            TrackFinalPlaylistCount = true,
        });

    private static async Task<int> CountHttpEntriesAsync(string playlistPath) =>
        (await File.ReadAllLinesAsync(playlistPath))
            .Count(l => l.StartsWith("http://", StringComparison.Ordinal)
                     || l.StartsWith("https://", StringComparison.Ordinal));

    // ─────────────────────────────────────────────────────────────────
    // Regressão principal
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task All_retryable_failures_do_not_shrink_published_playlist()
    {
        var previous = await SeedPlaylistAsync();
        var outputDir = Path.Combine(_root, "publish");
        Directory.CreateDirectory(outputDir);

        var filter = TelegramScraperService.FilterRetainedStreams(
            Retest(previous, StreamFailureKind.Timeout));
        Assert.Equal(previous.Count, filter.Preserved.Count);
        Assert.All(filter.Preserved, s => Assert.False(s.IsWorking));
        Assert.All(filter.Preserved, s => Assert.True(s.Retained));

        var finalStreams = TelegramScraperService.MergeStreams(filter.Preserved, new List<M3uStream>());
        Assert.Equal(previous.Count, finalStreams.Count);

        var published = await PublishAsync(outputDir, finalStreams);

        Assert.Equal(previous.Count, published.Published.Count);
        Assert.Equal(previous.Count, await CountHttpEntriesAsync(published.PlaylistPath));
    }

    [Fact]
    public async Task Terminal_failure_is_still_removed()
    {
        var previous = await SeedPlaylistAsync();
        var outputDir = Path.Combine(_root, "publish-terminal");
        Directory.CreateDirectory(outputDir);

        // O último passou a 404 (terminal) — os restantes timeout (retryable).
        var outcomes = Retest(previous.Take(previous.Count - 1), StreamFailureKind.Timeout)
            .Append((new M3uStream
            {
                Url = previous[^1].Url,
                Title = previous[^1].Title,
                Group = previous[^1].Group,
                IsWorking = false,
            }, StreamFailureKind.Deterministic))
            .ToList();

        var filter = TelegramScraperService.FilterRetainedStreams(outcomes);
        Assert.Equal(previous.Count - 1, filter.Preserved.Count);
        Assert.Equal(1, filter.RemovedTerminal);

        var finalStreams = TelegramScraperService.MergeStreams(filter.Preserved, new List<M3uStream>());
        var published = await PublishAsync(outputDir, finalStreams);

        Assert.Equal(previous.Count - 1, published.Published.Count);
        Assert.Equal(previous.Count - 1, await CountHttpEntriesAsync(published.PlaylistPath));
    }

    [Fact]
    public async Task New_not_working_candidate_is_still_rejected_by_selection()
    {
        var previous = await SeedPlaylistAsync();
        var outputDir = Path.Combine(_root, "publish-new-dead");
        Directory.CreateDirectory(outputDir);

        // Candidato NOVO (não retido) mapeado a um canal canónico, mas sem
        // validação física: continua rejeitado pela selecção.
        var fresh = new M3uStream
        {
            Url = previous[0].Url,
            Title = previous[0].Title,
            Group = previous[0].Group,
            IsWorking = false,
        };
        Assert.False(fresh.Retained);

        var published = await PublishAsync(outputDir, new[] { fresh });

        Assert.Empty(published.Published);
        Assert.Equal(0, await CountHttpEntriesAsync(published.PlaylistPath));
    }

    [Fact]
    public async Task Unmatched_stream_still_passes_through()
    {
        await SeedPlaylistAsync();
        var outputDir = Path.Combine(_root, "publish-unmatched");
        Directory.CreateDirectory(outputDir);

        var unmatched = new M3uStream
        {
            Url = "http://unknown.example/not-in-catalog.ts",
            Title = "Canal Desconhecido",
            Group = "PT",
            IsWorking = true,
        };

        var published = await PublishAsync(outputDir, new[] { unmatched });

        Assert.Single(published.Published);
        Assert.Equal(1, await CountHttpEntriesAsync(published.PlaylistPath));
    }

    [Fact]
    public async Task Repeated_retryable_cycle_is_idempotent()
    {
        var previous = await SeedPlaylistAsync();
        var outputDir = Path.Combine(_root, "publish-idempotent");
        Directory.CreateDirectory(outputDir);

        var firstFilter = TelegramScraperService.FilterRetainedStreams(
            Retest(previous, StreamFailureKind.Timeout));
        var first = await PublishAsync(outputDir, TelegramScraperService.MergeStreams(firstFilter.Preserved, new List<M3uStream>()));

        // Segundo ciclo: recarregar do ficheiro publicado (Retained reset a
        // false, como em produção), retestar tudo timeout e republicar.
        var reloaded = await new PlaylistManagerService().LoadFromM3uPlaylist(first.PlaylistPath);
        Assert.Equal(previous.Count, reloaded.Count);

        var secondFilter = TelegramScraperService.FilterRetainedStreams(
            Retest(reloaded, StreamFailureKind.Timeout));
        var second = await PublishAsync(outputDir, TelegramScraperService.MergeStreams(secondFilter.Preserved, new List<M3uStream>()));

        Assert.Equal(first.Published.Count, second.Published.Count);
        Assert.Equal(first.Published.Select(s => s.Url).OrderBy(u => u),
            second.Published.Select(s => s.Url).OrderBy(u => u));
    }
}
