using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

public class CredentialSanitizerTests
{
    [Fact]
    public void Sanitizes_password_in_userinfo()
    {
        var sanitized = CredentialSanitizer.SanitizeUrl("http://alice:secret@host.example.com/live/alice/secret/1.ts");
        Assert.DoesNotContain("secret", sanitized);
        Assert.Contains("***", sanitized);
        Assert.Contains("alice", sanitized); // username in userinfo is preserved (not a secret in itself)
        Assert.Contains("host.example.com", sanitized);
    }

    [Fact]
    public void Sanitizes_password_in_query_string()
    {
        var sanitized = CredentialSanitizer.SanitizeUrl("http://host.example.com/get.php?username=alice&password=secret&type=m3u_plus");
        Assert.DoesNotContain("secret", sanitized);
        Assert.Contains("password=***", sanitized);
        Assert.Contains("username=***", sanitized);
        Assert.Contains("type=m3u_plus", sanitized); // não-credential param preserved
        Assert.Contains("host.example.com", sanitized);
    }

    [Fact]
    public void Sanitizes_xtream_path_user_and_pass()
    {
        var sanitized = CredentialSanitizer.SanitizeUrl("http://host.example.com/live/SECXyz/SECPwd/12345.ts");
        Assert.DoesNotContain("SECXyz", sanitized);
        Assert.DoesNotContain("SECPwd", sanitized);
        Assert.Contains("/live/***/***/", sanitized);
    }

    [Fact]
    public void Does_not_change_normal_url()
    {
        const string url = "https://example.com/path/to/playlist.m3u8?id=1&format=m3u8";
        var sanitized = CredentialSanitizer.SanitizeUrl(url);
        Assert.Equal(url, sanitized);
    }

    [Fact]
    public void Sanitizes_token_in_query()
    {
        var sanitized = CredentialSanitizer.SanitizeUrl("http://host.example.com/api?token=abc123&type=m3u");
        Assert.DoesNotContain("abc123", sanitized);
        Assert.Contains("token=***", sanitized);
    }

    [Theory]
    [InlineData("http://user:SECXyz@host/live/user/SECXyz/1.ts")]
    [InlineData("http://host/get.php?username=alice&password=SECXyz&type=m3u_plus")]
    [InlineData("http://host/get.php?username=alice&password=SECXyz")]
    [InlineData("http://host/movie/alice/SECXyz/1.mkv")]
    [InlineData("http://host/series/alice/SECXyz/1.mp4")]
    public void Secret_credential_value_never_appears_in_sanitized_output(string url)
    {
        var sanitized = CredentialSanitizer.SanitizeUrl(url);
        Assert.DoesNotContain("SECXyz", sanitized);
    }

    [Fact]
    public void Empty_or_null_returns_empty()
    {
        Assert.Equal(string.Empty, CredentialSanitizer.SanitizeUrl(null));
        Assert.Equal(string.Empty, CredentialSanitizer.SanitizeUrl(""));
    }

    [Theory]
    // Combinações: userinfo + path + query; todos os formatos Xtream; URL complexa.
    [InlineData("http://alice:SEC@host/live/alice/SEC/1.ts?token=tk",
                "alice:***@", "/live/***/***", "token=***")]
    [InlineData("http://u:SEC@host/movie/u/SEC/1.mkv?username=u&password=SEC&type=m3u_plus",
                "u:***@", "/movie/***/***", "username=***", "password=***", "type=m3u_plus")]
    [InlineData("http://u:SEC@host/series/u/SEC/1.mp4?password=SEC",
                "u:***@", "/series/***/***", "password=***")]
    [InlineData("http://host/live/alice/SEC/1.ts?token=tk",
                "/live/***/***", "token=***")]
    [InlineData("http://host/get.php?username=alice&password=SEC&type=m3u_plus&token=tk",
                "username=***", "password=***", "token=***", "type=m3u_plus")]
    public void Combined_url_is_sanitized_for_all_credential_forms(string url, params string[] mustContain)
    {
        var sanitized = CredentialSanitizer.SanitizeUrl(url);
        foreach (var fragment in mustContain)
            Assert.Contains(fragment, sanitized);
        // Nenhuma credencial deve sobreviver.
        Assert.DoesNotContain("SEC", sanitized);
    }

