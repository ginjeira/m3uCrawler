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
/// W5.5 — API HTTP de Review (DL-120; `22-API-CTRACTS` §7): as cinco novas rotas
/// <c>GET /api/reviews</c>, <c>GET /api/review?id</c>,
/// <c>POST /api/review/{resolve|ignore|reopen}</c>. Cobre auth/CSRF, contrato de
/// erro, filtro/paginação, identidade <c>ReviewItem.Id</c>, integração com o
/// lifecycle W5.4, idempotência, audit e os change types suportados.
/// </summary>
[Collection("DashboardStaticState")]
public class WaveW55ReviewApiTests : IAsyncLifetime
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

    public WaveW55ReviewApiTests()
    {
        _root = TestTempDb.SuitePath($"w55-review-api-{Guid.NewGuid():N}");
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

    // ────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────

    private DashboardBootstrapEndpointTests.DashboardHarness StartHarness()
        => StartHarnessWithResolver(_resolver);

    private DashboardBootstrapEndpointTests.DashboardHarness StartHarnessWithResolver(CatalogResolver resolver)
    {
        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, resolver, _composer, _history, _lifecycle, _auth, _bootstrap,
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

    private async Task<List<AuditRecordEntity>> ReadAuditRowsAsync()
    {
        await using var context = _factory.CreateDbContext();
        return await context.AuditRecords.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static void AssertErrorContract(JsonElement root)
    {
        Assert.Equal(JsonValueKind.String, root.GetProperty("error").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("message").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("correlationId").GetString()));
        // Nunca expor excepções internas.
        var message = root.GetProperty("message").GetString()!;
        Assert.DoesNotContain("Exception", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", message);
    }

    // ════════════════════════════════════════════════════════════════
    // GET /api/reviews
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task List_returns_summaries_with_contract_fields()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        var item = await OpenReviewAsync("w55-list-obs");

        var response = await harness.Client.GetAsync("/api/reviews");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = await ReadJsonAsync(response);
        var row = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal(item.Id, row.GetProperty("id").GetInt64());
        Assert.Equal("w55-list-obs", row.GetProperty("subject").GetString());
        Assert.Equal("Open", row.GetProperty("state").GetString());
        Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("createdAt").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("updatedAt").GetString()));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("runId").ValueKind);
    }

    [Fact]
    public async Task List_filters_by_state()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        _ = await LoginAsync(harness);

        var open = await OpenReviewAsync("w55-filter-open");
        var ignored = await OpenReviewAsync("w55-filter-ignored");
        await _resolver.IgnoreReviewAsync(ignored.Fingerprint, "motivo");

        var response = await harness.Client.GetAsync("/api/reviews?state=Ignored");
        using var doc = await ReadJsonAsync(response);
        var row = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal(ignored.Id, row.GetProperty("id").GetInt64());
        Assert.DoesNotContain(open.Id, doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("id").GetInt64()));
    }

    [Fact]
    public async Task List_invalid_state_returns_400_invalid_filter()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        _ = await LoginAsync(harness);

        var response = await harness.Client.GetAsync("/api/reviews?state=Bogus");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("invalid-filter", doc.RootElement.GetProperty("error").GetString());
        AssertErrorContract(doc.RootElement);
    }

    [Fact]
    public async Task List_pagination_and_deterministic_ordering()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        _ = await LoginAsync(harness);

        var a = await OpenReviewAsync("w55-page-a");
        var b = await OpenReviewAsync("w55-page-b");
        var c = await OpenReviewAsync("w55-page-c");

        var response = await harness.Client.GetAsync("/api/reviews?limit=1&offset=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        var row = Assert.Single(doc.RootElement.EnumerateArray());
        // Ordenação CreatedAtUtc DESC, Id DESC → o primeiro offset=1 é o intermédio.
        Assert.Equal(b.Id, row.GetProperty("id").GetInt64());
        Assert.NotEqual(a.Id, row.GetProperty("id").GetInt64());
        Assert.NotEqual(c.Id, row.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task List_requires_authentication()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        // Sem login.

        var response = await harness.Client.GetAsync("/api/reviews");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ════════════════════════════════════════════════════════════════
    // GET /api/review?id
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Detail_returns_item_by_id()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        _ = await LoginAsync(harness);
        var item = await OpenReviewAsync("w55-detail-obs");

        var response = await harness.Client.GetAsync($"/api/review?id={item.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = await ReadJsonAsync(response);
        Assert.Equal(item.Id, doc.RootElement.GetProperty("id").GetInt64());
        Assert.Equal("w55-detail-obs", doc.RootElement.GetProperty("subject").GetString());
        Assert.Equal("Open", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal("sig", doc.RootElement.GetProperty("reason").GetString());
        Assert.Equal("observação inicial", doc.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Detail_missing_id_returns_400_review_id_required()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        _ = await LoginAsync(harness);

        var response = await harness.Client.GetAsync("/api/review");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("review-id-required", doc.RootElement.GetProperty("error").GetString());
        AssertErrorContract(doc.RootElement);
    }

    [Fact]
    public async Task Detail_unknown_id_returns_404_review_not_found()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        _ = await LoginAsync(harness);

        var response = await harness.Client.GetAsync("/api/review?id=999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("review-not-found", doc.RootElement.GetProperty("error").GetString());
        AssertErrorContract(doc.RootElement);
    }

    [Fact]
    public async Task Detail_requires_authentication()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);

        var response = await harness.Client.GetAsync("/api/review?id=1");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ════════════════════════════════════════════════════════════════
    // POST /api/review/ignore
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Ignore_open_to_ignored_and_audited()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        var item = await OpenReviewAsync("w55-ignore-open");

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/review/ignore",
            new { reviewItemId = item.Id, reason = "duplicado" }, csrf));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var doc = await ReadJsonAsync(response))
        {
            Assert.Equal("Ignored", doc.RootElement.GetProperty("state").GetString());
            Assert.True(doc.RootElement.GetProperty("changed").GetBoolean());
        }

        await using (var ctx = _factory.CreateDbContext())
        {
            var stored = await ctx.ReviewItems.AsNoTracking().SingleAsync(r => r.Id == item.Id);
            Assert.Equal(ReviewItemState.Ignored, stored.State);
            Assert.Equal("duplicado", stored.Note);
        }

        var rows = await ReadAuditRowsAsync();
        var row = Assert.Single(rows, r => r.Operation == "catalog.review.ignore");
        Assert.Equal("review-item", row.ObjectType);
        Assert.Contains("\"state\":\"Open\"", row.BeforeJson);
        Assert.Contains("\"state\":\"Ignored\"", row.AfterJson);
    }

    [Fact]
    public async Task Ignore_inreview_to_ignored()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        var item = await OpenReviewAsync("w55-ignore-inreview");
        await _resolver.BeginReviewAsync(item.Fingerprint);

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/review/ignore",
            new { reviewItemId = item.Id, reason = "motivo" }, csrf));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("Ignored", doc.RootElement.GetProperty("state").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Ignore_missing_reason_returns_400_reason_required(string? reason)
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        var item = await OpenReviewAsync($"w55-ignore-reason-{reason?.Length ?? -1}");

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/review/ignore",
            new { reviewItemId = item.Id, reason }, csrf));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("reason-required", doc.RootElement.GetProperty("error").GetString());
        AssertErrorContract(doc.RootElement);
    }

    [Fact]
    public async Task Ignore_resolved_returns_409_state_conflict()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        var item = await OpenReviewAsync("w55-ignore-conflict");
        await _resolver.BeginReviewAsync(item.Fingerprint);
        await _resolver.ResolveReviewAsync(item.Fingerprint, null);

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/review/ignore",
            new { reviewItemId = item.Id, reason = "motivo" }, csrf));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("state-conflict", doc.RootElement.GetProperty("error").GetString());
        AssertErrorContract(doc.RootElement);
    }

    [Fact]
    public async Task Ignore_is_idempotent()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        var item = await OpenReviewAsync("w55-ignore-idempotent");

        await harness.Client.SendAsync(Json(HttpMethod.Post, "/api/review/ignore",
            new { reviewItemId = item.Id, reason = "motivo" }, csrf));
        var second = await harness.Client.SendAsync(Json(HttpMethod.Post, "/api/review/ignore",
            new { reviewItemId = item.Id, reason = "motivo" }, csrf));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var doc = await ReadJsonAsync(second);
        Assert.False(doc.RootElement.GetProperty("changed").GetBoolean());
    }

    [Fact]
    public async Task Ignore_requires_csrf()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        _ = await LoginAsync(harness);
        var item = await OpenReviewAsync("w55-ignore-csrf");

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/review/ignore",
            new { reviewItemId = item.Id, reason = "motivo" }, csrf: null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("csrf-invalid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ignore_requires_authentication()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var item = await OpenReviewAsync("w55-ignore-auth");

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/review/ignore",
            new { reviewItemId = item.Id, reason = "motivo" }, csrf: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ════════════════════════════════════════════════════════════════
    // POST /api/review/reopen
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Reopen_resolved_to_open()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        var item = await OpenReviewAsync("w55-reopen-resolved");
        await _resolver.BeginReviewAsync(item.Fingerprint);
        await _resolver.ResolveReviewAsync(item.Fingerprint, null);

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/review/reopen",
            new { reviewItemId = item.Id, justification = "nova evidência" }, csrf));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("Open", doc.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Reopen_ignored_to_open()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        var item = await OpenReviewAsync("w55-reopen-ignored");
        await _resolver.IgnoreReviewAsync(item.Fingerprint, "motivo");

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/review/reopen",
            new { reviewItemId = item.Id, justification = "nova evidência" }, csrf));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("Open", doc.RootElement.GetProperty("state").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Reopen_missing_justification_returns_400(string? justification)
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        var item = await OpenReviewAsync($"w55-reopen-just-{justification?.Length ?? -1}");
        await _resolver.BeginReviewAsync(item.Fingerprint);
        await _resolver.ResolveReviewAsync(item.Fingerprint, null);

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/review/reopen",
            new { reviewItemId = item.Id, justification }, csrf));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("reason-required", doc.RootElement.GetProperty("error").GetString());
        AssertErrorContract(doc.RootElement);
    }

    [Fact]
    public async Task Reopen_incompatible_state_returns_409()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        var item = await OpenReviewAsync("w55-reopen-conflict");
        await _resolver.BeginReviewAsync(item.Fingerprint); // InReview → não reabrível

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/review/reopen",
            new { reviewItemId = item.Id, justification = "evidência" }, csrf));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("state-conflict", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Reopen_requires_csrf()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        _ = await LoginAsync(harness);
        var item = await OpenReviewAsync("w55-reopen-csrf");

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/review/reopen",
            new { reviewItemId = item.Id, justification = "evidência" }, csrf: null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ════════════════════════════════════════════════════════════════
    // POST /api/review/resolve
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Resolve_channelAlias_adds_alias_and_resolves()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        var channel = await NewChannelAsync("w55-resolve-target");
        var item = await OpenReviewAsync("w55-resolve-alias-obs");

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/review/resolve",
            new
            {
                reviewItemId = item.Id,
                change = new { type = "channelAlias", canonicalChannelKey = "w55-resolve-target" },
                note = "aprovado pelo operador",
            }, csrf));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var doc = await ReadJsonAsync(response))
        {
            Assert.Equal("Resolved", doc.RootElement.GetProperty("state").GetString());
            Assert.Equal("channelAlias", doc.RootElement.GetProperty("change").GetString());
            Assert.True(doc.RootElement.GetProperty("catalogueChanged").GetBoolean());
            Assert.Equal(channel.Id, doc.RootElement.GetProperty("canonicalChannelId").GetInt64());
        }

        await using (var ctx = _factory.CreateDbContext())
        {
            var alias = await ctx.ChannelAliases.AsNoTracking()
                .SingleAsync(a => a.CanonicalChannelId == channel.Id);
            Assert.Equal(ChannelNormalizer.Normalize("w55-resolve-alias-obs"), alias.NormalizedAlias);
            var stored = await ctx.ReviewItems.AsNoTracking().SingleAsync(r => r.Id == item.Id);
            Assert.Equal(ReviewItemState.Resolved, stored.State);
        }

        var row = Assert.Single(await ReadAuditRowsAsync(), r => r.Operation == "catalog.review.resolve");
        Assert.Contains("\"state\":\"Open\"", row.BeforeJson);
        Assert.Contains("\"state\":\"Resolved\"", row.AfterJson);
        Assert.Contains("aprovado pelo operador", row.Detail);
    }

    [Fact]
    public async Task Resolve_canonicalChannel_creates_channel_and_resolves()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        var item = await OpenReviewAsync("w55-resolve-create-obs");

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/review/resolve",
            new
            {
                reviewItemId = item.Id,
                change = new
                {
                    type = "canonicalChannel",
                    channel = new { key = "w55-new-key", name = "W55 New Channel" },
                    alias = "w55-resolve-create-obs",
                },
            }, csrf));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("Resolved", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal("canonicalChannel", doc.RootElement.GetProperty("change").GetString());
        Assert.Equal("w55-new-key", doc.RootElement.GetProperty("canonicalChannelKey").GetString());
    }

    [Fact]
    public async Task Resolve_inreview_to_resolved()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        await NewChannelAsync("w55-resolve-inreview");
        var item = await OpenReviewAsync("w55-resolve-inreview-obs");
        await _resolver.BeginReviewAsync(item.Fingerprint);

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/review/resolve",
            new
            {
                reviewItemId = item.Id,
                change = new { type = "channelAlias", canonicalChannelKey = "w55-resolve-inreview" },
            }, csrf));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("Resolved", doc.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Resolve_missing_declaration_field_returns_422()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        var item = await OpenReviewAsync("w55-resolve-422");

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/review/resolve",
            new { reviewItemId = item.Id, change = new { type = "channelAlias" } }, csrf));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("declared-change-invalid", doc.RootElement.GetProperty("error").GetString());
        AssertErrorContract(doc.RootElement);
    }

    [Fact]
    public async Task Resolve_unknown_canonical_channel_returns_422()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        var item = await OpenReviewAsync("w55-resolve-unknown-channel");

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/review/resolve",
            new
            {
                reviewItemId = item.Id,
                change = new { type = "channelAlias", canonicalChannelKey = "does-not-exist" },
            }, csrf));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("declared-change-invalid", doc.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("externalIdentity")]
    [InlineData("channelSource")]
    [InlineData("bogus")]
    public async Task Resolve_unsupported_change_types_return_422(string type)
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        var item = await OpenReviewAsync($"w55-resolve-unsupported-{type}");

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/review/resolve",
            new { reviewItemId = item.Id, change = new { type } }, csrf));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("declared-change-invalid", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Resolve_same_declaration_is_idempotent()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        await NewChannelAsync("w55-resolve-idem");
        var item = await OpenReviewAsync("w55-resolve-idem-obs");

        var body = new
        {
            reviewItemId = item.Id,
            change = new { type = "channelAlias", canonicalChannelKey = "w55-resolve-idem" },
        };
        var first = await harness.Client.SendAsync(Json(HttpMethod.Post, "/api/review/resolve", body, csrf));
        var second = await harness.Client.SendAsync(Json(HttpMethod.Post, "/api/review/resolve", body, csrf));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var doc = await ReadJsonAsync(second);
        Assert.True(doc.RootElement.GetProperty("idempotent").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("catalogueChanged").GetBoolean());
    }

    [Fact]
    public async Task Resolve_ignored_item_returns_409()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);
        await NewChannelAsync("w55-resolve-ignored");
        var item = await OpenReviewAsync("w55-resolve-ignored-obs");
        await _resolver.IgnoreReviewAsync(item.Fingerprint, "motivo");

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/review/resolve",
            new
            {
                reviewItemId = item.Id,
                change = new { type = "channelAlias", canonicalChannelKey = "w55-resolve-ignored" },
            }, csrf));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("state-conflict", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Resolve_requires_csrf()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        _ = await LoginAsync(harness);
        var item = await OpenReviewAsync("w55-resolve-csrf");

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/review/resolve",
            new { reviewItemId = item.Id, change = new { type = "channelAlias", canonicalChannelKey = "x" } },
            csrf: null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ════════════════════════════════════════════════════════════════
    // Sanitização
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Responses_do_not_expose_credentials()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        _ = await LoginAsync(harness);
        // Identidade com forma de URL com credenciais; não deve sair em claro.
        var item = await _resolver.UpsertReviewItemAsync(
            "http://user:sup3rsecret@example.test/live.m3u8", "grp", "sig",
            "ver http://user:sup3rsecret@example.test/live.m3u8");

        var list = await harness.Client.GetStringAsync("/api/reviews");
        var detail = await harness.Client.GetStringAsync($"/api/review?id={item.Id}");

        Assert.DoesNotContain("sup3rsecret", list);
        Assert.DoesNotContain("sup3rsecret", detail);
    }

    // ════════════════════════════════════════════════════════════════
    // B1 — 500 persistence-error (excepções inesperadas)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Unexpected_exception_returns_500_persistence_error()
    {
        // Resolver de catálogo "quebrado" (DB num directório inexistente) para
        // provocar uma excepção inesperada determinística no handler da Review API.
        var brokenPath = Path.Combine(_root, "missing-directory", "broken.db");
        var brokenResolver = new CatalogResolver(new TestDbContextFactory(brokenPath), brokenPath);
        var harness = StartHarnessWithResolver(brokenResolver);
        await ReachReadyAsync(harness);
        _ = await LoginAsync(harness);

        var response = await harness.Client.GetAsync("/api/reviews");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("persistence-error", doc.RootElement.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("message").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("correlationId").GetString()));
        AssertErrorContract(doc.RootElement);
        Assert.DoesNotContain("Sqlite", body);
        Assert.DoesNotContain("   at ", body);
    }

    // ════════════════════════════════════════════════════════════════
    // B2 — envelope 401/403 nas Review APIs
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Unauthenticated_review_api_uses_error_envelope()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        // Sem login.

        var response = await harness.Client.GetAsync("/api/reviews");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("authentication-required", doc.RootElement.GetProperty("error").GetString());
        AssertErrorContract(doc.RootElement);
    }

    [Fact]
    public async Task Csrf_error_on_review_api_uses_error_envelope()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        _ = await LoginAsync(harness);
        var item = await OpenReviewAsync("w55-b2-csrf");

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, "/api/review/ignore",
            new { reviewItemId = item.Id, reason = "motivo" }, csrf: null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("csrf-invalid", doc.RootElement.GetProperty("error").GetString());
        AssertErrorContract(doc.RootElement);
    }

    [Fact]
    public async Task Legacy_gate_error_format_is_unchanged()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        // Endpoint não-Review (legacy) sem sessão: mantém o corpo legado {error}.
        var response = await harness.Client.GetAsync("/api/catalog/reviews");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.Equal("authentication-required", doc.RootElement.GetProperty("error").GetString());
        Assert.False(doc.RootElement.TryGetProperty("message", out _));
        Assert.False(doc.RootElement.TryGetProperty("correlationId", out _));
    }
}
