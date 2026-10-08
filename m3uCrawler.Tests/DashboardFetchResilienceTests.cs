using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// DC-2 — Caracterização estrutural da resiliência do front-end do Dashboard.
///
/// O script principal é servido como uma única <c>string</c> C#; sem um harness
/// de execução JS (DC-8) o contrato observável é a forma do próprio script.
/// Este teste garante que:
/// <list type="bullet">
/// <item>existe UM helper canónico <c>apiRequest</c> (try/catch + leitura segura);</item>
/// <item>os handlers de mutação migrados usam esse helper e não <c>await fetch(</c> cru;</item>
/// <item>nenhum ramo de erro volta a fazer <c>await r.json()</c>;</item>
/// <item>os únicos <c>await fetch(</c> remanescentes são leituras toleradas.</item>
/// </list>
/// </summary>
public class DashboardFetchResilienceTests
{
    private static string BuildDashboardHtml()
    {
        var method = typeof(WebDashboardService).GetMethod(
            "BuildDashboardHtml",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, null)!;
    }

    private static readonly Regex FunctionDef = new(
        @"(?:async\s+)?function\s+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)\s*\(");

    /// <summary>
    /// Extrai o corpo de uma função pelo balanceamento de chaves. Os corpos do
    /// Dashboard contêm template literals (<c>${...}</c>) e objectos literais,
    /// ambos com chaves balanceadas, pelo que o fecho coincide com o <c>}</c> final.
    /// </summary>
    private static string? ExtractFunctionBody(string html, string name)
    {
        var marker = "function " + name + "(";
        var asyncMarker = "async function " + name + "(";
        var idx = html.IndexOf(asyncMarker, StringComparison.Ordinal);
        if (idx < 0)
        {
            idx = html.IndexOf(marker, StringComparison.Ordinal);
        }
        if (idx < 0)
        {
            return null;
        }

        var brace = html.IndexOf('{', idx);
        if (brace < 0)
        {
            return null;
        }

        var depth = 0;
        for (var i = brace; i < html.Length; i++)
        {
            var ch = html[i];
            if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return html.Substring(idx, i - idx + 1);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Handlers de mutação migrados para <c>apiRequest</c> em DC-2. A lista é
    /// deliberadamente explícita: se um handler migrado voltar ao <c>fetch</c>
    /// cru (ou for removido/renomeado sem actualizar o teste), o teste falha.
    /// </summary>
    public static TheoryData<string> MigratedMutationHandlers()
    {
        var names = new[]
        {
            "saveDiscoverySettings",
            "runDispatcharrAction",
            "loadCountries",
            "saveValidationPolicy",
            "runValidationTest",
            "addAliasFromDetail",
            "removeAliasFromDetail",
            "toggleChannelEnabled",
            "confirmChannelPolicy",
            "deleteChannel",
            "submitCreateChannel",
            "submitCreateSource",
            "deleteSource",
            "toggleChannelSource",
            "deleteChannelSource",
            "saveOrderingListEdit",
            "submitCreateOrderingList",
            "confirmDuplicateOrderingList",
            "deleteOrderingList",
            "addOrderingItem",
            "moveOrderingItem",
            "toggleOrderingItem",
            "removeOrderingItem",
            "saveGlobalPriority",
            "saveChannelPriority",
            "saveSourceSelectionPolicy",
            "saveChannelSourceSelectionPolicy",
            "deleteChannelSourceSelectionPolicy",
            "submitImportPolicyEdit",
            "submitCanonicalGroupEdit",
            "deleteCanonicalGroup",
            "submitCreateScheduledJob",
            "toggleScheduledJob",
            "deleteScheduledJob",
            "approvePendingCountryApproval",
            "rejectPendingCountryApproval",
            "submitAddRule",
            "deleteRule",
            "saveAffinityDelimiter",
            "submitAddAffinityGroup",
            "deleteAffinityGroup",
            "submitReviewAliasApproval",
            "submitReviewExclude",
            "submitReviewReopen",
            "startLiveRun",
            "setupFetch",
        };

        var data = new TheoryData<string>();
        foreach (var n in names)
        {
            data.Add(n);
        }
        return data;
    }

    [Fact]
    public void Canonical_helper_is_defined_exactly_once_and_is_resilient()
    {
        var html = BuildDashboardHtml();

        var definitions = FunctionDef.Matches(html)
            .Select(m => m.Groups["name"].Value)
            .Count(n => string.Equals(n, "apiRequest", StringComparison.Ordinal));
        Assert.Equal(1, definitions);

        var body = ExtractFunctionBody(html, "apiRequest");
        Assert.NotNull(body);
        // Envolve fetch em try/catch (nunca propaga rejeições).
        Assert.Contains("try", body);
        Assert.Contains("catch", body);
        Assert.Contains("falha de rede", body);
        // Serializa o corpo e define Content-Type quando recebe um objecto.
        Assert.Contains("JSON.stringify", body);
        Assert.Contains("Content-Type", body);
        // Lê o corpo de forma segura (text + parse tolerante), nunca r.json().
        Assert.Contains("r.text()", body);
        Assert.Contains("JSON.parse", body);
        Assert.DoesNotContain("r.json()", body);
        // Resultado normalizado com ok/status/json/error.
        Assert.Contains("ok:", body);
        Assert.Contains("status:", body);
        Assert.Contains("json:", body);
        Assert.Contains("error:", body);
    }

    [Fact]
    public void ApiRequest_serializes_body_inside_try_to_honor_never_throws_contract()
    {
        var html = BuildDashboardHtml();
        var body = ExtractFunctionBody(html, "apiRequest");
        Assert.NotNull(body);

        // A serialização pode lançar (ex.: referências circulares); tem de
        // ocorrer dentro do try para que o helper nunca propague a excepção.
        var tryIdx = body!.IndexOf("try {", StringComparison.Ordinal);
        var stringifyIdx = body.IndexOf("JSON.stringify(body)", StringComparison.Ordinal);

        Assert.True(tryIdx >= 0, "apiRequest deve envolver a serialização num try.");
        Assert.True(stringifyIdx > tryIdx,
            "JSON.stringify(body) deve estar dentro do try (contrato 'nunca lança').");
    }

    [Fact]
    public void Error_surface_helpers_are_present_and_shared()
    {
        var html = BuildDashboardHtml();

        Assert.Equal(1, FunctionDef.Matches(html).Select(m => m.Groups["name"].Value)
            .Count(n => n == "errorMessageFromBody"));
        Assert.Equal(1, FunctionDef.Matches(html).Select(m => m.Groups["name"].Value)
            .Count(n => n == "setStatus"));

        // readErrorBody reutiliza a extracção partilhada (sem duplicação).
        var readBody = ExtractFunctionBody(html, "readErrorBody");
        Assert.NotNull(readBody);
        Assert.Contains("errorMessageFromBody", readBody);

        // setSchedFormStatus passa a delegar na superfície uniforme.
        var schedStatus = ExtractFunctionBody(html, "setSchedFormStatus");
        Assert.NotNull(schedStatus);
        Assert.Contains("setStatus(", schedStatus);

        // O adaptador de Setup não mantém transporte próprio.
        var setupFetch = ExtractFunctionBody(html, "setupFetch");
        Assert.NotNull(setupFetch);
        Assert.Contains("apiRequest(", setupFetch);
        Assert.DoesNotContain("await fetch(", setupFetch);
    }

    [Theory]
    [MemberData(nameof(MigratedMutationHandlers))]
    public void Migrated_handler_uses_apiRequest_and_no_raw_fetch(string name)
    {
        var html = BuildDashboardHtml();
        var body = ExtractFunctionBody(html, name);
        Assert.True(body != null, $"Função '{name}' não encontrada no Dashboard.");
        Assert.Contains("apiRequest(", body);
        Assert.DoesNotContain("await fetch(", body);
        Assert.DoesNotContain("await r.json()", body);
        Assert.DoesNotContain("await rr.json()", body);
    }

    [Fact]
    public void Remaining_raw_fetch_calls_are_only_tolerated_reads()
    {
        var html = BuildDashboardHtml();

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "apiRequest",     // o próprio helper
            "safeFetchJson",  // fetch de leitura tolerante
            "loadPlaylist",   // GET /api/playlist/preview (leitura)
            "loadLiveRun",    // GET /api/run/status (polling de leitura)
        };

        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match def in FunctionDef.Matches(html))
        {
            var name = def.Groups["name"].Value;
            var body = ExtractFunctionBody(html, name);
            if (body != null && body.Contains("await fetch(", StringComparison.Ordinal) && !allowed.Contains(name))
            {
                offenders.Add(name);
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Handlers com `await fetch(` cru fora da lista de leituras toleradas: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void Silent_empty_catches_in_playlist_and_diagnostics_are_gone()
    {
        var html = BuildDashboardHtml();

        var playlist = ExtractFunctionBody(html, "loadPlaylist");
        Assert.NotNull(playlist);
        Assert.DoesNotContain("catch (e) {}", playlist);

        var diagnostics = ExtractFunctionBody(html, "loadDiagnostics");
        Assert.NotNull(diagnostics);
        Assert.DoesNotContain("catch(e) {}", diagnostics);
    }
}
