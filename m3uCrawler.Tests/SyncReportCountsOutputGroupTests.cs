using m3uCrawler.Models;
using m3uCrawler.Services.Matching;
using Xunit;

namespace m3uCrawler.Tests;

public class SyncReportCountsOutputGroupTests
{
    [Fact]
    public void SyncReportCounts_OutputGroups_defaults_to_empty()
    {
        var counts = new SyncReportCounts();
        Assert.NotNull(counts.OutputGroups);
        Assert.Empty(counts.OutputGroups);
    }

    [Fact]
    public void SyncReportCounts_OutputGroups_serializes_as_outputGroups()
    {
        var counts = new SyncReportCounts
        {
            OutputGroups = new Dictionary<string, int>
            {
                ["PortugalLive"] = 10,
                ["Foreign"] = 4,
                ["PortugalVOD"] = 2,
            },
        };

        var json = System.Text.Json.JsonSerializer.Serialize(counts);
        Assert.Contains("\"outputGroups\"", json);
        Assert.Contains("\"PortugalLive\":10", json);
        Assert.Contains("\"Foreign\":4", json);
        Assert.Contains("\"PortugalVOD\":2", json);
    }

    [Fact]
    public void SyncReportCounts_with_empty_OutputGroups_still_serializes_outputGroups_key()
    {
        var counts = new SyncReportCounts();
        var json = System.Text.Json.JsonSerializer.Serialize(counts);
        Assert.Contains("\"outputGroups\"", json);
    }

    [Fact]
    public void SyncReportCounts_OutputGroups_round_trips()
    {
        var counts = new SyncReportCounts
        {
            Matched = 3,
            OutputGroups = new Dictionary<string, int>
            {
                ["PortugalLive"] = 5,
                ["Foreign"] = 7,
            },
        };

        var json = System.Text.Json.JsonSerializer.Serialize(counts);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<SyncReportCounts>(json);
        Assert.NotNull(deserialized);
        Assert.Equal(3, deserialized.Matched);
        Assert.Equal(2, deserialized.OutputGroups.Count);
        Assert.Equal(5, deserialized.OutputGroups["PortugalLive"]);
        Assert.Equal(7, deserialized.OutputGroups["Foreign"]);
    }
}
