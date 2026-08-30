using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Regressão para o crash em TelegramScraperService.cs:352 (InvalidOperationException:
/// "Collection was modified; enumeration operation may not execute") que ocorria quando
/// uma mensagem do Telegram continha um anexo cujo download devolvia conteúdo #EXTM3U.
///
/// A causa raiz era enumerar `found.Where(...)` ao mesmo tempo que o corpo do loop fazia
/// `found.Add(...)`. A correcção materializa o Where numa lista separada antes do foreach.
///
/// Estes testes chamam ProcessAttachmentCandidatesAsync (a mesma lógica que o código de
/// produção agora invoca) com um downloader controlado, sem depender do WTelegram.Client.
/// </summary>
public class TelegramAttachmentProcessingTests
{
    [Fact]
    public async Task Adding_EXTM3U_candidate_during_attachment_iteration_does_not_throw()
    {
        var found = new List<CandidatePlaylist>
        {
            new()
            {
                Kind = CandidateSourceKind.Attachment,
                FileName = "channels.m3u",
                SourceText = "lista",
                Content = null,
                DetectedFrom = "attachment filename"
            }
        };

        var downloaderCalls = 0;
        var ex = await Record.ExceptionAsync(() =>
            TelegramScraperService.ProcessAttachmentCandidatesAsync(
                found,
                hasAttachment: true,
                filename: "channels.m3u",
                text: "lista",
                downloader: () =>
                {
                    downloaderCalls++;
                    return Task.FromResult<string?>("#EXTM3U\n#EXTINF:0,Channel A\nhttp://example.test/a.ts");
                }));

        Assert.Null(ex);
        Assert.Equal(1, downloaderCalls);
        Assert.Equal(2, found.Count);
        Assert.Contains(found, c => c.DetectedFrom == "attachment filename" && c.Content != null);
        Assert.Contains(found, c => c.DetectedFrom == "#EXTM3U content" && c.Content != null);
    }

    [Fact]
    public async Task Does_not_add_EXTM3U_candidate_when_attachment_content_is_empty()
    {
        var found = new List<CandidatePlaylist>
        {
            new()
            {
                Kind = CandidateSourceKind.Attachment,
                FileName = "channels.m3u",
                SourceText = "lista",
                Content = null,
                DetectedFrom = "attachment filename"
            }
        };

        await TelegramScraperService.ProcessAttachmentCandidatesAsync(
            found,
            hasAttachment: true,
            filename: "channels.m3u",
            text: "lista",
            downloader: () => Task.FromResult<string?>(""));

        Assert.Single(found);
        Assert.NotNull(found[0].Content);
    }

    [Fact]
    public async Task Does_not_add_EXTM3U_candidate_when_content_is_not_EXTM3U()
    {
        var found = new List<CandidatePlaylist>
        {
            new()
            {
                Kind = CandidateSourceKind.Attachment,
                FileName = "channels.m3u",
                SourceText = "lista",
                Content = null,
                DetectedFrom = "attachment filename"
            }
        };

        await TelegramScraperService.ProcessAttachmentCandidatesAsync(
            found,
            hasAttachment: true,
            filename: "channels.m3u",
            text: "lista",
            downloader: () => Task.FromResult<string?>("this is not an m3u playlist"));

        Assert.Single(found);
        Assert.Equal("this is not an m3u playlist", found[0].Content);
    }

    [Fact]
    public async Task Does_not_download_when_there_are_no_attachment_candidates()
    {
        var found = new List<CandidatePlaylist>
        {
            new()
            {
                Kind = CandidateSourceKind.Url,
                Url = "https://example.test/list.m3u",
                SourceText = "https://example.test/list.m3u",
                DetectedFrom = "m3u url"
            }
        };

        var downloaderCalls = 0;

        await TelegramScraperService.ProcessAttachmentCandidatesAsync(
            found,
            hasAttachment: true,
            filename: "ignored.m3u",
            text: "",
            downloader: () =>
            {
                downloaderCalls++;
                return Task.FromResult<string?>("#EXTM3U");
            });

        Assert.Equal(0, downloaderCalls);
        Assert.Single(found);
    }

    [Fact]
    public async Task Does_not_add_duplicate_EXTM3U_candidate_when_already_present()
    {
        var found = new List<CandidatePlaylist>
        {
            new()
            {
                Kind = CandidateSourceKind.Attachment,
                FileName = "channels.m3u",
                SourceText = "lista",
                Content = "#EXTM3U\n#EXTINF:0,A\nhttp://example.test/a",
                DetectedFrom = "#EXTM3U content"
            }
        };

        await TelegramScraperService.ProcessAttachmentCandidatesAsync(
            found,
            hasAttachment: true,
            filename: "channels.m3u",
            text: "lista",
            downloader: () => Task.FromResult<string?>("#EXTM3U\n#EXTINF:0,A\nhttp://example.test/a"));

        Assert.Single(found);
    }

    [Fact]
    public async Task Swallows_download_exception_and_continues()
    {
        var found = new List<CandidatePlaylist>
        {
            new()
            {
                Kind = CandidateSourceKind.Attachment,
                FileName = "channels.m3u",
                SourceText = "lista",
                Content = null,
                DetectedFrom = "attachment filename"
            }
        };

        var ex = await Record.ExceptionAsync(() =>
            TelegramScraperService.ProcessAttachmentCandidatesAsync(
                found,
                hasAttachment: true,
                filename: "channels.m3u",
                text: "lista",
                downloader: () => throw new InvalidOperationException("simulated download failure")));

        Assert.Null(ex);
        Assert.Single(found);
        Assert.Null(found[0].Content);
    }
}