using System.Reflection;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W4 — Cobertura estrutural do HTML/JS do Dashboard para Scheduler, Ordering
/// Lists e Canonical Channels. Garante que o markup de edição existe e que os
/// novos handlers inline estão declarados (a exportação para <c>window</c> é
/// verificada por <see cref="DashboardInlineHandlerScopeTests"/>).
/// </summary>
public class WaveW4UiHtmlTests
{
    private static string BuildDashboardHtml()
    {
        var method = typeof(WebDashboardService).GetMethod(
            "BuildDashboardHtml",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, null)!;
    }

    [Fact]
    public void Ordering_edit_form_markup_and_handlers_exist()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("id='orderingEditForm'", html);
        Assert.Contains("data-ordering-edit='name'", html);
        Assert.Contains("data-ordering-edit='country'", html);
        Assert.Contains("data-ordering-edit='description'", html);
        Assert.Contains("data-ordering-edit='enabled'", html);

        Assert.Contains("function editOrderingList(", html);
        Assert.Contains("function saveOrderingListEdit(", html);
        Assert.Contains("function cancelOrderingListEdit(", html);
        Assert.Contains("window.editOrderingList = editOrderingList;", html);
        Assert.Contains("window.saveOrderingListEdit = saveOrderingListEdit;", html);
        Assert.Contains("window.cancelOrderingListEdit = cancelOrderingListEdit;", html);
    }

    [Fact]
    public void Ordering_duplicate_handler_quotes_key_in_js_context()
    {
        var html = BuildDashboardHtml();

        // A key é injectada como literal JS via JSON.stringify (e escapada
        // para o atributo HTML), não como string com escapeHtml — que
        // decodifica entidades e quebra a string JS com aspas.
        Assert.Contains("duplicateOrderingList(${l.id}, ${escapeAttr(JSON.stringify(l.key))})", html);
        Assert.DoesNotContain("duplicateOrderingList(${l.id}, \"${escapeHtml(l.key)}\")", html);
        Assert.Contains("window.duplicateOrderingList = duplicateOrderingList;", html);
        Assert.Contains("window.confirmDuplicateOrderingList = confirmDuplicateOrderingList;", html);
    }

    [Fact]
    public void Canonical_channel_create_form_exposes_category_and_group_selects()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("id='createChannelTitle'", html);
        Assert.Contains("id='createChannelSubmitBtn'", html);
        Assert.Contains("id='newChannelAliasesBlock'", html);
        Assert.Contains("id='newChannelCategory'", html);
        Assert.Contains("id='newChannelGroup'", html);
        Assert.Contains("id='newChannelPolicy'", html);

        // As opções de categoria continuam estáticas no formulário.
        Assert.Contains("<option value='Desporto'>Desporto</option>", html);

        // Wave C1 — o select de grupo passa a ser populado dinamicamente a
        // partir dos grupos canónicos configuráveis (não há opções estáticas).
        Assert.Contains("async function loadChannelGroupOptions(", html);
        Assert.Contains("'/api/catalog/canonical-groups'", html);
        Assert.Contains("<option value='${escapeAttr(g.key)}'>${escapeHtml(g.displayName || g.key)}</option>", html);
        Assert.DoesNotContain("<option value='PortugalDesporto'>PortugalDesporto</option>", html);

        // O mesmo formulário é reutilizado para edição.
        Assert.Contains("Editar Canal Canónico", html);
        Assert.Contains("function editChannelInline(", html);
    }

    [Fact]
    public void Scheduler_edit_and_new_handlers_exist()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("function editScheduledJob(", html);
        Assert.Contains("function newScheduledJob(", html);
        Assert.Contains("_schedJobsCache", html);
        Assert.Contains("window.editScheduledJob = editScheduledJob;", html);
        Assert.Contains("window.newScheduledJob = newScheduledJob;", html);
        Assert.Contains("onclick='newScheduledJob()'", html);
    }

    [Fact]
    public void Ordering_detail_row_renders_one_based_number_key_and_display()
    {
        var html = BuildDashboardHtml();

        // Coluna "#" 1-based (o position da DB/API continua 0-based).
        Assert.Contains("<td>${i.position + 1}</td>", html);
        // Coluna "Canal" a partir da key canónica, com fallback numérico.
        Assert.Contains("i.canonicalChannelKey || ('#' + i.canonicalChannelId)", html);
        // Coluna "Display" a partir do display name canónico.
        Assert.Contains("escapeHtml(i.canonicalChannelDisplayName || '—')", html);
    }
}
