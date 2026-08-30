using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

public class WebDashboardHtmlTests
{
    [Fact]
    public void Dashboard_html_contains_navigation_menu()
    {
        // A página é renderizada por BuildDashboardHtml(). Usamos Reflection para
        // invocar o método sem HttpListener.
        var method = typeof(WebDashboardService).GetMethod(
            "BuildDashboardHtml",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var html = (string)method!.Invoke(null, null)!;

        Assert.Contains("Overview", html);
        Assert.Contains("Execuções", html);
        Assert.Contains("Descoberta", html);
        Assert.Contains("Canais / Países", html);
        Assert.Contains("Playlist", html);
        Assert.Contains("Dispatcharr", html);
        Assert.Contains("Diagnóstico", html);
        Assert.Contains("dispatcharr/state", html);  // /api/dispatcharr/state
        Assert.Contains("discovery/summary", html);
        Assert.Contains("execution/", html);
        Assert.Contains("output/inventory", html);
    }

    [Fact]
    public void Dashboard_html_uses_safe_text_in_pages_and_no_raw_credentials()
    {
        var method = typeof(WebDashboardService).GetMethod(
            "BuildDashboardHtml",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var html = (string)method!.Invoke(null, null)!;

        // O dashboard nunca deve incluir nenhum bloco de credenciais Xtream em claro
        // (no template). Apenas referenciar sanitização.
        Assert.DoesNotContain("userinfo_password", html);
        Assert.DoesNotContain("application/x-www-form-urlencoded", html);
        Assert.Contains("sanitiz", html, StringComparison.OrdinalIgnoreCase);
    }
}