    [Fact]
    public void SanitizeM3uContent_sanitizes_all_urls_in_playlist()
    {
        var m3u = "#EXTM3U\n#EXTINF:-1 tvg-name=\"RTP1\" group-title=\"PT\",RTP1\n"
                + "http://host/live/alice/SECPwd/12345.ts\n"
                + "#EXTINF:-1 tvg-name=\"SIC\" group-title=\"PT\",SIC\n"
                + "http://host/get.php?username=u&password=SECQ&type=m3u_plus\n"
                + "https://example.com/normal/playlist.m3u8\n";

        var sanitized = CredentialSanitizer.SanitizeM3uContent(m3u);

        Assert.DoesNotContain("SECPwd", sanitized);
        Assert.DoesNotContain("SECQ", sanitized);
        Assert.Contains("/live/***/***/", sanitized);
        Assert.Contains("username=***", sanitized);
        Assert.Contains("password=***", sanitized);
        // URL normal sem credenciais deve permanecer inalterada.
        Assert.Contains("https://example.com/normal/playlist.m3u8", sanitized);
        // Cabeçalhos preservados.
        Assert.Contains("#EXTM3U", sanitized);
        Assert.Contains("tvg-name=\"RTP1\"", sanitized);
    }

    [Fact]
    public void SanitizeM3uContent_preserves_non_url_lines_unchanged()
    {
        var content = "#EXTM3U\n#EXTINF:-1,RTP1\nhttp://host/live/u/SEC/1.ts\n";
        var sanitized = CredentialSanitizer.SanitizeM3uContent(content);

        Assert.Contains("#EXTM3U", sanitized);
        Assert.Contains("#EXTINF:-1,RTP1", sanitized);
        Assert.DoesNotContain("SEC", sanitized);
    }

    [Fact]
    public void SanitizeM3uContent_handles_empty_and_null()
    {
        Assert.Equal(string.Empty, CredentialSanitizer.SanitizeM3uContent(null));
        Assert.Equal(string.Empty, CredentialSanitizer.SanitizeM3uContent(""));
    }

    // W9.A — Forma "bare" do Xtream: scheme://host:port/<user>/<pass>/<stream-id>.
    [Fact]
    public void Sanitizes_bare_xtream_path_user_and_pass_with_explicit_port()
    {
        var sanitized = CredentialSanitizer.SanitizeUrl(
            "http://neorcqds.example:8080/201788541134/SECBarePwd9/12345");

        Assert.DoesNotContain("201788541134", sanitized);
        Assert.DoesNotContain("SECBarePwd9", sanitized);
        Assert.Contains("http://neorcqds.example:8080/***/***/12345", sanitized);
    }

    [Fact]
    public void Sanitizes_bare_xtream_path_over_https_with_port()
    {
        var sanitized = CredentialSanitizer.SanitizeUrl(
            "https://host.example:8080/201788541134/SECBarePwd9/777");

        Assert.StartsWith("https://", sanitized);
        Assert.Contains(":8080", sanitized);
        Assert.Contains("/***/***/777", sanitized);
        Assert.DoesNotContain("201788541134", sanitized);
        Assert.DoesNotContain("SECBarePwd9", sanitized);
    }

    [Fact]
    public void Bare_xtream_stream_id_is_preserved_and_distinct()
    {
        var first = CredentialSanitizer.SanitizeUrl(
            "http://host.example:8080/201788541134/SECBarePwd9/12345");
        var second = CredentialSanitizer.SanitizeUrl(
            "http://host.example:8080/201788541134/SECBarePwd9/67890");

        Assert.NotEqual(first, second);
        Assert.Contains("/12345", first);
        Assert.Contains("/67890", second);
        Assert.DoesNotContain("201788541134", first);
        Assert.DoesNotContain("201788541134", second);
        Assert.DoesNotContain("SECBarePwd9", first);
        Assert.DoesNotContain("SECBarePwd9", second);
    }

    [Fact]
    public void Sanitizes_bare_xtream_path_with_media_extension()
    {
        var sanitized = CredentialSanitizer.SanitizeUrl(
            "http://host.example:8080/201788541134/SECBarePwd9/12345.m3u8");

        Assert.Contains("/***/***/12345.m3u8", sanitized);
        Assert.DoesNotContain("201788541134", sanitized);
        Assert.DoesNotContain("SECBarePwd9", sanitized);
    }

    [Fact]
    public void Sanitizes_bare_xtream_path_with_query_string()
    {
        var sanitized = CredentialSanitizer.SanitizeUrl(
            "http://host.example:8080/201788541134/SECBarePwd9/12345?token=tk");

        Assert.Contains("/***/***/12345?token=***", sanitized);
        Assert.DoesNotContain("201788541134", sanitized);
        Assert.DoesNotContain("SECBarePwd9", sanitized);
    }

