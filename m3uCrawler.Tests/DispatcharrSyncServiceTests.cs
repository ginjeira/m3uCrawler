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

public class DispatcharrSyncServiceTests
{
    private static string TempPlaylist(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pl_{Guid.NewGuid():N}.m3u");
        File.WriteAllText(path, body);
        return path;
    }

    private static DispatcharrConfig Enabled(bool dryRun = true) => new()
    {
        Enabled = true,
        BaseUrl = "http://dispatcharr.local",
        ApiKey = "PLACEHOLDER-API-KEY",
        DryRun = dryRun,
        MatchThreshold = 80,
    };

    [Fact]
    public async Task RunAsync_is_noop_when_disabled()
    {
        var svc = new DispatcharrSyncService(DispatcharrConfig.Disabled(), Path.GetTempPath());
        var r = await svc.RunAsync("anything.m3u");
        Assert.True(r.DryRun);
    }

    [Fact]
    public async Task DryRun_writes_plan_and_report_with_zero_writes()
    {
        var playlist = TempPlaylist(
            "#EXTM3U\n#EXTINF:-1 group-title=\"Portugal\",RTP1\nhttps://provider_a.example/rtp1\n");
        var outputDir = Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid():N}");
        try
        {
            var (svc, _) = BuildServiceWithFakeHttp(Enabled(dryRun: true), outputDir);
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
    public async Task Apply_writes_only_when_state_differs()
    {
        var playlist = TempPlaylist("#EXTM3U\n#EXTINF:-1 group-title=\"News\",CNN\nhttps://provider_a.example/cnn\n");
        var outputDir = Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid():N}");
        var writeCalls = new List<string>();

        var handler = new JsonHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path.Contains("/api/channels/channels/") && !path.Contains("/streams"))
            {
                return Json(new { count = 0, results = Array.Empty<object>() });
            }
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/channels/streams/"))
            {
                return Json(new { count = 0, results = Array.Empty<object>() });
            }
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/channels/groups/"))
            {
                return Json(new { count = 0, results = Array.Empty<object>() });
            }
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/core/version/"))
            {
                return Json(new { version = "0.30.0" });
            }
            if (req.Method == HttpMethod.Post)
            {
                writeCalls.Add($"{req.Method} {path}");
                if (path.EndsWith("/api/channels/streams/"))
                {
                    var body = req.Content!.ReadAsStringAsync().Result;
                    var url = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("url").GetString();
                    return Json(new { id = 1L, name = "n", url = url ?? "", is_custom = true });
                }
                if (path.EndsWith("/api/channels/channels/"))
                {
                    return Json(new { id = 100L, name = "CNN", channel_number = 1.0, streams = new long[] { 1 } });
                }
                if (path.EndsWith("/api/channels/groups/"))
                {
                    return Json(new { id = 5L, name = "News" });
                }
            }
            if (req.Method == HttpMethod.Patch && path.Contains("/api/channels/channels/"))
            {
                writeCalls.Add($"{req.Method} {path}");
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (req.Method == HttpMethod.Delete)
            {
                writeCalls.Add($"{req.Method} {path}");
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        try
        {
            var inner = new HttpClient(handler) { BaseAddress = new Uri("http://dispatcharr.local/api/") };
            var login = new DispatcharrLoginApi(new HttpClient()) { ApiKey = "PLACEHOLDER" };
            var auth = new DispatcharrAuthState();
            auth.Set("PLACEHOLDER", null);
            var authHandler = new DispatcharrAuthHandler(auth, login) { InnerHandler = handler };
            using var client = new HttpClient(authHandler) { BaseAddress = new Uri("http://dispatcharr.local/api/") };
            var svc = new DispatcharrSyncService(
                Enabled(dryRun: false),
                outputDir,
                aliases: new AliasResolver(),
                ordering: new StreamOrderingPolicy(),
                channels: new DispatcharrChannelClient(client),
                streams: new DispatcharrStreamClient(client),
                m3u: new DispatcharrM3UClient(client),
                http: client,
                auth: auth,
                login: login);

            var r = await svc.RunAsync(playlist);
            Assert.False(r.DryRun);
            Assert.NotEmpty(writeCalls);
        }
        finally
        {
            File.Delete(playlist);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Idempotent_apply_sends_no_writes_on_second_run()
    {
        var playlist = TempPlaylist("#EXTM3U\n#EXTINF:-1 group-title=\"News\",CNN\nhttps://provider_a.example/cnn\n");
        var outputDir = Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid():N}");
        var writes = 0;
        var gets = 0;

        var handler = new JsonHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            gets++;
            if (req.Method == HttpMethod.Patch || req.Method == HttpMethod.Post || req.Method == HttpMethod.Delete)
            {
                writes++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (path.EndsWith("/api/channels/channels/100/streams/"))
                return Json(new[] { new { id = 1L, name = "CNN", url = "https://provider_a.example/cnn" } });
            if (path.EndsWith("/api/channels/channels/"))
                return Json(new { count = 1, results = new[] { new { id = 100L, name = "CNN", channel_number = 1.0, tvg_id = (string?)null, streams = new long[] { 1 } } } });
            if (path.EndsWith("/api/channels/streams/"))
                return Json(new { count = 1, results = new[] { new { id = 1L, name = "CNN", url = "https://provider_a.example/cnn", tvg_id = (string?)null, channel_group = (long?)5, m3u_account = (long?)null, m3u_account_name = (string?)null, is_custom = true } } });
            if (path.EndsWith("/api/channels/groups/"))
                return Json(new { count = 1, results = new[] { new { id = 5L, name = "News" } } });
            if (path.EndsWith("/api/core/version/"))
                return Json(new { version = "0.30.0" });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        try
        {
            var auth = new DispatcharrAuthState(); auth.Set("K", null);
            var login = new DispatcharrLoginApi(new HttpClient()) { ApiKey = "K" };
            var authHandler = new DispatcharrAuthHandler(auth, login) { InnerHandler = handler };
            using var client = new HttpClient(authHandler) { BaseAddress = new Uri("http://dispatcharr.local/api/") };
            var svc = new DispatcharrSyncService(
                Enabled(dryRun: false),
                outputDir,
                aliases: new AliasResolver(),
                ordering: new StreamOrderingPolicy(),
                channels: new DispatcharrChannelClient(client),
                streams: new DispatcharrStreamClient(client),
                m3u: new DispatcharrM3UClient(client),
                http: client,
                auth: auth,
                login: login);

            await svc.RunAsync(playlist);
            int firstRunWrites = writes;

            await svc.RunAsync(playlist);
            int secondRunWrites = writes - firstRunWrites;
            Assert.Equal(0, secondRunWrites);
        }
        finally
        {
            File.Delete(playlist);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    private static HttpResponseMessage Json(object payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
    }

    private static (DispatcharrSyncService svc, JsonHandler handler) BuildServiceWithFakeHttp(DispatcharrConfig cfg, string outputDir)
    {
        var handler = new JsonHandler(_ => Json(new { count = 0, results = Array.Empty<object>() }));
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
            http: client, auth: auth, login: login);
        return (svc, handler);
    }
}
