using m3uCrawler.Models;
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
            IDispatcharrM3UClient? m3u = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _outputDir = outputDir ?? "output";
            _aliases = aliases ?? AliasResolver.FromFile(config.AliasFile);
            _ordering = ordering ?? new StreamOrderingPolicy(config.ProviderPriority);
            _matcher = matcher ?? new ChannelMatcher(_aliases);

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

        private async Task ApplyAsync(MatchPlan plan, DispatcharrState existing, List<FailedReportEntry> failed, CancellationToken ct)
        {
            var groupByName = existing.Groups
                .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

            foreach (var channel in plan.Channels)
            {
                try
                {
                    if (channel.Outcome == SyncOutcome.Ambiguous || channel.Outcome == SyncOutcome.Skipped)
                        continue;

                    long? groupId = null;
                    if (!string.IsNullOrWhiteSpace(channel.ChannelGroupName))
                    {
                        if (!groupByName.TryGetValue(channel.ChannelGroupName!, out var existingId) && _config.AutoCreateGroups)
                        {
                            existingId = await _m3u.CreateGroupAsync(channel.ChannelGroupName!, ct);
                            groupByName[channel.ChannelGroupName!] = existingId;
                        }
                        if (groupByName.TryGetValue(channel.ChannelGroupName!, out var resolved))
                            groupId = resolved;
                    }

                    var orderedWorking = channel.Streams
                        .Where(s => s.Outcome != SyncOutcome.Skipped && s.IsWorking)
                        .OrderBy(s => s.ProposedOrder)
                        .ToList();

                    var newStreamIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    foreach (var s in orderedWorking.Where(s => s.Outcome == SyncOutcome.NewStream))
                    {
                        var newId = await _streams.CreateAsync(new NewStreamRequest
                        {
                            Name = s.StreamName,
                            Url = s.StreamUrl,
                            ChannelGroupId = groupId,
                            IsCustom = true,
                        }, ct);
                        newStreamIds[s.StreamUrl] = newId;
                    }

                    var existingId2s = orderedWorking
                        .Where(s => s.Outcome == SyncOutcome.ExistingUnchanged && s.ExistingStreamId.HasValue)
                        .Select(s => s.ExistingStreamId!.Value)
                        .ToList();

                    var allStreamIds = orderedWorking
                        .Select(s => s.ExistingStreamId ?? (newStreamIds.TryGetValue(s.StreamUrl, out var nid) ? nid : (long?)null))
                        .Where(id => id.HasValue)
                        .Select(id => id!.Value)
                        .ToList();

                    if (channel.Outcome == SyncOutcome.NewChannel)
                    {
                        await _channels.CreateAsync(new NewChannelRequest
                        {
                            Name = channel.CanonicalName,
                            ChannelGroupId = groupId,
                            Streams = allStreamIds,
                        }, ct);
                    }
                    else if (channel.ExistingChannelId.HasValue && allStreamIds.Count > 0)
                    {
                        var currentIds = await _channels.ListStreamIdsAsync(channel.ExistingChannelId.Value, ct);
                        if (!currentIds.SequenceEqual(allStreamIds))
                            await _channels.UpdateStreamsAsync(channel.ExistingChannelId.Value, allStreamIds, ct);
                    }

                    foreach (var removed in channel.Streams.Where(s => s.Outcome == SyncOutcome.Removed && s.ExistingStreamId.HasValue))
                    {
                        try { await _streams.DeleteAsync(removed.ExistingStreamId!.Value, ct); }
                        catch (DispatcharrException dex)
                        {
                            Console.WriteLine($"⚠️ Falha a remover stream {removed.ExistingStreamId}: {dex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Falha ao aplicar canal '{channel.CanonicalName}': {ex.Message}");
                    failed.Add(new FailedReportEntry
                    {
                        Identity = channel.Identity,
                        Reason = $"{ex.GetType().Name}: {ex.Message}",
                        ExistingChannelId = channel.ExistingChannelId,
                    });
                }
            }
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
                    FailedChannels = failedChannels,
                };
            }
        }
    }
}
