using System;
using System.Reflection;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// DC-5d — Cobertura estrutural do HTML/JS do Dashboard para o sumário de
/// classificação do último <c>MatchPlan</c> (<c>GET /api/classification-summary</c>,
/// até agora inalcançável na UI). Verifica que a vista Dispatcharr tem o card
/// <c>#classificationSummary</c>/<c>#classificationStatus</c> depois de
/// <c>#dispatcharrOverview</c>, que o loader usa o contrato
/// <c>/api/classification-summary</c> via o helper <c>apiRequest</c> (DC-2),
/// que trata o estado <c>{error}</c> ("sem plano") de forma graciosa e que
/// escapa todo o output. O endpoint backend não é alterado.
/// </summary>
public class WaveDC5dClassificationSummaryUiHtmlTests
{
    private static string BuildDashboardHtml()
    {
        var method = typeof(WebDashboardService).GetMethod(
            "BuildDashboardHtml",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, null)!;
    }

    private static string Slice(string html, string from, string to)
    {
        var start = html.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Não encontrei '{from}' no HTML.");
        var end = html.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Não encontrei '{to}' depois de '{from}' no HTML.");
        return html.Substring(start, end - start);
    }

    [Fact]
    public void Classification_summary_card_exists_after_dispatcharr_overview()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("id='classificationSummary'", html);
        Assert.Contains("id='classificationStatus'", html);

        var overview = html.IndexOf("id='dispatcharrOverview'", StringComparison.Ordinal);
        var summary = html.IndexOf("id='classificationSummary'", StringComparison.Ordinal);
        Assert.True(overview >= 0 && summary > overview,
            "#classificationSummary deve estar depois de #dispatcharrOverview.");
    }

    [Fact]
    public void Classification_status_region_is_announced()
    {
        var html = BuildDashboardHtml();
        var status = Slice(html, "id='classificationStatus'", ">") + ">";
        Assert.Contains("aria-live='polite'", status);
    }

    [Fact]
    public void Classification_loader_uses_endpoint_via_apiRequest_and_escapes_output()
    {
        var html = BuildDashboardHtml();
        var loader = Slice(html,
            "async function loadClassificationSummary(",
            "async function loadDispatcharr(");

        Assert.Contains("/api/classification-summary", loader);
        Assert.Contains("apiRequest(", loader);

        // Renderiza as contagens por ChannelKind, o excludedCount, o
        // timestamp (tsLocal) e a fonte, e uma amostra com title/group/kind/reason.
        Assert.Contains("classification", loader);
        Assert.Contains("excludedCount", loader);
        Assert.Contains("tsLocal(", loader);
        Assert.Contains("planSourcePlaylistPath", loader);
        Assert.Contains("sample", loader);
        Assert.Contains("e.title", loader);
        Assert.Contains("e.group", loader);
        Assert.Contains("e.kind", loader);
        Assert.Contains("e.reason", loader);

        // Todo o output é escapado.
        Assert.Contains("escapeHtml(", loader);

        // O estado `{error}` (sem plano) é tratado como "sem dados", não como falha fatal.
        Assert.Contains("data.error", loader);
        Assert.Contains("Sem plano de classificação disponível.", loader);
    }

    [Fact]
    public void Classification_loader_is_wired_into_loadDispatcharr()
    {
        var html = BuildDashboardHtml();
        var loader = Slice(html, "async function loadDispatcharr(", "let dispatcharrActionBusy");
        Assert.Contains("loadClassificationSummary();", loader);
    }
}
