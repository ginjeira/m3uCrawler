using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Tests for the explicit ContentClassifier that runs BEFORE the
/// channel-matching pipeline. Classification decides whether an entry
/// is eligible to become a <see cref="ChannelDecision"/> at all.
/// Non-channel kinds (Group, Bundle, Vod, LiveCam, Category, Foreign,
/// Unknown, Placeholder) must NEVER produce NewChannel / NewStream /
/// ExistingReassigned decisions.
///
/// The architectural separation is:
///   DiscoveredStream
///     -> ContentClassifier.Classify(title, group)  <-- new boundary
///     -> if Kind == Channel: ResolveIdentity, bucket, match
///     -> if Kind != Channel: counted + diagnostic, no bucket
/// </summary>
public class ContentClassifierTests
{
    private static DiscoveredStream Stream(string title, string group, bool working = true)
        => new(
            new M3uStream
            {
                Title = title,
                Url = $"https://provider.test/{title}",
                IsWorking = working,
                ResponseTime = 100,
                Group = group,
            },
            "Provider_A",
            "src");

    private static ChannelMatcher NewMatcher() => new(new AliasResolver(null));

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
            "test.m3u",
            "http://x",
            dryRun: true);

    // ===================== Kind unit tests =====================

    [Theory]
    [InlineData("SIC", "PORTUGUESE")]
    [InlineData("SIC HD", "Portugal")]
    [InlineData("SIC NOTICIAS", "PORTUGUESE")]
    [InlineData("RTP 1", "PORTUGUESE")]
    [InlineData("RTP NOTICIAS", "Portugal")]
    [InlineData("TVI", "PORTUGUESE")]
    [InlineData("CMTV", "Portugal")]
    [InlineData("CNN PORTUGAL", "EU | PT | GENERAL")]
    [InlineData("SPORT TV 1", "SPORTS NETWORKS")]
    [InlineData("CNN Portugal", "TV")]
    public void Real_channel_is_classified_as_Channel(string title, string group)
    {
        var k = ContentClassifier.Classify(title, group);
        Assert.Equal(ChannelKind.Channel, k.Kind);
    }

    [Theory]
    [InlineData("Filmes Angelina Jolie 24/7 ( Exclusivo ) PT", "Portugal - Canais 24-7")]
    [InlineData("Combates UFC 24/7 ( Exclusivo ) PT", "Portugal - Canais 24-7")]
    [InlineData("Netflix Stand-up Comedy 24/7 ( Exclusivo ) PT", "Portugal - Canais 24-7")]
    [InlineData("Dragon Ball Filmes 24/7 ( Exclusivo ) PT", "Portugal - Canais 24-7")]
    [InlineData("SPORT TV PACK", "─ ✧･ﾟ|| PORTUGAL")]
    [InlineData("PACK CANAIS", "PORTUGUESE")]
    [InlineData("MEGA BUNDLE FILMES", "Portugal")]
    public void Bundle_title_pattern_is_classified_as_Bundle(string title, string group)
    {
        var k = ContentClassifier.Classify(title, group);
        Assert.Equal(ChannelKind.Bundle, k.Kind);
    }

    [Theory]
    [InlineData("PT - O Diabo Veste Prada 2 - 2026", "VOD | PORTUGAL")]
    [InlineData("PT - Algum Filme Sem Ano", "VOD | PORTUGAL")]
    [InlineData("PT - NO EVENT", "VIP | LIGA PORTUGAL BETCLIC")]
    [InlineData("PT - O Diabo Veste Prada 2 - 2026", "PT | FILMES")]
    public void Vod_title_or_group_is_classified_as_Vod(string title, string group)
    {
        var k = ContentClassifier.Classify(title, group);
        Assert.Equal(ChannelKind.Vod, k.Kind);
    }

    [Theory]
    [InlineData("LiveCam Nazaré | Praia do Norte PT", "Cameras")]
    [InlineData("LiveCam Lisboa HD", "Default Group")]
    public void LiveCam_keyword_is_classified_as_LiveCam(string title, string group)
    {
        var k = ContentClassifier.Classify(title, group);
        Assert.Equal(ChannelKind.LiveCam, k.Kind);
    }

    [Theory]
    [InlineData("#f#11ffff00###### PT - DOCUMENTARIOS #####", "EU | PT | DOCUMENTÁRIOS")]
    [InlineData("#00ff00ff####", "Default Group")]
    public void Colour_placeholder_is_classified_as_Placeholder(string title, string group)
    {
        var k = ContentClassifier.Classify(title, group);
        Assert.Equal(ChannelKind.Placeholder, k.Kind);
    }

    [Theory]
    [InlineData("EU | PT | ESPORTES", "EU | FR | SPORTS")] // group canonical but title is group name
    public void Title_equal_to_canonical_group_name_without_known_channel_identity_is_Unknown(string title, string group)
    {
        var k = ContentClassifier.Classify(title, group);
        // Title is just a group name, not a known channel identity.
        Assert.Equal(ChannelKind.Unknown, k.Kind);
    }

    [Theory]
    [InlineData("Some unknown stream", "Some random group")]
    [InlineData("", "")]
    [InlineData("xyz", "abc")]
    public void No_evidence_is_classified_as_Unknown(string title, string group)
    {
        var k = ContentClassifier.Classify(title, group);
        Assert.Equal(ChannelKind.Unknown, k.Kind);
    }

    // ===================== Matcher integration: non-Channel kinds must not become NewChannel =====================

    [Fact]
    public void PT_NO_EVENT_with_betclic_group_does_not_become_NewChannel()
    {
        // Concrete regression from production playlist 2026-08-31.
        // Title `PT - NO EVENT` is not a VOD year-format match, but
        // lives in a PPV/BETCLIC source group. Without classification,
        // it would resolve to a channel bucket and produce a NewChannel.
        var plan = BuildPlan(Stream("PT - NO EVENT", "VIP | LIGA PORTUGAL BETCLIC"));
        Assert.Empty(plan.Channels);
        Assert.Contains(plan.ClassifiedExclusions, e => e.Title == "PT - NO EVENT" && e.Kind == ChannelKind.Vod);
    }

    [Fact]
    public void Bundle_title_does_not_become_NewChannel_and_is_recorded_as_exclusion()
    {
        var plan = BuildPlan(Stream("Filmes Batman 24/7 ( Exclusivo ) PT", "Portugal - Canais 24-7"));
        Assert.Empty(plan.Channels);
        var ex = Assert.Single(plan.ClassifiedExclusions);
        Assert.Equal(ChannelKind.Bundle, ex.Kind);
        Assert.Equal("Filmes Batman 24/7 ( Exclusivo ) PT", ex.Title);
        Assert.Equal("Portugal - Canais 24-7", ex.Group);
    }

    [Fact]
    public void LiveCam_does_not_become_NewChannel()
    {
        var plan = BuildPlan(Stream("LiveCam Nazaré | Praia do Norte PT", "Cameras"));
        Assert.Empty(plan.Channels);
        Assert.Single(plan.ClassifiedExclusions);
        Assert.Equal(ChannelKind.LiveCam, plan.ClassifiedExclusions[0].Kind);
    }

    [Fact]
    public void VOD_entry_with_year_does_not_become_NewChannel()
    {
        var plan = BuildPlan(Stream("PT - O Diabo Veste Prada 2 - 2026", "VOD | PORTUGAL"));
        Assert.Empty(plan.Channels);
        Assert.Single(plan.ClassifiedExclusions);
        Assert.Equal(ChannelKind.Vod, plan.ClassifiedExclusions[0].Kind);
    }

    [Fact]
    public void Unknown_entry_with_no_existing_match_is_review_required()
    {
        // Unknown NUNCA gera NewChannel. Sem match no Dispatcharr
        // (a simulação não cria canais existentes), o bucket cai no
        // path MatchBand.None e é registado em UnknownReviewRequired.
        var plan = BuildPlan(Stream("xyz", "abc"));
        Assert.Empty(plan.Channels);
        Assert.Empty(plan.ClassifiedExclusions);
        Assert.Single(plan.UnknownReviewRequired);
        Assert.Equal(ChannelKind.Unknown, plan.UnknownReviewRequired[0].Kind);
        Assert.Equal("unknown-review-required", plan.UnknownReviewRequired[0].MatchingDisposition);
    }

    [Fact]
    public void Legitimate_channel_still_becomes_NewChannel()
    {
        var plan = BuildPlan(Stream("RTP 1", "Portugal"));
        Assert.Single(plan.Channels);
        Assert.Equal(SyncOutcome.NewChannel, plan.Channels[0].Outcome);
    }

    [Fact]
    public void Classification_counts_are_aggregated_in_match_plan()
    {
        var plan = BuildPlan(
            Stream("RTP 1", "PORTUGUESE"),                                  // Channel
            Stream("Filmes Batman 24/7 ( Exclusivo ) PT", "Portugal - Canais 24-7"), // Bundle
            Stream("PT - O Diabo Veste Prada 2 - 2026", "VOD | PORTUGAL"),  // Vod
            Stream("LiveCam Nazaré PT", "Cameras"),                        // LiveCam
            Stream("xyz", "abc"));                                          // Unknown

        Assert.Single(plan.Channels);
        // Three exclusions (Bundle, Vod, LiveCam) are EXCLUDED.
        Assert.Equal(3, plan.ClassifiedExclusions.Count);
        // The Unknown entry is review-required (no existing match).
        Assert.Single(plan.UnknownReviewRequired);
        Assert.Equal(1, plan.Counts.Classification["Channel"]);
        Assert.Equal(1, plan.Counts.Classification["Bundle"]);
        Assert.Equal(1, plan.Counts.Classification["Vod"]);
        Assert.Equal(1, plan.Counts.Classification["LiveCam"]);
        Assert.Equal(1, plan.Counts.Classification["Unknown"]);
        // Disposition counters split excluded vs review-required.
        Assert.Equal(3, plan.Counts.MatchingDisposition["excluded"]);
        Assert.Equal(1, plan.Counts.MatchingDisposition["unknownReviewRequired"]);
        Assert.Equal(0, plan.Counts.MatchingDisposition["unknownMatchedToExisting"]);
        Assert.Equal(1, plan.Counts.MatchingDisposition["newChannelsFromCuratedIdentity"]);
    }

    [Fact]
    public void Bundle_does_not_collide_with_legitimate_channel_in_same_plan()
    {
        var plan = BuildPlan(
            Stream("Filmes Batman 24/7 ( Exclusivo ) PT", "Portugal - Canais 24-7"),
            Stream("SPORT TV 1", "SPORTS NETWORKS"));

        Assert.Single(plan.Channels);
        Assert.Equal("SPORT TV 1", plan.Channels[0].CanonicalName);
        Assert.Equal(SyncOutcome.NewChannel, plan.Channels[0].Outcome);
        Assert.Single(plan.ClassifiedExclusions);
        Assert.Equal(ChannelKind.Bundle, plan.ClassifiedExclusions[0].Kind);
    }

    [Fact]
    public void Classified_exclusions_never_carry_credentials_or_urls()
    {
        var plan = BuildPlan(Stream("PT - NO EVENT", "VIP | LIGA PORTUGAL BETCLIC"));
        var ex = Assert.Single(plan.ClassifiedExclusions);
        // ClassifiedExclusion must only expose title, group, kind,
        // reason — never the stream URL or provider.
        var exposed = ex.GetType().GetProperties().Select(p => p.Name).ToHashSet();
        Assert.Contains("Title", exposed);
        Assert.Contains("Group", exposed);
        Assert.Contains("Kind", exposed);
        Assert.Contains("Reason", exposed);
        Assert.DoesNotContain("Url", exposed);
        Assert.DoesNotContain("Provider", exposed);
        Assert.DoesNotContain("StreamUrl", exposed);
    }
}
