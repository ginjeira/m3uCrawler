using System;
using System.Reflection;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W6 — Cobertura estrutural do HTML/JS do Dashboard para a UI Dispatcharr
/// (Dry Run + Sync real) e para a gestão de configuração de país. A
/// exportação para <c>window</c> é reforçada por
/// <see cref="DashboardInlineHandlerScopeTests"/>.
/// </summary>
public class WaveW6DispatcharrUiHtmlTests
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
    public void Dispatcharr_tab_has_action_container_and_both_buttons()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("id='dispatcharrActionStatus'", html);
        Assert.Contains("id='dispatcharrDryRunBtn'", html);
        Assert.Contains("id='dispatcharrSyncBtn'", html);
        Assert.Contains("onclick='runDispatcharrDryRun()'", html);
        Assert.Contains("onclick='runDispatcharrSync()'", html);
    }

    [Fact]
    public void Dispatcharr_sync_has_strong_confirmation_dry_run_does_not()
    {
        var html = BuildDashboardHtml();

        var dry = Slice(html, "async function runDispatcharrDryRun(", "async function runDispatcharrSync(");
        Assert.DoesNotContain("confirm(", dry);

        var sync = Slice(html, "async function runDispatcharrSync(", "async function loadDiagnostics(");
        Assert.Contains("confirm(", sync);
        Assert.Contains("mutação REAL", sync);
    }

    [Fact]
    public void Dispatcharr_handlers_have_anti_double_click_busy_guard()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("dispatcharrActionBusy", html);
        // Ambos os botões são desactivados enquanto corre.
        Assert.Contains("dry.disabled = busy", html);
        Assert.Contains("sync.disabled = busy", html);
    }

    [Fact]
    public void Dispatcharr_handlers_are_exported_to_window()
    {
        var html = BuildDashboardHtml();

        Assert.Contains("window.runDispatcharrDryRun = runDispatcharrDryRun;", html);
        Assert.Contains("window.runDispatcharrSync = runDispatcharrSync;", html);
    }

    [Fact]
    public void Dispatcharr_payload_uses_canonical_playlist()
    {
        var html = BuildDashboardHtml();

        var action = Slice(html, "async function runDispatcharrAction(", "async function runDispatcharrDryRun(");
        Assert.Contains("playlistPath: 'playlist.m3u'", action);
        Assert.Contains("'/api/dispatcharr/dry-run'", html);
        Assert.Contains("'/api/dispatcharr/sync'", html);
    }

    [Fact]
    public void Dispatcharr_result_renders_counts_and_paths_and_errors_are_not_masked()
    {
        var html = BuildDashboardHtml();

        var render = Slice(html, "function renderDispatcharrActionResult(", "async function runDispatcharrAction(");
        foreach (var field in new[] { "matched", "newChannels", "newStreams", "removedStreams", "skipped", "ambiguous", "unchanged", "failed" })
        {
            Assert.Contains(field, render);
        }
        Assert.Contains("planPath", render);
        Assert.Contains("reportPath", render);

        var action = Slice(html, "async function runDispatcharrAction(", "async function runDispatcharrDryRun(");
        // O erro real é lido do body (não mascarado por r.ok).
        Assert.Contains("await r.json()", action);
        Assert.Contains("body.error", action);
    }

    [Fact]
    public void Dispatcharr_action_js_never_references_credentials()
    {
        var html = BuildDashboardHtml();
        var action = Slice(html, "let dispatcharrActionBusy", "async function loadDiagnostics(");

        Assert.DoesNotContain("apiKey", action);
        Assert.DoesNotContain("api_key", action);
        Assert.DoesNotContain("password", action, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", action);
    }

    // ─────────────────────────── Countries ───────────────────────────

    [Fact]
    public void Countries_tab_is_framed_as_configuration_validation()
    {
        var html = BuildDashboardHtml();

        var section = Slice(html, "<section id='view-countries'", "</section>");
        Assert.Contains("Configuração e validação por país", section);
        Assert.Contains("Configuração de canais por país", section);
        Assert.Contains("não é um CRUD de uma entidade de domínio", section);
        Assert.Contains("id='newCountryCode'", section);
        Assert.Contains("id='newCountryName'", section);
        Assert.Contains("onclick='createCountry()'", section);
    }

    [Fact]
    public void Countries_save_preserves_display_name_and_delete_supported()
    {
        var html = BuildDashboardHtml();
        var countries = Slice(html, "async function loadCountries(", "async function createCountry(");

        Assert.Contains("data-displayname", countries);
        Assert.Contains("btn.getAttribute('data-displayname')", countries);
        // Regressão: o save já não sobrescreve com code.toUpperCase().
        Assert.DoesNotContain("displayName: code.toUpperCase()", countries);

        Assert.Contains("data-delete-country", countries);
        Assert.Contains("method: 'DELETE'", countries);
        Assert.Contains("/api/country?country=", countries);
    }

    [Fact]
    public void Create_country_posts_minimal_payload_and_is_exported()
    {
        var html = BuildDashboardHtml();
        var create = Slice(html, "async function createCountry(", "async function loadCountryValidation(");

        Assert.Contains("/api/country/save", create);
        Assert.Contains("channels: []", create);
        Assert.Contains("window.createCountry = createCountry;", html);
    }
}
