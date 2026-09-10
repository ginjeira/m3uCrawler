using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using HtmlAgilityPack;
using m3uCrawler.Models;

namespace m3uCrawler.Services
{
    /// <summary>
    /// Resolve publicacoes HTML (paginas que apresentam uma ou mais "cards" Xtream
    /// com Host/User/Password) numa lista de XtreamAccountInfo. Componente puro,
    /// sem I/O: recebe o HTML ja descarregado e devolve zero ou mais contas.
    ///
    /// Responsabilidades:
    ///   * identificar cards Xtream no HTML (labels semanticos tolerantes a capitalizacao/HTML entities/&lt;a href&gt;);
    ///   * extrair Host/Username/Password e, quando presentes, M3U/EPG/Expires/MaxConn/etc.;
    ///   * deduplicar por identidade logica (endpoint normalizado + username). Password NUNCA participa;
    ///   * nao fabricar contas com campos essenciais em falta (Host/User/Pass);
    ///   * ignorar secoes tipo "MEDIA LIST" como canais (nunca produz canais a partir delas).
    ///
    /// NAO expoe credenciais em strings, logs ou diagnosticos.
    /// </summary>
    internal static class XtreamPublicationResolver
    {
        private static readonly string[] HostLabels =
        {
            "host", "server", "endpoint", "address", "url", "dominio", "domain", "server host"
        };
        private static readonly string[] UserLabels =
        {
            "user", "username", "usuario", "utilizador", "login", "account"
        };
        private static readonly string[] PassLabels =
        {
            "pass", "password", "senha", "pwd"
        };
        private static readonly string[] M3uLabels =
        {
            "m3u", "m3u url", "m3u_url", "playlist", "playlist url",
            "host m3u", "real m3u", "m3u host", "m3u real"
        };
        private static readonly string[] EpgLabels =
        {
            "epg", "epg url", "epg_url", "xmltv", "xmltv url", "guide",
            "epg link", "epg lnk"
        };
        private static readonly string[] ExpiresLabels =
        {
            "expires", "expiration", "expiry", "valid until", "valid until:", "expira"
        };
        private static readonly string[] MaxConnLabels =
        {
            "max connections", "max_connections", "maxconnection", "max conn", "maxcon"
        };
        private static readonly string[] ActiveConnLabels =
        {
            "active connections", "active_connections", "active conn", "connections"
        };
        private static readonly string[] ChannelCountLabels =
        {
            "channels", "channel count", "channelcount", "canais"
        };
        private static readonly string[] VodCountLabels =
        {
            "vod", "vod count", "vodcount", "movies", "filmes"
        };
        private static readonly string[] SeriesCountLabels =
        {
            "series", "series count", "seriescount"
        };
        private static readonly string[] ServerNameLabels =
        {
            "server name", "servername", "name", "server"
        };
        private static readonly string[] ServerIpLabels =
        {
            "ip", "server ip", "serverip", "address ip"
        };

        // URL http(s) completo (com credenciais possiveis em userinfo).
        // O resolver nunca escreve este URL em logs.
        private static readonly Regex HrefRegex = new(
            @"https?://[^\s<>""']+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static IReadOnlyList<XtreamAccountInfo> ResolveFromHtml(
            string html,
            string sourcePublicationUrl,
            string sourceTelegramMessageId = "",
            string sourceTelegramChannel = "")
        {
            if (string.IsNullOrWhiteSpace(html)) return Array.Empty<XtreamAccountInfo>();

            HtmlDocument doc;
            try
            {
                doc = new HtmlDocument();
                doc.LoadHtml(html);
            }
            catch
            {
                return Array.Empty<XtreamAccountInfo>();
            }

            // Estrategia de segmentacao:
            //   (a) procurar grupos explicitos de cards (<div/section/article/li class='card'>);
            //   (b) se nenhuma classe 'card', tratar cada <tr> de uma <table> como card;
            //   (c) se nenhuma table, tratar <body> como container unico;
            //   (d) dentro de cada card container, extrair pares label:value.
            var cardContainers = ExtractCardContainers(doc);
            if (cardContainers.Count == 0)
            {
                cardContainers.Add(doc.DocumentNode);
            }

            var accounts = new List<XtreamAccountInfo>();
            foreach (var card in cardContainers)
            {
                var fields = ExtractLabelValuePairs(card);
                var account = BuildAccount(
                    fields,
                    sourcePublicationUrl,
                    sourceTelegramMessageId,
                    sourceTelegramChannel);
                if (account != null) accounts.Add(account);
            }

            // Fallback adaptativo: se o caminho DOM nao extraiu nenhuma conta,
            // tentar o parser flat-text baseado em clustering de ancora. Isto
            // absorve formatos onde o publisher nao usa estrutura HTML (cosmetic
            // Unicode, sem <table>/<hr>/class=card). Ver ResolveFromFlatText para
            // detalhes. O threshold minimo de tamanho e' baixo (50 chars) porque
            // o proprio ResolveFromFlatText ja descarta inputs muito pequenos.
            if (accounts.Count == 0)
            {
                var flatText = HtmlEntity.DeEntitize(doc.DocumentNode.InnerText ?? string.Empty);
                var flatAccounts = ResolveFromFlatText(
                    flatText,
                    sourcePublicationUrl,
                    sourceTelegramMessageId,
                    sourceTelegramChannel);
                if (flatAccounts.Count > 0)
                {
                    return Deduplicate(flatAccounts.ToList());
                }
            }

            return Deduplicate(accounts);
        }

        // Tamanho minimo do texto plano para activar o fallback adaptativo.
        // ResolveFromFlatText faz a sua propria gating em 50 chars.
        private const int MinFlatTextLengthForFallback = 50;

