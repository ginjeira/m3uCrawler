using System.Globalization;
using m3uCrawler.Models;
using m3uCrawler.Services.SourceOrdering;

namespace m3uCrawler.Services.Matching
{
    public interface IChannelMatcher
    {
        MatchPlan BuildPlan(
            IReadOnlyList<DiscoveredStream> discovered,
            DispatcharrState existing,
            MatchingOptions options,
            IStreamOrderingPolicy ordering,
            string sourcePlaylistPath,
            string dispatcharrBaseUrl,
            bool dryRun,
            DateTime? nowUtc = null);
    }

    public sealed class ChannelMatcher : IChannelMatcher
    {
        private readonly AliasResolver _aliases;
        private readonly FuzzyMatcher _fuzzy = new();
        private readonly MatchScorer _scorer = new();
        private readonly Func<string?, string?, string?, bool, OutputGroupKind>? _resolutionPolicy;

        /// <summary>
        /// Constructs a ChannelMatcher with the legacy single-dependency
        /// surface. OutputGroup resolution (ResolutionPolicy) is
        /// disabled unless the overload below is used.
        /// </summary>
        public ChannelMatcher(AliasResolver aliases)
            : this(aliases, null)
        {
        }

        /// <summary>
        /// Constructs a ChannelMatcher with an injected
        /// <see cref="ResolutionPolicy"/> delegate (or null to disable
        /// the OutputGroup pipeline). CanonicalName is always resolved
        /// through the static <see cref="GroupResolver"/>.
        ///
        /// <para>
        /// Foreign is determined deterministically: a SourceGroup that
        /// <see cref="GroupTaxonomy"/> maps to
        /// <see cref="OutputGroupKind.Foreign"/> marks the representative
        /// stream as foreign. Streams whose SourceGroup is not a known
        /// foreign group are treated as PT (isForeign=false).
        /// </para>
        /// </summary>
        public ChannelMatcher(
            AliasResolver aliases,
            Func<string?, string?, string?, bool, OutputGroupKind>? resolutionPolicy)
        {
            _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
            _resolutionPolicy = resolutionPolicy;
        }

        public MatchPlan BuildPlan(
            IReadOnlyList<DiscoveredStream> discovered,
            DispatcharrState existing,
            MatchingOptions options,
            IStreamOrderingPolicy ordering,
            string sourcePlaylistPath,
            string dispatcharrBaseUrl,
            bool dryRun,
            DateTime? nowUtc = null)
        {
            if (discovered == null) throw new ArgumentNullException(nameof(discovered));
            if (existing == null) throw new ArgumentNullException(nameof(existing));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (ordering == null) throw new ArgumentNullException(nameof(ordering));

            int threshold = options.MatchThreshold;
            var stamp = (nowUtc ?? DateTime.UtcNow).ToString("o");

            // Two-level classification + matching policy (commit
            // 3f4af81 + this iteration):
            //   1. ContentClassifier returns Kind + eligibility flags
            //      (ExistingMatchEligibility, NewChannelEligibility).
            //   2. Kinds with eligibility=false for BOTH flags are
            //      excluded immediately (Bundle, Vod, LiveCam,
            //      Placeholder, Category, Group, Foreign).
            //   3. Kinds with NewChannelEligibility=true go through the
            //      normal matching pipeline (curated channel identities).
            //   4. Kinds with only ExistingMatchEligibility=true
            //      (Unknown) try to attach to a single non-ambiguous
            //      existing channel; never create a new one.
            //
            // Invariantes preserved:
            //   b0dfc48 (stream dedup)
            //   91cad8e (reconcile by existingChannelId)
            //   420df83 (ambiguous groups without arbitrary selection)
            //   8877f6f (preserve channels without sources)
            //   b3157d6 (per-stream removal vs replace, untouched)
            var classificationCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var dispositionCounts = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["excluded"] = 0,
                ["unknownMatchedToExisting"] = 0,
                ["unknownReviewRequired"] = 0,
                ["newChannelsFromCuratedIdentity"] = 0,
            };
            var excluded = new List<ClassifiedExclusion>();
            // Review-required entries (Unknown without an existing
            // match) — counted, NOT promoted to NewChannel.
            var reviewRequired = new List<ClassifiedExclusion>();
            // Buckets that go through the existing-channel matcher
            // (Channel + Unknown with ExistingMatchEligibility=true).
            var channelBuckets = new Dictionary<string, List<DiscoveredStream>>(StringComparer.Ordinal);
            // Tracks, per bucket identity, whether the bucket is from
            // a curated Channel identity or from an Unknown entry. We
            // need this to enforce the NewChannelEligibility rule:
            // Unknown buckets that reach MatchBand.None become
            // review-required, NOT NewChannel.
            var bucketIsCurated = new Dictionary<string, bool>(StringComparer.Ordinal);

