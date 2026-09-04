using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Dispatcharr;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;

namespace m3uCrawler.Services.Sync
{
    public sealed class DispatcharrSyncResult
    {
        public MatchPlan Plan { get; init; } = new();
        public SyncReport Report { get; init; } = new();
        public string? PlanPath { get; init; }
        public string? ReportPath { get; init; }
        public bool DryRun { get; init; }
    }

    public interface IDispatcharrSyncService
    {
        Task<DispatcharrSyncResult> RunAsync(string playlistPath, CancellationToken ct = default);
    }

    public sealed class DispatcharrSyncService : IDispatcharrSyncService
    {
        private readonly DispatcharrConfig _config;
        private readonly IChannelMatcher _matcher;
        private readonly IStreamOrderingPolicy _ordering;
        private readonly IDispatcharrChannelClient _channels;
        private readonly IDispatcharrStreamClient _streams;
        private readonly IDispatcharrM3UClient _m3u;
        private readonly HttpClient _http;
        private readonly DispatcharrAuthState _auth;
        private readonly DispatcharrLoginApi _login;
        private readonly AliasResolver _aliases;
        private readonly CatalogResolver? _catalog;
        private readonly string _outputDir;

        public DispatcharrSyncService(
            DispatcharrConfig config,
            string outputDir,
            AliasResolver? aliases = null,
            IStreamOrderingPolicy? ordering = null,
            IChannelMatcher? matcher = null,
            HttpClient? http = null,
            DispatcharrAuthState? auth = null,
            DispatcharrLoginApi? login = null,
            IDispatcharrChannelClient? channels = null,
            IDispatcharrStreamClient? streams = null,
            IDispatcharrM3UClient? m3u = null,
            CatalogResolver? catalog = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _outputDir = outputDir ?? "output";
            _aliases = aliases ?? AliasResolver.FromFile(config.AliasFile);
            _ordering = ordering ?? new StreamOrderingPolicy(config.ProviderPriority);
            _matcher = matcher ?? new ChannelMatcher(_aliases);
            _catalog = catalog;

            if (config.Enabled)
            {
                if (http != null && auth != null && login != null && channels != null && streams != null && m3u != null)
                {
                    _http = http;
                    _auth = auth;
                    _login = login;
                    _channels = channels;
                    _streams = streams;
                    _m3u = m3u;
                }
                else
                {
                    var built = DispatcharrClientFactory.Build(config.BaseUrl, config.ApiKey, config.Username, config.Password);
                    _http = http ?? built.Http;
                    _auth = auth ?? built.Auth;
                    _login = login ?? built.Login;
                    _channels = channels ?? built.Channels;
                    _streams = streams ?? built.Streams;
                    _m3u = m3u ?? built.M3U;
                }
            }
            else
            {
                _http = http ?? new HttpClient();
                _auth = auth ?? new DispatcharrAuthState();
                _login = login ?? new DispatcharrLoginApi(_http);
                _channels = channels ?? new DispatcharrChannelClient(_http);
                _streams = streams ?? new DispatcharrStreamClient(_http);
                _m3u = m3u ?? new DispatcharrM3UClient(_http);
            }
        }

