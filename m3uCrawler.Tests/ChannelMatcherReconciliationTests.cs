using m3uCrawler.Models;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using Xunit;

namespace m3uCrawler.Tests;

public class ChannelMatcherReconciliationTests
{
    private static DiscoveredStream Stream(string title, string provider = "Provider_A", string group = "", bool working = true)
        => new(
            new M3uStream { Title = title, Url = $"https://{provider.ToLowerInvariant()}/{title}", IsWorking = working, Group = group },
            provider, "src");

    private static ChannelMatcher NewMatcher() => new(new AliasResolver());

    private static MatchPlan Build(
        IReadOnlyList<DiscoveredStream> discovered,
        params DispatcharrChannel[] channels)
    {
        var matcher = NewMatcher();
        return matcher.BuildPlan(
            discovered,
            new DispatcharrState(channels, Array.Empty<DispatcharrStream>(), Array.Empty<DispatcharrChannelGroup>(), null),
            MatchingOptions.Default,
            new StreamOrderingPolicy(),
            "x.m3u", "http://x", dryRun: true);
    }

    private static MatchPlan BuildWithStreams(
        IReadOnlyList<DiscoveredStream> discovered,
        IReadOnlyList<DispatcharrStream> existingStreams,
        params DispatcharrChannel[] channels)
    {
        var matcher = NewMatcher();
        return matcher.BuildPlan(
            discovered,
            new DispatcharrState(channels, existingStreams, Array.Empty<DispatcharrChannelGroup>(), null),
            MatchingOptions.Default,
            new StreamOrderingPolicy(),
            "x.m3u", "http://x", dryRun: true);
    }

    [Fact]
    public void Multiple_buckets_matching_same_channel_collapse_to_one_decision()
    {
        var existing = new[] { new DispatcharrChannel(3, "Sic", "Portugal", 100, null, Array.Empty<long>()) };
        var discovered = new[]
        {
            Stream("Sic", "Provider_A"),
            Stream("sic na", "Provider_B"),
            Stream("sic ◉", "Provider_C"),
            Stream("k sic", "Provider_D"),
        };
        var plan = Build(discovered, existing);

        var sicDecisions = plan.Channels
            .Where(c => c.ExistingChannelId == 3 && c.Outcome != SyncOutcome.Ambiguous)
            .ToList();

        Assert.Single(sicDecisions);
        var merged = sicDecisions[0];
        Assert.Equal(SyncOutcome.ExistingReassigned, merged.Outcome);
        Assert.Equal(4, merged.Streams.Count);
    }

    [Fact]
    public void Streams_kept_by_any_decision_are_not_marked_removed()
    {
        var existing = new[] { new DispatcharrChannel(3, "Sic", "Portugal", 100, null, new long[] { 22072 }) };
        var existingStreams = new[]
        {
            new DispatcharrStream(22072, "Sic FHD PT", "https://provider_a.example/sic-fhd-pt", null, "Portugal", "m3u", true, true, 100),
        };
        var discovered = new[]
        {
            Stream("Sic FHD PT", "Provider_A"),
            Stream("sic na", "Provider_B"),
            Stream("k sic", "Provider_C"),
        };
        var plan = BuildWithStreams(discovered, existingStreams, existing);

        var merged = plan.Channels.Single(c => c.ExistingChannelId == 3);
        Assert.Equal(3, merged.Streams.Count);
        Assert.Contains(merged.Streams, s => s.ExistingStreamId == 22072 && s.Outcome == SyncOutcome.ExistingUnchanged);
        Assert.DoesNotContain(merged.Streams, s => s.Outcome == SyncOutcome.Removed);
    }

    [Fact]
    public void New_streams_from_multiple_buckets_are_all_kept()
    {
        var existing = new[] { new DispatcharrChannel(3, "Sic", "Portugal", 100, null, Array.Empty<long>()) };
        var discovered = new[]
        {
            Stream("Sic", "Provider_A"),
            Stream("sic na", "Provider_B"),
            Stream("k sic", "Provider_C"),
            Stream("Sic v2", "Provider_A"),
        };
        var plan = Build(discovered, existing);

        var merged = plan.Channels.Single(c => c.ExistingChannelId == 3);
        Assert.Equal(4, merged.Streams.Count);
        Assert.Equal(4, merged.Streams.Count(s => s.Outcome == SyncOutcome.NewStream));
    }

