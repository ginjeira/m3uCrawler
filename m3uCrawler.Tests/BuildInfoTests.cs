using m3uCrawler.Build;
using Xunit;

namespace m3uCrawler.Tests;

public class BuildInfoTests
{
    [Fact]
    public void Application_is_m3uCrawler()
    {
        Assert.Equal("m3uCrawler", BuildInfo.Application);
    }

    [Fact]
    public void Version_is_set_and_non_empty()
    {
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.Current.Version));
    }

    [Fact]
    public void Commit_is_set_and_non_empty()
    {
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.Current.Commit));
    }

    [Fact]
    public void BuildNumber_is_non_negative()
    {
        Assert.True(BuildInfo.Current.BuildNumber >= 0);
    }

    [Fact]
    public void BuildDate_is_set_and_not_minvalue()
    {
        Assert.NotEqual(default(DateTimeOffset), BuildInfo.Current.BuildDate);
        Assert.NotEqual(DateTimeOffset.MinValue, BuildInfo.Current.BuildDate);
    }

    [Fact]
    public void BuildDate_is_utc()
    {
        Assert.Equal(TimeSpan.Zero, BuildInfo.Current.BuildDate.Offset);
    }

    [Fact]
    public void OverrideForTesting_replaces_values()
    {
        var original = BuildInfo.Current;
        try
        {
            var replacement = new DateTimeOffset(2026, 9, 2, 14, 30, 0, TimeSpan.Zero);
            BuildInfo.OverrideForTesting("9.9.9-test", "deadbeef0001", 9999, replacement);

            Assert.Equal("9.9.9-test", BuildInfo.Current.Version);
            Assert.Equal("deadbeef0001", BuildInfo.Current.Commit);
            Assert.Equal(9999, BuildInfo.Current.BuildNumber);
            Assert.Equal(replacement, BuildInfo.Current.BuildDate);
        }
        finally
        {
            BuildInfo.ResetForTesting();
            Assert.Same(original, BuildInfo.Current);
        }
    }

    [Fact]
    public void ToFormatString_returns_line_for_cli()
    {
        BuildInfo.OverrideForTesting("1.2.3-cli-test", "abc1234", 42, new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero));
        var line = BuildInfo.Current.ToCliLine();
        Assert.Equal("m3uCrawler 1.2.3-cli-test (abc1234, build 42, 2026-09-02T10:00:00Z)", line);
        BuildInfo.ResetForTesting();
    }

    [Fact]
    public void ToFormatString_works_with_default_values()
    {
        var line = BuildInfo.Current.ToCliLine();
        Assert.StartsWith("m3uCrawler ", line);
        Assert.Contains(", build ", line);
        Assert.Contains("Z)", line);
    }

    [Fact]
    public void Parser_extracts_sha_build_date()
    {
        var semver = BuildInfo.ParseInformationalVersion(
            "0.4.0+sha.abc1234+build.42+date.2026-09-02T10:00:00Z",
            out var sha, out var build, out var date);

        Assert.Equal("0.4.0", semver);
        Assert.Equal("abc1234", sha);
        Assert.Equal(42, build);
        Assert.NotNull(date);
        Assert.Equal(new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero), date!.Value);
    }

    [Fact]
    public void Parser_tolerates_missing_metadata()
    {
        var semver = BuildInfo.ParseInformationalVersion("0.4.0", out var sha, out var build, out var date);
        Assert.Equal("0.4.0", semver);
        Assert.Equal(string.Empty, sha);
        Assert.Null(build);
        Assert.Null(date);
    }

    [Fact]
    public void Constructor_normalises_invalid_inputs()
    {
        var info = new BuildInfo("", "", -3, default);
        Assert.Equal(BuildInfo.FallbackVersion, info.Version);
        Assert.Equal(BuildInfo.FallbackCommit, info.Commit);
        Assert.Equal(0, info.BuildNumber);
        Assert.Equal(BuildInfo.FallbackBuildDate, info.BuildDate);
    }
}