        public async Task<DispatcharrSyncResult> RunAsync(string playlistPath, CancellationToken ct = default)
        {
            if (!_config.Enabled)
                return new DispatcharrSyncResult { DryRun = true };

            Directory.CreateDirectory(_outputDir);

            var startedAt = DateTime.UtcNow;
            Console.WriteLine("🛰️  A iniciar sincronização com Dispatcharr...");

            var discovered = await PlaylistReader.ReadAsync(playlistPath, ct: ct);
            Console.WriteLine($"📥 Streams extraídos da playlist: {discovered.Count}");

            var existing = await FetchStateAsync(ct);
            if (existing.Version != null)
                Console.WriteLine($"🧾 Dispatcharr versão detetada: {existing.Version}");

            var options = new MatchingOptions
            {
                MatchThreshold = _config.MatchThreshold,
                Aliases = AliasMapFor(_aliases),
            };

            var plan = _matcher.BuildPlan(
                discovered,
                existing,
                options,
                _ordering,
                playlistPath,
                _config.BaseUrl,
                _config.DryRun);

            var planPath = Path.Combine(_outputDir, $"dispatcharr_plan_{startedAt:yyyyMMdd_HHmmss}.json");
            await MatchPlanSerializer.WriteAsync(plan, planPath, ct);

            var failed = new List<FailedReportEntry>();
            var reportBuilder = new ReportBuilder(plan, existing.Version, playlistPath, startedAt);
            var preReport = reportBuilder.Build();

            if (_config.DryRun)
            {
                Console.WriteLine("🟡 Dry-run activo: a aplicar seria um no-op. Plano + relatório gerados sem chamadas HTTP de escrita.");
            }
            else
            {
                await ApplyAsync(plan, existing, failed, ct);
            }

            preReport.Counts.Failed = failed.Count;
            var finalReport = reportBuilder.Finish(DateTime.UtcNow, preReport, failed);
            var reportPath = Path.Combine(_outputDir, $"dispatcharr_report_{startedAt:yyyyMMdd_HHmmss}.json");
            await MatchPlanSerializer.WriteReportAsync(finalReport, reportPath, ct);

            Console.WriteLine();
            Console.WriteLine("✅ Sincronização concluída.");
            Console.WriteLine($"   • Matched:         {finalReport.Counts.Matched}");
            Console.WriteLine($"   • New channels:    {finalReport.Counts.NewChannels}");
            Console.WriteLine($"   • New streams:     {finalReport.Counts.NewStreams}");
            Console.WriteLine($"   • Removed streams: {finalReport.Counts.RemovedStreams}");
            Console.WriteLine($"   • Skipped:         {finalReport.Counts.Skipped}");
            Console.WriteLine($"   • Ambiguous:       {finalReport.Counts.Ambiguous}");
            Console.WriteLine($"   • Unchanged:       {finalReport.Counts.Unchanged}");
            Console.WriteLine($"   • Failed:          {finalReport.Counts.Failed}");
            Console.WriteLine($"   • Plano:           {planPath}");
            Console.WriteLine($"   • Relatório:       {reportPath}");

            return new DispatcharrSyncResult
            {
                Plan = plan,
                Report = finalReport,
                PlanPath = planPath,
                ReportPath = reportPath,
                DryRun = _config.DryRun,
            };
        }

        private async Task<DispatcharrState> FetchStateAsync(CancellationToken ct)
        {
            var version = await _m3u.GetVersionAsync(ct);
            var channels = await _channels.ListAsync(ct);
            var streams = await _streams.ListAsync(ct);
            var groups = await _m3u.ListGroupsAsync(ct);
            return new DispatcharrState(channels, streams, groups, version);
        }

