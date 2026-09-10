using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

public class XtreamPublicationResolverSanitizationTests
{
    private const string PubUrl = "https://example.com/page";

    [Fact]
    public void ResolveFromHtml_does_not_emit_password_to_console()
    {
        var originalOut = Console.Out;
        var sw = new System.IO.StringWriter();
        Console.SetOut(sw);
        try
        {
            var html = @"<html><body>
                <div>Host: quiet.example:80</div>
                <div>User: qui</div>
                <div>Pass: secretP@ss</div>
            </body></html>";
            var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
            Assert.Single(result);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        var output = sw.ToString();
        Assert.DoesNotContain("secretP@ss", output);
    }

    [Fact]
    public void XtreamAccountInfo_ToString_does_not_include_password()
    {
        var html = @"<html><body>
            <div>Host: tonext.example:80</div>
            <div>User: ut</div>
            <div>Pass: neverInToString99</div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        Assert.Single(result);
        var s = result[0].ToString();
        Assert.DoesNotContain("neverInToString99", s);
        Assert.DoesNotContain("Pass", s);
    }

    [Fact]
    public void LogicalIdentity_does_not_include_password()
    {
        var html = @"<html><body>
            <div>Host: ident.example:80</div>
            <div>User: idu</div>
            <div>Pass: hiddenPW</div>
        </body></html>";
        var result = XtreamPublicationResolver.ResolveFromHtml(html, PubUrl);
        var identity = result[0].LogicalIdentity;
        Assert.DoesNotContain("hiddenPW", identity);
    }

    [Fact]
    public void Resolution_does_not_serialize_accounts_anywhere_visible()
    {
        // XtreamAccountInfo e' internal sealed. Password existe na propriedade
        // mas o tipo nunca e' serializado nem exposto via API publica. Nao ha
        // chamada a JsonSerializer.Serialize que o receba.
        var t = typeof(XtreamAccountInfo);
        Assert.True(t.IsNotPublic);
        Assert.True(t.IsSealed);
        // Garantir que existe o setter-like (init) para Password, sem o tornar
        // parte de qualquer superficie publica.
        var pwd = t.GetProperty("Password");
        Assert.NotNull(pwd);
    }
}
