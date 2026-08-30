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

        public ChannelMatcher(AliasResolver aliases)
        {
            _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
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

            var channelBuckets = new Dictionary<string, List<DiscoveredStream>>(StringComparer.Ordinal);
            foreach (var s in discovered)
            {
                var resolved = ResolveIdentity(s.Title);
                if (!channelBuckets.TryGetValue(resolved, out var list))
                {
                    list = new List<DiscoveredStream>();
                    channelBuckets[resolved] = list;
                }
                list.Add(s);
            }

            var existingById = existing.Channels.ToDictionary(c => c.Id, c => c);
            var streamsByChannel = existing.Channels
                .SelectMany(c => c.StreamIds.Select(id => (ChannelId: c.Id, StreamId: id)))
                .GroupBy(t => t.ChannelId)
                .ToDictionary(g => g.Key, g => g.Select(t => t.StreamId).ToHashSet());

            var existingStreamsById = existing.Streams.ToDictionary(s => s.Id, s => s);
            var groupByName = existing.Groups.ToDictionary(g => NormalizeGroupKey(g.Name), g => g, StringComparer.OrdinalIgnoreCase);

            var newChannelIdSeed = -1L;
            var decisions = new List<ChannelDecision>();
            var counts = new SyncReportCounts();

            foreach (var bucket in channelBuckets.OrderBy(b => b.Key, StringComparer.Ordinal))
            {
                var identity = bucket.Key;
                var canonicalName = ChooseCanonicalName(bucket.Value);

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
                    counts = counts with { Ambiguous = counts.Ambiguous + 1 };
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
                            counts = counts with { Skipped = counts.Skipped + 1 };
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
                        if (isNew) counts = counts with { NewStreams = counts.NewStreams + 1 };
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
                            counts = counts with { RemovedStreams = counts.RemovedStreams + 1 };
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
                        MatchReason = candidates[0].Reason,
                        MatchScore = candidates[0].Score,
                        Streams = streamDecisions,
                        AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
                    });
                    counts = counts with
                    {
                        Matched = counts.Matched + 1,
                        Unchanged = (streamDecisions.Any(d => d.Outcome == SyncOutcome.NewStream || d.Outcome == SyncOutcome.Removed)
                            ? counts.Unchanged
                            : counts.Unchanged + 1)
                    };
                    continue;
                }

                var newGroupName = ResolveGroupName(bucket.Value, groupByName);
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
                    MatchReason = "no-match",
                    MatchScore = 0,
                    Streams = newStreamDecisions,
                    AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
                });
                counts = counts with
                {
                    NewChannels = counts.NewChannels + 1,
                    NewStreams = counts.NewStreams + newStreamDecisions.Count(d => d.IsWorking),
                    Skipped = counts.Skipped + newStreamDecisions.Count(d => d.Outcome == SyncOutcome.Skipped),
                };
            }

            var plan = new MatchPlan
            {
                GeneratedAtUtc = stamp,
                SourcePlaylistPath = sourcePlaylistPath,
                DispatcharrBaseUrl = dispatcharrBaseUrl,
                DryRun = dryRun,
                MatchThreshold = threshold,
                Counts = counts,
                Channels = decisions,
            };

            return plan;
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

        private string ChooseCanonicalName(IReadOnlyList<DiscoveredStream> bucket)
        {
            var working = bucket.Where(b => b.IsWorking).ToList();
            var pool = working.Count > 0 ? working : bucket.ToList();
            return pool
                .OrderBy(s => ChannelNormalizer.Normalize(s.Title).Length)
                .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .First()
                .Title;
        }

        private static string NormalizeGroupKey(string name) =>
            string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim().ToLowerInvariant();

        private static string? ResolveGroupName(IReadOnlyList<DiscoveredStream> bucket, Dictionary<string, DispatcharrChannelGroup> existingGroups)
        {
            string? candidate = null;
            foreach (var s in bucket)
            {
                if (!string.IsNullOrWhiteSpace(s.Group))
                {
                    candidate = s.Group.Trim();
                    break;
                }
            }
            return candidate;
        }
    }
}
