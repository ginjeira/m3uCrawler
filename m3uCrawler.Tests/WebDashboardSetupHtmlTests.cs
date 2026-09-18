using System;
using System.Reflection;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Wave 6 (PHASE 9C) — Marcadores HTML/JS da experiência de Setup no
/// dashboard. Não duplica o comportamento dos endpoints de setup (já
/// coberto por <c>SetupConfigEndpointTests</c>); verifica apenas que a
/// página servida expõe a vista, o banner de prontidão e que os POSTs do
/// setup levam o cabeçalho CSRF.
/// </summary>
public class WebDashboardSetupHtmlTests
{
    [Fact]
    public void Dashboard_html_contains_setup_view_and_readiness_markers()
    {
        var html = InvokeBuildDashboardHtml();

        Assert.Contains("SETUP REQUIRED", html);
        Assert.Contains("id='view-setup'", html);
        Assert.Contains("data-view='setup'", html);
        Assert.Contains("/api/configuration/readiness", html);
        Assert.Contains("/api/telegram/auth/start", html);
        Assert.Contains("/api/dispatcharr/test", html);
    }

    [Fact]
    public void Dashboard_html_setup_view_includes_telegram_and_dispatcharr_forms()
    {
        var html = InvokeBuildDashboardHtml();

        // Telegram
        Assert.Contains("id='setupTelegramApiId'", html);
        Assert.Contains("id='setupTelegramApiHash'", html);
        Assert.Contains("id='setupTelegramPhone'", html);
        Assert.Contains("id='setupTelegram2fa'", html);
        Assert.Contains("/api/telegram/config", html);
        Assert.Contains("/api/telegram/auth/code", html);
        Assert.Contains("/api/telegram/auth/password", html);
        Assert.Contains("/api/telegram/auth/status", html);

        // Dispatcharr
        Assert.Contains("id='setupDispatcharrEnabled'", html);
        Assert.Contains("id='setupDispatcharrBaseUrl'", html);
        Assert.Contains("id='setupDispatcharrApiKey'", html);
        Assert.Contains("id='setupDispatcharrDryRun'", html);
        Assert.Contains("/api/dispatcharr/config", html);

        // Estados mapeados do teste de ligação.
        Assert.Contains("AUTHENTICATION_FAILED", html);
        Assert.Contains("INVALID_CONFIGURATION", html);
    }

    [Fact]
    public void Dashboard_html_setup_post_helper_includes_csrf_header()
    {
        var html = InvokeBuildDashboardHtml();

        var start = html.IndexOf("function setupHeaders", StringComparison.Ordinal);
        Assert.True(start >= 0, "setupHeaders() não encontrado no HTML do dashboard.");
        var end = html.IndexOf("function setupFetch", start, StringComparison.Ordinal);
        Assert.True(end > start, "setupFetch() deve seguir setupHeaders().");

        var helper = html.Substring(start, end - start);
        Assert.Contains("X-CSRF-Token", helper);

        // Os POSTs do setup usam o helper (logo, herdam o cabeçalho CSRF).
        Assert.Contains("setupFetch('/api/telegram/config'", html);
        Assert.Contains("setupFetch('/api/telegram/auth/start'", html);
        Assert.Contains("setupFetch('/api/telegram/auth/code'", html);
        Assert.Contains("setupFetch('/api/telegram/auth/password'", html);
        Assert.Contains("setupFetch('/api/dispatcharr/config'", html);
        Assert.Contains("setupFetch('/api/dispatcharr/test'", html);
    }

    [Fact]
    public void Served_dashboard_page_exposes_csrf_token_to_the_setup_helper()
    {
        var method = typeof(WebDashboardService).GetMethod(
            "BuildHtmlPage",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var html = (string)method!.Invoke(null, new object?[] { "csrf-token-value" })!;

        Assert.Contains("X-CSRF-Token", html);
        Assert.Contains("csrf-token-value", html);
        Assert.Contains("__m3uCrawlerCsrf", html);
    }

    private static string InvokeBuildDashboardHtml()
    {
        var method = typeof(WebDashboardService).GetMethod(
            "BuildDashboardHtml",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, null)!;
    }
}