            foreach (var s in discovered)
            {
                var classification = ContentClassifier.Classify(s.Title, s.Group);
                var kindName = classification.Kind.ToString();
                classificationCounts.TryGetValue(kindName, out var prev);
                classificationCounts[kindName] = prev + 1;

                // 1) Kind-level exclusion: never matched, never created.
                if (!classification.ExistingMatchEligibility && !classification.NewChannelEligibility)
                {
                    dispositionCounts["excluded"] = dispositionCounts["excluded"] + 1;
                    excluded.Add(new ClassifiedExclusion
                    {
                        Title = s.Title ?? string.Empty,
                        Group = s.Group ?? string.Empty,
                        Kind = classification.Kind,
                        Reason = classification.Reason,
                        MatchingDisposition = "excluded",
                    });
                    continue;
                }

                var resolved = ResolveIdentity(s.Title);
                if (!channelBuckets.TryGetValue(resolved, out var list))
                {
                    list = new List<DiscoveredStream>();
                    channelBuckets[resolved] = list;
                    bucketIsCurated[resolved] = classification.NewChannelEligibility;
                }
                list.Add(s);
            }

            var existingById = existing.Channels.ToDictionary(c => c.Id, c => c);
            var streamsByChannel = existing.Channels
                .SelectMany(c => c.StreamIds.Select(id => (ChannelId: c.Id, StreamId: id)))
                .GroupBy(t => t.ChannelId)
                .ToDictionary(g => g.Key, g => g.Select(t => t.StreamId).ToHashSet());

            var existingStreamsById = existing.Streams.ToDictionary(s => s.Id, s => s);
            var groupIndex = GroupNameIndex.Build(existing.Groups);

            var newChannelIdSeed = -1L;
            var decisions = new List<ChannelDecision>();
            var counts = new SyncReportCounts
            {
                AmbiguousGroups = groupIndex.AmbiguousEntries.Count,
                Skipped = excluded.Count,
                Classification = classificationCounts,
                MatchingDisposition = dispositionCounts,
            };