    [Fact]
    public void Streams_in_existing_but_not_in_any_merged_bucket_are_removed()
    {
        var existing = new[]
        {
            new DispatcharrChannel(3, "Sic", "Portugal", 100, null, new long[] { 22072, 22100, 22101 }),
        };
        var existingStreams = new[]
        {
            new DispatcharrStream(22072, "Sic FHD PT", "https://provider_a.example/sic-fhd", null, "Portugal", "m3u", true, true, 100),
            new DispatcharrStream(22100, "Sic SD 1", "https://provider_a.example/sic-sd-1", null, "Portugal", "m3u", true, true, 100),
            new DispatcharrStream(22101, "Sic SD 2", "https://provider_a.example/sic-sd-2", null, "Portugal", "m3u", true, true, 100),
        };
        var discovered = new[]
        {
            Stream("Sic FHD PT", "Provider_A"),
            Stream("sic na", "Provider_B"),
        };
        var plan = BuildWithStreams(discovered, existingStreams, existing);

        var merged = plan.Channels.Single(c => c.ExistingChannelId == 3);
        Assert.Equal(4, merged.Streams.Count);

        Assert.Single(merged.Streams, s => s.Outcome == SyncOutcome.NewStream);
        Assert.Contains(merged.Streams, s => s.ExistingStreamId == 22072 && s.Outcome == SyncOutcome.ExistingUnchanged);
        Assert.Equal(2, merged.Streams.Count(s => s.Outcome == SyncOutcome.Removed));
        var removedIds = merged.Streams.Where(s => s.Outcome == SyncOutcome.Removed).Select(s => s.ExistingStreamId).ToHashSet();
        Assert.Contains(22100L, removedIds);
        Assert.Contains(22101L, removedIds);
        Assert.DoesNotContain(22072L, removedIds);
    }

    [Fact]
    public void Real_sic_tvi_cmtv_sport_tv_6_each_yield_exactly_one_decision()
    {
        var existing = new[]
        {
            new DispatcharrChannel(3, "Sic", "Portugal", 100, null, new long[] { 22072 }),
            new DispatcharrChannel(4, "Tvi", "Portugal", 101, null, new long[] { 22870 }),
            new DispatcharrChannel(28, "CMTV", "Portugal", 102, null, new long[] { 22244 }),
            new DispatcharrChannel(16, "Sport TV 6", "Sports", 103, null, new long[] { 22674 }),
        };
        var discovered = new[]
        {
            Stream("Sic", "Provider_A"),
            Stream("sic na", "Provider_B"),
            Stream("sic ◉", "Provider_C"),
            Stream("k sic", "Provider_D"),
            Stream("Tvi", "Provider_A"),
            Stream("tvi v+", "Provider_B"),
            Stream("v+ tvi", "Provider_C"),
            Stream("CMTV", "Provider_A"),
            Stream("cm tv", "Provider_B"),
            Stream("mtv", "Provider_C"),
            Stream("Sport TV 6", "Provider_A"),
            Stream("sporttv 6", "Provider_B"),
        };

        var plan = Build(discovered, existing);

        Assert.Single(plan.Channels, c => c.ExistingChannelId == 3);
        Assert.Single(plan.Channels, c => c.ExistingChannelId == 4);
        Assert.Single(plan.Channels, c => c.ExistingChannelId == 28);
        Assert.Single(plan.Channels, c => c.ExistingChannelId == 16);
    }

    [Fact]
    public void Independent_channels_remain_independent()
    {
        var existing = new[]
        {
            new DispatcharrChannel(3, "Sic", "Portugal", 100, null, Array.Empty<long>()),
            new DispatcharrChannel(11, "Sport TV 1", "Sports", 200, null, Array.Empty<long>()),
        };
        var discovered = new[]
        {
            Stream("Sic", "Provider_A"),
            Stream("Sport TV 1", "Provider_A"),
            Stream("sport tv 1", "Provider_B"),
        };
        var plan = Build(discovered, existing);

        Assert.Single(plan.Channels, c => c.ExistingChannelId == 3);
        Assert.Single(plan.Channels, c => c.ExistingChannelId == 11);

        var sic = plan.Channels.Single(c => c.ExistingChannelId == 3);
        Assert.Single(sic.Streams);

        var sportTv = plan.Channels.Single(c => c.ExistingChannelId == 11);
        Assert.Equal(2, sportTv.Streams.Count);
    }

