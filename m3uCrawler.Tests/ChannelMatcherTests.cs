using m3uCrawler.Models;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using m3uCrawler.Services.Sync;
using Xunit;

namespace m3uCrawler.Tests;

public class ChannelMatcherTests
{
    private static DiscoveredStream Stream(string title, string provider = "Provider_A", string group = "", bool working = true, double rt = 100)
        => new(
            new M3uStream { Title = title, Url = $"https://{provider.ToLowerInvariant()}/{title}", IsWorking = working, ResponseTime = rt, Group = group },
            provider, "src");

    private static ChannelMatcher NewMatcher(IDictionary<string, string>? aliases = null)
        => new(new AliasResolver(aliases != null
            ? aliases.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            : null));

    [Fact]
    public void Sport_tv_variants_converge_to_one_channel()
    {
        var matcher = NewMatcher();
        var discovered = new[]
        {
            Stream("PT | SPORT TV 1 HD", "Provider_A"),
            Stream("Sport TV1", "Provider_B"),
            Stream("SPORT TV 1", "Provider_C"),
            Stream("Sport TV 1 FHD", "Provider_D"),
        };
        var plan = matcher.BuildPlan(
            discovered,
            new DispatcharrState(Array.Empty<DispatcharrChannel>(), Array.Empty<DispatcharrStream>(), Array.Empty<DispatcharrChannelGroup>(), null),
            MatchingOptions.Default,
            new StreamOrderingPolicy(),
            "test.m3u", "http://x", dryRun: true);

        Assert.Single(plan.Channels);
        var ch = plan.Channels[0];
        Assert.Equal(4, ch.Streams.Count);
        Assert.All(ch.Streams, s => Assert.Equal(SyncOutcome.NewStream, s.Outcome));
        Assert.Equal(SyncOutcome.NewChannel, ch.Outcome);
    }

    [Fact]
    public void Existing_channel_with_normalized_name_is_matched()
    {
        var matcher = NewMatcher();
        var existing = new[]
        {
            new DispatcharrChannel(42, "CNN International", "News", 101, null, new long[] { 7 }),
        };
        var discovered = new[] { Stream("cnn-international", "Provider_A") };
        var plan = matcher.BuildPlan(
            discovered, new DispatcharrState(existing, Array.Empty<DispatcharrStream>(), Array.Empty<DispatcharrChannelGroup>(), null),
            MatchingOptions.Default, new StreamOrderingPolicy(), "x.m3u", "http://x", dryRun: true);

        Assert.Single(plan.Channels);
        var ch = plan.Channels[0];
        Assert.Equal(42, ch.ExistingChannelId);
        Assert.True(ch.Outcome == SyncOutcome.ExistingUnchanged || ch.Outcome == SyncOutcome.ExistingReassigned);
        Assert.Single(ch.Streams);
    }

    [Fact]
    public void Ambiguous_when_two_close_candidates_exist()
    {
        var matcher = NewMatcher();
        var existing = new[]
        {
            new DispatcharrChannel(7, "Fox Sports 1", "Sports", 200, null, Array.Empty<long>()),
            new DispatcharrChannel(8, "Fox Sports 2", "Sports", 201, null, Array.Empty<long>()),
        };
        var discovered = new[] { Stream("Fox Sports") };
        var plan = matcher.BuildPlan(
            discovered, new DispatcharrState(existing, Array.Empty<DispatcharrStream>(), Array.Empty<DispatcharrChannelGroup>(), null),
            MatchingOptions.Default, new StreamOrderingPolicy(), "x.m3u", "http://x", dryRun: true);

        Assert.Single(plan.Channels);
        Assert.Equal(SyncOutcome.Ambiguous, plan.Channels[0].Outcome);
        Assert.Equal(2, plan.Channels[0].AmbiguousCandidates.Count);
    }

    [Fact]
    public void Removed_streams_are_listed_for_existing_channel()
    {
        var matcher = NewMatcher();
        var existing = new[]
        {
            new DispatcharrChannel(42, "Sport TV 1", "Sports", 100, null, new long[] { 7, 8 }),
        };
        var existingStreams = new[]
        {
            new DispatcharrStream(7, "old", "https://provider_a.example/old", null, "Sports", null, true, true, 100),
            new DispatcharrStream(8, "Sport TV 1", "https://provider_a.example/sport-tv-1", null, "Sports", null, true, true, 100),
        };
        var discovered = new[] { Stream("Sport TV 1", "Provider_A", group: "Sports") };
        var plan = matcher.BuildPlan(
            discovered,
            new DispatcharrState(existing, existingStreams, Array.Empty<DispatcharrChannelGroup>(), null),
            MatchingOptions.Default, new StreamOrderingPolicy(), "x.m3u", "http://x", dryRun: true);

        var ch = plan.Channels[0];
        Assert.Contains(ch.Streams, s => s.Outcome == SyncOutcome.Removed && s.ExistingStreamId == 7);
        Assert.Contains(ch.Streams, s => s.ExistingStreamId == 8 && s.Outcome == SyncOutcome.ExistingUnchanged);
    }

