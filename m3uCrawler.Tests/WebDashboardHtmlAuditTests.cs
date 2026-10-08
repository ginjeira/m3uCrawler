using System.Linq;
using System.Reflection;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

public class WebDashboardHtmlAuditTests
{
    [Fact]
    public void Dashboard_html_has_no_duplicate_ids()
    {
        var html = InvokeBuildHtml();
        // Match both single- and double-quoted id attributes.
        var matches = System.Text.RegularExpressions.Regex.Matches(
            html, @"id\s*=\s*(?:""([^""]+)""|'([^']+)')");

        var ids = matches
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
            .ToList();

        var dup = ids.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dup.Count == 0,
            $"Found duplicate id(s): {string.Join(", ", dup)}");
    }

    [Fact]
    public void Dashboard_html_does_not_duplicate_any_function_name()
    {
        // Each `function name(` should appear exactly once to avoid subtle shadowing.
        var html = InvokeBuildHtml();
        var matches = System.Text.RegularExpressions.Regex.Matches(
            html, @"function\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(");

        var names = matches.Select(m => m.Groups[1].Value).ToList();
        var dup = names.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dup.Count == 0,
            $"Duplicate function declarations: {string.Join(", ", dup)}");
    }

    [Fact]
    public void Dashboard_html_references_all_required_dependencies()
    {
        var html = InvokeBuildHtml();

        // Each tab needs a corresponding function and an element to mount into.
        // HTML uses single quotes for attribute values.
        Assert.Contains("id='view-overview'", html);
        Assert.Contains("loadOverview(", html);

        Assert.Contains("id='view-executions'", html);
        Assert.Contains("loadHistory(", html);
        Assert.Contains("showHistoryDetail(", html);

        Assert.Contains("id='view-discovery'", html);
        Assert.Contains("loadDiscovery(", html);
        Assert.Contains("renderDiscovery(", html);

        Assert.Contains("id='view-countries'", html);
        Assert.Contains("loadCountries(", html);
        Assert.Contains("loadCountryValidation(", html);

        Assert.Contains("id='view-playlist'", html);
        Assert.Contains("loadPlaylist(", html);

        Assert.Contains("id='view-dispatcharr'", html);
        Assert.Contains("loadDispatcharr(", html);
        Assert.Contains("'/api/dispatcharr/state'", html);

        Assert.Contains("id='view-diagnostics'", html);
        Assert.Contains("loadDiagnostics(", html);
    }

    [Fact]
    public void Dashboard_html_handles_dispatcharr_state_absent_gracefully()
    {
        // When /api/dispatcharr/state returns null/error, the JS must not throw.
        var html = InvokeBuildHtml();
        // Guard: only show reason when s && s.reason
        Assert.Contains("s && s.reason", html);
        // The "não activa" / "Não activa" guard badge
        Assert.True(html.Contains("não activa") || html.Contains("N\u00e3o activa"),
            "Expected a Portuguese 'not active' badge in the dispatcharr handler");
        // We must not require a present sync when reading version/etc.
        Assert.Contains("s.dispatcharrVersion", html);
    }

    [Fact]
    public void Dashboard_html_handles_run_report_absent_gracefully()
    {
        var html = InvokeBuildHtml();
        // The diagnostics view should not crash when /api/run-report/summary errors.
        Assert.Contains("Sem run report", html);
        // The execution detail click handler must validate idx bounds.
        Assert.Contains("idx < 0 || idx >= historyCache.length", html);
    }

    [Fact]
    public void Dashboard_diagnostics_inventory_is_written_only_by_loadDiagnostics()
    {
        var html = InvokeBuildHtml();

        // DC-4 — loadPlaylist must not write the diagnostics inventory node
        // (#diagInventory belongs to the Diagnóstico view only).
        var loadPlaylistStart = html.IndexOf("async function loadPlaylist(", StringComparison.Ordinal);
        Assert.True(loadPlaylistStart >= 0, "loadPlaylist não encontrada.");
        var loadPlaylistEnd = html.IndexOf("async function loadDiagnostics(", loadPlaylistStart, StringComparison.Ordinal);
        Assert.True(loadPlaylistEnd > loadPlaylistStart, "Fim de loadPlaylist não encontrado.");
        var loadPlaylistBody = html.Substring(loadPlaylistStart, loadPlaylistEnd - loadPlaylistStart);
        Assert.DoesNotContain("diagInventory", loadPlaylistBody);

        // The Playlist view has its own dedicated node for the file listing.
        Assert.Contains("id='playlistFiles'", html);
        Assert.Contains("document.getElementById('playlistFiles')", html);

        // loadDiagnostics remains the sole writer of #diagInventory.
        var diagnosticsStart = html.IndexOf("async function loadDiagnostics(", StringComparison.Ordinal);
        Assert.True(diagnosticsStart >= 0, "loadDiagnostics não encontrada.");
        var diagnosticsEnd = html.IndexOf("// ===== CATALOG =====", diagnosticsStart, StringComparison.Ordinal);
        Assert.True(diagnosticsEnd > diagnosticsStart, "Fim de loadDiagnostics não encontrado.");
        var diagnosticsBody = html.Substring(diagnosticsStart, diagnosticsEnd - diagnosticsStart);
        Assert.Contains("document.getElementById('diagInventory').innerHTML", diagnosticsBody);
    }

    [Fact]
    public void Dashboard_country_data_setup_deep_links_to_countries_view()
    {
        var html = InvokeBuildHtml();

        // DC-4 — setupConfigure('countryData') must open the Canais / Países
        // view (view-countries), not the Catalog view.
        var idx = html.IndexOf("if (key === 'countryData')", StringComparison.Ordinal);
        Assert.True(idx >= 0, "Mapeamento de countryData não encontrado.");
        var snippet = html.Substring(idx, Math.Min(80, html.Length - idx));
        Assert.Contains("showView('countries')", snippet);
        Assert.DoesNotContain("showView('catalog')", snippet);

        // The setup label remains the Portuguese "Dados de país".
        Assert.Contains("countryData: 'Dados de país'", html);
    }

    [Fact]
    public void Dashboard_live_run_does_not_duplicate_scheduled_jobs_listing()
    {
        var html = InvokeBuildHtml();

        // DC-4 — the inline scheduled-jobs loader/table was removed from
        // Live Run; the listing lives only in Catálogo → Scheduled Jobs.
        Assert.DoesNotContain("loadLiveRunScheduled", html);
        Assert.DoesNotContain("id='liveRunScheduled'", html);

        // A deep-link into the Catalog "scheduled" sub-tab replaces it.
        Assert.Contains("function openScheduledJobs(", html);
        Assert.Contains("loadCatalogTab('scheduled')", html);
        Assert.Contains("onclick='openScheduledJobs()'", html);

        // The explanatory text about the Telegram actions remains.
        Assert.Contains("telegramRun", html);
        Assert.Contains("telegramMaintainRun", html);
    }

    private static string InvokeBuildHtml()
    {
        var method = typeof(WebDashboardService).GetMethod(
            "BuildDashboardHtml",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, null)!;
    }
}
