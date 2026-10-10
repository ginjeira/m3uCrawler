using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.LiveRun;
using m3uCrawler.Services.Sync;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W-ACQUIRED (2026-10-10) — Playlist de aquisição
/// (<c>playlist_acquired.m3u</c>).
///
/// <para>
/// Acumula, por run, <b>todos</b> os streams parseados das playlists que
/// foram funcionais para o país (passaram o gate e têm ≥1 stream working).
/// Playlists não-funcionais não contribuem. O teste físico continua a ser
/// só dos streams do país.
/// </para>
/// </summary>
public class AcquiredPlaylistTests
{
    // ===================== Acumulação no pipeline =====================

    [Fact]
    public void Accumulate_adds_all_parsed_streams_from_a_functional_playlist()
    {
        var acquired = new List<M3uStream>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();

        var parsed = new List<M3uStream>
        {
            Stream("RTP1", "http://x/rtp1.ts"),
            Stream("Canal US", "http://x/us.ts"),
        };

        TelegramScraperService.AccumulateAcquiredStreams(acquired, seen, gate, parsed, workingStreams: 1);

        Assert.Equal(2, acquired.Count);
        Assert.Contains(acquired, s => s.Url == "http://x/rtp1.ts");
        Assert.Contains(acquired, s => s.Url == "http://x/us.ts");
    }

    [Fact]
    public void Accumulate_skips_playlists_without_working_streams()
    {
        var acquired = new List<M3uStream>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();

        TelegramScraperService.AccumulateAcquiredStreams(
            acquired, seen, gate, new List<M3uStream> { Stream("X", "http://x/x.ts") }, workingStreams: 0);

        Assert.Empty(acquired);
    }

    [Fact]
    public void Accumulate_dedups_by_url_case_insensitively_keeping_first()
    {
        var acquired = new List<M3uStream>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();

        TelegramScraperService.AccumulateAcquiredStreams(
            acquired, seen, gate,
            new List<M3uStream> { Stream("Primeiro", "http://X/A.TS") },
            workingStreams: 1);
        TelegramScraperService.AccumulateAcquiredStreams(
            acquired, seen, gate,
            new List<M3uStream> { Stream("Segundo", "http://x/a.ts") },
            workingStreams: 1);

        var s = Assert.Single(acquired);
        Assert.Equal("Primeiro", s.Title);
    }

    // ===================== Publicação =====================

