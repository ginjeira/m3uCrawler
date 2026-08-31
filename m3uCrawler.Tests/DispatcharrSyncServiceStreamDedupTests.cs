using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using m3uCrawler.Models;
using m3uCrawler.Services.Dispatcharr;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using m3uCrawler.Services.Sync;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Regression tests for the UniqueViolation 500s observed on commit 91cad8e.
///
/// Root cause: when the matcher produced two <c>StreamMatchDecision</c> entries
/// with the same <c>ExistingStreamId</c> (two playlist entries that both URL-match
/// the same Dispatcharr stream), <c>BeginChannelApplyAsync.AllStreamIds</c>
/// carried the duplicates into the PATCH body, triggering
/// <c>psycopg.errors.UniqueViolation: unique_channel_stream</c> on the Dispatcharr side.
///
/// The fix is a single <c>.Distinct()</c> at the construction site.
/// </summary>
public class DispatcharrSyncServiceStreamDedupTests
{
    [Fact]
    public async Task RunAsync_real_run_dedupes_stream_ids_in_PATCH_body()
    {
        // Two #EXTINF entries with the SAME URL will both URL-match the same
        // existing stream (id=1). Without the .Distinct() fix, the PATCH body
        // would contain [1, 1] and Dispatcharr would answer HTTP 500 with
        // UniqueViolation: unique_channel_stream.
        var playlist = TempPlaylist(
            "#EXTM3U\n" +
            "#EXTINF:-1 group-title=\"News\",CNN\nhttps://provider_a.example/cnn\n" +
            "#EXTINF:-1 group-title=\"News\",CNN\nhttps://provider_a.example/cnn\n");
        var outputDir = Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid():N}");
        try
        {
            var (svc, client, handler) = BuildServiceWithCapturingHandler(outputDir, dryRun: false);
            await svc.RunAsync(playlist);

            Assert.NotEmpty(handler.PatchBodies);
            foreach (var body in handler.PatchBodies)
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(body);
                Assert.True(doc.TryGetProperty("streams", out var streamsEl),
                    "PATCH body must include a 'streams' array");
                var ids = streamsEl.EnumerateArray().Select(e => e.GetInt64()).ToList();
                Assert.NotEmpty(ids);
                // The actual assertion: no duplicate stream id within a single PATCH body.
                Assert.Equal(ids.Count, ids.Distinct().Count());
            }
        }
        finally
        {
            File.Delete(playlist);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    private static string TempPlaylist(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pl_{Guid.NewGuid():N}.m3u");
        File.WriteAllText(path, body);
        return path;
    }

    /// <summary>
    /// Wires the full DispatcharrSyncService graph against the capturing handler.
    /// Returns the service plus the underlying HttpClient so the test can keep both
    /// alive until the assertions run.
    /// </summary>
    private static (DispatcharrSyncService svc, HttpClient client, CapturingJsonHandler handler) BuildServiceWithCapturingHandler(
        string outputDir, bool dryRun)
    {
        var cfg = new DispatcharrConfig
        {
            Enabled = true,
            BaseUrl = "http://dispatcharr.local",
            ApiKey = "PLACEHOLDER-API-KEY",
            DryRun = dryRun,
            MatchThreshold = 80,
        };

        var handler = new CapturingJsonHandler();
        var auth = new DispatcharrAuthState();
        auth.Set("PLACEHOLDER-API-KEY", null);
        var login = new DispatcharrLoginApi(new HttpClient()) { ApiKey = "PLACEHOLDER-API-KEY" };
        var authHandler = new DispatcharrAuthHandler(auth, login) { InnerHandler = handler };
        var client = new HttpClient(authHandler) { BaseAddress = new Uri("http://dispatcharr.local/api/") };

        var svc = new DispatcharrSyncService(
            cfg, outputDir,
            aliases: new AliasResolver(),
            ordering: new StreamOrderingPolicy(),
            channels: new DispatcharrChannelClient(client),
            streams: new DispatcharrStreamClient(client),
            m3u: new DispatcharrM3UClient(client),
            http: client,
            auth: auth,
            login: login);
        return (svc, client, handler);
    }

    /// <summary>
    /// Stubs a minimal Dispatcharr with one existing channel (id=100) that has one
    /// attached stream (id=1) at the URL <c>https://provider_a.example/cnn</c>. Captures
    /// every PATCH body sent on <c>/api/channels/channels/&lt;id&gt;/</c>.
    /// </summary>
    private sealed class CapturingJsonHandler : HttpMessageHandler
    {
        public List<string> PatchBodies { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/core/version/"))
                return Task.FromResult(JsonResp(new { version = "0.30.0" }));
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/channels/groups/"))
                return Task.FromResult(JsonResp(new { count = 1, results = new[] { new { id = 5L, name = "News" } } }));
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/channels/channels/"))
                return Task.FromResult(JsonResp(new
                {
                    count = 1,
                    results = new[] { new { id = 100L, name = "CNN", channel_number = 1.0, tvg_id = (string?)null, streams = new long[] { 1 } } }
                }));
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/channels/streams/"))
                return Task.FromResult(JsonResp(new
                {
                    count = 1,
                    results = new[] { new { id = 1L, name = "CNN", url = "https://provider_a.example/cnn", tvg_id = (string?)null, channel_group = (long?)5, m3u_account = (long?)null, m3u_account_name = (string?)null, is_custom = true } }
                }));
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/channels/channels/100/streams/"))
                // Return a DIFFERENT list than the plan's AllStreamIds so the matcher
                // is forced to PATCH. This is the only way to capture the PATCH body in the
                // happy path (when state already matches, no PATCH is sent).
                return Task.FromResult(JsonResp(new[] { new { id = 99L, name = "OLD", url = "https://old.example/x" } }));
            if (req.Method == HttpMethod.Post && path.EndsWith("/api/channels/streams/"))
                return Task.FromResult(JsonResp(new { id = 99L, name = "n", url = "https://x", is_custom = true }));
            if (req.Method == HttpMethod.Post && path.EndsWith("/api/channels/channels/"))
                return Task.FromResult(JsonResp(new { id = 200L, name = "c", channel_number = 1.0, streams = new long[] { 99 } }));
            if (req.Method == HttpMethod.Post && path.EndsWith("/api/channels/groups/"))
                return Task.FromResult(JsonResp(new { id = 5L, name = "News" }));
            if (req.Method == HttpMethod.Patch && path.Contains("/api/channels/channels/"))
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                PatchBodies.Add(body);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
            if (req.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResp(object payload) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
    }
}