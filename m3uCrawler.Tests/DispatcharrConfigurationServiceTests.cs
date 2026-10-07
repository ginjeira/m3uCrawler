using System.Text.Json;
using m3uCrawler.Services.Configuration;
using Xunit;

namespace m3uCrawler.Tests;

public sealed class DispatcharrConfigurationServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public DispatcharrConfigurationServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "m3ucrawler-dispatcharr-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "wtelegram.config");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private DispatcharrConfigurationService CreateService()
        => new(new WtelegramConfigStore(_configPath));

    private IReadOnlyDictionary<string, string> ReadRaw()
        => new WtelegramConfigStore(_configPath).Read();

    [Fact]
    public void Save_then_reload_reflects_changes()
    {
        var service = CreateService();

        service.Save(new DispatcharrConfigurationWrite(
            Enabled: true,
            BaseUrl: "http://dispatcharr.local:9191",
            DryRun: false,
            ApiKey: "SECRET-API-KEY",
            Username: "operator",
            Password: "SECRET-PASSWORD"));

        var cfg = service.Get();
        Assert.True(cfg.Enabled);
        Assert.Equal("http://dispatcharr.local:9191", cfg.BaseUrl);
        Assert.False(cfg.DryRun);
        Assert.Equal("SECRET-API-KEY", cfg.ApiKey);
        Assert.Equal("operator", cfg.Username);
        Assert.Equal("SECRET-PASSWORD", cfg.Password);
    }

    [Fact]
    public void Save_preserves_unknown_and_non_dispatcharr_keys()
    {
        File.WriteAllLines(_configPath, new[]
        {
            "telegram_api_id=12345",
            "custom_unknown=keep-me",
            "dispatcharr_enabled=false",
        });

        var service = CreateService();
        service.Save(new DispatcharrConfigurationWrite(
            Enabled: true,
            BaseUrl: "http://dispatcharr.local",
            DryRun: true,
            ApiKey: null,
            Username: null,
            Password: null));

        var raw = ReadRaw();
        Assert.Equal("12345", raw["telegram_api_id"]);
        Assert.Equal("keep-me", raw["custom_unknown"]);
        Assert.Equal("true", raw["dispatcharr_enabled"]);
        Assert.Equal("http://dispatcharr.local", raw["dispatcharr_base_url"]);
    }

    [Fact]
    public void GetForDisplay_never_contains_api_key_or_password()
    {
        var service = CreateService();
        service.Save(new DispatcharrConfigurationWrite(
            Enabled: true,
            BaseUrl: "http://dispatcharr.local",
            DryRun: true,
            ApiKey: "SECRET-API-KEY",
            Username: "operator",
            Password: "SECRET-PASSWORD"));

        var display = service.GetForDisplay();
        var json = JsonSerializer.Serialize(display);

        Assert.DoesNotContain("SECRET-API-KEY", json);
        Assert.DoesNotContain("SECRET-PASSWORD", json);
        Assert.True(display.HasApiKey);
        Assert.True(display.HasUsername);
        Assert.True(display.Enabled);
        Assert.Equal("http://dispatcharr.local", display.BaseUrl);
    }

    [Fact]
    public void Null_api_key_leaves_existing_value_untouched()
    {
        var service = CreateService();
        service.Save(new DispatcharrConfigurationWrite(
            Enabled: true, BaseUrl: "http://dispatcharr.local", DryRun: true,
            ApiKey: "SECRET-API-KEY", Username: null, Password: null));

        service.Save(new DispatcharrConfigurationWrite(
            Enabled: true, BaseUrl: "http://dispatcharr.local", DryRun: true,
            ApiKey: null, Username: null, Password: null));

        Assert.Equal("SECRET-API-KEY", ReadRaw()["dispatcharr_api_key"]);
        Assert.True(service.GetForDisplay().HasApiKey);
    }

    [Fact]
    public void Empty_api_key_clears_existing_value()
    {
        var service = CreateService();
        service.Save(new DispatcharrConfigurationWrite(
            Enabled: true, BaseUrl: "http://dispatcharr.local", DryRun: true,
            ApiKey: "SECRET-API-KEY", Username: null, Password: null));

        service.Save(new DispatcharrConfigurationWrite(
            Enabled: true, BaseUrl: "http://dispatcharr.local", DryRun: true,
            ApiKey: string.Empty, Username: null, Password: null));

        Assert.Equal(string.Empty, ReadRaw()["dispatcharr_api_key"]);
        Assert.False(service.GetForDisplay().HasApiKey);
    }

    [Fact]
    public void Dry_run_toggles()
    {
        var service = CreateService();

        service.Save(new DispatcharrConfigurationWrite(
            Enabled: true, BaseUrl: "http://dispatcharr.local", DryRun: false,
            ApiKey: null, Username: null, Password: null));
        Assert.False(service.Get().DryRun);

        service.Save(new DispatcharrConfigurationWrite(
            Enabled: true, BaseUrl: "http://dispatcharr.local", DryRun: true,
            ApiKey: null, Username: null, Password: null));
        Assert.True(service.Get().DryRun);
    }

    [Fact]
    public void GetForDisplay_exposes_match_threshold_and_target_group()
    {
        File.WriteAllLines(_configPath, new[]
        {
            "dispatcharr_enabled=true",
            "dispatcharr_base_url=http://dispatcharr.local",
            "dispatcharr_match_threshold=85",
            "dispatcharr_target_group_name=IPTV",
        });

        var display = CreateService().GetForDisplay();

        Assert.Equal("85", display.MatchThreshold);
        Assert.Equal("IPTV", display.TargetGroupName);
    }

    [Fact]
    public void Save_round_trips_all_supported_keys()
    {
        var service = CreateService();

        service.Save(new DispatcharrConfigurationWrite(
            Enabled: true,
            BaseUrl: "http://dispatcharr.local:9191",
            DryRun: false,
            ApiKey: "SECRET-API-KEY",
            Username: "operator",
            Password: "SECRET-PASSWORD",
            MatchThreshold: 85,
            TargetGroupName: "IPTV",
            ProviderPriority: new[] { "provider-b", "provider-a" },
            AliasFile: "aliases.json",
            AutoCreateGroups: false));

        var cfg = service.Get();
        Assert.True(cfg.Enabled);
        Assert.Equal("http://dispatcharr.local:9191", cfg.BaseUrl);
        Assert.False(cfg.DryRun);
        Assert.Equal("SECRET-API-KEY", cfg.ApiKey);
        Assert.Equal("operator", cfg.Username);
        Assert.Equal("SECRET-PASSWORD", cfg.Password);
        Assert.Equal(85, cfg.MatchThreshold);
        Assert.Equal("IPTV", cfg.TargetGroupName);
        Assert.Equal(new[] { "provider-b", "provider-a" }, cfg.ProviderPriority);
        Assert.Equal("aliases.json", cfg.AliasFile);
        Assert.False(cfg.AutoCreateGroups);

        var raw = ReadRaw();
        Assert.Equal("true", raw["dispatcharr_enabled"]);
        Assert.Equal("http://dispatcharr.local:9191", raw["dispatcharr_base_url"]);
        Assert.Equal("false", raw["dispatcharr_dry_run"]);
        Assert.Equal("85", raw["dispatcharr_match_threshold"]);
        Assert.Equal("IPTV", raw["dispatcharr_target_group_name"]);
        Assert.Equal("provider-b,provider-a", raw["dispatcharr_provider_priority"]);
        Assert.Equal("aliases.json", raw["dispatcharr_alias_file"]);
        Assert.Equal("false", raw["dispatcharr_auto_create_groups"]);
    }

    [Fact]
    public void GetForDisplay_exposes_new_fields_but_never_secrets()
    {
        var service = CreateService();
        service.Save(new DispatcharrConfigurationWrite(
            Enabled: true,
            BaseUrl: "http://dispatcharr.local",
            DryRun: true,
            ApiKey: "SECRET-API-KEY",
            Username: "operator",
            Password: "SECRET-PASSWORD",
            MatchThreshold: 77,
            TargetGroupName: "News",
            ProviderPriority: new[] { "one", "two" },
            AliasFile: "alias.json",
            AutoCreateGroups: false));

        var display = service.GetForDisplay();
        var json = JsonSerializer.Serialize(display);

        Assert.DoesNotContain("SECRET-API-KEY", json);
        Assert.DoesNotContain("SECRET-PASSWORD", json);
        Assert.True(display.HasApiKey);
        Assert.True(display.HasUsername);
        Assert.True(display.HasPassword);
        Assert.Equal("77", display.MatchThreshold);
        Assert.Equal("News", display.TargetGroupName);
        Assert.Equal(new[] { "one", "two" }, display.ProviderPriority);
        Assert.Equal("alias.json", display.AliasFile);
        Assert.False(display.AutoCreateGroups);
    }

    [Fact]
    public void Patch_save_preserves_unspecified_keys_and_clears_on_empty()
    {
        var service = CreateService();
        service.Save(new DispatcharrConfigurationWrite(
            Enabled: true,
            BaseUrl: "http://dispatcharr.local",
            DryRun: false,
            ApiKey: "SECRET-API-KEY",
            Username: "operator",
            Password: "SECRET-PASSWORD",
            MatchThreshold: 80,
            TargetGroupName: "IPTV",
            ProviderPriority: new[] { "one" },
            AliasFile: "alias.json",
            AutoCreateGroups: true));

        // Patch parcial: só o threshold.
        service.Save(new DispatcharrConfigurationWrite(MatchThreshold: 55));

        var cfg = service.Get();
        Assert.Equal(55, cfg.MatchThreshold);
        Assert.Equal("http://dispatcharr.local", cfg.BaseUrl);
        Assert.Equal("SECRET-API-KEY", cfg.ApiKey);
        Assert.Equal("IPTV", cfg.TargetGroupName);
        Assert.Equal(new[] { "one" }, cfg.ProviderPriority);
        Assert.Equal("alias.json", cfg.AliasFile);
        Assert.True(cfg.AutoCreateGroups);

        // String vazia limpa; null mantém.
        service.Save(new DispatcharrConfigurationWrite(
            TargetGroupName: string.Empty,
            AliasFile: string.Empty,
            ProviderPriority: Array.Empty<string>(),
            ApiKey: null));

        var raw = ReadRaw();
        Assert.Equal(string.Empty, raw["dispatcharr_target_group_name"]);
        Assert.Equal(string.Empty, raw["dispatcharr_alias_file"]);
        Assert.Equal(string.Empty, raw["dispatcharr_provider_priority"]);
        Assert.Equal("SECRET-API-KEY", raw["dispatcharr_api_key"]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Save_rejects_out_of_range_match_threshold(int threshold)
    {
        var service = CreateService();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            service.Save(new DispatcharrConfigurationWrite(MatchThreshold: threshold)));
    }
}
