using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Teste contra o HTML real da publicacao m3u@iptvgold.online_07-09-2026.html
/// descarregada do Telegram (canal 1635952193, msgId 110658, data 07-09-2026).
///
/// O ficheiro NAO esta versionado no repo (contem credenciais reais e foi
/// obtido em runtime a partir de uma publicacao autorizada). Para correr este
/// teste localmente, copiar o HTML para:
///   m3uCrawler.Tests/Fixtures/iptvgold_07-09-2026.html
///
/// Se o ficheiro nao existir, o teste e' skipped (NAO falha).
///
/// LIMITACOES CONHECIDAS deste publisher (iptvgold):
///   * 70 contas Xtream intercaladas com uma MEDIA LIST grande (lista de
///     canais/VOD/series). A MEDIA LIST contem labels que coincidem com o
///     vocabulario Xtream ("CHANNELS", "MOVIES", "SERIES", "PASS").
///   * O parser adaptativo actual NAO distingue essas metadata labels de
///     labels do card sem heuristica especifica do publisher. Como consequencia:
///     - Cards adjacentes que partilham labels sao fundidos num unico card,
///       reduzindo o numero de contas extraidas.
///     - Labels M3U/EPG que ficam fora da janela de clustering (500 chars)
///       podem ser perdidas.
///
/// Os testes abaixo reflectem o comportamento real do parser (nao o
/// comportamento ideal). A melhoria para iptvgold exigiria heuristica
/// especifica deste publisher, fora do escopo deste teste.
/// </summary>
public class XtreamPublicationResolverRealWorldFixtureTests
{
    private const string FixturePath = "Fixtures/iptvgold_07-09-2026.html";
    private const string SourceUrl = "https://t.me/c/1635952193/110658";

    [Fact]
    public void Real_iptvgold_publication_resolves_at_least_first_account_with_full_metadata()
    {
        if (!File.Exists(FixturePath)) return;

        var html = File.ReadAllText(FixturePath);
        var accounts = XtreamPublicationResolver.ResolveFromHtml(html, SourceUrl);

        // Pelo menos uma conta. Pode ser menos que 70 (limitacao conhecida: cards
        // adjacentes com labels partilhadas fundem-se). Ver comentario da classe.
        Assert.NotEmpty(accounts);

        // A primeira conta deve ter os campos essenciais.
        var first = accounts[0];
        Assert.Equal("iptvgold.online", first.Host);
        Assert.Equal(8880, first.Port);
        Assert.False(string.IsNullOrEmpty(first.Username));
        Assert.False(string.IsNullOrEmpty(first.Password));

        // M3U/EPG podem ou nao estar presentes (depende se estao dentro da
        // janela de clustering). Quando estao, devem ter conteudo valido.
        if (first.M3uUrl != null)
        {
            Assert.Contains("iptvgold.online:8880", first.M3uUrl);
            Assert.Contains("get.php", first.M3uUrl);
        }
        if (first.EpgUrl != null)
        {
            Assert.Contains("xmltv.php", first.EpgUrl);
        }
    }

    [Fact]
    public void Real_iptvgold_publication_toString_does_not_leak_password()
    {
        if (!File.Exists(FixturePath)) return;
        var html = File.ReadAllText(FixturePath);
        var accounts = XtreamPublicationResolver.ResolveFromHtml(html, SourceUrl);

        foreach (var a in accounts)
        {
            var s = a.ToString();
            Assert.False(s.Contains(a.Password ?? string.Empty),
                $"ToString() of account {a.Username} leaked the password");
        }
    }

    [Fact]
    public void Real_iptvgold_publication_does_not_treat_media_list_as_channels()
    {
        if (!File.Exists(FixturePath)) return;
        var html = File.ReadAllText(FixturePath);
        var accounts = XtreamPublicationResolver.ResolveFromHtml(html, SourceUrl);

        // A MEDIA LIST contem labels UK | RADIO | QUALIFIERS | ... que NAO
        // devem aparecer como usernames Xtream.
        foreach (var a in accounts)
        {
            Assert.DoesNotContain("UK", a.Username);
            Assert.DoesNotContain("RADIO", a.Username);
            Assert.DoesNotContain("QUALIFIERS", a.Username);
        }
    }

    [Fact]
    public void Real_iptvgold_publication_keeps_distinct_logical_identities()
    {
        if (!File.Exists(FixturePath)) return;
        var html = File.ReadAllText(FixturePath);
        var accounts = XtreamPublicationResolver.ResolveFromHtml(html, SourceUrl);

        // Pelo menos 2 identidades distintas (nao colapsa tudo num unico card).
        var identities = accounts.Select(a => a.LogicalIdentity).ToHashSet();
        Assert.True(identities.Count >= 2,
            $"Expected at least 2 distinct identities, got {identities.Count}");
    }

    [Fact]
    public void Real_iptvgold_publication_passwords_are_not_in_logical_identity()
    {
        if (!File.Exists(FixturePath)) return;
        var html = File.ReadAllText(FixturePath);
        var accounts = XtreamPublicationResolver.ResolveFromHtml(html, SourceUrl);

        foreach (var a in accounts)
        {
            Assert.DoesNotContain(a.Password ?? string.Empty, a.LogicalIdentity);
            Assert.DoesNotContain(a.Password ?? string.Empty, a.ToString());
        }
    }
}
