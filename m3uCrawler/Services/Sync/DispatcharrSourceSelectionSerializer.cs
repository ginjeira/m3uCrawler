using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using m3uCrawler.Models;
using m3uCrawler.Services;

namespace m3uCrawler.Services.Sync
{
    public static class DispatcharrSourceSelectionSerializer
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string Serialize(DispatcharrSourceSelection selection)
        {
            if (selection == null) throw new ArgumentNullException(nameof(selection));
            var sanitized = SanitizeForSerialization(selection);
            return JsonSerializer.Serialize(sanitized, Options);
        }

        public static async Task WriteAsync(DispatcharrSourceSelection selection, string path, CancellationToken ct = default)
        {
            var json = Serialize(selection);
            await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), ct);
        }

        public static DispatcharrSourceSelection? Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<DispatcharrSourceSelection>(json, Options);
        }

        public static async Task<DispatcharrSourceSelection?> ReadAsync(string path, CancellationToken ct = default)
        {
            if (!File.Exists(path)) return null;
            var json = await File.ReadAllTextAsync(path, ct);
            return Deserialize(json);
        }

        private static DispatcharrSourceSelection SanitizeForSerialization(DispatcharrSourceSelection selection)
        {
            var channels = selection.Channels.Select(c => new ChannelSourceSelection
            {
                CanonicalChannelKey = c.CanonicalChannelKey,
                CanonicalChannelId = c.CanonicalChannelId,
                PolicyScope = c.PolicyScope,
                CandidateCount = c.CandidateCount,
                RejectedCount = c.RejectedCount,
                Selected = c.Selected.Select(s => new SelectedStreamSelection
                {
                    StreamUrl = CredentialSanitizer.SanitizeUrl(s.StreamUrl),
                    Rank = s.Rank,
                    Provider = s.Provider,
                    Reason = s.Reason,
                    SourceId = s.SourceId,
                }).ToList(),
            }).ToList();

            return new DispatcharrSourceSelection
            {
                GeneratedAtUtc = selection.GeneratedAtUtc,
                Counts = selection.Counts,
                Channels = channels,
            };
        }
    }
}