    private static string NewDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wacquired", $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static RunPublicationService NewService(string outputDir)
        => new(
            outputDir,
            new PlaylistManagerService(),
            importHistory: null,
            countryValidator: null,
            catalog: null,
            dispatcharr: new DispatcharrSyncCoordinator(() => DispatcharrConfig.Disabled()),
            clock: () => new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Local),
            utcClock: () => new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc));

    private static M3uStream Stream(string title, string url) => new()
    {
        Url = url,
        Title = title,
        Group = "Geral",
        IsWorking = true,
        ResponseTime = 10,
    };

    [Fact]
    public async Task Publication_writes_acquired_playlist_when_present()
    {
        var outputDir = NewDir("present");
        try
        {
            var result = await NewService(outputDir).PublishAsync(new RunPublicationRequest
            {
                Streams = new List<M3uStream> { Stream("RTP1", "http://x/rtp1.ts") },
                AcquiredStreams = new List<M3uStream>
                {
                    Stream("RTP1", "http://x/rtp1.ts"),
                    Stream("Canal US", "http://x/us.ts"),
                    Stream("Duplicado", "http://x/rtp1.ts"),
                },
                Report = new RunReport(),
                RunCountryGate = false,
                Verbose = false,
            });

            var acquiredPath = Path.Combine(outputDir, "playlist_acquired.m3u");
            Assert.True(File.Exists(acquiredPath));
            Assert.Equal(acquiredPath, result.AcquiredPlaylistPath);

            var urls = ReadUrls(acquiredPath);
            Assert.Equal(2, urls.Count);
            Assert.Contains("http://x/rtp1.ts", urls);
            Assert.Contains("http://x/us.ts", urls);

            // Dedup preserva a PRIMEIRA ocorrência (não "Duplicado").
            Assert.DoesNotContain("Duplicado", await File.ReadAllTextAsync(acquiredPath));
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Publication_does_not_write_acquired_playlist_when_absent()
    {
        var outputDir = NewDir("absent");
        try
        {
            var result = await NewService(outputDir).PublishAsync(new RunPublicationRequest
            {
                Streams = new List<M3uStream> { Stream("RTP1", "http://x/rtp1.ts") },
                Report = new RunReport(),
                RunCountryGate = false,
                Verbose = false,
            });

            Assert.False(File.Exists(Path.Combine(outputDir, "playlist_acquired.m3u")));
            Assert.Null(result.AcquiredPlaylistPath);
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Publication_does_not_write_acquired_playlist_when_empty()
    {
        var outputDir = NewDir("empty");
        try
        {
            var result = await NewService(outputDir).PublishAsync(new RunPublicationRequest
            {
                Streams = new List<M3uStream> { Stream("RTP1", "http://x/rtp1.ts") },
                AcquiredStreams = new List<M3uStream>(),
                Report = new RunReport(),
                RunCountryGate = false,
                Verbose = false,
            });

            Assert.False(File.Exists(Path.Combine(outputDir, "playlist_acquired.m3u")));
            Assert.Null(result.AcquiredPlaylistPath);
        }
        finally
        {
            try { Directory.Delete(outputDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static List<string> ReadUrls(string path)
        => File.ReadAllLines(path)
            .Where(l => l.StartsWith("http://", StringComparison.Ordinal)
                     || l.StartsWith("https://", StringComparison.Ordinal))
            .ToList();

    // ===================== Endpoints HTTP =====================

    private const string RawUrl =
        "http://user:sup3rsecret@dash-acq.example.test/live/USER/PASS/1";

    public sealed class Http : IAsyncLifetime
    {
        private string _root = null!;
        private string _outputDir = null!;
        private DashboardBootstrapEndpointTests.DashboardHarness _harness = null!;

        public async Task InitializeAsync()
        {
            _root = Path.Combine(Path.GetTempPath(), $"wacquired-http-{Guid.NewGuid():N}");
            var dbPath = Path.Combine(_root, "channel-catalog.db");
            _outputDir = Path.Combine(_root, "output");
            Directory.CreateDirectory(_outputDir);

            var factory = new TestDbContextFactory(dbPath);
            var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
            await using (var ctx = await bootstrapper.InitializeAsync())
            {
            }

            var resolver = new CatalogResolver(factory, dbPath);
            var composer = new PlaylistComposerService(factory);
            var history = new ImportHistoryService(_outputDir);

            _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
                _outputDir, resolver, composer, history,
                lifecycle: null, auth: null, bootstrap: null, webToken: null,
                standalone: true);
        }

        public async Task DisposeAsync()
        {
            await _harness.DisposeAsync();
            WebDashboardService.SetAuth(null, null);
            WebDashboardService.SetConfigurationLifecycle(null);
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }

        private HttpClient Client => _harness.Client;

        private static CancellationToken ShortToken()
            => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

        [Fact]
        public async Task Missing_file_returns_404_with_message()
        {
            var response = await Client.GetAsync("/api/playlist_acquired", ShortToken());

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("Playlist de aquisição não encontrada", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Existing_file_returns_raw_content()
        {
            var content = "#EXTM3U\n#EXTINF:-1,Canal\n" + RawUrl + "\n";
            File.WriteAllText(
                Path.Combine(_outputDir, "playlist_acquired.m3u"), content, new UTF8Encoding(false));

            var response = await Client.GetAsync("/api/playlist_acquired", ShortToken());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("audio/x-mpegurl", response.Content.Headers.ContentType?.MediaType);
            var body = await response.Content.ReadAsStringAsync();
            // A playlist de aquisição é um artefacto funcional (URLs reais),
            // tal como playlist.m3u.
            Assert.Contains("sup3rsecret", body);
        }

        [Fact]
        public async Task Preview_sanitizes_credentials()
        {
            var content = "#EXTM3U\n#EXTINF:-1,Canal\n" + RawUrl + "\n";
            File.WriteAllText(
                Path.Combine(_outputDir, "playlist_acquired.m3u"), content, new UTF8Encoding(false));

            var response = await Client.GetAsync("/api/playlist_acquired/preview", ShortToken());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("audio/x-mpegurl", response.Content.Headers.ContentType?.MediaType);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("sup3rsecret", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("***", body);
            Assert.Equal(CredentialSanitizer.SanitizeM3uContent(content), body);
        }

        [Theory]
        [InlineData("/api/playlist_acquired")]
        [InlineData("/api/playlist_acquired/preview")]
        public async Task Wrong_verb_returns_405(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };

            var response = await Client.SendAsync(request, ShortToken());

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
    }

    // ===================== UI (HTML estático) =====================

    public class Ui
    {
        private static string BuildDashboardHtml()
        {
            var method = typeof(WebDashboardService).GetMethod(
                "BuildDashboardHtml",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);
            return (string)method!.Invoke(null, null)!;
        }

        [Fact]
        public void Playlist_view_links_and_previews_acquired_playlist()
        {
            var html = BuildDashboardHtml();

            Assert.Contains("/api/playlist_acquired", html);
            Assert.Contains("id='playlistAcquiredPreview'", html);

            // O nó de preview está depois do preview de playlist_temp.
            var temp = html.IndexOf("id='playlistTempPreview'", StringComparison.Ordinal);
            var acquired = html.IndexOf("id='playlistAcquiredPreview'", StringComparison.Ordinal);
            Assert.True(temp >= 0 && acquired > temp);
        }

        [Fact]
        public void Loader_fetches_acquired_preview_and_handles_404()
        {
            var html = BuildDashboardHtml();
            var start = html.IndexOf("async function loadPlaylist()", StringComparison.Ordinal);
            Assert.True(start >= 0);
            var end = html.IndexOf("// DC-5d", start, StringComparison.Ordinal);
            Assert.True(end > start);
            var loader = html.Substring(start, end - start);

            Assert.Contains("/api/playlist_acquired/preview", loader);
            Assert.Contains("playlistAcquiredPreview", loader);
            Assert.Contains("playlist_acquired.m3u não encontrada", loader);
        }

        [Fact]
        public void Discovery_card_exposes_feed_canonical_fallback_toggle()
        {
            var html = BuildDashboardHtml();

            Assert.Contains("id='discoveryFeedCanonicalFallback'", html);
            // A flag é persistida pelo endpoint de discovery.
            Assert.Contains("feedCanonicalFallback", html);
        }
    }
}
