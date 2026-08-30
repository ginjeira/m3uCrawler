using System.Text.RegularExpressions;
using m3uCrawler.Models;

namespace m3uCrawler.Services.SourceOrdering
{
    public interface IStreamOrderingPolicy
    {
        IReadOnlyList<(DiscoveredStream Stream, string Reason)> Order(IReadOnlyList<DiscoveredStream> streams);
    }

    public sealed class StreamOrderingPolicy : IStreamOrderingPolicy
    {
        private readonly IReadOnlyDictionary<string, int> _providerRank;
        private static readonly Regex QualityPattern = new(@"\b(4K|UHD|FHD|HD|SD|HDR|HEVC)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly IReadOnlyDictionary<string, int> QualityRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["4K"] = 0,
            ["UHD"] = 0,
            ["FHD"] = 1,
            ["HD"] = 2,
            ["SD"] = 3,
        };

        public StreamOrderingPolicy(IEnumerable<string>? providerPriority = null)
        {
            var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int rank = 0;
            if (providerPriority != null)
            {
                foreach (var p in providerPriority)
                {
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    if (!ranks.ContainsKey(p.Trim())) ranks[p.Trim()] = rank++;
                }
            }
            _providerRank = ranks;
        }

        public IReadOnlyList<(DiscoveredStream Stream, string Reason)> Order(IReadOnlyList<DiscoveredStream> streams)
        {
            if (streams == null) throw new ArgumentNullException(nameof(streams));
            int index = 0;
            var working = streams
                .Where(s => s.IsWorking)
                .Select(s => (Stream: s, Index: index++, Quality: DetectQuality(s.Title)))
                .ToList();

            var ordered = working
                .OrderBy(t => t.Quality.Rank)
                .ThenBy(t => ProviderRankOf(t.Stream.Provider))
                .ThenBy(t => t.Stream.ResponseTime)
                .ThenBy(t => t.Index)
                .ToList();

            var result = new List<(DiscoveredStream, string)>(ordered.Count);
            int order = 0;
            foreach (var t in ordered)
            {
                var reasonParts = new List<string>();
                if (_providerRank.ContainsKey(t.Stream.Provider))
                    reasonParts.Add($"provider-rank={_providerRank[t.Stream.Provider]}");
                if (t.Quality.Rank < 4)
                    reasonParts.Add($"quality={t.Quality.Token}");
                else
                    reasonParts.Add("quality=none");
                reasonParts.Add($"rt={t.Stream.ResponseTime:F0}ms");
                result.Add((t.Stream, string.Join(';', reasonParts)));
                order++;
            }
            return result;
        }

        private int ProviderRankOf(string provider)
        {
            if (string.IsNullOrWhiteSpace(provider)) return int.MaxValue;
            return _providerRank.TryGetValue(provider, out var r) ? r : int.MaxValue - 1;
        }

        private static (string Token, int Rank) DetectQuality(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return (string.Empty, 4);
            var match = QualityPattern.Match(title);
            if (!match.Success) return (string.Empty, 4);
            var token = match.Value.ToUpperInvariant();
            if (QualityRank.TryGetValue(token, out var rank)) return (token, rank);
            return (token, 4);
        }
    }
}
