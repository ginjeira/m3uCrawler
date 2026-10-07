using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Wave B — quando um bucket resolve para um canal canónico que tem
/// grupo, o <c>ChannelGroupName</c> publicado no Dispatcharr é o
/// <c>DisplayName</c> desse grupo canónico (source-independent). Sem
/// resolução canónica, ou com canal canónico sem grupo, mantém-se o
/// comportamento histórico (grupo derivado da fonte via
/// <c>ResolveGroupName</c> / <c>matched.GroupName</c>).
///
/// O nome do grupo é apenas apresentação — a identidade continua a ser
/// <c>CanonicalChannelKey</c>/Id (nunca a string).
/// </summary>
public class ChannelMatcherCanonicalGroupNameTests
{
    // ──────────────────────────────── helpers ────────────────────────────────

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), $"waveb-group-{Guid.NewGuid():N}.db");

    private static async Task<(string DbPath, CatalogResolver Resolver)> NewCatalogAsync()
    {
        var dbPath = NewDbPath();
        var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        return (dbPath, new CatalogResolver(new TestDbContextFactory(dbPath), dbPath));
    }

    private static Task<CanonicalChannelEntity> CreateCanonicalAsync(
        CatalogResolver resolver,
        string key,
        string displayName,
        string groupKey,
        PublicationPolicy policy = PublicationPolicy.CreateEligible)
        => resolver.CreateCanonicalChannelAsync(
            key, displayName, EditorialCategory.Live, groupKey, policy,
            isEnabled: true, normalizedAliases: Array.Empty<string>(), country: "pt");

    private static async Task ClearGroupAsync(string dbPath, long canonicalChannelId)
    {
        await using var ctx = new TestDbContextFactory(dbPath).CreateDbContext();
        var tracked = await ctx.CanonicalChannels.SingleAsync(c => c.Id == canonicalChannelId);
        tracked.GroupId = null;
        await ctx.SaveChangesAsync();
    }

    private static ChannelMatcher MatcherWithCatalog(CatalogResolver catalog) =>
        new(new AliasResolver(null), resolutionPolicy: null, catalog: catalog);

    private static ChannelMatcher MatcherWithoutCatalog() =>
        new(new AliasResolver(null));

    private static DiscoveredStream Stream(string title, string group, int index)
        => new(
            new M3uStream
            {
                Title = title,
                Url = $"http://x/{title.Replace(' ', '_')}/{index}",
                Group = group,
                IsWorking = true,
                ResponseTime = 100,
            },
            "P", "src");

    private static DispatcharrState State(
        IReadOnlyList<DispatcharrChannel>? channels = null,
        IReadOnlyList<DispatcharrChannelGroup>? groups = null)
        => new(
            channels ?? Array.Empty<DispatcharrChannel>(),
            Array.Empty<DispatcharrStream>(),
            groups ?? Array.Empty<DispatcharrChannelGroup>(),
            "0.30.0");

    private static MatchPlan Build(
        ChannelMatcher matcher,
        IReadOnlyList<DiscoveredStream> discovered,
        DispatcharrState existing)
        => matcher.BuildPlan(
            discovered, existing, MatchingOptions.Default,
            new StreamOrderingPolicy(), "x.m3u", "http://dispatcharr.local", dryRun: true);

    // ─────────────────────────── (a) same canonical identity ─────────────────

    [Fact]
    public async Task NewChannel_uses_canonical_group_DisplayName_over_source_group()
    {
        var (dbPath, resolver) = await NewCatalogAsync();
        try
        {
            await CreateCanonicalAsync(
                resolver, "wavebalfa", "WaveB Alfa", CanonicalGroupKeys.PortugalDesporto);

            var matcher = MatcherWithCatalog(resolver);
            var plan = Build(
                matcher,
                new[]
                {
                    Stream("wavebalfa", "Generalistas HD", 0),
                    Stream("wavebalfa", "Portugal", 1),
                },
                State());

            var decision = Assert.Single(plan.Channels);
            Assert.Equal(SyncOutcome.NewChannel, decision.Outcome);
            Assert.Equal(2, decision.Streams.Count);
            // Ambos os streams (source groups distintos) partilham o
            // grupo canónico — nunca o group-title da fonte.
            Assert.Equal("PortugalDesporto", decision.ChannelGroupName);
            Assert.DoesNotContain("Generalistas", decision.ChannelGroupName);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task NewChannel_canonical_group_is_independent_of_stream_order()
    {
        var (dbPath, resolver) = await NewCatalogAsync();
        try
        {
            await CreateCanonicalAsync(
                resolver, "wavebalfa", "WaveB Alfa", CanonicalGroupKeys.PortugalDesporto);

            var matcher = MatcherWithCatalog(resolver);
            var forward = Build(
                matcher,
                new[]
                {
                    Stream("wavebalfa", "Generalistas HD", 0),
                    Stream("wavebalfa", "Portugal", 1),
                },
                State());
            var reversed = Build(
                matcher,
                new[]
                {
                    Stream("wavebalfa", "Portugal", 0),
                    Stream("wavebalfa", "Generalistas HD", 1),
                },
                State());

            Assert.Equal("PortugalDesporto", forward.Channels.Single().ChannelGroupName);
            Assert.Equal(
                forward.Channels.Single().ChannelGroupName,
                reversed.Channels.Single().ChannelGroupName);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    // ─────────────────────────── (b) no canonical resolution ─────────────────

    [Fact]
    public void NewChannel_without_catalog_keeps_source_derived_group()
    {
        // Sem resolução canónica (matcher legacy, sem catálogo): o grupo
        // continua a derivar da fonte via ResolveGroupName.
        var matcher = MatcherWithoutCatalog();
        var plan = Build(
            matcher,
            new[] { Stream("RTP 1", "Portugal", 0) },
            State(groups: new[] { new DispatcharrChannelGroup(222, "Portugal") }));

        var decision = Assert.Single(plan.Channels);
        Assert.Equal(SyncOutcome.NewChannel, decision.Outcome);
        Assert.Equal("Portugal", decision.ChannelGroupName);
    }

    // ─────────────────────── (c) canonical channel without group ─────────────

    [Fact]
    public async Task NewChannel_with_canonical_channel_without_group_falls_back_to_source_group()
    {
        var (dbPath, resolver) = await NewCatalogAsync();
        try
        {
            var channel = await CreateCanonicalAsync(
                resolver, "wavebalfa", "WaveB Alfa", CanonicalGroupKeys.PortugalDesporto);
            // Remove o grupo: canal canónico sem GroupId.
            await ClearGroupAsync(dbPath, channel.Id);

            var matcher = MatcherWithCatalog(resolver);
            var plan = Build(
                matcher,
                new[] { Stream("wavebalfa", "Portugal", 0) },
                State());

            var decision = Assert.Single(plan.Channels);
            Assert.Equal(SyncOutcome.NewChannel, decision.Outcome);
            Assert.Equal("Portugal", decision.ChannelGroupName);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    // ─────────────────────────── (d) existing channel match ──────────────────

    [Fact]
    public async Task ExistingChannel_canonical_group_overrides_matched_group()
    {
        var (dbPath, resolver) = await NewCatalogAsync();
        try
        {
            await CreateCanonicalAsync(
                resolver, "wavebalfa", "WaveB Alfa", CanonicalGroupKeys.PortugalDesporto);

            var existing = new[]
            {
                new DispatcharrChannel(
                    100, "wavebalfa", "FonteGrupo", 1, null, Array.Empty<long>()),
            };
            var matcher = MatcherWithCatalog(resolver);
            var plan = Build(
                matcher,
                new[] { Stream("wavebalfa", "Portugal", 0) },
                State(channels: existing));

            var decision = Assert.Single(plan.Channels);
            Assert.Equal(100, decision.ExistingChannelId);
            Assert.Equal("PortugalDesporto", decision.ChannelGroupName);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task ExistingChannel_with_canonical_channel_without_group_falls_back_to_matched_group()
    {
        var (dbPath, resolver) = await NewCatalogAsync();
        try
        {
            var channel = await CreateCanonicalAsync(
                resolver, "wavebalfa", "WaveB Alfa", CanonicalGroupKeys.PortugalDesporto);
            await ClearGroupAsync(dbPath, channel.Id);

            var existing = new[]
            {
                new DispatcharrChannel(
                    100, "wavebalfa", "FonteGrupo", 1, null, Array.Empty<long>()),
            };
            var matcher = MatcherWithCatalog(resolver);
            var plan = Build(
                matcher,
                new[] { Stream("wavebalfa", "Portugal", 0) },
                State(channels: existing));

            var decision = Assert.Single(plan.Channels);
            Assert.Equal(100, decision.ExistingChannelId);
            Assert.Equal("FonteGrupo", decision.ChannelGroupName);
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }
}
