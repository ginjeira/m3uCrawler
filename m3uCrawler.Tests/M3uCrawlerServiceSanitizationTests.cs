using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Regressao contra BUG-SEC-001: o m3uCrawlerService nao pode imprimir
/// URLs Xtream com credenciais reais em Console.WriteLine.
/// </summary>
public class M3uCrawlerServiceSanitizationTests
{
    [Fact]
    public void SanitizeUrl_redacts_username_password_token_in_query()
    {
        var raw = "http://exemplo.com/get.php?username=alice&password=secret123&token=tok_abc&type=m3u_plus";
        var safe = CredentialSanitizer.SanitizeUrl(raw);

        Assert.DoesNotContain("alice", safe);
        Assert.DoesNotContain("secret123", safe);
        Assert.DoesNotContain("tok_abc", safe);
        Assert.Contains("exemplo.com", safe);
        Assert.Contains("get.php", safe);
        Assert.Contains("type=m3u_plus", safe);
    }

    [Fact]
    public void SanitizeUrl_redacts_password_in_userinfo_and_query()
    {
        // O sanitizer preserva o username no userinfo mas oculta sempre o password (em
        // qualquer posição). Isto garante que `password=secret123` e `alice:secret123@`
        // nunca aparecem em logs/diagnosticos. O username em userinfo e em query
        // (caso nao coexistam) e redacted na query (regra canonica do projecto).
        var raw = "http://alice:secret123@exemplo.com:8080/get.php?password=secret123&type=m3u_plus";
        var safe = CredentialSanitizer.SanitizeUrl(raw);

        Assert.DoesNotContain("secret123", safe);
        Assert.Contains("exemplo.com", safe);
        Assert.Contains("type=m3u_plus", safe);
        // Forma esperada: password em userinfo redacted, password em query redacted.
        Assert.Contains("***", safe);
    }

    [Fact]
    public void SanitizeUrl_handles_empty_and_null_safely()
    {
        Assert.Equal(string.Empty, CredentialSanitizer.SanitizeUrl(null));
        Assert.Equal(string.Empty, CredentialSanitizer.SanitizeUrl(""));
        Assert.Equal("   ", CredentialSanitizer.SanitizeUrl("   "));
    }

    [Fact]
    public void Authenticated_url_pattern_built_by_service_redacts_password_after_sanitize()
    {
        // Regressao directa para BUG-SEC-001. O m3uCrawlerService constroi URLs
        // Xtream no formato `${origin}/get.php?username=USER&password=PASS&type=m3u_plus`.
        // Antes da correccao, esse URL era impresso tal-qual em Console.WriteLine
        // (linhas 108 e 118 de M3uCrawlerService.cs), expondo o password em claro.
        // Apos a correccao, a string passada para Console.WriteLine e a URL sanitizada.
        var origin = "http://exemplo.com:8080";
        var username = "alice";
        var password = "secret123";
        var m3uUrl = $"{origin}/get.php?username={Uri.EscapeDataString(username)}&password={Uri.EscapeDataString(password)}&type=m3u_plus";
        var safe = CredentialSanitizer.SanitizeUrl(m3uUrl);

        Assert.DoesNotContain(password, safe);
        Assert.Contains("exemplo.com", safe);
        Assert.Contains("type=m3u_plus", safe);
    }
}
