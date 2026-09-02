using System.Text.Json.Serialization;
using m3uCrawler.Services.Matching;

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
        [JsonPropertyName("ambiguousGroups")] public IReadOnlyList<AmbiguousGroupEntry> AmbiguousGroups { get; init; } = Array.Empty<AmbiguousGroupEntry>();
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
        /// <summary>
        /// True when this decision emptied the channel on Dispatcharr: at least one
        /// <see cref="SyncOutcome.Removed"/> stream and no surviving or new streams.
        /// Triggers an explicit PATCH with <c>streams=[]</c> in the apply phase.
        /// </summary>
        [JsonPropertyName("streamsEmptied")] public bool StreamsEmptied { get; init; }

        /// <summary>
        /// Editorial output-group kind (Live/VOD/Filmes24-7/PPV/PT-category
        /// labels/Foreign). Computed by <c>ResolutionPolicy</c> during
        /// <c>ChannelMatcher.BuildPlan</c>. Optional: null means the
        /// resolver did not produce a value (e.g. bucket without a
        /// representative stream). Consumers that do not read this
        /// field are unaffected.
        /// </summary>
        [JsonPropertyName("outputGroup")] public OutputGroupKind? OutputGroup { get; init; }
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

    public sealed class SyncReportCounts
    {
        [JsonPropertyName("matched")] public int Matched { get; set; }
        [JsonPropertyName("newChannels")] public int NewChannels { get; set; }
        [JsonPropertyName("newStreams")] public int NewStreams { get; set; }
        [JsonPropertyName("removedStreams")] public int RemovedStreams { get; set; }
        [JsonPropertyName("skipped")] public int Skipped { get; set; }
        [JsonPropertyName("ambiguous")] public int Ambiguous { get; set; }
        [JsonPropertyName("unchanged")] public int Unchanged { get; set; }
        [JsonPropertyName("failed")] public int Failed { get; set; }
        [JsonPropertyName("ambiguousGroups")] public int AmbiguousGroups { get; set; }
        [JsonPropertyName("totalChannels")] public int TotalChannels => Matched + NewChannels + Unchanged + Ambiguous + Failed + Skipped;

        /// <summary>
        /// Aggregated count of channels per <see cref="OutputGroupKind"/>,
        /// keyed by the enum name (e.g. "PortugalLive", "Foreign"). Null
        /// <c>OutputGroup</c> entries on <see cref="ChannelDecision"/> are
        /// skipped. Bucket-less decisions (no representative stream)
        /// therefore never contribute. The map is serialised as
        /// <c>outputGroups</c> in <c>dispatcharr_report_*.json</c>.
        /// </summary>
        [JsonPropertyName("outputGroups")] public IReadOnlyDictionary<string, int> OutputGroups { get; set; } = new Dictionary<string, int>();
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
        [JsonPropertyName("ambiguousGroups")] public IReadOnlyList<AmbiguousGroupEntry> AmbiguousGroups { get; init; } = Array.Empty<AmbiguousGroupEntry>();
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

    public sealed class AmbiguousGroupEntry
    {
        [JsonPropertyName("normalizedName")] public string NormalizedName { get; init; } = string.Empty;
        [JsonPropertyName("groupIds")] public IReadOnlyList<long> GroupIds { get; init; } = Array.Empty<long>();
        [JsonPropertyName("groupNames")] public IReadOnlyList<string> GroupNames { get; init; } = Array.Empty<string>();
    }
}
