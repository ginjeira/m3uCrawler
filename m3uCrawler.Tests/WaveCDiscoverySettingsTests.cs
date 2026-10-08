using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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
using m3uCrawler.Services.Validation;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Wave C — Fonte de verdade única dos parâmetros de discovery
/// (persistência em <c>app_settings.json</c>), overrides de run e
/// paridade do scheduler (reload de config/policy por execução).
///
/// <para>
/// Não toca a rede: as acções são exercitadas com fakes e o endpoint
/// HTTP corre num <see cref="HttpListener"/> de loopback com um
/// runtime-data temporário (scope estático isolado).
/// </para>
/// </summary>
[Collection("DashboardStaticState")]
public class WaveCDiscoverySettingsTests : IAsyncLifetime
{
    private const string ValidPassword = "a-very-strong-password";

    private readonly string _root;
    private readonly string _runtimeDataDir;
    private readonly string _outputDir;
    private readonly string _dbPath;

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;
    private WaveCDashboardHarness? _harness;

    public WaveCDiscoverySettingsTests()
    {
        _root = TestTempDb.SuitePath($"wavec-{Guid.NewGuid():N}");
        _runtimeDataDir = Path.Combine(_root, "runtime-data");
        _outputDir = Path.Combine(_root, "output");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_runtimeDataDir);
        Directory.CreateDirectory(_outputDir);
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        await using (var ctx = await bootstrapper.InitializeAsync())
        {
            // Bootstrap fecha o contexto.
        }
        _resolver = new CatalogResolver(_factory, _dbPath);
    }

    public async Task DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
        TestTempDb.Cleanup(_dbPath);
        TestTempDb.CleanupDirectory(_root);
    }

    private AppSettingsStore NewStore() => new(_runtimeDataDir);
    private DiscoverySettingsProvider NewProvider()
        => new(NewStore());

    // ==================== Persistência / validação ====================

    [Fact]
    public void AppSettingsStore_persists_discovery_round_trip()
    {
        var store = NewStore();
        var settings = store.Load();
        settings.Discovery = new DiscoverySettings
        {
            HistoryHours = 48,
            MaxStreams = 321,
            Keyword = "iptv custom",
        };
        store.Save(settings);

        var reloaded = new AppSettingsStore(_runtimeDataDir).Load();

        Assert.Equal(48, reloaded.Discovery.HistoryHours);
        Assert.Equal(321, reloaded.Discovery.MaxStreams);
        Assert.Equal("iptv custom", reloaded.Discovery.Keyword);
    }

    [Fact]
    public void AppSettingsStore_fresh_file_defaults_discovery()
    {
        var dir = Path.Combine(TestTempDb.EnsureSuiteRoot(), $"wavec-fresh-{Guid.NewGuid():N}");
        try
        {
            var store = new AppSettingsStore(dir);
            var loaded = store.Load();

            Assert.Equal(DiscoverySettings.DefaultHistoryHours, loaded.Discovery.HistoryHours);
            Assert.Equal(DiscoverySettings.DefaultMaxStreams, loaded.Discovery.MaxStreams);
            Assert.Equal(DiscoverySettings.DefaultKeyword, loaded.Discovery.Keyword);
        }
        finally
        {
            TestTempDb.CleanupDirectory(dir);
        }
    }

    [Fact]
    public void DiscoverySettings_sanitizes_invalid_values_to_defaults()
    {
        var settings = new DiscoverySettings
        {
            HistoryHours = 0,
            MaxStreams = -5,
            Keyword = "   ",
        };

        settings.Sanitize();

        Assert.Equal(DiscoverySettings.DefaultHistoryHours, settings.HistoryHours);
        Assert.Equal(DiscoverySettings.DefaultMaxStreams, settings.MaxStreams);
        Assert.Equal(DiscoverySettings.DefaultKeyword, settings.Keyword);
    }

    [Fact]
    public void DiscoverySettings_sanitizes_above_max_history_hours()
    {
        var settings = new DiscoverySettings { HistoryHours = DiscoverySettings.MaxValidHistoryHours + 1 };
        settings.Sanitize();
        Assert.Equal(DiscoverySettings.DefaultHistoryHours, settings.HistoryHours);
    }

    [Theory]
    [InlineData(0, 500, "portugal", false)]
    [InlineData(721, 500, "portugal", true)]
    [InlineData(1441, 500, "portugal", false)]
    [InlineData(1, 500, "portugal", true)]
    [InlineData(720, 500, "portugal", true)]
    [InlineData(24, 0, "portugal", false)]
    [InlineData(24, 1, "portugal", true)]
    [InlineData(24, 500, "   ", false)]
    public void DiscoverySettings_validate_enforces_ranges(
        int historyHours, int maxStreams, string keyword, bool expectedValid)
    {
        var settings = new DiscoverySettings
        {
            HistoryHours = historyHours,
            MaxStreams = maxStreams,
            Keyword = keyword,
        };

        var valid = settings.TryValidate(out var error);

        Assert.Equal(expectedValid, valid);
        if (!expectedValid)
        {
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    [Fact]
    public void DiscoverySettings_default_min_history_hours_is_zero()
    {
        var settings = new DiscoverySettings();
        Assert.Equal(0, settings.MinHistoryHours);
        Assert.Equal(DiscoverySettings.DefaultMinHistoryHours, settings.MinHistoryHours);
    }

    // Nota: a linha (0, 0, true) do plano colide com o invariante
    // MinValidHistoryHours = 1 (historyHours = 0 é inválido, como já
    // documenta DiscoverySettings_validate_enforces_ranges). Usa-se
    // (0, 1, true) para exercitar "min = 0 aceite com o menor max válido".
    [Theory]
    [InlineData(0, 384, true)]
    [InlineData(384, 720, true)]
    [InlineData(720, 1000, true)]
    [InlineData(0, 1, true)]
    [InlineData(-1, 24, false)]
    [InlineData(400, 384, false)]
    [InlineData(1001, 1000, false)]
    public void DiscoverySettings_validate_enforces_min_max_window(
        int min, int max, bool expectedValid)
    {
        var settings = new DiscoverySettings
        {
            MinHistoryHours = min,
            HistoryHours = max,
            MaxStreams = 500,
            Keyword = "portugal",
        };

        var valid = settings.TryValidate(out var error);

        Assert.Equal(expectedValid, valid);
        if (!expectedValid)
        {
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    [Fact]
    public void DiscoverySettings_sanitizes_inverted_or_negative_min_to_zero()
    {
        var negative = new DiscoverySettings { MinHistoryHours = -5, HistoryHours = 24 };
        negative.Sanitize();
        Assert.Equal(0, negative.MinHistoryHours);

        var inverted = new DiscoverySettings { MinHistoryHours = 800, HistoryHours = 100 };
        inverted.Sanitize();
        Assert.Equal(0, inverted.MinHistoryHours);
        Assert.Equal(100, inverted.HistoryHours);
    }

    [Fact]
    public void AppSettingsStore_persists_min_history_hours_round_trip()
    {
        var store = NewStore();
        var settings = store.Load();
        settings.Discovery = new DiscoverySettings
        {
            MinHistoryHours = 384,
            HistoryHours = 720,
            MaxStreams = 321,
            Keyword = "iptv custom",
        };
        store.Save(settings);

        var reloaded = new AppSettingsStore(_runtimeDataDir).Load();

        Assert.Equal(384, reloaded.Discovery.MinHistoryHours);
        Assert.Equal(720, reloaded.Discovery.HistoryHours);
    }

    // ==================== Precedência / resolução ====================

    [Fact]
    public void Provider_resolve_prefers_overrides_over_persisted_values()
    {
        var store = NewStore();
        var settings = store.Load();
        settings.Discovery = new DiscoverySettings
        {
            HistoryHours = 24,
            MaxStreams = 500,
            Keyword = "portugal",
        };
        store.Save(settings);

        var resolved = NewProvider().Resolve("custom", 48, 123);

        Assert.Equal("custom", resolved.Keyword);
        Assert.Equal(48, resolved.HistoryHours);
        Assert.Equal(123, resolved.MaxStreams);
    }

    [Fact]
    public void Provider_resolve_falls_back_to_persisted_when_overrides_absent_or_invalid()
    {
        var store = NewStore();
        var settings = store.Load();
        settings.Discovery = new DiscoverySettings
        {
            HistoryHours = 72,
            MaxStreams = 900,
            Keyword = "persistido",
        };
        store.Save(settings);

        var provider = NewProvider();

        var noOverrides = provider.Resolve(null, null, null);
        Assert.Equal("persistido", noOverrides.Keyword);
        Assert.Equal(72, noOverrides.HistoryHours);
        Assert.Equal(900, noOverrides.MaxStreams);

        var invalidOverrides = provider.Resolve("  ", 0, 0);
        Assert.Equal("persistido", invalidOverrides.Keyword);
        Assert.Equal(72, invalidOverrides.HistoryHours);
        Assert.Equal(900, invalidOverrides.MaxStreams);
    }

    [Fact]
    public void Provider_resolve_resets_min_when_max_override_inverts_window()
    {
        var store = NewStore();
        var provider = NewProvider();

        var persisted = store.Load();
        persisted.Discovery = new DiscoverySettings
        {
            MinHistoryHours = 400,
            HistoryHours = 720,
            MaxStreams = 500,
            Keyword = "portugal",
        };
        store.Save(persisted);

        var inverted = provider.Resolve(null, 100, null);
        Assert.Equal(0, inverted.MinHistoryHours);
        Assert.Equal(100, inverted.HistoryHours);

        var compatiblePersisted = store.Load();
        compatiblePersisted.Discovery.MinHistoryHours = 384;
        compatiblePersisted.Discovery.HistoryHours = 720;
        store.Save(compatiblePersisted);

        var compatible = provider.Resolve(null, 720, null);
        Assert.Equal(384, compatible.MinHistoryHours);
        Assert.Equal(720, compatible.HistoryHours);
    }

    [Fact]
    public void Provider_resolve_observes_edits_without_process_restart()
    {
        var store = NewStore();
        var provider = NewProvider();

        var before = provider.Resolve(null, null, null);
        Assert.Equal(DiscoverySettings.DefaultHistoryHours, before.HistoryHours);

        var updated = store.Load();
        updated.Discovery.HistoryHours = 120;
        updated.Discovery.MaxStreams = 777;
        updated.Discovery.Keyword = "actualizado";
        store.Save(updated);

        var after = provider.Resolve(null, null, null);
        Assert.Equal(120, after.HistoryHours);
        Assert.Equal(777, after.MaxStreams);
        Assert.Equal("actualizado", after.Keyword);
    }

    [Fact]
    public void Start_payload_mapping_keeps_run_overrides()
    {
        var request = LiveRunApiMappings.ParseStartPayload(new LiveRunStartPayload
        {
            Mode = "telegram",
            Keyword = "override-term",
            HistoryHours = 48,
            MaxStreams = 123,
        });

        Assert.NotNull(request);
        Assert.Equal("override-term", request!.Keyword);
        Assert.Equal(48, request.HistoryHours);
        Assert.Equal(123, request.MaxStreams);
    }

    [Fact]
    public void Start_payload_mapping_accepts_telegram_maintain_wire_name()
    {
        var request = LiveRunApiMappings.ParseStartPayload(new LiveRunStartPayload
        {
            Mode = "telegram-maintain",
        });

        Assert.NotNull(request);
        Assert.Equal(LiveRunMode.TelegramMaintain, request!.Mode);
    }

    [Fact]
    public void Start_payload_mapping_rejects_unknown_mode()
    {
        var request = LiveRunApiMappings.ParseStartPayload(new LiveRunStartPayload
        {
            Mode = "bogus",
        });

        Assert.Null(request);
    }

    [Fact]
    public void Start_payload_mapping_defaults_without_overrides()
    {
        var request = LiveRunApiMappings.ParseStartPayload(new LiveRunStartPayload());

        Assert.NotNull(request);
        Assert.Equal(LiveRunMode.Telegram, request!.Mode);
        Assert.Equal(LiveRunSource.Manual, request.Source);
        Assert.Null(request.Keyword);
        Assert.Null(request.HistoryHours);
        Assert.Null(request.MaxStreams);
    }

    // ==================== Scheduler: Telegram action ====================

    [Fact]
    public async Task Telegram_action_populates_request_from_persisted_settings()
    {
        var store = NewStore();
        var settings = store.Load();
        settings.Discovery = new DiscoverySettings
        {
            HistoryHours = 96,
            MaxStreams = 654,
            Keyword = "agendado",
        };
        store.Save(settings);

        LiveRunRequest? captured = null;
        var host = new LiveRunHost(_factory);
        host.ConfigureExecutor(_ => new CapturingPipeline(r => captured = r));

        var action = new ScheduledTelegramRunAction(
            host, LiveRunMode.Telegram, new DiscoverySettingsProvider(store));

        var result = await action.ExecuteAsync(CancellationToken.None);

        Assert.Equal("live-run:telegram:ok", result);
        Assert.NotNull(captured);
        Assert.Equal("agendado", captured!.Keyword);
        Assert.Equal(96, captured.HistoryHours);
        Assert.Equal(654, captured.MaxStreams);
        Assert.Equal(LiveRunSource.Scheduler, captured.Source);
    }

    [Fact]
    public async Task Telegram_action_rereads_settings_on_each_execution()
    {
        var store = NewStore();
        var provider = new DiscoverySettingsProvider(store);

        LiveRunRequest? captured = null;
        var host = new LiveRunHost(_factory);
        host.ConfigureExecutor(_ => new CapturingPipeline(r => captured = r));
        var action = new ScheduledTelegramRunAction(host, LiveRunMode.Telegram, provider);

        await action.ExecuteAsync(CancellationToken.None);
        Assert.Equal("portugal", captured!.Keyword);

        var updated = store.Load();
        updated.Discovery.Keyword = "segunda";
        updated.Discovery.HistoryHours = 200;
        store.Save(updated);

        await action.ExecuteAsync(CancellationToken.None);
        Assert.Equal("segunda", captured!.Keyword);
        Assert.Equal(200, captured.HistoryHours);
    }

    // ==================== Scheduler: config / policy reload ====================

    [Fact]
    public async Task Dispatcharr_action_reloads_config_per_run()
    {
        var calls = 0;
        var action = new ScheduledDispatcharrSyncAction(
            DispatcharrConfig.Disabled(),
            new ScheduledActionOptions { OutputDir = _outputDir },
            catalog: null,
            transport: null,
            configLoader: () =>
            {
                calls++;
                // 1ª execução: desactivado; 2ª execução: activo (sem
                // playlist, logo "no-playlist" prova que leu a config nova).
                return calls == 1
                    ? DispatcharrConfig.Disabled()
                    : new DispatcharrConfig { Enabled = true, BaseUrl = "http://fake.local" };
            });

        var first = await action.ExecuteAsync(CancellationToken.None);
        var second = await action.ExecuteAsync(CancellationToken.None);

        Assert.Equal("dispatcharr-disabled", first);
        Assert.Equal("no-playlist", second);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Validation_action_reloads_policy_per_run()
    {
        var store = new StreamValidationPolicyStore(_runtimeDataDir);
        store.Save(new StreamValidationOptions { ConnectionTimeoutSeconds = 3 });

        var state = new StreamValidationState(store);
        Assert.Equal(3, state.Options.ConnectionTimeoutSeconds);

        // Edição "no dashboard" depois do state ter sido carregado.
        store.Save(new StreamValidationOptions { ConnectionTimeoutSeconds = 7 });

        var action = new ScheduledValidationAction(
            new M3uTesterService(),
            new PlaylistManagerService(),
            new ScheduledActionOptions { OutputDir = _outputDir },
            validationState: state);

        var result = await action.ExecuteAsync(CancellationToken.None);

        Assert.Equal("no-playlist", result);
        Assert.Equal(7, state.Options.ConnectionTimeoutSeconds);
    }

    // ==================== Endpoint HTTP ====================

    [Fact]
    public async Task Get_discovery_settings_requires_authentication_in_user_auth()
    {
        var harness = StartHarness(withAuth: true);
        await ReachReadyAndLoginAsync(harness);

        using var fresh = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{harness.Port}") };
        var response = await fresh.GetAsync("/api/discovery/settings");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_discovery_settings_returns_persisted_values()
    {
        var store = NewStore();
        var settings = store.Load();
        settings.Discovery = new DiscoverySettings
        {
            HistoryHours = 48,
            MaxStreams = 444,
            Keyword = "guardado",
        };
        store.Save(settings);

        var harness = StartHarness(withAuth: true);
        await ReachReadyAndLoginAsync(harness);

        var get = await harness.Client.GetAsync("/api/discovery/settings");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal(48, doc.RootElement.GetProperty("historyHours").GetInt32());
        Assert.Equal(444, doc.RootElement.GetProperty("maxStreams").GetInt32());
        Assert.Equal("guardado", doc.RootElement.GetProperty("keyword").GetString());
    }

    [Fact]
    public async Task Post_discovery_settings_without_csrf_is_rejected()
    {
        var harness = StartHarness(withAuth: true);
        await ReachReadyAndLoginAsync(harness);

        var response = await harness.Client.PostAsync(
            "/api/discovery/settings",
            JsonBody(JsonSerializer.Serialize(new { historyHours = 48, maxStreams = 100, keyword = "x" })));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("csrf-invalid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_discovery_settings_validates_and_persists()
    {
        var harness = StartHarness(withAuth: true);
        var csrf = await ReachReadyAndLoginAsync(harness);

        var invalid = await PostWithCsrfAsync(
            harness, csrf,
            JsonSerializer.Serialize(new { historyHours = 0, maxStreams = 100, keyword = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("historyHours", await invalid.Content.ReadAsStringAsync());

        var invalidMax = await PostWithCsrfAsync(
            harness, csrf,
            JsonSerializer.Serialize(new { historyHours = 24, maxStreams = 0, keyword = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, invalidMax.StatusCode);
        Assert.Contains("maxStreams", await invalidMax.Content.ReadAsStringAsync());

        var valid = await PostWithCsrfAsync(
            harness, csrf,
            JsonSerializer.Serialize(new { historyHours = 48, maxStreams = 100, keyword = "novo" }));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        using (var doc = JsonDocument.Parse(await valid.Content.ReadAsStringAsync()))
        {
            Assert.Equal(48, doc.RootElement.GetProperty("historyHours").GetInt32());
            Assert.Equal(100, doc.RootElement.GetProperty("maxStreams").GetInt32());
            Assert.Equal("novo", doc.RootElement.GetProperty("keyword").GetString());
        }

        // Persistido no mesmo ficheiro e visível por leitura directa.
        var persisted = new AppSettingsStore(_runtimeDataDir).Load().Discovery;
        Assert.Equal(48, persisted.HistoryHours);
        Assert.Equal(100, persisted.MaxStreams);
        Assert.Equal("novo", persisted.Keyword);
    }

    [Fact]
    public async Task Post_discovery_settings_rejects_above_max_history_hours()
    {
        var harness = StartHarness(withAuth: true);
        var csrf = await ReachReadyAndLoginAsync(harness);

        var response = await PostWithCsrfAsync(
            harness, csrf,
            JsonSerializer.Serialize(new { historyHours = 1441, maxStreams = 100, keyword = "x" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("historyHours", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_discovery_settings_returns_min_history_hours()
    {
        var store = NewStore();
        var settings = store.Load();
        settings.Discovery = new DiscoverySettings
        {
            MinHistoryHours = 384,
            HistoryHours = 720,
            MaxStreams = 444,
            Keyword = "guardado",
        };
        store.Save(settings);

        var harness = StartHarness(withAuth: true);
        await ReachReadyAndLoginAsync(harness);

        var get = await harness.Client.GetAsync("/api/discovery/settings");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal(384, doc.RootElement.GetProperty("minHistoryHours").GetInt32());
        Assert.Equal(720, doc.RootElement.GetProperty("historyHours").GetInt32());
    }

    [Fact]
    public async Task Post_discovery_settings_persists_min_and_max_window()
    {
        var harness = StartHarness(withAuth: true);
        var csrf = await ReachReadyAndLoginAsync(harness);

        var valid = await PostWithCsrfAsync(
            harness, csrf,
            JsonSerializer.Serialize(new { historyHours = 720, minHistoryHours = 384, maxStreams = 100, keyword = "novo" }));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        using (var doc = JsonDocument.Parse(await valid.Content.ReadAsStringAsync()))
        {
            Assert.Equal(384, doc.RootElement.GetProperty("minHistoryHours").GetInt32());
            Assert.Equal(720, doc.RootElement.GetProperty("historyHours").GetInt32());
        }

        var persisted = new AppSettingsStore(_runtimeDataDir).Load().Discovery;
        Assert.Equal(384, persisted.MinHistoryHours);
        Assert.Equal(720, persisted.HistoryHours);
    }

    [Fact]
    public async Task Post_discovery_settings_rejects_min_above_max()
    {
        var harness = StartHarness(withAuth: true);
        var csrf = await ReachReadyAndLoginAsync(harness);

        var response = await PostWithCsrfAsync(
            harness, csrf,
            JsonSerializer.Serialize(new { historyHours = 384, minHistoryHours = 500 }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("minHistoryHours", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_discovery_settings_rejects_negative_min()
    {
        var harness = StartHarness(withAuth: true);
        var csrf = await ReachReadyAndLoginAsync(harness);

        var response = await PostWithCsrfAsync(
            harness, csrf,
            JsonSerializer.Serialize(new { minHistoryHours = -1 }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("minHistoryHours", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_discovery_settings_gate_does_not_echo_the_body()
    {
        var harness = StartHarness(withAuth: true);
        var csrf = await ReachReadyAndLoginAsync(harness);

        var response = await PostWithCsrfAsync(
            harness, csrf,
            JsonSerializer.Serialize(new { historyHours = 24, maxStreams = 100, keyword = "x" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("csrf", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Put_discovery_settings_is_method_not_allowed()
    {
        var harness = StartHarness(withAuth: true);
        var csrf = await ReachReadyAndLoginAsync(harness);

        var request = new HttpRequestMessage(HttpMethod.Put, "/api/discovery/settings")
        {
            Content = JsonBody(JsonSerializer.Serialize(new { historyHours = 24 })),
        };
        request.Headers.Add("X-CSRF-Token", csrf);
        var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    // ==================== Helpers ====================

    private static StringContent JsonBody(string json)
        => new(json, Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> PostWithCsrfAsync(
        WaveCDashboardHarness harness, string csrf, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/discovery/settings")
        {
            Content = JsonBody(json),
        };
        request.Headers.Add("X-CSRF-Token", csrf);
        return await harness.Client.SendAsync(request);
    }

    private WaveCDashboardHarness StartHarness(bool withAuth)
    {
        _harness = WaveCDashboardHarness.Start(
            _runtimeDataDir,
            _outputDir,
            _resolver,
            new PlaylistComposerService(_factory),
            new ImportHistoryService(_outputDir),
            NewAuthWiring(withAuth));
        return _harness;
    }

    private AuthWiring? NewAuthWiring(bool withAuth)
    {
        if (!withAuth) return null;

        var lifecycleStore = new ConfigurationLifecycleStore(
            Path.Combine(_root, ConfigurationLifecycleStore.FileName));
        var lifecycle = new ConfigurationLifecycleService(lifecycleStore, _factory, _outputDir);
        var users = new AdminUserStore(_factory);
        var auth = new AuthService(users, new SessionStore(_factory));
        var bootstrap = new BootstrapService(
            lifecycle, users,
            new BootstrapConfigurationValidator(_factory, _outputDir));
        return new AuthWiring(lifecycle, auth, bootstrap);
    }

    private async Task<string> ReachReadyAndLoginAsync(WaveCDashboardHarness harness)
    {
        var start = await harness.Client.PostAsync("/api/bootstrap/start", JsonBody("{}"));
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);

        var admin = await harness.Client.PostAsync(
            "/api/bootstrap/admin",
            JsonBody(JsonSerializer.Serialize(new { username = "admin", password = ValidPassword })));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);

        var complete = await harness.Client.PostAsync("/api/bootstrap/complete", JsonBody("{}"));
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        var login = await harness.Client.PostAsync(
            "/api/session",
            JsonBody(JsonSerializer.Serialize(new { username = "admin", password = ValidPassword })));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("csrfToken").GetString()!;
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

    private sealed class AuthWiring
    {
        public AuthWiring(
            ConfigurationLifecycleService lifecycle,
            AuthService auth,
            BootstrapService bootstrap)
        {
            Lifecycle = lifecycle;
            Auth = auth;
            Bootstrap = bootstrap;
        }

        public ConfigurationLifecycleService Lifecycle { get; }
        public AuthService Auth { get; }
        public BootstrapService Bootstrap { get; }
    }

    /// <summary>
    /// Harness de loopback com um runtime-data temporário por pedido
    /// (<see cref="WebDashboardService.StaticRuntimeDataDirScope"/>).
    /// </summary>
    private sealed class WaveCDashboardHarness : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly string _runtimeDataDir;
        private Task _loop = Task.CompletedTask;

        private WaveCDashboardHarness(
            HttpListener listener, HttpClient client, int port, string runtimeDataDir)
        {
            _listener = listener;
            Client = client;
            Port = port;
            _runtimeDataDir = runtimeDataDir;
        }

        public HttpClient Client { get; }
        public int Port { get; }

        public static WaveCDashboardHarness Start(
            string runtimeDataDir,
            string outputDir,
            CatalogResolver resolver,
            PlaylistComposerService composer,
            ImportHistoryService history,
            AuthWiring? auth)
        {
            var port = GetFreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            var handler = new HttpClientHandler
            {
                CookieContainer = new CookieContainer(),
                AllowAutoRedirect = false,
                UseCookies = true,
            };
            var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var harness = new WaveCDashboardHarness(listener, client, port, runtimeDataDir);

            harness._loop = Task.Run(async () =>
            {
                while (!harness._cts.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); }
                    catch { break; }

                    using var runtimeScope =
                        new WebDashboardService.StaticRuntimeDataDirScope(harness._runtimeDataDir);
                    try
                    {
                        await WebDashboardService.HandleRequestWithAuthOnTestAsync(
                            context, outputDir, resolver, composer, history,
                            auth?.Lifecycle, auth?.Auth, auth?.Bootstrap);
                    }
                    catch
                    {
                        try { context.Response.StatusCode = 500; context.Response.Close(); }
                        catch { /* closed */ }
                    }
                }
            });

            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* stopped */ }
            try { _listener.Close(); } catch { /* closed */ }
            try { await _loop; } catch { /* best effort */ }
            Client.Dispose();
            _cts.Dispose();
        }

        private static int GetFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
