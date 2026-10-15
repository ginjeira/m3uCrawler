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

    private static PreviewOptions Options(
        TimeSpan? idle = null,
        TimeSpan? max = null,
        int concurrency = 2,
        bool transcode = false,
        TimeSpan? ready = null) => new()
    {
        MaxConcurrentSessions = concurrency,
        IdleTimeout = idle ?? TimeSpan.FromMinutes(5),
        MaxDuration = max ?? TimeSpan.FromMinutes(5),
        TranscodeNonPlayable = transcode,
        ReadyTimeout = ready ?? TimeSpan.FromSeconds(5),
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
        Func<string, CancellationToken, Task<StreamReference?>> resolver,
        Action<string>? log = null) =>
        new(options, runner, probe, resolver, _root, log);

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
    public async Task Start_waits_for_manifest_created_after_a_delay()
    {
        var runner = RunnerFor(H264AacJson);
        runner.ArtifactWriter = async (args, workDir) =>
        {
            var target = args.First(a => a.EndsWith("index.m3u8", StringComparison.Ordinal));
            await Task.Delay(300).ConfigureAwait(false);
            FakeProcessRunner.WriteArtifactFile(target, workDir);
        };
        await using var manager = CreateManager(
            runner, ProbeFor(runner), Options(ready: TimeSpan.FromSeconds(3)), ResolverFor(StreamUrl));

        var result = await manager.StartAsync("1", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("hls", result!.Kind);
        Assert.Null(result.Reason);
        Assert.Equal($"/api/preview/{result.SessionId}/index.m3u8", result.ManifestUrl);
        Assert.Equal($"/api/preview/{result.SessionId}/thumb.jpg", result.ThumbnailUrl);
        Assert.Single(runner.Starts);
        Assert.True(File.Exists(Path.Combine(_root, result.SessionId, "index.m3u8")));
    }

    [Fact]
    public async Task Start_returns_timeout_and_no_urls_when_manifest_never_appears()
    {
        var runner = RunnerFor(H264AacJson);
        runner.AutoCreateArtifacts = false;
        await using var manager = CreateManager(
            runner, ProbeFor(runner),
            Options(ready: TimeSpan.FromMilliseconds(250)), ResolverFor(StreamUrl));

        var result = await manager.StartAsync("1", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("preview-start-timeout", result!.Reason);
        Assert.Null(result.ManifestUrl);
        Assert.Null(result.ThumbnailUrl);
        Assert.Equal(string.Empty, result.SessionId);
        Assert.False(manager.HasSession(result.SessionId));
        Assert.Equal(0, manager.ActiveSessionCount);
        // HLS falha → fallback snapshot → também falha: dois arranques, nenhuma sessão.
        Assert.Equal(2, runner.Starts.Count);
    }

    [Fact]
    public async Task Start_classifies_network_failure_as_stream_unreachable_and_logs_late_stderr()
    {
        var logs = new List<string>();
        var gate = new object();
        var runner = RunnerFor(H264AacJson);
        runner.AutoCreateArtifacts = false;
        // stderr configurado mas só visível após o exit (saída tardia): se o manager o
        // lesse antes de parar o processo, viria vazio.
        runner.HandleConfigurator = (_, handle) => handle.StderrTail = "Connection timed out";

        await using var manager = CreateManager(
            runner, ProbeFor(runner),
            Options(ready: TimeSpan.FromMilliseconds(250)), ResolverFor(StreamUrl),
            log: message => { lock (gate) logs.Add(message); });

        var result = await manager.StartAsync("1", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("preview-stream-unreachable", result!.Reason);
        Assert.Null(result.ManifestUrl);
        Assert.Null(result.ThumbnailUrl);
        Assert.Equal(string.Empty, result.SessionId);
        Assert.Equal(2, runner.Starts.Count);

        string joined;
        lock (gate) joined = string.Join('\n', logs);
        // ExitCode lido APÓS o exit (-1 do Kill) e stderr real presente no log.
        Assert.Contains("falhou (-1) Connection timed out", joined);
        Assert.Contains("preview-stream-unreachable", joined);
        Assert.DoesNotContain("host.example.com", joined);
    }

    [Fact]
    public async Task Start_classifies_non_network_failure_as_start_timeout()
    {
        var logs = new List<string>();
        var gate = new object();
        var runner = RunnerFor(H264AacJson);
        runner.AutoCreateArtifacts = false;
        runner.HandleConfigurator = (_, handle) => handle.StderrTail = "Invalid argument";

        await using var manager = CreateManager(
            runner, ProbeFor(runner),
            Options(ready: TimeSpan.FromMilliseconds(250)), ResolverFor(StreamUrl),
            log: message => { lock (gate) logs.Add(message); });

        var result = await manager.StartAsync("1", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("preview-start-timeout", result!.Reason);
        Assert.Null(result.ManifestUrl);
        Assert.Null(result.ThumbnailUrl);

        string joined;
        lock (gate) joined = string.Join('\n', logs);
        Assert.Contains("Invalid argument", joined);
        Assert.Contains("preview-start-timeout", joined);
    }

    [Fact]
    public void Fake_process_reveals_exit_code_and_stderr_only_after_exit()
    {
        var handle = new FakeProcessHandle("Connection refused");

        // Processo vivo: sem exit code e sem stderr (saída tardia simulada).
        Assert.Null(handle.ExitCode);
        Assert.Equal(string.Empty, handle.StderrTail);

        handle.MarkExited(9);

        Assert.Equal(9, handle.ExitCode);
        Assert.Equal("Connection refused", handle.StderrTail);
    }

    [Fact]
    public async Task Start_assumes_playable_and_starts_hls_when_probe_fails()
    {
        var runner = new FakeProcessRunner
        {
            RunHandler = _ => new ProcessResult(1, string.Empty, "ffprobe: 403", false),
        };
        await using var manager = CreateManager(runner, ProbeFor(runner), Options(), ResolverFor(StreamUrl));

        var result = await manager.StartAsync("1", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("hls", result!.Kind);
        Assert.True(result.Playable);
        Assert.Null(result.Reason);
        Assert.NotNull(result.ManifestUrl);
        Assert.Single(runner.Starts);
        Assert.Contains("copy", runner.Starts[0].Args);
        // ProbeRetries default 1 → 2 tentativas de ffprobe antes de assumir playable.
        Assert.Equal(2, runner.RunCallCount);
    }

    [Fact]
    public async Task Start_falls_back_to_snapshot_when_hls_fails()
    {
        var runner = RunnerFor(H264AacJson);
        runner.ArtifactWriter = (args, workDir) =>
        {
            var thumb = args.FirstOrDefault(a => a.EndsWith("thumb.jpg", StringComparison.Ordinal));
            if (thumb is not null)
            {
                FakeProcessRunner.WriteArtifactFile(thumb, workDir);
            }
            return Task.CompletedTask;
        };
        await using var manager = CreateManager(
            runner, ProbeFor(runner),
            Options(ready: TimeSpan.FromMilliseconds(250)), ResolverFor(StreamUrl));

        var result = await manager.StartAsync("1", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("snapshot", result!.Kind);
        Assert.Equal("hls-failed-snapshot", result.Reason);
        Assert.Null(result.ManifestUrl);
        Assert.Equal($"/api/preview/{result.SessionId}/thumb.jpg", result.ThumbnailUrl);
        Assert.Equal(2, runner.Starts.Count);
        Assert.True(File.Exists(Path.Combine(_root, result.SessionId, "thumb.jpg")));
    }

    [Fact]
    public async Task Start_never_logs_the_stream_url()
    {
        var logs = new List<string>();
        var gate = new object();
        var runner = RunnerFor(H264AacJson);
        await using var manager = CreateManager(
            runner, ProbeFor(runner), Options(), ResolverFor(StreamUrl),
            log: message => { lock (gate) logs.Add(message); });

        var result = await manager.StartAsync("1", CancellationToken.None);
        Assert.NotNull(result);

        string joined;
        lock (gate) joined = string.Join('\n', logs);
        Assert.Contains("🎬 preview", joined);
        Assert.Contains("kind=hls", joined);
        Assert.DoesNotContain("host.example.com", joined);
        Assert.DoesNotContain("/live/u/p/1.ts", joined);
    }

    [Fact]
    public async Task Touch_renews_idle_timeout()
    {
        var runner = RunnerFor(H264AacJson);
        // Idle curto o suficiente para o teste correr depressa, mas com margem ampla
        // face ao intervalo entre Touch (50 ms): um Touch a cada 50 ms renova sempre o
        // idle de 1 s, mesmo que o scheduling atrase um delay. Não há asserções de
        // precisão de milissegundos.
        var options = Options(idle: TimeSpan.FromSeconds(1));
        await using var manager = CreateManager(runner, ProbeFor(runner), options, ResolverFor(StreamUrl));

        var result = await manager.StartAsync("1", CancellationToken.None);
        Assert.NotNull(result);
        var id = result!.SessionId;
        Assert.True(manager.HasSession(id));

        // Renovação: manter a sessão "tocada" durante ~2,5 s (2,5× o idle). Sem Touch,
        // o sweeper tê-la-ia expirado ao fim de ~1 s; uma sessão tocada continua viva.
        var touchDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2.5);
        while (DateTimeOffset.UtcNow < touchDeadline)
        {
            manager.Touch(id);
            await Task.Delay(50);
        }
        Assert.True(manager.HasSession(id));

        // Sem mais actividade, a sessão acaba por expirar: polling paciente em vez de
        // depender do instante exacto em que o sweeper corre.
        Assert.True(await WaitUntilAsync(() => !manager.HasSession(id), TimeSpan.FromSeconds(8)));
        Assert.False(manager.HasSession(id));
        Assert.Equal(0, manager.ActiveSessionCount);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(25);
        }
        return condition();
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
