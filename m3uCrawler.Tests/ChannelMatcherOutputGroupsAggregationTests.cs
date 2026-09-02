using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using Xunit;

namespace m3uCrawler.Tests;

public class ChannelMatcherOutputGroupsAggregationTests
{
    private static DiscoveredStream Stream(string title, string group)
    {
        var m3u = new M3uStream
        {
            Title = title,
            Url = $"http://x/{title.Replace(' ', '_')}",
            Group = group,
            IsWorking = true,
            ResponseTime = 100,
        };
        return new DiscoveredStream(m3u, "P", "src");
    }

    private static ChannelMatcher NewMatcher()
    {
        return new ChannelMatcher(
            new AliasResolver(null),
            ResolutionPolicy.Resolve);
    }

    private static MatchPlan BuildPlan(
        ChannelMatcher matcher,
        params DiscoveredStream[] streams)
        => matcher.BuildPlan(
            streams.ToList(),
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

    [Fact]
    public void Counts_OutputGroups_aggregates_OutputGroup_per_ChannelDecision()
    {
        var matcher = NewMatcher();
        var plan = BuildPlan(
            matcher,
            Stream("SIC", "eu | pt | general"),
            Stream("RTP 1", "eu | pt | documentarios"),
            Stream("TVI", "portugal - canais 24-7"),
            Stream("Canal BENELUX", "eu | belgium"),
            Stream("Canal LATAM", "am | latino"));

        var og = plan.Counts.OutputGroups;
        Assert.NotNull(og);

        var present = og.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal(1, present[OutputGroupKind.PortugalLive.ToString()]);
        Assert.Equal(1, present[OutputGroupKind.PortugalDocumentarios.ToString()]);
        Assert.Equal(1, present[OutputGroupKind.PortugalFilmes24_7.ToString()]);
        Assert.Equal(2, present[OutputGroupKind.Foreign.ToString()]);
    }

    [Fact]
    public void Counts_OutputGroups_is_empty_when_no_streams_produce_a_decision()
    {
        var matcher = new ChannelMatcher(new AliasResolver(null));
        var plan = BuildPlan(matcher);

        Assert.NotNull(plan.Counts.OutputGroups);
        Assert.Empty(plan.Counts.OutputGroups);
    }

    [Fact]
    public void Counts_OutputGroups_keys_are_alphabetical()
    {
        var matcher = NewMatcher();
        var plan = BuildPlan(
            matcher,
            Stream("SIC", "eu | pt | general"),
            Stream("Canal LATAM", "am | latino"));

        var keys = plan.Counts.OutputGroups.Keys.ToList();
        var sorted = keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, keys);
    }
}
