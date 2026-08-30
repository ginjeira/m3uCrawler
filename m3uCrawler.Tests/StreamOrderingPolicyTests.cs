using m3uCrawler.Models;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using Xunit;

namespace m3uCrawler.Tests;

public class StreamOrderingPolicyTests
{
    private static DiscoveredStream Stream(string provider, string title, double rt = 100, bool working = true)
        => new(
            new M3uStream { Title = title, Url = $"https://{provider}/x", IsWorking = working, ResponseTime = rt },
            provider, "src");

    [Fact]
    public void Provider_priority_is_respected_when_quality_equal()
    {
        var ordering = new StreamOrderingPolicy(new[] { "Provider_A", "Provider_B" });
        var ordered = ordering.Order(new[] { Stream("Provider_B", "X"), Stream("Provider_A", "X") });
        Assert.Equal("Provider_A", ordered[0].Stream.Provider);
        Assert.Equal("Provider_B", ordered[1].Stream.Provider);
    }

    [Fact]
    public void Quality_beats_provider_priority()
    {
        var ordering = new StreamOrderingPolicy(new[] { "Provider_A", "Provider_B" });
        var ordered = ordering.Order(new[]
        {
            Stream("Provider_A", "X HD"),
            Stream("Provider_B", "X FHD"),
        });
        Assert.Equal("Provider_B", ordered[0].Stream.Provider);
        Assert.Contains("FHD", ordered[0].Reason);
    }

    [Fact]
    public void Not_working_streams_are_excluded()
    {
        var ordering = new StreamOrderingPolicy();
        var ordered = ordering.Order(new[]
        {
            Stream("A", "alive", working: false),
            Stream("B", "alive", working: true),
        });
        Assert.Single(ordered);
        Assert.Equal("B", ordered[0].Stream.Provider);
    }

    [Fact]
    public void Lower_response_time_wins_ties()
    {
        var ordering = new StreamOrderingPolicy();
        var ordered = ordering.Order(new[]
        {
            Stream("A", "X", rt: 500),
            Stream("B", "X", rt: 50),
        });
        Assert.Equal("B", ordered[0].Stream.Provider);
    }

    [Fact]
    public void Empty_provider_priority_uses_quality_then_response_time()
    {
        var ordering = new StreamOrderingPolicy();
        var ordered = ordering.Order(new[]
        {
            Stream("Provider_A", "X"),
            Stream("Provider_B", "X FHD"),
        });
        Assert.Equal("Provider_B", ordered[0].Stream.Provider);
    }

    [Fact]
    public void Reason_string_includes_quality_and_response_time()
    {
        var ordering = new StreamOrderingPolicy(new[] { "Provider_A" });
        var ordered = ordering.Order(new[] { Stream("Provider_A", "X HD", rt: 120) });
        Assert.Contains("HD", ordered[0].Reason);
        Assert.Contains("rt=120ms", ordered[0].Reason);
    }
}
