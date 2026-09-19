using System;
using System.IO;
using m3uCrawler.Services.Configuration;
using m3uCrawler.Services.Dispatcharr;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W6c — O resultado do último teste de ligação ao Dispatcharr é persistido
/// no <see cref="AppSettingsStore"/> existente (não num segundo ficheiro) e
/// não contém segredos.
/// </summary>
public sealed class DispatcharrConnectionTestStoreTests : IDisposable
{
    private readonly string _dir;

    public DispatcharrConnectionTestStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"w6c-test-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Never_tested_returns_null()
    {
        var store = new DispatcharrConnectionTestStore(new AppSettingsStore(_dir));
        Assert.Null(store.Load());
    }

    [Fact]
    public void Saved_connected_test_round_trips_via_app_settings()
    {
        var appSettings = new AppSettingsStore(_dir);
        var store = new DispatcharrConnectionTestStore(appSettings);
        var testedAt = new DateTimeOffset(2026, 9, 19, 10, 30, 0, TimeSpan.Zero);

        store.Save(new DispatcharrTestRecord(
            DispatcharrConnectionStatus.Connected, "0.30.0", testedAt));

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(DispatcharrConnectionStatus.Connected, loaded!.Status);
        Assert.Equal("0.30.0", loaded.Version);
        Assert.Equal(testedAt, loaded.TestedAtUtc);
        Assert.True(loaded.IsConnected);

        // É o mesmo settings store existente — app_settings.json.
        Assert.True(File.Exists(Path.Combine(_dir, "app_settings.json")));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Failed_test_is_persisted_and_not_connected()
    {
        var store = new DispatcharrConnectionTestStore(new AppSettingsStore(_dir));

        store.Save(new DispatcharrTestRecord(
            DispatcharrConnectionStatus.Unreachable, null, DateTimeOffset.UtcNow));

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(DispatcharrConnectionStatus.Unreachable, loaded!.Status);
        Assert.False(loaded.IsConnected);
    }

    [Fact]
    public void Later_test_overwrites_previous_result()
    {
        var store = new DispatcharrConnectionTestStore(new AppSettingsStore(_dir));
        store.Save(new DispatcharrTestRecord(
            DispatcharrConnectionStatus.Connected, "1.0.0", DateTimeOffset.UtcNow));
        store.Save(new DispatcharrTestRecord(
            DispatcharrConnectionStatus.AuthenticationFailed, null, DateTimeOffset.UtcNow));

        var loaded = store.Load();
        Assert.Equal(DispatcharrConnectionStatus.AuthenticationFailed, loaded!.Status);
        Assert.Null(loaded.Version);
    }
}
