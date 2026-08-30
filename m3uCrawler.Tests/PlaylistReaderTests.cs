using m3uCrawler.Services.Sync;
using Xunit;

namespace m3uCrawler.Tests;

public class PlaylistReaderTests
{
    [Fact]
    public void Parses_simple_playlist()
    {
        var content = "#EXTM3U\n#EXTINF:-1 tvg-name=\"RTP1\" group-title=\"Portugal\",RTP1\nhttps://provider_a.example/rtp1\n#EXTINF:-1,SIC\nhttps://provider_a.example/sic\n";
        var streams = PlaylistReader.Parse(content, defaultProvider: null);
        Assert.Equal(2, streams.Count);
        Assert.Equal("RTP1", streams[0].Title);
        Assert.Equal("Portugal", streams[0].Group);
        Assert.Equal("provider_a.example", streams[0].Provider);
    }

    [Fact]
    public void Falls_back_to_default_provider()
    {
        var content = "#EXTM3U\n#EXTINF:-1,X\nhttps://anywhere/x\n";
        var streams = PlaylistReader.Parse(content, defaultProvider: "Custom");
        Assert.Equal("Custom", streams[0].Provider);
    }

    [Fact]
    public async Task ReadAsync_throws_for_missing_file()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => PlaylistReader.ReadAsync("Z:/no-such-playlist.m3u"));
    }
}
