using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Auth;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// DC-3 — Sanitização no boundary de saída (defense-in-depth). As URLs de
/// stream (e o <c>baseUrl</c> do Dispatcharr) podem chegar às projecções JSON
/// já sanitizadas em persistência, mas linhas legacy/direct-DB podem conter
/// credenciais cruas. Estes testes semeiam uma URL Xtream com credenciais
/// directamente na BD (contornando o <see cref="CatalogResolver"/>, que
/// sanitiza) e verificam que a resposta HTTP devolve a forma mascarada.
/// </summary>
[Collection("DashboardStaticState")]
public class DashboardOutputSanitizationTests : IAsyncLifetime
{
    private const string ValidPassword = "a-very-strong-password";
    private const string RawUrl = "http://user:sup3rsecret@dash-sanitize.example.test/live/USER/PASS/1";

    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _outputDir;
    private readonly string _storePath;
    private readonly string _wtelegramPath;

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;
    private PlaylistComposerService _composer = null!;
    private ImportHistoryService _history = null!;
    private ConfigurationLifecycleService _lifecycle = null!;
    private AuthService _auth = null!;
    private BootstrapService _bootstrap = null!;
    private WtelegramConfigStore _wtelegramStore = null!;
    private DispatcharrConfigurationService _dispatcharrConfig = null!;
    private DashboardBootstrapEndpointTests.DashboardHarness? _harness;

    public DashboardOutputSanitizationTests()
    {
        _root = TestTempDb.SuitePath($"dash-output-sanitize-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _outputDir = Path.Combine(_root, "output");
        _storePath = Path.Combine(_root, ConfigurationLifecycleStore.FileName);
        _wtelegramPath = Path.Combine(_root, "wtelegram.config");
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

        _wtelegramStore = new WtelegramConfigStore(_wtelegramPath);
        _dispatcharrConfig = new DispatcharrConfigurationService(_wtelegramStore);
    }

    public async Task DisposeAsync()
    {
        if (_harness != null)
        {
            await _harness.DisposeAsync();
        }
        WebDashboardService.SetAuth(null, null);
        WebDashboardService.SetConfigurationLifecycle(null);
        WebDashboardService.SetSetupServices(null, null, null, null);
        TestTempDb.Cleanup(_dbPath);
        TestTempDb.Cleanup(_storePath);
        TestTempDb.Cleanup(_wtelegramPath);
        TestTempDb.CleanupDirectory(_root);
    }

    private DashboardBootstrapEndpointTests.DashboardHarness StartHarness()
    {
        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history,
            _lifecycle, _auth, _bootstrap, webToken: null,
            dispatcharrConfig: _dispatcharrConfig);
        return _harness;
    }

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

    private static void AssertSanitized(string body)
    {
        Assert.DoesNotContain("sup3rsecret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PASS", body, StringComparison.Ordinal);
        Assert.Contains("***", body);
    }

    /// <summary>
    /// Insere um <see cref="ChannelSourceEntity"/> cru (URL com credenciais)
    /// directamente na BD, contornando o <see cref="CatalogResolver"/>.
    /// </summary>
    private async Task<(long SourceId, long ChannelSourceId)> SeedRawChannelSourceAsync()
    {
        var source = await _resolver.EnsureSourceAsync(
            "dash-sanitize", "dash-sanitize", SourceKind.Telegram, "telegram://dash-sanitize", 0);
        var channels = await _resolver.ListCanonicalChannelsAsync();
        var channel = channels[0];

        await using var context = _factory.CreateDbContext();
        var now = DateTime.UtcNow;
        var entity = new ChannelSourceEntity
        {
            CanonicalChannelId = channel.Id,
            SourceId = source.Id,
            StreamUrl = RawUrl,
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
        };
        context.ChannelSources.Add(entity);
        await context.SaveChangesAsync();
        return (source.Id, entity.Id);
    }

