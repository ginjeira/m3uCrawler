using m3uCrawler.Models;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Tests for the bundle / VOD / LiveCam / colour-placeholder guard in
/// ChannelMatcher.BuildPlan.
///
/// Rationale: the report
/// `.kilo/plans/1788214551330-channel-normalization-investigation-report.md`
/// identified four classes of "non-channel" entries that currently
/// reach MatchPlan as if they were channels:
///   - "Filmes X 24/7 ( Exclusivo ) PT" (76 entries, group
///     "Portugal - Canais 24-7")
///   - "Combates UFC 24/7 ( Exclusivo ) PT" and other 24/7 bundles
///   - "PT - <título> - <ano>" VOD entries (252, group
///     "VOD | PORTUGAL")
///   - "LiveCam <praia> PT" feeds (47)
///   - "#f#11ffff00..." colour placeholders
///
/// These should never reach `NewChannel`, `ExistingReassigned`, or
/// `NewStream`. They must be excluded from the bucket entirely so
/// that no apply decision is produced for them.
/// </summary>
public class ChannelMatcherBundleGuardTests
{
    private static DiscoveredStream Stream(
        string title,
        string group = "",
        string provider = "Provider_A",
        bool working = true)
        => new(
            new M3uStream
            {
                Title = title,
                Url = $"https://{provider.ToLowerInvariant()}/{title}",
                IsWorking = working,
                ResponseTime = 100,
                Group = group,
            },
            provider, "src");

    private static ChannelMatcher NewMatcher()
        => new(new AliasResolver(null));

    private static MatchPlan BuildPlan(params DiscoveredStream[] streams)
        => NewMatcher().BuildPlan(
            streams,
            new DispatcharrState(
                Array.Empty<DispatcharrChannel>(),
                Array.Empty<DispatcharrStream>(),
                Array.Empty<DispatcharrChannelGroup>(),
                null),
            MatchingOptions.Default,
            new StreamOrderingPolicy(),
            "test.m3u", "http://x", dryRun: true);

    // ----- Bundles rotativos ("Filmes X 24/7", "Combates UFC 24/7", etc.) -----

    [Fact]
    public void Filmes_24_7_title_is_excluded_from_match_plan()
    {
        var plan = BuildPlan(Stream(
            "Filmes Bruce Willis 24/7 ( Exclusivo ) PT",
            group: "Portugal - Canais 24-7"));

        Assert.Empty(plan.Channels);
    }

    [Fact]
    public void Combates_24_7_title_is_excluded_from_match_plan()
    {
        var plan = BuildPlan(Stream(
            "Combates UFC 24/7 ( Exclusivo ) PT",
            group: "Portugal - Canais 24-7"));

        Assert.Empty(plan.Channels);
    }

    [Fact]
    public void Netflix_24_7_title_is_excluded_from_match_plan()
    {
        var plan = BuildPlan(Stream(
            "Netflix Stand-up Comedy 24/7 ( Exclusivo ) PT",
            group: "Portugal - Canais 24-7"));

        Assert.Empty(plan.Channels);
    }

    [Fact]
    public void Dragon_Ball_Filmes_24_7_is_excluded_from_match_plan()
    {
        var plan = BuildPlan(Stream(
            "Dragon Ball Filmes 24/7 ( Exclusivo ) PT",
            group: "Portugal - Canais 24-7"));

        Assert.Empty(plan.Channels);
    }

    // ----- VOD entries ("PT - <título> - <ano>", group "VOD | PORTUGAL") -----

    [Fact]
    public void VOD_entry_with_year_is_excluded_from_match_plan()
    {
        var plan = BuildPlan(Stream(
            "PT - O Diabo Veste Prada 2 - 2026",
            group: "VOD | PORTUGAL"));

        Assert.Empty(plan.Channels);
    }

    [Fact]
    public void VOD_group_alone_is_enough_to_exclude()
    {
        // Mesmo sem o padrão "PT - <título> - <ano>" no título, o
        // grupo "VOD | PORTUGAL" deve ser suficiente para excluir a
        // entrada, porque o grupo é uma categoria de arquivo, não um
        // canal ao vivo.
        var plan = BuildPlan(Stream(
            "Algum Filme Sem Ano",
            group: "VOD | PORTUGAL"));

        Assert.Empty(plan.Channels);
    }

    // ----- LiveCam feeds -----

    [Fact]
    public void LiveCam_title_is_excluded_from_match_plan()
    {
        var plan = BuildPlan(Stream(
            "LiveCam Nazaré | Praia do Norte PT",
            group: "Default Group"));

        Assert.Empty(plan.Channels);
    }

    // ----- Colour placeholders -----

    [Fact]
    public void Colour_placeholder_title_is_excluded_from_match_plan()
    {
        var plan = BuildPlan(Stream(
            "#f#11ffff00###### PT - DOCUMENTARIOS #####",
            group: "EU | PT | DOCUMENTÁRIOS"));

        Assert.Empty(plan.Channels);
    }

    // ----- SPORT TV PACK -----

    [Fact]
    public void SPORT_TV_PACK_title_is_excluded_from_match_plan()
    {
        var plan = BuildPlan(Stream(
            "SPORT TV PACK",
            group: "─ ✧･ﾟ|| PORTUGAL"));

        Assert.Empty(plan.Channels);
    }

    // ----- Regressões: canais legítimos NÃO devem ser filtrados -----

    [Fact]
    public void Legitimate_channel_is_NOT_excluded()
    {
        var plan = BuildPlan(Stream("RTP 1", group: "Portugal"));

        Assert.Single(plan.Channels);
        Assert.Equal(SyncOutcome.NewChannel, plan.Channels[0].Outcome);
    }

    [Fact]
    public void Legitimate_sport_channel_is_NOT_excluded()
    {
        var plan = BuildPlan(Stream("SPORT TV 1", group: "Sports"));

        Assert.Single(plan.Channels);
        Assert.Equal(SyncOutcome.NewChannel, plan.Channels[0].Outcome);
    }

    [Fact]
    public void Legitimate_tv_cine_channel_is_NOT_excluded()
    {
        var plan = BuildPlan(Stream("TV CINE ACTION", group: "─ ✧･ﾟ|| PORTUGAL VIP"));

        Assert.Single(plan.Channels);
        Assert.Equal(SyncOutcome.NewChannel, plan.Channels[0].Outcome);
    }

    [Fact]
    public void Legitimate_cnn_portugal_is_NOT_excluded()
    {
        var plan = BuildPlan(Stream("CNN Portugal", group: "TV"));

        Assert.Single(plan.Channels);
        Assert.Equal(SyncOutcome.NewChannel, plan.Channels[0].Outcome);
    }

    // ----- Mix: bundles não devem contaminar buckets vizinhos -----

    [Fact]
    public void Bundle_does_not_collide_with_legitimate_channel_in_same_plan()
    {
        var plan = BuildPlan(
            Stream("Filmes Batman 24/7 ( Exclusivo ) PT", group: "Portugal - Canais 24-7"),
            Stream("SPORT TV 1", group: "Sports"));

        Assert.Single(plan.Channels);
        var ch = plan.Channels[0];
        Assert.Equal("SPORT TV 1", ch.CanonicalName);
        Assert.Equal(SyncOutcome.NewChannel, ch.Outcome);
        Assert.Single(ch.Streams);
        Assert.Equal(SyncOutcome.NewStream, ch.Streams[0].Outcome);
    }
}
