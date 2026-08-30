using m3uCrawler.Services.Matching;
using Xunit;

namespace m3uCrawler.Tests;

public class FuzzyMatcherTests
{
    private readonly FuzzyMatcher _fuzzy = new();

    [Fact]
    public void Exact_match_after_normalization_is_100()
    {
        var ms = _fuzzy.Score("CNN International", "cnn-international");
        Assert.Equal(100, ms.Score);
        Assert.Equal("exact", ms.Reason);
    }

    [Fact]
    public void Substring_match_is_95()
    {
        var ms = _fuzzy.Score("CNN Int", "CNN International");
        Assert.True(ms.Score >= 90, $"Expected high score, got {ms.Score} ({ms.Reason})");
    }

    [Fact]
    public void Numeric_sibling_guard_rejects_different_digit_channels()
    {
        var ms = _fuzzy.Score("Fox Sports 1", "Fox Sports 2");
        Assert.True(ms.Score < 80, $"Numeric-sibling guard should have triggered, got {ms.Score} ({ms.Reason})");
    }

    [Fact]
    public void Completely_different_names_score_low()
    {
        var ms = _fuzzy.Score("CNN", "BBC News");
        Assert.True(ms.Score < 60);
    }

    [Fact]
    public void Token_set_similarity_cnn_int_vs_cnn_international()
    {
        var ms = _fuzzy.Score("CNN Int", "CNN International");
        Assert.InRange(ms.Score, 70, 99);
    }

    [Fact]
    public void Empty_inputs_return_zero()
    {
        Assert.Equal(0, _fuzzy.Score("", "CNN").Score);
        Assert.Equal(0, _fuzzy.Score("CNN", "").Score);
    }
}

public class MatchScorerTests
{
    private readonly MatchScorer _scorer = new();

    [Fact]
    public void Exact_band()
    {
        Assert.Equal(MatchBand.Exact, _scorer.Classify(100, new List<int>(), 80));
    }

    [Fact]
    public void Matched_band_when_no_close_runner_up()
    {
        Assert.Equal(MatchBand.Matched, _scorer.Classify(85, new List<int> { 60 }, 80));
    }

    [Fact]
    public void Ambiguous_band_when_close_runner_up_exists()
    {
        Assert.Equal(MatchBand.Ambiguous, _scorer.Classify(85, new List<int> { 84 }, 80));
    }

    [Fact]
    public void None_band_when_below_threshold()
    {
        Assert.Equal(MatchBand.None, _scorer.Classify(70, new List<int>(), 80));
    }
}
