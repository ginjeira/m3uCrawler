using System.Text.RegularExpressions;
using m3uCrawler.Models;

namespace m3uCrawler.Services
{
    /// <summary>
    /// Faz o parsing de conteúdo M3U/M3U8 para uma lista de M3uStream, preservando os metadados
    /// do EXTINF (título, grupo, logo) e a linha EXTINF original em OriginalExtInf. Não assume que
    /// qualquer .m3u8 seja uma playlist de canais: variantes HLS (#EXT-X-STREAM-INF) são tratadas
    /// como metadados, não como entradas de canal.
    /// </summary>
    public class M3uParserService
    {
        public List<M3uStream> Parse(string? content)
        {
            var streams = new List<M3uStream>();
            if (string.IsNullOrWhiteSpace(content)) return streams;

            string? pendingExtInf = null;
            string? pendingStreamInf = null;

            var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
                {
                    pendingExtInf = line;
                    continue;
                }

                if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                {
                    pendingStreamInf = line;
                    continue;
                }

                if (line.StartsWith("#"))
                {
                    pendingExtInf = null;
                    pendingStreamInf = null;
                    continue;
                }

                if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    var stream = new M3uStream
                    {
                        Url = line,
                        OriginalExtInf = pendingExtInf ?? string.Empty
                    };

                    if (!string.IsNullOrWhiteSpace(pendingExtInf))
                    {
                        stream.Title = ExtractTitle(pendingExtInf);
                        stream.Group = ExtractAttribute(pendingExtInf, "group-title");
                        stream.Logo = ExtractAttribute(pendingExtInf, "tvg-logo");
                    }
                    else if (!string.IsNullOrWhiteSpace(pendingStreamInf))
                    {
                        stream.Title = ExtractAttribute(pendingStreamInf, "tvg-name");
                        stream.Group = ExtractAttribute(pendingStreamInf, "group-title");
                        stream.Logo = ExtractAttribute(pendingStreamInf, "tvg-logo");
                    }

                    if (string.IsNullOrWhiteSpace(stream.Title))
                    {
                        stream.Title = ExtractFileName(line);
                    }

                    streams.Add(stream);
                    pendingExtInf = null;
                    pendingStreamInf = null;
                }
            }

            return streams;
        }

        private static string ExtractTitle(string extInf)
        {
            int comma = extInf.LastIndexOf(',');
            if (comma >= 0 && comma < extInf.Length - 1)
            {
                return extInf[(comma + 1)..].Trim();
            }

            return string.Empty;
        }

        private static string ExtractAttribute(string line, string attr)
        {
            var match = Regex.Match(line, $"{attr}=\"([^\"]*)\"", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static string ExtractFileName(string url)
        {
            try
            {
                var uri = new Uri(url);
                var name = Path.GetFileNameWithoutExtension(uri.LocalPath);
                return string.IsNullOrEmpty(name) ? url : name;
            }
            catch
            {
                return url;
            }
        }
    }
}
