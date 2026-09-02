using System.Text.Json;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Matching;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Read-only investigation that builds two matrices from the current
/// m3uCrawler playlist (snapshot 2026-08-31 22:39:14):
///
///   1. Per-group: each group-title -> stream count, distinct channel
///      count, top channels, and candidate OutputGroup.
///   2. Per-channel: each canonical channel identity -> the set of
///      distinct SourceGroups it appears in.
///
/// Output: JSON dump to %TEMP%/taxonomy_investigation_<ts>.json plus
/// human-readable Console.WriteLine summary.
/// </summary>
public class GroupTaxonomyInvestigationTests
{
    private const string PlaylistFile = "m3ucrawler_playlist_20260831_223914.m3u";

    [Fact]
    public void Build_source_group_to_channel_matrix()
    {
        var playlistPath = Path.Combine(AppContext.BaseDirectory, "TestData", PlaylistFile);
        Assert.True(File.Exists(playlistPath));

        var content = File.ReadAllText(playlistPath);
        var parser = new M3uParserService();
        var streams = parser.Parse(content);

        var byGroup = streams
            .Where(s => !string.IsNullOrWhiteSpace(s.Group))
            .GroupBy(s => s.Group!)
            .OrderByDescending(g => g.Count())
            .ToList();

        var perGroup = byGroup
            .Select(g =>
            {
                var titles = g.Select(s => s.Title ?? string.Empty).ToList();
                var distinctChannels = titles
                    .Select(t => ChannelNormalizer.Normalize(t))
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct()
                    .ToList();
                var topChannels = distinctChannels
                    .GroupBy(c => c)
                    .Select(c => new ChannelCount { Identity = c.Key, Count = c.Count() })
                    .OrderByDescending(x => x.Count)
                    .Take(8)
                    .ToList();
                return new PerGroupEntry
                {
                    Name = g.Key,
                    StreamCount = g.Count(),
                    DistinctChannelCount = distinctChannels.Count,
                    TopChannels = topChannels,
                };
            })
            .ToList();

        // Per-channel: identity -> set of SourceGroups (distinct)
        var perChannel = streams
            .Where(s => !string.IsNullOrWhiteSpace(s.Group))
            .Select(s => new
            {
                Identity = ChannelNormalizer.Normalize(s.Title ?? string.Empty),
                SourceGroup = s.Group!,
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Identity))
            .GroupBy(x => x.Identity)
            .Select(g => new PerChannelEntry
            {
                Identity = g.Key,
                StreamCount = g.Count(),
                DistinctSourceGroups = g.Select(x => x.SourceGroup).Distinct().Count(),
                SourceGroups = g.Select(x => x.SourceGroup).Distinct()
                    .OrderBy(x => x, StringComparer.Ordinal).ToList(),
            })
            .Where(c => c.DistinctSourceGroups > 1)
            .OrderByDescending(c => c.DistinctSourceGroups)
            .ThenByDescending(c => c.StreamCount)
            .ToList();

        // Per-channel: identity -> top category (suggestion from groups)
        var perChannelWithSuggestion = perChannel.Select(c => new PerChannelWithSuggestion
        {
            Identity = c.Identity,
            StreamCount = c.StreamCount,
            DistinctSourceGroups = c.DistinctSourceGroups,
            SourceGroups = c.SourceGroups,
        }).ToList();

        // Channel categorization hints from group titles
        var perChannelSuggestion = BuildChannelCategoryHints(perChannelWithSuggestion);

        var report = new TaxonomyInvestigationReport
        {
            InputStreamCount = streams.Count,
            DistinctGroupCount = byGroup.Count,
            PerGroup = perGroup,
            ChannelsAcrossMultipleGroups = perChannel,
            ChannelCategorySuggestions = perChannelSuggestion,
        };

        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var jsonPath = Path.Combine(Path.GetTempPath(), $"taxonomy_investigation_{stamp}.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, jsonOpts));

        Console.WriteLine("==== Taxonomy Investigation ====");
        Console.WriteLine($"Input streams: {streams.Count}");
        Console.WriteLine($"Distinct SourceGroups: {byGroup.Count}");
        Console.WriteLine($"Channels in 2+ SourceGroups: {perChannel.Count}");
        Console.WriteLine($"JSON dump: {jsonPath}");
    }

    private static List<ChannelCategorySuggestion> BuildChannelCategoryHints(
        List<PerChannelWithSuggestion> entries)
    {
        var result = new List<ChannelCategorySuggestion>();
        foreach (var e in entries)
        {
            var hints = new List<string>();
            foreach (var g in e.SourceGroups)
            {
                var lower = g.ToLowerInvariant();
                if (lower.Contains("vod")) hints.Add("vod");
                if (lower.Contains("24-7") || lower.Contains("canais 24")) hints.Add("bundle-24-7");
                if (lower.Contains("filmes e séries") || lower.Contains("filmes e series")) hints.Add("filmes-series");
                if (lower.Contains("entretenimento")) hints.Add("entretenimento");
                if (lower.Contains("desporto") || lower.Contains("esporte") || lower.Contains("sport")) hints.Add("desporto");
                if (lower.Contains("infantil")) hints.Add("infantil");
                if (lower.Contains("document")) hints.Add("documentarios");
                if (lower.Contains("filmes")) hints.Add("filmes");
                if (lower.Contains("vip")) hints.Add("vip");
                if (lower.Contains("liga")) hints.Add("liga");
                if (lower.Contains("4k") || lower.Contains("uhd")) hints.Add("qualidade-4k");
                if (lower.Contains("hevc")) hints.Add("codec-hevc");
                if (lower.Contains("sports networks")) hints.Add("sports-generic");
            }
            result.Add(new ChannelCategorySuggestion
            {
                Identity = e.Identity,
                SourceGroups = e.SourceGroups,
                Hints = hints.Distinct().OrderBy(h => h).ToList(),
            });
        }
        return result;
    }

    private class PerGroupEntry
    {
        public string Name { get; set; } = "";
        public int StreamCount { get; set; }
        public int DistinctChannelCount { get; set; }
        public List<ChannelCount> TopChannels { get; set; } = new();
    }

    private class ChannelCount
    {
        public string Identity { get; set; } = "";
        public int Count { get; set; }
    }

    private class PerChannelEntry
    {
        public string Identity { get; set; } = "";
        public int StreamCount { get; set; }
        public int DistinctSourceGroups { get; set; }
        public List<string> SourceGroups { get; set; } = new();
    }

    private class PerChannelWithSuggestion
    {
        public string Identity { get; set; } = "";
        public int StreamCount { get; set; }
        public int DistinctSourceGroups { get; set; }
        public List<string> SourceGroups { get; set; } = new();
    }

    private class ChannelCategorySuggestion
    {
        public string Identity { get; set; } = "";
        public List<string> SourceGroups { get; set; } = new();
        public List<string> Hints { get; set; } = new();
    }

    private class TaxonomyInvestigationReport
    {
        public int InputStreamCount { get; set; }
        public int DistinctGroupCount { get; set; }
        public List<PerGroupEntry> PerGroup { get; set; } = new();
        public List<PerChannelEntry> ChannelsAcrossMultipleGroups { get; set; } = new();
        public List<ChannelCategorySuggestion> ChannelCategorySuggestions { get; set; } = new();
    }
}