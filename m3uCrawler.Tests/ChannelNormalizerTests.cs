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

    // R3-hardening: o normalizer deve remover tokens de país tanto em
    // maiúsculas como em minúsculas. Anteriormente, "cnn portugal"
    // (já lowercase) não era reduzido para "cnn" porque o regex
    // CountryToken exigia capitalização. Isto causava inconsistência
    // entre o título raw que o utilizador vê e a forma normalizada
    // que o matcher consulta no catálogo.
    [Theory]
    [InlineData("CNN Portugal", "cnn")]
    [InlineData("cnn portugal", "cnn")]
    [InlineData("SIC notícias", "sic noticias")]
    [InlineData("rtp notícias", "rtp noticias")]
    [InlineData("Porto Canal", "porto canal")]
    [InlineData("porto canal", "porto canal")]
    // ES (Espanha) e BR (Brasil) são tokens de país — são removidos.
    [InlineData("ES La 1", "la 1")]
    [InlineData("BR Globo News", "globo news")]
    public void Normalize_strips_country_tokens_case_insensitively(string raw, string expected)
    {
        Assert.Equal(expected, ChannelNormalizer.Normalize(raw));
    }

    [Fact]
    public void Tokens_returns_lowercase_split()
    {
        Assert.Equal(new[] { "sport", "tv", "1" }, ChannelNormalizer.Tokens("sport tv 1"));
    }
}
