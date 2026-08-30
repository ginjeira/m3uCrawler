using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

public class M3uParserServiceTests
{
    private readonly M3uParserService _parser = new();

    [Fact]
    public void Preserves_extinf_metadata()
    {
        var content = "#EXTM3U\n#EXTINF:-1 tvg-name=\"RTP1\" tvg-logo=\"http://l/rtp.png\" group-title=\"Portugal\",RTP1\nhttps://x/rtp1\n";
        var streams = _parser.Parse(content);

        Assert.Single(streams);
        var s = streams[0];
        Assert.Equal("https://x/rtp1", s.Url);
        Assert.Equal("RTP1", s.Title);
        Assert.Equal("Portugal", s.Group);
        Assert.Equal("http://l/rtp.png", s.Logo);
        Assert.StartsWith("#EXTINF", s.OriginalExtInf);
    }

    [Fact]
    public void Parses_multiple_entries()
    {
        var content = "#EXTM3U\n#EXTINF:-1,RTP1\nhttps://x/1\n#EXTINF:-1,SIC\nhttps://x/2\n";
        var streams = _parser.Parse(content);
        Assert.Equal(2, streams.Count);
    }

    [Fact]
    public void Distinguishes_channel_playlist_from_hls_master()
    {
        var channel = "#EXTM3U\n#EXTINF:-1,RTP1\nhttps://x/1\n";
        var master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000,tvg-name=\"V1\",group-title=\"G\"\nhttps://x/v1.m3u8\n";

        var channelStreams = _parser.Parse(channel);
        var masterStreams = _parser.Parse(master);

        Assert.Single(channelStreams);
        Assert.Single(masterStreams);
        Assert.StartsWith("#EXTINF", channelStreams[0].OriginalExtInf);
        Assert.Equal(string.Empty, masterStreams[0].OriginalExtInf);
    }

    [Fact]
    public void Returns_empty_for_null_or_empty_content()
    {
        Assert.Empty(_parser.Parse(null));
        Assert.Empty(_parser.Parse(""));
    }
}
