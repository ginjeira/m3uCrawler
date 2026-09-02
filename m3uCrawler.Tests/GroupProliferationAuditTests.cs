using System.Text.Json;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Matching;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Read-only audit of group-title proliferation in the current
/// production m3uCrawler playlist (snapshot 2026-08-31 22:39:14).
///
/// Produces:
///   - inventory of all groups with stream counts
///   - groups with single channel
///   - candidate similar/mergeable groups
///   - channels distributed across multiple groups
///   - bundle/category residual
///
/// Output written to:
///   C:\Users\ULSSJOSE\AppData\Local\Temp\group_audit_<ts>.json
/// </summary>
public class GroupProliferationAuditTests
{
    private const string PlaylistFile = "m3ucrawler_playlist_20260831_223914.m3u";

    [Fact]
    public void Audit_current_playlist_group_proliferation()
    {
        var playlistPath = Path.Combine(AppContext.BaseDirectory, "TestData", PlaylistFile);
        Assert.True(File.Exists(playlistPath), $"Playlist fixture missing: {playlistPath}");

        var content = File.ReadAllText(playlistPath);
        var parser = new M3uParserService();
        var streams = parser.Parse(content);

        var report = BuildReport(streams);

        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var jsonPath = Path.Combine(Path.GetTempPath(), $"group_audit_{stamp}.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, jsonOpts));

        // Console summary
        Console.WriteLine("==== Group Proliferation Audit ====");
        Console.WriteLine($"Input streams: {report.InputStreamCount}");
        Console.WriteLine($"Distinct groups: {report.DistinctGroupCount}");
        Console.WriteLine($"Groups with 1 channel: {report.SingleChannelGroups.Count}");
        Console.WriteLine($"Groups with 2-5 channels: {report.BucketsBySize["2to5"]}");
        Console.WriteLine($"Groups with 6-10 channels: {report.BucketsBySize["6to10"]}");
        Console.WriteLine($"Groups with >10 channels: {report.BucketsBySize["over10"]}");
        Console.WriteLine($"Distinct (normalized lower+trim) groups: {report.DistinctNormalizedGroupCount}");
        Console.WriteLine($"Channels distributed across multiple groups: {report.ChannelsAcrossMultipleGroups.Count}");
        Console.WriteLine($"Bundle/category residual count: {report.BundleCategoryResidual.Count}");
        Console.WriteLine($"JSON dump: {jsonPath}");
    }

    private static GroupAuditReport BuildReport(IReadOnlyList<M3uStream> streams)
    {
        var distinctGroups = streams
            .Where(s => !string.IsNullOrWhiteSpace(s.Group))
            .Select(s => s.Group!)
            .Distinct()
            .OrderBy(g => g, StringComparer.Ordinal)
            .ToList();

        var byGroup = streams
            .Where(s => !string.IsNullOrWhiteSpace(s.Group))
            .GroupBy(s => s.Group!)
            .ToDictionary(g => g.Key, g => g.ToList());

        var distinctNormalizedGroups = distinctGroups
            .Select(g => (g, Normalize(g)))
            .GroupBy(t => t.Item2)
            .ToDictionary(t => t.Key, t => t.Select(x => x.g).OrderBy(x => x, StringComparer.Ordinal).ToList());

        // Inventory
        var inventory = distinctGroups
            .Select(g => new GroupInventoryEntry
            {
                Name = g,
                NormalizedKey = Normalize(g),
                StreamCount = byGroup[g].Count,
                Percent = streams.Count > 0
                    ? Math.Round(byGroup[g].Count * 100.0 / streams.Count, 2)
                    : 0,
            })
            .OrderByDescending(e => e.StreamCount)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

        // Buckets by size
        var buckets = new Dictionary<string, int>
        {
            ["1"] = 0, ["2to5"] = 0, ["6to10"] = 0, ["over10"] = 0,
        };
        var singleChannelGroups = new List<string>();
        foreach (var e in inventory)
        {
            if (e.StreamCount == 1) { buckets["1"]++; singleChannelGroups.Add(e.Name); }
            else if (e.StreamCount <= 5) buckets["2to5"]++;
            else if (e.StreamCount <= 10) buckets["6to10"]++;
            else buckets["over10"]++;
        }

        // Find candidate merge groups: same NormalizedKey but different raw names
        var mergeCandidates = distinctNormalizedGroups
            .Where(kv => kv.Value.Count > 1)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        // Channels distributed across multiple groups
        var channelsByIdentity = streams
            .GroupBy(s => ChannelNormalizer.Normalize(s.Title ?? ""))
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .ToDictionary(g => g.Key, g => g.ToList());
        var channelsAcrossMultipleGroups = channelsByIdentity
            .Where(kv =>
            {
                var distinctGroupsInBucket = kv.Value
                    .Where(s => !string.IsNullOrWhiteSpace(s.Group))
                    .Select(s => s.Group!)
                    .Distinct()
                    .Count();
                return distinctGroupsInBucket > 1;
            })
            .Select(kv => new ChannelAcrossGroups
            {
                Identity = kv.Key,
                DistinctGroups = kv.Value
                    .Where(s => !string.IsNullOrWhiteSpace(s.Group))
                    .Select(s => s.Group!)
                    .Distinct()
                    .OrderBy(g => g, StringComparer.Ordinal)
                    .ToList(),
                StreamCount = kv.Value.Count,
            })
            .OrderByDescending(c => c.DistinctGroups.Count)
            .ThenBy(c => c.Identity)
            .ToList();

        // Bundle / category residual detection (post-bundle-guard)
        var bundlePatterns = new[]
        {
            "Filmes", "Combates", "LiveCam", "24/7", "PACK", "BUNDLE", "#f#", "VOD",
        };
        var bundleGroups = distinctGroups
            .Where(g => bundlePatterns.Any(p => g.Contains(p, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(g => g)
            .ToList();
        var bundleResidualStreams = streams
            .Where(s => !string.IsNullOrWhiteSpace(s.Group) &&
                bundlePatterns.Any(p => s.Group!.Contains(p, StringComparison.OrdinalIgnoreCase)))
            .Select(s => new BundleCategoryResidual
            {
                Title = s.Title,
                Group = s.Group,
            })
            .ToList();

        return new GroupAuditReport
        {
            InputStreamCount = streams.Count,
            DistinctGroupCount = distinctGroups.Count,
            DistinctNormalizedGroupCount = distinctNormalizedGroups.Count,
            Inventory = inventory,
            BucketsBySize = buckets,
            SingleChannelGroups = singleChannelGroups,
            MergeCandidatesByNormalizedKey = mergeCandidates,
            ChannelsAcrossMultipleGroups = channelsAcrossMultipleGroups,
            BundleCategoryGroups = bundleGroups,
            BundleCategoryResidual = bundleResidualStreams,
        };
    }

    private static string Normalize(string name) =>
        string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim().ToLowerInvariant();

    // ===== report DTOs =====

    private class GroupAuditReport
    {
        public int InputStreamCount { get; set; }
        public int DistinctGroupCount { get; set; }
        public int DistinctNormalizedGroupCount { get; set; }
        public List<GroupInventoryEntry> Inventory { get; set; } = new();
        public Dictionary<string, int> BucketsBySize { get; set; } = new();
        public List<string> SingleChannelGroups { get; set; } = new();
        public Dictionary<string, List<string>> MergeCandidatesByNormalizedKey { get; set; } = new();
        public List<ChannelAcrossGroups> ChannelsAcrossMultipleGroups { get; set; } = new();
        public List<string> BundleCategoryGroups { get; set; } = new();
        public List<BundleCategoryResidual> BundleCategoryResidual { get; set; } = new();
    }

    private class GroupInventoryEntry
    {
        public string Name { get; set; } = "";
        public string NormalizedKey { get; set; } = "";
        public int StreamCount { get; set; }
        public double Percent { get; set; }
    }

    private class ChannelAcrossGroups
    {
        public string Identity { get; set; } = "";
        public int StreamCount { get; set; }
        public List<string> DistinctGroups { get; set; } = new();
    }

    private class BundleCategoryResidual
    {
        public string? Title { get; set; }
        public string? Group { get; set; }
    }
}