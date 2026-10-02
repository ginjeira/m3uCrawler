using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Automation;
using m3uCrawler.Services.Catalog;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 12 — Integração HTTP dos endpoints de Scheduled Jobs. Prova a
/// propagação correcta de status (o bug corrigido: <c>WriteJsonAsync</c>
/// repunha 200), a validação de <c>actionName</c> contra o registry de
/// <see cref="IScheduledAction"/> e o novo shape de
/// <c>GET /api/scheduled-actions</c>.
/// </summary>
[Collection("DashboardStaticState")]
public class ScheduledJobsEndpointTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _outputDir;

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;
    private PlaylistComposerService _composer = null!;
    private ImportHistoryService _history = null!;
    private DashboardBootstrapEndpointTests.DashboardHarness? _harness;

    private sealed class FakeScheduledAction : IScheduledAction
    {
        public string Name => "discoverM3u";

        public string Description => "fake";

        public Task<string> ExecuteAsync(CancellationToken cancellationToken)
            => Task.FromResult("ok");
    }

    public ScheduledJobsEndpointTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"dash-sched-{Guid.NewGuid():N}");
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

        WebDashboardService.SetScheduledActions(new IScheduledAction[] { new FakeScheduledAction() });

        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history,
            lifecycle: null, auth: null, bootstrap: null, webToken: null,
            standalone: true);
    }

    public async Task DisposeAsync()
    {
        if (_harness != null)
        {
            await _harness.DisposeAsync();
        }
        WebDashboardService.SetScheduledActions(Array.Empty<IScheduledAction>());
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static StringContent Json(string body)
        => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Post_valid_job_is_persisted_and_returned_by_get()
    {
        var response = await _harness!.Client.PostAsync(
            "/api/catalog/scheduled-jobs",
            Json("{\"name\":\"job-ok\",\"cronExpression\":\"*/5 * * * *\",\"actionName\":\"discoverM3u\",\"isEnabled\":true}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var listed = await _harness.Client.GetAsync("/api/catalog/scheduled-jobs");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using var doc = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        Assert.True(
            doc.RootElement.EnumerateArray().Any(e => e.GetProperty("name").GetString() == "job-ok"),
            "GET deve devolver o job persistido 'job-ok'.");
    }

    [Fact]
    public async Task Post_invalid_cron_returns_400_and_is_not_persisted()
    {
        var response = await _harness!.Client.PostAsync(
            "/api/catalog/scheduled-jobs",
            Json("{\"name\":\"job-bad-cron\",\"cronExpression\":\"0 0 8 * * *\",\"actionName\":\"discoverM3u\"}"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()));
        }

        var listed = await _harness.Client.GetAsync("/api/catalog/scheduled-jobs");
        using var listDoc = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        Assert.False(
            listDoc.RootElement.EnumerateArray().Any(e => e.GetProperty("name").GetString() == "job-bad-cron"),
            "Job com cron inválido não deve ficar persistido.");
    }

    [Fact]
    public async Task Post_invalid_action_name_returns_400_and_is_not_persisted()
    {
        var response = await _harness!.Client.PostAsync(
            "/api/catalog/scheduled-jobs",
            Json("{\"name\":\"job-bad-action\",\"cronExpression\":\"*/5 * * * *\",\"actionName\":\"nope\"}"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Contains("ActionName inválido", doc.RootElement.GetProperty("error").GetString());
        }

        var listed = await _harness.Client.GetAsync("/api/catalog/scheduled-jobs");
        using var listDoc = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        Assert.False(
            listDoc.RootElement.EnumerateArray().Any(e => e.GetProperty("name").GetString() == "job-bad-action"),
            "Job com actionName inválido não deve ficar persistido.");
    }

    [Fact]
    public async Task Delete_missing_job_returns_404_with_json_body()
    {
        var response = await _harness!.Client.DeleteAsync("/api/catalog/scheduled-jobs/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Get_scheduled_actions_returns_metadata_objects()
    {
        var response = await _harness!.Client.GetAsync("/api/scheduled-actions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);

        var first = doc.RootElement.EnumerateArray().First();
        Assert.Equal("discoverM3u", first.GetProperty("name").GetString());
        Assert.Equal("fake", first.GetProperty("description").GetString());
        Assert.True(first.TryGetProperty("capabilities", out _));
        Assert.True(first.TryGetProperty("requiresTelegram", out _));
        Assert.True(first.TryGetProperty("requiresDispatcharr", out _));
    }
}
