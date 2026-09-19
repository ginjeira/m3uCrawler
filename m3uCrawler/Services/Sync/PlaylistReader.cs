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

        /// <summary>
        /// Converte conteúdo M3U em streams descobertas. Delega no contrato
        /// único de parsing (<see cref="M3uParserService"/>): cabeçalho
        /// <c>#EXTM3U</c> obrigatório, entrada <c>#EXTINF</c>+URL absoluto
        /// http/https, metadados benignos ignorados e alvos com esquema não
        /// http/https excluídos. Uma playlist Failed não produz streams.
        /// </summary>
        public static IReadOnlyList<DiscoveredStream> Parse(string content, string? defaultProvider)
        {
            var result = new List<DiscoveredStream>();
            if (string.IsNullOrWhiteSpace(content)) return result;

            var parsed = new M3uParserService().ParseDetailed(content);
            if (parsed.Status == M3uPlaylistStatus.Failed) return result;

            foreach (var stream in parsed.Streams)
            {
                stream.IsWorking = true;
                var provider = defaultProvider ?? DeriveProvider(stream.Url);
                result.Add(new DiscoveredStream(stream, provider, "playlist.m3u"));
            }

            return result;
        }

        private static string DeriveProvider(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            {
                return uri.Host;
            }

            return "(unknown)";
        }
    }
}
