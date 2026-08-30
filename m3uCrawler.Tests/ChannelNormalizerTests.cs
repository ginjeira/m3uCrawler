using m3uCrawler.Services.Matching;
using Xunit;

namespace m3uCrawler.Tests;

public class ChannelNormalizerTests
{
    [Theory]
    [InlineData("SPORT TV 1", "sport tv 1")]
    [InlineData("Sport TV1", "sport tv 1")]
    [InlineData("PT | SPORT TV 1 HD", "sport tv 1")]
    [InlineData("Sport TV 1 FHD", "sport tv 1")]
    [InlineData("Sport TV 1 [4K]", "sport tv 1")]
    [InlineData("sport tv 1 (UHD)", "sport tv 1")]
    [InlineData("CNN International (East)", "cnn international")]
    [InlineData("US: FOX Sports 1", "fox sports 1")]
    public void Normalizes_channel_names(string raw, string expected)
    {
        Assert.Equal(expected, ChannelNormalizer.Normalize(raw));
    }

    [Fact]
    public void Strips_diacritics()
    {
        Assert.Equal("sicnoticias", ChannelNormalizer.Normalize("SICnotícias"));
    }

    [Fact]
    public void Empty_input_returns_empty()
    {
        Assert.Equal(string.Empty, ChannelNormalizer.Normalize(""));
        Assert.Equal(string.Empty, ChannelNormalizer.Normalize("   "));
        Assert.Equal(string.Empty, ChannelNormalizer.Normalize(null));
    }

    [Theory]
    [InlineData("RTP1", "rtp 1")]
    [InlineData("RTP 1", "rtp 1")]
    public void Digit_merges_normalize_to_separate_token(string raw, string expected)
    {
        Assert.Equal(expected, ChannelNormalizer.Normalize(raw));
    }

    [Fact]
    public void Tokens_returns_lowercase_split()
    {
        Assert.Equal(new[] { "sport", "tv", "1" }, ChannelNormalizer.Tokens("sport tv 1"));
    }
}
