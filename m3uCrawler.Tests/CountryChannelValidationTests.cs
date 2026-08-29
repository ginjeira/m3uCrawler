using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

public class CountryChannelValidationTests
{
    [Fact]
    public void Should_match_portuguese_channel_names_in_playlist_content()
    {
        var validator = new CountryChannelValidator();

        var playlist = "#EXTM3U\n#EXTINF:-1 tvg-name=\"RTP1\" group-title=\"Portugal\",RTP1\nhttp://example.com/rtp1\n#EXTINF:-1 tvg-name=\"SIC\" group-title=\"Portugal\",SIC\nhttp://example.com/sic";

        var result = validator.ValidatePlaylist(playlist, "pt");

        Assert.True(result.IsMatch);
        Assert.Contains("rtp1", result.MatchedAliases.Select(x => x.ToLowerInvariant()));
        Assert.Contains("sic", result.MatchedAliases.Select(x => x.ToLowerInvariant()));
    }

    [Fact]
    public void Should_not_match_when_country_list_is_empty()
    {
        var validator = new CountryChannelValidator();

        var playlist = "#EXTM3U\n#EXTINF:-1 tvg-name=\"FOX\" group-title=\"USA\",FOX\nhttp://example.com/fox";

        var result = validator.ValidatePlaylist(playlist, "pt");

        Assert.False(result.IsMatch);
        Assert.Empty(result.MatchedAliases);
    }

    [Fact]
    public void Should_load_country_channel_list_from_json_file()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var filePath = Path.Combine(tempDir, "pt.json");
            File.WriteAllText(filePath, """
            {
              "country": "pt",
              "channels": ["RTP1", "RTP 1", "SIC", "TVI", "SPORT TV 1", "BTV", "BENFICATV"]
            }
            """);

            var validator = new CountryChannelValidator(tempDir);
            var result = validator.ValidatePlaylist("#EXTM3U\n#EXTINF:-1 tvg-name=\"SPORT TV 1\",SPORT TV 1\nhttp://example.com/1", "pt");

            Assert.True(result.IsMatch);
            Assert.Contains("sport tv 1", result.MatchedAliases.Select(x => x.ToLowerInvariant()));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
