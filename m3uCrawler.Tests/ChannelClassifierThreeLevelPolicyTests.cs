using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Regression tests for the three-level classification / matching
/// policy introduced in B1.2-CLASSIFIER-2. The contract is:
///
///   exclude / match-existing / create-new
///
/// Scenarios A-G come from the brief and must remain stable.
/// </summary>
public class ChannelClassifierThreeLevelPolicyTests
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

    private static MatchPlan Build(
        DispatcharrState existing,
        params DiscoveredStream[] streams)
        => NewMatcher().BuildPlan(
            streams,
            existing,
            MatchingOptions.Default,
            new StreamOrderingPolicy(),
            "x.m3u",
            "http://x",
            dryRun: true);

    // A. PT - NO EVENT / BETCLIC: excluded, never NewChannel.
    [Fact]
    public void A_PT_NO_EVENT_with_BETCLIC_is_excluded_and_never_becomes_NewChannel()
    {
        var existing = new DispatcharrState(
            Array.Empty<DispatcharrChannel>(),
            Array.Empty<DispatcharrStream>(),
            Array.Empty<DispatcharrChannelGroup>(),
            null);
        var plan = Build(existing, Stream("PT - NO EVENT", "VIP | LIGA PORTUGAL BETCLIC"));

        Assert.Empty(plan.Channels);
        Assert.Single(plan.ClassifiedExclusions);
        Assert.Equal(ChannelKind.Vod, plan.ClassifiedExclusions[0].Kind);
        Assert.Equal("excluded", plan.ClassifiedExclusions[0].MatchingDisposition);
        Assert.Equal(0, plan.Counts.MatchingDisposition["newChannelsFromCuratedIdentity"]);
    }

    // B. Filmes 24/7 / bundle: excluded, never NewChannel.
    [Fact]
    public void B_Filmes_24_7_is_excluded_and_never_becomes_NewChannel()
    {
        var existing = new DispatcharrState(
            Array.Empty<DispatcharrChannel>(),
            Array.Empty<DispatcharrStream>(),
            Array.Empty<DispatcharrChannelGroup>(),
            null);
        var plan = Build(existing, Stream("Filmes Batman 24/7 ( Exclusivo ) PT", "Portugal - Canais 24-7"));

        Assert.Empty(plan.Channels);
        Assert.Single(plan.ClassifiedExclusions);
        Assert.Equal(ChannelKind.Bundle, plan.ClassifiedExclusions[0].Kind);
        Assert.Equal("excluded", plan.ClassifiedExclusions[0].MatchingDisposition);
    }

    // C. Curated channel: may become NewChannel.
    [Fact]
    public void C_Curated_channel_can_become_NewChannel()
    {
        var existing = new DispatcharrState(
            Array.Empty<DispatcharrChannel>(),
            Array.Empty<DispatcharrStream>(),
            Array.Empty<DispatcharrChannelGroup>(),
            null);
        var plan = Build(existing, Stream("RTP 1", "PORTUGUESE"));

        Assert.Single(plan.Channels);
        Assert.Equal(SyncOutcome.NewChannel, plan.Channels[0].Outcome);
        Assert.Equal(1, plan.Counts.MatchingDisposition["newChannelsFromCuratedIdentity"]);
    }

    // D. Legitimate channel not in ChannelCategoryLookup, but already
    //    in Dispatcharr: matched/updated, never dropped, never NewChannel.
    [Fact]
    public void D_Unknown_channel_already_in_Dispatcharr_is_updated_and_never_dropped()
    {
        // "Meo TV" é um canal PT real que NÃO está no
        // ChannelCategoryLookup curado mas já existe em Dispatcharr.
        // O matcher deve attachá-lo à decisão ExistingReassigned/ExistingUnchanged
        // (nunca dropped, nunca NewChannel).
        var existingChannel = new DispatcharrChannel(
            Id: 9001,
            Name: "Meo TV",
            GroupName: "PORTUGAL",
            ChannelNumber: 50,
            TvgId: null,
            StreamIds: new long[] { 1001, 1002 });
        var existingStreams = new[]
        {
            new DispatcharrStream(1001, "Meo TV", "https://provider_a.example/meo-old-1", null, "PORTUGAL", null, true, true, 50),
            new DispatcharrStream(1002, "Meo TV", "https://provider_a.example/meo-old-2", null, "PORTUGAL", null, true, true, 50),
        };
        var existing = new DispatcharrState(
            new[] { existingChannel },
            existingStreams,
            new[] { new DispatcharrChannelGroup(42, "PORTUGAL") },
            null);

        var plan = Build(existing, Stream("Meo TV", "PORTUGAL"));
        Assert.Single(plan.Channels);
        var ch = plan.Channels[0];
        Assert.Equal(9001L, ch.ExistingChannelId);
        Assert.True(ch.Outcome == SyncOutcome.ExistingReassigned || ch.Outcome == SyncOutcome.ExistingUnchanged);
        // Nunca NewChannel.
        Assert.NotEqual(SyncOutcome.NewChannel, ch.Outcome);
        // O stream existente 1001 partilha o nome (após normalize) com
        // o stream novo, por isso é preservado. O stream 1002 (URL
        // distinta) é removido por não estar no playlist actual.
        Assert.Contains(ch.Streams, s => s.ExistingStreamId == 1001 && s.Outcome == SyncOutcome.ExistingUnchanged);
        Assert.Contains(ch.Streams, s => s.ExistingStreamId == 1002 && s.Outcome == SyncOutcome.Removed);
    }

    // E. Unknown sem match no Dispatcharr: review-required, never NewChannel.
    [Fact]
    public void E_Unknown_without_existing_match_is_review_required()
    {
        var existing = new DispatcharrState(
            Array.Empty<DispatcharrChannel>(),
            Array.Empty<DispatcharrStream>(),
            Array.Empty<DispatcharrChannelGroup>(),
            null);
        var plan = Build(existing, Stream("Some Unknown Channel", "PORTUGAL"));

        Assert.Empty(plan.Channels);
        Assert.Single(plan.UnknownReviewRequired);
        Assert.Equal(ChannelKind.Unknown, plan.UnknownReviewRequired[0].Kind);
        Assert.Equal("unknown-review-required", plan.UnknownReviewRequired[0].MatchingDisposition);
        // Nunca NewChannel.
        Assert.Equal(0, plan.Counts.MatchingDisposition["newChannelsFromCuratedIdentity"]);
        Assert.Equal(1, plan.Counts.MatchingDisposition["unknownReviewRequired"]);
    }

    // F. Group/category name as title: review-required or excluded,
    //    never NewChannel.
    [Theory]
    [InlineData("PORTUGAL")]
    [InlineData("portuguese")]
    [InlineData("Filmes")]
    [InlineData("Default Group")]
    public void F_Group_or_category_name_as_title_is_excluded_or_review_required(string title)
    {
        var existing = new DispatcharrState(
            Array.Empty<DispatcharrChannel>(),
            Array.Empty<DispatcharrStream>(),
            Array.Empty<DispatcharrChannelGroup>(),
            null);
        var plan = Build(existing, Stream(title, "PORTUGAL"));

        // NUNCA NewChannel.
        Assert.DoesNotContain(plan.Channels, c => c.Outcome == SyncOutcome.NewChannel);
        // E o título aparece no diagnóstico, ou como exclusion (Group)
        // ou como review-required (Unknown editorial).
        var total = plan.ClassifiedExclusions.Count + plan.UnknownReviewRequired.Count;
        Assert.True(total >= 1, $"title='{title}' should be classified; got {total} diagnostics");
    }

    // G. Match ambíguo para Unknown: ambiguous/review-required, nunca
    //    escolhido arbitrariamente.
    [Fact]
    public void G_Unknown_with_ambiguous_existing_match_is_review_required()
    {
        // "Fox Sportz" (typo intencional) normaliza para "fox sportz",
        // que NÃO está no ChannelCategoryLookup, por isso cai em
        // Unknown com ExistingMatchEligibility=true. O fuzzy matcher
        // encontra dois candidatos próximos (Fox Sports 1 / Fox Sports
        // 2); o resultado deve ser review-required, nunca NewChannel,
        // e nunca escolha arbitrária.
        var existing = new DispatcharrState(
            new[]
            {
                new DispatcharrChannel(200, "Fox Sports 1", "Sports", 200, null, Array.Empty<long>()),
                new DispatcharrChannel(201, "Fox Sports 2", "Sports", 201, null, Array.Empty<long>()),
            },
            Array.Empty<DispatcharrStream>(),
            new[] { new DispatcharrChannelGroup(13, "Sports") },
            null);
        var plan = Build(existing, Stream("Fox Sportz", "Sports"));

        // NUNCA NewChannel.
        Assert.DoesNotContain(plan.Channels, c => c.Outcome == SyncOutcome.NewChannel);
        // O bucket Unknown cai em unknown-review-required.
        Assert.Contains(plan.UnknownReviewRequired, r => r.Title == "Fox Sportz");
        Assert.True(
            plan.Counts.MatchingDisposition["unknownReviewRequired"] >= 1,
            "ambiguous Unknown should be flagged for review, not selected arbitrarily");
    }
}
