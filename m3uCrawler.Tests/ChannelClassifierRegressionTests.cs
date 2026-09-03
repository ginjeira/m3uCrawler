using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Matcher integration test that runs against the **live production
/// playlist fixture** (captured 2026-08-31). Proves that the new
/// classification boundary prevents non-channel kinds from
/// becoming <see cref="SyncOutcome.NewChannel"/> in
/// <see cref="MatchPlan.Channels"/>, while legitimate channels still
/// match.
///
/// This is the regression that motivated the architectural fix:
/// `PT - NO EVENT` was reaching the matcher as a NewChannel because
/// the bundle-guard regex did not match its title.
/// </summary>
public class ChannelClassifierRegressionTests
{
    [Fact]
    public void Live_playlist_no_non_channel_kind_reaches_NewChannel()
    {
        var playlistPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "m3ucrawler_playlist_20260831_223914.m3u");
        Assert.True(File.Exists(playlistPath), $"Live playlist fixture missing: {playlistPath}");

        var content = File.ReadAllText(playlistPath);
        var streams = new M3uParserService().Parse(content);
        Assert.NotEmpty(streams);

        var discovered = streams
            .Select((s, i) => new DiscoveredStream(
                new M3uStream
                {
                    Title = s.Title,
                    Url = s.Url,
                    Group = s.Group,
                    Logo = s.Logo,
                    OriginalExtInf = s.OriginalExtInf,
                    IsWorking = true,
                    LastTested = s.LastTested,
                    ResponseTime = s.ResponseTime,
                },
                "live-fixture",
                $"line-{i}"))
            .ToList();

        var plan = new ChannelMatcher(new AliasResolver(null)).BuildPlan(
            discovered,
            new DispatcharrState(
                Array.Empty<DispatcharrChannel>(),
                Array.Empty<DispatcharrStream>(),
                Array.Empty<DispatcharrChannelGroup>(),
                null),
            MatchingOptions.Default,
            new StreamOrderingPolicy(),
            playlistPath,
            "http://x",
            dryRun: true);

        // Invariant: every ChannelDecision must have come from a Channel
        // classification. There is no way for a Bundle/Vod/LiveCam/Group/
        // Foreign/Unknown/Placeholder entry to reach NewChannel.
        Assert.NotEmpty(plan.Channels);
        foreach (var decision in plan.Channels)
        {
            Assert.NotNull(decision.Identity);
            Assert.NotEmpty(decision.Identity);
        }

        // Concrete regression: the playlist contains 8 occurrences of
        // "PT - NO EVENT" in group "VIP | LIGA PORTUGAL BETCLIC" which
        // must never become a NewChannel.
        var noEventDecisions = plan.Channels
            .Where(c => c.CanonicalName.Contains("NO EVENT", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(noEventDecisions);

        // No Filmes 24/7 entries either.
        var filmesDecisions = plan.Channels
            .Where(c => c.CanonicalName.Contains("Filmes ", StringComparison.OrdinalIgnoreCase)
                     && c.CanonicalName.Contains("24/7", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(filmesDecisions);

        // Exclusions record the work that was filtered out.
        Assert.NotEmpty(plan.ClassifiedExclusions);
        Assert.Contains(plan.ClassifiedExclusions, e => e.Title.Contains("NO EVENT", StringComparison.OrdinalIgnoreCase));
    }
}
