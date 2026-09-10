using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Testes sinteticos para o parser flat-text adaptativo do
/// XtreamPublicationResolver. Estes testes nao dependem de HTML real;
/// validam que o parser absorve variacoes de formato (separadores,
/// labels Unicode, layouts) sem alteracao de codigo.
///
/// Cada teste documenta o comportamento actual, incluindo limitacoes
/// conhecidas. Os testes que falham documentam casos limite; quando o
/// parser e' melhorado, esses testes sao actualizados.
/// </summary>
public class XtreamPublicationResolverAdaptiveParsingTests
{
    [Fact]
    public void Resolves_account_with_arrow_separator()
    {
        var html = @"
            <html><body>
            <pre>
            Host  -> http://example.com:8080
            User  -> alice
            Pass  -> secret123
            M3U URL -> http://example.com:8080/get.php?username=alice&password=secret123&type=m3u_plus
            </pre>
            </body></html>";

        var accounts = XtreamPublicationResolver.ResolveFromHtml(html, "https://example.com/page.html");

        // NOTA: este teste falha actualmente porque o regex do parser requer
        // que o label seja seguido por whitespace OU um separador, e o valor
        // seguinte deve ser "limpo". O separador `-> ` e' seguido por letra
        // (http) e o value-extractor strip-leading-arrow nao trata o caso
        // de o value estar "encostado" ao arrow.
        // Documentamos a limitacao: publisher com `Label -> Value` nao e'
        // suportado. Para suportar, mudar o regex para consumir o separador
        // e whitespace adjacente.
        Assert.NotEmpty(accounts);
    }

    [Fact]
    public void Resolves_account_with_pipe_separator()
    {
        var html = @"
            <html><body>
            <pre>
            Host | http://example.com:80
            User | bob
            Pass | hunter2
            </pre>
            </body></html>";

        var accounts = XtreamPublicationResolver.ResolveFromHtml(html, "https://example.com/page.html");

        var acc = Assert.Single(accounts);
        Assert.Equal("bob", acc.Username);
        Assert.Equal("hunter2", acc.Password);
    }

    [Fact]
    public void Resolves_account_with_unicode_label_PT()
    {
        var html = @"
            <html><body>
            <pre>
            Host: http://example.com:8080
            Usuário: carlos
            Senha: minhaSenha
            </pre>
            </body></html>";

        var accounts = XtreamPublicationResolver.ResolveFromHtml(html, "https://example.com/page.html");

        var acc = Assert.Single(accounts);
        Assert.Equal("carlos", acc.Username);
        Assert.Equal("minhaSenha", acc.Password);
    }

    [Fact]
    public void Resolves_account_with_label_on_one_line_value_on_next()
    {
        var html = @"
            <html><body>
            <pre>
            Host
            http://example.com:8080

            User
            dave

            Pass
            p@ssw0rd
            </pre>
            </body></html>";

        var accounts = XtreamPublicationResolver.ResolveFromHtml(html, "https://example.com/page.html");

        var acc = Assert.Single(accounts);
        Assert.Equal("example.com", acc.Host);
        Assert.Equal(8080, acc.Port);
        Assert.Equal("dave", acc.Username);
        Assert.Equal("p@ssw0rd", acc.Password);
    }

    [Fact]
    public void Ignores_free_text_that_mentions_Host_User_Pass_without_valid_host()
    {
        // Texto que menciona os 3 termos mas com Host invalido. O parser deve
        // rejeitar porque `TryParseHost` valida formato (precisa de TLD .).
        var html = @"
            <html><body>
            <p>This is a blog post about networking. The article discusses
            host names, user accounts, and password policies. It does not
            publish any credentials. Host: not-a-real-host. The author is on
            vacation. User: someone. Pass: whatever.</p>
            <p>Nothing here is an Xtream publication.</p>
            </body></html>";

        var accounts = XtreamPublicationResolver.ResolveFromHtml(html, "https://example.com/blog.html");

        Assert.Empty(accounts);
    }

    [Fact]
    public void Resolves_account_with_unicode_label_in_HTML_card_no_table()
    {
        // HTML sem <table>, <hr>, class=card. So o <pre> com labels fancy.
        var html = @"
            <html><body>
            <pre>
Hᴏsᴛ  http://example.com:8080
Usᴇʀ  eve
Pᴀss  pass123
            </pre>
            </body></html>";

        var accounts = XtreamPublicationResolver.ResolveFromHtml(html, "https://example.com/page.html");

        var acc = Assert.Single(accounts);
        Assert.Equal("example.com", acc.Host);
        Assert.Equal(8080, acc.Port);
        Assert.Equal("eve", acc.Username);
        Assert.Equal("pass123", acc.Password);
    }

    [Fact]
    public void Multiple_accounts_with_host_boundary_split_into_distinct_cards()
    {
        // Cada card comeca com 'Host' explicito, o que actua como boundary
        // mesmo quando as contas estao dentro da janela de clustering. Cards
        // adjacentes sao separados pelo 'Host' repetido do card seguinte.
        var html = @"
            <html><body>
            <pre>
Host  http://server.example:8080
User  alice
Pass  secret1
Host  http://server.example:8080
User  bob
Pass  secret2
            </pre>
            </body></html>";

        var accounts = XtreamPublicationResolver.ResolveFromHtml(html, "https://example.com/page.html");

        Assert.Equal(2, accounts.Count);
        Assert.Equal("alice", accounts[0].Username);
        Assert.Equal("bob", accounts[1].Username);
    }
}
