using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Cartões Stalker Portal: deteção + skip. Não é implementada aquisição
/// Stalker — apenas se reconhece o cartão e se ignora para não gastar tempo
/// a descarregar/testar. Um candidato válido no mesmo texto (Xtream/M3U/HTML)
/// continua a ser devolvido.
/// </summary>
public class StalkerPortalDetectionTests
{
    private readonly M3uCandidateDetector _detector = new();

    // Exemplo canónico: portal /c//c/, MAC, Serial/Device_Id, play_token=, type=m3u_plus.
    private const string PortalUrl = "http://103.176.90.27:80/c//c/";

    private const string PlayUrl =
        "http://103.176.90.27:80/portal.php?mac=00:1A:79:85:CC:47&play_token=abcdef123456&type=m3u_plus";

    private const string StalkerCardText =
        "Stalker Portal\n" +
        "Portal: http://103.176.90.27:80/c//c/\n" +
        "MAC: 00:1A:79:85:CC:47\n" +
        "Serial: 0123456789ABCDEF\n" +
        "Device_Id: A1B2C3D4E5F6\n" +
        "Play: http://103.176.90.27:80/portal.php?mac=00:1A:79:85:CC:47&play_token=abcdef123456&type=m3u_plus";

    // ======================= IsStalkerPortalUrl =======================

    [Theory]
    [InlineData("http://103.176.90.27:80/c//c/")]
    [InlineData("http://103.176.90.27:80/c/")]
    [InlineData("http://103.176.90.27:80/portal.php")]
    [InlineData("http://host.example:8080/stb.php")]
    [InlineData("http://host.example:8080/load.php")]
    [InlineData("http://host.example:8080/portalstb/")]
    [InlineData("http://host.example:8080/portal.php?mac=00:1A:79:85:CC:47")]
    [InlineData("http://host.example/play?play_token=abc123")]
    [InlineData("http://host.example/get.php?type=stb")]
    [InlineData("HTTPS://HOST.EXAMPLE/PORTAL.PHP")]
    public void IsStalkerPortalUrl_true(string url) => Assert.True(_detector.IsStalkerPortalUrl(url));

    [Theory]
    [InlineData("http://host.example/get.php?username=u&password=p&type=m3u_plus")]
    [InlineData("http://host.example/live/alice/secret/12345.ts")]
    [InlineData("https://example.com/about")]
    [InlineData("https://example.com/list.m3u8")]
    [InlineData("ftp://host.example/portal.php")]
    [InlineData("http://host.example/cx/portal/")]
    [InlineData("")]
    [InlineData(null)]
    public void IsStalkerPortalUrl_false(string? url) => Assert.False(_detector.IsStalkerPortalUrl(url));

    // ======================= LooksLikeStalkerCard =======================

    [Theory]
    [InlineData("MAC: 00:1A:79:85:CC:47")]
    [InlineData("mac 00:1a:79:85:cc:47")]
    [InlineData("url?play_token=abc123")]
    [InlineData("stb_lang=en")]
    [InlineData("type=stb")]
    [InlineData("Serial: 0123456789ABCDEF Device_Id: A1B2C3D4E5F6")]
    public void LooksLikeStalkerCard_true(string text) => Assert.True(_detector.LooksLikeStalkerCard(text));

    [Theory]
    [InlineData("apenas texto normal de uma playlist")]
    [InlineData("00:1A:79:85:CC")]                 // 5 grupos, não é MAC
    [InlineData("00:1A:79:85:CC:ZZ:47")]           // grupo não hexadecimal
    [InlineData("Serial: 0123456789ABCDEF")]       // Serial sem Device_Id
    [InlineData("Device_Id: A1B2C3D4E5F6")]        // Device_Id sem Serial
    [InlineData("https://example.com/list.m3u8")]
    [InlineData("")]
    [InlineData(null)]
    public void LooksLikeStalkerCard_false(string? text) => Assert.False(_detector.LooksLikeStalkerCard(text));

    // ======================= DetectFromMessage: skip =======================

