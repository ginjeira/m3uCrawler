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
/// W6b-1 — A aprovação de uma Review declara explicitamente a mudança
/// de catálogo que produz (<c>05-CATALOGUE.md §9</c>) e é auditada
/// (<c>17-SECURITY.md §5</c>). Cobre os três contratos
/// (<c>add-alias</c>, <c>create-channel</c>, <c>exclude</c>),
/// idempotência/conflictos e a preservação da autenticação/CSRF/405.
/// </summary>
[Collection("DashboardStaticState")]
public class ReviewApprovalEndpointTests : IAsyncLifetime
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

    public ReviewApprovalEndpointTests()
    {
        _root = TestTempDb.SuitePath($"review-approval-{Guid.NewGuid():N}");
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
            _outputDir, _resolver, _composer, _history,
            _lifecycle, _auth, _bootstrap, webToken: null,
            auditService: _audit);
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

    private static StringContent EmptyJson()
        => new("{}", Encoding.UTF8, "application/json");

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

    private async Task<ReviewItemEntity> OpenReviewAsync(string identity)
        => await _resolver.UpsertReviewItemAsync(identity, "grp", "sig", "observação inicial");

    private async Task<CanonicalChannelEntity> NewChannelAsync(string key, params string[] aliases)
        => await _resolver.CreateCanonicalChannelAsync(
            key, key, EditorialCategory.Live, CanonicalGroupKeys.PortugalGeneralistas,
            PublicationPolicy.CreateEligible, isEnabled: true,
            normalizedAliases: aliases.ToList());

    private async Task<List<AuditRecordEntity>> ReadAuditRowsAsync()
    {
        await using var context = _factory.CreateDbContext();
        return await context.AuditRecords.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
    }

    private async Task<string> ApproveAsync(
        DashboardBootstrapEndpointTests.DashboardHarness harness,
        string csrf,
        string fingerprint,
        object body)
    {
        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, $"/api/catalog/reviews/{fingerprint}/approve", body, csrf));
        return await response.Content.ReadAsStringAsync();
    }

    // ════════════════════════════════════════════════════════════════
    // 1. add-alias
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Approve_add_alias_adds_normalized_alias_and_records_audit()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var channel = await NewChannelAsync("rev-alias-target");
        var review = await OpenReviewAsync("reviewaliasobs");

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post,
            $"/api/catalog/reviews/{review.Fingerprint}/approve",
            new { action = "add-alias", canonicalChannelKey = "rev-alias-target" },
            csrf));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal("Resolved", doc.RootElement.GetProperty("state").GetString());
            Assert.Equal("add-alias", doc.RootElement.GetProperty("action").GetString());
            Assert.True(doc.RootElement.GetProperty("catalogueChanged").GetBoolean());
            Assert.Equal(channel.Id, doc.RootElement.GetProperty("canonicalChannelId").GetInt64());
            Assert.Equal(ChannelNormalizer.Normalize("reviewaliasobs"),
                doc.RootElement.GetProperty("alias").GetString());
            Assert.NotEqual(JsonValueKind.Null, doc.RootElement.GetProperty("resolvedAtUtc").ValueKind);
        }

        await using (var ctx = _factory.CreateDbContext())
        {
            var alias = await ctx.ChannelAliases
                .SingleAsync(a => a.CanonicalChannelId == channel.Id);
            Assert.Equal(ChannelNormalizer.Normalize("reviewaliasobs"), alias.NormalizedAlias);

            var item = await ctx.ReviewItems.SingleAsync(r => r.Fingerprint == review.Fingerprint);
            Assert.Equal(ReviewItemState.Resolved, item.State);
            Assert.Equal(channel.Id, item.ApprovedCanonicalChannelId);
            Assert.NotNull(item.ResolvedAtUtc);
        }

        var rows = await ReadAuditRowsAsync();
        var row = Assert.Single(rows, r => r.ObjectId == review.Fingerprint
            && r.Operation == "catalog.review.approve.add-alias");
        Assert.Equal("review-item", row.ObjectType);
        Assert.Equal(AuditActorType.User, row.ActorType);
        Assert.Equal("admin", row.ActorName);
        Assert.Equal(AuditResult.Success, row.Result);
        Assert.Contains("rev-alias-target", row.AfterJson);
        Assert.Contains("Open", row.BeforeJson);
    }

    // ════════════════════════════════════════════════════════════════
    // 2. create-channel (explícito, nunca implícito)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Approve_create_channel_creates_explicitly_and_records_audit()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var review = await OpenReviewAsync("reviewcreateobs");
        var key = "rev-created-channel";

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post,
            $"/api/catalog/reviews/{review.Fingerprint}/approve",
            new
            {
                action = "create-channel",
                channel = new { key, name = "Rev Created", country = "PT" },
            },
            csrf));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        long createdId;
        using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal("Resolved", doc.RootElement.GetProperty("state").GetString());
            Assert.Equal("create-channel", doc.RootElement.GetProperty("action").GetString());
            Assert.True(doc.RootElement.GetProperty("catalogueChanged").GetBoolean());
            createdId = doc.RootElement.GetProperty("canonicalChannelId").GetInt64();
            Assert.True(createdId > 0);
        }

        await using (var ctx = _factory.CreateDbContext())
        {
            var channel = await ctx.CanonicalChannels.SingleAsync(c => c.Key == key);
            Assert.Equal("Rev Created", channel.DisplayName);
            Assert.Equal(EditorialCategory.Live, channel.EditorialCategory);
            Assert.True(await ctx.ChannelAliases.AnyAsync(a =>
                a.CanonicalChannelId == channel.Id
                && a.NormalizedAlias == ChannelNormalizer.Normalize("reviewcreateobs")));

            var item = await ctx.ReviewItems.SingleAsync(r => r.Fingerprint == review.Fingerprint);
            Assert.Equal(ReviewItemState.Resolved, item.State);
            Assert.Equal(channel.Id, item.ApprovedCanonicalChannelId);
        }

        var rows = await ReadAuditRowsAsync();
        var row = Assert.Single(rows, r => r.ObjectId == review.Fingerprint
            && r.Operation == "catalog.review.approve.create-channel");
        Assert.Equal(AuditResult.Success, row.Result);
        Assert.Contains(key, row.AfterJson);
    }

    // ════════════════════════════════════════════════════════════════
    // 3. exclude (Ignored; sem alteração de catálogo)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Approve_exclude_marks_ignored_without_catalogue_change_and_records_audit()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var review = await OpenReviewAsync("reviewexcludeobs");
        int channelsBefore;
        int aliasesBefore;
        await using (var ctx = _factory.CreateDbContext())
        {
            channelsBefore = await ctx.CanonicalChannels.CountAsync();
            aliasesBefore = await ctx.ChannelAliases.CountAsync();
        }

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post,
            $"/api/catalog/reviews/{review.Fingerprint}/approve",
            new { action = "exclude", reason = "conteúdo indesejado" },
            csrf));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal("Ignored", doc.RootElement.GetProperty("state").GetString());
            Assert.Equal("exclude", doc.RootElement.GetProperty("action").GetString());
            Assert.False(doc.RootElement.GetProperty("catalogueChanged").GetBoolean());
        }

        await using (var ctx = _factory.CreateDbContext())
        {
            Assert.Equal(channelsBefore, await ctx.CanonicalChannels.CountAsync());
            Assert.Equal(aliasesBefore, await ctx.ChannelAliases.CountAsync());

            var item = await ctx.ReviewItems.SingleAsync(r => r.Fingerprint == review.Fingerprint);
            Assert.Equal(ReviewItemState.Ignored, item.State);
            Assert.Null(item.ApprovedCanonicalChannelId);
            Assert.Equal("conteúdo indesejado", item.Note);
            Assert.NotNull(item.ResolvedAtUtc);
        }

        var rows = await ReadAuditRowsAsync();
        var row = Assert.Single(rows, r => r.ObjectId == review.Fingerprint);
        Assert.Equal("catalog.review.exclude", row.Operation);
        Assert.Equal(AuditResult.Success, row.Result);
        Assert.Contains("indesejado", row.AfterJson);
    }

    // ════════════════════════════════════════════════════════════════
    // 4. Sem action declarada → 400, sem mudança de estado
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Approve_without_declared_action_returns_400_and_no_state_change()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var review = await OpenReviewAsync("reviewnoactionobs");

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post,
            $"/api/catalog/reviews/{review.Fingerprint}/approve",
            new { },
            csrf));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var ctx = _factory.CreateDbContext();
        var item = await ctx.ReviewItems.SingleAsync(r => r.Fingerprint == review.Fingerprint);
        Assert.Equal(ReviewItemState.Open, item.State);
        Assert.Null(item.ResolvedAtUtc);
        var rows = await ReadAuditRowsAsync();
        Assert.DoesNotContain(rows, r => r.ObjectId == review.Fingerprint);
    }

    [Fact]
    public async Task Approve_unknown_action_returns_400_and_no_state_change()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var review = await OpenReviewAsync("reviewunknownactionobs");

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post,
            $"/api/catalog/reviews/{review.Fingerprint}/approve",
            new { action = "delete-everything" },
            csrf));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var ctx = _factory.CreateDbContext();
        Assert.Equal(ReviewItemState.Open,
            (await ctx.ReviewItems.SingleAsync(r => r.Fingerprint == review.Fingerprint)).State);
    }

    [Fact]
    public async Task Approve_add_alias_without_channel_key_returns_400()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var review = await OpenReviewAsync("reviewnokeyobs");

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post,
            $"/api/catalog/reviews/{review.Fingerprint}/approve",
            new { action = "add-alias" },
            csrf));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var ctx = _factory.CreateDbContext();
        Assert.Equal(ReviewItemState.Open,
            (await ctx.ReviewItems.SingleAsync(r => r.Fingerprint == review.Fingerprint)).State);
    }

    // ════════════════════════════════════════════════════════════════
    // 5. Idempotência e conflictos
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Reapprove_same_add_alias_is_idempotent_and_does_not_duplicate()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var channel = await NewChannelAsync("rev-idem-target");
        var review = await OpenReviewAsync("reviewidemobs");
        var body = new { action = "add-alias", canonicalChannelKey = "rev-idem-target" };

        var first = await harness.Client.SendAsync(Json(
            HttpMethod.Post, $"/api/catalog/reviews/{review.Fingerprint}/approve", body, csrf));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await harness.Client.SendAsync(Json(
            HttpMethod.Post, $"/api/catalog/reviews/{review.Fingerprint}/approve", body, csrf));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using (var doc = JsonDocument.Parse(await second.Content.ReadAsStringAsync()))
        {
            Assert.True(doc.RootElement.GetProperty("idempotent").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("catalogueChanged").GetBoolean());
        }

        await using var ctx = _factory.CreateDbContext();
        Assert.Equal(1, await ctx.ChannelAliases.CountAsync(a => a.CanonicalChannelId == channel.Id));
        Assert.Equal(1, await ctx.ReviewItems.CountAsync(r => r.Fingerprint == review.Fingerprint));
    }

    [Fact]
    public async Task Reapprove_with_conflicting_channel_returns_409_and_keeps_original()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var channelA = await NewChannelAsync("rev-conflict-a");
        var channelB = await NewChannelAsync("rev-conflict-b");
        var review = await OpenReviewAsync("reviewconflictobs");

        var first = await harness.Client.SendAsync(Json(
            HttpMethod.Post, $"/api/catalog/reviews/{review.Fingerprint}/approve",
            new { action = "add-alias", canonicalChannelKey = "rev-conflict-a" }, csrf));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var conflict = await harness.Client.SendAsync(Json(
            HttpMethod.Post, $"/api/catalog/reviews/{review.Fingerprint}/approve",
            new { action = "add-alias", canonicalChannelKey = "rev-conflict-b" }, csrf));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        await using var ctx = _factory.CreateDbContext();
        var item = await ctx.ReviewItems.SingleAsync(r => r.Fingerprint == review.Fingerprint);
        Assert.Equal(ReviewItemState.Resolved, item.State);
        Assert.Equal(channelA.Id, item.ApprovedCanonicalChannelId);
        Assert.Equal(1, await ctx.ChannelAliases.CountAsync(a => a.CanonicalChannelId == channelA.Id));
        Assert.Equal(0, await ctx.ChannelAliases.CountAsync(a => a.CanonicalChannelId == channelB.Id));
    }

    [Fact]
    public async Task Excluding_an_approved_review_returns_409()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        await NewChannelAsync("rev-approved-exclude");
        var review = await OpenReviewAsync("reviewapprovedexcobs");

        Assert.Equal(HttpStatusCode.OK, (await harness.Client.SendAsync(Json(
            HttpMethod.Post, $"/api/catalog/reviews/{review.Fingerprint}/approve",
            new { action = "add-alias", canonicalChannelKey = "rev-approved-exclude" }, csrf))).StatusCode);

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post, $"/api/catalog/reviews/{review.Fingerprint}/exclude",
            new { reason = "já não interessa" }, csrf));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await using var ctx = _factory.CreateDbContext();
        Assert.Equal(ReviewItemState.Resolved,
            (await ctx.ReviewItems.SingleAsync(r => r.Fingerprint == review.Fingerprint)).State);
    }

    [Fact]
    public async Task Resolved_review_is_not_reopened_by_a_later_observation()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        await NewChannelAsync("rev-not-reopened");
        var review = await OpenReviewAsync("reviewnotreopenedobs");

        Assert.Equal(HttpStatusCode.OK, (await harness.Client.SendAsync(Json(
            HttpMethod.Post, $"/api/catalog/reviews/{review.Fingerprint}/approve",
            new { action = "add-alias", canonicalChannelKey = "rev-not-reopened" }, csrf))).StatusCode);

        ReviewItemEntity observed = null!;
        await using (var ctx = _factory.CreateDbContext())
        {
            var resolvedAt = (await ctx.ReviewItems.AsNoTracking()
                .SingleAsync(r => r.Fingerprint == review.Fingerprint)).ResolvedAtUtc;

            observed = await _resolver.UpsertReviewItemAsync("reviewnotreopenedobs", "grp", "sig", "nova evidência");

            Assert.Equal(ReviewItemState.Resolved, observed.State);
            Assert.Equal(resolvedAt, observed.ResolvedAtUtc);
        }

        await using var ctx2 = _factory.CreateDbContext();
        Assert.Single(await ctx2.ReviewItems.Where(r => r.Fingerprint == review.Fingerprint).ToListAsync());
    }

    // ════════════════════════════════════════════════════════════════
    // 6. Autenticação / CSRF / método
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Approve_requires_authentication()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);

        var review = await OpenReviewAsync("reviewauthobs");

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post,
            $"/api/catalog/reviews/{review.Fingerprint}/approve",
            new { action = "add-alias", canonicalChannelKey = "whatever" },
            csrf: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Approve_requires_csrf()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        await LoginAsync(harness);

        var review = await OpenReviewAsync("reviewcsrfobs");

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post,
            $"/api/catalog/reviews/{review.Fingerprint}/approve",
            new { action = "add-alias", canonicalChannelKey = "whatever" },
            csrf: null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Approve_wrong_method_returns_405()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var review = await OpenReviewAsync("reviewmethodobs");

        var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/catalog/reviews/{review.Fingerprint}/approve");
        request.Headers.Add("X-CSRF-Token", csrf);
        var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Approve_unknown_fingerprint_returns_404()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await harness.Client.SendAsync(Json(
            HttpMethod.Post,
            "/api/catalog/reviews/does-not-exist/approve",
            new { action = "add-alias", canonicalChannelKey = "whatever" },
            csrf));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
