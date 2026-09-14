using System.Text;
using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Cobre o fallback URL-only de XtreamPublicationResolver.ResolveFromHtml
/// (introduzido em 2026-09-14): quando os caminhos DOM-based e flat-text nao
/// produzem nenhuma conta mas o HTML contem URLs /get.php com username e
/// password, o fallback extrai uma <see cref="XtreamAccountInfo"/> por
/// combinacao unica (host:port, username). Cobertura:
///
///   1. formato antigo (com labels) NAO regride (cai no caminho DOM-based);
///   2. formato novo sem labels (110751-like) resolve via fallback;
///   3. multiplas URLs para o mesmo (host:port, username) deduplicam;
///   4. portas explicitas vs implicitas (80 default);
///   5. parametros de query em ordem diferente sao ambos reconhecidos;
///   6. URL invalida/incompleta ignorada silenciosamente;
///   7. URL sem username ignorada;
///   8. URL sem password ignorada;
///   9. HTML vazio, sem URLs, devolve 0 contas;
///  10. URL contendo apenas texto parecido (ex. &quot;username-like&quot;) nao
///      e&#39; capturado como conta.
///
/// Todas as fixtures usam credenciais ficticias (***/MASKED) para nao expor
/// credenciais reais em testes.
/// </summary>
public class XtreamPublicationResolverUrlOnlyFallbackTests
{
    private const string MaskedPass = "***";
    private const string PubUrl = "m3u@scan_14-09-2026.html";

    private static string MaskedUrl(string host, int port, string user)
        => $"http://{host}:{port}/get.php?username={user}&password={MaskedPass}";

    [Fact]
    public void Legacy_format_with_labels_still_resolves_via_dom_path()
    {
        // Formato "antigo" (analogous to 110705): HTML com div class=card
        // contendo Host/User/Pass. O caminho DOM-based deve produzir as contas
        // sem passar pelo fallback URL-only. Nenhuma URL get.php visivel.
        var html = @"<html><body>
            <div class='card'>
                <span>Host</span>: legacy.example:8080<br>
                <span>User</span>: legacyUser<br>
                <span>Pass</span>: legacyPass<br>
            </div>
            <hr>
            <div class='card'>
                <span>Host</span>: legacy.example:8080<br>
                <span>User</span>: legacyUser2<br>
                <span>Pass</span>: legacyPass2<br>
            </div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Equal(2, result.Count);
        Assert.All(result, a => Assert.Equal("legacy.example", a.Host));
        Assert.All(result, a => Assert.Equal(8080, a.Port));
        Assert.Contains(result, a => a.Username == "legacyUser");
        Assert.Contains(result, a => a.Username == "legacyUser2");
    }

    [Fact]
    public void Url_only_format_like_110751_resolves_via_fallback()
    {
        // Formato "novo" (analogous to 110751): apenas URLs get.php, sem labels.
        var html = @"<!DOCTYPE html>
<html><body>
<h1>M3U-Scan 14-09-2026</h1>
<div class='content' id='playlist'>
 http://onlyhost.example:8080/get.php?username=userA&password=" + MaskedPass + @"
 http://onlyhost.example:8080/get.php?username=userB&password=" + MaskedPass + @"
 http://onlyhost.example:8080/get.php?username=userC&password=" + MaskedPass + @"
</div>
</body></html>";

        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Equal(3, result.Count);
        Assert.All(result, a => Assert.Equal("onlyhost.example", a.Host));
        Assert.All(result, a => Assert.Equal(8080, a.Port));
        var users = result.Select(a => a.Username).OrderBy(u => u).ToArray();
        Assert.Equal(new[] { "userA", "userB", "userC" }, users);
    }

