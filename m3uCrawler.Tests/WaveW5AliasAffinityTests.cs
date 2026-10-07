using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Matching;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W5 — Add Alias / Create Channel alimentam exactamente UMA Channel
/// affinity do canal, no mesmo contexto/transacção e de forma idempotente.
/// Testes ao nível do resolver, a afirmar estado persistido (nunca apenas
/// HTTP 200).
/// </summary>
public class WaveW5AliasAffinityTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private ChannelCatalogBootstrapper _bootstrapper = null!;
    private CatalogResolver _resolver = null!;

    public WaveW5AliasAffinityTests()
    {
        _dbPath = TestTempDb.SuitePath($"w5-alias-affinity-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        _bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await _bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        _resolver = new CatalogResolver(new TestDbContextFactory(_dbPath), _dbPath);
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        return Task.CompletedTask;
    }

    private Task<CanonicalChannelEntity> NewChannelAsync(string key, params string[] aliases)
        => _resolver.CreateCanonicalChannelAsync(
            key, key, EditorialCategory.Live, CanonicalGroupKeys.PortugalGeneralistas,
            PublicationPolicy.CreateEligible, isEnabled: true, normalizedAliases: aliases.ToList());

    private Task<ReviewItemEntity> OpenReviewAsync(string identity)
        => _resolver.UpsertReviewItemAsync(identity, "grp", "sig", "obs");

    private async Task<List<AffinityGroupEntity>> ChannelGroupsAsync()
    {
        var groups = await _resolver.ListAffinityGroupsAsync();
        return groups.Where(g => g.Kind == AffinityKind.Channel).ToList();
    }

    // ════════════════════════════════════════════════════════════════
    // A — canal já com Channel affinity: membro entra no grupo existente
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AddAlias_with_existing_affinity_adds_member_without_new_group()
    {
        var channel = await NewChannelAsync("w5-a-target");
        await _resolver.CreateAffinityGroupAsync(
            "W5 A Existing", AffinityKind.Channel, "w5-a-target", null, new[] { "w5 a original" });
        var review = await OpenReviewAsync("w5-a-new-variant");

        var result = await _resolver.ApplyReviewApprovalAsync(
            review.Fingerprint,
            new ReviewApprovalDecision(ReviewApprovalAction.AddAlias, "w5-a-target", "w5-a-new-variant"));

        Assert.NotNull(result);
        Assert.Equal(ReviewItemState.Resolved, result!.Review.State);

        var groups = await ChannelGroupsAsync();
        var group = Assert.Single(groups);
        Assert.Equal("w5-a-target", group.CanonicalChannelKey);
        Assert.Equal(channel.Id, group.CanonicalChannelId);
        Assert.Equal(2, group.Members.Count);
        Assert.Single(group.Members, m =>
            m.NormalizedMember == ChannelNormalizer.Normalize("w5-a-new-variant"));
    }

    // ════════════════════════════════════════════════════════════════
    // B — sem affinity: cria exactamente UMA e associa o canal
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AddAlias_without_affinity_creates_single_channel_group()
    {
        var channel = await NewChannelAsync("w5-b-target");
        var review = await OpenReviewAsync("w5-b-new-variant");

        var result = await _resolver.ApplyReviewApprovalAsync(
            review.Fingerprint,
            new ReviewApprovalDecision(ReviewApprovalAction.AddAlias, "w5-b-target", "w5-b-new-variant"));

        Assert.NotNull(result);

        var groups = await ChannelGroupsAsync();
        var group = Assert.Single(groups);
        Assert.Equal("w5-b-target", group.Name);
        Assert.Equal("w5-b-target", group.CanonicalChannelKey);
        Assert.Equal(channel.Id, group.CanonicalChannelId);
        var member = Assert.Single(group.Members);
        Assert.Equal(ChannelNormalizer.Normalize("w5-b-new-variant"), member.NormalizedMember);
        Assert.Equal(AffinityKind.Channel, member.Kind);
    }

    // ════════════════════════════════════════════════════════════════
    // Idempotência — sem grupo/membro duplicado
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AddAlias_is_idempotent_for_group_and_member()
    {
        await NewChannelAsync("w5-idem-target");
        var review = await OpenReviewAsync("w5-idem-variant");
        var decision = new ReviewApprovalDecision(
            ReviewApprovalAction.AddAlias, "w5-idem-target", "w5-idem-variant");

        var first = await _resolver.ApplyReviewApprovalAsync(review.Fingerprint, decision);
        var second = await _resolver.ApplyReviewApprovalAsync(review.Fingerprint, decision);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(second!.Idempotent);

        var groups = await ChannelGroupsAsync();
        var group = Assert.Single(groups);
        var member = Assert.Single(group.Members);
        Assert.Equal(ChannelNormalizer.Normalize("w5-idem-variant"), member.NormalizedMember);
    }

    [Fact]
    public async Task EnsureChannelAffinityMember_public_wrapper_is_idempotent()
    {
        var channel = await NewChannelAsync("w5-pub-target");

        await _resolver.EnsureChannelAffinityMemberAsync("w5-pub-target", "w5 pub variant");
        // Formas diferentes que normalizam para o mesmo membro.
        await _resolver.EnsureChannelAffinityMemberAsync("w5-pub-target", "W5 Pub Variant");

        var groups = await ChannelGroupsAsync();
        var group = Assert.Single(groups);
        Assert.Equal(channel.Id, group.CanonicalChannelId);
        Assert.Single(group.Members);
    }

    // ════════════════════════════════════════════════════════════════
    // Erros de domínio
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AddAlias_unknown_canonical_channel_throws_channel_not_found()
    {
        var review = await OpenReviewAsync("w5-unknown-channel-obs");

        var ex = await Assert.ThrowsAsync<ChannelAdministrationException>(() =>
            _resolver.ApplyReviewApprovalAsync(
                review.Fingerprint,
                new ReviewApprovalDecision(ReviewApprovalAction.AddAlias, "w5-does-not-exist", null)));

        Assert.Equal(ChannelAdministrationError.ChannelNotFound, ex.Error);
    }

    [Fact]
    public async Task AddAlias_conflicting_alias_throws_alias_conflict()
    {
        await NewChannelAsync("w5-dup-a", "w5 shared alias");
        await NewChannelAsync("w5-dup-b");
        var review = await OpenReviewAsync("w5-dup-obs");

        var ex = await Assert.ThrowsAsync<ChannelAdministrationException>(() =>
            _resolver.ApplyReviewApprovalAsync(
                review.Fingerprint,
                new ReviewApprovalDecision(ReviewApprovalAction.AddAlias, "w5-dup-b", "w5 shared alias")));

        Assert.Equal(ChannelAdministrationError.AliasConflict, ex.Error);
    }

    // ════════════════════════════════════════════════════════════════
    // Create Channel via Review — membro + IsEnabled
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateChannel_via_review_feeds_affinity_and_respects_isEnabled()
    {
        var review = await OpenReviewAsync("w5-create-obs");

        var result = await _resolver.ApplyReviewApprovalAsync(
            review.Fingerprint,
            new ReviewApprovalDecision(
                ReviewApprovalAction.CreateChannel,
                null,
                null,
                null,
                new ReviewChannelSpec("w5-created", "W5 Created", IsEnabled: false)));

        Assert.NotNull(result);
        var channelId = result!.Channel!.Id;

        await using (var ctx = new TestDbContextFactory(_dbPath).CreateDbContext())
        {
            var channel = await ctx.CanonicalChannels.AsNoTracking().SingleAsync(c => c.Id == channelId);
            Assert.False(channel.IsEnabled);
            var stored = await ctx.ReviewItems.AsNoTracking().SingleAsync(r => r.Id == review.Id);
            Assert.Equal(ReviewItemState.Resolved, stored.State);
        }

        var groups = await ChannelGroupsAsync();
        var group = Assert.Single(groups);
        Assert.Equal(channelId, group.CanonicalChannelId);
        Assert.Single(group.Members, m =>
            m.NormalizedMember == ChannelNormalizer.Normalize("w5-create-obs"));
    }

    [Fact]
    public async Task CreateChannel_via_review_without_isEnabled_defaults_to_true()
    {
        var review = await OpenReviewAsync("w5-create-default-obs");

        var result = await _resolver.ApplyReviewApprovalAsync(
            review.Fingerprint,
            new ReviewApprovalDecision(
                ReviewApprovalAction.CreateChannel,
                null,
                null,
                null,
                new ReviewChannelSpec("w5-created-default", "W5 Created Default")));

        Assert.NotNull(result);
        await using var ctx = new TestDbContextFactory(_dbPath).CreateDbContext();
        var channel = await ctx.CanonicalChannels.AsNoTracking().SingleAsync(c => c.Id == result!.Channel!.Id);
        Assert.True(channel.IsEnabled);
    }
}
