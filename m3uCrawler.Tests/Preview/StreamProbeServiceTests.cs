using m3uCrawler.Services.Preview;
using Xunit;

namespace m3uCrawler.Tests;

public class StreamProbeServiceTests
{
    private const string H264AacJson =
        "{\"streams\":[" +
        "{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":1920,\"height\":1080}," +
        "{\"codec_type\":\"audio\",\"codec_name\":\"aac\"}" +
        "],\"format\":{\"format_name\":\"mpegts\",\"duration\":\"120.5\"}}";

    private const string HevcAc3Json =
        "{\"streams\":[" +
        "{\"codec_type\":\"video\",\"codec_name\":\"hevc\",\"width\":3840,\"height\":2160}," +
        "{\"codec_type\":\"audio\",\"codec_name\":\"ac3\"}" +
        "],\"format\":{\"format_name\":\"mpegts\"}}";

    [Fact]
    public async Task Parses_h264_aac_probe_as_browser_playable()
    {
        var runner = new FakeProcessRunner { RunHandler = _ => new ProcessResult(0, H264AacJson, string.Empty, false) };
        var service = new StreamProbeService(runner, new PreviewOptions());

        var probe = await service.ProbeAsync("http://host:8080/live/u/p/1.ts", CancellationToken.None);

        Assert.NotNull(probe);
        Assert.Equal("h264", probe!.VideoCodec);
        Assert.Equal("aac", probe.AudioCodec);
        Assert.Equal(1920, probe.Width);
        Assert.Equal(1080, probe.Height);
        Assert.Equal("mpegts", probe.FormatName);
        Assert.Equal(120.5, probe.DurationSeconds);
        Assert.True(probe.PlayableByBrowser);
    }

    [Fact]
    public async Task Parses_hevc_ac3_probe_as_not_playable()
    {
        var runner = new FakeProcessRunner { RunHandler = _ => new ProcessResult(0, HevcAc3Json, string.Empty, false) };
        var service = new StreamProbeService(runner, new PreviewOptions());

        var probe = await service.ProbeAsync("http://host:8080/live/u/p/2.ts", CancellationToken.None);

        Assert.NotNull(probe);
        Assert.Equal("hevc", probe!.VideoCodec);
        Assert.Equal("ac3", probe.AudioCodec);
        Assert.False(probe.PlayableByBrowser);
    }

    [Fact]
    public async Task Cache_avoids_reexecuting_ffprobe_within_ttl()
    {
        var runner = new FakeProcessRunner { RunHandler = _ => new ProcessResult(0, H264AacJson, string.Empty, false) };
        var options = new PreviewOptions { ProbeCacheTtl = TimeSpan.FromMinutes(10) };
        var service = new StreamProbeService(runner, options);

        var first = await service.ProbeAsync("http://host/live/u/p/1.ts", CancellationToken.None);
        var second = await service.ProbeAsync("http://host/live/u/p/1.ts", CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, runner.RunCallCount);
    }

    [Fact]
    public async Task Failed_probe_is_not_cached_and_reexecutes_ffprobe()
    {
        var attempts = 0;
        var runner = new FakeProcessRunner
        {
            RunHandler = _ => ++attempts == 1
                ? new ProcessResult(1, string.Empty, "ffprobe: Connection timed out", false)
                : new ProcessResult(0, H264AacJson, string.Empty, false),
        };
        var service = new StreamProbeService(
            runner, new PreviewOptions { ProbeRetries = 0, ProbeCacheTtl = TimeSpan.FromMinutes(10) });

        var first = await service.ProbeAsync("http://host/live/u/p/fail.ts", CancellationToken.None);
        var second = await service.ProbeAsync("http://host/live/u/p/fail.ts", CancellationToken.None);

        // A primeira falha não é memorizada: a segunda chamada volta a executar o ffprobe
        // e apanha a recuperação transitória do upstream.
        Assert.Null(first);
        Assert.NotNull(second);
        Assert.Equal(2, runner.RunCallCount);
    }

