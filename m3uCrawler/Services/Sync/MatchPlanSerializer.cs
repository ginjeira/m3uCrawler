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
            return JsonSerializer.Serialize(report, Options);
        }

        public static async Task WriteReportAsync(SyncReport report, string path, CancellationToken ct = default)
        {
            var json = SerializeReport(report);
            await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), ct);
        }

        private static MatchPlan SanitizeForSerialization(MatchPlan plan)
        {
            var channels = plan.Channels.Select(c => new ChannelDecision
            {
                Identity = c.Identity,
                CanonicalName = c.CanonicalName,
                Outcome = c.Outcome,
                ExistingChannelId = c.ExistingChannelId,
                ProposedChannelNumber = c.ProposedChannelNumber,
                ChannelGroupName = c.ChannelGroupName,
                MatchReason = c.MatchReason,
                MatchScore = c.MatchScore,
                AmbiguousCandidates = c.AmbiguousCandidates,
                StreamsEmptied = c.StreamsEmptied,
                Streams = c.Streams.Select(s => new StreamMatchDecision
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
                }).ToList(),
            }).ToList();

            return new MatchPlan
            {
                GeneratedAtUtc = plan.GeneratedAtUtc,
                SourcePlaylistPath = plan.SourcePlaylistPath,
                DispatcharrBaseUrl = plan.DispatcharrBaseUrl,
                DryRun = plan.DryRun,
                MatchThreshold = plan.MatchThreshold,
                Counts = plan.Counts,
                Channels = channels,
                AmbiguousGroups = plan.AmbiguousGroups,
            };
        }
    }
}
