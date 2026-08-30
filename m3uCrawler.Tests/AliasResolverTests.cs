using m3uCrawler.Services.Matching;
using Xunit;

namespace m3uCrawler.Tests;

public class AliasResolverTests
{
    [Fact]
    public void Resolves_alias_to_canonical()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sport tv1|SPORTTV1"] = "Sport TV 1",
        };
        var resolver = new AliasResolver(map);
        var r = resolver.Resolve("SportTV1");
        Assert.NotNull(r);
        Assert.Equal("sport tv 1", r!.Canonical);
    }

    [Fact]
    public void Missing_alias_returns_null()
    {
        var resolver = new AliasResolver();
        Assert.Null(resolver.Resolve("Unknown Channel"));
    }

    [Fact]
    public void Alias_resolution_is_case_insensitive()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cnn"] = "CNN International",
        };
        var resolver = new AliasResolver(map);
        Assert.Equal("cnn international", resolver.Resolve("CNN")!.Canonical);
        Assert.Equal("cnn international", resolver.Resolve("cnn")!.Canonical);
    }

    [Fact]
    public void FromFile_returns_empty_when_missing()
    {
        var resolver = AliasResolver.FromFile(null);
        Assert.Null(resolver.Resolve("anything"));
    }

    [Fact]
    public void FromFile_throws_on_malformed_json()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alias_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            Assert.Throws<InvalidDataException>(() => AliasResolver.FromFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
