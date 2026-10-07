using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Sync;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Contrato W3 — parsing M3U (<c>docs/Reestructure/04-PLAYLIST-STREAM.md</c> §7).
/// Testes determinísticos, sem rede: header #EXTM3U obrigatório, entrada
/// #EXTINF+URL http/https, malformado registado sem abortar, alvo não
/// utilizável distinto de malformado, metadados benignos e sanitização.
/// </summary>
public class WaveW3M3uParsingContractTests
{
    private readonly M3uParserService _parser = new();

    // ===================== Básico =====================

    [Fact]
    public void Basic_http_entry_is_success()
    {
        var result = _parser.ParseDetailed("#EXTM3U\n#EXTINF:-1,RTP1\nhttp://x/1\n");

        Assert.Equal(M3uPlaylistStatus.Success, result.Status);
        Assert.Single(result.Streams);
        Assert.Equal(1, result.ValidCount);
        Assert.Equal(0, result.MalformedCount);
        Assert.Equal(0, result.UnusableTargetCount);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("http://x/1", result.Streams[0].Url);
    }

    [Fact]
    public void Basic_https_entry_is_success()
    {
        var result = _parser.ParseDetailed("#EXTM3U\n#EXTINF:-1,RTP1\nhttps://x/1\n");

        Assert.Equal(M3uPlaylistStatus.Success, result.Status);
        Assert.Single(result.Streams);
        Assert.Equal(1, result.ValidCount);
    }

    [Fact]
    public void Header_is_case_insensitive_and_bom_is_tolerated()
    {
        var result = _parser.ParseDetailed("\uFEFF#extm3u\n#EXTINF:-1,RTP1\nhttps://x/1\n");

        Assert.Equal(M3uPlaylistStatus.Success, result.Status);
        Assert.Single(result.Streams);
    }

    // ===================== Cabeçalho =====================

    [Fact]
    public void Missing_header_is_failed_with_zero_streams_and_entries_not_ingested()
    {
        var result = _parser.ParseDetailed("#EXTINF:-1,RTP1\nhttps://x/1\n");

        Assert.Equal(M3uPlaylistStatus.Failed, result.Status);
        Assert.Empty(result.Streams);
        Assert.Equal(0, result.ValidCount);
        Assert.Single(result.Diagnostics);
        Assert.Equal(M3uParseDiagnosticKind.MissingHeader, result.Diagnostics[0].Kind);
    }

