using System.Text.Json;
using System.Text.Json.Serialization;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Validation harness: drives the production ChannelMatcher.BuildPlan
/// against the **actual live m3uCrawler playlist** captured at
/// 2026-08-31 20:45:29 (test data file copied from the HTTP fetch of
/// /api/playlist). The state of Dispatcharr is empty because the
/// /api/channels endpoint requires the X-API-Key auth header that this
/// validation pipeline does not have. The bundle/category guard runs
/// BEFORE state-dependent code, so this is sufficient to measure its
/// effect.
///
/// Output is dumped via xUnit's ITestOutputHelper for inspection in
/// the test logs.
/// </summary>
public class BundleGuardLiveValidationTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void Live_playlist_bundle_guard_summary()
    {
        var playlistPath = Path.Combine(AppContext.BaseDirectory, "TestData", "m3ucrawler_playlist_20260831_204529.m3u");
        Assert.True(File.Exists(playlistPath), $"Live playlist fixture missing: {playlistPath}");

        var content = File.ReadAllText(playlistPath);

        var parser = new M3uParserService();
        var streams = parser.Parse(content);
        Assert.NotEmpty(streams);

        // Convert to DiscoveredStream (no Provider info from the raw
        // playlist, so use a generic one). All entries are assumed
        // working (the real pipeline also tests streams; here we focus
        // on bundle exclusion which happens BEFORE working-state).
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

        // First, do an "unfiltered" simulation by replicating the
        // matcher logic with the guard disabled: count how many
        // streams WOULD pass through BuildPlan without the guard.
        // We approximate by calling BuildPlan with a no-op alias map
        // and empty DispatcharrState, then counting which entries the
        // matcher would have ingested. Since the guard runs FIRST and
        // excludes by pattern only, the count below is "channels that
        // would have been created without the guard", equivalent to
        // "streams that match the bundle patterns".
        int bundleMatchesInTitle = 0;
        int bundleMatchesInGroup = 0;
        var matchedTitleEntries = new List<string>();
        var matchedGroupEntries = new List<string>();

        foreach (var s in streams)
        {
            // The guard's title regex: \b(Filmes|Combates|LiveCam|24\s*/\s*7|PACK|BUNDLE)\b|#f#
            bool titleHit = System.Text.RegularExpressions.Regex.IsMatch(
                s.Title ?? string.Empty,
                @"\b(Filmes|Combates|LiveCam|24\s*/\s*7|PACK|BUNDLE)\b|#f#",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            bool groupHit = System.Text.RegularExpressions.Regex.IsMatch(
                (s.Group ?? string.Empty).Trim(),
                @"^VOD\s*\|",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (titleHit)
            {
                bundleMatchesInTitle++;
                matchedTitleEntries.Add(s.Title ?? string.Empty);
            }
            if (groupHit)
            {
                bundleMatchesInGroup++;
                matchedGroupEntries.Add($"{s.Title}  ::group::  {s.Group}");
            }
        }

        // Now run the real BuildPlan with the guard active.
        var matcher = new ChannelMatcher(new AliasResolver(null));
        var plan = matcher.BuildPlan(
            discovered,
            new DispatcharrState(
                Array.Empty<DispatcharrChannel>(),
                Array.Empty<DispatcharrStream>(),
                Array.Empty<DispatcharrChannelGroup>(),
                null),
            MatchingOptions.Default,
            new StreamOrderingPolicy(),
            playlistPath, "http://x", dryRun: true);

        // Build a per-bucket summary
        var bucketSample = plan.Channels
            .OrderBy(c => c.CanonicalName, StringComparer.OrdinalIgnoreCase)
            .Select(c => new
            {
                c.CanonicalName,
                c.Outcome,
                c.MatchScore,
                c.MatchReason,
                StreamCount = c.Streams.Count,
                StreamNames = c.Streams.Select(s => s.StreamName).ToList(),
            })
            .ToList();

        var summary = new
        {
            input = new
            {
                playlistFile = playlistPath,
                totalStreams = streams.Count,
            },
            bundleGuard = new
            {
                titleMatches = bundleMatchesInTitle,
                groupMatches = bundleMatchesInGroup,
                totalExcluded = bundleMatchesInTitle + bundleMatchesInGroup,
                exampleTitles = matchedTitleEntries.Take(20).ToList(),
                exampleGroupEntries = matchedGroupEntries.Take(20).ToList(),
            },
            matchPlan = new
            {
                planChannelCount = plan.Channels.Count,
                counts = new
                {
                    plan.Counts.NewChannels,
                    plan.Counts.Matched,
                    plan.Counts.Ambiguous,
                    plan.Counts.Skipped,
                    plan.Counts.Unchanged,
                    plan.Counts.RemovedStreams,
                    plan.Counts.NewStreams,
                },
                buckets = bucketSample,
            },
        };

        var json = JsonSerializer.Serialize(summary, JsonOpts);
        // xUnit captures stdout per test via ITestOutputHelper. Since we
        // don't have one injected here, write to a temp file for
        // inspection and rely on test output.
        var dumpPath = Path.Combine(Path.GetTempPath(), $"bundle_guard_validation_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
        File.WriteAllText(dumpPath, json);

        // Basic invariants
        Assert.Equal(0, plan.Counts.Ambiguous);
        Assert.True(plan.Channels.Count > 0, "Should produce at least the legitimate PT channels.");
        // Every bucket should NOT contain any of the bundle patterns.
        foreach (var ch in plan.Channels)
        {
            Assert.DoesNotContain("24/7", ch.CanonicalName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Filmes ", ch.CanonicalName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Combates ", ch.CanonicalName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("LiveCam", ch.CanonicalName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("#f#", ch.CanonicalName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PACK", ch.CanonicalName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("BUNDLE", ch.CanonicalName, StringComparison.OrdinalIgnoreCase);
        }

        Console.WriteLine("==== BUNDLE GUARD VALIDATION REPORT ====");
        Console.WriteLine($"Input streams: {streams.Count}");
        Console.WriteLine($"Title-pattern matches: {bundleMatchesInTitle}");
        Console.WriteLine($"Group-pattern matches: {bundleMatchesInGroup}");
        Console.WriteLine($"Total excluded: {bundleMatchesInTitle + bundleMatchesInGroup}");
        Console.WriteLine($"Output channels in plan: {plan.Channels.Count}");
        Console.WriteLine($"Skipped (counts.Skipped): {plan.Counts.Skipped}");
        Console.WriteLine($"NewChannels: {plan.Counts.NewChannels}");
        Console.WriteLine($"NewStreams: {plan.Counts.NewStreams}");
        // Compute total streams that reached buckets (post-guard)
        int streamsInBuckets = plan.Channels.Sum(c => c.Streams.Count);
        Console.WriteLine($"Total streams across buckets: {streamsInBuckets}");
        Console.WriteLine($"JSON dump: {dumpPath}");
    }
}