            foreach (var bucket in channelBuckets.OrderBy(b => b.Key, StringComparer.Ordinal))
            {
                var identity = bucket.Key;
                var canonicalName = GroupResolver.ResolveCanonical(bucket.Value);
                var isCurated = bucketIsCurated.TryGetValue(identity, out var v) && v;

                // Representative stream for country/OutputGroup decisions:
                // prefer working, else first with a valid title.
                var representative = bucket.Value
                    .OrderByDescending(s => s.IsWorking)
                    .ThenBy(s => string.IsNullOrWhiteSpace(s.Title))
                    .ThenBy(s => ChannelNormalizer.Normalize(s.Title).Length)
                    .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                OutputGroupKind? outputGroup = null;
                if (_resolutionPolicy != null && representative != null)
                {
                    var taxonomyKind = GroupTaxonomy.Lookup(
                        GroupNormalizer.Normalize(representative.Group)).OutputGroup;
                    var isForeign = taxonomyKind == OutputGroupKind.Foreign;
                    outputGroup = _resolutionPolicy(
                        ChannelNormalizer.Normalize(representative.Title),
                        GroupNormalizer.Normalize(representative.Group),
                        representative.Title,
                        isForeign);
                }

                var candidates = existing.Channels
                    .Select(c => (Channel: c, Normalized: ChannelNormalizer.Normalize(c.Name)))
                    .Where(c => !string.IsNullOrWhiteSpace(c.Normalized))
                    .Select(c =>
                    {
                        var viaAlias = _aliases.Resolve(c.Channel.Name);
                        var queryForAlias = viaAlias?.Canonical ?? identity;
                        var ms = _fuzzy.Score(queryForAlias, c.Normalized);
                        var aliasMatch = viaAlias != null && string.Equals(viaAlias.Canonical, ChannelNormalizer.Normalize(c.Channel.Name), StringComparison.Ordinal);
                        return new
                        {
                            Channel = c.Channel,
                            Score = ms.Score,
                            Reason = aliasMatch && viaAlias != null ? $"alias:{viaAlias.Reason}" : ms.Reason,
                        };
                    })
                    .OrderByDescending(c => c.Score)
                    .Take(5)
                    .ToList();

                bool hasAnyCandidate = candidates.Count > 0;
                int topScore = hasAnyCandidate ? candidates[0].Score : 0;
                var otherScores = hasAnyCandidate ? candidates.Skip(1).Select(c => c.Score).ToList() : new List<int>();

                var band = hasAnyCandidate
                    ? _scorer.Classify(topScore, otherScores, threshold)
                    : MatchBand.None;

                if (band == MatchBand.Ambiguous)
                {
                    // Ambiguous: never NewChannel, never silent
                    // attachment. Record per-stream diagnostic.
                    if (isCurated)
                    {
                        // For curated buckets with ambiguous matches,
                        // preserve the legacy decision surface: an
                        // Ambiguous decision is exposed, not silently
                        // dropped, so the user can resolve via UI.
                        var top = candidates[0];
                        var second = candidates[1];
                        var streamDecisions = bucket.Value
                            .Select(s => StreamDecisionForNewChannel(s, newChannelIdSeed--, ordering, defaultStreamId: null))
                            .ToList();
                        decisions.Add(new ChannelDecision
                        {
                            Identity = identity,
                            CanonicalName = canonicalName,
                            Outcome = SyncOutcome.Ambiguous,
                            ExistingChannelId = null,
                            ChannelGroupName = null,
                            OutputGroup = outputGroup,
                            MatchReason = $"ambiguous:{top.Reason}|{second.Reason}",
                            MatchScore = top.Score,
                            Streams = streamDecisions,
                            AmbiguousCandidates = new[]
                            {
                                new AmbiguousCandidate
                                {
                                    ExistingChannelId = top.Channel.Id,
                                    ExistingChannelName = top.Channel.Name,
                                    Score = top.Score,
                                    Reason = top.Reason,
                                },
                                new AmbiguousCandidate
                                {
                                    ExistingChannelId = second.Channel.Id,
                                    ExistingChannelName = second.Channel.Name,
                                    Score = second.Score,
                                    Reason = second.Reason,
                                }
                            }
                        });
                        counts.Ambiguous = counts.Ambiguous + 1;
                    }
                    else
                    {
                        // Unknown bucket: ambiguous existing matches
                        // → review-required, no NewChannel.
                        RecordUnknownReviewRequired(
                            bucket.Value, classificationCounts, dispositionCounts,
                            reviewRequired, "ambiguous-existing-match");
                    }
                    continue;
                }

                if (band == MatchBand.Matched || band == MatchBand.Exact)
                {
                    var matched = candidates[0].Channel;
                    var existingStreamIds = streamsByChannel.TryGetValue(matched.Id, out var sids)
                        ? sids.ToHashSet()
                        : new HashSet<long>();

                    var keepStreamIds = new HashSet<long>();

                    var ordered = ordering.Order(bucket.Value);
                    var streamDecisions = new List<StreamMatchDecision>();
                    int order = 0;
                    foreach (var (stream, reason) in ordered)
                    {
                        var existingForUrl = existing.Streams.FirstOrDefault(es =>
                            string.Equals(es.Url, stream.Url, StringComparison.OrdinalIgnoreCase) && es.IsWorking);

                        if (existingForUrl == null)
                        {
                            var streamNormTitle = ChannelNormalizer.Normalize(stream.Title);
                            if (!string.IsNullOrWhiteSpace(streamNormTitle))
                            {
                                existingForUrl = existing.Streams.FirstOrDefault(es =>
                                    es.IsWorking
                                    && string.Equals(ChannelNormalizer.Normalize(es.Name), streamNormTitle, StringComparison.Ordinal)
                                    && !keepStreamIds.Contains(es.Id));
                            }
                        }

                        if (existingForUrl == null && !stream.IsWorking)
                        {
                            streamDecisions.Add(new StreamMatchDecision
                            {
                                Provider = stream.Provider,
                                StreamUrl = stream.Url,
                                StreamName = stream.Title,
                                Outcome = SyncOutcome.Skipped,
                                ProposedOrder = -1,
                                OrderReason = "not-working",
                                IsWorking = false,
                                GroupName = stream.Group,
                            });
                            counts.Skipped = counts.Skipped + 1;
                            continue;
                        }

                        long? existingStreamId = existingForUrl?.Id;
                        if (existingStreamId.HasValue) keepStreamIds.Add(existingStreamId.Value);

                        bool isNew = existingForUrl == null;
                        var outcome = isNew ? SyncOutcome.NewStream : SyncOutcome.ExistingUnchanged;
                        streamDecisions.Add(new StreamMatchDecision
                        {
                            Provider = stream.Provider,
                            StreamUrl = stream.Url,
                            StreamName = stream.Title,
                            Outcome = outcome,
                            ExistingStreamId = existingStreamId,
                            ProposedOrder = isNew ? order : -1,
                            OrderReason = reason,
                            IsWorking = stream.IsWorking,
                            GroupName = stream.Group,
                        });
                        if (isNew) counts.NewStreams = counts.NewStreams + 1;
                        order++;
                    }

                    var staleIds = existingStreamIds.Except(keepStreamIds).ToList();
                    foreach (var sid in staleIds)
                    {
                        if (existingStreamsById.TryGetValue(sid, out var es))
                        {
                            streamDecisions.Add(new StreamMatchDecision
                            {
                                Provider = es.M3uAccountName ?? "(unknown)",
                                StreamUrl = es.Url,
                                StreamName = es.Name,
                                Outcome = SyncOutcome.Removed,
                                ExistingStreamId = sid,
                                ProposedOrder = -1,
                                OrderReason = "missing-from-current-playlist",
                                IsWorking = es.IsWorking,
                                GroupName = es.GroupName,
                            });
                            counts.RemovedStreams = counts.RemovedStreams + 1;
                        }
                    }

                    var groupName = matched.GroupName;
                    decisions.Add(new ChannelDecision
                    {
                        Identity = identity,
                        CanonicalName = matched.Name,
                        Outcome = streamDecisions.Any(d => d.Outcome == SyncOutcome.NewStream || d.Outcome == SyncOutcome.Removed)
                            ? SyncOutcome.ExistingReassigned
                            : SyncOutcome.ExistingUnchanged,
                        ExistingChannelId = matched.Id,
                        ChannelGroupName = groupName,
                        OutputGroup = outputGroup,
                        MatchReason = candidates[0].Reason,
                        MatchScore = candidates[0].Score,
                        Streams = streamDecisions,
                        AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
                    });
                    if (isCurated)
                    {
                        counts.Matched = counts.Matched + 1;
                    }
                    else
                    {
                        // Unknown bucket that attached to an existing
                        // channel: count as matched (preserves
                        // reconcile behaviour) but route the
                        // diagnostic to a new disposition counter
                        // so the dashboard distinguishes curated
                        // matches from Unknown-to-existing matches.
                        counts.Matched = counts.Matched + 1;
                        dispositionCounts["unknownMatchedToExisting"] =
                            dispositionCounts["unknownMatchedToExisting"] + 1;
                    }
                    if (!(streamDecisions.Any(d => d.Outcome == SyncOutcome.NewStream || d.Outcome == SyncOutcome.Removed)))
                        counts.Unchanged = counts.Unchanged + 1;
                    continue;
                }

                // MatchBand.None: no existing match.
                if (!isCurated)
                {
                    // Unknown bucket with no existing match → review
                    // required. NEVER NewChannel.
                    RecordUnknownReviewRequired(
                        bucket.Value, classificationCounts, dispositionCounts,
                        reviewRequired, "no-existing-match");
                    continue;
                }

                // Curated bucket with no existing match → NewChannel
                // (only path that promotes a curated identity).
                dispositionCounts["newChannelsFromCuratedIdentity"] =
                    dispositionCounts["newChannelsFromCuratedIdentity"] + 1;
                var newGroupName = ResolveGroupName(bucket.Value, groupIndex);
                var newStreamDecisions = bucket.Value
                    .Select(s => StreamDecisionForNewChannel(s, newChannelIdSeed--, ordering, defaultStreamId: null))
                    .ToList();

                decisions.Add(new ChannelDecision
                {
                    Identity = identity,
                    CanonicalName = canonicalName,
                    Outcome = SyncOutcome.NewChannel,
                    ExistingChannelId = null,
                    ChannelGroupName = newGroupName,
                    OutputGroup = outputGroup,
                    MatchReason = "no-match",
                    MatchScore = 0,
                    Streams = newStreamDecisions,
                    AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
                });
                counts.NewChannels = counts.NewChannels + 1;
                counts.NewStreams = counts.NewStreams + newStreamDecisions.Count(d => d.IsWorking);
                counts.Skipped = counts.Skipped + newStreamDecisions.Count(d => d.Outcome == SyncOutcome.Skipped);
            }

