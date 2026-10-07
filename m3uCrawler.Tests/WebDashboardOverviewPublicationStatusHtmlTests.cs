using System.Reflection;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

// DL-130 Slice 2 — Dashboard Publication Status.
//
// These tests validate the JS embedded inside BuildDashboardHtml() —
// specifically the loadOverview() function — by string-asserting the literals
// that the 4 states of the new "Publicação do catálogo" card must contain.
//
// Style mirrors WebDashboardHtmlAuditTests.cs: xUnit + reflection on the
// private static BuildDashboardHtml() + Assert.Contains on rendered HTML.
// These tests do not mutate static state of WebDashboardService, so they do
// not require the [Collection("DashboardStaticState")] attribute.
public class WebDashboardOverviewPublicationStatusHtmlTests
{
    [Fact]
    public void Overview_includes_publication_status_card_and_endpoint()
    {
        var html = InvokeBuildHtml();

        // The endpoint must be called from loadOverview via safeFetchJson.
        // The literal in the embedded JS uses single quotes around the URL.
        Assert.Contains("'/api/publication/status'", html);

        // The card title literal must be present.
        Assert.Contains("'Publicação do catálogo'", html);

        // loadOverview must reference the 5 endpoints in Promise.all. The
        // signature change from 4 to 5 is the structural fingerprint of the
        // Slice 2 wiring.
        Assert.Contains("loadOverview(", html);
        Assert.Contains("/api/run-report/summary", html);
        Assert.Contains("/api/history", html);
        Assert.Contains("/api/dispatcharr/state", html);
        Assert.Contains("/api/output/inventory", html);
        Assert.Contains("/api/publication/status", html);

        // The destructuring assignment must include the new 'pub' binding.
        Assert.Contains("[run, hist, dispatcharr, inv, pub]", html);
    }

    [Fact]
    public void Overview_renders_publication_pending_with_warn_badge()
    {
        // State A: publicationPending == true AND lastSuccessfulPublicationAtUtc != null
        // → "Pendente" with .badge.warn
        var html = InvokeBuildHtml();
        // The badge class must be 'warn' (not 'err'). Pending is an operational
        // expectation, not a failure — brief §3.A.
        Assert.Contains("badge warn'>Pendente</span>", html);
        // The literal "Pendente" must be present.
        Assert.Contains("Pendente", html);
    }

    [Fact]
    public void Overview_renders_publication_ok_with_ok_badge()
    {
        // State B: publicationPending == false
        // → "Em dia" with .badge.ok
        var html = InvokeBuildHtml();
        Assert.Contains("badge ok'>Em dia</span>", html);
        Assert.Contains("Em dia", html);
    }

    [Fact]
    public void Overview_renders_sem_publicacao_anterior_when_no_prior_publication()
    {
        // State C: lastSuccessfulPublicationAtUtc == null
        // → "Sem publicação anterior" with .badge.warn
        var html = InvokeBuildHtml();
        Assert.Contains("badge warn'>Sem publicação anterior</span>", html);
        Assert.Contains("Sem publicação anterior", html);
    }

    [Fact]
    public void Overview_renders_indisponivel_when_endpoint_errors()
    {
        // State D: /api/publication/status errors (e.g. 503) or returns null
        // → "Indisponível" with .badge.muted
        var html = InvokeBuildHtml();
        Assert.Contains("badge muted'>Indisponível</span>", html);
        Assert.Contains("Indisponível", html);

        // Error-path must surface the safeFetchJson-derived error message.
        Assert.Contains("pub.error", html);
    }

    [Fact]
    public void Overview_does_not_recalculate_publication_pending_in_frontend()
    {
        // Brief §7: "Não criar qualquer lógica do género
        //   catalogChangedAtUtc > lastSuccessfulPublicationAtUtc
        // no JavaScript."
        // The frontend MUST consume the pre-computed boolean (pub.publicationPending)
        // and must NOT recompute it from the two cursors.
        var html = InvokeBuildHtml();

        // The frontend reads the boolean — that part is required.
        Assert.Contains("pub.publicationPending", html);

        // The frontend must NOT directly compare the two cursor timestamps to
        // derive publicationPending. Either of these patterns would violate
        // the invariant. We assert their ABSENCE.
        Assert.DoesNotContain("catalogChangedAtUtc >", html);
        Assert.DoesNotContain("> lastSuccessfulPublicationAtUtc", html);
        Assert.DoesNotContain("lastSuccessfulPublicationAtUtc <", html);
        Assert.DoesNotContain("< catalogChangedAtUtc", html);
    }

    [Fact]
    public void Overview_does_not_add_publication_polling()
    {
        // Brief §5: "Não adicionar polling. O estado deve actualizar:
        //   - quando loadOverview() é executado;
        //   - através do mecanismo existente de Recarregar."
        // The only setInterval in the dashboard today is the Live Run polling
        // (loadLiveRun / startLiveRunPolling). Slice 2 must NOT add another one
        // for publication status.
        var html = InvokeBuildHtml();

        // The single setInterval in the file is the Live Run poll. Slice 2
        // must not add a second one.
        var intervalCount = System.Text.RegularExpressions.Regex.Matches(
            html, @"\bsetInterval\s*\(").Count;
        Assert.Equal(1, intervalCount);

        // The existing setInterval must be the Live Run poll, not anything
        // related to publication status. Slice 2 must not introduce polling
        // for /api/publication/status.
        // The setInterval call is on the RHS of `liveRunTimer = setInterval(...)`,
        // so we look for that assignment to identify the Live Run poll.
        Assert.Contains("liveRunTimer = setInterval", html);

        // Find the setInterval call and inspect a wide context window. We need
        // to capture enough characters to cover the entire callback body, which
        // sits AFTER setInterval(. The variable declaration `var liveRunTimer`
        // appears BEFORE the setInterval call. So we verify both: (a) the only
        // setInterval is the Live Run one (liveRunTimer assignment present in
        // the HTML), and (b) no setInterval references publication status.
        var setIntervalIdx = html.IndexOf("setInterval(", System.StringComparison.Ordinal);
        Assert.True(setIntervalIdx > 0, "Expected a setInterval in the rendered HTML.");

        // Take a wide window (8 KB) after setInterval( to span the entire Live Run
        // polling function body.
        var windowSize = System.Math.Min(8192, html.Length - setIntervalIdx);
        var context = html.Substring(setIntervalIdx, windowSize);

        // Inside the Live Run poll callback body there must be NO reference to
        // publication status (no `/api/publication/status`, no `pub.` access).
        Assert.DoesNotContain("/api/publication/status", context);
        Assert.DoesNotContain("pub.", context);
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
