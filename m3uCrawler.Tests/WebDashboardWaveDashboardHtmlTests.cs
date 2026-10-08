using System;
using System.Reflection;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE W-DASHBOARD (PARTE UI) — marcadores HTML/JS adicionados a
/// <c>BuildDashboardHtml()</c>: formulário de configuração de discovery,
/// contexto das activities no Live Run, proveniência na tabela de
/// descoberta e visibilidade de W-DEDUP em Overview/Execuções.
///
/// Não testa o comportamento dos endpoints (já cobertos noutro sítio);
/// verifica apenas que a página servida expõe os marcadores e que o JS
/// referencia os campos do wire. Testes puramente de string, sem tocar
/// estado estático — não requerem a colecção <c>DashboardStaticState</c>.
/// </summary>
public class WebDashboardWaveDashboardHtmlTests
{
    [Fact]
    public void Discovery_view_exposes_settings_form_markers()
    {
        var html = InvokeBuildDashboardHtml();

        // IDs únicos do formulário (o audit test cobre a unicidade).
        Assert.Contains("id='discoveryKeyword'", html);
        Assert.Contains("id='discoveryMinHistoryHours'", html);
        Assert.Contains("id='discoveryMaxHistoryHours'", html);
        Assert.Contains("id='discoveryMaxStreams'", html);
        Assert.Contains("id='discoverySettingsSaveBtn'", html);
        Assert.Contains("id='discoverySettingsStatus'", html);
        Assert.Contains("id='discoveryWindowExplainer'", html);

        // GET (leitura) e POST (mutação) do mesmo endpoint.
        Assert.Contains("safeFetchJson('/api/discovery/settings'", html);
        Assert.Contains("apiRequest('/api/discovery/settings'", html);
        Assert.Contains("method: 'POST'", html);

        // Explainer da janela inclusiva (substring estável).
        Assert.Contains("≤ idade da mensagem ≤", html);

        // Limites HTML honestos (hints), sem duplicar a validação backend.
        Assert.Contains("id='discoveryMinHistoryHours' type='number' min='0'", html);
        Assert.Contains("id='discoveryMaxHistoryHours' type='number' min='1' max='1440'", html);
        Assert.Contains("id='discoveryMaxStreams' type='number' min='1'", html);

        // Campos persistidos referidos na ajuda e recarregados após o save.
        Assert.Contains("app_settings.json", html);
        Assert.Contains("loadDiscoverySettings", html);
        Assert.Contains("saveDiscoverySettings", html);
    }

    [Fact]
    public void Live_run_activities_reference_category_and_metadata()
    {
        var html = InvokeBuildDashboardHtml();

        Assert.Contains("a.category", html);
        Assert.Contains("liveRunCategoryBadge", html);
        Assert.Contains("a.metadata", html);

        // Rótulos humanos dos contadores (LiveRunCounts), incluindo W-DEDUP.
        Assert.Contains("Streams testados (físico)", html);
        Assert.Contains("Streams reutilizados (W-DEDUP)", html);
    }

    [Fact]
    public void Discovery_table_exposes_message_provenance_columns()
    {
        var html = InvokeBuildDashboardHtml();

        Assert.Contains("<th>MessageId</th>", html);
        Assert.Contains("<th>Data mensagem</th>", html);
        Assert.Contains("<th>Candidato</th>", html);
        Assert.Contains("p.candidateId", html);
        Assert.Contains("p.messageId", html);
        Assert.Contains("p.messageDateUtc", html);
        Assert.Contains("fmtUtcDateTime", html);
    }

    [Fact]
    public void Overview_surfaces_w_dedup_skipped_counter()
    {
        var html = InvokeBuildDashboardHtml();

        Assert.Contains("streamsSkippedAlreadyValidated", html);
        Assert.Contains("reutilizados (W-DEDUP)", html);
    }

    [Fact]
    public void Executions_table_exposes_w_dedup_column()
    {
        var html = InvokeBuildDashboardHtml();

        Assert.Contains("<th>Reutilizados (W-DEDUP)</th>", html);
    }

    private static string InvokeBuildDashboardHtml()
    {
        var method = typeof(WebDashboardService).GetMethod(
            "BuildDashboardHtml",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, null)!;
    }
}
