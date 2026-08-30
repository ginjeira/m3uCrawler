using System.Text.Json.Serialization;

namespace m3uCrawler.Models
{
    public sealed class MatchPlan
    {
        [JsonPropertyName("generatedAtUtc")] public string GeneratedAtUtc { get; init; } = string.Empty;
        [JsonPropertyName("sourcePlaylistPath")] public string SourcePlaylistPath { get; init; } = string.Empty;
        [JsonPropertyName("dispatcharrBaseUrl")] public string DispatcharrBaseUrl { get; init; } = string.Empty;
        [JsonPropertyName("dryRun")] public bool DryRun { get; init; }
        [JsonPropertyName("matchThreshold")] public int MatchThreshold { get; init; }
        [JsonPropertyName("counts")] public SyncReportCounts Counts { get; init; } = new();
        [JsonPropertyName("channels")] public IReadOnlyList<ChannelDecision> Channels { get; init; } = Array.Empty<ChannelDecision>();
    }

    public sealed class ChannelDecision
    {
        [JsonPropertyName("identity")] public string Identity { get; init; } = string.Empty;
        [JsonPropertyName("canonicalName")] public string CanonicalName { get; init; } = string.Empty;
        [JsonPropertyName("outcome")] public SyncOutcome Outcome { get; init; }
        [JsonPropertyName("existingChannelId")] public long? ExistingChannelId { get; init; }
        [JsonPropertyName("proposedChannelNumber")] public double? ProposedChannelNumber { get; init; }
        [JsonPropertyName("channelGroupName")] public string? ChannelGroupName { get; init; }
        [JsonPropertyName("matchReason")] public string MatchReason { get; init; } = string.Empty;
        [JsonPropertyName("matchScore")] public int MatchScore { get; init; }
        [JsonPropertyName("streams")] public IReadOnlyList<StreamMatchDecision> Streams { get; init; } = Array.Empty<StreamMatchDecision>();
        [JsonPropertyName("ambiguousCandidates")] public IReadOnlyList<AmbiguousCandidate> AmbiguousCandidates { get; init; } = Array.Empty<AmbiguousCandidate>();
    }

    public sealed class StreamMatchDecision
    {
        [JsonPropertyName("provider")] public string Provider { get; init; } = string.Empty;
        [JsonPropertyName("streamUrl")] public string StreamUrl { get; init; } = string.Empty;
        [JsonPropertyName("streamName")] public string StreamName { get; init; } = string.Empty;
        [JsonPropertyName("outcome")] public SyncOutcome Outcome { get; init; }
        [JsonPropertyName("existingStreamId")] public long? ExistingStreamId { get; init; }
        [JsonPropertyName("proposedOrder")] public int ProposedOrder { get; init; }
        [JsonPropertyName("orderReason")] public string OrderReason { get; init; } = string.Empty;
        [JsonPropertyName("isWorking")] public bool IsWorking { get; init; }
        [JsonPropertyName("groupName")] public string? GroupName { get; init; }
    }

    public sealed class AmbiguousCandidate
    {
        [JsonPropertyName("existingChannelId")] public long ExistingChannelId { get; init; }
        [JsonPropertyName("existingChannelName")] public string ExistingChannelName { get; init; } = string.Empty;
        [JsonPropertyName("score")] public int Score { get; init; }
        [JsonPropertyName("reason")] public string Reason { get; init; } = string.Empty;
    }

    public sealed record SyncReportCounts
    {
        [JsonPropertyName("matched")] public int Matched { get; init; }
        [JsonPropertyName("newChannels")] public int NewChannels { get; init; }
        [JsonPropertyName("newStreams")] public int NewStreams { get; init; }
        [JsonPropertyName("removedStreams")] public int RemovedStreams { get; init; }
        [JsonPropertyName("skipped")] public int Skipped { get; init; }
        [JsonPropertyName("ambiguous")] public int Ambiguous { get; init; }
        [JsonPropertyName("unchanged")] public int Unchanged { get; init; }
        [JsonPropertyName("failed")] public int Failed { get; init; }
        [JsonPropertyName("totalChannels")] public int TotalChannels => Matched + NewChannels + Unchanged + Ambiguous + Failed + Skipped;
    }

    public sealed class SyncReport
    {
        [JsonPropertyName("startedAtUtc")] public string StartedAtUtc { get; init; } = string.Empty;
        [JsonPropertyName("finishedAtUtc")] public string FinishedAtUtc { get; init; } = string.Empty;
        [JsonPropertyName("dryRun")] public bool DryRun { get; init; }
        [JsonPropertyName("dispatcharrVersion")] public string? DispatcharrVersion { get; init; }
        [JsonPropertyName("sourcePlaylistPath")] public string SourcePlaylistPath { get; init; } = string.Empty;
        [JsonPropertyName("counts")] public SyncReportCounts Counts { get; init; } = new();
        [JsonPropertyName("channels")] public IReadOnlyList<ChannelDecision> Channels { get; init; } = Array.Empty<ChannelDecision>();
        [JsonPropertyName("ambiguousDecisions")] public IReadOnlyList<AmbiguousReportEntry> AmbiguousDecisions { get; init; } = Array.Empty<AmbiguousReportEntry>();
        [JsonPropertyName("failedChannels")] public IReadOnlyList<FailedReportEntry> FailedChannels { get; init; } = Array.Empty<FailedReportEntry>();
    }

    public sealed class AmbiguousReportEntry
    {
        [JsonPropertyName("identity")] public string Identity { get; init; } = string.Empty;
        [JsonPropertyName("candidates")] public IReadOnlyList<AmbiguousCandidate> Candidates { get; init; } = Array.Empty<AmbiguousCandidate>();
    }

    public sealed class FailedReportEntry
    {
        [JsonPropertyName("identity")] public string Identity { get; init; } = string.Empty;
        [JsonPropertyName("reason")] public string Reason { get; init; } = string.Empty;
        [JsonPropertyName("existingChannelId")] public long? ExistingChannelId { get; init; }
    }
}
