using m3uCrawler.Models;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using Xunit;

namespace m3uCrawler.Tests;

public class ChannelMatcherAmbiguousGroupsTests
{
    private static DiscoveredStream Stream(string title, string provider = "Provider_A", string group = "", bool working = true)
        => new(
            new M3uStream { Title = title, Url = $"https://{provider.ToLowerInvariant()}/{title}", IsWorking = working, Group = group },
            provider, "src");

    private static ChannelMatcher NewMatcher() => new(new AliasResolver());

    private static MatchPlan Build(
        IReadOnlyList<DiscoveredStream> discovered,
        IReadOnlyList<DispatcharrChannelGroup> groups,
        params DispatcharrChannel[] channels)
    {
        var matcher = NewMatcher();
        return matcher.BuildPlan(
            discovered,
            new DispatcharrState(channels, Array.Empty<DispatcharrStream>(), groups, null),
            MatchingOptions.Default,
            new StreamOrderingPolicy(),
            "x.m3u", "http://x", dryRun: true);
    }

    [Fact]
    public void Two_groups_with_same_normalized_name_do_not_throw()
    {
        var groups = new[]
        {
            new DispatcharrChannelGroup(162, "PORTUGAL"),
            new DispatcharrChannelGroup(222, "Portugal"),
        };
        var ex = Record.Exception(() => Build(Array.Empty<DiscoveredStream>(), groups));
        Assert.Null(ex);
    }

    [Fact]
    public void Two_groups_with_same_normalized_name_are_reported_as_ambiguous()
    {
        var groups = new[]
        {
            new DispatcharrChannelGroup(162, "PORTUGAL"),
            new DispatcharrChannelGroup(222, "Portugal"),
        };
        var plan = Build(Array.Empty<DiscoveredStream>(), groups);

        Assert.NotNull(plan.AmbiguousGroups);
        Assert.Single(plan.AmbiguousGroups!);

        var entry = plan.AmbiguousGroups![0];
        Assert.Equal("portugal", entry.NormalizedName);
        Assert.Equal(new long[] { 162, 222 }, entry.GroupIds);
        Assert.Equal(new[] { "PORTUGAL", "Portugal" }, entry.GroupNames);
        Assert.Equal(1, plan.Counts.AmbiguousGroups);
    }

    [Fact]
    public void Matcher_does_not_select_either_162_or_222_for_portugal()
    {
        var groups = new[]
        {
            new DispatcharrChannelGroup(162, "PORTUGAL"),
            new DispatcharrChannelGroup(222, "Portugal"),
        };
        var discovered = new[] { Stream("RTP1", group: "Portugal") };
        var plan = Build(discovered, groups);

        Assert.Single(plan.Channels);
        var ch = plan.Channels[0];
        Assert.Equal(SyncOutcome.NewChannel, ch.Outcome);
        Assert.Null(ch.ExistingChannelId);
        Assert.Null(ch.ChannelGroupName);
    }

    [Fact]
    public void Other_groups_remain_indexed_and_matchable()
    {
        var groups = new[]
        {
            new DispatcharrChannelGroup(162, "PORTUGAL"),
            new DispatcharrChannelGroup(222, "Portugal"),
            new DispatcharrChannelGroup(13, "Sports"),
            new DispatcharrChannelGroup(2, "TV"),
        };
        var discovered = new[] { Stream("My Sport Channel", group: "Sports") };
        var plan = Build(discovered, groups);

        Assert.Single(plan.Channels);
        var ch = plan.Channels[0];
        Assert.Equal(SyncOutcome.NewChannel, ch.Outcome);
        Assert.Equal("Sports", ch.ChannelGroupName);

        Assert.Single(plan.AmbiguousGroups);
        Assert.Single(plan.AmbiguousGroups, g => g.NormalizedName == "portugal");
    }

    [Fact]
    public void Single_portugal_group_is_not_ambiguous()
    {
        var groups = new[] { new DispatcharrChannelGroup(222, "Portugal") };
        var discovered = new[] { Stream("RTP1", group: "Portugal") };
        var plan = Build(discovered, groups);

        Assert.Empty(plan.AmbiguousGroups!);
        Assert.Equal(0, plan.Counts.AmbiguousGroups);
        Assert.Single(plan.Channels);
        Assert.Equal(SyncOutcome.NewChannel, plan.Channels[0].Outcome);
        Assert.Equal("Portugal", plan.Channels[0].ChannelGroupName);
    }

    [Fact]
    public void Distinct_group_names_remain_unambiguous()
    {
        var groups = new[]
        {
            new DispatcharrChannelGroup(1, "Default Group"),
            new DispatcharrChannelGroup(2, "TV"),
            new DispatcharrChannelGroup(13, "Sports"),
            new DispatcharrChannelGroup(227, "PORTUGUESE"),
        };
        var discovered = new[]
        {
            Stream("Channel A", group: "TV"),
            Stream("Channel B", group: "Sports"),
            Stream("Channel C", group: "PORTUGUESE"),
            Stream("Channel D", group: "Default Group"),
        };
        var plan = Build(discovered, groups);

        Assert.Empty(plan.AmbiguousGroups!);
        Assert.Equal(0, plan.Counts.AmbiguousGroups);
        Assert.Equal(4, plan.Channels.Count);
        Assert.All(plan.Channels, c => Assert.Equal(SyncOutcome.NewChannel, c.Outcome));
        Assert.Contains(plan.Channels, c => c.ChannelGroupName == "TV");
        Assert.Contains(plan.Channels, c => c.ChannelGroupName == "Sports");
        Assert.Contains(plan.Channels, c => c.ChannelGroupName == "PORTUGUESE");
        Assert.Contains(plan.Channels, c => c.ChannelGroupName == "Default Group");
    }

    [Fact]
    public void Three_or_more_groups_with_same_normalized_name_are_all_reported()
    {
        var groups = new[]
        {
            new DispatcharrChannelGroup(100, "PORTUGAL"),
            new DispatcharrChannelGroup(200, "Portugal"),
            new DispatcharrChannelGroup(300, "portugal"),
        };
        var plan = Build(Array.Empty<DiscoveredStream>(), groups);

        Assert.Single(plan.AmbiguousGroups!);
        var entry = plan.AmbiguousGroups![0];
        Assert.Equal("portugal", entry.NormalizedName);
        Assert.Equal(new long[] { 100, 200, 300 }, entry.GroupIds);
        Assert.Equal(3, entry.GroupIds.Count);
    }

    [Fact]
    public void Existing_channel_match_is_unaffected_by_ambiguous_groups()
    {
        var groups = new[]
        {
            new DispatcharrChannelGroup(162, "PORTUGAL"),
            new DispatcharrChannelGroup(222, "Portugal"),
        };
        var existing = new[]
        {
            new DispatcharrChannel(42, "Sport TV 1", "Sports", 100, null, Array.Empty<long>()),
        };
        var matcher = NewMatcher();
        var plan = matcher.BuildPlan(
            new[] { Stream("Sport TV 1") },
            new DispatcharrState(existing, Array.Empty<DispatcharrStream>(), groups, null),
            MatchingOptions.Default, new StreamOrderingPolicy(), "x.m3u", "http://x", dryRun: true);

        Assert.Single(plan.Channels);
        Assert.Equal(42, plan.Channels[0].ExistingChannelId);
        Assert.Equal("Sports", plan.Channels[0].ChannelGroupName);
        Assert.Single(plan.AmbiguousGroups!);
    }
}