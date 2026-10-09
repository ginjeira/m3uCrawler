using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Auth;
using m3uCrawler.Services.Automation;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using m3uCrawler.Services.LiveRun;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// DC-9 (decisão DC-D2) — Overrides de discovery por Scheduled Job.
///
/// <para>
/// Cobre: serialização estável de <see cref="DiscoveryOverrides"/>;
/// precedência <c>override do job &gt; global</c> (com herança quando
/// ausente e normalização <c>Min &gt; History → 0</c>); validação/clamping
/// de <c>minHistoryHours</c> (inclusive em <c>ParseStartPayload</c>); acção
/// job-aware; desserialização pelo <see cref="ScheduledJobRunner"/>
/// (JSON inválido ⇒ herda a global sem rebentar); CRUD POST/GET dos
/// campos de discovery; migração EF; e presença dos controlos no HTML.
/// </para>
/// </summary>
[Collection("DashboardStaticState")]
public class Dc9ScheduledJobDiscoveryTests : IAsyncLifetime
{
    private const string ValidPassword = "a-very-strong-password";
    private const string LatestMigration = "20261008183725_AddScheduledJobDiscovery";

    private readonly string _root;
    private readonly string _runtimeDataDir;
    private readonly string _outputDir;
    private readonly string _dbPath;
    private readonly string _storePath;

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;
    private PlaylistComposerService _composer = null!;
    private ImportHistoryService _history = null!;
    private DiscoverySettingsProvider _provider = null!;

    private ConfigurationLifecycleService _lifecycle = null!;
    private AuthService _auth = null!;
    private BootstrapService _bootstrap = null!;
    private DashboardBootstrapEndpointTests.DashboardHarness? _harness;

    public Dc9ScheduledJobDiscoveryTests()
    {
        _root = TestTempDb.SuitePath($"dc9-discovery-{Guid.NewGuid():N}");
        _runtimeDataDir = Path.Combine(_root, "runtime-data");
        _outputDir = Path.Combine(_root, "output");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _storePath = Path.Combine(_root, ConfigurationLifecycleStore.FileName);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_runtimeDataDir);
        Directory.CreateDirectory(_outputDir);
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();

        _resolver = new CatalogResolver(_factory, _dbPath);
        _composer = new PlaylistComposerService(_factory);
        _history = new ImportHistoryService(_outputDir);
        _provider = new DiscoverySettingsProvider(new AppSettingsStore(_runtimeDataDir));