            var provisional = new MatchPlan
            {
                GeneratedAtUtc = stamp,
                SourcePlaylistPath = sourcePlaylistPath,
                DispatcharrBaseUrl = dispatcharrBaseUrl,
                DryRun = dryRun,
                MatchThreshold = threshold,
                Counts = counts,
                Channels = decisions,
                AmbiguousGroups = groupIndex.AmbiguousEntries,
                ClassifiedExclusions = excluded,
                UnknownReviewRequired = reviewRequired,
            };

            return ReconcileByExistingChannelId(provisional);
        }

        /// <summary>
        /// Records a bucket whose classification is <see cref="ChannelKind.Unknown"/>
        /// (eligible to match existing channels only) as
        /// review-required. Used when the existing-channel match was
        /// <see cref="MatchBand.None"/> (no match) or
        /// <see cref="MatchBand.Ambiguous"/> (multiple close matches).
        /// Never promotes the entry to <see cref="SyncOutcome.NewChannel"/>.
        ///
        /// <para>
        /// The Kind-level counter (<c>Classification</c>) is NOT
        /// incremented here — the main loop already counted each entry
        /// when it was first classified. Re-counting here would
        /// double the per-stream kind counter.
        /// </para>
        /// </summary>
        private static void RecordUnknownReviewRequired(
            IReadOnlyList<DiscoveredStream> bucket,
            IDictionary<string, int> classificationCounts,
            IDictionary<string, int> dispositionCounts,
            List<ClassifiedExclusion> reviewRequired,
            string reasonSuffix)
        {
            dispositionCounts["unknownReviewRequired"] =
                dispositionCounts["unknownReviewRequired"] + 1;
            foreach (var s in bucket)
            {
                // Kind for diagnostic metadata only; the kind
                // counter is already updated by the main loop.
                var kind = ContentClassifier.Classify(s.Title, s.Group).Kind;
                reviewRequired.Add(new ClassifiedExclusion
                {
                    Title = s.Title ?? string.Empty,
                    Group = s.Group ?? string.Empty,
                    Kind = kind,
                    Reason = $"unknown-review-required:{reasonSuffix}",
                    MatchingDisposition = "unknown-review-required",
                });
            }
        }

