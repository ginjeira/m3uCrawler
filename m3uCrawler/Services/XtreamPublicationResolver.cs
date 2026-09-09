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
            "m3u", "m3u url", "m3u_url", "playlist", "playlist url"
        };
        private static readonly string[] EpgLabels =
        {
            "epg", "epg url", "epg_url", "xmltv", "xmltv url", "guide"
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

            return Deduplicate(accounts);
        }

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
                return !string.IsNullOrWhiteSpace(host);
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
    }
}
