using System.Text.RegularExpressions;
using m3uCrawler.Models;

namespace m3uCrawler.Services
{
    /// <summary>
    /// Deteta candidatos a playlists M3U/M3U8 a partir de uma mensagem do Telegram (texto, nome de
    /// anexo ou conteúdo do anexo). A deteção é feita exclusivamente por extensão/cabeçalho/conteúdo
    /// — NUNCA pela presença de uma keyword no texto ou no nome do ficheiro.
    /// </summary>
    public class M3uCandidateDetector
    {
        private static readonly Regex _m3uUrlRegex = new(
            @"https?://[^\s<>""'()]+\.m3u8?(?:\?[^\s<>""'()]*)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Captura qualquer URL http(s) para poder, em separado, aplicar a heurística de "plausível".
        private static readonly Regex _httpUrlRegex = new(
            @"https?://[^\s<>""'()]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Dicas de caminho/query que justificam inspecionar um URL sem extensão .m3u/.m3u8.
        private static readonly string[] _playlistHints =
        {
            "playlist", "m3u", "iptv", "list", "xtream", "channel", "canal", "live", "getplaylist"
        };

        // Xtream Codes: URL de servidor com credenciais no path (/live/USER/PASS/...).
        private static readonly Regex _xtreamServerRegex = new(
            @"https?://[^/\s]+/(live|movie|series)/([^/\s]+)/([^/\s]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Xtream Codes: endpoint de playlist (get.php).
        private static readonly Regex _xtreamPlaylistRegex = new(
            @"https?://[^/\s]+/get\.php(\?|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public bool IsM3uUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return Regex.IsMatch(url, @"\.m3u8?(?:\?[^<>""'\s]*)?$", RegexOptions.IgnoreCase);
        }

        public bool IsM3uFilename(string? filename)
        {
            if (string.IsNullOrWhiteSpace(filename)) return false;
            return Regex.IsMatch(filename, @"\.m3u8?$", RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Um URL http(s) SEM extensão .m3u/.m3u8 é considerado candidato a inspeccionar apenas
        /// quando há uma razão plausível (dica de playlist no caminho/query). Não classifica URLs
        /// HTTP arbitrários como playlist apenas por serem HTTP.
        /// </summary>
        public bool IsPlausiblePlaylistUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (IsM3uUrl(url)) return false;

            var lower = url.ToLowerInvariant();
            return _playlistHints.Any(hint => lower.Contains(hint));
        }

        /// <summary>
        /// Reconhece URLs Xtream Codes de servidor (ex.: http://host:port/live/USER/PASS/ID.ext).
        /// </summary>
        public bool IsXtreamServerUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            return _xtreamServerRegex.IsMatch(url);
        }

        /// <summary>
        /// Reconhece URLs de playlist Xtream Codes (ex.: http://host/get.php?...&amp;type=m3u_plus).
        /// </summary>
        public bool IsXtreamPlaylistUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            return _xtreamPlaylistRegex.IsMatch(url);
        }

        /// <summary>
        /// Resolve uma URL de servidor Xtream para a URL da playlist correspondente
        /// (get.php?username=...&amp;password=...&amp;type=m3u_plus). Devolve null se não for Xtream.
        /// </summary>
        public string? ResolveXtreamPlaylistUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var m = _xtreamServerRegex.Match(url);
            if (!m.Success) return null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

            var user = m.Groups[2].Value;
            var pass = m.Groups[3].Value;
            return $"{uri.Scheme}://{uri.Host}:{uri.Port}/get.php?username={Uri.EscapeDataString(user)}&password={Uri.EscapeDataString(pass)}&type=m3u_plus";
        }

        public bool LooksLikePlaylistContent(string? content)
        {
            if (string.IsNullOrWhiteSpace(content)) return false;
            var trimmed = content.TrimStart();
            return trimmed.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase);
        }

        public IReadOnlyList<CandidatePlaylist> DetectFromMessage(
            string? text, string? filename = null, string? attachmentContent = null)
        {
            var candidates = new List<CandidatePlaylist>();

            if (!string.IsNullOrWhiteSpace(text))
            {
                foreach (Match m in _httpUrlRegex.Matches(text))
                {
                    if (IsM3uUrl(m.Value))
                    {
                        candidates.Add(new CandidatePlaylist
                        {
                            Kind = CandidateSourceKind.Url,
                            Url = m.Value,
                            SourceText = text,
                            DetectedFrom = "m3u url"
                        });
                    }
                    else if (IsXtreamServerUrl(m.Value))
                    {
                        // URL de servidor Xtream (/live/USER/PASS/...): resolvemos para a playlist
                        // (get.php?...) para que o pipeline faça apenas UM download válido.
                        var playlist = ResolveXtreamPlaylistUrl(m.Value);
                        if (playlist != null)
                        {
                            candidates.Add(new CandidatePlaylist
                            {
                                Kind = CandidateSourceKind.Url,
                                Url = playlist,
                                SourceText = text,
                                DetectedFrom = "xtream server",
                                RequiresContentVerification = true
                            });
                        }
                    }
                    else if (IsXtreamPlaylistUrl(m.Value))
                    {
                        candidates.Add(new CandidatePlaylist
                        {
                            Kind = CandidateSourceKind.Url,
                            Url = m.Value,
                            SourceText = text,
                            DetectedFrom = "xtream playlist",
                            RequiresContentVerification = true
                        });
                    }
                    else if (IsPlausiblePlaylistUrl(m.Value))
                    {
                        candidates.Add(new CandidatePlaylist
                        {
                            Kind = CandidateSourceKind.Url,
                            Url = m.Value,
                            SourceText = text,
                            DetectedFrom = "url (inspect)",
                            RequiresContentVerification = true
                        });
                    }
                }
            }

            if (IsM3uFilename(filename))
            {
                candidates.Add(new CandidatePlaylist
                {
                    Kind = CandidateSourceKind.Attachment,
                    FileName = filename,
                    SourceText = text,
                    Content = attachmentContent,
                    DetectedFrom = "attachment filename"
                });
            }

            // Mesmo sem nome de ficheiro .m3u, o conteúdo que começa por #EXTM3U é um candidato.
            if (!string.IsNullOrWhiteSpace(attachmentContent) && LooksLikePlaylistContent(attachmentContent))
            {
                bool alreadyAdded = candidates.Any(c =>
                    c.Kind == CandidateSourceKind.Attachment &&
                    string.Equals(c.FileName, filename, StringComparison.OrdinalIgnoreCase));

                if (!alreadyAdded)
                {
                    candidates.Add(new CandidatePlaylist
                    {
                        Kind = CandidateSourceKind.Attachment,
                        FileName = filename,
                        SourceText = text,
                        Content = attachmentContent,
                        DetectedFrom = "#EXTM3U content"
                    });
                }
            }

            return candidates;
        }
    }
}
