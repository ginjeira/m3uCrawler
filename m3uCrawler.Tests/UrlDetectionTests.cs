using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

public class UrlDetectionTests
{
    [Fact]
    public void ShouldKeepOnlyStreamsMatchingKnownPortugueseChannelNames()
    {
        var tempBasePlaylist = Path.GetTempFileName();
        File.WriteAllText(tempBasePlaylist,
            "#EXTM3U\n" +
            "#EXTINF:-1 group-title=\"PORTUGAL\",RTP 1 HD\n" +
            "http://example.com/rtp1\n" +
            "#EXTINF:-1 group-title=\"PORTUGAL\",CMTV HD\n" +
            "http://example.com/cmtv\n");

        var streams = new List<M3uStream>
        {
            new() { Title = "RTP 1 HD", Url = "http://example.com/rtp1", IsWorking = true },
            new() { Title = "TVI", Url = "http://example.com/tvi", IsWorking = true },
            new() { Title = "CMTV HD", Url = "http://example.com/cmtv", IsWorking = true }
        };

        var filtered = PlaylistManagerService.FilterByKnownChannelMatches(streams, tempBasePlaylist);

        Assert.Equal(2, filtered.Count);
        Assert.Contains(filtered, s => s.Title == "RTP 1 HD");
        Assert.Contains(filtered, s => s.Title == "CMTV HD");
        Assert.DoesNotContain(filtered, s => s.Title == "TVI");

        File.Delete(tempBasePlaylist);
    }

    [Theory]
    [InlineData("http://example.com/get.php?username=user&password=pass&type=m3u_plus")]
    [InlineData("https://example.com/get.php?username=user&password=pass&type=m3u")]
    [InlineData("https://example.com/live/user/pass/123456.ts")]
    [InlineData("https://example.com/playlist.m3u")]
    [InlineData("https://example.com/index.m3u8")]
    [InlineData("https://example.com/live/user/pass/123456.m3u8")]
    public void ShouldTreatCommonIptvUrlsAsPlaylistCandidates(string url)
    {
        var isCandidate = M3uCrawlerService.IsLikelyPlaylistUrl(url);
        Assert.True(isCandidate, $"Expected {url} to be treated as a valid IPTV playlist candidate.");
    }

    [Theory]
    [InlineData("https://example.com/not-a-playlist.mp4")]
    [InlineData("https://example.com/image.jpg")]
    [InlineData("https://example.com/video.mp4?token=abc")]
    public void ShouldRejectNonPlaylistUrls(string url)
    {
        var isCandidate = M3uCrawlerService.IsLikelyPlaylistUrl(url);
        Assert.False(isCandidate, $"Expected {url} to be rejected as a playlist URL.");
    }

    [Fact]
    public void ShouldLoadCountrySpecificIndicatorsFromStructuredConfig()
    {
        var tempConfig = Path.GetTempFileName();
        File.WriteAllText(tempConfig, """
        {
          "countries": {
            "portugal": {
              "indicators": ["rtp1", "sic", "tvi"],
              "strict": ["sporttv"],
              "soft": ["cnn portugal"]
            },
            "spain": {
              "indicators": ["la1", "antena3"]
            }
          }
        }
        """);

        var portugalIndicators = PlaylistManagerService.LoadChannelIndicators(tempConfig, "portugal");
        var spainIndicators = PlaylistManagerService.LoadChannelIndicators(tempConfig, "spain");

        Assert.Contains("rtp1", portugalIndicators, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("sic", portugalIndicators, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("la1", spainIndicators, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("antena3", spainIndicators, StringComparer.OrdinalIgnoreCase);

        File.Delete(tempConfig);
    }
}