    [Fact]
    public void Plan_with_only_new_channels_still_yields_one_decision_per_canonical_name()
    {
        var discovered = new[]
        {
            Stream("Brand New Channel", "Provider_A"),
            Stream("brand new channel", "Provider_B"),
            Stream("BRAND NEW CHANNEL", "Provider_C"),
        };
        var plan = Build(discovered);

        var newDecisions = plan.Channels.Where(c => c.Outcome == SyncOutcome.NewChannel).ToList();
        Assert.Single(newDecisions);
        Assert.Equal(3, newDecisions[0].Streams.Count);
    }

    [Fact]
    public void After_merge_no_existingchannelid_has_more_than_one_decision()
    {
        var existing = new[]
        {
            new DispatcharrChannel(3, "Sic", "Portugal", 100, null, new long[] { 22072 }),
            new DispatcharrChannel(4, "Tvi", "Portugal", 101, null, new long[] { 22870 }),
        };
        var discovered = new[]
        {
            Stream("Sic", "A"),
            Stream("sic na", "B"),
            Stream("k sic", "C"),
            Stream("Tvi", "A"),
            Stream("tvi v+", "B"),
            Stream("v+ tvi", "C"),
        };
        var plan = Build(discovered, existing);

        foreach (var grp in plan.Channels.GroupBy(c => c.ExistingChannelId).Where(g => g.Key.HasValue))
        {
            Assert.True(grp.Count() == 1,
                $"existingChannelId={grp.Key} appeared in {grp.Count()} decisions (expected 1)");
        }
    }

    [Fact]
    public void Removed_stream_kept_by_another_decision_is_not_marked_removed_after_merge()
    {
        var existing = new[]
        {
            new DispatcharrChannel(3, "Sic", "Portugal", 100, null, new long[] { 22072, 99999 }),
        };
        var existingStreams = new[]
        {
            new DispatcharrStream(22072, "Sic FHD PT", "https://provider_a.example/sic-fhd-pt", null, "Portugal", "m3u", true, true, 100),
            new DispatcharrStream(99999, "Sic OLD", "https://provider_a.example/sic-old", null, "Portugal", "m3u", true, true, 100),
        };
        var discovered = new[]
        {
            Stream("Sic FHD PT", "A"),
            Stream("sic na", "B"),
        };
        var plan = BuildWithStreams(discovered, existingStreams, existing);

        var merged = plan.Channels.Single(c => c.ExistingChannelId == 3);
        Assert.Equal(3, merged.Streams.Count);
        Assert.Contains(merged.Streams, s => s.ExistingStreamId == 22072 && s.Outcome == SyncOutcome.ExistingUnchanged);
        Assert.Contains(merged.Streams, s => s.ExistingStreamId == 99999 && s.Outcome == SyncOutcome.Removed);
    }

    [Fact]
    public void Merged_decision_carries_source_identities_in_identity_field()
    {
        var existing = new[] { new DispatcharrChannel(3, "Sic", "Portugal", 100, null, Array.Empty<long>()) };
        var discovered = new[]
        {
            Stream("Sic", "A"),
            Stream("sic na", "B"),
            Stream("k sic", "C"),
        };
        var plan = Build(discovered, existing);

        var merged = plan.Channels.Single(c => c.ExistingChannelId == 3);
        Assert.Contains("|", merged.Identity);
    }

    [Fact]
    public void Dazn_1_with_two_decisions_collapses()
    {
        var existing = new[] { new DispatcharrChannel(23, "Dazn 1", "Sports", 100, null, new long[] { 22162 }) };
        var discovered = new[]
        {
            Stream("Dazn 1", "A"),
            Stream("dazn 1 vip", "A"),
        };
        var plan = Build(discovered, existing);

        Assert.Single(plan.Channels, c => c.ExistingChannelId == 23);
        var merged = plan.Channels.Single(c => c.ExistingChannelId == 23);
        Assert.Equal(2, merged.Streams.Count);
    }
}