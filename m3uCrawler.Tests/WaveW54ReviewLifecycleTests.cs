using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Services.Audit;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.Recognition;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W5.4 — Review lifecycle (DL-105/DL-119; <c>33-STATE-MACHINES.md §ReviewItem</c>).
///
/// <para>
/// Cobre estados e transições, transições inválidas, motivo obrigatório em
/// Ignore, justificação obrigatória em Reopen, auditoria, preservação de
/// histórico, idempotência, mapeamento legacy e a integração do diagnóstico
/// fuzzy (<c>fuzzy-ambiguous</c>) num <see cref="ReviewItemEntity"/>.
/// </para>
/// </summary>
public class WaveW54ReviewLifecycleTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;
    private CatalogResolver _catalog = null!;
    private AuditService _audit = null!;

    public WaveW54ReviewLifecycleTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"wave-w5-54-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        _factory = new TestDbContextFactory(_dbPath);
        _audit = new AuditService(_factory);
        _catalog = new CatalogResolver(_factory, _dbPath, _audit);
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        return Task.CompletedTask;
    }

    // ────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────

    private Task<ReviewItemEntity> OpenAsync(string identity)
        => _catalog.UpsertReviewItemAsync(identity, "grp", "sig", "observação inicial");

    private async Task<ReviewItemEntity> LoadAsync(string fingerprint)
    {
        await using var ctx = _factory.CreateDbContext();
        return await ctx.ReviewItems.AsNoTracking().SingleAsync(r => r.Fingerprint == fingerprint);
    }

    private async Task<List<AuditRecordEntity>> AuditRowsAsync(string fingerprint)
    {
        await using var ctx = _factory.CreateDbContext();
        return await ctx.AuditRecords.AsNoTracking()
            .Where(r => r.ObjectType == "review-item" && r.ObjectId == fingerprint)
            .OrderBy(r => r.Id)
            .ToListAsync();
    }

    private static string Norm(string raw) => ChannelNormalizer.Normalize(raw);

    private Task<CanonicalChannelEntity> Ch(string key, string displayName, bool enabled = true, params string[] aliases)
        => _catalog.CreateCanonicalChannelAsync(
            key, displayName, EditorialCategory.Live, CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy.CreateEligible, enabled, aliases);

    private static RecognitionPolicy FuzzyPolicy(int threshold, int margin)
        => new(
            Enabled: true, FuzzyEnabled: true, FuzzyThreshold: threshold,
            FuzzyAmbiguityMargin: margin, FuzzyWeightsJson: null, Version: 1);

    private static async Task AssertInvalidAsync(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<ChannelAdministrationException>(action);
        Assert.Equal(ChannelAdministrationError.ReviewConflict, ex.Error);
    }

    private static async Task AssertInvalidInputAsync(Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<ChannelAdministrationException>(action);
        Assert.Equal(ChannelAdministrationError.InvalidInput, ex.Error);
    }

    // ────────────────────────────────────────────────────────────────
    // Máquina de estados pura
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Machine_allows_exactly_the_six_normative_transitions()
    {
        var allowed = new HashSet<(ReviewItemState, ReviewItemState)>
        {
            (ReviewItemState.Open, ReviewItemState.InReview),
            (ReviewItemState.Open, ReviewItemState.Ignored),
            (ReviewItemState.InReview, ReviewItemState.Resolved),
            (ReviewItemState.InReview, ReviewItemState.Ignored),
            (ReviewItemState.Resolved, ReviewItemState.Open),
            (ReviewItemState.Ignored, ReviewItemState.Open),
        };

        foreach (var from in Enum.GetValues<ReviewItemState>())
        {
            foreach (var to in Enum.GetValues<ReviewItemState>())
            {
                Assert.Equal(allowed.Contains((from, to)), ReviewLifecycle.CanTransition(from, to));
            }
        }
    }

    [Theory]
    [InlineData(ReviewItemState.Open, ReviewItemState.Resolved)]
    [InlineData(ReviewItemState.Open, ReviewItemState.Open)]
    [InlineData(ReviewItemState.InReview, ReviewItemState.InReview)]
    [InlineData(ReviewItemState.InReview, ReviewItemState.Open)]
    [InlineData(ReviewItemState.Resolved, ReviewItemState.InReview)]
    [InlineData(ReviewItemState.Resolved, ReviewItemState.Ignored)]
    [InlineData(ReviewItemState.Resolved, ReviewItemState.Resolved)]
    [InlineData(ReviewItemState.Ignored, ReviewItemState.InReview)]
    [InlineData(ReviewItemState.Ignored, ReviewItemState.Resolved)]
    [InlineData(ReviewItemState.Ignored, ReviewItemState.Ignored)]
    public void Machine_rejects_forbidden_transitions(ReviewItemState from, ReviewItemState to)
    {
        Assert.False(ReviewLifecycle.CanTransition(from, to));
        Assert.Throws<InvalidOperationException>(() => ReviewLifecycle.EnsureTransition(from, to));
    }

    // ────────────────────────────────────────────────────────────────
    // Estados e transições válidas (serviço)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Initial_state_is_open()
    {
        var item = await OpenAsync("w54-open");
        Assert.Equal(ReviewItemState.Open, item.State);
        Assert.Null(item.ResolvedAtUtc);
    }

    [Fact]
    public async Task Open_to_InReview()
    {
        var item = await OpenAsync("w54-begin");
        var result = await _catalog.BeginReviewAsync(item.Fingerprint);

        Assert.NotNull(result);
        Assert.True(result!.Changed);
        Assert.Equal(ReviewItemState.InReview, result.Review.State);
        Assert.Equal("catalog.review.begin", result.Operation);
        Assert.Equal(ReviewItemState.InReview, (await LoadAsync(item.Fingerprint)).State);
    }

    [Fact]
    public async Task Open_to_Ignored()
    {
        var item = await OpenAsync("w54-open-ignore");
        var result = await _catalog.IgnoreReviewAsync(item.Fingerprint, "duplicado");

        Assert.NotNull(result);
        Assert.Equal(ReviewItemState.Ignored, result!.Review.State);
        var stored = await LoadAsync(item.Fingerprint);
        Assert.Equal(ReviewItemState.Ignored, stored.State);
        Assert.NotNull(stored.ResolvedAtUtc);
        Assert.Equal("duplicado", stored.Note);
    }

    [Fact]
    public async Task InReview_to_Resolved()
    {
        var item = await OpenAsync("w54-resolve");
        await _catalog.BeginReviewAsync(item.Fingerprint);
        var result = await _catalog.ResolveReviewAsync(item.Fingerprint, approvedCanonicalChannelId: null);

        Assert.NotNull(result);
        Assert.Equal(ReviewItemState.Resolved, result!.Review.State);
        var stored = await LoadAsync(item.Fingerprint);
        Assert.Equal(ReviewItemState.Resolved, stored.State);
        Assert.NotNull(stored.ResolvedAtUtc);
    }

    [Fact]
    public async Task InReview_to_Ignored()
    {
        var item = await OpenAsync("w54-inreview-ignore");
        await _catalog.BeginReviewAsync(item.Fingerprint);
        var result = await _catalog.IgnoreReviewAsync(item.Fingerprint, "sem correspondência");

        Assert.Equal(ReviewItemState.Ignored, result!.Review.State);
    }

    [Fact]
    public async Task Resolved_to_Open_by_manual_reopen()
    {
        var item = await OpenAsync("w54-reopen-resolved");
        await _catalog.BeginReviewAsync(item.Fingerprint);
        await _catalog.ResolveReviewAsync(item.Fingerprint, approvedCanonicalChannelId: null);

        var result = await _catalog.ReopenReviewAsync(item.Fingerprint, "nova evidência do operador");

        Assert.NotNull(result);
        Assert.Equal(ReviewItemState.Open, result!.Review.State);
        Assert.Equal("catalog.review.reopen", result.Operation);
        var stored = await LoadAsync(item.Fingerprint);
        Assert.Equal(ReviewItemState.Open, stored.State);
        Assert.Null(stored.ResolvedAtUtc);
    }

    [Fact]
    public async Task Ignored_to_Open_by_manual_reopen()
    {
        var item = await OpenAsync("w54-reopen-ignored");
        await _catalog.IgnoreReviewAsync(item.Fingerprint, "motivo");

        var result = await _catalog.ReopenReviewAsync(item.Fingerprint, "evidência nova");

        Assert.Equal(ReviewItemState.Open, result!.Review.State);
        Assert.Equal(ReviewItemState.Open, (await LoadAsync(item.Fingerprint)).State);
    }

    // ────────────────────────────────────────────────────────────────
    // Transições inválidas (serviço)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_from_open_is_rejected()
    {
        var item = await OpenAsync("w54-resolve-open");
        await AssertInvalidAsync(() => _catalog.ResolveReviewAsync(item.Fingerprint, null));
    }

    [Fact]
    public async Task BeginReview_from_ignored_is_rejected()
    {
        var item = await OpenAsync("w54-begin-ignored");
        await _catalog.IgnoreReviewAsync(item.Fingerprint, "motivo");
        await AssertInvalidAsync(() => _catalog.BeginReviewAsync(item.Fingerprint));
    }

    [Fact]
    public async Task Ignore_from_resolved_is_rejected()
    {
        var item = await OpenAsync("w54-ignore-resolved");
        await _catalog.BeginReviewAsync(item.Fingerprint);
        await _catalog.ResolveReviewAsync(item.Fingerprint, null);
        await AssertInvalidAsync(() => _catalog.IgnoreReviewAsync(item.Fingerprint, "motivo"));
    }

    [Fact]
    public async Task Resolve_from_ignored_is_rejected()
    {
        var item = await OpenAsync("w54-resolve-ignored");
        await _catalog.IgnoreReviewAsync(item.Fingerprint, "motivo");
        await AssertInvalidAsync(() => _catalog.ResolveReviewAsync(item.Fingerprint, null));
    }

    [Fact]
    public async Task BeginReview_from_in_review_is_idempotent()
    {
        var item = await OpenAsync("w54-begin-idempotent");
        await _catalog.BeginReviewAsync(item.Fingerprint);
        var second = await _catalog.BeginReviewAsync(item.Fingerprint);

        Assert.NotNull(second);
        Assert.False(second!.Changed);
        Assert.Equal(ReviewItemState.InReview, second.Review.State);
    }

    // ────────────────────────────────────────────────────────────────
    // Ignore — motivo obrigatório
    // ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Ignore_requires_a_non_empty_reason(string? reason)
    {
        var item = await OpenAsync($"w54-ignore-reason-{reason?.Length ?? -1}");
        await AssertInvalidInputAsync(() => _catalog.IgnoreReviewAsync(item.Fingerprint, reason!));

        // O estado não foi alterado.
        Assert.Equal(ReviewItemState.Open, (await LoadAsync(item.Fingerprint)).State);
    }

    // ────────────────────────────────────────────────────────────────
    // Reopen — justificação obrigatória
    // ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Reopen_requires_a_non_empty_justification(string? justification)
    {
        var item = await OpenAsync($"w54-reopen-just-{justification?.Length ?? -1}");
        await _catalog.BeginReviewAsync(item.Fingerprint);
        await _catalog.ResolveReviewAsync(item.Fingerprint, null);

        await AssertInvalidInputAsync(() => _catalog.ReopenReviewAsync(item.Fingerprint, justification!));

        Assert.Equal(ReviewItemState.Resolved, (await LoadAsync(item.Fingerprint)).State);
    }

    // ────────────────────────────────────────────────────────────────
    // Auditoria
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lifecycle_operations_are_audited()
    {
        var item = await OpenAsync("w54-audit");

        await _catalog.BeginReviewAsync(item.Fingerprint);
        await _catalog.ResolveReviewAsync(item.Fingerprint, null);
        await _catalog.ReopenReviewAsync(item.Fingerprint, "evidência nova");
        await _catalog.IgnoreReviewAsync(item.Fingerprint, "motivo final");

        var rows = await AuditRowsAsync(item.Fingerprint);
        Assert.Equal(4, rows.Count);

        Assert.Equal(
            new[]
            {
                "catalog.review.begin",
                "catalog.review.resolve",
                "catalog.review.reopen",
                "catalog.review.ignore",
            },
            rows.Select(r => r.Operation).ToArray());

        var begin = rows[0];
        Assert.Contains("\"state\":\"Open\"", begin.BeforeJson);
        Assert.Contains("\"state\":\"InReview\"", begin.AfterJson);
        Assert.Equal("system", begin.ActorType);
        Assert.NotEqual(default, begin.OccurredAtUtc);

        Assert.Contains("evidência nova", rows[2].Detail);
        Assert.Contains("motivo final", rows[3].Detail);
    }

    [Fact]
    public async Task Audit_is_optional_and_absent_without_service()
    {
        var item = await OpenAsync("w54-no-audit");
        var withoutAudit = new CatalogResolver(_factory, _dbPath);

        var result = await withoutAudit.BeginReviewAsync(item.Fingerprint);

        Assert.Equal(ReviewItemState.InReview, result!.Review.State);
        Assert.Empty(await AuditRowsAsync(item.Fingerprint));
    }

    // ────────────────────────────────────────────────────────────────
    // Histórico / idempotência
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reopen_preserves_the_previous_decision_history()
    {
        var item = await OpenAsync("w54-history");
        await _catalog.IgnoreReviewAsync(item.Fingerprint, "razão original");

        await _catalog.ReopenReviewAsync(item.Fingerprint, "reavaliado");

        var stored = await LoadAsync(item.Fingerprint);
        Assert.Equal(ReviewItemState.Open, stored.State);
        // O item não é eliminado e a nota anterior mantém-se.
        Assert.Equal("razão original", stored.Note);
        Assert.Single(await ListAllAsync(), r => r.Fingerprint == item.Fingerprint);
    }

    [Fact]
    public async Task Review_items_are_idempotent_by_fingerprint()
    {
        var first = await OpenAsync("w54-idempotent");
        var second = await OpenAsync("w54-idempotent");

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await ListAllAsync(), r => r.Fingerprint == first.Fingerprint);
    }

    private async Task<IReadOnlyList<ReviewItemEntity>> ListAllAsync()
        => await _catalog.ListAllReviewItemsAsync();

    // ────────────────────────────────────────────────────────────────
    // Mapeamento legacy
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Legacy_approve_maps_to_resolved()
    {
        var item = await OpenAsync("w54-legacy-approve");
        var approved = await _catalog.ApproveReviewAsync(item.Fingerprint, null);

        Assert.Equal(ReviewItemState.Resolved, approved!.State);
        Assert.Equal(ReviewItemState.Resolved, (await LoadAsync(item.Fingerprint)).State);
    }

    [Fact]
    public async Task Legacy_approve_composes_through_in_review()
    {
        // A composição Open → InReview → Resolved não introduz a aresta
        // proibida Open → Resolved na máquina.
        Assert.False(ReviewLifecycle.CanTransition(ReviewItemState.Open, ReviewItemState.Resolved));

        var item = await OpenAsync("w54-legacy-compose");
        var approved = await _catalog.ApproveReviewAsync(item.Fingerprint, null);

        Assert.Equal(ReviewItemState.Resolved, approved!.State);
    }

    [Fact]
    public async Task Legacy_exclude_maps_to_ignored()
    {
        var item = await OpenAsync("w54-legacy-exclude");
        var excluded = await _catalog.ExcludeReviewAsync(item.Fingerprint);

        Assert.Equal(ReviewItemState.Ignored, excluded!.State);
        var stored = await LoadAsync(item.Fingerprint);
        Assert.Equal(ReviewItemState.Ignored, stored.State);
        Assert.Null(stored.ApprovedCanonicalChannelId);
    }

    [Fact]
    public async Task Legacy_approve_on_ignored_is_rejected()
    {
        var item = await OpenAsync("w54-legacy-conflict");
        await _catalog.ExcludeReviewAsync(item.Fingerprint);
        await AssertInvalidAsync(() => _catalog.ApproveReviewAsync(item.Fingerprint, null));
    }

    [Fact]
    public async Task Legacy_exclude_on_resolved_is_rejected()
    {
        var item = await OpenAsync("w54-legacy-conflict-2");
        await _catalog.ApproveReviewAsync(item.Fingerprint, null);
        await AssertInvalidAsync(() => _catalog.ExcludeReviewAsync(item.Fingerprint));
    }

    // ────────────────────────────────────────────────────────────────
    // Fuzzy ambiguity → ReviewItem
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Fuzzy_ambiguous_resolution_produces_a_fuzzy_review_item()
    {
        await Ch("w54-fuzzy-a", "CNN International");
        await Ch("w54-fuzzy-b", "CNN International Plus");

        var resolution = await _catalog.ResolveAsync(
            Norm("CNN Internat"), null, FuzzyPolicy(threshold: 80, margin: 0));

        // W5.3: a ambiguidade de fuzzy é exposta no diagnóstico.
        Assert.Equal(CatalogResolutionKind.Ambiguous, resolution.Kind);
        Assert.NotNull(resolution.FuzzyDiagnostic);
        Assert.Equal("fuzzy-ambiguous", resolution.FuzzyDiagnostic!.Value.DecisionReason);

        // W5.4: o motivo fuzzy é usado como reasonSignature do ReviewItem.
        var reason = PipelineIngestionService.AmbiguousReasonSignature(resolution);
        Assert.Equal("fuzzy-ambiguous", reason);

        var item = await _catalog.UpsertReviewItemAsync(
            Norm("CNN Internat"), "grp", reason, "ambiguidade fuzzy (W5.3)");

        Assert.Equal(ReviewItemState.Open, item.State);
        Assert.Equal("fuzzy-ambiguous", item.ReasonSignature);
    }

    [Fact]
    public void Non_fuzzy_ambiguous_keeps_the_historical_reason()
    {
        var resolution = CatalogResolution.Ambiguous("ambiguous-external-identity");
        Assert.Equal("ambiguous-external-identity", PipelineIngestionService.AmbiguousReasonSignature(resolution));
    }

    [Fact]
    public async Task Canonical_resolution_does_not_create_a_fuzzy_review()
    {
        await Ch("w54-canonical", "CNN International");

        var resolution = await _catalog.ResolveAsync(
            Norm("CNN Internat"), null, FuzzyPolicy(threshold: 80, margin: 5));

        Assert.Equal(CatalogResolutionKind.Canonical, resolution.Kind);
        Assert.Equal(RecognitionMatchMethods.Fuzzy, resolution.MatchMethod);
        // O diagnóstico existe, mas não é uma ambiguidade: não origina Review.
        Assert.Equal("fuzzy-canonical", resolution.FuzzyDiagnostic!.Value.DecisionReason);
        Assert.Empty(await _catalog.ListAllReviewItemsAsync());
    }

    [Fact]
    public async Task Unknown_resolution_does_not_create_a_fuzzy_review()
    {
        await Ch("w54-unknown", "Zzz Qqq Completamente Diferente");

        var resolution = await _catalog.ResolveAsync(
            Norm("CNN Internat"), null, FuzzyPolicy(threshold: 99, margin: 0));

        Assert.Equal(CatalogResolutionKind.Unknown, resolution.Kind);
        Assert.Empty(await _catalog.ListAllReviewItemsAsync());
    }
}