    [Fact]
    public async Task Repeated_failures_are_never_cached()
    {
        var runner = new FakeProcessRunner
        {
            RunHandler = _ => new ProcessResult(1, string.Empty, "ffprobe: 403", false),
        };
        var service = new StreamProbeService(
            runner, new PreviewOptions { ProbeRetries = 0, ProbeCacheTtl = TimeSpan.FromMinutes(10) });

        Assert.Null(await service.ProbeAsync("http://host/live/u/p/always.ts", CancellationToken.None));
        Assert.Null(await service.ProbeAsync("http://host/live/u/p/always.ts", CancellationToken.None));
        Assert.Null(await service.ProbeAsync("http://host/live/u/p/always.ts", CancellationToken.None));

        Assert.Equal(3, runner.RunCallCount);
    }

    [Fact]
    public async Task Expired_cache_entry_triggers_a_new_probe()
    {
        var runner = new FakeProcessRunner { RunHandler = _ => new ProcessResult(0, H264AacJson, string.Empty, false) };
        var options = new PreviewOptions { ProbeCacheTtl = TimeSpan.Zero };
        var service = new StreamProbeService(runner, options);

        await service.ProbeAsync("http://host/live/u/p/1.ts", CancellationToken.None);
        await service.ProbeAsync("http://host/live/u/p/1.ts", CancellationToken.None);

        Assert.Equal(2, runner.RunCallCount);
    }

    [Fact]
    public async Task Non_zero_exit_returns_null_without_throwing()
    {
        var runner = new FakeProcessRunner
        {
            RunHandler = _ => new ProcessResult(1, string.Empty, "ffprobe: erro", false),
        };
        var service = new StreamProbeService(runner, new PreviewOptions());

        var probe = await service.ProbeAsync("http://host/live/u/p/1.ts", CancellationToken.None);

        Assert.Null(probe);
    }

    [Fact]
    public async Task Probe_retries_on_failure_up_to_ProbeRetries()
    {
        var runner = new FakeProcessRunner
        {
            RunHandler = _ => new ProcessResult(1, string.Empty, "ffprobe: 403", false),
        };
        var service = new StreamProbeService(runner, new PreviewOptions { ProbeRetries = 1 });

        var probe = await service.ProbeAsync("http://host/live/u/p/retry.ts", CancellationToken.None);

        Assert.Null(probe);
        Assert.Equal(2, runner.RunCallCount);
    }

    [Fact]
    public async Task Probe_succeeds_after_a_transient_failure()
    {
        var attempts = 0;
        var runner = new FakeProcessRunner
        {
            RunHandler = _ => ++attempts == 1
                ? new ProcessResult(1, string.Empty, "ffprobe: 403", false)
                : new ProcessResult(0, H264AacJson, string.Empty, false),
        };
        var service = new StreamProbeService(runner, new PreviewOptions { ProbeRetries = 1 });

        var probe = await service.ProbeAsync("http://host/live/u/p/transient.ts", CancellationToken.None);

        Assert.NotNull(probe);
        Assert.True(probe!.PlayableByBrowser);
        Assert.Equal(2, runner.RunCallCount);
    }

    [Fact]
    public void Parse_invalid_json_returns_null()
    {
        Assert.Null(StreamProbeService.Parse("not-json"));
        Assert.Null(StreamProbeService.Parse(string.Empty));
        Assert.Null(StreamProbeService.Parse("{\"other\":1}"));
    }

    [Theory]
    [InlineData("h264", "aac", true)]
    [InlineData("H264", "AAC", true)]
    [InlineData("h264", null, true)]
    [InlineData("vp9", "opus", true)]
    [InlineData("av1", "mp3", true)]
    [InlineData("hevc", "aac", false)]
    [InlineData("h264", "ac3", false)]
    [InlineData(null, "aac", false)]
    public void IsBrowserPlayable_applies_codec_heuristic(string? video, string? audio, bool expected)
    {
        Assert.Equal(expected, StreamProbe.IsBrowserPlayable(video, audio));
    }

    [Fact]
    public void Hash_is_deterministic_and_does_not_contain_the_url()
    {
        const string url = "http://host:8080/live/alice/secret/12345.ts";
        var hash = StreamProbeService.HashUrl(url);

        Assert.Equal(hash, StreamProbeService.HashUrl(url));
        Assert.NotEqual(hash, StreamProbeService.HashUrl(url + "x"));
        Assert.DoesNotContain("alice", hash);
        Assert.DoesNotContain("secret", hash);
        Assert.DoesNotContain("host", hash);
        Assert.Equal(64, hash.Length);
    }
}
