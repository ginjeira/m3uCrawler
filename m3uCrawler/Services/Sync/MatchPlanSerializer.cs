using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using m3uCrawler.Models;
using m3uCrawler.Services;

namespace m3uCrawler.Services.Sync
{
    public static class MatchPlanSerializer
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string Serialize(MatchPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            var sanitized = SanitizeForSerialization(plan);
            return JsonSerializer.Serialize(sanitized, Options);
        }

        public static async Task WriteAsync(MatchPlan plan, string path, CancellationToken ct = default)
        {
            var json = Serialize(plan);
            await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), ct);
        }

        public static MatchPlan? Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<MatchPlan>(json, Options);
        }

        public static async Task<MatchPlan?> ReadAsync(string path, CancellationToken ct = default)
        {
            if (!File.Exists(path)) return null;
            var json = await File.ReadAllTextAsync(path, ct);
            return Deserialize(json);
        }

        public static string SerializeReport(SyncReport report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            var sanitized = SanitizeForSerialization(report);
            return JsonSerializer.Serialize(sanitized, Options);
        }

        public static async Task WriteReportAsync(SyncReport report, string path, CancellationToken ct = default)
        {
            var json = SerializeReport(report);
            await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), ct);
        }

        private static MatchPlan SanitizeForSerialization(MatchPlan plan)
        {
            return new MatchPlan
            {
                GeneratedAtUtc = plan.GeneratedAtUtc,
                SourcePlaylistPath = plan.SourcePlaylistPath,
                DispatcharrBaseUrl = plan.DispatcharrBaseUrl,
                DryRun = plan.DryRun,
                MatchThreshold = plan.MatchThreshold,
                Counts = plan.Counts,
                Channels = plan.Channels.Select(SanitizeChannel).ToList(),
                AmbiguousGroups = plan.AmbiguousGroups,
                ClassifiedExclusions = plan.ClassifiedExclusions,
                UnknownReviewRequired = plan.UnknownReviewRequired,
            };
        }

        /// <summary>
        /// Produz uma cópia sanitizada do <see cref="SyncReport"/> antes da
        /// serialização, reutilizando exactamente a mesma projecção dos canais
        /// e streams do plano. É o único ponto de escrita de
        /// <c>dispatcharr_report_*.json</c>; garante que credenciais Xtream
        /// embutidas no path (ex.: <c>/live/user/pass/id</c>) nunca são
        /// persistidas. As contagens, estados, IDs e mensagens não sensíveis
        /// são preservados.
        /// </summary>
        private static SyncReport SanitizeForSerialization(SyncReport report)
        {
            var failedChannels = report.FailedChannels
                .Select(f => new FailedReportEntry
                {
                    Identity = f.Identity,
                    Reason = CredentialSanitizer.SanitizeText(f.Reason),
                    ExistingChannelId = f.ExistingChannelId,
                })
                .ToList();

            return new SyncReport
            {
                StartedAtUtc = report.StartedAtUtc,
                FinishedAtUtc = report.FinishedAtUtc,
                DryRun = report.DryRun,
                DispatcharrVersion = report.DispatcharrVersion,
                SourcePlaylistPath = report.SourcePlaylistPath,
                Counts = report.Counts,
                Channels = report.Channels.Select(SanitizeChannel).ToList(),
                AmbiguousDecisions = report.AmbiguousDecisions,
                AmbiguousGroups = report.AmbiguousGroups,
                FailedChannels = failedChannels,
            };
        }

        private static ChannelDecision SanitizeChannel(ChannelDecision c)
        {
            return new ChannelDecision
            {
                Identity = c.Identity,
                CanonicalName = c.CanonicalName,
                CanonicalChannelKey = c.CanonicalChannelKey,
                CanonicalChannelId = c.CanonicalChannelId,
                Outcome = c.Outcome,
                ExistingChannelId = c.ExistingChannelId,
                ProposedChannelNumber = c.ProposedChannelNumber,
                ChannelGroupName = c.ChannelGroupName,
                MatchReason = c.MatchReason,
                MatchScore = c.MatchScore,
                Streams = c.Streams.Select(SanitizeStream).ToList(),
                AmbiguousCandidates = c.AmbiguousCandidates,
                StreamsEmptied = c.StreamsEmptied,
                OutputGroup = c.OutputGroup,
            };
        }

        private static StreamMatchDecision SanitizeStream(StreamMatchDecision s)
        {
            return new StreamMatchDecision
            {
                Provider = s.Provider,
                StreamUrl = CredentialSanitizer.SanitizeUrl(s.StreamUrl),
                StreamName = s.StreamName,
                Outcome = s.Outcome,
                ExistingStreamId = s.ExistingStreamId,
                ProposedOrder = s.ProposedOrder,
                OrderReason = s.OrderReason,
                IsWorking = s.IsWorking,
                GroupName = s.GroupName,
            };
        }
    }
}