    [Theory]
    [InlineData("https://example.com/path/to/playlist.m3u8?id=1&format=m3u8")]
    [InlineData("http://host.example/video/season/episode.mp4")]
    [InlineData("http://host.example/api/v1/2")]
    [InlineData("http://host.example/docs/2024/report.pdf")]
    [InlineData("http://host.example/a/b/1")]
    [InlineData("https://example.com/normal/playlist.m3u8")]
    public void Does_not_treat_arbitrary_three_segment_urls_as_xtream(string url)
    {
        Assert.Equal(url, CredentialSanitizer.SanitizeUrl(url));
    }

    [Fact]
    public void Preserves_explicit_port_8080_when_sanitizing_bare_xtream()
    {
        var sanitized = CredentialSanitizer.SanitizeUrl(
            "http://host.example:8080/201788541134/SECBarePwd9/777");

        Assert.Contains(":8080", sanitized);
    }

    [Theory]
    [InlineData("http://host/live/alice/SEC/1.ts", "/live/***/***/")]
    [InlineData("http://host/movie/alice/SEC/1.mkv", "/movie/***/***/")]
    [InlineData("http://host/series/alice/SEC/1.mp4", "/series/***/***/")]
    public void Marked_xtream_forms_still_use_their_marker(string url, string expectedFragment)
    {
        var sanitized = CredentialSanitizer.SanitizeUrl(url);

        Assert.Contains(expectedFragment, sanitized);
        Assert.DoesNotContain("SEC", sanitized);
    }

    [Fact]
    public void Userinfo_form_still_preserves_username_and_masks_password()
    {
        var sanitized = CredentialSanitizer.SanitizeUrl(
            "http://alice:SECPwd@host.example.com/live/alice/SECPwd/1.ts");

        Assert.Contains("alice:***@", sanitized);
        Assert.DoesNotContain("SECPwd", sanitized);
    }

    [Fact]
    public void Query_credential_params_still_masked()
    {
        var sanitized = CredentialSanitizer.SanitizeUrl(
            "http://host/get.php?username=alice&password=SECQ&token=tk");

        Assert.Contains("username=***", sanitized);
        Assert.Contains("password=***", sanitized);
        Assert.Contains("token=***", sanitized);
        Assert.DoesNotContain("SECQ", sanitized);
    }

    [Fact]
    public void SanitizeUrl_does_not_modify_the_raw_input()
    {
        const string raw = "http://host.example:8080/201788541134/SECBarePwd9/12345";

        var returned = CredentialSanitizer.SanitizeUrl(raw);

        Assert.Equal("http://host.example:8080/201788541134/SECBarePwd9/12345", raw);
        Assert.NotEqual(raw, returned);
        Assert.Equal("http://host.example:8080/***/***/12345", returned);
    }

    [Fact]
    public void SanitizeM3uContent_sanitizes_bare_xtream_urls()
    {
        var m3u = "#EXTM3U\n#EXTINF:-1 tvg-name=\"RTP1\" group-title=\"PT\",RTP1\n"
                + "http://neorcqds.example:8080/201788541134/SECBarePwd9/99999\n"
                + "#EXTINF:-1 tvg-name=\"SIC\" group-title=\"PT\",SIC\n"
                + "https://example.com/normal/playlist.m3u8\n";

        var sanitized = CredentialSanitizer.SanitizeM3uContent(m3u);

        Assert.DoesNotContain("201788541134", sanitized);
        Assert.DoesNotContain("SECBarePwd9", sanitized);
        Assert.Contains("/***/***/99999", sanitized);
        Assert.Contains("http://neorcqds.example:8080/***/***/99999", sanitized);
        Assert.Contains("https://example.com/normal/playlist.m3u8", sanitized);
        Assert.Contains("#EXTM3U", sanitized);
        Assert.Contains("#EXTINF", sanitized);
    }

    [Fact]
    public void SanitizeText_sanitizes_bare_xtream_url_inside_text()
    {
        var text = "Fonte encontrada em http://host.example:8080/201788541134/SECBarePwd9/4242 agora.";

        var sanitized = CredentialSanitizer.SanitizeText(text);

        Assert.DoesNotContain("201788541134", sanitized);
        Assert.DoesNotContain("SECBarePwd9", sanitized);
        Assert.Contains("/***/***/4242", sanitized);
        Assert.Contains("Fonte encontrada em", sanitized);
        Assert.Contains("agora.", sanitized);
    }
}
