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

public class DispatcharrSyncServiceFactoryIntegrationTests
{
    [Fact]
    public async Task RunAsync_dry_run_does_not_throw_when_factory_is_used_with_fake_transport()
    {
        var playlist = TempPlaylist("#EXTM3U\n#EXTINF:-1 group-title=\"News\",CNN\nhttps://provider_a.example/cnn\n");
        var outputDir = Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid():N}");
        try
        {
            var (svc, _) = BuildServiceUsingRealFactory(outputDir);
            var r = await svc.RunAsync(playlist);

            Assert.True(r.DryRun);
            Assert.NotNull(r.PlanPath);
            Assert.NotNull(r.ReportPath);
            Assert.True(File.Exists(r.PlanPath!));
            Assert.True(File.Exists(r.ReportPath!));
        }
        finally
        {
            File.Delete(playlist);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_dry_run_reaches_FetchState_through_real_factory_wiring()
    {
        var playlist = TempPlaylist("#EXTM3U\n#EXTINF:-1 group-title=\"News\",CNN\nhttps://provider_a.example/cnn\n");
        var outputDir = Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid():N}");
        try
        {
            var (svc, handler) = BuildServiceUsingRealFactory(outputDir);
            await svc.RunAsync(playlist);
            Assert.True(handler.Hits > 0, "real factory wiring must drive the transport, not throw InvalidOperationException");
        }
        finally
        {
            File.Delete(playlist);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    private static (DispatcharrSyncService svc, RecordingTransport transport) BuildServiceUsingRealFactory(string outputDir)
    {
        var cfg = new DispatcharrConfig
        {
            Enabled = true,
            BaseUrl = "http://dispatcharr.local",
            ApiKey = "PLACEHOLDER-API-KEY",
            DryRun = true,
            MatchThreshold = 80,
        };

        var transport = new RecordingTransport();
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

    private sealed class RecordingTransport : HttpMessageHandler
    {
        public int Hits { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Hits++;
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/core/version/"))
                return Task.FromResult(Json(new { version = "0.30.0" }));
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/channels/groups/"))
                return Task.FromResult(Json(new { count = 0, results = Array.Empty<object>() }));
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/channels/streams/"))
                return Task.FromResult(Json(new { count = 0, results = Array.Empty<object>() }));
            if (req.Method == HttpMethod.Get && path.Contains("/api/channels/channels/") && !path.Contains("/streams"))
                return Task.FromResult(Json(new { count = 0, results = Array.Empty<object>() }));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(object payload) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
    }
}