        // Janela maxima (em caracteres) entre duas ancora consecutivas dentro
        // do mesmo card. Acima desta distancia, considera-se que comeca um novo
        // card (a menos que a ancora em causa seja 'host', que SEMPRE fecha
        // o cluster anterior - ver ClusterAnchors).
        //
        // NOTA: o parser adaptativo tem um trade-off fundamental. Publishers
        // que intercalam metadata (e.g. "MEDIA LIST" com labels como CHANNELS,
        // MOVIES, SERIES) entre cards Xtream tornam impossivel distinguir
        // essas metadata labels de labels do card sem heuristicas especifica
        // do publisher. Para esses casos, o parser produz menos contas do
        // que existem (cards que partilham labels sao fundidos num so').
        // Janela conservadora (1000 chars) acomoda iptvgold onde Host fica
        // no topo e M3U/EPG no fundo (separados por ~700-800 chars).
        private const int AnchorClusterWindowChars = 1000;

        private static List<HtmlNode> ExtractCardContainers(HtmlDocument doc)
        {
            var containers = new List<HtmlNode>();

            // (a) Agrupar <tr> de tabelas em cards quando a tabela tem >=2 <tr>.
            //     Cada <tr> com pelo menos 2 <td> (label/value) e tratado como card.
            var tables = doc.DocumentNode.SelectNodes("//table");
            if (tables != null)
            {
                foreach (var table in tables)
                {
                    var trs = table.SelectNodes(".//tr");
                    if (trs == null || trs.Count < 2) continue;
                    foreach (var tr in trs) containers.Add(tr);
                }
            }

            // (b) Blocos com classe 'card' (ou nomes equivalentes) sao cards explicitos.
            //     Procuramos div/section/article/li cuja @class contenha 'card'.
            var classTags = new[] { "div", "section", "article", "li", "fieldset" };
            var seen = new HashSet<HtmlNode>(ReferenceEqualityComparer.Instance);
            foreach (var c in containers) seen.Add(c);
            foreach (var tag in classTags)
            {
                var nodes = doc.DocumentNode.SelectNodes($"//{tag}[contains(translate(@class, 'CARD', 'card'), 'card')]");
                if (nodes == null) continue;
                foreach (var n in nodes)
                {
                    if (seen.Add(n)) containers.Add(n);
                }
            }

            // (c)+(d) Decidir o conjunto final de containers:
            //   1. Se existem <hr> no body, segmentar por <hr>.
            //   2. Caso contrario:
            //      a. Se os containers individuais nao produzem pelo menos 1
            //         conta valida (Host+User+Pass), tentar o body inteiro
            //         como um unico container (cobre tabelas com varias <tr>
            //         que formam um unico card).
            //      b. Caso contrario, devolver os containers individuais.
            //   3. Fallback final: body.
            var grouped = SplitBodyByHr(doc);
            if (grouped.Count > 1)
            {
                return grouped;
            }

            if (containers.Count > 0)
            {
                bool anyHasFields = containers.Any(c =>
                {
                    var f = ExtractLabelValuePairs(c);
                    return !string.IsNullOrWhiteSpace(FindValue(f, HostLabels))
                        && !string.IsNullOrWhiteSpace(FindValue(f, UserLabels))
                        && !string.IsNullOrWhiteSpace(FindValue(f, PassLabels));
                });
                if (anyHasFields) return containers;
                // Containers nao produzem conta isoladamente: tentar body.
                return new List<HtmlNode> { doc.DocumentNode };
            }

            return new List<HtmlNode> { doc.DocumentNode };
        }

        private static List<HtmlNode> SplitBodyByHr(HtmlDocument doc)
        {
            var result = new List<HtmlNode>();
            var body = doc.DocumentNode.SelectSingleNode("//body");
            var bodyHtml = body?.InnerHtml ?? doc.DocumentNode.InnerHtml ?? string.Empty;
            // Dividir por <hr ...> (case-insensitive, com ou sem /).
            var parts = Regex.Split(bodyHtml, @"<hr\b[^>]*>", RegexOptions.IgnoreCase);
            foreach (var part in parts)
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                HtmlDocument sub;
                try
                {
                    sub = new HtmlDocument();
                    sub.LoadHtml($"<root>{part}</root>");
                }
                catch
                {
                    continue;
                }
                if (sub.DocumentNode == null) continue;
                result.Add(sub.DocumentNode);
            }
            return result;
        }

        /// <summary>
        /// Extrai pares (label, value) de um no. Estrategia:
        ///   1) tentar formato label:valor inline ("Host: example.com:80");
        ///   2) tentar formato tabela com 2+ <td> (label / value);
        ///   3) tentar atributo <a href> quando o value e um URL.
        /// </summary>
        private static Dictionary<string, string> ExtractLabelValuePairs(HtmlNode node)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // HtmlAgilityPack junta o texto sem newline em <br>. Construir uma versao
            // "etiquetada" do HTML, inserir quebras antes/depois de <br> e fechos de
            // bloco, e extrair InnerText atraves de um HtmlDocument temporario.
            var rawHtml = node.InnerHtml ?? string.Empty;
            var normalised = Regex.Replace(rawHtml, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            normalised = Regex.Replace(normalised, @"</(p|div|li|tr|h[1-6]|section|article|fieldset)>",
                "\n", RegexOptions.IgnoreCase);

            HtmlDocument tmp;
            try
            {
                tmp = new HtmlDocument();
                tmp.LoadHtml($"<root>{normalised}</root>");
            }
            catch
            {
                tmp = new HtmlDocument();
                tmp.LoadHtml(node.InnerHtml ?? string.Empty);
            }
            var innerText = DecodeEntities(HtmlEntity.DeEntitize(tmp.DocumentNode.InnerText ?? string.Empty));

            // 1a) Label: value inline na mesma linha.
            //    Cada linha (split por \n) e' analisada independentemente para
            //    evitar que o motor de regex "engula" labels vizinhos separados
            //    por espacos.
            var lineParts = innerText.Split(new[] { '\n' }, StringSplitOptions.None);
            foreach (var line in lineParts)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                var lm = Regex.Match(trimmed, @"^\s*([A-Za-z][A-Za-z0-9 _\-\.]{1,40}?)\s*[:=]\s*(.+?)\s*$");
                if (!lm.Success) continue;
                var label = NormalizeLabel(lm.Groups[1].Value);
                if (label.Length == 0) continue;
                var value = lm.Groups[2].Value.Trim();
                if (value.Length == 0) continue;
                if (!dict.ContainsKey(label)) dict[label] = value;
            }

