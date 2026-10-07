using System;
using System.Text.Json;
using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

public class CandidateMessageProvenanceTests
{
    private static readonly DateTime FixedUtc =
        new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ApplyTelegramMessageProvenance_sets_chat_and_message_provenance()
    {
        var candidate = new CandidatePlaylist();

        TelegramScraperService.ApplyTelegramMessageProvenance(
            candidate, "canal pt", 12345, FixedUtc);

        Assert.Equal("canal pt", candidate.Source);
        Assert.Equal(12345L, candidate.SourceMessageId);
        Assert.Equal(FixedUtc, candidate.SourceMessageDateUtc);
    }

    [Fact]
    public void ApplyTelegramMessageProvenance_replaces_source_and_is_idempotent()
    {
        var candidate = new CandidatePlaylist { Source = "origem pré-existente" };

        TelegramScraperService.ApplyTelegramMessageProvenance(
            candidate, "canal pt", 12345, FixedUtc);
        TelegramScraperService.ApplyTelegramMessageProvenance(
            candidate, "canal pt", 12345, FixedUtc);

        Assert.Equal("canal pt", candidate.Source);
        Assert.Equal(12345L, candidate.SourceMessageId);
        Assert.Equal(FixedUtc, candidate.SourceMessageDateUtc);
    }

    [Fact]
    public void CandidatePlaylist_provenance_defaults_to_null()
    {
        var candidate = new CandidatePlaylist();

        Assert.Null(candidate.SourceMessageId);
        Assert.Null(candidate.SourceMessageDateUtc);
    }

    [Fact]
    public void PromoteXtreamAccount_inherits_message_provenance()
    {
        var c = TelegramScraperService.PromoteXtreamAccount(
            playlistUrl: "http://host/x.m3u",
            publicationUrl: "https://ex.com/p",
            sourceMessageId: 12345,
            sourceMessageDateUtc: FixedUtc);

        Assert.NotNull(c);
        Assert.Equal(12345L, c!.SourceMessageId);
        Assert.Equal(FixedUtc, c.SourceMessageDateUtc);
        Assert.Equal(CandidateSourceKind.Url, c.Kind);
        Assert.Equal("xtream publication: https://ex.com/p", c.Source);
        Assert.Equal("xtream publication", c.DetectedFrom);
        Assert.True(c.RequiresContentVerification);
    }

    [Fact]
    public void PromoteXtreamAccount_defaults_keep_provenance_null()
    {
        var c = TelegramScraperService.PromoteXtreamAccount(
            "http://host/x.m3u", "https://ex.com/p");

        Assert.NotNull(c);
        Assert.Null(c!.SourceMessageId);
        Assert.Null(c.SourceMessageDateUtc);
    }

    [Fact]
    public void DiscoveredPlaylist_serializes_provenance_camelCase()
    {
        var json = JsonSerializer.Serialize(
            new DiscoveredPlaylist
            {
                CandidateId = "abc123",
                MessageId = 12345,
                MessageDateUtc = FixedUtc,
                Source = "chat",
                Name = "n"
            },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.Contains("\"candidateId\"", json);
        Assert.Contains("\"messageId\"", json);
        Assert.Contains("\"messageDateUtc\"", json);
        Assert.Contains("\"candidateId\":\"abc123\"", json);
        Assert.Contains("\"messageId\":12345", json);
        Assert.Contains("\"messageDateUtc\":\"2026-10-02T12:00:00Z\"", json);
    }

    [Fact]
    public void DiscoveredPlaylist_provenance_defaults_null()
    {
        var item = new DiscoveredPlaylist();

        Assert.Null(item.CandidateId);
        Assert.Null(item.MessageId);
        Assert.Null(item.MessageDateUtc);

        var json = JsonSerializer.Serialize(
            item,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.Contains("\"candidateId\":null", json);
        Assert.Contains("\"messageId\":null", json);
        Assert.Contains("\"messageDateUtc\":null", json);
    }
}
