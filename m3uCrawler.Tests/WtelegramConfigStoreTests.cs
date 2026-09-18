using m3uCrawler.Services.Configuration;
using Xunit;

namespace m3uCrawler.Tests;

public sealed class WtelegramConfigStoreTests : IDisposable
{
    private readonly string _dir;

    public WtelegramConfigStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "m3ucrawler-wtelegram-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string ConfigPath => Path.Combine(_dir, "wtelegram.config");

    [Fact]
    public void Creates_file_when_missing_with_only_provided_keys()
    {
        var store = new WtelegramConfigStore(ConfigPath);

        store.Upsert(new Dictionary<string, string> { ["alpha"] = "1", ["beta"] = "2" });

        Assert.True(File.Exists(ConfigPath));
        var lines = File.ReadAllLines(ConfigPath).Where(l => l.Length > 0).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Contains("alpha=1", lines);
        Assert.Contains("beta=2", lines);
    }

    [Fact]
    public void Updates_existing_key_in_place_and_preserves_comments_unknown_keys_and_order()
    {
        var fixture = string.Join(Environment.NewLine, new[]
        {
            "# top comment",
            "key1=old",
            "unknown=keep",
            string.Empty,
            "# trailing comment",
            "key3=untouched",
        }) + Environment.NewLine;
        File.WriteAllText(ConfigPath, fixture);

        var store = new WtelegramConfigStore(ConfigPath);
        store.Upsert(new Dictionary<string, string> { ["key1"] = "new" });

        var expected = string.Join(Environment.NewLine, new[]
        {
            "# top comment",
            "key1=new",
            "unknown=keep",
            string.Empty,
            "# trailing comment",
            "key3=untouched",
        }) + Environment.NewLine;

        Assert.Equal(expected, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Appends_a_new_key_at_the_end()
    {
        File.WriteAllText(ConfigPath, "existing=value" + Environment.NewLine);

        var store = new WtelegramConfigStore(ConfigPath);
        store.Upsert(new Dictionary<string, string> { ["added"] = "yes" });

        var lines = File.ReadAllLines(ConfigPath);
        Assert.Equal(new[] { "existing=value", "added=yes" }, lines);
    }

    [Fact]
    public void Value_containing_equals_round_trips()
    {
        var store = new WtelegramConfigStore(ConfigPath);
        store.Upsert(new Dictionary<string, string> { ["alpha"] = "base64=a=b==" });

        Assert.Contains("alpha=base64=a=b==", File.ReadAllLines(ConfigPath));
        Assert.Equal("base64=a=b==", store.Read()["alpha"]);
    }

    [Fact]
    public void Read_returns_merged_view_after_upsert()
    {
        File.WriteAllText(ConfigPath, "first=1" + Environment.NewLine);
        var store = new WtelegramConfigStore(ConfigPath);

        store.Upsert(new Dictionary<string, string> { ["second"] = "2" });

        var values = store.Read();
        Assert.Equal("1", values["first"]);
        Assert.Equal("2", values["second"]);
        Assert.Equal(2, values.Count);
    }

    [Fact]
    public void Upsert_leaves_no_temp_files_behind()
    {
        File.WriteAllText(ConfigPath, "first=1" + Environment.NewLine);
        var store = new WtelegramConfigStore(ConfigPath);

        store.Upsert(new Dictionary<string, string> { ["first"] = "2", ["second"] = "3" });

        var files = Directory.GetFiles(_dir);
        Assert.Single(files);
        Assert.Equal("wtelegram.config", Path.GetFileName(files[0]));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }
}
