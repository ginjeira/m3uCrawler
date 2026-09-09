using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

public class TelegramPublicationFanOutTests
{
    private static readonly M3uCandidateDetector _detector = new();

    [Fact]
    public void ExtractRemainingHttpUrls_returns_empty_when_no_urls()
    {
        var text = "ola mundo sem urls";
        var detected = _detector.DetectFromMessage(text);
        var remaining = TelegramScraperService.ExtractRemainingHttpUrls(text, detected);
        Assert.Empty(remaining);
    }

    [Fact]
    public void ExtractRemainingHttpUrls_returns_empty_when_all_already_detected()
    {
        var text = "aqui https://example.com/list.m3u e https://server:80/live/u/p/1.ts";
        var detected = _detector.DetectFromMessage(text);
        var remaining = TelegramScraperService.ExtractRemainingHttpUrls(text, detected);
        Assert.Empty(remaining);
    }

    [Fact]
    public void ExtractRemainingHttpUrls_returns_only_urls_not_already_detected()
    {
        var text = "veja https://example.com/some-page e a playlist https://x.com/list.m3u";
        var detected = _detector.DetectFromMessage(text);
        var remaining = TelegramScraperService.ExtractRemainingHttpUrls(text, detected);
        Assert.Single(remaining);
        Assert.Equal("https://example.com/some-page", remaining[0]);
    }

    [Fact]
    public void ExtractRemainingHttpUrls_ignores_non_http_urls()
    {
        var text = "ftp://server/path e mailto:a@b.com e https://example.com/page";
        var detected = _detector.DetectFromMessage(text);
        var remaining = TelegramScraperService.ExtractRemainingHttpUrls(text, detected);
        Assert.Single(remaining);
        Assert.Equal("https://example.com/page", remaining[0]);
    }

    [Fact]
    public void ExtractRemainingHttpUrls_dedupes_repeated_url()
    {
        var text = "https://example.com/page aparece https://example.com/page duas vezes";
        var detected = _detector.DetectFromMessage(text);
        var remaining = TelegramScraperService.ExtractRemainingHttpUrls(text, detected);
        Assert.Single(remaining);
    }

    [Fact]
    public void PromoteXtreamAccount_produces_candidate_with_url_and_required_verification()
    {
        var c = TelegramScraperService.PromoteXtreamAccount(
            playlistUrl: "http://h.example:80/get.php?username=u&password=p&type=m3u_plus",
            publicationUrl: "https://example.com/page");
        Assert.NotNull(c);
        Assert.Equal(CandidateSourceKind.Url, c!.Kind);
        Assert.Equal("http://h.example:80/get.php?username=u&password=p&type=m3u_plus", c.Url);
        Assert.True(c.RequiresContentVerification);
    }

    [Fact]
    public void PromoteXtreamAccount_sets_DetectedFrom_to_xtream_publication()
    {
        var c = TelegramScraperService.PromoteXtreamAccount(
            playlistUrl: "http://h.example:80/get.php?username=u&password=p&type=m3u_plus",
            publicationUrl: "https://example.com/page");
        Assert.Equal("xtream publication", c!.DetectedFrom);
    }

    [Fact]
    public void PromoteXtreamAccount_Source_carries_publication_url_only()
    {
        var c = TelegramScraperService.PromoteXtreamAccount(
            playlistUrl: "http://h.example:80/get.php?username=u&password=p&type=m3u_plus",
            publicationUrl: "https://example.com/page");
        // Source is used in DiscoveredPlaylists; we sanitize so no creds leak.
        Assert.Contains("https://example.com/page", c!.Source);
        Assert.DoesNotContain("password=p", c.Source);
        Assert.DoesNotContain("username=u", c.Source);
    }

    [Fact]
    public void PromoteXtreamAccount_returns_null_for_null_playlist()
    {
        Assert.Null(TelegramScraperService.PromoteXtreamAccount(null, "https://example.com/page"));
    }

    [Fact]
    public void LooksLikeHtmlPublication_recognises_html()
    {
        Assert.True(TelegramScraperService.LooksLikeHtmlPublication("<html><body>x</body></html>"));
        Assert.True(TelegramScraperService.LooksLikeHtmlPublication("<!DOCTYPE html><html>x"));
        Assert.True(TelegramScraperService.LooksLikeHtmlPublication("  <HTML>"));
        Assert.False(TelegramScraperService.LooksLikeHtmlPublication("#EXTM3U\n#EXTINF:1,a\nhttp://x"));
        Assert.False(TelegramScraperService.LooksLikeHtmlPublication(null));
        Assert.False(TelegramScraperService.LooksLikeHtmlPublication(""));
        Assert.False(TelegramScraperService.LooksLikeHtmlPublication("plain text without html"));
    }
}