    [Fact]
    public void Multiple_urls_same_endpoint_and_user_are_deduplicated()
    {
        // Tres URLs exactamente iguais: uma unica conta deve ser produzida.
        var html = @"<html><body>
            " + MaskedUrl("dup.example", 8080, "dupuser1") + @"
            " + MaskedUrl("dup.example", 8080, "dupuser1") + @"
            " + MaskedUrl("dup.example", 8080, "dupuser1") + @"
            " + MaskedUrl("dup.example", 8080, "dupuser2") + @"
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "dupuser1", "dupuser2" }, result.Select(a => a.Username).OrderBy(u => u));
    }

    [Fact]
    public void Implicit_port_uses_scheme_default()
    {
        // Sem porta explicita no URL: https://.../get.php -> port 443.
        var html = @"<html><body>
            https://ssl.example/get.php?username=suser&password=" + MaskedPass + @"
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Single(result);
        Assert.Equal("ssl.example", result[0].Host);
        Assert.Equal(443, result[0].Port);
        Assert.Equal("https", result[0].Scheme);
        Assert.Equal("suser", result[0].Username);
    }

    [Fact]
    public void Query_parameters_in_different_order_are_recognized()
    {
        // type= vem antes de password= (ordem diferente da canonica).
        // O fallback nao exige uma ordem especifica; deve reconhecer ambos.
        var html = @"<html><body>
            http://order.example:8080/get.php?type=m3u_plus&username=orderuser&password=" + MaskedPass + @"
            http://order.example:8080/get.php?output=ts&password=" + MaskedPass + @"&username=orderuser2
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, a => a.Username == "orderuser");
        Assert.Contains(result, a => a.Username == "orderuser2");
    }

    [Fact]
    public void Malformed_or_incomplete_urls_are_silently_ignored()
    {
        // URL sem path /get.php: NAO e&#39; considerada (o parser exige /get.php).
        // URL com espacos dentro do URL: NAO e&#39; capturada.
        // URL completamente invalida: ignorada.
        var html = @"<html><body>
            https://noscheme.example/anything.html?username=shouldbeignored&password=" + MaskedPass + @"
            <invalid url>
            https://valid.example:8080/get.php?username=validuser&password=" + MaskedPass + @"
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        // Apenas a URL valida produz conta.
        Assert.Single(result);
        Assert.Equal("validuser", result[0].Username);
    }

    [Fact]
    public void Url_without_username_parameter_is_ignored()
    {
        // Tem get.php e password=, mas falta username= (NAO pode ser conta sem username).
        var html = @"<html><body>
            http://nokey.example:8080/get.php?password=" + MaskedPass + @"
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Empty(result);
    }

    [Fact]
    public void Url_without_password_parameter_is_ignored()
    {
        // Tem get.php e username=, mas falta password=.
        var html = @"<html><body>
            http://nopass.example:8080/get.php?username=nopassuser
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Empty(result);
    }

    [Fact]
    public void Html_without_any_account_signals_returns_zero()
    {
        var html = @"<!DOCTYPE html>
<html><body>
<h1>Generic Page</h1>
<p>No Xtream data here. Just some placeholder text and a
link to https://other.example/something-else.html.</p>
</body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Empty(result);
    }

    [Fact]
    public void Text_mentioning_username_but_not_in_real_url_is_not_picked_up()
    {
        // Texto que parece username= mas NAO esta num URL get.php real.
        // "username=foo" aparece como texto puro. Nenhuma URL tem /get.php?
        // portanto nada deve ser capturado.
        var html = @"<html><body>
            This is just explanatory text. The username=foo and
            password=" + MaskedPass + @" appear in the prose only.
            There is no real URL here.
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Empty(result);
    }

    [Fact]
    public void Realistic_sanitized_110751_like_fixture_resolves()
    {
        // Fixture construida a partir da estrutura real observada no HTML da
        // 110751 (capsula head/cabecalho estilo m3u-sᴄᴀɴ), com credenciais
        // FICTICIAS (sanitized). Esperado: 4 contas distintas.
        var html = @"<!DOCTYPE html>
<html lang='de'>
<head>
  <meta charset='UTF-8'>
  <title>M3U-Scan-SANITIZED-14-09-2026</title>
  <style>body { background: #000; color: #0f0; font-family: monospace; }</style>
</head>
<body>
  <div class='header'>
    <h1>M3U-Scan-SANITIZED-14-09-2026</h1>
    <div class='subtitle'>Sanitised fixture for XtreamPublicationResolver URL-only fallback regression.</div>
  </div>
  <div class='content' id='playlist'>
    http://sanitized.example:8080/get.php?username=alpha&password=" + MaskedPass + @"
    ⚠️ Sanitized note (decorative Unicode, no real account)
    http://sanitized.example:8080/get.php?username=beta&password=" + MaskedPass + @"
    More decorative unicode: 🔒🗝️💥
    http://sanitized.example:8080/get.php?username=gamma&password=" + MaskedPass + @"
    Press Ctrl+A then Ctrl+C to copy
    http://sanitized.example:8080/get.php?username=alpha&password=" + MaskedPass + @"
  </div>
</body></html>";

        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        // 4 URLs -> 3 usernames distintos (alpha aparece 2x; dedup esperado).
        Assert.Equal(3, result.Count);
        Assert.All(result, a => Assert.Equal("sanitized.example", a.Host));
        Assert.All(result, a => Assert.Equal(8080, a.Port));
        var users = result.Select(a => a.Username).OrderBy(u => u).ToArray();
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, users);
    }
}
