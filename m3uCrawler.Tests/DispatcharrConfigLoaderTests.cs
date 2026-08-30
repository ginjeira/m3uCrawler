using m3uCrawler.Models;
using Xunit;

namespace m3uCrawler.Tests;

public class DispatcharrConfigLoaderTests
{
    [Fact]
    public void Returns_disabled_when_file_missing()
    {
        var cfg = DispatcharrConfigLoader.Load();
        Assert.False(cfg.Enabled);
    }

    [Fact]
    public void Reads_keys_from_wtelegram_config()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "wtelegram.config");
        var origExists = File.Exists(path);
        var origContent = origExists ? File.ReadAllText(path) : null;
        try
        {
            File.WriteAllText(path,
                "dispatcharr_enabled=true\n" +
                "dispatcharr_base_url=http://dispatcharr.local:9191\n" +
                "dispatcharr_api_key=PLACEHOLDER-API-KEY\n" +
                "dispatcharr_dry_run=false\n" +
                "dispatcharr_match_threshold=85\n" +
                "dispatcharr_provider_priority=Provider_A,Provider_B\n" +
                "dispatcharr_target_group_name=IPTV\n");
            var cfg = DispatcharrConfigLoader.Load();
            Assert.True(cfg.Enabled);
            Assert.Equal("http://dispatcharr.local:9191", cfg.BaseUrl);
            Assert.False(cfg.DryRun);
            Assert.Equal(85, cfg.MatchThreshold);
            Assert.Equal(new[] { "Provider_A", "Provider_B" }, cfg.ProviderPriority);
            Assert.Equal("IPTV", cfg.TargetGroupName);
        }
        finally
        {
            if (origExists && origContent != null) File.WriteAllText(path, origContent);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Disabled_when_enabled_key_not_present_even_if_base_url_set()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "wtelegram.config");
        var origExists = File.Exists(path);
        var origContent = origExists ? File.ReadAllText(path) : null;
        try
        {
            File.WriteAllText(path,
                "dispatcharr_base_url=http://dispatcharr.local\n");
            var cfg = DispatcharrConfigLoader.Load();
            Assert.False(cfg.Enabled);
        }
        finally
        {
            if (origExists && origContent != null) File.WriteAllText(path, origContent);
            else if (File.Exists(path)) File.Delete(path);
        }
    }
}
