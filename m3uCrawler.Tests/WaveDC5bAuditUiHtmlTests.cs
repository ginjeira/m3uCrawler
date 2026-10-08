using System;
using System.Reflection;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// DC-5b — Cobertura estrutural do HTML/JS do Dashboard para o visualizador
/// de auditoria (<c>GET /api/audit</c>). Verifica a presença do sub-tab
/// "Auditoria", dos filtros e da tabela, que o loader usa o contrato
/// <c>/api/audit</c> com <c>objectType</c>/<c>objectId</c>/<c>limit</c> via o
/// helper <c>apiRequest</c> (DC-2), que trata explicitamente o 503
/// <c>audit-unavailable</c>, e que o conteúdo é escapado. A exportação para
/// <c>window</c> é reforçada por <see cref="DashboardInlineHandlerScopeTests"/>.
/// </summary>
public class WaveDC5bAuditUiHtmlTests
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
    public void Audit_sub_tab_filters_and_table_exist()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("data-ctab='audit'", html);
        Assert.Contains("id='ctab-audit'", html);
        Assert.Contains("id='auditObjectType'", html);
        Assert.Contains("id='auditObjectId'", html);
        Assert.Contains("id='auditLimit'", html);
        Assert.Contains("id='auditStatus'", html);
        Assert.Contains("id='auditTable'", html);

        // Default 100 e cap 1000 no input de limite.
        var limit = Slice(html, "id='auditLimit'", ">");
        Assert.Contains("max='1000'", limit);
        Assert.Contains("value='100'", limit);
    }

    [Fact]
    public void Audit_loader_uses_api_audit_with_filters_and_escapes_output()
    {
        var html = BuildDashboardHtml();
        var loader = Slice(html, "async function loadCatalogAudits(", "async function loadSources(");

        Assert.Contains("/api/audit?", loader);
        Assert.Contains("params.set('objectType'", loader);
        Assert.Contains("params.set('objectId'", loader);
        Assert.Contains("params.set('limit'", loader);
        Assert.Contains("apiRequest(", loader);

        // JSON before/after em detalhes expansíveis e sempre como texto escapado.
        Assert.Contains("escapeHtml(", loader);
        Assert.Contains("<details>", loader);
        Assert.Contains("beforeJson", loader);
        Assert.Contains("afterJson", loader);
    }

    [Fact]
    public void Audit_loader_handles_audit_unavailable_503()
    {
        var html = BuildDashboardHtml();
        var loader = Slice(html, "async function loadCatalogAudits(", "async function loadSources(");

        Assert.Contains("res.status === 503", loader);
        Assert.Contains("audit-unavailable", loader);
        Assert.Contains("setStatus(", loader);
    }

    [Fact]
    public void Audit_tab_is_wired_into_loadCatalogTab_and_exported()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("else if (tab === 'audit') loadCatalogAudits();", html);
        Assert.Contains("window.loadCatalogAudits = loadCatalogAudits;", html);
    }
}