        private static MatchPlan ReconcileByExistingChannelId(MatchPlan plan)
        {
            // Group decisions by their resolved Dispatcharr channel id.
            //   - ExistingReassigned / ExistingUnchanged: keyed by ExistingChannelId.
            //   - Ambiguous: dropped here (handled by ReportBuilder.Build later).
            //   - NewChannel: keyed by CanonicalName (post-normalization) since no id is assigned yet.
            // Within each group we union the Streams, drop per-decision Removed outcomes that
            // conflict with another decision's ExistingUnchanged / NewStream, and recompute the
            // channel outcome deterministically.
            var byExistingId = new Dictionary<long, List<ChannelDecision>>();
            var newChannelGroups = new Dictionary<string, List<ChannelDecision>>();

            foreach (var ch in plan.Channels)
            {
                if (ch.ExistingChannelId.HasValue)
                {
                    if (!byExistingId.TryGetValue(ch.ExistingChannelId.Value, out var list))
                    {
                        list = new List<ChannelDecision>();
                        byExistingId[ch.ExistingChannelId.Value] = list;
                    }
                    list.Add(ch);
                }
                else if (ch.Outcome == SyncOutcome.NewChannel)
                {
                    var key = NormalizeIdentityForDedup(ch.CanonicalName);
                    if (!newChannelGroups.TryGetValue(key, out var list))
                    {
                        list = new List<ChannelDecision>();
                        newChannelGroups[key] = list;
                    }
                    list.Add(ch);
                }
            }

            var reconciled = new List<ChannelDecision>(plan.Channels.Count);
            var seenIds = new HashSet<long>();
            var seenNewKeys = new HashSet<string>();

            // Preserve original order: walk plan.Channels; if the id was not yet merged, merge now.
            foreach (var ch in plan.Channels)
            {
                if (ch.ExistingChannelId.HasValue)
                {
                    var id = ch.ExistingChannelId.Value;
                    if (!seenIds.Add(id)) continue;
                    var list = byExistingId[id];
                    reconciled.Add(MergeExistingChannelDecisions(list));
                }
                else if (ch.Outcome == SyncOutcome.NewChannel)
                {
                    var key = NormalizeIdentityForDedup(ch.CanonicalName);
                    if (!seenNewKeys.Add(key)) continue;
                    var list = newChannelGroups[key];
                    reconciled.Add(MergeNewChannelDecisions(list));
                }
                else
                {
                    reconciled.Add(ch);
                }
            }

            plan.Counts.OutputGroups = reconciled
                .Where(c => c.OutputGroup.HasValue)
                .GroupBy(c => c.OutputGroup!.Value.ToString())
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count());