        _lifecycle = new ConfigurationLifecycleService(
            new ConfigurationLifecycleStore(_storePath), _factory, _outputDir);
        var users = new AdminUserStore(_factory);
        _auth = new AuthService(users, new SessionStore(_factory));
        _bootstrap = new BootstrapService(
            _lifecycle, users,
            new BootstrapConfigurationValidator(_factory, _outputDir));
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
        WebDashboardService.SetAuth(null, null);
        WebDashboardService.SetConfigurationLifecycle(null);
        TestTempDb.CleanupDirectory(_root);
    }

    private void SeedGlobal(int historyHours, int minHistoryHours, int maxStreams, string keyword)
    {
        var store = new AppSettingsStore(_runtimeDataDir);
        var settings = store.Load();
        settings.Discovery = new DiscoverySettings
        {
            HistoryHours = historyHours,
            MinHistoryHours = minHistoryHours,
            MaxStreams = maxStreams,
            Keyword = keyword,
        };
        store.Save(settings);
    }

    // ==================== Serialização ====================

    [Fact]
    public void Overrides_serialize_camel_case_and_omit_nulls()
    {
        var json = new DiscoveryOverrides("custom", 6, 48, 123).ToJson();

        Assert.Contains("\"keyword\":\"custom\"", json);
        Assert.Contains("\"minHistoryHours\":6", json);
        Assert.Contains("\"historyHours\":48", json);
        Assert.Contains("\"maxStreams\":123", json);
        Assert.DoesNotContain("null", json);
    }

    [Fact]
    public void Overrides_parse_absent_fields_as_null_and_is_tolerant()
    {
        var parsed = DiscoveryOverrides.TryParse("{\"keyword\":\"x\"}");

        Assert.NotNull(parsed);
        Assert.Equal("x", parsed!.Keyword);
        Assert.Null(parsed.MinHistoryHours);
        Assert.Null(parsed.HistoryHours);
        Assert.Null(parsed.MaxStreams);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("{ not json")]
    public void Overrides_parse_invalid_or_empty_returns_null(string? json)
    {
        Assert.Null(DiscoveryOverrides.TryParse(json));
    }

    // ==================== Precedência ====================

    [Fact]
    public void Job_overrides_take_precedence_over_global()
    {
        SeedGlobal(24, 0, 500, "portugal");

        var effective = _provider.Resolve(new DiscoveryOverrides("jobterm", 6, 48, 123));

        Assert.Equal("jobterm", effective.Keyword);
        Assert.Equal(6, effective.MinHistoryHours);
        Assert.Equal(48, effective.HistoryHours);
        Assert.Equal(123, effective.MaxStreams);
    }

    [Fact]
    public void Absent_overrides_inherit_global()
    {
        SeedGlobal(72, 12, 900, "globalterm");

        var effective = _provider.Resolve((DiscoveryOverrides?)null);

        Assert.Equal("globalterm", effective.Keyword);
        Assert.Equal(12, effective.MinHistoryHours);
        Assert.Equal(72, effective.HistoryHours);
        Assert.Equal(900, effective.MaxStreams);
    }

    [Fact]
    public void Partial_overrides_only_replace_provided_fields()
    {
        SeedGlobal(72, 12, 900, "globalterm");

        var effective = _provider.Resolve(new DiscoveryOverrides(HistoryHours: 200));

        Assert.Equal("globalterm", effective.Keyword);
        Assert.Equal(12, effective.MinHistoryHours);
        Assert.Equal(200, effective.HistoryHours);
        Assert.Equal(900, effective.MaxStreams);
    }

    [Fact]
    public void Inverted_window_after_history_override_resets_min_to_zero()
    {
        SeedGlobal(720, 400, 500, "portugal");

        var effective = _provider.Resolve(new DiscoveryOverrides(HistoryHours: 100));

        Assert.Equal(100, effective.HistoryHours);
        Assert.Equal(0, effective.MinHistoryHours);
    }

    // ==================== Validação / clamping ====================

    [Theory]
    [InlineData(-1)]
    [InlineData(DiscoverySettings.MaxValidHistoryHours + 1)]
    public void WithOverrides_ignores_invalid_min_and_inherits_non_zero_global(int badMin)
    {
        // Global com min não-zero: distingue "override inválido ignorado →
        // herda a global" de "reset a 0" (o valor herdado é o da global).
        const int globalMin = 240;
        var global = new DiscoverySettings { MinHistoryHours = globalMin, HistoryHours = 720 };

        var effective = global.WithOverrides(null, null, null, badMin);

        Assert.Equal(globalMin, effective.MinHistoryHours);
    }

    [Fact]
    public void WithOverrides_accepts_valid_min()
    {
        var global = new DiscoverySettings { MinHistoryHours = 0, HistoryHours = 720 };

        var effective = global.WithOverrides(null, null, null, 384);

        Assert.Equal(384, effective.MinHistoryHours);
    }

    [Fact]
    public void ParseStartPayload_keeps_valid_min_history_hours()
    {
        var request = LiveRunApiMappings.ParseStartPayload(new LiveRunStartPayload
        {
            MinHistoryHours = 12,
        });

        Assert.NotNull(request);
        Assert.Equal(12, request!.MinHistoryHours);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(DiscoverySettings.MaxValidHistoryHours + 1)]
    public void ParseStartPayload_ignores_out_of_range_min_history_hours(int badMin)
    {
        var request = LiveRunApiMappings.ParseStartPayload(new LiveRunStartPayload
        {
            MinHistoryHours = badMin,
        });

        Assert.NotNull(request);
        Assert.Null(request!.MinHistoryHours);
    }

    // ==================== Acção job-aware ====================

    [Fact]
    public async Task Telegram_action_applies_job_overrides()
    {
        SeedGlobal(24, 0, 500, "portugal");

        LiveRunRequest? captured = null;
        var host = new LiveRunHost(_factory);
        host.ConfigureExecutor(_ => new CapturingPipeline(r => captured = r));
        var action = new ScheduledTelegramRunAction(host, LiveRunMode.Telegram, _provider);

        var result = await action.ExecuteAsync(
            new ScheduledJobContext(1, "nightly", ScheduledTelegramRunAction.TelegramActionName,
                new DiscoveryOverrides("jobterm", 6, 48, 123)),
            CancellationToken.None);

        Assert.Equal("live-run:telegram:ok", result);
        Assert.NotNull(captured);
        Assert.Equal("jobterm", captured!.Keyword);
        Assert.Equal(6, captured.MinHistoryHours);
        Assert.Equal(48, captured.HistoryHours);
        Assert.Equal(123, captured.MaxStreams);
        Assert.Equal(LiveRunSource.Scheduler, captured.Source);
    }

    [Fact]
    public async Task Telegram_action_without_overrides_uses_global()
    {
        SeedGlobal(96, 24, 654, "agendado");

        LiveRunRequest? captured = null;
        var host = new LiveRunHost(_factory);
        host.ConfigureExecutor(_ => new CapturingPipeline(r => captured = r));
        var action = new ScheduledTelegramRunAction(host, LiveRunMode.TelegramMaintain, _provider);

        var result = await action.ExecuteAsync(CancellationToken.None);

        Assert.Equal("live-run:telegram-maintain:ok", result);
        Assert.NotNull(captured);
        Assert.Equal("agendado", captured!.Keyword);
        Assert.Equal(24, captured.MinHistoryHours);
        Assert.Equal(96, captured.HistoryHours);
        Assert.Equal(654, captured.MaxStreams);
    }

    [Fact]
    public async Task Telegram_action_without_coordinator_returns_not_configured()
    {
        var action = new ScheduledTelegramRunAction(null, LiveRunMode.Telegram, _provider);

        var result = await action.ExecuteAsync(
            new ScheduledJobContext(1, "x", ScheduledTelegramRunAction.TelegramActionName,
                new DiscoveryOverrides("jobterm", 6, 48, 123)),
            CancellationToken.None);

        Assert.Equal(ScheduledTelegramRunAction.NotConfiguredResult, result);
    }

    // ==================== Runner: desserialização ====================

    [Fact]
    public async Task Runner_deserializes_discovery_json_and_passes_to_action()
    {
        SeedGlobal(24, 0, 500, "portugal");

        LiveRunRequest? captured = null;
        var liveRunHost = new LiveRunHost(_factory);
        liveRunHost.ConfigureExecutor(_ => new CapturingPipeline(r => captured = r));
        using var automation = ScheduledAutomationHost.Build(
            _resolver, _outputDir, DispatcharrConfig.Disabled(),
            liveRunHost: liveRunHost, discoverySettings: _provider);

        var job = await _resolver.UpsertScheduledJobAsync(
            "runner-override", "0 * * * *",
            ScheduledTelegramRunAction.TelegramActionName, isEnabled: true,
            discoveryJson: new DiscoveryOverrides("runnerterm", 3, 36, 77).ToJson());
        await ForceDueAsync(job.Id);

        var ran = await automation.Runner.TickOnceAsync();

        Assert.Equal(1, ran);
        Assert.NotNull(captured);
        Assert.Equal("runnerterm", captured!.Keyword);
        Assert.Equal(3, captured.MinHistoryHours);
        Assert.Equal(36, captured.HistoryHours);
        Assert.Equal(77, captured.MaxStreams);

        var reloaded = (await _resolver.ListScheduledJobsAsync()).Single(j => j.Id == job.Id);
        Assert.Equal("live-run:telegram:ok", reloaded.LastResult);
    }

    [Fact]
    public async Task Runner_invalid_discovery_json_falls_back_to_global_without_crashing()
    {
        SeedGlobal(120, 0, 888, "fallbackterm");

        LiveRunRequest? captured = null;
        var liveRunHost = new LiveRunHost(_factory);
        liveRunHost.ConfigureExecutor(_ => new CapturingPipeline(r => captured = r));
        using var automation = ScheduledAutomationHost.Build(
            _resolver, _outputDir, DispatcharrConfig.Disabled(),
            liveRunHost: liveRunHost, discoverySettings: _provider);

        await using (var ctx = _factory.CreateDbContext())
        {
            ctx.ScheduledJobs.Add(new ScheduledJobEntity
            {
                Name = "runner-invalid",
                CronExpression = "0 * * * *",
                ActionName = ScheduledTelegramRunAction.TelegramActionName,
                IsEnabled = true,
                DiscoveryJson = "{ this is not valid json",
                NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var originalOut = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        int ran;
        try
        {
            ran = await automation.Runner.TickOnceAsync();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal(1, ran);
        Assert.NotNull(captured);
        Assert.Equal("fallbackterm", captured!.Keyword);
        Assert.Equal(120, captured.HistoryHours);
        Assert.Equal(888, captured.MaxStreams);

        // DC-9 (fix Low) — o JSON inválido não é descartado em silêncio.
        var output = sw.ToString();
        Assert.Contains("DiscoveryJson inválido", output, StringComparison.Ordinal);
        Assert.Contains("runner-invalid", output, StringComparison.Ordinal);
    }

    // ==================== CRUD HTTP (POST/GET) ====================

    [Fact]
    public async Task Post_and_get_scheduled_job_round_trip_discovery_fields()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var body = JsonSerializer.Serialize(new
        {
            name = "dc9-crud-job",
            cronExpression = "0 3 * * *",
            actionName = ScheduledTelegramRunAction.TelegramActionName,
            isEnabled = true,
            keyword = "crupterm",
            minHistoryHours = 5,
            historyHours = 50,
            maxStreams = 111,
        });

        var create = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, "/api/catalog/scheduled-jobs", body, csrf));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        using (var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync()))
        {
            var disc = created.RootElement.GetProperty("discovery");
            Assert.Equal("crupterm", disc.GetProperty("keyword").GetString());
            Assert.Equal(5, disc.GetProperty("minHistoryHours").GetInt32());
            Assert.Equal(50, disc.GetProperty("historyHours").GetInt32());
            Assert.Equal(111, disc.GetProperty("maxStreams").GetInt32());
            Assert.True(created.RootElement.GetProperty("effectiveDiscovery").TryGetProperty("keyword", out _));
        }

        var list = await harness.Client.GetAsync("/api/catalog/scheduled-jobs");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var listed = listDoc.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("name").GetString() == "dc9-crud-job");
        var listedDisc = listed.GetProperty("discovery");
        Assert.Equal("crupterm", listedDisc.GetProperty("keyword").GetString());
        Assert.Equal(50, listedDisc.GetProperty("historyHours").GetInt32());

        // Sem overrides: discovery ausente (herda a global).
        var noOverride = JsonSerializer.Serialize(new
        {
            name = "dc9-crud-nodisc",
            cronExpression = "0 4 * * *",
            actionName = ScheduledTelegramRunAction.TelegramActionName,
            isEnabled = true,
        });
        var create2 = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, "/api/catalog/scheduled-jobs", noOverride, csrf));
        Assert.Equal(HttpStatusCode.OK, create2.StatusCode);
        using (var created2 = JsonDocument.Parse(await create2.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Null, created2.RootElement.GetProperty("discovery").ValueKind);
        }

        // Persistência directa confirmada.
        await using var ctx = _factory.CreateDbContext();
        var persisted = await ctx.ScheduledJobs.AsNoTracking().SingleAsync(j => j.Name == "dc9-crud-job");
        Assert.NotNull(persisted.DiscoveryJson);
        Assert.Null((await ctx.ScheduledJobs.AsNoTracking().SingleAsync(j => j.Name == "dc9-crud-nodisc")).DiscoveryJson);
    }

    [Fact]
    public async Task Upsert_clears_overrides_when_payload_has_none()
    {
        var job = await _resolver.UpsertScheduledJobAsync(
            "clear-overrides", "0 * * * *",
            ScheduledTelegramRunAction.TelegramActionName, isEnabled: true,
            discoveryJson: new DiscoveryOverrides("x", 1, 2, 3).ToJson());
        Assert.NotNull(job.DiscoveryJson);

        var updated = await _resolver.UpsertScheduledJobAsync(
            "clear-overrides", "0 * * * *",
            ScheduledTelegramRunAction.TelegramActionName, isEnabled: true,
            discoveryJson: null);
        Assert.Null(updated.DiscoveryJson);
    }

    // ============ Validação server-side dos overrides (POST) ============

    [Fact]
    public async Task Post_scheduled_job_nulls_all_invalid_overrides_and_inherits_global()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var body = JsonSerializer.Serialize(new
        {
            name = "dc9-invalid-disc",
            cronExpression = "0 5 * * *",
            actionName = ScheduledTelegramRunAction.TelegramActionName,
            isEnabled = true,
            keyword = "   ",
            minHistoryHours = -5,
            historyHours = 0,
            maxStreams = 0,
        });

        var create = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, "/api/catalog/scheduled-jobs", body, csrf));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        using (var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Null, created.RootElement.GetProperty("discovery").ValueKind);
        }

        await using var ctx = _factory.CreateDbContext();
        var persisted = await ctx.ScheduledJobs.AsNoTracking().SingleAsync(j => j.Name == "dc9-invalid-disc");
        Assert.Null(persisted.DiscoveryJson);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(DiscoverySettings.MaxValidHistoryHours + 1)]
    public async Task Post_scheduled_job_nulls_out_of_range_min_history_hours(int badMin)
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var body = JsonSerializer.Serialize(new
        {
            name = $"dc9-badmin-{badMin}",
            cronExpression = "0 5 * * *",
            actionName = ScheduledTelegramRunAction.TelegramActionName,
            isEnabled = true,
            minHistoryHours = badMin,
        });

        var create = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, "/api/catalog/scheduled-jobs", body, csrf));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, created.RootElement.GetProperty("discovery").ValueKind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(DiscoverySettings.MaxValidHistoryHours + 1)]
    public async Task Post_scheduled_job_nulls_out_of_range_history_hours(int badHistory)
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var body = JsonSerializer.Serialize(new
        {
            name = $"dc9-badhist-{badHistory}",
            cronExpression = "0 5 * * *",
            actionName = ScheduledTelegramRunAction.TelegramActionName,
            isEnabled = true,
            historyHours = badHistory,
        });

        var create = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, "/api/catalog/scheduled-jobs", body, csrf));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, created.RootElement.GetProperty("discovery").ValueKind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    public async Task Post_scheduled_job_nulls_out_of_range_max_streams(int badMax)
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var body = JsonSerializer.Serialize(new
        {
            name = $"dc9-badmax-{badMax}",
            cronExpression = "0 5 * * *",
            actionName = ScheduledTelegramRunAction.TelegramActionName,
            isEnabled = true,
            maxStreams = badMax,
        });

        var create = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, "/api/catalog/scheduled-jobs", body, csrf));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, created.RootElement.GetProperty("discovery").ValueKind);
    }

    [Fact]
    public async Task Post_scheduled_job_keeps_valid_bounds_and_trims_keyword()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var body = JsonSerializer.Serialize(new
        {
            name = "dc9-bounds-ok",
            cronExpression = "0 5 * * *",
            actionName = ScheduledTelegramRunAction.TelegramActionName,
            isEnabled = true,
            keyword = "  crup  ",
            minHistoryHours = 0,
            historyHours = 24 * 30,
            maxStreams = DiscoverySettings.MinMaxStreams,
        });

        var create = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, "/api/catalog/scheduled-jobs", body, csrf));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var disc = created.RootElement.GetProperty("discovery");
        Assert.Equal("crup", disc.GetProperty("keyword").GetString());
        Assert.Equal(0, disc.GetProperty("minHistoryHours").GetInt32());
        Assert.Equal(24 * 30, disc.GetProperty("historyHours").GetInt32());
        Assert.Equal(DiscoverySettings.MinMaxStreams, disc.GetProperty("maxStreams").GetInt32());
    }

    [Theory]
    [InlineData(720)]
    [InlineData(DiscoverySettings.MaxValidHistoryHours)]
    public async Task Post_scheduled_job_accepts_history_hours_up_to_max(int historyHours)
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var body = JsonSerializer.Serialize(new
        {
            name = $"dc9-hist-{historyHours}",
            cronExpression = "0 5 * * *",
            actionName = ScheduledTelegramRunAction.TelegramActionName,
            isEnabled = true,
            historyHours,
        });

        var create = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, "/api/catalog/scheduled-jobs", body, csrf));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        using (var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync()))
        {
            var disc = created.RootElement.GetProperty("discovery");
            Assert.Equal(JsonValueKind.Object, disc.ValueKind);
            Assert.Equal(historyHours, disc.GetProperty("historyHours").GetInt32());
        }

        await using var ctx = _factory.CreateDbContext();
        var persisted = await ctx.ScheduledJobs.AsNoTracking()
            .SingleAsync(j => j.Name == $"dc9-hist-{historyHours}");
        Assert.NotNull(persisted.DiscoveryJson);
        Assert.Contains($"\"historyHours\":{historyHours}", persisted.DiscoveryJson);
    }

    // ==================== Migração ====================
    [Fact]
    public async Task Migration_adds_discovery_json_column()
    {
        await using var ctx = _factory.CreateDbContext();

        var migrations = await ctx.Database
            .SqlQueryRaw<string>("SELECT MigrationId AS Value FROM __EFMigrationsHistory")
            .ToListAsync();
        Assert.Contains(LatestMigration, migrations);

        var columns = await ctx.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('scheduled_jobs')")
            .ToListAsync();
        Assert.Contains("DiscoveryJson", columns);
    }

    // ==================== HTML ====================

    [Fact]
    public async Task Dashboard_html_contains_discovery_controls_and_relabel()
    {
        // Standalone (sem auth) para servir o HTML sem redirect a /bootstrap.
        var harness = StartHtmlHarness();
        var response = await harness.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("data-sched-create='discoveryKeyword'", html);
        Assert.Contains("data-sched-create='discoveryMinHistoryHours'", html);
        Assert.Contains("data-sched-create='discoveryHistoryHours'", html);
        Assert.Contains("data-sched-create='discoveryMaxStreams'", html);
        Assert.Contains("id='schedDiscoveryPreview'", html);
        Assert.Contains("id='liveRunMinHistoryHours'", html);
        Assert.Contains("Configuração de discovery predefinida", html);
    }

    [Fact]
    public async Task Dashboard_html_preview_enforces_same_ranges_as_backend()
    {
        var harness = StartHtmlHarness();
        var response = await harness.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        // A pré-visualização normaliza valores fora do intervalo (herda a
        // global), com os mesmos limites do POST server-side.
        Assert.Contains("function schedDiscValidInt(", html);
        Assert.Contains("function schedDiscOutOfRange(", html);
        Assert.Contains("schedDiscValidInt('discoveryMinHistoryHours', 0, 1440)", html);
        Assert.Contains("schedDiscValidInt('discoveryHistoryHours', 1, 1440)", html);
        Assert.Contains("schedDiscValidInt('discoveryMaxStreams', 1, null)", html);
    }

    // ==================== Helpers ====================

    private DashboardBootstrapEndpointTests.DashboardHarness StartHarness()
    {
        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history,
            _lifecycle, _auth, _bootstrap, webToken: null,
            runtimeDataDir: _runtimeDataDir);
        return _harness;
    }

    private DashboardBootstrapEndpointTests.DashboardHarness StartHtmlHarness()
    {
        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history,
            lifecycle: null, auth: null, bootstrap: null, webToken: null,
            standalone: true, runtimeDataDir: _runtimeDataDir);
        return _harness;
    }

    private async Task ForceDueAsync(long jobId)
    {
        await using var ctx = _factory.CreateDbContext();
        var entity = await ctx.ScheduledJobs.FirstAsync(j => j.Id == jobId);
        entity.NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await ctx.SaveChangesAsync();
    }

    private static async Task ReachReadyAsync(DashboardBootstrapEndpointTests.DashboardHarness harness)
    {
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.PostAsync("/api/bootstrap/start", EmptyJson())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.PostAsync(
            "/api/bootstrap/admin",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.PostAsync("/api/bootstrap/complete", EmptyJson())).StatusCode);
    }

    private static async Task<string> LoginAsync(DashboardBootstrapEndpointTests.DashboardHarness harness)
    {
        var login = await harness.Client.PostAsync(
            "/api/session",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("csrfToken").GetString()!;
    }

    private static StringContent EmptyJson() => new("{}", Encoding.UTF8, "application/json");

    private static HttpRequestMessage WithCsrf(HttpMethod method, string path, string body, string csrf)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CSRF-Token", csrf);
        return request;
    }

    private sealed class CapturingPipeline : IRunPipeline
    {
        private readonly Action<LiveRunRequest> _onExecute;
        public CapturingPipeline(Action<LiveRunRequest> onExecute) => _onExecute = onExecute;
        public Task ExecuteAsync(LiveRunRequest request, CancellationToken cancellationToken)
        {
            _onExecute(request);
            return Task.CompletedTask;
        }
    }
}