    [Fact]
    public void Stalker_card_without_valid_candidates_yields_no_candidates()
    {
        Assert.True(_detector.IsStalkerPortalUrl(PortalUrl));
        Assert.True(_detector.IsStalkerPortalUrl(PlayUrl));
        Assert.True(_detector.LooksLikeStalkerCard(StalkerCardText));

        var candidates = _detector.DetectFromMessage(StalkerCardText);
        Assert.Empty(candidates);
    }

    [Theory]
    [InlineData("Portal http://host.example/portal.php")]
    [InlineData("Portal http://host.example/stb.php")]
    [InlineData("Portal http://host.example/load.php")]
    [InlineData("Portal http://host.example/portalstb/")]
    [InlineData("Portal http://103.176.90.27:80/c//c/")]
    public void Stalker_portal_url_is_not_a_candidate(string text)
    {
        Assert.Empty(_detector.DetectFromMessage(text));
    }

    [Fact]
    public void Stalker_card_with_valid_xtream_url_returns_only_the_valid_candidate()
    {
        var text = StalkerCardText +
            "\nXtream: http://host.example/get.php?username=u&password=p&type=m3u_plus";

        var candidates = _detector.DetectFromMessage(text);

        Assert.Single(candidates);
        Assert.Equal("xtream playlist", candidates[0].DetectedFrom);
        Assert.Equal(CandidateSourceKind.Url, candidates[0].Kind);
    }

    [Fact]
    public void Stalker_card_with_valid_m3u_url_returns_only_the_valid_candidate()
    {
        var text = StalkerCardText + "\nLista: https://example.com/list.m3u8";

        var candidates = _detector.DetectFromMessage(text);

        Assert.Single(candidates);
        Assert.Equal(CandidateSourceKind.Url, candidates[0].Kind);
        Assert.EndsWith(".m3u8", candidates[0].Url);
    }

    [Fact]
    public void Normal_message_is_unchanged()
    {
        var candidates = _detector.DetectFromMessage("aqui https://example.com/list.m3u fim");
        Assert.Single(candidates);
        Assert.Equal(CandidateSourceKind.Url, candidates[0].Kind);
    }

    // ======================= ExtractRemainingHttpUrls =======================

    [Fact]
    public void ExtractRemainingHttpUrls_skips_stalker_portal_url()
    {
        var text = StalkerCardText + "\nSite: https://example.com/about";
        var detected = _detector.DetectFromMessage(text);

        var remaining = TelegramScraperService.ExtractRemainingHttpUrls(text, detected);

        Assert.Single(remaining);
        Assert.Equal("https://example.com/about", remaining[0]);
    }

    // ======================= Contador do relatório =======================

    [Fact]
    public void Ignored_stalker_card_increments_report_counter()
    {
        var report = new RunReport();
        var text = StalkerCardText;
        var candidates = _detector.DetectFromMessage(text).ToList();

        TelegramScraperService.CountIgnoredStalkerCard(_detector, text, candidates, report);

        Assert.Equal(1, report.StalkerCandidatesSkipped);
    }

    [Fact]
    public void Stalker_card_with_valid_candidate_does_not_increment_counter()
    {
        var report = new RunReport();
        var text = StalkerCardText +
            "\nXtream: http://host.example/get.php?username=u&password=p&type=m3u_plus";
        var candidates = _detector.DetectFromMessage(text).ToList();
        Assert.Single(candidates);

        TelegramScraperService.CountIgnoredStalkerCard(_detector, text, candidates, report);

        Assert.Equal(0, report.StalkerCandidatesSkipped);
    }

    [Fact]
    public void Normal_message_does_not_increment_counter()
    {
        var report = new RunReport();
        var text = "aqui https://example.com/list.m3u fim";
        var candidates = _detector.DetectFromMessage(text).ToList();

        TelegramScraperService.CountIgnoredStalkerCard(_detector, text, candidates, report);

        Assert.Equal(0, report.StalkerCandidatesSkipped);
    }

    [Fact]
    public void Counter_is_a_noop_without_report()
    {
        var text = StalkerCardText;
        var candidates = _detector.DetectFromMessage(text).ToList();

        // Não deve lançar.
        TelegramScraperService.CountIgnoredStalkerCard(_detector, text, candidates, report: null);
    }
}