        internal async Task ApplyAsync(MatchPlan plan, DispatcharrState existing, List<FailedReportEntry> failed, CancellationToken ct)
        {
            var ambiguousGroupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in existing.Groups)
            {
                var duplicates = existing.Groups
                    .Where(x => !string.IsNullOrWhiteSpace(x.Name) &&
                                string.Equals(x.Name, g.Name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (duplicates.Count > 1)
                    ambiguousGroupNames.Add(g.Name);
            }

            var groupByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in existing.Groups)
            {
                if (ambiguousGroupNames.Contains(g.Name)) continue;
                groupByName[g.Name] = g.Id;
            }

            // Cross-channel stream ownership:
            //
            // Dispatcharr's data model is M2M (Channel.streams ↔ Stream), but DELETE on a
            // Stream is GLOBAL (cascades to all ChannelStream rows referencing it). Phase 4
            // must therefore run ONLY after every channel's Phase 2 + Phase 3 has completed
            // — otherwise a DELETE from one channel can destroy a stream still needed by
            // another channel whose PATCH hasn't been issued yet, producing
            // "Invalid pk" HTTP 400 races.
            //
            // We collect:
            //   globalKeepStreamIds   — stream IDs the matcher intends to keep across all
            //                            channels. Built incrementally as Phase 2/3 progress.
            //   globalRemoveCandidates — stream IDs the matcher intends to remove. Excluded
            //                            from the final DELETE set are those still in globalKeep
            //                            (cross-channel sharing) and those whose channel's
            //                            Phase 3 failed (preserve intent when uncertain).
            var globalKeepStreamIds = new HashSet<long>();
            var globalRemoveCandidates = new HashSet<long>();

            foreach (var channel in plan.Channels)
            {
                if (channel.Outcome == SyncOutcome.Ambiguous || channel.Outcome == SyncOutcome.Skipped)
                    continue;

                var ctx = await BeginChannelApplyAsync(channel, existing, groupByName, ct);

                // Record stream IDs the matcher intended to keep on this channel BEFORE
                // any DELETE happens. NewStreamIds (Phase 2) are physical creations that
                // must survive. AllStreamIds (Phase 3 body) is the deduplicated union of
                // kept-existing + new ids that this channel will reference.
                foreach (var id in ctx.NewStreamIds.Values) globalKeepStreamIds.Add(id);
                foreach (var id in ctx.AllStreamIds) globalKeepStreamIds.Add(id);

                // Record this channel's Removed candidates. We deduplicate globally and
                // exclude them from the actual DELETE set if:
                //   (a) another channel still needs the stream (globalKeepStreamIds contains it), or
                //   (b) this channel's Phase 3 failed (we trust the matcher intent but cannot
                //       confirm the channel state — preserving the stream is safer).
                if (!ctx.PatchFailed)
                {
                    foreach (var s in channel.Streams)
                    {
                        if (s.Outcome == SyncOutcome.Removed && s.ExistingStreamId.HasValue)
                            globalRemoveCandidates.Add(s.ExistingStreamId!.Value);
                    }
                }
                else
                {
                    failed.Add(new FailedReportEntry
                    {
                        Identity = channel.Identity,
                        Reason = $"{ctx.PatchException?.GetType().Name}: {ctx.PatchException?.Message}",
                        ExistingChannelId = channel.ExistingChannelId,
                    });
                    Console.WriteLine($"❌ Falha ao aplicar canal '{channel.CanonicalName}': {ctx.PatchException?.Message}");
                }
            }

            // Phase 4 (global): DELETE only streams that no channel in the plan keeps.
            // Distinct() guards against repeated references to the same id.
            //
            // Ownership guard (defesa redundante): o filtro principal
            // vive em ChannelMatcher.BuildExistingDecision. Esta
            // barreira em ApplyAsync é uma salvaguarda adicional:
            // mesmo que um plano inválido contenha um Removed para
            // uma stream com Ownership External/Unknown, NUNCA
            // emitimos DELETE. Apenas streams comprovadamente
            // CrawlerManaged são removidas. Sem catalog, o filtro
            // não se aplica (legacy mode).
            IReadOnlyDictionary<long, StreamOwnership> ownershipMap =
                new Dictionary<long, StreamOwnership>();
            if (_catalog != null && globalRemoveCandidates.Count > 0)
            {
                ownershipMap = await _catalog.GetStreamOwnershipMapAsync(
                    globalRemoveCandidates.ToList(), ct);
            }
            foreach (var streamId in globalRemoveCandidates
                .Where(id => !globalKeepStreamIds.Contains(id))
                .Distinct())
            {
                // Streams sem registo de ownership caem no
                // default seguro (Unknown) APENAS quando o catalog
                // está activo. Sem catalog (legacy mode) o mapa
                // está vazio e todas as streams caem no fallback
                // CrawlerManaged (mesmo comportamento histórico).
                var ownership = _catalog != null
                    ? (ownershipMap.TryGetValue(streamId, out var own)
                        ? own
                        : StreamOwnership.Unknown)
                    : StreamOwnership.CrawlerManaged;
                if (ownership != StreamOwnership.CrawlerManaged)
                {
                    // Salvaguarda: stream com ownership protegida
                    // (External, Unknown, ou sem registo) nunca
                    // recebe DELETE. Log informativo apenas; não
                    // conta como failed (não é erro de aplicação,
                    // é defesa intencional).
                    Console.WriteLine($"🛡️ Ownership guard: stream {streamId} ({ownership}) mantida apesar de plano a marcar como Removed.");
                    continue;
                }
                try
                {
                    await _streams.DeleteAsync(streamId, ct);
                }
                catch (DispatcharrException dex)
                {
                    Console.WriteLine($"⚠️ Falha a remover stream {streamId}: {dex.Message}");
                }
            }
        }

