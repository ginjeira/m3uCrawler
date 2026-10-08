using System.Reflection;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// DC-7 (acessibilidade, UX e limpeza) e DC-12 ("mover para posição N") —
/// asserções estruturais sobre o HTML/JS servido pelo Dashboard. Não substitui
/// os testes de comportamento de DC-8 (<c>DashboardJsSmokeTests</c>), que
/// cobrem o restauro de foco e o PUT zerado do mover.
/// </summary>
public class WaveDC7Dc12DashboardUiHtmlTests
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
    public void Dashboard_has_no_debug_console_and_no_native_prompt()
    {
        var html = BuildDashboardHtml();

        // Guarda contra a reintrodução do ruído de debug e de diálogos nativos.
        Assert.DoesNotContain("[DEBUG]", html);
        Assert.DoesNotContain("prompt(", html);
    }

    [Fact]
    public void Modal_captures_and_restores_element_focus()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("_modalPrevFocus = document.activeElement", html);
        Assert.Contains("_modalPrevFocus.focus()", html);
        // O restauro nunca deve lançar se o elemento entretanto desaparecer.
        Assert.Contains("try { _modalPrevFocus.focus(); } catch (e) {}", html);
    }

    [Fact]
    public void Channel_policy_uses_modal_select_instead_of_native_dialog()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("id='channelPolicyForm'", html);
        Assert.Contains("id='channelPolicySelect'", html);
        Assert.Contains("<option value='CreateEligible'>CreateEligible</option>", html);
        Assert.Contains("<option value='MergeOnly'>MergeOnly</option>", html);
        Assert.Contains("<option value='ReviewOnly'>ReviewOnly</option>", html);
        Assert.Contains("<option value='Excluded'>Excluded</option>", html);
        Assert.Contains("function confirmChannelPolicy(", html);
        Assert.Contains("openModalPanel('channelPolicyForm')", html);
        Assert.Contains("window.confirmChannelPolicy = confirmChannelPolicy;", html);
    }

    [Fact]
    public void Duplicate_ordering_list_uses_modal_input()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("id='orderingDuplicateForm'", html);
        Assert.Contains("id='orderingDuplicateKey'", html);
        Assert.Contains("function confirmDuplicateOrderingList(", html);
        Assert.Contains("openModalPanel('orderingDuplicateForm')", html);
        Assert.Contains("window.confirmDuplicateOrderingList = confirmDuplicateOrderingList;", html);
        // Default derivado da key original.
        Assert.Contains("'-copy'", html);
    }

    [Fact]
    public void Ordering_has_move_to_position_control_and_handler()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("orderingMovePos-", html);
        Assert.Contains("aria-label='Mover para posição'", html);
        Assert.Contains("onclick='moveOrderingItemToPosition(", html);
        Assert.Contains("function moveOrderingItemToPosition(", html);
        Assert.Contains("window.moveOrderingItemToPosition = moveOrderingItemToPosition;", html);
        // Reutiliza o PUT existente (sem alterações de backend).
        Assert.Contains("'/api/catalog/ordering-items/' + itemId", html);
    }

    [Fact]
    public void Reviews_pager_buttons_and_exports_remain_present()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("id='reviewsPrevBtn'", html);
        Assert.Contains("id='reviewsNextBtn'", html);
        Assert.Contains("onclick='reviewsPrevPage()'", html);
        Assert.Contains("onclick='reviewsNextPage()'", html);
        Assert.Contains("window.reviewsPrevPage = reviewsPrevPage;", html);
        Assert.Contains("window.reviewsNextPage = reviewsNextPage;", html);
    }
}
