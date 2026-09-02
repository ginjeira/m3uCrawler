using System.Text.Json;
using m3uCrawler.Models;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.Sync;
using Xunit;

namespace m3uCrawler.Tests;

public class MatchPlanSerializerOutputGroupTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    [Fact]
    public void SanitizeForSerialization_preserves_OutputGroup()
    {
        var plan = new MatchPlan
        {
            GeneratedAtUtc = "2025-01-01T00:00:00Z",
            SourcePlaylistPath = "/tmp/playlist.m3u",
            DispatcharrBaseUrl = "http://localhost:7878",
            DryRun = true,
            MatchThreshold = 4500,
            Channels = new ChannelDecision[]
            {
                new ChannelDecision
                {
                    Identity = "sic",
                    CanonicalName = "SIC",
                    OutputGroup = OutputGroupKind.PortugalLive,
                    Outcome = SyncOutcome.NewChannel,
                    ChannelGroupName = "Portugal",
                    MatchReason = "exact",
                    MatchScore = 5000,
                    Streams = Array.Empty<StreamMatchDecision>(),
                    AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
                    StreamsEmptied = false,
                },
                new ChannelDecision
                {
                    Identity = "rtp1",
                    CanonicalName = "RTP 1",
                    Outcome = SyncOutcome.NewChannel,
                    OutputGroup = OutputGroupKind.Foreign,
                    ChannelGroupName = "EU | BE",
                    MatchReason = "new",
                    MatchScore = 0,
                    Streams = Array.Empty<StreamMatchDecision>(),
                    AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
                    StreamsEmptied = false,
                },
            },
            AmbiguousGroups = Array.Empty<AmbiguousGroupEntry>(),
        };

        var json = MatchPlanSerializer.Serialize(plan);

        Assert.Contains("\"outputGroup\": 0", json, StringComparison.Ordinal);
        Assert.Contains("\"outputGroup\": 8", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeForSerialization_preserves_null_OutputGroup()
    {
        var plan = new MatchPlan
        {
            GeneratedAtUtc = "2025-01-01T00:00:00Z",
            SourcePlaylistPath = "/tmp/playlist.m3u",
            DispatcharrBaseUrl = "http://localhost:7878",
            DryRun = true,
            MatchThreshold = 4500,
            Channels = new ChannelDecision[]
            {
                new ChannelDecision
                {
                    Identity = "sic",
                    CanonicalName = "SIC",
                    Outcome = SyncOutcome.NewChannel,
                    OutputGroup = null,
                    ChannelGroupName = "Portugal",
                    MatchReason = "exact",
                    MatchScore = 5000,
                    Streams = Array.Empty<StreamMatchDecision>(),
                    AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
                    StreamsEmptied = false,
                },
            },
            AmbiguousGroups = Array.Empty<AmbiguousGroupEntry>(),
        };

        var json = MatchPlanSerializer.Serialize(plan);

        var deserialized = JsonSerializer.Deserialize<MatchPlan>(json, JsonOpts);
        Assert.NotNull(deserialized);
        Assert.Single(deserialized.Channels);
        Assert.Null(deserialized.Channels[0].OutputGroup);
    }

    [Fact]
    public void SerializeReport_preserves_OutputGroup()
    {
        var report = new SyncReport
        {
            StartedAtUtc = "2025-01-01T00:00:00Z",
            FinishedAtUtc = "2025-01-01T00:01:00Z",
            DryRun = true,
            SourcePlaylistPath = "/tmp/playlist.m3u",
            Counts = new SyncReportCounts { Matched = 1 },
            Channels = new ChannelDecision[]
            {
                new ChannelDecision
                {
                    Identity = "sic",
                    CanonicalName = "SIC",
                    Outcome = SyncOutcome.NewChannel,
                    OutputGroup = OutputGroupKind.PortugalEntretenimento,
                    ChannelGroupName = "Portugal",
                    MatchReason = "exact",
                    MatchScore = 5000,
                    Streams = Array.Empty<StreamMatchDecision>(),
                    AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
                    StreamsEmptied = false,
                },
            },
            AmbiguousDecisions = Array.Empty<AmbiguousReportEntry>(),
            AmbiguousGroups = Array.Empty<AmbiguousGroupEntry>(),
            FailedChannels = Array.Empty<FailedReportEntry>(),
        };

        var json = MatchPlanSerializer.SerializeReport(report);

        Assert.Contains("\"outputGroup\": 3", json, StringComparison.Ordinal);
    }
}
