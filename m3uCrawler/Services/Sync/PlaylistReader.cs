using m3uCrawler.Models;

namespace m3uCrawler.Services.Sync
{
    public static class PlaylistReader
    {
        public static async Task<IReadOnlyList<DiscoveredStream>> ReadAsync(string playlistPath, string? defaultProvider = null, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(playlistPath))
                throw new ArgumentException("playlist path required", nameof(playlistPath));
            if (!File.Exists(playlistPath))
                throw new FileNotFoundException("playlist not found", playlistPath);

            var content = await File.ReadAllTextAsync(playlistPath, ct);
            return Parse(content, defaultProvider);
        }

        public static IReadOnlyList<DiscoveredStream> Parse(string content, string? defaultProvider)
        {
            var result = new List<DiscoveredStream>();
            if (string.IsNullOrWhiteSpace(content)) return result;

            string? pendingExtInf = null;
            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length == 0) continue;
                if (line.StartsWith("#EXTM3U", StringComparison.Ordinal)) continue;
                if (line.StartsWith("#PLAYLIST:", StringComparison.Ordinal)) continue;
                if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                {
                    pendingExtInf = line;
                    continue;
                }
                if (line.StartsWith("#")) continue;

                var url = line.Trim();
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;

                var title = ExtractTitle(pendingExtInf) ?? string.Empty;
                var group = ExtractAttribute(pendingExtInf, "group-title");
                var logo = ExtractAttribute(pendingExtInf, "tvg-logo");

                var stream = new M3uStream
                {
                    Url = url,
                    Title = title,
                    Group = group ?? string.Empty,
                    Logo = logo ?? string.Empty,
                    IsWorking = true,
                    OriginalExtInf = pendingExtInf ?? string.Empty,
                };

                var provider = defaultProvider ?? DeriveProvider(uri);

                result.Add(new DiscoveredStream(stream, provider, "playlist.m3u"));
                pendingExtInf = null;
            }

            return result;
        }

        private static string DeriveProvider(Uri uri) => uri.Host ?? "(unknown)";

        private static string? ExtractTitle(string? extInf)
        {
            if (string.IsNullOrWhiteSpace(extInf)) return null;
            var idx = extInf.LastIndexOf(',');
            if (idx < 0 || idx == extInf.Length - 1) return null;
            return extInf[(idx + 1)..].Trim();
        }

        private static string? ExtractAttribute(string? extInf, string name)
        {
            if (string.IsNullOrWhiteSpace(extInf)) return null;
            var pattern = $"{name}=\"";
            var i = extInf.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += pattern.Length;
            var j = extInf.IndexOf('"', i);
            if (j < 0) return null;
            return extInf[i..j];
        }
    }
}
