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
    public void Canonical_channel_create_form_exposes_category_and_group_selects()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("id='createChannelTitle'", html);
        Assert.Contains("id='createChannelSubmitBtn'", html);
        Assert.Contains("id='newChannelAliasesBlock'", html);
        Assert.Contains("id='newChannelCategory'", html);
        Assert.Contains("id='newChannelGroup'", html);
        Assert.Contains("id='newChannelPolicy'", html);

        // As opções de categoria/grupo continuam presentes no formulário.
        Assert.Contains("<option value='Desporto'>Desporto</option>", html);
        Assert.Contains("<option value='PortugalDesporto'>PortugalDesporto</option>", html);

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
}
