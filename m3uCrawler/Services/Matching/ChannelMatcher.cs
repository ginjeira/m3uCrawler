using System.Globalization;
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;
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
        private readonly m3uCrawler.Services.Catalog.CatalogResolver? _catalog;

        /// <summary>
        /// Constructs a ChannelMatcher with the legacy single-dependency
        /// surface. OutputGroup resolution (ResolutionPolicy) is
        /// disabled unless the overload below is used.
        /// </summary>
        public ChannelMatcher(AliasResolver aliases)
            : this(aliases, null, null)
        {
        }

        /// <summary>
        /// Constructs a ChannelMatcher with an injected
        /// <see cref="ResolutionPolicy"/> delegate (or null to disable
        /// the OutputGroup pipeline). CanonicalName is always resolved
        /// through the static <see cref="GroupResolver"/>.
        /// </summary>
        public ChannelMatcher(
            AliasResolver aliases,
            Func<string?, string?, string?, bool, OutputGroupKind>? resolutionPolicy)
            : this(aliases, resolutionPolicy, null)
        {
        }

        /// <summary>
        /// Constructs a ChannelMatcher with an injected
        /// <see cref="m3uCrawler.Services.Catalog.CatalogResolver"/>
        /// (or null to keep the legacy
        /// <c>ChannelCategoryLookup.Contains()</c>-driven policy).
        /// When the catalog is provided, publication policy
        /// (<c>CreateEligible / ReviewOnly / Excluded / MergeOnly</c>)
        /// is read from the persistent catalog; otherwise the legacy
        /// in-memory dictionary drives the decision.
        /// </summary>
        public ChannelMatcher(
            AliasResolver aliases,
            Func<string?, string?, string?, bool, OutputGroupKind>? resolutionPolicy,
            m3uCrawler.Services.Catalog.CatalogResolver? catalog)
        {
            _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
            _resolutionPolicy = resolutionPolicy;
            _catalog = catalog;
        }

        private async Task<MatchPlan> BuildPlanCoreAsync(
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

            return await BuildPlanCoreInternalAsync(discovered, existing, options, ordering, sourcePlaylistPath, dispatcharrBaseUrl, dryRun, nowUtc);
        }

        /// <summary>
        /// Sync shim that calls the async core. Tests not using the
        /// catalog can still call <c>BuildPlan(...)</c> and get a
        /// result back synchronously.
        /// </summary>
        public async Task<MatchPlan> BuildPlanAsync(
            IReadOnlyList<DiscoveredStream> discovered,
            DispatcharrState existing,
            MatchingOptions options,
            IStreamOrderingPolicy ordering,
            string sourcePlaylistPath,
            string dispatcharrBaseUrl,
            bool dryRun,
            DateTime? nowUtc = null)
        {
            return await BuildPlanCoreAsync(discovered, existing, options, ordering, sourcePlaylistPath, dispatcharrBaseUrl, dryRun, nowUtc);
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
            return BuildPlanAsync(discovered, existing, options, ordering, sourcePlaylistPath, dispatcharrBaseUrl, dryRun, nowUtc)
                .GetAwaiter()
                .GetResult();
        }

        private async Task<MatchPlan> BuildPlanCoreInternalAsync(
            IReadOnlyList<DiscoveredStream> discovered,
            DispatcharrState existing,
            MatchingOptions options,
            IStreamOrderingPolicy ordering,
            string sourcePlaylistPath,
            string dispatcharrBaseUrl,
            bool dryRun,
            DateTime? nowUtc = null)
        {
            int threshold = options.MatchThreshold;
            var stamp = (nowUtc ?? DateTime.UtcNow).ToString("o");

            // Two-level classification + matching policy:
            //   1. ContentClassifier returns Kind + ExistingMatchEligibility
            //      (NewChannelEligibility is removed; it now comes from
            //      the persistent catalog in step 1.5).
            //   2. Kinds with ExistingMatchEligibility=false are excluded
            //      immediately (Bundle, Vod, LiveCam, Placeholder,
            //      Category, Group, Foreign).
            //   3. Streams are split by ELIGIBILITY TIER into two
            //      parallel bucket collections (curated vs unknown),
            //      keyed by tier+identity. This guarantees that:
            //        - a curated stream can never be promoted to a
            //          decision whose input is shared with an Unknown
            //          stream of the same identity (and vice-versa);
            //        - the order of arrival cannot promote an Unknown
            //          stream to a curated path (or vice-versa);
            //        - each tier uses its own matching strategy:
            //            curated: full fuzzy + alias + threshold;
            //            unknown: equality OR explicit alias only.
            //   1.5. For <c>Kind=Channel</c> the matcher consults the
            //        catalog to decide <c>NewChannelEligibility</c>:
            //        - IdentityRule ReviewOnly → bucket tier = Unknown
            //          (ReviewItem path; never NewChannel);
            //        - IdentityRule Excluded → excluded (no bucket);
            //        - CanonicalChannel with CreateEligible →
            //          NewChannelEligibility = true;
            //        - CanonicalChannel with MergeOnly/ReviewOnly/
            //          Excluded → NewChannelEligibility = false (Unknown
            //          tier; merge-only attach to existing);
            //        - Unknown canonical → NewChannelEligibility = false.
            //
            // Invariantes preservados:
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
            var reviewRequired = new List<ClassifiedExclusion>();
            // Tier-split bucket storage. Each entry keeps track of
            // whether the stream's classification is curated.
            var channelBuckets = new Dictionary<(BucketTier Tier, string Identity), List<DiscoveredStream>>();
            // Tracks the source-of-truth eligibility tier for the
            // bucket identity in the tier. A given (tier, identity)
            // pair is homogeneous by construction (we don't mix streams
            // with different NewChannelEligibility in the same tier).
            var bucketNewChannelEligible = new Dictionary<(BucketTier, string), bool>();

            foreach (var s in discovered)
            {
                var classification = ContentClassifier.Classify(s.Title, s.Group);
                var kindName = classification.Kind.ToString();
                classificationCounts.TryGetValue(kindName, out var prev);
                classificationCounts[kindName] = prev + 1;

                // 1) Kind-level exclusion: never matched, never created.
                if (!classification.ExistingMatchEligibility)
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

                // 1.5) If the catalog is active, resolve the
                //     publication policy for this normalized identity.
                //     - IdentityRule ReviewOnly → bucket tier = Unknown
                //       (ReviewItem path; never NewChannel);
                //     - IdentityRule Excluded → excluded (no bucket);
                //     - CanonicalChannel with CreateEligible →
                //       NewChannelEligibility = true (tier = Curated);
                //     - CanonicalChannel with MergeOnly/ReviewOnly/
                //       Excluded → NewChannelEligibility = false (tier =
                //       Unknown; merge-only attach to existing);
                //     - Unknown canonical → NewChannelEligibility = false.
                bool canCreateNew = false;
                CatalogResolution? catalogResolution = null;
                if (_catalog != null)
                {
                    var normalized = ChannelNormalizer.Normalize(s.Title);
                    catalogResolution = await _catalog.ResolveAsync(normalized);
                    if (catalogResolution?.Kind == CatalogResolutionKind.Rule)
                    {
                        if (catalogResolution.Value.RuleDisposition == RuleDisposition.ReviewOnly)
                        {
                            // Mark for review; never NewChannel.
                            await _catalog.UpsertReviewItemAsync(
                                normalized,
                                s.Group ?? string.Empty,
                                "not-approved-in-publication-catalog",
                                catalogResolution.Value.RuleReason ?? string.Empty);
                            dispositionCounts["unknownReviewRequired"] =
                                dispositionCounts["unknownReviewRequired"] + 1;
                            reviewRequired.Add(new ClassifiedExclusion
                            {
                                Title = s.Title ?? string.Empty,
                                Group = s.Group ?? string.Empty,
                                Kind = ChannelKind.Unknown,
                                Reason = $"review-only:{catalogResolution.Value.RuleReason}",
                                MatchingDisposition = "unknown-review-required",
                            });
                            continue;
                        }
                        else
                        {
                            // Excluded by rule.
                            dispositionCounts["excluded"] = dispositionCounts["excluded"] + 1;
                            excluded.Add(new ClassifiedExclusion
                            {
                                Title = s.Title ?? string.Empty,
                                Group = s.Group ?? string.Empty,
                                Kind = classification.Kind,
                                Reason = "excluded-by-rule",
                                MatchingDisposition = "excluded",
                            });
                            continue;
                        }
                    }
                    canCreateNew = catalogResolution.Value.AllowsNewChannel;
                }
                else
                {
                    // Legacy mode (no catalog): the legacy code marked
                    // every Channel kind as eligible to create new.
                    canCreateNew = classification.Kind == ChannelKind.Channel;
                }

                // 2) Pick tier.
                var tier = canCreateNew ? BucketTier.Curated : BucketTier.Unknown;

                // Use the catalog-resolved canonical identity when
                // available (so that "BTV" / "benficatv" / "benfica tv"
                // all bucket to "benfica-tv"). When the catalog is
                // inactive or the resolution is unknown, fall back to
                // the legacy alias resolver / normalizer.
                string resolved = (catalogResolution?.CanonicalKey) is { Length: > 0 } k
                    ? k
                    : ResolveIdentity(s.Title);
                var key = (tier, resolved);
                if (!channelBuckets.TryGetValue(key, out var list))
                {
                    list = new List<DiscoveredStream>();
                    channelBuckets[key] = list;
                    bucketNewChannelEligible[key] = canCreateNew;
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

            // Ownership guard: mapa de stream id → StreamOwnership
            // carregado em batch (uma única query) para que
            // BuildExistingDecision possa filtrar Removed de
            // streams External/Unknown. Sem catalog (legacy mode)
            // o mapa fica vazio e o filtro trata todas as streams
            // como CrawlerManaged — mesmo comportamento histórico
            // para não regredir testes pré-catalog.
            var ownershipByStreamId = new Dictionary<long, StreamOwnership>();
            if (_catalog != null && existing.Streams.Count > 0)
            {
                ownershipByStreamId = (await _catalog.GetStreamOwnershipMapAsync(
                    existing.Streams.Select(s => s.Id).ToList()))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
            }

            var newChannelIdSeed = -1L;
            var decisions = new List<ChannelDecision>();
            var counts = new SyncReportCounts
            {
                AmbiguousGroups = groupIndex.AmbiguousEntries.Count,
                Skipped = excluded.Count,
                Classification = classificationCounts,
                MatchingDisposition = dispositionCounts,
            };

            // Iterate buckets in deterministic order: by tier, then
            // identity. Curated first, then Unknown (so the
            // Reconciliation step sees a stable sequence).
            foreach (var bucket in channelBuckets
                .OrderBy(kv => kv.Key.Tier)
                .ThenBy(kv => kv.Key.Identity, StringComparer.Ordinal))
            {
                var (tier, identity) = bucket.Key;
                var isCurated = bucketNewChannelEligible[bucket.Key];
                var canonicalName = GroupResolver.ResolveCanonical(bucket.Value);

                // Representative stream for country/OutputGroup decisions.
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

                bool ambiguous = false;
                bool skipNoMatchRecord = false;
                DispatcharrChannel? matched = null;
                string? matchReason = null;
                int matchScore = 0;
                if (tier == BucketTier.Curated)
                {
                    (matched, matchReason, matchScore, ambiguous) = FindCuratedMatch(
                        identity, existing.Channels, threshold);
                }
                else
                {
                    // Unknown: exact equality or explicit alias only.
                    // The match must be deterministic and reproducible
                    // (no fuzzy scores); a stream with a near-miss name
                    // is NOT allowed to alter the streams of an
                    // unrelated existing channel that just happens to
                    // be similar in name. Furthermore, multiple exact
                    // or alias candidates are AMBIGUOUS — we refuse to
                    // pick one arbitrarily.
                    var unknown = FindUnknownMatch(identity, existing.Channels);
                    switch (unknown.State)
                    {
                        case UnknownMatchState.Unique:
                            matched = unknown.UniqueChannel;
                            matchReason = unknown.UniqueReason;
                            break;
                        case UnknownMatchState.Ambiguous:
                            // Record the bucket as review-required
                            // with the diagnostic of ambiguity. The
                            // matched slot is left null so the "no
                            // existing match" branch below does NOT
                            // re-record.
                            RecordUnknownAmbiguous(
                                bucket.Value, classificationCounts, dispositionCounts,
                                reviewRequired, "ambiguous-exact-or-alias-match");
                            skipNoMatchRecord = true;
                            matched = null;
                            matchReason = null;
                            break;
                        case UnknownMatchState.NoMatch:
                        default:
                            matched = null;
                            matchReason = null;
                            break;
                    }
                }

                if (ambiguous && matched != null)
                {
                    // Curated bucket with ambiguous existing match:
                    // preserved as Ambiguous decision (legacy surface);
                    // Unknown bucket cannot reach this path (the
                    // exact-only matcher is binary).
                    if (isCurated)
                    {
                        var top = matched;
                        var second = existing.Channels
                            .Where(c => c.Id != top.Id)
                            .OrderBy(c => _fuzzy.Score(
                                _aliases.Resolve(c.Name)?.Canonical ?? identity,
                                ChannelNormalizer.Normalize(c.Name)).Score)
                            .Last();
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
                            MatchReason = $"ambiguous:{top.Name}|{second.Name}",
                            MatchScore = matchScore,
                            Streams = streamDecisions,
                            AmbiguousCandidates = new[]
                            {
                                new AmbiguousCandidate
                                {
                                    ExistingChannelId = top.Id,
                                    ExistingChannelName = top.Name,
                                    Score = matchScore,
                                    Reason = "top-candidate",
                                },
                                new AmbiguousCandidate
                                {
                                    ExistingChannelId = second.Id,
                                    ExistingChannelName = second.Name,
                                    Score = matchScore,
                                    Reason = "second-candidate",
                                }
                            }
                        });
                        counts.Ambiguous = counts.Ambiguous + 1;
                    }
                    else
                    {
                        // Defensive: should be unreachable (Unknown
                        // matcher is binary), but if reached, treat
                        // as review-required.
                        RecordUnknownReviewRequired(
                            bucket.Value, classificationCounts, dispositionCounts,
                            reviewRequired, "ambiguous-existing-match");
                    }
                    continue;
                }

                if (matched != null)
                {
                    if (tier == BucketTier.Unknown)
                    {
                        dispositionCounts["unknownMatchedToExisting"] =
                            dispositionCounts["unknownMatchedToExisting"] + 1;
                    }
                    decisions.AddRange(new[] { BuildExistingDecision(
                        bucket.Value, matched, isCurated, outputGroup,
                        matchReason!, matchScore, ordering, existing.Streams,
                        existingStreamsById, streamsByChannel, ownershipByStreamId, counts) });
                    continue;
                }

                // No existing match.
                if (skipNoMatchRecord)
                {
                    // Diagnostic already recorded by FindUnknownMatch's
                    // Ambiguous branch. Continue to next bucket.
                    continue;
                }
                if (!isCurated)
                {
                    // Unknown bucket without exact/alias match →
                    // review-required. NEVER NewChannel.
                    var reasonSuffix = tier == BucketTier.Unknown
                        ? "no-exact-or-alias-match"
                        : "no-existing-match";
                    RecordUnknownReviewRequired(
                        bucket.Value, classificationCounts, dispositionCounts,
                        reviewRequired, reasonSuffix);
                    continue;
                }

                // Curated bucket without existing match → NewChannel
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

        /// <summary>
        /// Records an Unknown bucket whose exact/alias match produced
        /// 2+ candidates in existing channels. We never pick one
        /// arbitrarily — the bucket is held back for manual review
        /// with a diagnostic of ambiguity. The reason suffix
        /// <c>ambiguous-exact-or-alias-match</c> distinguishes this
        /// path from the plain "no-match" path.
        /// </summary>
        private static void RecordUnknownAmbiguous(
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

        /// <summary>
        /// Eligibility tier for the bucket stage. Curated streams
        /// (with <c>NewChannelEligibility=true</c>) use the full
        /// fuzzy + threshold matching against existing channels.
        /// Unknown streams (with only
        /// <c>ExistingMatchEligibility=true</c>) only match by
        /// equality of the normalized identity or by an explicit
        /// alias. This is the core safety property of the
        /// three-level policy.
        /// </summary>
        private enum BucketTier
        {
            Curated = 0,
            Unknown = 1,
        }

        /// <summary>
        /// Result of <see cref="FindUnknownMatch"/>: tri-state that
        /// the main loop uses to decide between
        /// <c>ExistingReassigned/ExistingUnchanged</c> and
        /// <c>UnknownReviewRequired</c>.
        /// <list type="bullet">
        ///   <item><see cref="UnknownMatchState.NoMatch"/>: 0 exact
        ///         or alias candidates in existing channels. Caller
        ///         routes to <c>UnknownReviewRequired</c> with
        ///         <c>no-exact-or-alias-match</c>.</item>
        ///   <item><see cref="UnknownMatchState.Unique"/>: exactly 1
        ///         candidate. Caller attaches the streams to that
        ///         channel.</item>
        ///   <item><see cref="UnknownMatchState.Ambiguous"/>: 2+
        ///         candidates. Caller routes to
        ///         <c>UnknownReviewRequired</c> with
        ///         <c>ambiguous-exact-or-alias-match</c> —
        ///         <b>never</b> chooses arbitrarily.</item>
        /// </list>
        /// </summary>
        private enum UnknownMatchState
        {
            NoMatch,
            Unique,
            Ambiguous,
        }

        /// <summary>
        /// Discriminated union of unknown-match outcomes. All
        /// candidate channels for the <see cref="UnknownMatchState.Ambiguous"/>
        /// state are exposed so the diagnostic can list them.
        /// </summary>
        private readonly record struct UnknownMatch(
            UnknownMatchState State,
            DispatcharrChannel UniqueChannel,
            string UniqueReason,
            IReadOnlyList<DispatcharrChannel> AmbiguousCandidates)
        {
            public static UnknownMatch NoMatch() =>
                new(UnknownMatchState.NoMatch, default!, string.Empty, Array.Empty<DispatcharrChannel>());
            public static UnknownMatch Unique(DispatcharrChannel channel, string reason) =>
                new(UnknownMatchState.Unique, channel, reason, Array.Empty<DispatcharrChannel>());
            public static UnknownMatch Ambiguous(IReadOnlyList<DispatcharrChannel> candidates) =>
                new(UnknownMatchState.Ambiguous, default!, string.Empty, candidates);
        }

        /// <summary>
        /// Curated matching path: builds a candidate list from
        /// existing channels using the alias-resolved identity,
        /// scores each candidate with the fuzzy matcher, and picks
        /// the top scoring channel whose score passes
        /// <paramref name="threshold"/>. Returns the matched channel
        /// + reason + score, or <c>null</c> when no candidate scores
        /// high enough. Ambiguity is reported separately
        /// (<paramref name="ambiguous"/> = true) when the top
        /// candidates are within 5 points of each other.
        /// </summary>
        private (DispatcharrChannel? Channel, string? Reason, int Score, bool Ambiguous)
            FindCuratedMatch(
                string identity,
                IReadOnlyList<DispatcharrChannel> existing,
                int threshold)
        {
            var candidates = existing
                .Select(c => (Channel: c, Normalized: ChannelNormalizer.Normalize(c.Name)))
                .Where(c => !string.IsNullOrWhiteSpace(c.Normalized))
                .Select(c =>
                {
                    var viaAlias = _aliases.Resolve(c.Channel.Name);
                    var queryForAlias = viaAlias?.Canonical ?? identity;
                    var ms = _fuzzy.Score(queryForAlias, c.Normalized);
                    var aliasMatch = viaAlias != null
                        && string.Equals(viaAlias.Canonical,
                            ChannelNormalizer.Normalize(c.Channel.Name),
                            StringComparison.Ordinal);
                    return new
                    {
                        Channel = c.Channel,
                        Score = ms.Score,
                        Reason = aliasMatch && viaAlias != null
                            ? $"alias:{viaAlias.Reason}"
                            : ms.Reason,
                    };
                })
                .OrderByDescending(c => c.Score)
                .Take(5)
                .ToList();

            if (candidates.Count == 0)
                return (null, null, 0, false);

            var top = candidates[0];
            var others = candidates.Skip(1).Select(c => c.Score).ToList();
            var band = _scorer.Classify(top.Score, others, threshold);

            return band switch
            {
                MatchBand.Matched => (top.Channel, top.Reason, top.Score, false),
                MatchBand.Exact => (top.Channel, top.Reason, top.Score, false),
                MatchBand.Ambiguous => (top.Channel, top.Reason, top.Score, true),
                _ => (null, null, 0, false),
            };
        }

        /// <summary>
        /// Unknown matching path: a stream is allowed to attach to an
        /// existing channel if and only if:
        /// <list type="bullet">
        ///   <item>the normalized identity equals exactly the
        ///         existing channel's normalized name; or</item>
        ///   <item>an explicit alias from
        ///         <see cref="AliasResolver.Resolve(string?)"/> maps
        ///         the identity (or the existing channel name) to a
        ///         canonical that equals the other side.</item>
        /// </list>
        ///
        /// <para>
        /// Fuzzy similarity is NOT used. A near-miss name like
        /// "Fox Sportz" must not attach to "Fox Sports" via score.
        /// </para>
        ///
        /// <para>
        /// <b>Ambiguity rule for Unknown</b>: the method collects
        /// ALL candidates (exact + alias) and returns a tri-state:
        /// <list type="bullet">
        ///   <item>0 candidates → <see cref="UnknownMatch.NoMatch"/>;
        ///         caller routes to <c>UnknownReviewRequired</c> with
        ///         <c>no-exact-or-alias-match</c>.</item>
        ///   <item>1 candidate → <see cref="UnknownMatch.Unique"/>,
        ///         attaches to that channel.</item>
        ///   <item>2+ candidates → <see cref="UnknownMatch.Ambiguous"/>;
        ///         caller routes to <c>UnknownReviewRequired</c>
        ///         with <c>ambiguous-exact-or-alias-match</c>.
        ///         <b>Never</b> chooses arbitrarily.</item>
        /// </list>
        /// </para>
        /// </summary>
        private UnknownMatch FindUnknownMatch(
            string identity,
            IReadOnlyList<DispatcharrChannel> existing)
        {
            var identityAlias = _aliases.Resolve(identity);
            var candidates = new List<(DispatcharrChannel Channel, string Reason)>(capacity: 0);

            foreach (var c in existing)
            {
                var cNameNormalized = ChannelNormalizer.Normalize(c.Name);
                if (string.IsNullOrWhiteSpace(cNameNormalized)) continue;

                // Path 1: exact equality of normalized identity.
                if (string.Equals(identity, cNameNormalized, StringComparison.Ordinal))
                {
                    candidates.Add((c, "exact-identity"));
                    continue;
                }

                // Path 2: explicit alias matches.
                var cAlias = _aliases.Resolve(c.Name);
                if (identityAlias != null && cAlias != null
                    && string.Equals(identityAlias.Canonical, cAlias.Canonical, StringComparison.Ordinal))
                {
                    candidates.Add((c, $"alias:{identityAlias.Reason}->{cAlias.Canonical}"));
                    continue;
                }
                if (identityAlias != null
                    && string.Equals(identityAlias.Canonical, cNameNormalized, StringComparison.Ordinal))
                {
                    candidates.Add((c, $"alias:{identityAlias.Reason}"));
                    continue;
                }
                if (cAlias != null
                    && string.Equals(identity, cAlias.Canonical, StringComparison.Ordinal))
                {
                    candidates.Add((c, $"alias:{cAlias.Reason}"));
                    continue;
                }
            }

            if (candidates.Count == 0)
            {
                return UnknownMatch.NoMatch();
            }
            if (candidates.Count == 1)
            {
                return UnknownMatch.Unique(candidates[0].Channel, candidates[0].Reason);
            }
            // 2+ candidates: ambiguity. Caller routes to
            // UnknownReviewRequired with diagnostic. We expose the
            // candidate channel ids (not the reason) — the diagnostic
            // suffix on the review-required entry records the
            // ambiguity ("ambiguous-exact-or-alias-match") and the
            // operator can resolve manually.
            return UnknownMatch.Ambiguous(candidates.Select(c => c.Channel).ToList());
        }

        /// <summary>
        /// Builds the <see cref="ChannelDecision"/> for a bucket
        /// that attached to an existing channel. The bucket may be
        /// either tier (curated or unknown) — the existing-channel
        /// matching already decided this is a safe attach. Computes
        /// the union of streams between bucket and existing channel,
        /// marks stale existing streams as Removed, and tags the
        /// outcome as <see cref="SyncOutcome.ExistingReassigned"/>
        /// when there is any change.
        ///
        /// <para>
        /// Streams whose <see cref="StreamOwnership"/> é
        /// <see cref="StreamOwnership.External"/> ou
        /// <see cref="StreamOwnership.Unknown"/> NUNCA recebem
        /// <see cref="SyncOutcome.Removed"/>. Apenas streams
        /// comprovadamente <see cref="StreamOwnership.CrawlerManaged"/>
        /// podem sair. Streams protegidas são reclassificadas para
        /// <see cref="SyncOutcome.ExistingUnchanged"/> com
        /// <c>OrderReason = "protected-by-ownership"</c> para
        /// reflectir a decisão no plano sem mudar a stream no
        /// Dispatcharr.
        /// </para>
        /// </summary>
        private ChannelDecision BuildExistingDecision(
            IReadOnlyList<DiscoveredStream> bucket,
            DispatcharrChannel matched,
            bool isCurated,
            OutputGroupKind? outputGroup,
            string matchReason,
            int matchScore,
            IStreamOrderingPolicy ordering,
            IReadOnlyList<DispatcharrStream> allExistingStreams,
            IReadOnlyDictionary<long, DispatcharrStream> existingStreamsById,
            IReadOnlyDictionary<long, HashSet<long>> streamsByChannel,
            IReadOnlyDictionary<long, StreamOwnership> ownershipByStreamId,
            SyncReportCounts counts)
        {
            var existingStreamIds = streamsByChannel.TryGetValue(matched.Id, out var sids)
                ? sids.ToHashSet()
                : new HashSet<long>();

            var keepExisting = new HashSet<long>();
            var ordered = ordering.Order(bucket);
            var streamDecisions = new List<StreamMatchDecision>();
            int order = 0;
            foreach (var (stream, reason) in ordered)
            {
                var existingForUrl = allExistingStreams.FirstOrDefault(es =>
                    string.Equals(es.Url, stream.Url, StringComparison.OrdinalIgnoreCase) && es.IsWorking);

                if (existingForUrl == null)
                {
                    var streamNormTitle = ChannelNormalizer.Normalize(stream.Title);
                    if (!string.IsNullOrWhiteSpace(streamNormTitle))
                    {
                        existingForUrl = allExistingStreams.FirstOrDefault(es =>
                            es.IsWorking
                            && string.Equals(ChannelNormalizer.Normalize(es.Name), streamNormTitle, StringComparison.Ordinal)
                            && !keepExisting.Contains(es.Id));
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
                if (existingStreamId.HasValue) keepExisting.Add(existingStreamId.Value);

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

            var staleIds = existingStreamIds.Except(keepExisting).ToList();
            foreach (var sid in staleIds)
            {
                if (existingStreamsById.TryGetValue(sid, out var es))
                {
                    // Ownership guard: streams External ou Unknown
                    // nunca podem ser removidas. Apenas streams
                    // comprovadamente CrawlerManaged recebem
                    // SyncOutcome.Removed. Streams protegidas são
                    // reclassificadas como ExistingUnchanged com
                    // OrderReason = "protected-by-ownership" para
                    // ficarem visíveis no relatório mas não serem
                    // emitidas em DELETE/PATCH. Sem catalog (legacy
                    // mode) o mapa está vazio e todas as streams
                    // caem no fallback CrawlerManaged. Com catalog,
                    // streams sem registo na BD são tratadas como
                    // Unknown (default seguro) — nunca removidas.
                    var ownership = ownershipByStreamId.TryGetValue(sid, out var own)
                        ? own
                        : (_catalog != null
                            ? StreamOwnership.Unknown
                            : StreamOwnership.CrawlerManaged);
                    if (ownership != StreamOwnership.CrawlerManaged)
                    {
                        streamDecisions.Add(new StreamMatchDecision
                        {
                            Provider = es.M3uAccountName ?? "(unknown)",
                            StreamUrl = es.Url,
                            StreamName = es.Name,
                            Outcome = SyncOutcome.ExistingUnchanged,
                            ExistingStreamId = sid,
                            ProposedOrder = -1,
                            OrderReason = "protected-by-ownership",
                            IsWorking = es.IsWorking,
                            GroupName = es.GroupName,
                        });
                        counts.ProtectedExternalStreams = counts.ProtectedExternalStreams + 1;
                        continue;
                    }
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

            counts.Matched = counts.Matched + 1;
            if (!streamDecisions.Any(d => d.Outcome == SyncOutcome.NewStream || d.Outcome == SyncOutcome.Removed))
            {
                counts.Unchanged = counts.Unchanged + 1;
            }

            return new ChannelDecision
            {
                Identity = matched.Name,
                CanonicalName = matched.Name,
                Outcome = streamDecisions.Any(d => d.Outcome == SyncOutcome.NewStream || d.Outcome == SyncOutcome.Removed)
                    ? SyncOutcome.ExistingReassigned
                    : SyncOutcome.ExistingUnchanged,
                ExistingChannelId = matched.Id,
                ChannelGroupName = matched.GroupName,
                OutputGroup = outputGroup,
                MatchReason = matchReason,
                MatchScore = matchScore,
                Streams = streamDecisions,
                AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
            };
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
