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
using m3uCrawler.Services.Auth;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 13 (Wave 13-5) — Endpoint HTTP
/// <c>GET /api/catalog/source-selection-policies/preview</c> sobre o
/// <c>DashboardHarness</c> real: gate de sessão, resposta JSON read-only,
/// sanitização de credenciais e rejeição de métodos não-GET. Partilha a
/// colecção <c>DashboardStaticState</c> porque muta o estado estático do
/// dashboard.
/// </summary>
[Collection("DashboardStaticState")]
public class SourceSelectionPreviewEndpointTests : IAsyncLifetime
{
    private const string ValidPassword = "a-very-strong-password";
    private const string PreviewEndpoint = "/api/catalog/source-selection-policies/preview";
    private const string CredentialUrl = "http://user:secret@preview-dash.example.test/live/USER/PASS/1";

    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _outputDir;
    private readonly string _storePath;

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;
    private PlaylistComposerService _composer = null!;
    private ImportHistoryService _history = null!;
    private ConfigurationLifecycleService _lifecycle = null!;
    private AuthService _auth = null!;
    private BootstrapService _bootstrap = null!;
    private DashboardBootstrapEndpointTests.DashboardHarness? _harness;

    public SourceSelectionPreviewEndpointTests()
    {
        _root = TestTempDb.SuitePath($"source-selection-preview-dash-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _outputDir = Path.Combine(_root, "output");
        _storePath = Path.Combine(_root, ConfigurationLifecycleStore.FileName);
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
        if (_harness != null)
        {
            await _harness.DisposeAsync();
        }
        WebDashboardService.SetAuth(null, null);
        WebDashboardService.SetConfigurationLifecycle(null);
        TestTempDb.Cleanup(_dbPath);
        TestTempDb.Cleanup(_storePath);
        TestTempDb.CleanupDirectory(_root);
    }

    private DashboardBootstrapEndpointTests.DashboardHarness StartHarness()
    {
        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history,
            _lifecycle, _auth, _bootstrap, webToken: null);
        return _harness;
    }

