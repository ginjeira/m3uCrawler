using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Audit;
using m3uCrawler.Services.Auth;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using m3uCrawler.Services.Matching;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W5 — Filtragem da lista de Reviews (legacy e nova) e criação de canal
/// via Review com o spec completo (incl. <c>isEnabled</c>). Afirma estado
/// persistido e listas activas vs histórico, não apenas HTTP 200.
/// </summary>
[Collection("DashboardStaticState")]
public class WaveW5ReviewListAndCreateTests : IAsyncLifetime
{
    private const string ValidPassword = "a-very-strong-password";
    private static readonly DateTime FixedNow = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

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
    private AuditService _audit = null!;
    private DashboardBootstrapEndpointTests.DashboardHarness? _harness;

    public WaveW5ReviewListAndCreateTests()
    {
        _root = TestTempDb.SuitePath($"w5-review-list-{Guid.NewGuid():N}");
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

        _audit = new AuditService(_factory, () => FixedNow);
    }

    public async Task DisposeAsync()
    {
        if (_harness != null)
        {
            await _harness.DisposeAsync();
        }
        WebDashboardService.SetAuth(null, null);
        WebDashboardService.SetConfigurationLifecycle(null);
        WebDashboardService.SetAuditService(null);
        TestTempDb.CleanupDirectory(_root);
    }

    private DashboardBootstrapEndpointTests.DashboardHarness StartHarness()
    {
        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history, _lifecycle, _auth, _bootstrap,
            webToken: null, auditService: _audit);
        return _harness;
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
        var csrf = doc.RootElement.GetProperty("csrfToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(csrf));
        return csrf!;
    }

    private static StringContent EmptyJson() => new("{}", Encoding.UTF8, "application/json");