    [Fact]
    public void Below_threshold_creates_new_channel()
    {
        var matcher = NewMatcher();
        var existing = new[] { new DispatcharrChannel(99, "Completely Different Network XYZ", null, 999, null, Array.Empty<long>()) };
        var discovered = new[] { Stream("RTP1") };
        var plan = matcher.BuildPlan(
            discovered,
            new DispatcharrState(existing, Array.Empty<DispatcharrStream>(), Array.Empty<DispatcharrChannelGroup>(), null),
            MatchingOptions.Default, new StreamOrderingPolicy(), "x.m3u", "http://x", dryRun: true);
        Assert.Equal(SyncOutcome.NewChannel, plan.Channels[0].Outcome);
    }

    [Fact]
    public void Plan_is_deterministic_for_same_inputs()
    {
        var matcher = NewMatcher();
        var discovered = new[]
        {
            Stream("SPORT TV 1", "A"),
            Stream("Sport TV 1 FHD", "B"),
            Stream("RTP1", "C"),
        };
        var opts = MatchingOptions.Default;
        var ord = new StreamOrderingPolicy();
        var fixedNow = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc);
        var plan1 = matcher.BuildPlan(discovered, new DispatcharrState(Array.Empty<DispatcharrChannel>(), Array.Empty<DispatcharrStream>(), Array.Empty<DispatcharrChannelGroup>(), null), opts, ord, "p.m3u", "u", true, fixedNow);
        var plan2 = matcher.BuildPlan(discovered, new DispatcharrState(Array.Empty<DispatcharrChannel>(), Array.Empty<DispatcharrStream>(), Array.Empty<DispatcharrChannelGroup>(), null), opts, ord, "p.m3u", "u", true, fixedNow);
        Assert.Equal(MatchPlanSerializer.Serialize(plan1), MatchPlanSerializer.Serialize(plan2));
    }
}

public class MatchPlanSerializerTests
{
    [Fact]
    public void Round_trip_preserves_counts_and_outcomes()
    {
        var plan = new MatchPlan
        {
            GeneratedAtUtc = "2026-08-30T00:00:00Z",
            SourcePlaylistPath = "x.m3u",
            DispatcharrBaseUrl = "http://x",
            DryRun = true,
            MatchThreshold = 80,
            Counts = new SyncReportCounts { Matched = 5, NewChannels = 2, NewStreams = 3, Skipped = 1, Ambiguous = 1, Unchanged = 4 },
            Channels = new[]
            {
                new ChannelDecision { Identity = "rtp1", CanonicalName = "RTP1", Outcome = SyncOutcome.NewChannel, MatchScore = 0, MatchReason = "no-match",
                    Streams = new[] { new StreamMatchDecision { Provider = "A", StreamUrl = "http://x/r", StreamName = "RTP1", Outcome = SyncOutcome.NewStream, ProposedOrder = 0, OrderReason = "init", IsWorking = true } },
                    AmbiguousCandidates = Array.Empty<AmbiguousCandidate>() },
                new ChannelDecision { Identity = "cnn", CanonicalName = "CNN", Outcome = SyncOutcome.Ambiguous, MatchScore = 88, MatchReason = "fuzzy",
                    Streams = Array.Empty<StreamMatchDecision>(),
                    AmbiguousCandidates = new[] { new AmbiguousCandidate { ExistingChannelId = 1, ExistingChannelName = "CNN Intl", Score = 88, Reason = "fuzzy" } } }
            }
        };
        var json = MatchPlanSerializer.Serialize(plan);
        var back = MatchPlanSerializer.Deserialize(json);
        Assert.NotNull(back);
        Assert.Equal(plan.Counts.Matched, back!.Counts.Matched);
        Assert.Equal(plan.Counts.Ambiguous, back.Counts.Ambiguous);
        Assert.Equal(plan.Channels[1].AmbiguousCandidates.Count, back.Channels[1].AmbiguousCandidates.Count);
    }

    [Fact]
    public void Stream_urls_are_sanitized_in_serialized_output()
    {
        var plan = new MatchPlan
        {
            SourcePlaylistPath = "x.m3u",
            Channels = new[]
            {
                new ChannelDecision
                {
                    Identity = "x", CanonicalName = "X", Outcome = SyncOutcome.NewChannel,
                    Streams = new[]
                    {
                        new StreamMatchDecision
                        {
                            Provider = "A", StreamName = "X", StreamUrl = "http://alice:secret@host.example.com/live/alice/secret/1.ts",
                            Outcome = SyncOutcome.NewStream, ProposedOrder = 0, OrderReason = "init", IsWorking = true
                        }
                    },
                    AmbiguousCandidates = Array.Empty<AmbiguousCandidate>()
                }
            }
        };
        var json = MatchPlanSerializer.Serialize(plan);
        Assert.DoesNotContain("secret", json);
        Assert.Contains("***", json);
    }
}
