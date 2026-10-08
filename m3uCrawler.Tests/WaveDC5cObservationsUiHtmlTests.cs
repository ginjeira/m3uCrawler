using System;
using System.Reflection;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// DC-5c — Cobertura estrutural do HTML/JS do Dashboard para o histórico de
/// observações de um <i>channel-source</i> (endpoints já existentes
/// <c>GET/POST /api/catalog/channel-sources/{id}/observations</c>, até agora
/// inalcançáveis na UI). Verifica o botão na tabela de Sources, o painel
/// modal, os selects dos enums, que o loader usa <c>GET</c> com <c>limit</c>
/// e o registo usa <c>POST</c> no mesmo caminho via o helper
/// <c>apiRequest</c> (DC-2), que o output é escapado e que os handlers são
/// exportados para <c>window</c> (exigido por
/// <see cref="DashboardInlineHandlerScopeTests"/>).
/// </summary>
public class WaveDC5cObservationsUiHtmlTests
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
    public void Channel_source_row_has_observations_button()
    {
        var html = BuildDashboardHtml();
        Assert.Contains("onclick='openChannelSourceObservations(${cs.id})'", html);
    }

    [Fact]
    public void Observations_panel_and_form_fields_exist()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("id='channelSourceObservationsPanel'", html);
        Assert.Contains("id='csObservationsTitle'", html);
        Assert.Contains("id='csObservationsStatus'", html);
        Assert.Contains("id='csObservationsTable'", html);
        Assert.Contains("aria-live='polite'", html);

        // Formulário de registo — selects para os três enums + input numérico.
        Assert.Contains("id='csObservationQuality'", html);
        Assert.Contains("id='csObservationEpg'", html);
        Assert.Contains("id='csObservationAvailability'", html);
        Assert.Contains("id='csObservationResponseTimeMs'", html);

        // Valores conhecidos dos enums (StreamQuality/EpgState/AvailabilityState).
        Assert.Contains("value='FourK'", html);
        Assert.Contains("value='Unavailable'", html);
        Assert.Contains("value='Discovered'", html);

        // Handlers inline do painel.
        Assert.Contains("onclick='recordChannelSourceObservation()'", html);
        Assert.Contains("onclick='loadChannelSourceObservations()'", html);
    }

    [Fact]
    public void Observations_loader_and_recorder_use_existing_endpoints()
    {
        var html = BuildDashboardHtml();
        var js = Slice(html, "async function openChannelSourceObservations(",
            "document.getElementById('channelSourceSourceFilter')");

        // GET com limit=200 no caminho do histórico.
        Assert.Contains("/observations?limit=200", js);
        Assert.Contains("apiRequest(", js);

        // POST no mesmo caminho, com o corpo do payload.
        Assert.Contains("method: 'POST'", js);
        Assert.Contains("payload.responseTimeMs", js);

        // Output sempre escapado.
        Assert.Contains("escapeHtml(", js);
        Assert.Contains("tsLocal(o.observedAtUtc)", js);

        // Estado de erro consistente (setStatus) e id corrente guardado.
        Assert.Contains("setStatus(", js);
        Assert.Contains("_csObservationsId", js);
    }

    [Fact]
    public void Observations_handlers_are_exported_to_window()
    {
        var html = BuildDashboardHtml();
        Assert.Contains("window.openChannelSourceObservations = openChannelSourceObservations;", html);
        Assert.Contains("window.loadChannelSourceObservations = loadChannelSourceObservations;", html);
        Assert.Contains("window.recordChannelSourceObservation = recordChannelSourceObservation;", html);
    }
}
