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

public class DispatcharrSyncServiceFailureTrackingTests
{
    [Fact]
    public async Task RunAsync_dry_run_produces_zero_Failed_and_empty_FailedChannels()
    {
        var playlist = TempPlaylist("#EXTM3U\n#EXTINF:-1 group-title=\"News\",CNN\nhttps://provider_a.example/cnn\n");
        var outputDir = Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid():N}");
        try
        {
            var (svc, _) = BuildService(outputDir);
            var r = await svc.RunAsync(playlist);
            Assert.Equal(0, r.Report.Counts.Failed);
            Assert.Empty(r.Report.FailedChannels);
        }
        finally
        {
            File.Delete(playlist);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_real_run_with_failing_group_create_records_each_failure()
    {
        var playlist = TempPlaylist(
            "#EXTM3U\n" +
            "#EXTINF:-1 group-title=\"News\",CNN\nhttps://provider_a.example/cnn\n" +
            "#EXTINF:-1 group-title=\"Sports\",ESPN\nhttps://provider_a.example/espn\n" +
            "#EXTINF:-1 group-title=\"Kids\",NICK\nhttps://provider_a.example/nick\n");
        var outputDir = Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid():N}");
        try
        {
            var (svc, transport) = BuildService(outputDir, dryRun: false);
            transport.OnPostGroups = _ => HttpResponse(HttpStatusCode.BadRequest, "{\"detail\":\"name too long\"}");
            var r = await svc.RunAsync(playlist);

            Assert.Equal(3, r.Report.Counts.Failed);
            Assert.Equal(3, r.Report.FailedChannels.Count);
            foreach (var entry in r.Report.FailedChannels)
                Assert.Contains("HTTP 400", entry.Reason);
        }
        finally
        {
            File.Delete(playlist);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_real_run_records_failure_per_channel_and_does_not_double_count()
    {
        var playlist = TempPlaylist(
            "#EXTM3U\n" +
            "#EXTINF:-1 group-title=\"News\",CNN\nhttps://provider_a.example/cnn\n" +
            "#EXTINF:-1 group-title=\"News\",CNN2\nhttps://provider_b.example/cnn\n");
        var outputDir = Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid():N}");
        try
        {
            var (svc, transport) = BuildService(outputDir, dryRun: false);
            transport.OnPostGroups = _ => HttpResponse(HttpStatusCode.BadRequest, "boom");
            var r = await svc.RunAsync(playlist);

            Assert.Equal(2, r.Report.Counts.Failed);
            Assert.Equal(2, r.Report.FailedChannels.Count);
        }
        finally
        {
            File.Delete(playlist);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_real_run_first_channel_fails_but_second_channel_succeeds()
    {
        var playlist = TempPlaylist(
            "#EXTM3U\n" +
            "#EXTINF:-1 group-title=\"News\",CNN\nhttps://provider_a.example/cnn\n" +
            "#EXTINF:-1 group-title=\"Sports\",ESPN\nhttps://provider_a.example/espn\n");
        var outputDir = Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid():N}");
        try
        {
            var (svc, transport) = BuildService(outputDir, dryRun: false);
            var groupsCalls = 0;
            transport.OnPostGroups = _ =>
            {
                groupsCalls++;
                if (groupsCalls == 1) return HttpResponse(HttpStatusCode.BadRequest, "boom");
                return HttpResponse(HttpStatusCode.Created, "{\"id\":42,\"name\":\"Sports\"}");
            };
            var r = await svc.RunAsync(playlist);

            Assert.Equal(1, r.Report.Counts.Failed);
            Assert.Single(r.Report.FailedChannels);
        }
        finally
        {
            File.Delete(playlist);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_real_run_succeeds_when_group_create_succeeds()
    {
        var playlist = TempPlaylist(
            "#EXTM3U\n#EXTINF:-1 group-title=\"News\",CNN\nhttps://provider_a.example/cnn\n");
        var outputDir = Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid():N}");
        try
        {
            var (svc, transport) = BuildService(outputDir, dryRun: false);
            transport.OnPostGroups = _ => HttpResponse(HttpStatusCode.Created, "{\"id\":7,\"name\":\"News\"}");
            var r = await svc.RunAsync(playlist);
            Assert.Equal(0, r.Report.Counts.Failed);
            Assert.Empty(r.Report.FailedChannels);
            Assert.True(r.Report.Counts.NewChannels > 0);
        }
        finally
        {
            File.Delete(playlist);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_dry_run_does_not_invoke_POST_groups()
    {
        var playlist = TempPlaylist(
            "#EXTM3U\n#EXTINF:-1 group-title=\"News\",CNN\nhttps://provider_a.example/cnn\n");
        var outputDir = Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid():N}");
        try
        {
            var (svc, transport) = BuildService(outputDir, dryRun: true);
            transport.OnPostGroups = _ => HttpResponse(HttpStatusCode.Created, "{\"id\":7,\"name\":\"News\"}");
            var r = await svc.RunAsync(playlist);
            Assert.Equal(0, transport.PostGroupsCalls);
        }
        finally
        {
            File.Delete(playlist);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task FetchStateAsync_throws_when_groups_endpoint_returns_error()
    {
        var playlist = TempPlaylist("#EXTM3U\n#EXTINF:-1 group-title=\"News\",CNN\nhttps://provider_a.example/cnn\n");
        var outputDir = Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid():N}");
        try
        {
            var (svc, transport) = BuildService(outputDir, dryRun: false);
            transport.OnGetGroups = _ => HttpResponse(HttpStatusCode.InternalServerError, "kaboom");
            var ex = await Assert.ThrowsAsync<DispatcharrException>(() => svc.RunAsync(playlist));
            Assert.Contains("HTTP 500", ex.Message);
            Assert.Contains("/api/channels/groups/", ex.Message);
        }
        finally
        {
            File.Delete(playlist);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task FetchStateAsync_returns_normal_state_when_get_version_fails()
    {
        var playlist = TempPlaylist("#EXTM3U\n#EXTINF:-1 group-title=\"News\",CNN\nhttps://provider_a.example/cnn\n");
        var outputDir = Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid():N}");
        try
        {
            var (svc, transport) = BuildService(outputDir, dryRun: true);
            transport.OnGetVersion = _ => HttpResponse(HttpStatusCode.InternalServerError, "v-fail");
            var r = await svc.RunAsync(playlist);
            Assert.Null(r.Report.DispatcharrVersion);
        }
        finally
        {
            File.Delete(playlist);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    private static (DispatcharrSyncService svc, FailureTrackingTransport transport) BuildService(string outputDir, bool dryRun = true)
    {
        var cfg = new DispatcharrConfig
        {
            Enabled = true,
            BaseUrl = "http://dispatcharr.local",
            ApiKey = "PLACEHOLDER-API-KEY",
            DryRun = dryRun,
            MatchThreshold = 80,
        };

        var transport = new FailureTrackingTransport();
        var built = DispatcharrClientFactory.BuildWithTransport(
            cfg.BaseUrl, cfg.ApiKey, cfg.Username, cfg.Password, transport);

        var svc = new DispatcharrSyncService(
            cfg, outputDir,
            aliases: new AliasResolver(),
            ordering: new StreamOrderingPolicy(),
            channels: built.Channels,
            streams: built.Streams,
            m3u: built.M3U,
            http: built.Http,
            auth: built.Auth,
            login: built.Login);
        return (svc, transport);
    }

    private static string TempPlaylist(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pl_{Guid.NewGuid():N}.m3u");
        File.WriteAllText(path, body);
        return path;
    }

    private static HttpResponseMessage HttpResponse(HttpStatusCode code, string body) =>
        new(code)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class FailureTrackingTransport : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? OnGetVersion { get; set; }
        public Func<HttpRequestMessage, HttpResponseMessage>? OnGetGroups { get; set; }
        public Func<HttpRequestMessage, HttpResponseMessage>? OnPostGroups { get; set; }
        public int PostGroupsCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/core/version/"))
                return Task.FromResult(OnGetVersion?.Invoke(req) ?? JsonResp(new { version = "0.30.0" }));
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/channels/groups/"))
                return Task.FromResult(OnGetGroups?.Invoke(req) ?? JsonResp(new { count = 0, results = Array.Empty<object>() }));
            if (req.Method == HttpMethod.Post && path.EndsWith("/api/channels/groups/"))
            {
                PostGroupsCalls++;
                return Task.FromResult(OnPostGroups?.Invoke(req) ?? JsonResp(new { id = 1L, name = "g" }));
            }
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/channels/channels/"))
                return Task.FromResult(JsonResp(new { count = 0, results = Array.Empty<object>() }));
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/channels/streams/"))
                return Task.FromResult(JsonResp(new { count = 0, results = Array.Empty<object>() }));
            if (req.Method == HttpMethod.Post && path.EndsWith("/api/channels/streams/"))
                return Task.FromResult(JsonResp(new { id = 1L, name = "n", url = "https://x", is_custom = true }));
            if (req.Method == HttpMethod.Post && path.EndsWith("/api/channels/channels/"))
                return Task.FromResult(JsonResp(new { id = 1L, name = "c", channel_number = 1.0, streams = new long[] { 1 } }));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResp(object payload) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
    }
}
