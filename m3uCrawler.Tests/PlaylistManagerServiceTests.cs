using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

public class PlaylistManagerServiceTests
{
    private static string TempPath(string suffix)
    {
        return Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + suffix);
    }

    [Fact]
    public async Task SaveToJsonReport_sanitizes_xtream_stream_urls()
    {
        var service = new PlaylistManagerService();
        var path = TempPath(".json");
        try
        {
            var streams = new List<M3uStream>
            {
                new()
                {
                    Url = "http://host.example.com/live/alice/SECPwd/12345.ts",
                    Title = "RTP1",
                    IsWorking = true
                }
            };

            await service.SaveToJsonReport(streams, path);

            var json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("SECPwd", json);
            Assert.DoesNotContain("alice", json); // username no path também é mascarado
            Assert.Contains("***", json);
            Assert.Contains("RTP1", json); // título preservado
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveToJsonReport_sanitizes_xtream_query_credentials()
    {
        var service = new PlaylistManagerService();
        var path = TempPath(".json");
        try
        {
            var streams = new List<M3uStream>
            {
                new()
                {
                    Url = "http://host.example.com/get.php?username=alice&password=SECPwd&type=m3u_plus",
                    Title = "SIC",
                    IsWorking = true
                }
            };

            await service.SaveToJsonReport(streams, path);

            var json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("SECPwd", json);
            Assert.Contains("username=***", json);
            Assert.Contains("password=***", json);
            Assert.Contains("type=m3u_plus", json); // param não-credential preservado
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveToJsonReport_sanitizes_userinfo_password()
    {
        var service = new PlaylistManagerService();
        var path = TempPath(".json");
        try
        {
            var streams = new List<M3uStream>
            {
                new()
                {
                    Url = "http://alice:SECPwd@host.example.com/path/to/stream.ts",
                    Title = "TVI",
                    IsWorking = true
                }
            };

            await service.SaveToJsonReport(streams, path);

            var json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("SECPwd", json);
            Assert.Contains("alice:***@", json); // userinfo: username preservado, password mascarado
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveToJsonReport_contains_no_password_across_many_streams()
    {
        var service = new PlaylistManagerService();
        var path = TempPath(".json");
        try
        {
            var streams = new List<M3uStream>
            {
                new() { Url = "http://host/live/u/SEC1/1.ts", Title = "A", IsWorking = true },
                new() { Url = "http://host/movie/u/SEC2/2.mkv", Title = "B", IsWorking = false },
                new() { Url = "http://host/series/u/SEC3/3.mp4", Title = "C", IsWorking = true },
                new() { Url = "http://host/get.php?username=u&password=SEC4&type=m3u_plus", Title = "D", IsWorking = true },
                new() { Url = "http://u:SEC5@host/path.ts", Title = "E", IsWorking = true }
            };

            await service.SaveToJsonReport(streams, path);

            var json = await File.ReadAllTextAsync(path);
            foreach (var secret in new[] { "SEC1", "SEC2", "SEC3", "SEC4", "SEC5" })
            {
                Assert.DoesNotContain(secret, json);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveToM3uPlaylist_preserves_real_xtream_urls_for_playback()
    {
        // A playlist M3U é o artefacto funcional: deve conter as URLs reais para que o
        // player consiga reproduzir streams Xtream. A sanitização aplica-se apenas ao
        // diagnóstico (logs, JSON, dashboard), NÃO à playlist funcional.
        var service = new PlaylistManagerService();
        var path = TempPath(".m3u");
        try
        {
            var streams = new List<M3uStream>
            {
                new()
                {
                    Url = "http://host.example.com/live/alice/SECPwd/12345.ts",
                    Title = "RTP1",
                    Group = "PT",
                    OriginalExtInf = "#EXTINF:-1 tvg-name=\"RTP1\" group-title=\"PT\",RTP1",
                    IsWorking = true
                }
            };

            await service.SaveToM3uPlaylist(streams, path);

            var m3u = await File.ReadAllTextAsync(path);
            Assert.Contains("alice", m3u);
            Assert.Contains("SECPwd", m3u); // password REAL preservada para reprodução
            Assert.Contains("RTP1", m3u);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
