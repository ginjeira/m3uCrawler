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
/// Wave B — a identidade de canal é <c>CanonicalChannel (Key)</c> +
/// <c>ChannelAlias</c>. Todos os caminhos de escrita de aliases/membros
/// de afinidade produzem a mesma forma matchable que o
/// <see cref="ChannelNormalizer.Normalize"/> gera para os títulos, e
/// instalações pré-existentes são normalizadas no arranque.
/// </summary>
public class CatalogAliasNormalizationTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private IDbContextFactory<ChannelCatalogDbContext> _factory = null!;
    private ChannelCatalogBootstrapper _bootstrapper = null!;
    private CatalogResolver _resolver = null!;

    public CatalogAliasNormalizationTests()
    {
        _dbPath = TestTempDb.SuitePath($"catalog-normalization-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        _bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await _bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        _resolver = new CatalogResolver(_factory, _dbPath);
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        return Task.CompletedTask;
    }

    private Task<CanonicalChannelEntity> NewChannelAsync(string key, params string[] aliases) =>
        _resolver.CreateCanonicalChannelAsync(
            key, key, EditorialCategory.Live, CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy.CreateEligible, isEnabled: true,
            normalizedAliases: aliases.ToList());

    private async Task<List<string>> AliasesOfAsync(long channelId)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.ChannelAliases
            .Where(a => a.CanonicalChannelId == channelId)
            .Select(a => a.NormalizedAlias)
            .ToListAsync();
    }

    // ════════════════════════════════════════════════════════════════
    // A. Caminhos de escrita normalizam para a forma matchable
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateCanonicalChannel_dedupes_case_spacing_quality_and_country_tokens()
    {
        var ch = await NewChannelAsync(
            "norm-create",
            "Zetalândia TV", "zetalandia tv", "ZETALANDIA  TV HD", "PT Zetalandia TV");

        var aliases = await AliasesOfAsync(ch.Id);
        Assert.Single(aliases);
        Assert.Equal("zetalandia tv", aliases[0]);

        var resolved = await _resolver.ResolveAsync("zetalandia tv");
        Assert.Equal(CatalogResolutionKind.Canonical, resolved.Kind);
        Assert.Equal(ch.Id, resolved.CanonicalChannelId);
    }

    [Fact]
    public async Task CreateCanonicalChannel_strips_diacritics_consistently()
    {
        var ch = await NewChannelAsync("norm-diacritics", "Zédia Crítica", "Zedia Critica");

        var aliases = await AliasesOfAsync(ch.Id);
        Assert.Single(aliases);
        Assert.Equal("zedia critica", aliases[0]);
        Assert.Equal(ChannelNormalizer.Normalize("Zédia Crítica"), aliases[0]);
    }

    [Fact]
    public async Task AddAlias_normalizes_to_matchable_form_and_rejects_empty_normalization()
    {
        var ch = await NewChannelAsync("norm-add");

        var alias = await _resolver.AddAliasAsync(ch.Id, "Zetaflix HEVC PT");
        Assert.Equal("zetaflix", alias.NormalizedAlias);

        var resolved = await _resolver.ResolveAsync("zetaflix");
        Assert.Equal(CatalogResolutionKind.Canonical, resolved.Kind);
        Assert.Equal(ch.Id, resolved.CanonicalChannelId);

        // Um alias que normaliza para vazio não é matchable → rejeitado.
        await Assert.ThrowsAsync<ChannelAdministrationException>(
            () => _resolver.AddAliasAsync(ch.Id, "HD"));
    }

    [Fact]
    public async Task AddAlias_collision_on_normalized_form_is_reported()
    {
        var a = await NewChannelAsync("norm-collide-a", "zetaflix");
        var b = await NewChannelAsync("norm-collide-b");

        // "Zetaflix HEVC PT" normaliza para "zetaflix", do canal A.
        await Assert.ThrowsAsync<ChannelAdministrationException>(
            () => _resolver.AddAliasAsync(b.Id, "Zetaflix HEVC PT"));
    }

    [Fact]
    public async Task CreateCanonicalChannel_collision_on_normalized_form_is_reported()
    {
        await NewChannelAsync("norm-collide-c", "zetaflix");

        await Assert.ThrowsAsync<ChannelAdministrationException>(
            () => NewChannelAsync("norm-collide-d", "Zetaflix HEVC PT"));
    }

    // ════════════════════════════════════════════════════════════════
    // B. Membros de afinidade Channel normalizados; Country trim'd
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Channel_affinity_members_are_normalized_and_resolve()
    {
        var ch = await NewChannelAsync("norm-affinity");

        var group = await _resolver.CreateAffinityGroupAsync(
            "Norm Affinity", AffinityKind.Channel, ch.Key, null, new[] { "Zeta One HD" });

        var stored = group.Members.Single().NormalizedMember;
        Assert.Equal("zeta one", stored);

        var resolved = await _resolver.ResolveAsync(stored);
        Assert.Equal(CatalogResolutionKind.Canonical, resolved.Kind);
        Assert.Equal(ch.Id, resolved.CanonicalChannelId);
    }

    [Fact]
    public async Task UpdateAffinityGroup_members_are_normalized()
    {
        var ch = await NewChannelAsync("norm-affinity-update");
        var group = await _resolver.CreateAffinityGroupAsync(
            "Norm Affinity Update", AffinityKind.Channel, ch.Key, null, new[] { "zeta one" });

        var updated = await _resolver.UpdateAffinityGroupAsync(
            group.Id, "Norm Affinity Update", AffinityKind.Channel, ch.Key, null,
            new[] { "Zeta Two FHD" });

        Assert.NotNull(updated);
        Assert.Equal("zeta two", updated!.Members.Single().NormalizedMember);
    }

    [Fact]
    public async Task Country_affinity_members_are_trimmed_but_not_normalized()
    {
        var group = await _resolver.CreateAffinityGroupAsync(
            "Norm Country", AffinityKind.Country, null, "pt", new[] { "  PT TOKEN  " });

        Assert.Equal("PT TOKEN", group.Members.Single().NormalizedMember);
    }

    // ════════════════════════════════════════════════════════════════
    // C. Normalização de instalações pré-existentes no arranque
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Existing_raw_alias_becomes_matchable_after_bootstrap_and_is_idempotent()
    {
        long channelId;
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var now = DateTime.UtcNow;
            var ch = new CanonicalChannelEntity
            {
                Key = "legacy-raw",
                DisplayName = "Legacy Raw",
                EditorialCategory = EditorialCategory.Live,
                EditorialGroup = CanonicalEditorialGroup.PortugalLive,
                PublicationPolicy = PublicationPolicy.CreateEligible,
                IsEnabled = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            ctx.CanonicalChannels.Add(ch);
            await ctx.SaveChangesAsync();
            channelId = ch.Id;
            ctx.ChannelAliases.Add(new ChannelAliasEntity
            {
                NormalizedAlias = "LEGACY RAW HD",
                CanonicalChannelId = channelId,
                CreatedAtUtc = now,
            });
            await ctx.SaveChangesAsync();
        }

        // Antes da normalização o alias bruto não é matchable.
        var before = await _resolver.ResolveAsync("legacy raw");
        Assert.Equal(CatalogResolutionKind.Unknown, before.Kind);

        var ctx2 = await _bootstrapper.InitializeAsync();
        await ctx2.DisposeAsync();

        var after = await _resolver.ResolveAsync("legacy raw");
        Assert.Equal(CatalogResolutionKind.Canonical, after.Kind);
        Assert.Equal(channelId, after.CanonicalChannelId);

        var aliases = await AliasesOfAsync(channelId);
        Assert.Contains("legacy raw", aliases);
        Assert.DoesNotContain("LEGACY RAW HD", aliases);

        // Segundo arranque é idempotente.
        var ctx3 = await _bootstrapper.InitializeAsync();
        await ctx3.DisposeAsync();
        Assert.Equal(aliases.OrderBy(a => a), (await AliasesOfAsync(channelId)).OrderBy(a => a));
    }

    [Fact]
    public async Task Existing_raw_alias_collision_is_skipped_without_throwing()
    {
        long channelAId;
        long channelBId;
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var now = DateTime.UtcNow;
            var a = new CanonicalChannelEntity
            {
                Key = "norm-collision-a",
                DisplayName = "Norm Collision A",
                EditorialCategory = EditorialCategory.Live,
                EditorialGroup = CanonicalEditorialGroup.PortugalLive,
                PublicationPolicy = PublicationPolicy.CreateEligible,
                IsEnabled = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            var b = new CanonicalChannelEntity
            {
                Key = "norm-collision-b",
                DisplayName = "Norm Collision B",
                EditorialCategory = EditorialCategory.Live,
                EditorialGroup = CanonicalEditorialGroup.PortugalLive,
                PublicationPolicy = PublicationPolicy.CreateEligible,
                IsEnabled = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            ctx.CanonicalChannels.AddRange(a, b);
            await ctx.SaveChangesAsync();
            channelAId = a.Id;
            channelBId = b.Id;

            // B já detém a forma normalizada; A tem uma forma em bruto
            // que normaliza para o mesmo valor.
            ctx.ChannelAliases.Add(new ChannelAliasEntity
            {
                NormalizedAlias = "zzunique 1",
                CanonicalChannelId = channelBId,
                CreatedAtUtc = now,
            });
            ctx.ChannelAliases.Add(new ChannelAliasEntity
            {
                NormalizedAlias = "ZZUNIQUE 1 HD",
                CanonicalChannelId = channelAId,
                CreatedAtUtc = now,
            });
            await ctx.SaveChangesAsync();
        }

        // Não deve lançar.
        var ctx2 = await _bootstrapper.InitializeAsync();
        await ctx2.DisposeAsync();

        var bAliases = await AliasesOfAsync(channelBId);
        Assert.Contains("zzunique 1", bAliases);

        // A colisão é skip determinístico: o alias legacy fica como
        // está e o dono existente mantém-se.
        var aAliases = await AliasesOfAsync(channelAId);
        Assert.Contains("ZZUNIQUE 1 HD", aAliases);

        var resolved = await _resolver.ResolveAsync("zzunique 1");
        Assert.Equal(CatalogResolutionKind.Canonical, resolved.Kind);
        Assert.Equal(channelBId, resolved.CanonicalChannelId);
    }

    // ════════════════════════════════════════════════════════════════
    // D. Decisão humana de revisão não é revertida
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpsertReviewItem_does_not_reopen_approved_decision()
    {
        var open = await _resolver.UpsertReviewItemAsync(
            "review-approved-subject", "grp", "sig", "initial note");
        Assert.Equal(ReviewItemState.Open, open.State);

        var approved = await _resolver.ApproveReviewAsync(open.Fingerprint, null);
        Assert.NotNull(approved);
        var resolvedAt = approved!.ResolvedAtUtc;

        var observed = await _resolver.UpsertReviewItemAsync(
            "review-approved-subject", "grp", "sig", "new evidence");

        Assert.Equal(ReviewItemState.Approved, observed.State);
        Assert.Equal(open.Fingerprint, observed.Fingerprint);
        Assert.Equal(resolvedAt, observed.ResolvedAtUtc);
        Assert.Equal("initial note", observed.Note);

        // Idempotente: sem linhas duplicadas para o mesmo fingerprint.
        var all = await _resolver.ListAllReviewItemsAsync();
        Assert.Single(all, r => r.Fingerprint == open.Fingerprint);
    }

    [Fact]
    public async Task UpsertReviewItem_does_not_reopen_excluded_decision()
    {
        var open = await _resolver.UpsertReviewItemAsync(
            "review-excluded-subject", "grp", "sig", "n");
        await _resolver.ExcludeReviewAsync(open.Fingerprint);

        var observed = await _resolver.UpsertReviewItemAsync(
            "review-excluded-subject", "grp", "sig", "again");

        Assert.Equal(ReviewItemState.Excluded, observed.State);
        Assert.NotNull(observed.ResolvedAtUtc);
    }

    [Fact]
    public async Task UpsertReviewItem_open_is_idempotent()
    {
        var first = await _resolver.UpsertReviewItemAsync("review-open-subject", "grp", "sig", "n");
        var second = await _resolver.UpsertReviewItemAsync("review-open-subject", "grp", "sig", "n");

        Assert.Equal(first.Id, second.Id);
        var all = await _resolver.ListAllReviewItemsAsync();
        Assert.Single(all, r => r.Fingerprint == first.Fingerprint);
    }
}
