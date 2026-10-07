using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.Sync;
using Xunit;

namespace m3uCrawler.Tests;

public class DispatcharrReportSerializationSanitizationTests
{
    // Credenciais fictícias do fixture — nunca reais.
    private const string XtreamUrl = "http://example.test:8080/live/testuser/testpassword/12345";
    private const string TestUser = "testuser";
    private const string TestPassword = "testpassword";
    private const string SanitizedXtreamUrl = "http://example.test:8080/live/***/***/12345";

    private static SyncReport ReportWithStream(string url, FailedReportEntry[]? failed = null) => new()
    {
        StartedAtUtc = "2025-01-01T00:00:00Z",
        FinishedAtUtc = "2025-01-01T00:01:00Z",
        DryRun = false,
        DispatcharrVersion = "0.29.0",
        SourcePlaylistPath = "/opt/playlists/playlist.m3u",
        Counts = new SyncReportCounts { Matched = 1, RemovedStreams = 1 },
        Channels = new ChannelDecision[]
        {
            new ChannelDecision
            {
                Identity = "sic",
                CanonicalName = "SIC",
                CanonicalChannelKey = "pt-pt-sic",
                CanonicalChannelId = 3,
                Outcome = SyncOutcome.ExistingUnchanged,
                ExistingChannelId = 42,
                ChannelGroupName = "Portugal",
                MatchReason = "exact-name",
                MatchScore = 5000,
                OutputGroup = OutputGroupKind.PortugalLive,
                StreamsEmptied = false,
                AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
                Streams = new StreamMatchDecision[]
                {
                    new StreamMatchDecision
                    {
                        Provider = "telegram-portugal",
                        StreamUrl = url,
                        StreamName = "SIC HD",
                        Outcome = SyncOutcome.Removed,
                        ExistingStreamId = 7,
                        ProposedOrder = 1,
                        OrderReason = "stale",
                        IsWorking = false,
                        GroupName = "Portugal",
                    },
                },
            },
        },
        AmbiguousDecisions = Array.Empty<AmbiguousReportEntry>(),
        AmbiguousGroups = Array.Empty<AmbiguousGroupEntry>(),
        FailedChannels = failed ?? Array.Empty<FailedReportEntry>(),
    };

    [Fact]
    public void SerializeReport_does_not_leak_xtream_path_credentials()
    {
        var json = MatchPlanSerializer.SerializeReport(ReportWithStream(XtreamUrl));

        Assert.DoesNotContain(TestUser, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestPassword, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(XtreamUrl, json, StringComparison.Ordinal);
        Assert.Contains(SanitizedXtreamUrl, json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeReport_preserves_plain_url_without_credentials()
    {
        const string plain = "https://example.test/channel/123";
        var json = MatchPlanSerializer.SerializeReport(ReportWithStream(plain));

        Assert.Contains(plain, json, StringComparison.Ordinal);
        Assert.Contains("/channel/123", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeReport_sanitizes_query_string_credentials()
    {
        const string url = "http://example.test/get.php?username=alice&password=secret&type=m3u_plus";
        var json = MatchPlanSerializer.SerializeReport(ReportWithStream(url));

        Assert.DoesNotContain("alice", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("username=***", json, StringComparison.Ordinal);
        Assert.Contains("password=***", json, StringComparison.Ordinal);
        Assert.Contains("type=m3u_plus", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeReport_sanitizes_failed_channel_reason_urls()
    {
        var failed = new[]
        {
            new FailedReportEntry
            {
                Identity = "sic",
                Reason = "download failed for " + XtreamUrl,
                ExistingChannelId = 42,
            },
        };

        var json = MatchPlanSerializer.SerializeReport(ReportWithStream("https://example.test/channel/1", failed));

        Assert.DoesNotContain(TestUser, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestPassword, json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("download failed for", json, StringComparison.Ordinal);
        Assert.Contains(SanitizedXtreamUrl, json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeReport_preserves_report_semantics()
    {
        var json = MatchPlanSerializer.SerializeReport(ReportWithStream(XtreamUrl));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("2025-01-01T00:00:00Z", root.GetProperty("startedAtUtc").GetString());
        Assert.Equal("2025-01-01T00:01:00Z", root.GetProperty("finishedAtUtc").GetString());
        Assert.False(root.GetProperty("dryRun").GetBoolean());
        Assert.Equal("0.29.0", root.GetProperty("dispatcharrVersion").GetString());
        Assert.Equal(1, root.GetProperty("counts").GetProperty("matched").GetInt32());
        Assert.Equal(1, root.GetProperty("counts").GetProperty("removedStreams").GetInt32());

        var channel = root.GetProperty("channels")[0];
        Assert.Equal(42, channel.GetProperty("existingChannelId").GetInt64());
        Assert.Equal(3, channel.GetProperty("canonicalChannelId").GetInt64());
        Assert.Equal("pt-pt-sic", channel.GetProperty("canonicalChannelKey").GetString());
        Assert.Equal("exact-name", channel.GetProperty("matchReason").GetString());
        Assert.Equal((int)OutputGroupKind.PortugalLive, channel.GetProperty("outputGroup").GetInt32());

        var stream = channel.GetProperty("streams")[0];
        Assert.Equal(7, stream.GetProperty("existingStreamId").GetInt64());
        Assert.Equal("stale", stream.GetProperty("orderReason").GetString());
        Assert.Equal("telegram-portugal", stream.GetProperty("provider").GetString());
        Assert.Equal("SIC HD", stream.GetProperty("streamName").GetString());
    }

    [Fact]
    public void Plan_and_report_both_sanitize_the_same_stream_url()
    {
        var plan = new MatchPlan
        {
            GeneratedAtUtc = "2025-01-01T00:00:00Z",
            SourcePlaylistPath = "/opt/playlists/playlist.m3u",
            DispatcharrBaseUrl = "http://dispatcharr:9191",
            DryRun = false,
            MatchThreshold = 80,
            Channels = new ChannelDecision[]
            {
                new ChannelDecision
                {
                    Identity = "sic",
                    CanonicalName = "SIC",
                    Outcome = SyncOutcome.NewChannel,
                    Streams = new StreamMatchDecision[]
                    {
                        new StreamMatchDecision
                        {
                            Provider = "telegram-portugal",
                            StreamUrl = XtreamUrl,
                            StreamName = "SIC HD",
                            Outcome = SyncOutcome.NewStream,
                        },
                    },
                },
            },
        };

        var planJson = MatchPlanSerializer.Serialize(plan);
        var reportJson = MatchPlanSerializer.SerializeReport(ReportWithStream(XtreamUrl));

        Assert.DoesNotContain(TestUser, planJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestPassword, planJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestUser, reportJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestPassword, reportJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SanitizedXtreamUrl, planJson, StringComparison.Ordinal);
        Assert.Contains(SanitizedXtreamUrl, reportJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteReportAsync_persists_a_sanitized_artifact()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dispatcharr_report_test_{Guid.NewGuid():N}.json");
        try
        {
            await MatchPlanSerializer.WriteReportAsync(ReportWithStream(XtreamUrl), path);
            var json = await File.ReadAllTextAsync(path);

            Assert.DoesNotContain(TestUser, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(TestPassword, json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(SanitizedXtreamUrl, json, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
