using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 12 — Caracterização do bloco HTML/JS do separador "Scheduled Jobs"
/// no Dashboard. Verifica a estrutura do formulário (um único controlo por
/// campo), a ajuda cron, a validação live e o painel de actions.
/// </summary>
[Collection("DashboardStaticState")]
public class ScheduledJobsUiHtmlTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _outputDir;

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;
    private PlaylistComposerService _composer = null!;
    private ImportHistoryService _history = null!;
    private DashboardBootstrapEndpointTests.DashboardHarness? _harness;

    public ScheduledJobsUiHtmlTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sched-ui-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _outputDir = Path.Combine(_root, "output");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_outputDir);
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();

        _resolver = new CatalogResolver(_factory, _dbPath);
        _composer = new PlaylistComposerService(_factory);
        _history = new ImportHistoryService(_outputDir);
    }

    public async Task DisposeAsync()
    {
        if (_harness != null)
        {
            await _harness.DisposeAsync();
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private async Task<string> GetDashboardHtmlAsync()
    {
        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history,
            lifecycle: null, auth: null, bootstrap: null, webToken: null,
            standalone: true);

        var response = await _harness.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static string ExtractScheduledBlock(string html)
    {
        var anchor = html.IndexOf("<div id='ctab-scheduled'", StringComparison.Ordinal);
        Assert.True(anchor >= 0, "Bloco ctab-scheduled não encontrado.");
        var nextTab = html.IndexOf("<!-- TAB:", anchor, StringComparison.Ordinal);
        Assert.True(nextTab > anchor, "Marcador TAB seguinte não encontrado.");
        return html.Substring(anchor, nextTab - anchor);
    }

    [Fact]
    public async Task Scheduled_block_is_div_balanced_with_single_controls()
    {
        var html = await GetDashboardHtmlAsync();
        var block = ExtractScheduledBlock(html);

        var opens = CountOccurrences(block, "<div");
        var closes = CountOccurrences(block, "</div>");
        Assert.Equal(opens, closes);

        Assert.Equal(1, CountOccurrences(block, "data-sched-create='name'"));
        Assert.Equal(1, CountOccurrences(block, "data-sched-create='cron'"));
        Assert.Equal(1, CountOccurrences(block, "data-sched-create='action'"));
        Assert.Equal(1, CountOccurrences(block, "data-sched-create='enabled'"));

        Assert.DoesNotContain("discoverTelegram", html);
        Assert.DoesNotContain("action-select", block);
    }

    [Fact]
    public async Task Scheduled_block_carries_cron_help_and_live_status_markers()
    {
        var html = await GetDashboardHtmlAsync();

        Assert.Contains("dia da semana", html);
        Assert.Contains("* * * * *", html);
        Assert.Contains("6 campos", html);
        Assert.Contains("segundos", html);
        Assert.Contains("0 */6 * * *", html);
        Assert.Contains("30 2 * * 1", html);

        Assert.Contains("id='schedCronStatus'", html);
        Assert.Contains("id='schedActionsHelp'", html);

        Assert.Contains("loadScheduledActions", html);
        Assert.Contains("applySchedFrequency", html);
        Assert.Contains("window.__schedActions", html);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