    [Fact]
    public async Task Channel_sources_endpoint_sanitizes_credential_stream_url()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);
        var seeded = await SeedRawChannelSourceAsync();

        var response = await harness.Client.GetAsync($"/api/catalog/sources/{seeded.SourceId}/streams");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // A BD guarda a URL crua; a projecção de saída tem de a mascarar.
        var persisted = await _resolver.ListChannelSourcesAsync(sourceId: seeded.SourceId);
        Assert.Contains(RawUrl, persisted.Select(cs => cs.StreamUrl));
        AssertSanitized(body);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(
            CredentialSanitizer.SanitizeUrl(RawUrl),
            doc.RootElement[0].GetProperty("streamUrl").GetString());
    }

    [Fact]
    public async Task Ordering_list_preview_sanitizes_credential_stream_url()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);
        _ = await SeedRawChannelSourceAsync();

        var channels = await _resolver.ListCanonicalChannelsAsync();
        var list = await _resolver.CreateOrderingListAsync(
            $"dash-sanitize-{Guid.NewGuid():N}", "DC3 Sanitize", "pt", null);
        await _resolver.AddOrderingItemAsync(list.Id, channels[0].Id);

        var response = await harness.Client.GetAsync($"/api/catalog/ordering-lists/{list.Id}/preview");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        AssertSanitized(body);
        using var doc = JsonDocument.Parse(body);
        var entry = Assert.Single(doc.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal(
            CredentialSanitizer.SanitizeUrl(RawUrl),
            entry.GetProperty("streamUrl").GetString());
    }

    [Fact]
    public async Task Pending_country_approvals_endpoint_sanitizes_credential_stream_url()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        await using (var context = _factory.CreateDbContext())
        {
            var now = DateTime.UtcNow;
            context.PendingCountryApprovals.Add(new PendingCountryApprovalEntity
            {
                NormalizedIdentity = "dash-sanitize",
                OriginalTitle = "Dash Sanitize",
                CountryCode = "pt",
                StreamUrl = RawUrl,
                SourceGroup = "Portugal",
                ReasonSignature = "weak_country_match",
                State = PendingApprovalState.Open,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
            await context.SaveChangesAsync();
        }

        var response = await harness.Client.GetAsync("/api/catalog/pending-country-approvals");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        AssertSanitized(body);
        using var doc = JsonDocument.Parse(body);
        var row = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal(
            CredentialSanitizer.SanitizeUrl(RawUrl),
            row.GetProperty("streamUrl").GetString());
    }

    [Fact]
    public async Task Review_api_exposes_legacy_field_aliases_sanitized()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        const string credentialIdentity = "http://user:sup3rsecret@review.example.test/live/USER/PASS/1";
        var item = await _resolver.UpsertReviewItemAsync(
            credentialIdentity, "grp", "weak_country_match",
            "ver " + credentialIdentity);

        var list = await harness.Client.GetStringAsync("/api/reviews");
        var detail = await harness.Client.GetStringAsync($"/api/review?id={item.Id}");

        Assert.DoesNotContain("sup3rsecret", list, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sup3rsecret", detail, StringComparison.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(list);
        var row = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal(item.Fingerprint, row.GetProperty("fingerprint").GetString());
        Assert.Equal(
            row.GetProperty("subject").GetString(),
            row.GetProperty("normalizedIdentity").GetString());
        Assert.True(row.TryGetProperty("reasonSignature", out _));
        Assert.True(row.TryGetProperty("createdAt", out _));
        Assert.True(row.TryGetProperty("createdAtUtc", out _));
        Assert.True(row.TryGetProperty("updatedAtUtc", out _));
        Assert.True(row.TryGetProperty("resolvedAtUtc", out _));

        using var detailDoc = JsonDocument.Parse(detail);
        var root = detailDoc.RootElement;
        Assert.Equal(item.Fingerprint, root.GetProperty("fingerprint").GetString());
        Assert.True(root.TryGetProperty("normalizedIdentity", out _));
        Assert.True(root.TryGetProperty("reason", out _));
        Assert.True(root.TryGetProperty("reasonSignature", out _));
        Assert.True(root.TryGetProperty("resolvedAtUtc", out _));
    }

    [Fact]
    public async Task Dispatcharr_config_endpoint_sanitizes_credential_base_url()
    {
        const string rawBaseUrl = "http://user:sup3rsecret@dispatcharr.example.test";
        _dispatcharrConfig.Save(new DispatcharrConfigurationWrite(
            Enabled: true, BaseUrl: rawBaseUrl));

        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        var response = await harness.Client.GetAsync("/api/dispatcharr/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sup3rsecret", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("***", body);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(
            CredentialSanitizer.SanitizeUrl(rawBaseUrl),
            doc.RootElement.GetProperty("baseUrl").GetString());
    }

    [Fact]
    public async Task Dispatcharr_config_masked_base_url_is_not_re_persisted()
    {
        const string rawBaseUrl = "http://user:sup3rsecret@dispatcharr.example.test";
        _dispatcharrConfig.Save(new DispatcharrConfigurationWrite(
            Enabled: true, BaseUrl: rawBaseUrl));

        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        // O formulário reenvia o valor mascarado devolvido pelo GET (round-trip);
        // não pode ser persistido como `***`, sob pena de corromper a config.
        var masked = CredentialSanitizer.SanitizeUrl(rawBaseUrl);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/dispatcharr/config")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { baseUrl = masked }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CSRF-Token", csrf);

        var response = await harness.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(rawBaseUrl, _dispatcharrConfig.Get().BaseUrl);
    }

    [Fact]
    public async Task Dispatcharr_config_edited_masked_base_url_is_rejected()
    {
        const string rawBaseUrl = "http://user:sup3rsecret@dispatcharr.example.test";
        _dispatcharrConfig.Save(new DispatcharrConfigurationWrite(
            Enabled: true, BaseUrl: rawBaseUrl));

        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        // Editar só o host mantendo o userinfo mascarado (`http://user:***@newhost`)
        // não permite reconstruir as credenciais: rejeitar com 400 em vez de
        // ignorar silenciosamente (a edição legítima do host não é perdida).
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/dispatcharr/config")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { baseUrl = "http://user:***@newhost.example.test" }),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CSRF-Token", csrf);

        var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Contains("baseUrl inválido", doc.RootElement.GetProperty("error").GetString());
        }
        // O valor existente permanece intacto.
        Assert.Equal(rawBaseUrl, _dispatcharrConfig.Get().BaseUrl);
    }

    [Fact]
    public async Task Dispatcharr_config_new_base_url_without_mask_is_persisted()
    {
        const string rawBaseUrl = "http://user:sup3rsecret@dispatcharr.example.test";
        _dispatcharrConfig.Save(new DispatcharrConfigurationWrite(
            Enabled: true, BaseUrl: rawBaseUrl));

        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/dispatcharr/config")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { baseUrl = "http://newhost.example.test" }),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CSRF-Token", csrf);

        var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("http://newhost.example.test", _dispatcharrConfig.Get().BaseUrl);
    }

    [Fact]
    public async Task Legacy_catalog_reviews_route_sanitizes_review_texts()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        const string credentialIdentity = "http://user:sup3rsecret@legacy-review.example.test/live/USER/PASS/1";
        var item = await _resolver.UpsertReviewItemAsync(
            credentialIdentity,
            "group http://user:sup3rsecret@group.example.test",
            "weak_country_match",
            "ver " + credentialIdentity);

        var body = await harness.Client.GetStringAsync("/api/catalog/reviews");

        Assert.DoesNotContain("sup3rsecret", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("***", body);
        using var doc = JsonDocument.Parse(body);
        var row = Assert.Single(doc.RootElement.EnumerateArray());
        // `fingerprint` é identidade `sfp1`, não credencial → preservado.
        Assert.Equal(item.Fingerprint, row.GetProperty("fingerprint").GetString());
        Assert.Equal(
            CredentialSanitizer.SanitizeSensitiveText(credentialIdentity),
            row.GetProperty("normalizedIdentity").GetString());
        Assert.Equal(
            CredentialSanitizer.SanitizeSensitiveText("group http://user:sup3rsecret@group.example.test"),
            row.GetProperty("sourceGroup").GetString());
    }

    [Fact]
    public async Task Sources_endpoint_sanitizes_credential_origin()
    {
        const string rawOrigin = "http://user:sup3rsecret@source-origin.example.test/live/USER/PASS/1";

        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        await using (var context = _factory.CreateDbContext())
        {
            var now = DateTime.UtcNow;
            context.Sources.Add(new SourceEntity
            {
                Key = "dash-sanitize-origin",
                Name = "Dash Sanitize Origin",
                Kind = SourceKind.Telegram,
                Origin = rawOrigin,
                Priority = 0,
                IsEnabled = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
            await context.SaveChangesAsync();
        }

        var body = await harness.Client.GetStringAsync("/api/catalog/sources");

        Assert.DoesNotContain("sup3rsecret", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("***", body);
        using var doc = JsonDocument.Parse(body);
        var row = doc.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("key").GetString() == "dash-sanitize-origin");
        Assert.Equal(CredentialSanitizer.SanitizeUrl(rawOrigin), row.GetProperty("origin").GetString());
    }
}
