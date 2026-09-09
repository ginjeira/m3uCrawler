using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Cobre o parsing de referencias Telegram (t.me/c/...) a partir de texto de
/// mensagens. Estas referencias NAO sao URLs HTTP publicas enderecaveis: o
/// conteudo vive na sessao Telegram autenticada e exige resolucao via WTelegram.
/// </summary>
public class TelegramPublicationDiscoveryTests
{
    [Theory]
    [InlineData("https://t.me/c/1635952193/110637", 1635952193L, 110637)]
    [InlineData("http://t.me/c/1635952193/110637", 1635952193L, 110637)]
    [InlineData("https://t.me/c/1635952193/110637?comment=42", 1635952193L, 110637)] // query ignorada
    [InlineData("  https://t.me/c/1635952193/110637  ", 1635952193L, 110637)]
    public void Parses_valid_tme_c_reference(string input, long expectedChannel, int expectedMessage)
    {
        var refs = TelegramPublicationDiscovery.DiscoverFromText(input, source: "channel-x");
        Assert.Single(refs);
        Assert.Equal("telegram message link", refs[0].Kind);
        Assert.Equal(expectedChannel, refs[0].ChannelId);
        Assert.Equal(expectedMessage, refs[0].MessageId);
        Assert.Equal(PublicationState.Discovered, refs[0].State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sem urls aqui")]
    [InlineData("http://")]                    // URL malformada
    [InlineData("https://t.me/")]              // t.me sem path
    [InlineData("https://t.me/c/abc")]        // channel nao numerico
    [InlineData("https://t.me/c/123")]        // sem message id
    [InlineData("https://t.me/c/123/")]       // sem message id
    [InlineData("https://t.me/c/123/abc")]    // message id nao numerico
    [InlineData("https://t.me/foo/123")]      // /foo/ nao e /c/
    public void Does_not_parse_invalid_or_unrelated_references(string text)
    {
        var refs = TelegramPublicationDiscovery.DiscoverFromText(text, source: "channel-x");
        Assert.Empty(refs);
    }

    [Fact]
    public void Parses_http_url_as_publication_candidate()
    {
        var refs = TelegramPublicationDiscovery.DiscoverFromText(
            "veja https://example.com/page.html e diz algo", source: "channel-x");
        Assert.Single(refs);
        Assert.Equal("http publication url", refs[0].Kind);
        Assert.Null(refs[0].ChannelId);
    }

    [Fact]
    public void Does_not_dedupe_across_calls_by_default()
    {
        // Cada chamada devolve exactamente o que esta' no texto, sem dedup
        // (a dedup acontece no resolver com seen set).
        var refs = TelegramPublicationDiscovery.DiscoverFromText(
            "https://t.me/c/1/10 https://t.me/c/1/10",
            source: "x");
        Assert.Equal(2, refs.Count);
    }

    [Fact]
    public void Mixed_text_extracts_tme_references()
    {
        var text = "olha aqui https://t.me/c/1635952193/110637 e tambem https://example.com/page.html";
        var refs = TelegramPublicationDiscovery.DiscoverFromText(text, source: "channel-x");
        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, r => r.Kind == "telegram message link");
        Assert.Contains(refs, r => r.Kind == "http publication url");
    }

    [Fact]
    public void Discovered_reference_carries_sanitized_source()
    {
        // Source NUNCA deve conter passwords mesmo quando o texto original as tem.
        var text = "https://t.me/c/123/456 e http://host:80/live/user/secretpw/1.ts";
        var refs = TelegramPublicationDiscovery.DiscoverFromText(text, source: "channel-x");
        var tme = refs.Single(r => r.Kind == "telegram message link");
        Assert.DoesNotContain("secretpw", tme.DiscoveredFromText);
    }

    [Fact]
    public void Reference_to_string_does_not_leak_anything_sensitive()
    {
        // So para garantir que no futuro um ref nao ganha um ToString com creds.
        var refs = TelegramPublicationDiscovery.DiscoverFromText(
            "https://t.me/c/1635952193/110637", source: "channel-x");
        Assert.Single(refs);
        var s = refs[0].ToString();
        Assert.Contains("1635952193", s);
        Assert.Contains("110637", s);
        Assert.DoesNotContain("password", s);
    }
}