    private static HttpRequestMessage Json(HttpMethod method, string path, object body, string? csrf)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (csrf != null)
        {
            request.Headers.Add("X-CSRF-Token", csrf);
        }
        return request;
    }

    private Task<ReviewItemEntity> OpenReviewAsync(string identity)
        => _resolver.UpsertReviewItemAsync(identity, "grp", "sig", "observação inicial");

    private Task<CanonicalChannelEntity> NewChannelAsync(string key, params string[] aliases)
        => _resolver.CreateCanonicalChannelAsync(
            key, key, EditorialCategory.Live, CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy.CreateEligible, isEnabled: true, normalizedAliases: aliases.ToList());

    private static async Task<List<long>> ReadIdsAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("id").GetInt64())
            .ToList();
    }

    // ════════════════════════════════════════════════════════════════
    // Legacy GET /api/catalog/reviews
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Legacy_list_default_excludes_terminal_states()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        _ = await LoginAsync(harness);

        var open = await OpenReviewAsync("w5-list-active");
        var ignored = await OpenReviewAsync("w5-list-ignored");
        await _resolver.IgnoreReviewAsync(ignored.Fingerprint, "motivo");
        var resolved = await OpenReviewAsync("w5-list-resolved");
        await _resolver.BeginReviewAsync(resolved.Fingerprint);
        await _resolver.ResolveReviewAsync(resolved.Fingerprint, null);

        var active = await ReadIdsAsync(await harness.Client.GetAsync("/api/catalog/reviews"));
        Assert.Contains(open.Id, active);
        Assert.DoesNotContain(ignored.Id, active);
        Assert.DoesNotContain(resolved.Id, active);
    }

    [Fact]
    public async Task Legacy_list_explicit_state_returns_history()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        _ = await LoginAsync(harness);

        var open = await OpenReviewAsync("w5-hist-active");
        var ignored = await OpenReviewAsync("w5-hist-ignored");
        await _resolver.IgnoreReviewAsync(ignored.Fingerprint, "motivo");

        var resolvedList = await ReadIdsAsync(
            await harness.Client.GetAsync("/api/catalog/reviews?state=Ignored"));
        Assert.Contains(ignored.Id, resolvedList);
        Assert.DoesNotContain(open.Id, resolvedList);

        // includeResolved=true devolve activos + terminais.
        var all = await ReadIdsAsync(
            await harness.Client.GetAsync("/api/catalog/reviews?includeResolved=true"));
        Assert.Contains(open.Id, all);
        Assert.Contains(ignored.Id, all);
    }

    [Fact]
    public async Task Legacy_list_invalid_state_returns_400()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        _ = await LoginAsync(harness);

        var response = await harness.Client.GetAsync("/api/catalog/reviews?state=Bogus");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ════════════════════════════════════════════════════════════════
    // New GET /api/reviews
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task New_list_default_excludes_terminal_states()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        _ = await LoginAsync(harness);

        var open = await OpenReviewAsync("w5-new-list-active");
        var ignored = await OpenReviewAsync("w5-new-list-ignored");
        await _resolver.IgnoreReviewAsync(ignored.Fingerprint, "motivo");
        var resolved = await OpenReviewAsync("w5-new-list-resolved");
        await _resolver.BeginReviewAsync(resolved.Fingerprint);
        await _resolver.ResolveReviewAsync(resolved.Fingerprint, null);

        var active = await ReadIdsAsync(await harness.Client.GetAsync("/api/reviews"));
        Assert.Contains(open.Id, active);
        Assert.DoesNotContain(ignored.Id, active);
        Assert.DoesNotContain(resolved.Id, active);

        var ignoredOnly = await ReadIdsAsync(await harness.Client.GetAsync("/api/reviews?state=Ignored"));
        Assert.Contains(ignored.Id, ignoredOnly);
        Assert.DoesNotContain(open.Id, ignoredOnly);
    }

    // ════════════════════════════════════════════════════════════════
    // Create channel via legacy Review — spec completo
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Create_channel_via_review_persists_isEnabled_and_hides_from_active_list()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var review = await OpenReviewAsync("w5-create-disabled-obs");
        var key = "w5-disabled-channel";

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post,
            $"/api/catalog/reviews/{review.Fingerprint}/approve",
            new
            {
                action = "create-channel",
                channel = new
                {
                    key,
                    name = "W5 Disabled",
                    country = "pt",
                    editorialCategory = "Live",
                    editorialGroup = "PortugalLive",
                    publicationPolicy = "CreateEligible",
                    isEnabled = false,
                },
            },
            csrf));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal("Resolved", doc.RootElement.GetProperty("state").GetString());
            Assert.Equal("create-channel", doc.RootElement.GetProperty("action").GetString());
        }

        long channelId;
        await using (var ctx = _factory.CreateDbContext())
        {
            var channel = await ctx.CanonicalChannels.AsNoTracking().SingleAsync(c => c.Key == key);
            Assert.False(channel.IsEnabled);
            Assert.Equal("W5 Disabled", channel.DisplayName);
            channelId = channel.Id;

            var stored = await ctx.ReviewItems.AsNoTracking().SingleAsync(r => r.Id == review.Id);
            Assert.Equal(ReviewItemState.Resolved, stored.State);
        }

        // O item resolvido desaparece da lista activa mas continua no histórico.
        var active = await ReadIdsAsync(await harness.Client.GetAsync("/api/catalog/reviews"));
        Assert.DoesNotContain(review.Id, active);
        var history = await ReadIdsAsync(await harness.Client.GetAsync("/api/catalog/reviews?state=Resolved"));
        Assert.Contains(review.Id, history);

        // E alimenta exactamente uma Channel affinity do canal criado.
        var groups = (await _resolver.ListAffinityGroupsAsync())
            .Where(g => g.Kind == AffinityKind.Channel).ToList();
        var group = Assert.Single(groups);
        Assert.Equal(channelId, group.CanonicalChannelId);
        Assert.Single(group.Members, m =>
            m.NormalizedMember == ChannelNormalizer.Normalize("w5-create-disabled-obs"));
    }

    [Fact]
    public async Task Create_channel_via_review_duplicate_key_returns_409()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        await NewChannelAsync("w5-dup-key");
        var review = await OpenReviewAsync("w5-dup-key-obs");

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post,
            $"/api/catalog/reviews/{review.Fingerprint}/approve",
            new
            {
                action = "create-channel",
                channel = new { key = "w5-dup-key", name = "Dup" },
            },
            csrf));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("DuplicateKey", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Create_channel_via_review_invalid_enum_returns_400()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var review = await OpenReviewAsync("w5-invalid-enum-obs");

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post,
            $"/api/catalog/reviews/{review.Fingerprint}/approve",
            new
            {
                action = "create-channel",
                channel = new { key = "w5-invalid-enum", name = "Invalid", editorialCategory = "Bogus" },
            },
            csrf));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