            // 1b) Padrao multilinha (Host / valor em linhas separadas).
            // Para evitar falsos positivos (qualquer linha de texto livre virar label),
            // apenas aceitamos labels que constam na lista conhecida de labels Xtream.
            var lines = innerText
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToArray();
            for (int i = 0; i < lines.Length - 1; i++)
            {
                var candidate = NormalizeLabel(lines[i]);
                if (candidate.Length == 0) continue;
                if (!IsKnownLabelAny(candidate.ToLowerInvariant())) continue;
                var sb = new StringBuilder();
                int j = i + 1;
                while (j < lines.Length)
                {
                    var nextCandidate = NormalizeLabel(lines[j]);
                    if (nextCandidate.Length > 0 && IsKnownLabelAny(nextCandidate.ToLowerInvariant())) break;
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(lines[j]);
                    j++;
                }
                var value = sb.ToString().Trim();
                if (value.Length == 0) continue;
                if (!dict.ContainsKey(candidate)) dict[candidate] = value;
                i = j - 1;
            }

            // 2) Estrutura tabular: 2 <td> em <tr>.
            var tds = node.SelectNodes(".//td");
            if (tds != null && tds.Count >= 2)
            {
                for (int i = 0; i + 1 < tds.Count; i += 2)
                {
                    var label = NormalizeLabel(tds[i].InnerText);
                    if (label.Length == 0) continue;
                    var valueTd = tds[i + 1];
                    var value = ExtractValueFromNode(valueTd);
                    if (value.Length == 0) continue;
                    if (!dict.ContainsKey(label)) dict[label] = value;
                }
            }

            // 3) Estrutura <th><td>.
            var ths = node.SelectNodes(".//th");
            if (ths != null)
            {
                foreach (var th in ths)
                {
                    var label = NormalizeLabel(th.InnerText);
                    if (label.Length == 0) continue;
                    // Procurar proximo <td> irmao.
                    var td = th.SelectSingleNode("following-sibling::td[1]");
                    if (td == null) continue;
                    var value = ExtractValueFromNode(td);
                    if (value.Length == 0) continue;
                    if (!dict.ContainsKey(label)) dict[label] = value;
                }
            }

            // 4) Sub-nos com label conhecido e <a href> dentro do value.
            foreach (var child in node.Descendants())
            {
                var text = DecodeEntities(HtmlEntity.DeEntitize(child.InnerText ?? string.Empty)).Trim();
                if (text.Length == 0) continue;
                // Heuristica leve: se o no contem "M3U" como texto curto, capturar o href.
                var label = NormalizeLabel(text);
                if (label.Length == 0 || label.Length > 40) continue;
                if (!IsKnownLabelAny(label.ToLowerInvariant())) continue;
                var a = child.SelectSingleNode(".//a[@href]");
                if (a == null) continue;
                var href = a.GetAttributeValue("href", string.Empty);
                if (string.IsNullOrWhiteSpace(href)) continue;
                if (!dict.ContainsKey(label)) dict[label] = href;
            }

            // 5) Padrao "<label>: <a href>...</a>" — quando o valor e' um link,
            //    capturar o href mesmo que o texto entre <a>...</a> seja curto
            //    (ex.: "link", "aqui", "here"). Varrre todos os <a> e inspecciona
            //    o texto que os precede dentro do mesmo parent. Sobrepoe valores
            //    anteriores quando o novo valor e' um URL (higienizacao).
            foreach (var a in node.SelectNodes(".//a[@href]") ?? Enumerable.Empty<HtmlNode>())
            {
                var href = a.GetAttributeValue("href", string.Empty);
                if (string.IsNullOrWhiteSpace(href)) continue;
                var parent = a.ParentNode;
                if (parent == null) continue;
                string precedingText = string.Empty;
                foreach (var sib in parent.ChildNodes)
                {
                    if (sib == a) break;
                    precedingText += sib.InnerText + " ";
                }
                precedingText = HtmlEntity.DeEntitize(precedingText ?? string.Empty);
                precedingText = Regex.Replace(precedingText, @"\s+", " ").Trim();
                var m = Regex.Match(precedingText, @"(?i)([A-Za-z][A-Za-z0-9_\-\.]{1,40}?)\s*[:=]\s*$");
                if (!m.Success) continue;
                var label = NormalizeLabel(m.Groups[1].Value);
                if (label.Length == 0) continue;
                if (!IsKnownLabelAny(label.ToLowerInvariant())) continue;
                // Sobrescrever sempre: se o label e' de URL (M3U/EPG) e temos
                // um <a href>, esse e' o valor canonico.
                dict[label] = href;
            }