        internal sealed class ChannelApplyContext
        {
            public Dictionary<string, long> GroupByName { get; init; } = new();
            public Dictionary<string, long> NewStreamIds { get; } = new(StringComparer.OrdinalIgnoreCase);
            public IReadOnlyList<long> AllStreamIds { get; set; } = Array.Empty<long>();
            public bool PatchFailed { get; set; }
            public Exception? PatchException { get; set; }
        }

        internal async Task<ChannelApplyContext> BeginChannelApplyAsync(
            ChannelDecision channel,
            DispatcharrState existing,
            Dictionary<string, long> groupByName,
            CancellationToken ct)
        {
            var ctx = new ChannelApplyContext { GroupByName = groupByName };

            // Phase 1: resolve group (no HTTP yet).
            long? groupId = null;
            if (!string.IsNullOrWhiteSpace(channel.ChannelGroupName))
            {
                if (!groupByName.TryGetValue(channel.ChannelGroupName!, out var existingId) && _config.AutoCreateGroups)
                {
                    try
                    {
                        existingId = await _m3u.CreateGroupAsync(channel.ChannelGroupName!, ct);
                        groupByName[channel.ChannelGroupName!] = existingId;
                    }
                    catch (DispatcharrException ex)
                    {
                        ctx.PatchFailed = true;
                        ctx.PatchException = ex;
                        Console.WriteLine($"❌ Falha ao criar grupo '{channel.ChannelGroupName}': {ex.Message}");
                        return ctx;
                    }
                }
                if (groupByName.TryGetValue(channel.ChannelGroupName!, out var resolved))
                    groupId = resolved;
            }

            var orderedWorking = channel.Streams
                .Where(s => s.Outcome != SyncOutcome.Skipped
                         && s.Outcome != SyncOutcome.Removed
                         && s.IsWorking)
                .OrderBy(s => s.ProposedOrder)
                .ToList();

            // Phase 2: POST every NewStream up front.
            foreach (var s in orderedWorking.Where(s => s.Outcome == SyncOutcome.NewStream))
            {
                try
                {
                    var newId = await _streams.CreateAsync(new NewStreamRequest
                    {
                        Name = s.StreamName,
                        Url = s.StreamUrl,
                        ChannelGroupId = groupId,
                        IsCustom = true,
                    }, ct);
                    ctx.NewStreamIds[s.StreamUrl] = newId;
                }
                catch (DispatcharrException ex)
                {
                    ctx.PatchFailed = true;
                    ctx.PatchException = ex;
                    Console.WriteLine($"❌ Falha ao criar stream '{s.StreamUrl}': {ex.Message}");
                    return ctx;
                }
            }

            ctx.AllStreamIds = orderedWorking
                .Select(s => s.ExistingStreamId ?? (ctx.NewStreamIds.TryGetValue(s.StreamUrl, out var nid) ? nid : (long?)null))
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();

            // Phase 3: create new channel OR patch existing channel.
            //
            // Contract:
            //   A) AllStreamIds.Count > 0          -> PATCH with the deduplicated list.
            //   B) AllStreamIds.Count == 0 + StreamsEmptied  -> PATCH with streams=[].
            //   C) AllStreamIds.Count == 0 + !StreamsEmptied -> no PATCH, no DELETE-stream.
            try
            {
                if (channel.Outcome == SyncOutcome.NewChannel)
                {
                    await _channels.CreateAsync(new NewChannelRequest
                    {
                        Name = channel.CanonicalName,
                        ChannelGroupId = groupId,
                        Streams = ctx.AllStreamIds.ToList(),
                    }, ct);
                }
                else if (channel.ExistingChannelId.HasValue && ctx.AllStreamIds.Count > 0)
                {
                    var currentIds = await _channels.ListStreamIdsAsync(channel.ExistingChannelId.Value, ct);
                    if (!currentIds.SequenceEqual(ctx.AllStreamIds))
                    {
                        await _channels.UpdateStreamsAsync(channel.ExistingChannelId.Value, ctx.AllStreamIds.ToList(), ct);
                    }
                }
                else if (channel.ExistingChannelId.HasValue && channel.StreamsEmptied)
                {
                    // Scenario B: channel should be left with streams=[] on Dispatcharr.
                    // PATCH unconditionally — even if currentIds is already empty, the explicit
                    // PATCH documents the operator intent in the plan and is idempotent.
                    await _channels.UpdateStreamsAsync(channel.ExistingChannelId.Value, Array.Empty<long>(), ct);
                }
                // else (scenario C): no PATCH, channel left untouched.
            }
            catch (Exception ex)
            {
                ctx.PatchFailed = true;
                ctx.PatchException = ex;
            }

            return ctx;
        }

