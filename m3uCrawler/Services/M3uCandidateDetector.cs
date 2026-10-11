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

        // Stalker Portal: endereço MAC de cartão (ex.: 00:1A:79:85:CC:47). O `\b`
        // reduz falsos positivos dentro de sequências hexadecimais mais longas.
        private static readonly Regex _macAddressRegex = new(
            @"\b(?:[0-9a-f]{2}:){5}[0-9a-f]{2}\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Stalker Portal: indicadores de cartão em texto (MAC, play_token=,
        // stb_lang, type=stb e o par Serial + Device_Id).
        private static readonly string[] _stalkerCardHints =
        {
            "play_token=", "stb_lang", "type=stb"
        };

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
        /// Reconhece filenames terminados em .html ou .htm (case-insensitive).
        /// Usado para marcar anexos HTML como candidatos a inspeccao: o conteudo
        /// do ficheiro e' descarregado por ProcessAttachmentCandidatesAsync e
        /// entregue ao XtreamPublicationResolver. O detector NAO faz parsing
        /// HTML — apenas identifica o filename como candidato.
        /// </summary>
        public bool IsHtmlFilename(string? filename)
        {
            if (string.IsNullOrWhiteSpace(filename)) return false;
            return Regex.IsMatch(filename, @"\.html?$", RegexOptions.IgnoreCase);
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
        /// Reconhece URLs de portal Stalker (não Xtream/M3U). Cobre os endpoints
        /// clássicos (portal.php, /stb.php, /load.php, /portalstb/), o caminho de
        /// portal /c/ (ex.: http://host/c//c/) e URLs de reprodução com
        /// play_token= ou type=stb. Case-insensitive; apenas http(s).
        /// </summary>
        public bool IsStalkerPortalUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var lower = url.ToLowerInvariant();

            // Endpoints clássicos de portal Stalker.
            if (lower.Contains("portal.php") || lower.Contains("/stb.php") ||
                lower.Contains("/load.php") || lower.Contains("/portalstb/"))
            {
                return true;
            }

            // URLs de reprodução Stalker.
            if (lower.Contains("play_token=") || lower.Contains("type=stb"))
            {
                return true;
            }

            // Caminho de portal /c/ ou /c//c/ — apenas segmentos 'c'.
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length > 0 && segments.All(s => s == "c"))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Reconhece sinais de um cartão Stalker Portal no texto de uma mensagem:
        /// MAC válido, play_token=, stb_lang/type=stb, ou o par Serial + Device_Id.
        /// Não implica aquisição: serve para identificar cartões Stalker e ignorá-los.
        /// </summary>
        public bool LooksLikeStalkerCard(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            if (_macAddressRegex.IsMatch(text)) return true;

            var lower = text.ToLowerInvariant();

            if (_stalkerCardHints.Any(hint => lower.Contains(hint))) return true;

            // Identidade dupla típica de cartão Stalker (Serial + Device_Id).
            if (lower.Contains("serial") && lower.Contains("device_id")) return true;

            return false;
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
                    // Cartões Stalker Portal (portal.php, /c//c/, play_token=,
                    // type=stb, ...) não são Xtream/M3U: saltar por completo para
                    // não gastar tempo a descarregar/testar. O skip é por URL —
                    // um URL válido no mesmo texto continua a produzir candidato.
                    if (IsStalkerPortalUrl(m.Value)) continue;

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

            // Anexo HTML: nao sabemos se o conteudo e' uma publicacao Xtream ate
            // o descarregar e inspeccionar. Requer verificacao de conteudo
            // (RequiresContentVerification=true) para que o branch novo em
            // SearchAndTestM3UInTelegramAsync (LooksLikeHtmlPublication ->
            // XtreamPublicationResolver) seja activado. O download e' feito por
            // ProcessAttachmentCandidatesAsync / DownloadTelegramDocumentTextAsync
            // exactamente como para anexos .m3u.
            if (IsHtmlFilename(filename))
            {
                candidates.Add(new CandidatePlaylist
                {
                    Kind = CandidateSourceKind.Attachment,
                    FileName = filename,
                    SourceText = text,
                    Content = attachmentContent,
                    DetectedFrom = "html attachment",
                    RequiresContentVerification = true
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

            // Cartão Stalker detetado sem qualquer candidato válido: ignorado por
            // inteiro (devolve lista vazia). Se houver candidatos válidos no
            // mesmo texto (Xtream/M3U/HTML), devolve-os normalmente.
            if (candidates.Count == 0 && LooksLikeStalkerCard(text))
            {
                return candidates;
            }

            return candidates;
        }
    }
}