            return new MatchPlan
            {
                GeneratedAtUtc = plan.GeneratedAtUtc,
                SourcePlaylistPath = plan.SourcePlaylistPath,
                DispatcharrBaseUrl = plan.DispatcharrBaseUrl,
                DryRun = plan.DryRun,
                MatchThreshold = plan.MatchThreshold,
                Counts = plan.Counts,
                Channels = reconciled,
                AmbiguousGroups = plan.AmbiguousGroups,
                ClassifiedExclusions = plan.ClassifiedExclusions,
                UnknownReviewRequired = plan.UnknownReviewRequired,
            };
        }

        private static string NormalizeIdentityForDedup(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            return name.Trim().ToLowerInvariant();
        }

        private static ChannelDecision MergeExistingChannelDecisions(List<ChannelDecision> list)
        {
            if (list.Count == 1) return list[0];

            var first = list[0];
            var existingChannelId = first.ExistingChannelId!.Value;
            var canonicalName = first.CanonicalName;

            var keepExisting = new HashSet<long>();
            var newStreams = new List<StreamMatchDecision>();
            var removed = new List<StreamMatchDecision>();
            var identities = new SortedSet<string>(StringComparer.Ordinal);
            var matchReasons = new SortedSet<string>(StringComparer.Ordinal);
            int topScore = 0;

            foreach (var d in list)
            {
                identities.Add(d.Identity);
                if (!string.IsNullOrWhiteSpace(d.MatchReason)) matchReasons.Add(d.MatchReason);
                if (d.MatchScore > topScore) topScore = d.MatchScore;
                foreach (var s in d.Streams)
                {
                    switch (s.Outcome)
                    {
                        case SyncOutcome.ExistingUnchanged:
                        case SyncOutcome.ExistingReassigned:
                        case SyncOutcome.ExistingReordered:
                            if (s.ExistingStreamId.HasValue)
                            {
                                keepExisting.Add(s.ExistingStreamId.Value);
                            }
                            else
                            {
                                newStreams.Add(s);
                            }
                            break;
                        case SyncOutcome.NewStream:
                            newStreams.Add(s);
                            break;
                        case SyncOutcome.Removed:
                            // Defer: only remove if no other decision said keep.
                            if (s.ExistingStreamId.HasValue)
                                removed.Add(s);
                            else
                                removed.Add(s);
                            break;
                        case SyncOutcome.Skipped:
                            newStreams.Add(s);
                            break;
                        default:
                            newStreams.Add(s);
                            break;
                    }
                }
            }

            // Final removed set = removed-from-some-bucket minus kept-by-any-bucket.
            var finalRemoved = removed
                .Where(s => s.ExistingStreamId.HasValue && !keepExisting.Contains(s.ExistingStreamId.Value))
                .GroupBy(s => s.ExistingStreamId!.Value)
                .Select(g => g.First())
                .ToList();

            var streams = new List<StreamMatchDecision>();
            int order = 0;
            foreach (var s in newStreams
                .OrderBy(s => s.ProposedOrder < 0 ? int.MaxValue : s.ProposedOrder)
                .ThenBy(s => s.StreamUrl ?? string.Empty, StringComparer.Ordinal))
            {
                streams.Add(new StreamMatchDecision
                {
                    Provider = s.Provider,
                    StreamUrl = s.StreamUrl,
                    StreamName = s.StreamName,
                    Outcome = s.Outcome,
                    ExistingStreamId = s.ExistingStreamId,
                    ProposedOrder = s.Outcome == SyncOutcome.NewStream ? order : -1,
                    OrderReason = s.OrderReason,
                    IsWorking = s.IsWorking,
                    GroupName = s.GroupName,
                });
                if (s.Outcome == SyncOutcome.NewStream) order++;
            }
            foreach (var s in finalRemoved)
            {
                streams.Add(s);
            }
            // ExistingUnchanged/Reassigned at the end with stable order.
            foreach (var sid in keepExisting.OrderBy(x => x))
            {
                // Pick one representative stream per id from any decision.
                var rep = list.SelectMany(d => d.Streams)
                    .FirstOrDefault(s => s.ExistingStreamId == sid && s.Outcome != SyncOutcome.Removed);
                if (rep == null) continue;
                streams.Add(new StreamMatchDecision
                {
                    Provider = rep.Provider,
                    StreamUrl = rep.StreamUrl,
                    StreamName = rep.StreamName,
                    Outcome = SyncOutcome.ExistingUnchanged,
                    ExistingStreamId = sid,
                    ProposedOrder = -1,
                    OrderReason = rep.OrderReason ?? "merged-keep",
                    IsWorking = rep.IsWorking,
                    GroupName = rep.GroupName,
                });
            }

            bool hasChange = streams.Any(s => s.Outcome == SyncOutcome.NewStream || s.Outcome == SyncOutcome.Removed);
            var outcome = hasChange ? SyncOutcome.ExistingReassigned : SyncOutcome.ExistingUnchanged;

            // StreamsEmptied is true when the channel will end up with no streams on
            // Dispatcharr after apply: at least one Removed entry and no surviving, new or
            // skipped streams. The apply phase then issues an explicit PATCH with streams=[].
            // We exclude Skipped entries from the "keep or new" set because they represent
            // preservation / non-intervention (IsWorking=false) and never translate to a
            // stream id on the channel.
            bool streamsEmptied =
                streams.Any(s => s.Outcome == SyncOutcome.Removed)
                && !streams.Any(s =>
                    s.Outcome == SyncOutcome.NewStream
                    || s.Outcome == SyncOutcome.ExistingUnchanged
                    || s.Outcome == SyncOutcome.ExistingReassigned
                    || s.Outcome == SyncOutcome.ExistingReordered
                    || s.Outcome == SyncOutcome.Skipped);

            return new ChannelDecision
            {
                Identity = string.Join("|", identities),
                CanonicalName = canonicalName,
                Outcome = outcome,
                ExistingChannelId = existingChannelId,
                ChannelGroupName = first.ChannelGroupName,
                OutputGroup = first.OutputGroup,
                MatchReason = matchReasons.Count == 1
                    ? matchReasons.First()
                    : "merged:" + string.Join("|", matchReasons),
                MatchScore = topScore,
                Streams = streams,
                StreamsEmptied = streamsEmptied,
                AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
            };
        }

        private static ChannelDecision MergeNewChannelDecisions(List<ChannelDecision> list)
        {
            if (list.Count == 1) return list[0];
            var first = list[0];
            var streams = list.SelectMany(d => d.Streams)
                .OrderBy(s => s.ProposedOrder < 0 ? int.MaxValue : s.ProposedOrder)
                .ThenBy(s => s.StreamUrl ?? string.Empty, StringComparer.Ordinal)
                .ToList();
            return new ChannelDecision
            {
                Identity = string.Join("|", list.Select(d => d.Identity).OrderBy(x => x, StringComparer.Ordinal)),
                CanonicalName = first.CanonicalName,
                Outcome = SyncOutcome.NewChannel,
                ExistingChannelId = null,
                ChannelGroupName = first.ChannelGroupName,
                OutputGroup = first.OutputGroup,
                MatchReason = "merged:" + string.Join("|", list.Select(d => d.MatchReason)),
                MatchScore = 0,
                Streams = streams,
                AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
            };
        }

        private StreamMatchDecision StreamDecisionForNewChannel(
            DiscoveredStream s,
            long placeholderChannelId,
            IStreamOrderingPolicy ordering,
            long? defaultStreamId)
        {
            if (!s.IsWorking)
            {
                return new StreamMatchDecision
                {
                    Provider = s.Provider,
                    StreamUrl = s.Url,
                    StreamName = s.Title,
                    Outcome = SyncOutcome.Skipped,
                    ProposedOrder = -1,
                    OrderReason = "not-working",
                    IsWorking = false,
                    GroupName = s.Group,
                };
            }

            return new StreamMatchDecision
            {
                Provider = s.Provider,
                StreamUrl = s.Url,
                StreamName = s.Title,
                Outcome = SyncOutcome.NewStream,
                ExistingStreamId = defaultStreamId,
                ProposedOrder = 0,
                OrderReason = "new-channel-initial",
                IsWorking = true,
                GroupName = s.Group,
            };
        }

        private string ResolveIdentity(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return string.Empty;
            var alias = _aliases.Resolve(title);
            if (alias != null) return alias.Canonical;
            return ChannelNormalizer.Normalize(title);
        }

        /// <summary>
        /// Legacy bundle-guard helper retained as an obsolete stub for
        /// callers in the test suite that still reference it.
        /// Production code now delegates to
        /// <see cref="ContentClassifier.Classify"/>. This method will
        /// be removed in a follow-up cleanup once the tests are
        /// migrated.
        /// </summary>
        [Obsolete("Use ContentClassifier.Classify; retained for legacy test references.")]
        internal static bool IsBundleOrCategory(string? title, string? group)
        {
            return ContentClassifier.Classify(title, group).Kind != ChannelKind.Channel;
        }

        private static string NormalizeGroupKey(string name) =>
            string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim().ToLowerInvariant();

        private static string? ResolveGroupName(IReadOnlyList<DiscoveredStream> bucket, GroupNameIndex existingGroups)
        {
            foreach (var s in bucket)
            {
                if (string.IsNullOrWhiteSpace(s.Group)) continue;
                var key = NormalizeGroupKey(s.Group);
                if (key.Length == 0) continue;
                if (existingGroups.TryGetUnambiguous(key, out var group))
                    return group.Name;
                if (existingGroups.IsBlocked(key))
                    return null;
                return s.Group.Trim();
            }
            return null;
        }

        private readonly struct GroupNameIndex
        {
            private readonly Dictionary<string, DispatcharrChannelGroup> _unique;
            private readonly Dictionary<string, string> _blocked;

            public IReadOnlyList<AmbiguousGroupEntry> AmbiguousEntries { get; }

            private GroupNameIndex(
                Dictionary<string, DispatcharrChannelGroup> unique,
                Dictionary<string, string> blocked,
                IReadOnlyList<AmbiguousGroupEntry> ambiguous)
            {
                _unique = unique;
                _blocked = blocked;
                AmbiguousEntries = ambiguous;
            }

            public static GroupNameIndex Build(IReadOnlyList<DispatcharrChannelGroup> groups)
            {
                var byKey = new Dictionary<string, List<DispatcharrChannelGroup>>(StringComparer.Ordinal);

                foreach (var g in groups)
                {
                    var key = NormalizeGroupKey(g.Name);
                    if (key.Length == 0) continue;
                    if (!byKey.TryGetValue(key, out var list))
                    {
                        list = new List<DispatcharrChannelGroup>();
                        byKey[key] = list;
                    }
                    list.Add(g);
                }

                var unique = new Dictionary<string, DispatcharrChannelGroup>(StringComparer.Ordinal);
                var blocked = new Dictionary<string, string>(StringComparer.Ordinal);
                var ambiguous = new List<AmbiguousGroupEntry>();

                foreach (var kv in byKey.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    if (kv.Value.Count == 1)
                    {
                        unique[kv.Key] = kv.Value[0];
                    }
                    else
                    {
                        blocked[kv.Key] = string.Join("|", kv.Value.Select(g => g.Id));
                        ambiguous.Add(new AmbiguousGroupEntry
                        {
                            NormalizedName = kv.Key,
                            GroupIds = kv.Value.Select(g => g.Id).ToArray(),
                            GroupNames = kv.Value.Select(g => g.Name).ToArray(),
                        });
                    }
                }

                return new GroupNameIndex(unique, blocked, ambiguous);
            }

            public bool TryGetUnambiguous(string normalizedKey, out DispatcharrChannelGroup group)
            {
                if (_blocked.ContainsKey(normalizedKey))
                {
                    group = default!;
                    return false;
                }
                return _unique.TryGetValue(normalizedKey, out group!);
            }

            public bool IsBlocked(string normalizedKey) => _blocked.ContainsKey(normalizedKey);
        }
    }
}