        internal async Task CompleteChannelApplyAsync(
            ChannelDecision channel,
            ChannelApplyContext ctx,
            List<FailedReportEntry> failed,
            CancellationToken ct)
        {
            // Phase 4 (DELETE streams) was historically executed here on a per-channel
            // basis, but DELETE on a Dispatcharr Stream is GLOBAL (it cascades to every
            // ChannelStream referencing it). Doing that inside the channel loop races with
            // PATCHes from other channels that still need the same stream. Phase 4 now
            // runs ONCE at the end of ApplyAsync, in a single global pass that consults
            // globalKeepStreamIds. This method is intentionally a no-op retained for the
            // test seam so existing call-sites compile.
            _ = channel;
            _ = ctx;
            _ = failed;
            _ = ct;
            await Task.CompletedTask;
        }

        private static IReadOnlyDictionary<string, string> AliasMapFor(AliasResolver resolver)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return dict;
        }

        private sealed class ReportBuilder
        {
            private readonly MatchPlan _plan;
            private readonly string? _version;
            private readonly string _playlist;
            private readonly DateTime _startedAt;

            public ReportBuilder(MatchPlan plan, string? version, string playlist, DateTime startedAt)
            {
                _plan = plan;
                _version = version;
                _playlist = playlist;
                _startedAt = startedAt;
            }

            public SyncReport Build()
            {
                var ambiguous = _plan.Channels
                    .Where(c => c.Outcome == SyncOutcome.Ambiguous)
                    .Select(c => new AmbiguousReportEntry
                    {
                        Identity = c.Identity,
                        Candidates = c.AmbiguousCandidates,
                    })
                    .ToList();

                return new SyncReport
                {
                    StartedAtUtc = _startedAt.ToString("o"),
                    FinishedAtUtc = string.Empty,
                    DryRun = _plan.DryRun,
                    DispatcharrVersion = _version,
                    SourcePlaylistPath = _playlist,
                    Counts = _plan.Counts,
                    Channels = _plan.Channels,
                    AmbiguousDecisions = ambiguous,
                    AmbiguousGroups = _plan.AmbiguousGroups,
                    FailedChannels = Array.Empty<FailedReportEntry>(),
                };
            }

            public SyncReport Finish(DateTime finishedAt, SyncReport partial, IReadOnlyList<FailedReportEntry> failedChannels)
            {
                return new SyncReport
                {
                    StartedAtUtc = partial.StartedAtUtc,
                    FinishedAtUtc = finishedAt.ToString("o"),
                    DryRun = partial.DryRun,
                    DispatcharrVersion = partial.DispatcharrVersion,
                    SourcePlaylistPath = partial.SourcePlaylistPath,
                    Counts = partial.Counts,
                    Channels = partial.Channels,
                    AmbiguousDecisions = partial.AmbiguousDecisions,
                    AmbiguousGroups = partial.AmbiguousGroups,
                    FailedChannels = failedChannels,
                };
            }
        }
    }
}
