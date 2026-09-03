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

        /// <summary>
        /// Diagnostic list of streams excluded from the matching pipeline
        /// because their classification was not <see cref="ChannelKind.Channel"/>.
        /// Each entry carries the deterministic classification reason;
        /// credentials and URLs are NEVER included (sanitisation is the
        /// responsibility of the serializer / reporter).
        /// </summary>
        [JsonPropertyName("classifiedExclusions")] public IReadOnlyList<ClassifiedExclusion> ClassifiedExclusions { get; init; } = Array.Empty<ClassifiedExclusion>();

        /// <summary>
        /// Diagnostic list of streams classified as
        /// <see cref="ChannelKind.Unknown"/> that could not be safely
        /// attached to any existing channel in Dispatcharr (no match
        /// or ambiguous match). Held back for manual review — they
        /// NEVER produce <see cref="SyncOutcome.NewChannel"/>.
        /// </summary>
        [JsonPropertyName("unknownReviewRequired")] public IReadOnlyList<ClassifiedExclusion> UnknownReviewRequired { get; init; } = Array.Empty<ClassifiedExclusion>();
    }

    /// <summary>
    /// Diagnostic record for an entry that the classifier rejected
    /// from channel matching (or routed into the "Unknown can match
    /// existing only" bucket). Carries only the metadata required to
    /// explain the decision: title, source group, the kind assigned,
    /// the deterministic classification reason, and the final
    /// matching disposition (excluded / unknown-matched-to-existing /
    /// unknown-review-required / new-channels-from-curated-identity).
    ///
    /// <para>
    /// URL and provider are intentionally absent so this record can
    /// be serialised in logs / dashboard / report_builder without
    /// requiring further sanitisation.
    /// </para>
    /// </summary>
    public sealed class ClassifiedExclusion
    {
        [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
        [JsonPropertyName("group")] public string Group { get; init; } = string.Empty;
        [JsonPropertyName("kind")] public ChannelKind Kind { get; init; }
        [JsonPropertyName("reason")] public string Reason { get; init; } = string.Empty;

        /// <summary>
        /// How the matcher disposed of this entry:
        /// <list type="bullet">
        ///   <item><c>excluded</c> — never matched, never created
        ///         (Bundle, Vod, LiveCam, Placeholder, Category,
        ///         Group, Foreign).</item>
        ///   <item><c>unknown-matched-to-existing</c> — Unknown
        ///         entry that found a unique existing channel in
        ///         Dispatcharr and was attached to it as a new
        ///         stream.</item>
        ///   <item><c>unknown-review-required</c> — Unknown entry
        ///         that could not be matched to a single existing
        ///         channel (no match, ambiguous match, or weak
        ///         match). Held back for manual review.</item>
        ///   <item><c>new-channels-from-curated-identity</c> — a
        ///         curated channel identity that the matcher promoted
    ///         to a NewChannel decision. This disposition is set
    ///         on the matched <see cref="ChannelDecision"/>, not
    ///         on the exclusion record, but is enumerated by the
    ///         dashboard for symmetry.</item>
        /// </list>
        /// </summary>
        [JsonPropertyName("matchingDisposition")] public string MatchingDisposition { get; init; } = string.Empty;
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

        /// <summary>
        /// Aggregated count of <see cref="DiscoveredStream"/> entries by
        /// <see cref="ChannelKind"/> as decided by <c>ContentClassifier</c>
        /// before the matching pipeline runs. Keys are the enum names
        /// (e.g. "Channel", "Bundle", "Vod", "LiveCam", "Foreign",
        /// "Unknown", "Placeholder"). Only non-zero counts are
        /// serialised.
        /// </summary>
        [JsonPropertyName("classification")] public IReadOnlyDictionary<string, int> Classification { get; set; } = new Dictionary<string, int>();

        /// <summary>
        /// Aggregated count of entries by the <b>matching
        /// disposition</b> assigned by the matcher after classification
        /// (orthogonal to the kind-level counter above). Keys:
        /// </summary>
        /// <list type="bullet">
        ///   <item><c>excluded</c> — Bundle / Vod / LiveCam /
        ///         Placeholder / Category / Group / Foreign entries
        ///         that never matched, never created.</item>
        ///   <item><c>unknownMatchedToExisting</c> — Unknown entries
        ///         that attached to an existing channel in
        ///         Dispatcharr.</item>
        ///   <item><c>unknownReviewRequired</c> — Unknown entries
        ///         that did not match any existing channel and are
        ///         waiting for manual review (NOT promoted to
        ///         NewChannel).</item>
        ///   <item><c>newChannelsFromCuratedIdentity</c> — curated
        ///         channel identities promoted to NewChannel.</item>
        /// </list>
        [JsonPropertyName("matchingDisposition")]
        public IReadOnlyDictionary<string, int> MatchingDisposition { get; set; } = new Dictionary<string, int>();
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
