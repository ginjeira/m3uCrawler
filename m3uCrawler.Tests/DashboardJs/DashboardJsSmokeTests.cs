using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using Xunit;

namespace m3uCrawler.Tests.DashboardJs;

/// <summary>
/// DC-8 (enabler) — smoke de comportamento do JS embutido no Dashboard.
/// Extrai o IIFE do HTML servido em <c>GET /</c> e corre-o num motor Jint com
/// um shim DOM mínimo (<see cref="DashboardJsHarness"/>), cobrindo os alvos de
/// §3.6: G2 (<c>startLiveRun</c> + modo DC-1), G3 (helper <c>apiRequest</c> DC-2),
/// G4 (helpers de cron e montagem de frequência) e G6 (modal open/close).
///
/// Determinístico, 100% in-process, sem browser/servidor externo.
/// </summary>
[Collection("DashboardStaticState")]
public class DashboardJsSmokeTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _outputDir;

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;
    private PlaylistComposerService _composer = null!;
    private ImportHistoryService _history = null!;
    private DashboardBootstrapEndpointTests.DashboardHarness? _harness;

    private string _script = string.Empty;

    public DashboardJsSmokeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"dash-js-{Guid.NewGuid():N}");
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

        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history,
            lifecycle: null, auth: null, bootstrap: null, webToken: null,
            standalone: true);

        var response = await _harness.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        _script = DashboardJsScript.ExtractFrom(html);
    }

    public async Task DisposeAsync()
    {
        if (_harness != null)
        {
            await _harness.DisposeAsync();
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private DashboardJsEngine NewEngine() => DashboardJsEngine.FromScript(_script);

    // ---------------- G1 — enabler ----------------

    [Fact]
    public void Harness_evaluates_iife_and_exposes_all_probes()
    {
        Assert.False(string.IsNullOrWhiteSpace(_script));

        var engine = NewEngine();

        Assert.True(engine.HasProbe("apiRequest"));
        Assert.True(engine.HasProbe("startLiveRun"));
        Assert.True(engine.HasProbe("validateSchedCron"));
        Assert.True(engine.HasProbe("describeSchedCron"));
        Assert.True(engine.HasProbe("applySchedFrequency"));
        Assert.True(engine.HasProbe("openModalPanel"));
        Assert.True(engine.HasProbe("closeModalPanel"));
    }

    // ---------------- G2 — startLiveRun / DC-1 ----------------

    [Fact]
    public void StartLiveRun_sends_mode_and_only_populated_overrides()
    {
        var engine = NewEngine();
        engine.SetFetch("reject", message: "sem rede");

        // 1) Sem controlos preenchidos: só o modo, com default 'telegram'.
        engine.Eval("window.__dc8.startLiveRun()");
        var body = engine.LastRequestBodyFor("/api/run/start");
        Assert.NotNull(body);
        using (var doc = JsonDocument.Parse(body!))
        {
            Assert.Single(doc.RootElement.EnumerateObject());
            Assert.Equal("telegram", doc.RootElement.GetProperty("mode").GetString());
        }

        // 2) Overrides preenchidos: entram no corpo; keyword sofre trim;
        //    maxStreams em branco continua ausente.
        engine.SetElementValue("liveRunMode", "telegram-maintain");
        engine.SetElementValue("liveRunKeyword", "  notícias  ");
        engine.SetElementValue("liveRunHistoryHours", "72");
        engine.SetElementValue("liveRunMaxStreams", "");
        engine.Eval("window.__dc8.startLiveRun()");

        body = engine.LastRequestBodyFor("/api/run/start");
        Assert.NotNull(body);
        using (var doc = JsonDocument.Parse(body!))
        {
            var root = doc.RootElement;
            Assert.Equal("telegram-maintain", root.GetProperty("mode").GetString());
            Assert.Equal("notícias", root.GetProperty("keyword").GetString());
            Assert.Equal(72, root.GetProperty("historyHours").GetInt32());
            Assert.False(root.TryGetProperty("maxStreams", out _));
        }

        // 3) keyword só com espaços não conta como preenchida.
        engine.SetElementValue("liveRunKeyword", "   ");
        engine.SetElementValue("liveRunHistoryHours", "");
        engine.Eval("window.__dc8.startLiveRun()");

        body = engine.LastRequestBodyFor("/api/run/start");
        Assert.NotNull(body);
        using (var doc = JsonDocument.Parse(body!))
        {
            var root = doc.RootElement;
            Assert.False(root.TryGetProperty("keyword", out _));
            Assert.False(root.TryGetProperty("historyHours", out _));
        }
    }

    // ---------------- G3 — apiRequest / DC-2 ----------------

    [Fact]
    public void ApiRequest_normalizes_network_rejection_without_throwing()
    {
        var engine = NewEngine();
        engine.SetFetch("reject", message: "rede em baixo");

        // Se apiRequest propagasse a rejeição, UnwrapIfPromise lançaria aqui.
        var json = engine.EvalString(
            "window.__test.jsonAsync(window.__dc8.apiRequest('/api/probe'))");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal(0, root.GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("json").ValueKind);
        Assert.Equal("rede em baixo", root.GetProperty("error").GetString());
    }

    [Fact]
    public void ApiRequest_reads_error_message_from_json_body_and_ok_on_success()
    {
        var engine = NewEngine();

        engine.SetFetch("ok", status: 400, body: "{\"error\":\"boom\"}");
        var failure = engine.EvalString(
            "window.__test.jsonAsync(window.__dc8.apiRequest('/api/probe'))");
        using (var doc = JsonDocument.Parse(failure))
        {
            var root = doc.RootElement;
            Assert.False(root.GetProperty("ok").GetBoolean());
            Assert.Equal(400, root.GetProperty("status").GetInt32());
            Assert.Equal("boom", root.GetProperty("error").GetString());
        }

        engine.SetFetch("ok", status: 200, body: "{\"value\":7}");
        var success = engine.EvalString(
            "window.__test.jsonAsync(window.__dc8.apiRequest('/api/probe'))");
        using (var doc = JsonDocument.Parse(success))
        {
            var root = doc.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.Equal(200, root.GetProperty("status").GetInt32());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
            Assert.Equal(7, root.GetProperty("json").GetProperty("value").GetInt32());
        }
    }

    // ---------------- G4 — scheduled jobs ----------------

    [Fact]
    public void Scheduled_cron_helpers_validate_and_describe()
    {
        var engine = NewEngine();

        var daily = engine.EvalString("window.__test.json(window.__dc8.validateSchedCron('0 8 * * *'))");
        using (var doc = JsonDocument.Parse(daily))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("todos os dias às 08:00", doc.RootElement.GetProperty("message").GetString());
        }

        var invalid = engine.EvalString("window.__test.json(window.__dc8.validateSchedCron('nope'))");
        using (var doc = JsonDocument.Parse(invalid))
        {
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("message").GetString()));
        }

        Assert.Equal(
            "todos os dias às 08:00",
            engine.EvalString("window.__dc8.describeSchedCron(['0','8','*','*','*'])"));
        Assert.Equal(
            "de 6 em 6 horas",
            engine.EvalString("window.__dc8.describeSchedCron(['0','*/6','*','*','*'])"));
    }

    [Fact]
    public void ApplySchedFrequency_builds_cron_for_each_kind()
    {
        var engine = NewEngine();
        engine.SeedAttribute("data-sched-create", "cron");

        engine.SetElementValue("schedFreqKind", "daily");
        engine.SetElementValue("schedFreqTime", "08:30");
        engine.Exec("window.__dc8.applySchedFrequency();");
        Assert.Equal("30 8 * * *", engine.QueryValue("[data-sched-create=cron]"));

        engine.SetElementValue("schedFreqKind", "hours");
        engine.SetElementValue("schedFreqHours", "4");
        engine.Exec("window.__dc8.applySchedFrequency();");
        Assert.Equal("30 */4 * * *", engine.QueryValue("[data-sched-create=cron]"));

        engine.SetElementValue("schedFreqKind", "weekly");
        engine.SetElementValue("schedFreqWeekday", "3");
        engine.Exec("window.__dc8.applySchedFrequency();");
        Assert.Equal("30 8 * * 3", engine.QueryValue("[data-sched-create=cron]"));
    }

    // ---------------- DC-9 — preview do discovery ----------------

    [Fact]
    public void Sched_discovery_preview_normalizes_out_of_range_values_like_backend()
    {
        var engine = NewEngine();
        engine.SeedAttribute("data-sched-create", "discoveryHistoryHours");
        engine.SeedAttribute("data-sched-create", "discoveryMaxStreams");

        // Fora do intervalo (history > 1440) → herdado da global (24h).
        engine.Exec("document.querySelector(\"[data-sched-create=discoveryHistoryHours]\").value='5000';");
        engine.Exec("window.__dc8.updateSchedDiscoveryPreview();");
        Assert.Equal(
            "Efetivo: keyword=\"portugal\", min=0h, history=24h, maxStreams=500",
            engine.EvalString("window.__test.el('schedDiscoveryPreview').textContent"));

        // Valor válido → usado directamente.
        engine.Exec("document.querySelector(\"[data-sched-create=discoveryHistoryHours]\").value='100';");
        engine.Exec("window.__dc8.updateSchedDiscoveryPreview();");
        Assert.Contains(
            "history=100h",
            engine.EvalString("window.__test.el('schedDiscoveryPreview').textContent"));

        // maxStreams < 1 → herdado da global (500).
        engine.Exec("document.querySelector(\"[data-sched-create=discoveryMaxStreams]\").value='0';");
        engine.Exec("window.__dc8.updateSchedDiscoveryPreview();");
        Assert.Contains(
            "maxStreams=500",
            engine.EvalString("window.__test.el('schedDiscoveryPreview').textContent"));

        Assert.Equal(
            "null",
            engine.EvalString("JSON.stringify(window.__dc8.schedDiscValidInt('discoveryMaxStreams', 1, null))"));
    }

    // ---------------- G6 — modal ----------------
    [Fact]
    public void Open_and_close_modal_smoke()
    {
        var engine = NewEngine();
        engine.Exec("window.__test.el('orderingCreateForm').parentNode = window.__test.body();");

        engine.Exec("window.__dc8.openModalPanel('orderingCreateForm');");

        Assert.False(engine.EvalBool("window.__test.el('orderingCreateForm').hidden"));
        Assert.True(engine.EvalBool("window.__test.el('orderingCreateForm').classList.contains('modal-panel')"));
        Assert.True(engine.EvalBool("window.__test.el('orderingCreateForm').parentNode === window.__test.el('modalRoot')"));
        Assert.True(engine.EvalBool("window.__test.el('modalRoot').classList.contains('open')"));
        Assert.False(engine.EvalBool("window.__test.el('modalRoot').hidden"));
        Assert.True(engine.EvalBool("window.__test.body().classList.contains('modal-open')"));
        Assert.True(engine.EvalBool("!!window.__test.el('orderingCreateForm').querySelector('.modal-close')"));

        engine.Exec("window.__dc8.closeModalPanel();");

        Assert.True(engine.EvalBool("window.__test.el('orderingCreateForm').hidden"));
        Assert.False(engine.EvalBool("window.__test.el('orderingCreateForm').classList.contains('modal-panel')"));
        Assert.True(engine.EvalBool("window.__test.el('orderingCreateForm').parentNode === window.__test.body()"));
        Assert.False(engine.EvalBool("window.__test.el('modalRoot').classList.contains('open')"));
        Assert.True(engine.EvalBool("window.__test.el('modalRoot').hidden"));
        Assert.False(engine.EvalBool("window.__test.body().classList.contains('modal-open')"));
    }

    // ---------------- DC-7 — restauro de foco no fecho do modal ----------------

    [Fact]
    public void Close_modal_restores_focus_to_previously_focused_element()
    {
        var engine = NewEngine();
        engine.Exec("window.__test.el('orderingCreateForm').parentNode = window.__test.body();");
        engine.Exec("window.__test.el('channelPolicyAnchor').focus();");
        Assert.True(engine.EvalBool("document.activeElement === window.__test.el('channelPolicyAnchor')"));

        engine.Exec("window.__dc8.openModalPanel('orderingCreateForm');");
        engine.Exec("window.__dc8.closeModalPanel();");

        Assert.True(engine.EvalBool("document.activeElement === window.__test.el('channelPolicyAnchor')"));
    }

    // ---------------- DC-12 — mover para posição N ----------------

    [Fact]
    public void MoveOrderingItemToPosition_puts_zero_based_and_rejects_invalid_input()
    {
        var engine = NewEngine();
        engine.SetFetch("ok", status: 200, body: "{}");
        engine.Exec("var mv = window.__test.el('orderingMovePos-42'); mv.value='3'; mv.max='10';");

        // 1-based no input (3) => 0-based no PUT (2).
        engine.Eval("window.__dc8.moveOrderingItemToPosition(42)");
        var body = engine.LastRequestBodyFor("/api/catalog/ordering-items/42");
        Assert.NotNull(body);
        using (var doc = JsonDocument.Parse(body!))
        {
            Assert.Equal(2, doc.RootElement.GetProperty("position").GetInt32());
        }

        // Fora do intervalo (max=10): avisa e não faz novo PUT.
        engine.Exec("window.__test.el('orderingMovePos-42').value = '99';");
        engine.Eval("window.__dc8.moveOrderingItemToPosition(42)");
        Assert.Equal("1", engine.EvalString("String(window.__test.fetchCountFor('/api/catalog/ordering-items/42'))"));

        // Não numérico: também rejeitado sem novo PUT.
        engine.Exec("window.__test.el('orderingMovePos-42').value = 'abc';");
        engine.Eval("window.__dc8.moveOrderingItemToPosition(42)");
        Assert.Equal("1", engine.EvalString("String(window.__test.fetchCountFor('/api/catalog/ordering-items/42'))"));
    }
}
