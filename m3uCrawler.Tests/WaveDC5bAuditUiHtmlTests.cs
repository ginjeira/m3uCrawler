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

        // Antes/Depois abrem num modal de detalhe (botão "Ver"), não em
        // `<details>` dentro da célula (que ficava cortado pela largura
        // estreita da coluna + `table{overflow:hidden}`).
        Assert.Contains("escapeHtml(", loader);
        Assert.Contains("showAuditJson(", loader);
        Assert.Contains("<button", loader);
        Assert.DoesNotContain("<details>", loader);
        Assert.Contains("beforeJson", loader);
        Assert.Contains("afterJson", loader);
    }

    [Fact]
    public void Audit_json_detail_uses_modal_panel_and_export()
    {
        var html = BuildDashboardHtml();

        // Painel modal oculto no tab Auditoria + título/labels Antes/Depois.
        Assert.Contains("id='auditJsonModal'", html);
        Assert.Contains("id='auditJsonTitle'", html);
        Assert.Contains("id='auditJsonMeta'", html);
        Assert.Contains("id='auditJsonBefore'", html);
        Assert.Contains("id='auditJsonAfter'", html);

        // A função está declarada e exportada para `window` (handlers inline).
        Assert.Contains("function showAuditJson(", html);
        Assert.Contains("window.showAuditJson = showAuditJson;", html);
        Assert.Contains("openModalPanel('auditJsonModal')", html);
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