    /// <summary>
    /// Arranca o dashboard sem <c>CatalogResolver</c> (equivalente a falha de
    /// inicialização do catálogo no arranque). O gate de bootstrap/login não
    /// depende do catálogo, pelo que o fluxo até à sessão autenticada
    /// continua a funcionar.
    /// </summary>
    private DashboardBootstrapEndpointTests.DashboardHarness StartHarnessWithoutCatalog()
    {
        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            outputDir: _outputDir,
            resolver: null!,
            composer: _composer,
            history: _history,
            lifecycle: _lifecycle,
            auth: _auth,
            bootstrap: _bootstrap,
            webToken: null);
        return _harness;
    }

    private string[] OutputDirSnapshot()
        => Directory.Exists(_outputDir)
            ? Directory.GetFileSystemEntries(_outputDir)
                .Select(p => Path.GetFileName(p)!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();

    private static async Task ReachReadyAsync(DashboardBootstrapEndpointTests.DashboardHarness harness)
    {
        var start = await harness.Client.PostAsync("/api/bootstrap/start", EmptyJson());
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);

        var admin = await harness.Client.PostAsync(
            "/api/bootstrap/admin",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);

        var complete = await harness.Client.PostAsync("/api/bootstrap/complete", EmptyJson());
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
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
        var csrf = doc.RootElement.GetProperty("csrfToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(csrf));
        return csrf!;
    }

    private static StringContent EmptyJson()
        => new("{}", Encoding.UTF8, "application/json");

    private static HttpRequestMessage PostJson(string path, string body, string? csrf = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (csrf != null)
        {
            request.Headers.Add("X-CSRF-Token", csrf);
        }
        return request;
    }

    private async Task SeedChannelSourceAsync(string url)
    {
        var source = await _resolver.EnsureSourceAsync(
            "preview-dash", "preview-dash", SourceKind.Telegram, "telegram://preview-dash", 0);
        var channels = await _resolver.ListCanonicalChannelsAsync();
        await _resolver.RecordChannelSourceAsync(
            channels[0].Id, source.Id, url, matchMethod: "test");
    }

    /// <summary>
    /// Insere um <see cref="ChannelSourceEntity"/> directamente via
    /// <see cref="ChannelCatalogDbContext"/>, contornando o
    /// <c>CatalogResolver</c> (que sanitiza sempre a URL). Uma URL com
    /// credenciais cruas nunca coincide com a chave sanitizada da stream
    /// sintetizada pelo preview → prova o caminho <c>unmatched</c> real.
    /// </summary>
    private async Task SeedRawChannelSourceAsync(long canonicalChannelId, long sourceId, string rawUrl)
    {
        await using var context = _factory.CreateDbContext();
        var now = DateTime.UtcNow;
        context.ChannelSources.Add(new ChannelSourceEntity
        {
            CanonicalChannelId = canonicalChannelId,
            SourceId = sourceId,
            StreamUrl = rawUrl,
            Availability = AvailabilityState.Discovered,
            IsEnabled = true,
            MatchMethod = "test-raw",
            MatchConfidence = 0,
            FirstSeenAtUtc = now,
            LastSeenAtUtc = now,
            LastTestedAtUtc = now,
            LastResponseTimeMs = 0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        await context.SaveChangesAsync();
    }

    private async Task<int> PolicyRowCountAsync()
    {
        await using var context = _factory.CreateDbContext();
        return await context.SourceSelectionPolicies.CountAsync();
    }

    [Fact]
    public async Task Preview_without_session_is_unauthorized()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);

        var response = await harness.Client.GetAsync(PreviewEndpoint);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_preview_returns_json_and_does_not_create_a_global_policy_row()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        // Semear depois do bootstrap: um catálogo já povoado antes do arranque
        // seria interpretado como evidência legacy e adoptaria READY.
        await SeedChannelSourceAsync("http://preview-ok.example.test/1.ts");

        var outputBefore = OutputDirSnapshot();

        var response = await harness.Client.GetAsync(PreviewEndpoint);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("applied", out var applied));
        Assert.True(applied.GetBoolean());
        Assert.True(root.TryGetProperty("metrics", out _));
        Assert.True(root.TryGetProperty("channels", out var channels));
        Assert.True(channels.GetArrayLength() >= 1);
        Assert.True(root.TryGetProperty("unmatched", out var unmatched));
        Assert.Equal(0, unmatched.GetArrayLength());
        Assert.True(root.TryGetProperty("ambiguous", out var ambiguous));
        Assert.Equal(0, ambiguous.GetArrayLength());

        Assert.Equal(0, await PolicyRowCountAsync());

        // O preview é read-only também ao nível do filesystem: nenhum
        // ficheiro novo (playlist/report) é criado no output do dashboard.
        var outputAfter = OutputDirSnapshot();
        Assert.Equal(outputBefore, outputAfter);
        Assert.False(File.Exists(Path.Combine(_outputDir, "playlist.m3u")));
        Assert.False(File.Exists(Path.Combine(_outputDir, "playlist_temp.m3u")));
        Assert.False(File.Exists(Path.Combine(_outputDir, "telegram_run_report.json")));
        Assert.False(File.Exists(Path.Combine(_outputDir, "telegram_maintain_report.json")));
    }

    [Fact]
    public async Task Preview_unknown_channel_key_returns_not_applied_with_zeroed_metrics()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);
        await SeedChannelSourceAsync("http://preview-unknown.example.test/1.ts");

        var response = await harness.Client.GetAsync(PreviewEndpoint + "?channelKey=does-not-exist");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.False(root.GetProperty("applied").GetBoolean());
        Assert.Equal(0, root.GetProperty("inputStreamCount").GetInt32());
        Assert.Equal(0, root.GetProperty("channels").GetArrayLength());
        Assert.Equal(0, root.GetProperty("unmatched").GetArrayLength());
        Assert.Equal(0, root.GetProperty("ambiguous").GetArrayLength());

        var source = root.GetProperty("source");
        Assert.Equal("does-not-exist", source.GetProperty("channelKeyFilter").GetString());

        // Todos os escalares de metrics a zero e colecções vazias.
        var metrics = root.GetProperty("metrics");
        foreach (var property in metrics.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number)
            {
                Assert.Equal(0, property.Value.GetInt32());
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                Assert.Equal(0, property.Value.GetArrayLength());
            }
            else if (property.Value.ValueKind == JsonValueKind.Object)
            {
                Assert.Empty(property.Value.EnumerateObject());
            }
        }

        // Contrato Wave 13-5: chaves novas presentes e a antiga ausente.
        Assert.True(metrics.TryGetProperty("diversitySelectionCount", out _));
        Assert.True(metrics.TryGetProperty("distinctProviderCount", out _));
        Assert.True(metrics.TryGetProperty("totalUnmatchedStreamCount", out _));
        Assert.False(metrics.TryGetProperty("distinctProviderSelectionCount", out _));
    }

    [Fact]
    public async Task Preview_sanitizes_credential_shaped_channel_key_filter()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        const string rawKey = "http://user:secret@filter.example.test/live/USER/PASS/1";
        var response = await harness.Client.GetAsync(
            PreviewEndpoint + "?channelKey=" + Uri.EscapeDataString(rawKey));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PASS", body, StringComparison.Ordinal);
        Assert.DoesNotContain("USER", body, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("applied").GetBoolean());
        var echoed = root.GetProperty("source").GetProperty("channelKeyFilter").GetString();
        Assert.False(string.IsNullOrEmpty(echoed));
        Assert.Equal(CredentialSanitizer.SanitizeText(rawKey), echoed);
        Assert.Contains("***", echoed);
    }

    [Fact]
    public async Task Preview_without_catalog_resolver_returns_503()
    {
        var harness = StartHarnessWithoutCatalog();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        var response = await harness.Client.GetAsync(PreviewEndpoint);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Catálogo não inicializado.", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Authenticated_preview_response_is_sanitized()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        await SeedChannelSourceAsync(CredentialUrl);

        var response = await harness.Client.GetAsync(PreviewEndpoint);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PASS", body, StringComparison.Ordinal);
        Assert.DoesNotContain("USER", body, StringComparison.Ordinal);
        Assert.Contains("***", body);
    }

    [Fact]
    public async Task Preview_rejects_non_get_methods_with_405()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await harness.Client.SendAsync(
            PostJson(PreviewEndpoint, "{}", csrf));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Preview_serializes_non_empty_arrays_with_camel_case()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        var channels = await _resolver.ListCanonicalChannelsAsync();
        Assert.True(channels.Count >= 2);

        var source = await _resolver.EnsureSourceAsync(
            "preview-shape", "preview-shape", SourceKind.Telegram, "telegram://preview-shape", 0);

        // channel[0]: 2 linhas regulares; limite global 1 → 1 seleccionada,
        // 1 rejeitada. Hosts diferentes para haver dois fornecedores distintos.
        await _resolver.RecordChannelSourceAsync(
            channels[0].Id, source.Id, "http://shape-selected.example.test/1.ts", matchMethod: "test");
        await _resolver.RecordChannelSourceAsync(
            channels[0].Id, source.Id, "http://shape-rejected.example.test/1.ts", matchMethod: "test");

        // URL ambígua: a mesma URL sanitizada em dois canais canónicos.
        const string shared = "http://shape-ambiguous.example.test/1.ts";
        await _resolver.RecordChannelSourceAsync(
            channels[0].Id, source.Id, shared, matchMethod: "test");
        await _resolver.RecordChannelSourceAsync(
            channels[1].Id, source.Id, shared, matchMethod: "test");

        // Linha raw sem hit → unmatched.
        await SeedRawChannelSourceAsync(
            channels[0].Id, source.Id,
            "http://user:secret@shape-raw.example.test/live/USER/PASS/1");

        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(1, true, null, true);

        var response = await harness.Client.GetAsync(PreviewEndpoint);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("applied").GetBoolean());
        Assert.Equal("applied", root.GetProperty("status").GetString());

        var channelsJson = root.GetProperty("channels");
        Assert.True(channelsJson.GetArrayLength() >= 1);
        var matchedChannel = channelsJson.EnumerateArray()
            .Single(c => c.GetProperty("canonicalChannelId").GetInt64() == channels[0].Id);

        var selected = matchedChannel.GetProperty("selected");
        Assert.True(selected.GetArrayLength() >= 1);
        AssertCandidateShape(selected[0], expectedDecision: "selected");

        var rejected = matchedChannel.GetProperty("rejected");
        Assert.True(rejected.GetArrayLength() >= 1);
        AssertCandidateShape(rejected[0], expectedDecision: "rejected");

        var unmatched = root.GetProperty("unmatched");
        Assert.True(unmatched.GetArrayLength() >= 1);
        AssertUnmatchedShape(unmatched[0], expectedReason: "unmatched");

        var ambiguous = root.GetProperty("ambiguous");
        Assert.True(ambiguous.GetArrayLength() >= 1);
        AssertUnmatchedShape(ambiguous[0], expectedReason: "ambiguous");

        // camelCase: as chaves novas estão presentes e não há fugas PascalCase.
        Assert.True(root.TryGetProperty("inputStreamCount", out _));
        Assert.True(root.TryGetProperty("status", out _));
        Assert.True(root.GetProperty("metrics").TryGetProperty("totalUnmatchedStreamCount", out _));
        Assert.DoesNotContain("\"StreamUrlSanitized\"", body);
        Assert.DoesNotContain("\"TotalUnmatchedStreamCount\"", body);
        Assert.DoesNotContain("\"CanonicalChannelId\"", body);
        Assert.DoesNotContain("\"InputStreamCount\"", body);
        Assert.DoesNotContain("\"SelectedCount\"", body);
        Assert.DoesNotContain("\"GeneratedAtUtc\"", body);
    }

    [Fact]
    public async Task Preview_returns_500_preview_failed_when_selection_throws()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        // Falha determinística da leitura do preview: apaga a primeira tabela
        // que o serviço lê. Sem isto o endpoint devolveria 200 com o catálogo
        // normal. Limpa o pool para que a alteração de schema seja visível.
        SqliteConnection.ClearAllPools();
        await using (var context = _factory.CreateDbContext())
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE canonical_channels;");
        }
        SqliteConnection.ClearAllPools();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var response = await harness.Client.GetAsync(PreviewEndpoint, cts.Token);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("preview-failed", doc.RootElement.GetProperty("error").GetString());
    }

    private static void AssertCandidateShape(JsonElement candidate, string expectedDecision)
    {
        Assert.True(candidate.TryGetProperty("streamUrlSanitized", out var url));
        Assert.Equal(JsonValueKind.String, url.ValueKind);
        Assert.True(candidate.TryGetProperty("provider", out var provider));
        Assert.Equal(JsonValueKind.String, provider.ValueKind);
        Assert.True(candidate.TryGetProperty("rank", out _));
        Assert.True(candidate.TryGetProperty("decision", out var decision));
        Assert.Equal(expectedDecision, decision.GetString());
        Assert.True(candidate.TryGetProperty("reason", out var reason));
        Assert.Equal(JsonValueKind.String, reason.ValueKind);
        Assert.True(candidate.TryGetProperty("quality", out var quality));
        Assert.Equal(JsonValueKind.String, quality.ValueKind);
        Assert.True(candidate.TryGetProperty("availability", out var availability));
        Assert.Equal(JsonValueKind.String, availability.ValueKind);
    }

    private static void AssertUnmatchedShape(JsonElement entry, string expectedReason)
    {
        Assert.True(entry.TryGetProperty("streamUrlSanitized", out var url));
        Assert.Equal(JsonValueKind.String, url.ValueKind);
        Assert.True(entry.TryGetProperty("title", out var title));
        Assert.Equal(JsonValueKind.String, title.ValueKind);
        Assert.True(entry.TryGetProperty("reason", out var reason));
        Assert.Equal(expectedReason, reason.GetString());
    }
}
