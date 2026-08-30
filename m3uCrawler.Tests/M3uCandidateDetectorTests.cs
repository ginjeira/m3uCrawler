using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

public class M3uCandidateDetectorTests
{
    private readonly M3uCandidateDetector _detector = new();

    [Fact]
    public void Detects_m3u_url()
    {
        var candidates = _detector.DetectFromMessage("aqui https://example.com/list.m3u fim");
        Assert.Contains(candidates, c => c.Kind == CandidateSourceKind.Url && c.Url!.EndsWith(".m3u"));
    }

    [Fact]
    public void Detects_m3u8_url()
    {
        var candidates = _detector.DetectFromMessage("https://example.com/list.m3u8");
        Assert.Contains(candidates, c => c.Kind == CandidateSourceKind.Url && c.Url!.EndsWith(".m3u8"));
    }

    [Fact]
    public void Detects_m3u_url_with_query_string()
    {
        var candidates = _detector.DetectFromMessage("https://example.com/list.m3u?token=abc");
        Assert.Contains(candidates, c => c.Kind == CandidateSourceKind.Url && c.Url!.Contains("?token=abc"));
    }

    [Fact]
    public void Detects_m3u8_url_with_query_string()
    {
        var candidates = _detector.DetectFromMessage("https://example.com/list.m3u8?token=abc");
        Assert.Contains(candidates, c => c.Kind == CandidateSourceKind.Url && c.Url!.Contains("?token=abc"));
    }

    [Fact]
    public void Detects_m3u_filename()
    {
        var candidates = _detector.DetectFromMessage("ola", "lista_2026.m3u");
        Assert.Contains(candidates, c => c.Kind == CandidateSourceKind.Attachment && c.FileName == "lista_2026.m3u");
    }

    [Fact]
    public void Detects_m3u8_filename()
    {
        var candidates = _detector.DetectFromMessage("ola", "lista_2026.m3u8");
        Assert.Contains(candidates, c => c.Kind == CandidateSourceKind.Attachment && c.FileName == "lista_2026.m3u8");
    }

    [Fact]
    public void Detects_content_starting_with_extm3u()
    {
        var content = "#EXTM3U\n#EXTINF:-1,RTP1\nhttps://x/1";
        var candidates = _detector.DetectFromMessage("", null, content);
        Assert.Contains(candidates, c => c.Kind == CandidateSourceKind.Attachment && c.DetectedFrom == "#EXTM3U content");
    }

    [Fact]
    public void Detects_url_without_keyword_portugal()
    {
        var candidates = _detector.DetectFromMessage("partilho isto https://example.com/pt.m3u8 sem mais", null);
        Assert.NotEmpty(candidates);
        Assert.Contains(candidates, c => c.Kind == CandidateSourceKind.Url);
    }

    [Fact]
    public void Detects_attachment_without_keyword_portugal()
    {
        var content = "#EXTM3U\n#EXTINF:-1,RTP1\nhttps://x/1";
        var candidates = _detector.DetectFromMessage("sem palavra chave", "lista.m3u", content);
        Assert.Contains(candidates, c => c.Kind == CandidateSourceKind.Attachment);
    }

    [Theory]
    [InlineData("https://x.com/a.m3u")]
    [InlineData("https://x.com/a.m3u8")]
    [InlineData("https://x.com/a.m3u?t=1")]
    [InlineData("https://x.com/a.m3u8?t=1")]
    public void IsM3uUrl_true(string url) => Assert.True(_detector.IsM3uUrl(url));

    [Theory]
    [InlineData("https://x.com/a.ts")]
    [InlineData("https://x.com/a.m3u8x")]
    [InlineData("notaurl")]
    public void IsM3uUrl_false(string url) => Assert.False(_detector.IsM3uUrl(url));

