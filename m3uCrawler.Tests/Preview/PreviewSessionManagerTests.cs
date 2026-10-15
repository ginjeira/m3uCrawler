using m3uCrawler.Services.Preview;
using Xunit;

namespace m3uCrawler.Tests;

public sealed class PreviewSessionManagerTests : IAsyncLifetime
{
    private const string H264AacJson =
        "{\"streams\":[" +
        "{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":1280,\"height\":720}," +
        "{\"codec_type\":\"audio\",\"codec_name\":\"aac\"}" +
        "],\"format\":{\"format_name\":\"mpegts\"}}";

    private const string HevcAc3Json =
        "{\"streams\":[" +
        "{\"codec_type\":\"video\",\"codec_name\":\"hevc\",\"width\":3840,\"height\":2160}," +
        "{\"codec_type\":\"audio\",\"codec_name\":\"ac3\"}" +
        "],\"format\":{\"format_name\":\"mpegts\"}}";

    private const string StreamUrl = "http://host.example.com:8080/live/u/p/1.ts";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "m3uCrawler-preview-tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch { /* melhor esforço */ }
        return Task.CompletedTask;
    }

    private static PreviewOptions Options(TimeSpan? idle = null, TimeSpan? max = null, int concurrency = 2, bool transcode = false) => new()
    {
        MaxConcurrentSessions = concurrency,
        IdleTimeout = idle ?? TimeSpan.FromMinutes(5),
        MaxDuration = max ?? TimeSpan.FromMinutes(5),
        TranscodeNonPlayable = transcode,
    };

    private FakeProcessRunner RunnerFor(string probeJson) => new()
    {
        RunHandler = _ => new ProcessResult(0, probeJson, string.Empty, false),
    };

    private static Func<string, CancellationToken, Task<StreamReference?>> ResolverFor(string url) =>
        (_, _) => Task.FromResult<StreamReference?>(new StreamReference("channel", "1", url, "Test"));

    private static Func<string, CancellationToken, Task<StreamReference?>> UnknownResolver() =>
        (_, _) => Task.FromResult<StreamReference?>(null);

    private StreamProbeService ProbeFor(FakeProcessRunner runner) => new(runner, Options());

    private PreviewSessionManager CreateManager(
        FakeProcessRunner runner,
        StreamProbeService probe,
        PreviewOptions options,
        Func<string, CancellationToken, Task<StreamReference?>> resolver) =>
        new(options, runner, probe, resolver, _root);

    [Fact]
    public async Task Start_playable_resolves_hls_session_with_manifest_and_thumbnail()
    {
        var runner = RunnerFor(H264AacJson);
        await using var manager = CreateManager(runner, ProbeFor(runner), Options(), ResolverFor(StreamUrl));

        var result = await manager.StartAsync("1", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("hls", result!.Kind);
        Assert.True(result.Playable);
        Assert.Null(result.Reason);
        Assert.Equal($"/api/preview/{result.SessionId}/index.m3u8", result.ManifestUrl);
        Assert.Equal($"/api/preview/{result.SessionId}/thumb.jpg", result.ThumbnailUrl);
        Assert.NotNull(result.Probe);

        Assert.Single(runner.Starts);
        Assert.Equal("ffmpeg", runner.Starts[0].Exe);
        Assert.Contains("copy", runner.Starts[0].Args);
        Assert.Equal(Path.Combine(_root, result.SessionId), runner.Starts[0].WorkingDir);
    }

    [Fact]
    public async Task Start_unknown_reference_returns_null()
    {
        var runner = RunnerFor(H264AacJson);
        await using var manager = CreateManager(runner, ProbeFor(runner), Options(), UnknownResolver());

        var result = await manager.StartAsync("missing", CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, runner.RunCallCount);
        Assert.Empty(runner.Starts);
    }

    [Fact]
    public async Task Start_returns_busy_when_concurrency_cap_reached()
    {
        var runner = RunnerFor(H264AacJson);
        await using var manager = CreateManager(runner, ProbeFor(runner), Options(concurrency: 2), ResolverFor(StreamUrl));

        var first = await manager.StartAsync("1", CancellationToken.None);
        var second = await manager.StartAsync("2", CancellationToken.None);
        var third = await manager.StartAsync("3", CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);
        Assert.Equal("busy", third!.Reason);
        Assert.Equal(string.Empty, third.SessionId);
        Assert.Equal(2, manager.ActiveSessionCount);
        Assert.Equal(2, runner.Starts.Count);
    }

    [Fact]
    public async Task Start_unsupported_codec_uses_snapshot_path()
    {
        var runner = RunnerFor(HevcAc3Json);
        await using var manager = CreateManager(runner, ProbeFor(runner), Options(), ResolverFor(StreamUrl));

        var result = await manager.StartAsync("1", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("snapshot", result!.Kind);
        Assert.False(result.Playable);
        Assert.Null(result.ManifestUrl);
        Assert.Equal($"/api/preview/{result.SessionId}/thumb.jpg", result.ThumbnailUrl);

        var args = runner.Starts[0].Args;
        Assert.Contains("scale=480:-1", args);
        Assert.Contains("-frames:v", args);
        Assert.DoesNotContain("copy", args);
    }

    [Fact]
    public async Task Start_unsupported_codec_uses_hls_transcode_when_enabled()
    {
        var runner = RunnerFor(HevcAc3Json);
        await using var manager = CreateManager(runner, ProbeFor(runner), Options(transcode: true), ResolverFor(StreamUrl));

        var result = await manager.StartAsync("1", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("hls", result!.Kind);
        Assert.False(result.Playable);
        Assert.Equal($"/api/preview/{result.SessionId}/index.m3u8", result.ManifestUrl);
        Assert.Equal($"/api/preview/{result.SessionId}/thumb.jpg", result.ThumbnailUrl);

        var args = runner.Starts[0].Args;
        var list = args.ToList();
        Assert.Equal("libx264", list[list.IndexOf("-c:v") + 1]);
        Assert.Equal("aac", list[list.IndexOf("-c:a") + 1]);
        Assert.DoesNotContain("copy", args);
        Assert.DoesNotContain("scale=480:-1", args);
    }

    [Fact]
    public async Task Touch_renews_idle_timeout()
    {
        var runner = RunnerFor(H264AacJson);
        var options = Options(idle: TimeSpan.FromMilliseconds(300));
        await using var manager = CreateManager(runner, ProbeFor(runner), options, ResolverFor(StreamUrl));

        var result = await manager.StartAsync("1", CancellationToken.None);
        Assert.NotNull(result);
        Assert.True(manager.HasSession(result!.SessionId));

        await Task.Delay(200);
        manager.Touch(result.SessionId);
        await Task.Delay(200);

        Assert.True(manager.HasSession(result.SessionId));

        await Task.Delay(700);
        Assert.False(manager.HasSession(result.SessionId));
    }

    [Fact]
    public async Task Session_expires_when_max_duration_is_exceeded()
    {
        var runner = RunnerFor(H264AacJson);
        var options = Options(idle: TimeSpan.FromMinutes(5), max: TimeSpan.FromMilliseconds(300));
        await using var manager = CreateManager(runner, ProbeFor(runner), options, ResolverFor(StreamUrl));

        var result = await manager.StartAsync("1", CancellationToken.None);
        Assert.NotNull(result);

        manager.Touch(result!.SessionId);
        Assert.True(manager.HasSession(result.SessionId));

        await Task.Delay(800);

        Assert.False(manager.HasSession(result.SessionId));
        Assert.Equal(0, manager.ActiveSessionCount);
    }

    [Fact]
    public async Task Stop_kills_process_and_removes_session_directory()
    {
        var runner = RunnerFor(H264AacJson);
        await using var manager = CreateManager(runner, ProbeFor(runner), Options(), ResolverFor(StreamUrl));

        var result = await manager.StartAsync("1", CancellationToken.None);
        Assert.NotNull(result);
        var dir = Path.Combine(_root, result!.SessionId);
        Assert.True(Directory.Exists(dir));

        await manager.StopAsync(result.SessionId);

        Assert.False(manager.HasSession(result.SessionId));
        Assert.False(Directory.Exists(dir));
        Assert.True(runner.Handles[0].Killed);
    }

    [Fact]
    public async Task Process_dying_on_its_own_does_not_break_the_manager()
    {
        var runner = RunnerFor(H264AacJson);
        await using var manager = CreateManager(runner, ProbeFor(runner), Options(), ResolverFor(StreamUrl));

        var result = await manager.StartAsync("1", CancellationToken.None);
        Assert.NotNull(result);

        runner.Handles[0].MarkExited(1);

        manager.Touch(result!.SessionId);
        Assert.True(manager.HasSession(result.SessionId));
        Assert.Equal(1, runner.Handles[0].ExitCode);

        await manager.StopAsync(result.SessionId);
        Assert.False(manager.HasSession(result.SessionId));
    }

    [Fact]
    public async Task TryResolveFile_serves_only_files_inside_session_and_blocks_traversal()
    {
        var runner = RunnerFor(H264AacJson);
        await using var manager = CreateManager(runner, ProbeFor(runner), Options(), ResolverFor(StreamUrl));

        var result = await manager.StartAsync("1", CancellationToken.None);
        Assert.NotNull(result);
        var id = result!.SessionId;
        var dir = Path.Combine(_root, id);

        File.WriteAllText(Path.Combine(dir, "index.m3u8"), "#EXTM3U");
        File.WriteAllText(Path.Combine(dir, "seg00000.ts"), "segment");
        File.WriteAllText(Path.Combine(dir, "thumb.jpg"), "jpeg");

        var outside = Path.Combine(_root, "secret.txt");
        File.WriteAllText(outside, "top-secret");

        Assert.True(manager.TryResolveFile(id, "index.m3u8", out var manifestPath, out var manifestType));
        Assert.Equal(Path.Combine(dir, "index.m3u8"), manifestPath);
        Assert.Equal("application/vnd.apple.mpegurl", manifestType);

        Assert.True(manager.TryResolveFile(id, "seg00000.ts", out _, out var tsType));
        Assert.Equal("video/mp2t", tsType);

        Assert.True(manager.TryResolveFile(id, "thumb.jpg", out _, out var jpgType));
        Assert.Equal("image/jpeg", jpgType);

        Assert.False(manager.TryResolveFile(id, "../secret.txt", out _, out _));
        Assert.False(manager.TryResolveFile(id, "../../etc/passwd", out _, out _));
        Assert.False(manager.TryResolveFile(id, outside, out _, out _));
        Assert.False(manager.TryResolveFile(id, "does-not-exist.ts", out _, out _));
        Assert.False(manager.TryResolveFile("unknown-session", "index.m3u8", out _, out _));
        Assert.False(manager.TryResolveFile(id, string.Empty, out _, out _));
    }

    [Fact]
    public async Task DisposeAsync_kills_all_processes_and_cleans_up()
    {
        var runner = RunnerFor(H264AacJson);
        var manager = CreateManager(runner, ProbeFor(runner), Options(), ResolverFor(StreamUrl));

        var first = await manager.StartAsync("1", CancellationToken.None);
        var second = await manager.StartAsync("2", CancellationToken.None);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, manager.ActiveSessionCount);

        await manager.DisposeAsync();

        Assert.Equal(0, manager.ActiveSessionCount);
        Assert.False(manager.HasSession(first!.SessionId));
        Assert.False(manager.HasSession(second!.SessionId));
        Assert.False(Directory.Exists(Path.Combine(_root, first.SessionId)));
        Assert.False(Directory.Exists(Path.Combine(_root, second.SessionId)));
        Assert.All(runner.Handles, handle => Assert.True(handle.Killed));
    }
}