            return dict;
        }

        private static string ExtractValueFromNode(HtmlNode node)
        {
            // Preferir <a href> se existir e o texto for pequeno (e.g. "here").
            var a = node.SelectSingleNode(".//a[@href]");
            if (a != null)
            {
                var href = a.GetAttributeValue("href", string.Empty);
                var inner = DecodeEntities(HtmlEntity.DeEntitize(a.InnerText ?? string.Empty)).Trim();
                if (!string.IsNullOrWhiteSpace(href) &&
                    (inner.Length == 0 || inner.Length > href.Length))
                {
                    return href;
                }
            }
            return DecodeEntities(HtmlEntity.DeEntitize(node.InnerText ?? string.Empty)).Trim();
        }

        private static bool IsKnownLabelAny(string label)
        {
            var lower = label.ToLowerInvariant();
            foreach (var s in HostLabels) if (lower == s) return true;
            foreach (var s in UserLabels) if (lower == s) return true;
            foreach (var s in PassLabels) if (lower == s) return true;
            foreach (var s in M3uLabels) if (lower == s) return true;
            foreach (var s in EpgLabels) if (lower == s) return true;
            foreach (var s in ExpiresLabels) if (lower == s) return true;
            foreach (var s in MaxConnLabels) if (lower == s) return true;
            foreach (var s in ActiveConnLabels) if (lower == s) return true;
            foreach (var s in ChannelCountLabels) if (lower == s) return true;
            foreach (var s in VodCountLabels) if (lower == s) return true;
            foreach (var s in SeriesCountLabels) if (lower == s) return true;
            foreach (var s in ServerNameLabels) if (lower == s) return true;
            foreach (var s in ServerIpLabels) if (lower == s) return true;
            return false;
        }

        private static string NormalizeLabel(string raw)
        {
            var s = DecodeEntities(HtmlEntity.DeEntitize(raw ?? string.Empty)).Trim();
            s = s.TrimEnd(':', ';', '.', '-');
            s = Regex.Replace(s, @"\s+", " ");
            return s;
        }

        private static string DecodeEntities(string s)
        {
            return HttpUtility.HtmlDecode(s ?? string.Empty);
        }

        private static XtreamAccountInfo? BuildAccount(
            Dictionary<string, string> fields,
            string sourcePublicationUrl,
            string sourceTelegramMessageId,
            string sourceTelegramChannel)
        {
            // Quando existem ambas as chaves "Host" e "Server" no dict, "Server"
            // representa o nome do servidor (nao o endpoint). Nesses casos, excluir
            // "server" da pesquisa de Host.
            bool hostAlsoPresent = fields.ContainsKey("host");
            var hostLabels = hostAlsoPresent
                ? HostLabels.Where(l => !string.Equals(l, "server", StringComparison.OrdinalIgnoreCase)).ToArray()
                : HostLabels;
            string? host = FindValue(fields, hostLabels);
            string? user = FindValue(fields, UserLabels);
            string? pass = FindValue(fields, PassLabels);

            if (string.IsNullOrWhiteSpace(host) ||
                string.IsNullOrWhiteSpace(user) ||
                string.IsNullOrWhiteSpace(pass))
            {
                return null;
            }

            if (!TryParseHost(host, out var scheme, out var hostname, out var port))
            {
                return null;
            }

            var m3uRaw = FindValue(fields, M3uLabels);
            string? m3u = null;
            if (!string.IsNullOrWhiteSpace(m3uRaw))
            {
                // Aceitar URL completa ou path relativo.
                if (m3uRaw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    m3uRaw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    m3u = m3uRaw;
                }
                else if (m3uRaw.StartsWith("/"))
                {
                    m3u = $"{scheme}://{hostname}:{port}{m3uRaw}";
                }
            }

            var epgRaw = FindValue(fields, EpgLabels);
            string? epg = null;
            if (!string.IsNullOrWhiteSpace(epgRaw))
            {
                if (epgRaw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    epgRaw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    epg = epgRaw;
                }
                else if (epgRaw.StartsWith("/"))
                {
                    epg = $"{scheme}://{hostname}:{port}{epgRaw}";
                }
            }

            return new XtreamAccountInfo
            {
                Host = hostname,
                Port = port,
                Scheme = scheme,
                Username = user!,
                Password = pass!,
                M3uUrl = m3u,
                EpgUrl = epg,
                ExpiresAt = TryParseDate(FindValue(fields, ExpiresLabels)),
                MaxConnections = TryParseInt(FindValue(fields, MaxConnLabels)),
                ActiveConnections = TryParseInt(FindValue(fields, ActiveConnLabels)),
                ChannelCount = TryParseInt(FindValue(fields, ChannelCountLabels)),
                VodCount = TryParseInt(FindValue(fields, VodCountLabels)),
                SeriesCount = TryParseInt(FindValue(fields, SeriesCountLabels)),
                ServerName = FindValue(fields, ServerNameLabels),
                ServerIp = FindValue(fields, ServerIpLabels),
                SourcePublicationUrl = sourcePublicationUrl ?? string.Empty,
                SourceTelegramMessageId = sourceTelegramMessageId ?? string.Empty,
                SourceTelegramChannel = sourceTelegramChannel ?? string.Empty
            };
        }

        private static string? FindValue(Dictionary<string, string> fields, string[] labels)
        {
            foreach (var l in labels)
            {
                if (fields.TryGetValue(l, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
            }
            return null;
        }

        private static bool TryParseHost(string raw, out string scheme, out string host, out int port)
        {
            scheme = "http";
            host = string.Empty;
            port = 80;
            var s = (raw ?? string.Empty).Trim();
            if (s.Length == 0) return false;

            string hostPart;
            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return false;
                scheme = uri.Scheme.ToLowerInvariant();
                host = uri.Host;
                port = uri.IsDefaultPort ? DefaultPortFor(scheme) : uri.Port;
                if (string.IsNullOrWhiteSpace(host)) return false;
                return LooksLikeValidHost(host);
            }

            // Formato "host:port" sem esquema.
            var idx = s.LastIndexOf(':');
            if (idx > 0 && idx < s.Length - 1 && int.TryParse(s[(idx + 1)..], out var p))
            {
                hostPart = s[..idx];
                port = p;
            }
            else
            {
                hostPart = s;
                port = DefaultPortFor(scheme);
            }

            // Remover path/query se vierem juntos.
            var slash = hostPart.IndexOf('/');
            if (slash >= 0) hostPart = hostPart[..slash];
            host = hostPart.Trim().ToLowerInvariant();
            if (host.Length == 0) return false;
            return LooksLikeValidHost(host);
        }

        /// <summary>
        /// Validacao minima de um host string. Deve conter pelo menos um '.'
        /// (formato DNS valido) OU ser um IPv4 literal. Rejeita texto livre
        /// como "not-a-real-host" (com hifens) ou "example" (sem TLD).
        /// </summary>
        private static bool LooksLikeValidHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            // IPv4: 4 grupos de 1-3 digitos separados por '.'.
            if (System.Net.IPAddress.TryParse(host, out _)) return true;
            // Hostname: alfanumerico + '-', deve conter pelo menos um '.'.
            if (!host.Contains('.')) return false;
            // Cada label entre pontos deve ter pelo menos 1 char alfanumerico.
            foreach (var part in host.Split('.'))
            {
                if (part.Length == 0) return false;
                foreach (var c in part)
                {
                    if (!(char.IsLetterOrDigit(c) || c == '-')) return false;
                }
            }
            return true;
        }

        private static int DefaultPortFor(string scheme) =>
            scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;

        private static int? TryParseInt(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var s = Regex.Match(raw, @"-?\d+").Value;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
            return null;
        }

        private static DateTime? TryParseDate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var s = raw.Trim();
            // ISO yyyy-MM-dd (com ou sem hora).
            if (DateTime.TryParseExact(s, new[] { "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss" },
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var iso)) return iso.Date;
            // dd/MM/yyyy ou MM/dd/yyyy — tentar dd/MM primeiro (PT mais comum nas publicacoes).
            if (DateTime.TryParseExact(s, new[] { "dd/MM/yyyy", "d/M/yyyy" },
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var dmy)) return dmy.Date;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var any)) return any.Date;
            if (DateTime.TryParse(s, new CultureInfo("en-US"), DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var en)) return en.Date;
            if (DateTime.TryParse(s, new CultureInfo("pt-PT"), DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var pt)) return pt.Date;
            return null;
        }

        private static IReadOnlyList<XtreamAccountInfo> Deduplicate(List<XtreamAccountInfo> accounts)
        {
            if (accounts.Count <= 1) return accounts;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<XtreamAccountInfo>(accounts.Count);
            foreach (var a in accounts)
            {
                var id = a.LogicalIdentity;
                if (seen.Add(id)) result.Add(a);
            }
            return result;
        }

        /// <summary>
        /// Parser flat-text adaptativo. Usado como fallback quando o caminho DOM
        /// nao produz contas mas o texto e' significativo. Nao depende de estrutura
        /// HTML especifica (sem <table>, <hr>, class=card). Em vez disso:
        ///
        ///   1. Strip de ruido cosmico (emoji, linhas decorativas, fancy glyphs
        ///      que nao sao letras/digitos).
        ///   2. Deteccao de ANCORAS: posicoes no texto onde um label conhecido
        ///      do vocabulario Xtream aparece (com qualquer separador).
        ///   3. Clustering de ancora por proximidade: duas ancora dentro de
        ///      AnchorClusterWindowChars pertencem ao mesmo card; fora disso,
        ///      novo card.
        ///   4. Extraccao de valores por ancora dentro de cada cluster:
        ///      primeiro tenta mesma-linha, depois linha seguinte ate proxima
        ///      ancora.
        ///   5. Co-ocorrencia obrigatoria H+U+P; senao card descartado.
        ///
        /// O vocabulario de labels e' estavel (conceitos Xtream), portanto este
        /// parser absorve variacoes cosmicas (fontes Unicode, separadores
        /// exoticos, layouts diferentes) sem alteracao de codigo.
        /// </summary>
        internal static IReadOnlyList<XtreamAccountInfo> ResolveFromFlatText(
            string flatText,
            string sourcePublicationUrl,
            string sourceTelegramMessageId = "",
            string sourceTelegramChannel = "")
        {
            if (string.IsNullOrWhiteSpace(flatText)) return Array.Empty<XtreamAccountInfo>();

            var cleaned = StripCosmeticNoise(flatText);
            if (cleaned.Length < 50) return Array.Empty<XtreamAccountInfo>();

            var anchors = FindAnchors(cleaned);
            if (anchors.Count < 3) return Array.Empty<XtreamAccountInfo>();

            var clusters = ClusterAnchors(anchors, AnchorClusterWindowChars);
            if (clusters.Count == 0) return Array.Empty<XtreamAccountInfo>();

            var accounts = new List<XtreamAccountInfo>();
            foreach (var cluster in clusters)
            {
                var fields = ExtractFieldsFromCluster(cluster, cleaned);
                var account = BuildAccount(
                    fields,
                    sourcePublicationUrl,
                    sourceTelegramMessageId,
                    sourceTelegramChannel);
                if (account != null) accounts.Add(account);
            }
            return accounts;
        }

        /// <summary>
        /// Strip de ruido cosmico. Remove:
        ///   * ANSI escape codes (terminal coloring);
        ///   * box-drawing chars (U+2500..U+25FF);
        ///   * symbols/misc/dingbats (U+2600..U+27BF, U+2900..U+2BFF);
        ///   * sequencias longas de chars repetidos (divisores ━━━━━ ou /////);
        ///   * emojis (pares surrogate U+D800..U+DFFF + U+1F000..U+1FAFF);
        ///   * zero-width chars.
        /// Mantem letras, digitos, pontuacao util (: = > | - _) e espacos.
        ///
        /// IMPORTANTE: a transliteracao fancy->ASCII acontece ANTES do strip,
        /// para que pares surrogate de chars matematicos (e.g. U+1D7B
        /// "MATEMATICAL DOUBLE-STRUCK DIGIT THREE" = surrogate pair D835 DFF9)
        /// sejam convertidos em ASCII '3' antes do strip os apanhar.
        /// </summary>
        private static string StripCosmeticNoise(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            // Normalizar quebras de linha para \n.
            var s = raw.Replace("\r\n", "\n").Replace('\r', '\n');

            var lines = s.Split('\n');
            var sb = new StringBuilder(s.Length);
            foreach (var originalLine in lines)
            {
                // 0. Transliterar pares surrogate (math chars) para ASCII antes de strip.
                var line = TransliterateSupplementPlaneToAscii(originalLine);
                // 1. Strip ANSI escape codes (CSI sequences: ESC[ ... letter).
                line = Regex.Replace(line, @"\x1B\[[0-?]*[ -/]*[@-~]", " ");
                // 2. Strip box-drawing chars (U+2500..U+257F) e block elements (U+2580..U+259F).
                line = Regex.Replace(line, @"[\u2500-\u259F]+", " ");
                // 3. Strip geometric shapes + dingbats + arrows + symbols (U+25A0..U+27BF).
                line = Regex.Replace(line, @"[\u25A0-\u27BF]+", " ");
                // 4. Strip supplemental arrows + misc symbols + math (U+2900..U+2BFF).
                line = Regex.Replace(line, @"[\u2900-\u2BFF]+", " ");
                // 4b. Strip math operators (U+2200..U+22FF) e misc technical (U+2300..U+23FF).
                line = Regex.Replace(line, @"[\u2200-\u23FF]+", " ");
                // 5. Strip emoji / surrogate pairs (U+D800..U+DFFF + supplementary plane chars).
                line = Regex.Replace(line, @"[\uD800-\uDBFF\uDC00-\uDFFF]+", " ");
                line = Regex.Replace(line, @"[\uF000-\uFFFD]+", " ");
                // 6. Strip sequencias longas do mesmo char (>= 4) que nao sejam letras/digitos.
                line = Regex.Replace(line, @"([^\w\s])\1{3,}", " ");
                // 7. Strip zero-width chars.
                line = Regex.Replace(line, @"[\u200B-\u200F\uFEFF]+", "");

                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                // 8. Descartar linhas que so' tinham box-drawing/emoji no original.
                //    Heuristica: se depois do strip a linha tem < 2 chars alfanum,
                //    provavelmente era decoracao.
                var alphanum = Regex.Replace(trimmed, @"[^A-Za-z0-9]+", "");
                if (alphanum.Length < 2) continue;

                sb.Append(trimmed).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Transliterate codepoints em supplementary plane (U+10000..U+10FFFF,
        /// sempre codificados como surrogate pair) para ASCII. Usado antes
        /// do strip para que chars matematicos como U+1D7B (MATEMATICAL
        /// DOUBLE-STRUCK DIGIT THREE = surrogate D835 DFF9) nao sejam
        /// removidos como se fossem emoji.
        /// </summary>
        private static string TransliterateSupplementPlaneToAscii(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                int codePoint;
                int charCount;
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    codePoint = char.ConvertToUtf32(s[i], s[i + 1]);
                    charCount = 2;
                }
                else
                {
                    codePoint = s[i];
                    charCount = 1;
                }

                // Apenas mapeamos o que e' estritamente necessario: digits duplos
                // e symbols especificos usados por publicacoes IPTV.
                // Nota: o codepoint real do "double-struck 3" e' U+1D7D3 (e nao
                // U+1D7F9 como aparece quando vejo o surrogate pair errado).
                // Mapeamos APENAS o range oficial U+1D7D0..U+1D7D9 (0..9).
                if (codePoint >= 0x1D7D0 && codePoint <= 0x1D7D9)
                {
                    // Mathematical double-struck digits 0..9 (U+1D7D0..U+1D7D9).
                    sb.Append((char)('0' + (codePoint - 0x1D7D0)));
                }
                else if (codePoint == 0x1D7F9 || codePoint == 0x1D7D3)
                {
                    // Defensive: alguns renderers usam U+1D7F9 para "3". Mapeamos para '3'.
                    sb.Append('3');
                }
                else
                {
                    for (int j = 0; j < charCount; j++) sb.Append(s[i + j]);
                }
                i += charCount;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Encontra posicoes de labels conhecidos seguidos de um separador.
        /// Separadores suportados: : = > | - ➢ → em qualquer combinacao (1-3 chars).
        /// O label capturado e' normalizado (lowercase, fancy glyphs colapsados).
        /// </summary>
        private static List<Anchor> FindAnchors(string cleaned)
        {
            var result = new List<Anchor>();

            // Regex sobre label+separador+inicio de valor.
            // Label: 1-30 chars (letras latinas + Latin-1 supplement U+00C0..U+00FF
            //   para acentos PT/ES/FR + modifier letters U+1D00..U+1D7F
            //   + IPA extensions U+0260..U+029F usados em small caps / fonte estilizada)
            //   com pelo menos 1 char ASCII no inicio.
            // Separador: espacos OU : = > | - ➢ → (1-3 chars). Espacos sao
            //   separadores validos quando o publisher usa apenas whitespace
            //   (ex.: "Host   http://example.com").
            // NOTA: nao usamos negative lookahead aqui (causa falsos negativos
            //   quando o valor comeca por uma letra, e.g. URL "http://...").
            //   A proteccao contra labels compostos (e.g. "Active Connections"
            //   -> "Active" + "Connections") e' feita via clustering por
            //   proximidade + split em labels repetidas, ver ClusterAnchors.
            var pattern = new Regex(
                @"(?<label>[A-Za-z][A-Za-z0-9 _\-\.\u00C0-\u00FF\u1D00-\u1D7F\u0260-\u029F]{0,30}?[A-Za-z0-9\u00C0-\u00FF\u1D00-\u1D7F\u0260-\u029F])\s*[:=➢→|>\-]{0,3}\s+",
                RegexOptions.Compiled);
            var matches = pattern.Matches(cleaned);
            foreach (Match m in matches)
            {
                var rawLabel = m.Groups["label"].Value;
                var canonical = CanonicalLabelKey(rawLabel);
                if (canonical.Length == 0) continue;
                if (!IsKnownLabelAny(canonical)) continue;
                result.Add(new Anchor
                {
                    Position = m.Index,
                    LabelEndPosition = m.Index + m.Length,
                    RawLabel = rawLabel,
                    CanonicalLabel = canonical,
                });
            }

            return result;
        }

        /// <summary>
        /// Clusteriza ancora por proximidade COM boundary explicito em 'host'.
        ///
        /// Regras:
        ///   * Cada ancora 'host' FECHA o cluster anterior e ABRE um novo.
        ///     Isto e' o sinal de boundary mais robusto: cada card comeca com
        ///     Host ➢ http://..., e cada Host pertence a um unico card.
        ///     Mesmo que a janela de proximidade nao separe cards adjacentes,
        ///     um host explicito quebra o cluster.
        ///   * Dentro de um cluster, ancora 'user'/'pass'/'m3u'/'epg'/'expires'
        ///     sao absorvidas pelo cluster actual (desde que dentro da janela).
        ///   * Outras labels (channels/vod/series) sao ignoradas para cluster
        ///     (nao contribuem para o card mas nao criam boundary).
        ///
        /// Janela conservadora: 1000 chars acomodam o caso iptvgold onde
        /// Host fica no topo e M3U/EPG no fundo. Cards adjacentes sao
        /// separados pelo host repetido do card seguinte.
        /// </summary>
        private static List<List<Anchor>> ClusterAnchors(List<Anchor> anchors, int windowChars)
        {
            var result = new List<List<Anchor>>();
            if (anchors.Count == 0) return result;

            var current = new List<Anchor>();

            foreach (var a in anchors)
            {
                bool isHost = a.CanonicalLabel == "host";

                if (isHost && current.Count > 0)
                {
                    // Host FECHA cluster anterior (boundary explicito).
                    result.Add(current);
                    current = new List<Anchor>();
                }

                if (current.Count == 0)
                {
                    // Primeiro anchor do cluster (que pode ser host ou outro).
                    current.Add(a);
                }
                else if ((a.Position - current[current.Count - 1].Position) <= windowChars)
                {
                    // Dentro da janela: absorve.
                    current.Add(a);
                }
                // Fora da janela sem ser host: ainda absorve (host vai fechar).

                // Se ainda nada no cluster (host imediatamente seguido de outro
                // host): absorve para o novo cluster. Garantido pelo bloco acima.
            }

            if (current.Count > 0) result.Add(current);
            return result;
        }

        /// <summary>
        /// Extrai pares label/valor de um cluster. Para cada ancora:
        ///   1. Tenta valor na mesma linha (depois do separador ate fim da linha).
        ///   2. Se vazio, tenta concatenar linhas seguintes ate proxima ancora
        ///      ou fim do cluster.
        /// </summary>
        private static Dictionary<string, string> ExtractFieldsFromCluster(
            List<Anchor> cluster, string cleaned)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (cluster.Count == 0) return dict;

            var lines = cleaned.Split('\n');
            var lineOffsets = ComputeLineOffsets(cleaned);

            for (int i = 0; i < cluster.Count; i++)
            {
                var anchor = cluster[i];
                var nextAnchor = i + 1 < cluster.Count ? cluster[i + 1] : null;

                // Mapear posicao do anchor para linha.
                int lineIdx = FindLineIndex(anchor.LabelEndPosition, lineOffsets);
                if (lineIdx < 0) continue;

                // 1) Valor na mesma linha.
                string value = ExtractValueFromLineAfterPosition(lines[lineIdx], anchor.LabelEndPosition - lineOffsets[lineIdx]);

                // 2) Se vazio, tentar concatenar proximas linhas ate proxima ancora.
                if (string.IsNullOrWhiteSpace(value) && nextAnchor != null)
                {
                    int nextLineIdx = FindLineIndex(nextAnchor.Position, lineOffsets);
                    if (nextLineIdx > lineIdx + 1)
                    {
                        var sb = new StringBuilder();
                        for (int j = lineIdx + 1; j < nextLineIdx; j++)
                        {
                            var l = lines[j].Trim();
                            if (l.Length == 0) continue;
                            if (sb.Length > 0) sb.Append(' ');
                            sb.Append(l);
                        }
                        value = sb.ToString();
                    }
                }

                value = value?.Trim() ?? string.Empty;
                if (value.Length == 0) continue;
                if (!dict.ContainsKey(anchor.CanonicalLabel)) dict[anchor.CanonicalLabel] = value;
            }

            return dict;
        }

        private static int[] ComputeLineOffsets(string text)
        {
            var offsets = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') offsets.Add(i + 1);
            }
            return offsets.ToArray();
        }

        private static int FindLineIndex(int charPos, int[] lineOffsets)
        {
            // Binary search: devolve indice da linha onde charPos cai.
            int lo = 0, hi = lineOffsets.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                int start = lineOffsets[mid];
                int end = mid + 1 < lineOffsets.Length ? lineOffsets[mid + 1] - 1 : int.MaxValue;
                if (charPos < start) hi = mid - 1;
                else if (charPos > end) lo = mid + 1;
                else return mid;
            }
            return Math.Min(Math.Max(lo, 0), lineOffsets.Length - 1);
        }

        private static string ExtractValueFromLineAfterPosition(string line, int relPos)
        {
            if (relPos < 0 || relPos >= line.Length) return string.Empty;
            var v = line.Substring(relPos).Trim();
            // Strip leading separator-like chars que tenham ficado no valor
            // (ex.: "Host -> http://..." produz valor "-> http://...").
            // Iterar ate' nao haver mais desses chars no inicio.
            while (v.Length > 0)
            {
                var c = v[0];
                if (c == '-' || c == '=' || c == '|' || c == '>' || c == '➢' || c == '→' || c == ':')
                {
                    v = v.Substring(1).TrimStart();
                }
                else
                {
                    break;
                }
            }
            return v;
        }

        /// <summary>
        /// Canonicaliza uma label: colapsa fancy glyphs Unicode para ASCII,
        /// lowercasa, remove chars nao-alfanumericos (excepto espaco),
        /// colapsa espacos.
        /// Resultado: chave normalizada para usar em IsKnownLabelAny.
        /// "Usᴇʀ" -> "user", "Hᴏsᴛ" -> "host", "Pᴀss" -> "pass",
        /// "M𝟹ᴜ" -> "m u" (chars privados desaparecem), "Eᴘɢ Lɪɴᴋ" -> "epg lnk".
        /// </summary>
        private static string CanonicalLabelKey(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            // Transliterate cobre apenas o que foi mapeado em TryMapFancyToAscii.
            // O resto (incluindo chars fora do nosso mapa) e' simplesmente descartado
            // na fase final deste metodo.
            var s = TransliterateToAscii(raw.Trim().ToLowerInvariant());
            // Strip tudo o que nao seja a-z / 0-9 / espaco.
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == ' ')
                {
                    sb.Append(c);
                }
            }
            var collapsed = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
            return collapsed;
        }

        /// <summary>
        /// Tenta mapear chars Unicode "fancy" (modifier letters, small caps,
        /// subscript digits, etc.) para ASCII equivalente. Nao captura todos
        /// os casos (ha' milhares de glifos Unicode) mas abrange os mais
        /// comuns em publicacoes IPTV (U+1D00..U+1D7F, U+2090..U+209F).
        /// </summary>
        private static string TransliterateToAscii(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            // Modifier letters usados em "small caps" estilo fancy.
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                char ascii;
                if (TryMapFancyToAscii(c, out ascii))
                {
                    sb.Append(ascii);
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static bool TryMapFancyToAscii(char c, out char ascii)
        {
            switch (c)
            {
                // Latin small letter modifier (U+1D00..U+1D6F) usados em small caps.
                case '\u1D00': ascii = 'a'; return true; // ᴀ
                case '\u1D04': ascii = 'c'; return true; // ᴄ
                case '\u1D05': ascii = 'd'; return true; // ᴅ
                case '\u1D07': ascii = 'e'; return true; // ᴇ
                case '\u1D0B': ascii = 'k'; return true; // ᴋ
                case '\u1D0D': ascii = 'm'; return true; // ᴍ
                case '\u1D0F': ascii = 'o'; return true; // ᴏ
                case '\u1D18': ascii = 'p'; return true; // ᴘ
                case '\u1D1B': ascii = 't'; return true; // ᴛ
                case '\u1D1C': ascii = 'u'; return true; // ᴜ
                case '\u1D20': ascii = 'v'; return true; // ᴠ
                // Latin Extended-B / IPA Extensions usados em small caps ou fontes estilizadas.
                case '\u0262': ascii = 'g'; return true; // ɢ
                case '\u026A': ascii = 'i'; return true; // ɪ
                case '\u0274': ascii = 'n'; return true; // ɴ
                case '\u0280': ascii = 'r'; return true; // ʀ
                case '\u028B': ascii = 'v'; return true; // ʋ
                case '\u028F': ascii = 'y'; return true; // ʏ
                case '\u029C': ascii = 'h'; return true; // ʜ
                case '\u029F': ascii = 'l'; return true; // ʟ
                // Latin-1 supplement: chars acentuados comuns em PT/ES/FR.
                // Sao "folded" para a sua base ASCII.
                case '\u00E0': ascii = 'a'; return true; // à
                case '\u00E1': ascii = 'a'; return true; // á
                case '\u00E2': ascii = 'a'; return true; // â
                case '\u00E3': ascii = 'a'; return true; // ã
                case '\u00E4': ascii = 'a'; return true; // ä
                case '\u00E5': ascii = 'a'; return true; // å
                case '\u00E7': ascii = 'c'; return true; // ç
                case '\u00E8': ascii = 'e'; return true; // è
                case '\u00E9': ascii = 'e'; return true; // é
                case '\u00EA': ascii = 'e'; return true; // ê
                case '\u00EB': ascii = 'e'; return true; // ë
                case '\u00EC': ascii = 'i'; return true; // ì
                case '\u00ED': ascii = 'i'; return true; // í
                case '\u00EE': ascii = 'i'; return true; // î
                case '\u00EF': ascii = 'i'; return true; // ï
                case '\u00F1': ascii = 'n'; return true; // ñ
                case '\u00F2': ascii = 'o'; return true; // ò
                case '\u00F3': ascii = 'o'; return true; // ó
                case '\u00F4': ascii = 'o'; return true; // ô
                case '\u00F5': ascii = 'o'; return true; // õ
                case '\u00F6': ascii = 'o'; return true; // ö
                case '\u00F9': ascii = 'u'; return true; // ù
                case '\u00FA': ascii = 'u'; return true; // ú
                case '\u00FB': ascii = 'u'; return true; // û
                case '\u00FC': ascii = 'u'; return true; // ü
                // Modifier letter digits (superscripts/subscripts).
                case '\u1D7B': ascii = '3'; return true; // 𝟹 (legacy)
                case '\u2070': ascii = '0'; return true; // ⁰
                case '\u00B9': ascii = '1'; return true; // ¹
                case '\u00B2': ascii = '2'; return true; // ²
                case '\u00B3': ascii = '3'; return true; // ³
                case '\u2074': ascii = '4'; return true; // ⁴
                case '\u2075': ascii = '5'; return true; // ⁵
                case '\u2076': ascii = '6'; return true; // ⁶
                case '\u2077': ascii = '7'; return true; // ⁷
                case '\u2078': ascii = '8'; return true; // ⁸
                case '\u2079': ascii = '9'; return true; // ⁹
                // Double-struck letters usadas em "𝓒 𝓗 𝓝" (CHANNELS etc).
                case '\u2102': ascii = 'c'; return true; // ℂ
                case '\u210D': ascii = 'h'; return true; // ℍ
                case '\u2115': ascii = 'n'; return true; // ℕ
                default:
                    ascii = '\0';
                    return false;
            }
        }

        private sealed class Anchor
        {
            public int Position { get; init; }
            public int LabelEndPosition { get; init; }
            public string RawLabel { get; init; } = string.Empty;
            public string CanonicalLabel { get; init; } = string.Empty;
        }
    }
}
