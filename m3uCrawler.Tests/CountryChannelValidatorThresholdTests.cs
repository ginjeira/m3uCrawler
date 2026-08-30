using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

public class CountryChannelValidatorThresholdTests
{
    private static CountryChannelValidator CreateValidator()
    {
        // Diretório vazio -> usa o fallback de canais para "pt" (determinístico e isolado).
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        return new CountryChannelValidator(tempDir);
    }

    [Fact]
    public void RTP1_RTP2_SIC_are_recognized_as_portugal()
    {
        var validator = CreateValidator();
        var content = "#EXTM3U\n#EXTINF:-1,RTP1\nhttp://x/1\n#EXTINF:-1,RTP2\nhttp://x/2\n#EXTINF:-1,SIC\nhttp://x/3";
        var result = validator.AnalyzePlaylist(content, "pt", 3);
        Assert.True(result.IsTargetCountry);
        Assert.Equal(3, result.RecognizedChannelCount);
    }

    [Fact]
    public void RTP1_SIC_TVI_are_recognized_as_portugal()
    {
        var validator = CreateValidator();
        var content = "#EXTM3U\n#EXTINF:-1,RTP1\nhttp://x/1\n#EXTINF:-1,SIC\nhttp://x/2\n#EXTINF:-1,TVI\nhttp://x/3";
        var result = validator.AnalyzePlaylist(content, "pt", 3);
        Assert.True(result.IsTargetCountry);
        Assert.Equal(3, result.RecognizedChannelCount);
    }

    [Fact]
    public void Portuguese_playlist_without_Portugal_in_filename_is_recognized()
    {
        var validator = CreateValidator();
        var content = "#EXTM3U\n#EXTINF:-1,RTP1\nhttp://x/1\n#EXTINF:-1,SIC\nhttp://x/2\n#EXTINF:-1,TVI\nhttp://x/3";
        var result = validator.AnalyzePlaylist(content, "pt", 3);
        Assert.True(result.IsTargetCountry);
    }

    [Fact]
    public void Portuguese_playlist_without_Portugal_in_caption_is_recognized()
    {
        var validator = CreateValidator();
        var content = "#EXTM3U\n#EXTINF:-1,RTP1\nhttp://x/1\n#EXTINF:-1,SIC\nhttp://x/2\n#EXTINF:-1,TVI\nhttp://x/3";
        var result = validator.AnalyzePlaylist(content, "pt", 3);
        Assert.True(result.IsTargetCountry);
    }

    [Fact]
    public void Foreign_playlist_is_rejected_for_pt()
    {
        var validator = CreateValidator();
        var content = "#EXTM3U\n#EXTINF:-1,La 1\nhttp://x/1\n#EXTINF:-1,Antena 3\nhttp://x/2\n#EXTINF:-1,Telecinco\nhttp://x/3";
        var result = validator.AnalyzePlaylist(content, "pt", 3);
        Assert.False(result.IsTargetCountry);
        Assert.Equal(0, result.RecognizedChannelCount);
    }

    [Fact]
    public void Channel_variants_do_not_inflate_count()
    {
        var validator = CreateValidator();
        var content = "#EXTM3U\n#EXTINF:-1,RTP1\nhttp://x/1\n#EXTINF:-1,RTP 1\nhttp://x/2";
        var result = validator.AnalyzePlaylist(content, "pt", 3);
        Assert.Equal(1, result.RecognizedChannelCount);
    }

    [Fact]
    public void Short_aliases_do_not_cause_false_positives()
    {
        var validator = CreateValidator();
        var content = "#EXTM3U\n#EXTINF:-1,TV box review\nhttp://x/1";
        var result = validator.AnalyzePlaylist(content, "pt", 3);
        Assert.Equal(0, result.RecognizedChannelCount);
        Assert.False(result.IsTargetCountry);
    }

    [Theory]
    [InlineData("rtp1")]
    [InlineData("RTP1")]
    [InlineData("RTP 1")]
    [InlineData("RTP_1")]
    [InlineData("RTP-1")]
    [InlineData("RTP.1")]
    public void Normalization_recognizes_RTP1_variants(string channelToken)
    {
        var validator = CreateValidator();
        var content = $"#EXTM3U\n#EXTINF:-1,{channelToken}\nhttp://x/1";
        var result = validator.AnalyzePlaylist(content, "pt", 3);
        Assert.Equal(1, result.RecognizedChannelCount);
    }

    [Fact]
    public void Fewer_than_threshold_channels_is_rejected()
    {
        var validator = CreateValidator();
        var content = "#EXTM3U\n#EXTINF:-1,RTP1\nhttp://x/1\n#EXTINF:-1,SIC\nhttp://x/2";
        var result = validator.AnalyzePlaylist(content, "pt", 3);
        Assert.False(result.IsTargetCountry);
        Assert.Equal(2, result.RecognizedChannelCount);
    }

    [Fact]
    public void SIC_title_is_recognized_as_SIC()
    {
        var validator = CreateValidator();
        var content = "#EXTM3U\n#EXTINF:-1,SIC\nhttp://x/1";
        var result = validator.AnalyzePlaylist(content, "pt", 3);
        Assert.Contains("SIC", result.MatchedAliases, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SIC_HD_title_is_recognized_as_SIC()
    {
        var validator = CreateValidator();
        var content = "#EXTM3U\n#EXTINF:-1,SIC HD\nhttp://x/1";
        var result = validator.AnalyzePlaylist(content, "pt", 3);
        Assert.Contains("SIC", result.MatchedAliases, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("SIC HD", result.MatchedAliases, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void RTP_variants_collapse_to_same_family()
    {
        var validator = CreateValidator();
        var content = "#EXTM3U\n#EXTINF:-1,RTP1\nhttp://x/1\n#EXTINF:-1,RTP 1\nhttp://x/2\n#EXTINF:-1,RTP1 HD\nhttp://x/3";
        var result = validator.AnalyzePlaylist(content, "pt", 3);

        // RTP1 e RTP 1 colapsam na mesma família (rtp1) — não inflacionam a contagem.
        var rtp1Count = result.RecognizedChannels.Count(c => c.Equals("rtp1", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, rtp1Count);
        Assert.Contains("rtp1", result.RecognizedChannels, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("rtp1hd", result.RecognizedChannels, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Basics_title_is_not_recognized_as_SIC()
    {
        var validator = CreateValidator();
        var content = "#EXTM3U\n#EXTINF:-1,basics\nhttp://x/1";
        var result = validator.AnalyzePlaylist(content, "pt", 3);
        Assert.DoesNotContain("SIC", result.MatchedAliases, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(0, result.RecognizedChannelCount);
    }

    [Fact]
    public void Atvinew_title_is_not_recognized_as_TVI()
    {
        var validator = CreateValidator();
        var content = "#EXTM3U\n#EXTINF:-1,atvinew\nhttp://x/1";
        var result = validator.AnalyzePlaylist(content, "pt", 3);
        Assert.DoesNotContain("TVI", result.MatchedAliases, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(0, result.RecognizedChannelCount);
    }

    [Fact]
    public void Playlist_with_only_false_positives_is_rejected()
    {
        var validator = CreateValidator();
        var content = "#EXTM3U\n#EXTINF:-1,basics\nhttp://x/1\n#EXTINF:-1,atvinew\nhttp://x/2\n#EXTINF:-1,privati\nhttp://x/3";
        var result = validator.AnalyzePlaylist(content, "pt", 3);
        Assert.Equal(0, result.RecognizedChannelCount);
        Assert.False(result.IsTargetCountry);
    }
}
