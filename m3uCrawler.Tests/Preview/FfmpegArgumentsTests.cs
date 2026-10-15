using m3uCrawler.Services;
using m3uCrawler.Services.Preview;
using Xunit;

namespace m3uCrawler.Tests;

public class FfmpegArgumentsTests
{
    private const string Url = "http://host.example.com:8080/live/u/p/1.ts";
    private const string UserAgent = "Mozilla/5.0 (Test)";

    [Fact]
    public void HlsRemux_produces_expected_argument_list()
    {
        var outDir = Path.Combine(Path.GetTempPath(), "preview-out");

        var actual = FfmpegArguments.HlsRemux(Url, outDir, UserAgent);

        var expected = new[]
        {
            "-hide_banner",
            "-loglevel", "error",
            "-user_agent", UserAgent,
            "-reconnect", "1",
            "-reconnect_streamed", "1",
            "-reconnect_delay_max", "5",
            "-i", Url,
            "-c", "copy",
            "-f", "hls",
            "-hls_time", "2",
            "-hls_list_size", "6",
            "-hls_flags", "delete_segments+append_list+omit_endlist",
            "-hls_segment_filename", Path.Combine(outDir, "seg%05d.ts"),
            Path.Combine(outDir, "index.m3u8"),
        };

        Assert.Equal(expected, actual);
        Assert.Equal(Path.Combine(outDir, "index.m3u8"), actual[^1]);
        Assert.Contains("copy", actual);
        AssertReconnectBeforeInput(actual);
    }

    [Fact]
    public void HlsTranscode_produces_expected_argument_list()
    {
        var outDir = Path.Combine(Path.GetTempPath(), "preview-transcode-out");

        var actual = FfmpegArguments.HlsTranscode(Url, outDir, UserAgent);

        var expected = new[]
        {
            "-hide_banner",
            "-loglevel", "error",
            "-user_agent", UserAgent,
            "-reconnect", "1",
            "-reconnect_streamed", "1",
            "-reconnect_delay_max", "5",
            "-i", Url,
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-tune", "zerolatency",
            "-c:a", "aac",
            "-b:a", "128k",
            "-f", "hls",
            "-hls_time", "2",
            "-hls_list_size", "6",
            "-hls_flags", "delete_segments+append_list+omit_endlist",
            "-hls_segment_filename", Path.Combine(outDir, "seg%05d.ts"),
            Path.Combine(outDir, "index.m3u8"),
        };

        Assert.Equal(expected, actual);
        var list = actual.ToList();
        Assert.Equal("libx264", list[list.IndexOf("-c:v") + 1]);
        Assert.Equal("aac", list[list.IndexOf("-c:a") + 1]);
        Assert.Equal("128k", list[list.IndexOf("-b:a") + 1]);
        Assert.Equal("hls", list[list.IndexOf("-f") + 1]);
        Assert.DoesNotContain("copy", actual);
        Assert.Equal(Path.Combine(outDir, "index.m3u8"), actual[^1]);
        AssertReconnectBeforeInput(actual);
    }

    [Fact]
    public void Thumbnail_seeks_before_input_and_scales_to_480()
    {
        var outPath = Path.Combine(Path.GetTempPath(), "thumb.jpg");
        var at = TimeSpan.FromSeconds(1);

        var actual = FfmpegArguments.Thumbnail(Url, outPath, UserAgent, at).ToList();

        Assert.True(actual.IndexOf("-ss") < actual.IndexOf("-i"));
        Assert.Equal("1", actual[actual.IndexOf("-ss") + 1]);
        Assert.Equal("scale=480:-1", actual[actual.IndexOf("-vf") + 1]);
        Assert.Equal("1", actual[actual.IndexOf("-frames:v") + 1]);
        Assert.Equal("-y", actual[^2]);
        Assert.Equal(outPath, actual[^1]);
        AssertReconnectBeforeInput(actual);
    }

    [Fact]
    public void Thumbnail_formats_fractional_seconds_invariantly()
    {
        var actual = FfmpegArguments.Thumbnail(Url, "/tmp/t.jpg", UserAgent, TimeSpan.FromSeconds(2.5)).ToList();

        Assert.Equal("2.5", actual[actual.IndexOf("-ss") + 1]);
    }

    [Fact]
    public void Probe_produces_json_show_streams_and_format()
    {
        var actual = FfmpegArguments.Probe(Url, UserAgent);

        var expected = new[]
        {
            "-v", "error",
            "-user_agent", UserAgent,
            "-reconnect", "1",
            "-reconnect_streamed", "1",
            "-reconnect_delay_max", "5",
            "-print_format", "json",
            "-show_streams",
            "-show_format",
            Url,
        };

        Assert.Equal(expected, actual);
        AssertReconnectBeforeInput(actual);
    }

    /// <summary>
    /// As três opções de reconexão têm de aparecer (nesta ordem) antes do input URL/posicional.
    /// </summary>
    private static void AssertReconnectBeforeInput(IReadOnlyList<string> args)
    {
        var list = args.ToList();
        var reconnect = list.IndexOf("-reconnect");
        Assert.True(reconnect >= 0, "Faltam opções de reconexão HTTP.");
        Assert.Equal("1", list[reconnect + 1]);
        Assert.Equal("-reconnect_streamed", list[reconnect + 2]);
        Assert.Equal("1", list[reconnect + 3]);
        Assert.Equal("-reconnect_delay_max", list[reconnect + 4]);
        Assert.Equal("5", list[reconnect + 5]);

        var input = list.IndexOf("-i");
        if (input < 0)
        {
            input = list.Count - 1; // ffprobe: URL posicional no fim.
        }
        Assert.True(reconnect < input, "As opções de reconexão devem preceder o input.");
        Assert.True(reconnect + 5 < input, "As opções de reconexão devem preceder o input.");
    }

    [Fact]
    public void Sanitized_argument_line_never_exposes_credentials()
    {
        const string urlWithCreds = "http://host.example.com/live/alice/secretpass/12345.ts";
        var args = FfmpegArguments.Probe(urlWithCreds, UserAgent);

        var sanitized = CredentialSanitizer.SanitizeSensitiveText(string.Join(' ', args));

        Assert.DoesNotContain("secretpass", sanitized);
        Assert.DoesNotContain("alice", CredentialSanitizer.SanitizeUrl(urlWithCreds));
        Assert.Contains("/live/***/***/", sanitized);
    }
}