    [Fact]
    public void Header_only_is_failed_with_zero_streams()
    {
        var result = _parser.ParseDetailed("#EXTM3U\n");

        Assert.Equal(M3uPlaylistStatus.Failed, result.Status);
        Assert.Empty(result.Streams);
        Assert.Equal(0, result.ValidCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  \t \n")]
    public void Null_empty_or_whitespace_is_failed_deterministic(string? content)
    {
        var result = _parser.ParseDetailed(content);

        Assert.Equal(M3uPlaylistStatus.Failed, result.Status);
        Assert.Empty(result.Streams);
        Assert.Equal(0, result.ValidCount);
        Assert.Equal(0, result.MalformedCount);
        Assert.Equal(M3uParseDiagnosticKind.MissingHeader, result.Diagnostics[0].Kind);
    }

    // ===================== Malformados =====================

    [Fact]
    public void Uri_without_extinf_is_malformed_and_not_streamed()
    {
        var result = _parser.ParseDetailed("#EXTM3U\nhttps://x/1\n");

        Assert.Equal(M3uPlaylistStatus.Failed, result.Status);
        Assert.Empty(result.Streams);
        Assert.Equal(1, result.MalformedCount);
        Assert.Single(result.Diagnostics);
        Assert.Equal(M3uParseDiagnosticKind.Malformed, result.Diagnostics[0].Kind);
    }

    [Fact]
    public void Extinf_without_url_at_eof_is_malformed_and_previous_entry_survives()
    {
        var result = _parser.ParseDetailed("#EXTM3U\n#EXTINF:-1,A\nhttps://x/1\n#EXTINF:-1,B\n");

        Assert.Equal(M3uPlaylistStatus.Partial, result.Status);
        Assert.Single(result.Streams);
        Assert.Equal("https://x/1", result.Streams[0].Url);
        Assert.Equal(1, result.ValidCount);
        Assert.Equal(1, result.MalformedCount);
        Assert.Contains(result.Diagnostics, d => d.Kind == M3uParseDiagnosticKind.Malformed);
    }

    [Fact]
    public void Partial_valid_malformed_valid_reports_partial_with_two_streams()
    {
        var result = _parser.ParseDetailed(
            "#EXTM3U\n#EXTINF:-1,A\nhttps://x/1\nhttps://x/orphan\n#EXTINF:-1,B\nhttps://x/2\n");

        Assert.Equal(M3uPlaylistStatus.Partial, result.Status);
        Assert.Equal(2, result.Streams.Count);
        Assert.Equal(2, result.ValidCount);
        Assert.Equal(1, result.MalformedCount);
        Assert.Equal(0, result.UnusableTargetCount);
    }

    [Fact]
    public void Parsing_continues_after_malformed_entry()
    {
        var result = _parser.ParseDetailed(
            "#EXTM3U\nhttps://x/orphan\n#EXTINF:-1,A\nhttps://x/1\n#EXTINF:-1,B\nhttps://x/2\n");

        Assert.Equal(M3uPlaylistStatus.Partial, result.Status);
        Assert.Equal(2, result.Streams.Count);
        Assert.Equal(1, result.MalformedCount);
    }

    // ===================== Alvo não utilizável =====================

    [Fact]
    public void Rtmp_target_is_unusable_and_not_malformed()
    {
        var result = _parser.ParseDetailed("#EXTM3U\n#EXTINF:-1,A\nrtmp://x/a\n");

        Assert.Equal(1, result.UnusableTargetCount);
        Assert.Equal(0, result.MalformedCount);
        Assert.Empty(result.Streams);
        Assert.Single(result.Diagnostics);
        Assert.Equal(M3uParseDiagnosticKind.UnusableTarget, result.Diagnostics[0].Kind);
        Assert.Equal(M3uPlaylistStatus.Failed, result.Status);
    }

    [Fact]
    public void Valid_plus_unusable_is_partial_with_one_stream()
    {
        var result = _parser.ParseDetailed(
            "#EXTM3U\n#EXTINF:-1,A\nhttps://x/1\n#EXTINF:-1,B\nrtmp://x/b\n");

        Assert.Equal(M3uPlaylistStatus.Partial, result.Status);
        Assert.Single(result.Streams);
        Assert.Equal(1, result.ValidCount);
        Assert.Equal(1, result.UnusableTargetCount);
        Assert.Equal(0, result.MalformedCount);
    }

    // ===================== Metadados benignos =====================

    [Fact]
    public void Benign_metadata_produces_no_diagnostic_and_no_status_change()
    {
        var result = _parser.ParseDetailed(
            "#EXTM3U\n" +
            "#EXT-X-VERSION:3\n" +
            "#EXTGRP:GroupName\n" +
            "#EXTVLCOPT:http-user-agent=Mozilla\n" +
            "#KODIPROP:inputstream.adaptive.license_type=clearkey\n" +
            "#FOO:bar\n" +
            "# a plain comment\n" +
            "#EXTINF:-1,A\nhttps://x/1\n");

        Assert.Equal(M3uPlaylistStatus.Success, result.Status);
        Assert.Single(result.Streams);
        Assert.Equal(0, result.MalformedCount);
        Assert.Equal(0, result.UnusableTargetCount);
        Assert.Empty(result.Diagnostics);
        Assert.True(result.BenignMetadataCount >= 6);
    }

    [Fact]
    public void Benign_line_between_extinf_and_url_does_not_break_entry()
    {
        var result = _parser.ParseDetailed(
            "#EXTM3U\n#EXTINF:-1,A\n#EXTVLCOPT:http-user-agent=Mozilla\nhttps://x/1\n");

        Assert.Equal(M3uPlaylistStatus.Success, result.Status);
        Assert.Single(result.Streams);
        Assert.Equal(0, result.MalformedCount);
        Assert.Empty(result.Diagnostics);
    }

    // ===================== HLS =====================

    [Fact]
    public void Hls_stream_inf_is_metadata_not_extinf_channel()
    {
        var result = _parser.ParseDetailed(
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000,tvg-name=\"V1\",group-title=\"G\"\nhttps://x/v1.m3u8\n");

        Assert.Single(result.Streams);
        var stream = result.Streams[0];
        Assert.Equal("https://x/v1.m3u8", stream.Url);
        Assert.Equal("V1", stream.Title);
        Assert.Equal("G", stream.Group);
        Assert.Equal(string.Empty, stream.OriginalExtInf);
        Assert.Equal(0, result.MalformedCount);
    }

    [Fact]
    public void Hls_version_is_metadata()
    {
        var result = _parser.ParseDetailed(
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXTINF:-1,A\nhttps://x/1\n");

        Assert.Equal(M3uPlaylistStatus.Success, result.Status);
        Assert.Single(result.Streams);
        Assert.Empty(result.Diagnostics);
    }

    // ===================== Conteúdo ignorado =====================

    [Fact]
    public void Random_text_and_blank_lines_are_ignored_not_malformed()
    {
        var result = _parser.ParseDetailed(
            "#EXTM3U\n\nsome random text\n   \n#EXTINF:-1,A\nhttps://x/1\n");

        Assert.Equal(M3uPlaylistStatus.Success, result.Status);
        Assert.Single(result.Streams);
        Assert.Equal(0, result.MalformedCount);
        Assert.True(result.IgnoredCount >= 2);
    }

    // ===================== Segurança =====================

    [Fact]
    public void Credentials_in_malformed_url_are_not_leaked_in_diagnostics()
    {
        var result = _parser.ParseDetailed(
            "#EXTM3U\nhttps://alice:s3cret@host/live/bob/pass/1.ts\n");

        Assert.Equal(1, result.MalformedCount);
        var line = result.Diagnostics[0].SanitizedLine;
        Assert.DoesNotContain("s3cret", line);
        Assert.DoesNotContain("bob", line);
        Assert.Contains("***", line);
    }

    [Fact]
    public void Credentials_in_unusable_url_are_not_leaked_in_diagnostics()
    {
        var result = _parser.ParseDetailed(
            "#EXTM3U\n#EXTINF:-1,A\nrtmp://alice:s3cret@host/app\n");

        Assert.Equal(1, result.UnusableTargetCount);
        var line = result.Diagnostics[0].SanitizedLine;
        Assert.DoesNotContain("s3cret", line);
        Assert.Contains("***", line);
    }

    // ===================== Limites / cancelamento (mecanismo) =====================

    [Fact]
    public void Capped_entry_limit_is_parser_failure()
    {
        var parser = new M3uParserService(new M3uParserOptions { MaxEntries = 1 });
        var result = parser.ParseDetailed(
            "#EXTM3U\n#EXTINF:-1,A\nhttps://x/1\n#EXTINF:-1,B\nhttps://x/2\n");

        Assert.Equal(M3uPlaylistStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Kind == M3uParseDiagnosticKind.ParserFailure);
    }

    [Fact]
    public void Cancellation_is_parser_failure_without_throwing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = _parser.ParseDetailed("#EXTM3U\n#EXTINF:-1,A\nhttps://x/1\n", cts.Token);

        Assert.Equal(M3uPlaylistStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Kind == M3uParseDiagnosticKind.ParserFailure);
    }

    // ===================== Retro-compatibilidade =====================

    [Fact]
    public void Legacy_parse_returns_detailed_streams()
    {
        const string content = "#EXTM3U\n#EXTINF:-1,A\nhttps://x/1\n";

        var legacy = _parser.Parse(content);
        var detailed = _parser.ParseDetailed(content);

        Assert.Equal(detailed.Streams.Select(s => s.Url), legacy.Select(s => s.Url));
    }

    // ===================== Convergência de consumidores =====================

    [Fact]
    public void PlaylistReader_converges_with_parser_verdicts()
    {
        const string valid = "#EXTM3U\n#EXTINF:-1,A\nhttps://x/1\n#EXTINF:-1,B\nhttp://x/2\n";
        const string missingHeader = "#EXTINF:-1,A\nhttps://x/1\n";
        const string unusable = "#EXTM3U\n#EXTINF:-1,A\nrtmp://x/a\n";

        Assert.Equal(
            _parser.ParseDetailed(valid).Streams.Count,
            PlaylistReader.Parse(valid, defaultProvider: null).Count);
        Assert.Empty(PlaylistReader.Parse(missingHeader, defaultProvider: null));
        Assert.Empty(PlaylistReader.Parse(unusable, defaultProvider: null));
    }

    [Fact]
    public async Task PlaylistManager_converges_with_parser_verdicts()
    {
        const string valid = "#EXTM3U\n#EXTINF:-1,A\nhttps://x/1\n#EXTINF:-1,B\nhttp://x/2\n";
        const string missingHeader = "#EXTINF:-1,A\nhttps://x/1\n";

        var manager = new PlaylistManagerService();
        var validPath = Path.Combine(Path.GetTempPath(), $"w3-valid-{Guid.NewGuid():N}.m3u");
        var headerlessPath = Path.Combine(Path.GetTempPath(), $"w3-headerless-{Guid.NewGuid():N}.m3u");
        try
        {
            await File.WriteAllTextAsync(validPath, valid);
            await File.WriteAllTextAsync(headerlessPath, missingHeader);

            var loaded = await manager.LoadFromM3uPlaylist(validPath);
            var headerless = await manager.LoadFromM3uPlaylist(headerlessPath);

            Assert.Equal(_parser.ParseDetailed(valid).Streams.Count, loaded.Count);
            Assert.Empty(headerless);
        }
        finally
        {
            if (File.Exists(validPath)) File.Delete(validPath);
            if (File.Exists(headerlessPath)) File.Delete(headerlessPath);
        }
    }
}
