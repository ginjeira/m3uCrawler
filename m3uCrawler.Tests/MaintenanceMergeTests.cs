using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

public class MaintenanceMergeTests
{
    private static M3uStream Stream(string url, bool working, string title = "x")
        => new() { Url = url, IsWorking = working, Title = title };

    [Fact]
    public void Keeps_existing_when_no_fresh_candidates()
    {
        var existing = new List<M3uStream> { Stream("https://x/a", true) };
        var merged = TelegramScraperService.MergeStreams(existing, new List<M3uStream>());

        Assert.Single(merged);
        Assert.Equal("https://x/a", merged[0].Url);
    }

    [Fact]
    public void Dedupes_by_url()
    {
        var existing = new List<M3uStream> { Stream("https://x/a", true) };
        var fresh = new List<M3uStream> { Stream("https://x/a", true) };

        var merged = TelegramScraperService.MergeStreams(existing, fresh);
        Assert.Single(merged);
    }

    [Fact]
    public void Prefers_working_fresh_over_dead_existing()
    {
        var existing = new List<M3uStream> { Stream("https://x/a", false) };
        var fresh = new List<M3uStream> { Stream("https://x/a", true) };

        var merged = TelegramScraperService.MergeStreams(existing, fresh);
        Assert.Single(merged);
        Assert.True(merged[0].IsWorking);
    }

    [Fact]
    public void Adds_new_working_fresh()
    {
        var existing = new List<M3uStream> { Stream("https://x/a", true) };
        var fresh = new List<M3uStream> { Stream("https://x/b", true) };

        var merged = TelegramScraperService.MergeStreams(existing, fresh);
        Assert.Equal(2, merged.Count);
    }
}