    [Fact]
    public void Detects_extensionless_url_with_playlist_hint_as_inspect_candidate()
    {
        var candidates = _detector.DetectFromMessage("aqui https://example.com/getplaylist?id=123 fim");
        var c = candidates.FirstOrDefault(x => x.Kind == CandidateSourceKind.Url && x.Url!.Contains("getplaylist"));
        Assert.NotNull(c);
        Assert.True(c!.RequiresContentVerification);
        Assert.Equal("url (inspect)", c.DetectedFrom);
    }

    [Fact]
    public void Does_not_flag_arbitrary_extensionless_url()
    {
        var candidates = _detector.DetectFromMessage("visita https://example.com/about para mais info");
        Assert.DoesNotContain(candidates,
            c => c.Kind == CandidateSourceKind.Url && c.Url!.Contains("example.com/about"));
    }

    [Fact]
    public void Content_verification_confirms_m3u_vs_non_m3u()
    {
        Assert.True(_detector.LooksLikePlaylistContent("#EXTM3U\n#EXTINF:-1,RTP1\nhttps://x/1"));
        Assert.False(_detector.LooksLikePlaylistContent("<html>nao e playlist</html>"));
    }

    [Fact]
    public void Detects_xtream_server_url_and_resolves_to_get_php()
    {
        var candidates = _detector.DetectFromMessage("http://host.example.com:8080/live/alice/secret/12345.ts");
        var xtream = candidates.FirstOrDefault(c => c.DetectedFrom == "xtream server");

        Assert.NotNull(xtream);
        Assert.Equal(CandidateSourceKind.Url, xtream!.Kind);
        Assert.True(xtream.RequiresContentVerification);
        Assert.Contains("get.php", xtream.Url);
        Assert.Contains("type=m3u_plus", xtream.Url);
        // A URL resolvida contém as credenciais (necessárias para o download), mas NÃO é
        // a URL original com /live/USER/PASS/ — esta foi descartada para minimizar exposição.
        Assert.DoesNotContain("/live/alice/secret", xtream.Url);
    }

    [Fact]
    public void Detects_xtream_get_php_playlist_url()
    {
        var candidates = _detector.DetectFromMessage("http://host.example.com/get.php?username=alice&password=secret&type=m3u_plus");
        var xtream = candidates.FirstOrDefault(c => c.DetectedFrom == "xtream playlist");

        Assert.NotNull(xtream);
        Assert.Equal(CandidateSourceKind.Url, xtream!.Kind);
        Assert.True(xtream.RequiresContentVerification);
        Assert.Equal("http://host.example.com/get.php?username=alice&password=secret&type=m3u_plus", xtream.Url);
    }

    [Fact]
    public void Does_not_detect_arbitrary_non_xtream_url()
    {
        // URL arbitrária sem .m3u, sem dicas de playlist, sem padrão Xtream.
        var candidates = _detector.DetectFromMessage("veja https://example.com/some/page?id=42");
        Assert.DoesNotContain(candidates,
            c => c.Kind == CandidateSourceKind.Url && c.Url!.Contains("example.com/some/page"));
    }

    [Fact]
    public void Xtream_detection_is_keyword_independent()
    {
        // Mensagem sem qualquer keyword; apenas a URL Xtream.
        var candidates = _detector.DetectFromMessage(
            "sem palavra chave",
            filename: null,
            attachmentContent: null);
        // A função não recebe keyword; a detecção é puramente por URL.
        // Verificamos que, ao receber uma URL Xtream, é detectada sem keyword.
        var withXtream = _detector.DetectFromMessage("http://host/get.php?username=u&password=p&type=m3u_plus");
        Assert.Contains(withXtream, c => c.DetectedFrom == "xtream playlist");
        // E que sem URL Xtream e sem anexos, nada é detectado (não há keyword para usar).
        Assert.Empty(candidates);
    }

    [Fact]
    public void Xtream_candidate_url_is_the_resolved_playlist_not_the_original()
    {
        var resolved = _detector.ResolveXtreamPlaylistUrl("https://host.example.com:8080/live/alice/secret/1.ts");
        Assert.NotNull(resolved);
        Assert.Equal("https://host.example.com:8080/get.php?username=alice&password=secret&type=m3u_plus", resolved);
    }
}
