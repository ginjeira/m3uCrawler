using System.Diagnostics;
using System.Text.RegularExpressions;
using m3uCrawler.Models;

namespace m3uCrawler.Services
{
    /// <summary>
    /// Faz o parsing de conteúdo M3U/M3U8 segundo o contrato W3
    /// (<c>docs/Reestructure/04-PLAYLIST-STREAM.md</c> §7):
    /// <list type="bullet">
    ///   <item>a primeira linha não vazia/não-BOM tem de ser <c>#EXTM3U</c>;</item>
    ///   <item>entrada válida = <c>#EXTINF</c> seguido do próximo conteúdo
    ///         não vazio, um URL absoluto http/https;</item>
    ///   <item>entrada malformada é registada e o parsing continua
    ///         (<see cref="M3uPlaylistStatus.Partial"/>);</item>
    ///   <item>alvo não utilizável (esquema não http/https) não é uma entrada
    ///         malformada, mas é excluído das streams;</item>
    ///   <item>metadados benignos (<c>#...</c> não-directiva) não são streams,
    ///         não geram malformados e não alteram o estado.</item>
    /// </list>
    /// Preserva os metadados do EXTINF (título, grupo, logo, tvg-id) e a linha
    /// EXTINF original em <see cref="M3uStream.OriginalExtInf"/>. As variantes
    /// HLS (#EXT-X-STREAM-INF) continuam a ser tratadas como metadados: a sua
    /// URL gera stream com os atributos da directiva, não uma entrada de canal.
    /// </summary>
    public class M3uParserService
    {
        private const string Header = "#EXTM3U";

        private readonly M3uParserOptions _options;

        public M3uParserService()
            : this(M3uParserOptions.Default)
        {
        }

        public M3uParserService(M3uParserOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// Conveniência retro-compatível: devolve apenas as streams válidas do
        /// resultado detalhado. Callers de baixo risco continuam a compilar.
        /// </summary>
        public List<M3uStream> Parse(string? content)
            => ParseDetailed(content).Streams.ToList();

        /// <summary>
        /// Parsing detalhado (contrato W3). Nunca lança para input inválido:
        /// devolve <see cref="M3uParseResult"/> com <c>Status</c> Failed,
        /// Partial ou Success e diagnóstico sanitizado.
        /// </summary>
        public M3uParseResult ParseDetailed(string? content, CancellationToken ct = default)
        {
            var streams = new List<M3uStream>();
            var diagnostics = new List<M3uParseDiagnostic>();
            int valid = 0, malformed = 0, unusable = 0, benign = 0, ignored = 0;
            bool parserFailure = false;

            if (string.IsNullOrWhiteSpace(content))
            {
                diagnostics.Add(Diag(
                    M3uParseDiagnosticKind.MissingHeader, 0, string.Empty,
                    "playlist vazia: não existe cabeçalho #EXTM3U"));
                return Build(streams, diagnostics, valid, malformed, unusable, benign, ignored);
            }

            if (content.Length > _options.MaxDocumentLength)
            {
                diagnostics.Add(Diag(
                    M3uParseDiagnosticKind.ParserFailure, 0, string.Empty,
                    $"documento excede o tamanho máximo ({_options.MaxDocumentLength} caracteres)"));
                return Build(streams, diagnostics, valid, malformed, unusable, benign, ignored);
            }

            var normalized = NormalizeContent(content);
            var lines = normalized.Split('\n');
            var stopwatch = Stopwatch.StartNew();

            // 1) Cabeçalho: primeira linha não vazia, não-BOM.
            var headerIndex = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                if (CleanLine(lines[i]).Length == 0) continue;
                headerIndex = i;
                break;
            }

            if (headerIndex < 0 || !IsHeader(CleanLine(lines[headerIndex])))
            {
                var lineNumber = headerIndex >= 0 ? headerIndex + 1 : 0;
                var line = headerIndex >= 0 ? CleanLine(lines[headerIndex]) : string.Empty;
                diagnostics.Add(Diag(
                    M3uParseDiagnosticKind.MissingHeader, lineNumber, line,
                    "primeira linha não vazia não é #EXTM3U: entradas não ingeridas"));
                return Build(streams, diagnostics, valid, malformed, unusable, benign, ignored);
            }

            (string Line, int LineNumber)? pendingExtInf = null;
            (string Line, int LineNumber)? pendingStreamInf = null;

            for (var i = headerIndex + 1; i < lines.Length; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    parserFailure = true;
                    diagnostics.Add(Diag(
                        M3uParseDiagnosticKind.ParserFailure, i + 1, string.Empty,
                        "parsing cancelado"));
                    break;
                }

                if (stopwatch.Elapsed > _options.MaxParsingTime)
                {
                    parserFailure = true;
                    diagnostics.Add(Diag(
                        M3uParseDiagnosticKind.ParserFailure, i + 1, string.Empty,
                        $"parsing excedeu o tempo máximo ({_options.MaxParsingTime.TotalSeconds:0} s)"));
                    break;
                }

                var raw = lines[i];
                var line = CleanLine(raw);
                if (line.Length == 0)
                {
                    ignored++;
                    continue;
                }

                if (line.Length > _options.MaxFieldLength)
                {
                    malformed++;
                    diagnostics.Add(Diag(
                        M3uParseDiagnosticKind.Malformed, i + 1, line,
                        $"campo excede o comprimento máximo ({_options.MaxFieldLength} caracteres)"));
                    pendingExtInf = null;
                    pendingStreamInf = null;
                    continue;
                }

                if (line[0] == '#')
                {
                    if (IsDirective(line, "#EXTINF"))
                    {
                        if (pendingExtInf is { } previous)
                        {
                            malformed++;
                            diagnostics.Add(Diag(
                                M3uParseDiagnosticKind.Malformed, previous.LineNumber, previous.Line,
                                "entrada #EXTINF sem URL válida (substituída por novo #EXTINF)"));
                        }

                        pendingExtInf = (line, i + 1);
                        pendingStreamInf = null;
                        continue;
                    }

                    if (IsDirective(line, "#EXT-X-STREAM-INF"))
                    {
                        // Metadados benignos. Não substitui um #EXTINF pendente: se
                        // existir, a URL seguinte continua a pertencer ao #EXTINF.
                        if (pendingExtInf is null)
                        {
                            pendingStreamInf = (line, i + 1);
                        }

                        benign++;
                        continue;
                    }

                    // Qualquer outra linha # (versão, comentário, #EXTGRP,
                    // #EXTVLCOPT, #KODIPROP, desconhecida) é metadado benigno.
                    benign++;
                    continue;
                }

                if (TryClassifyUrl(line, out var uri, out var isHttp, out var httpPrefixButBroken))
                {
                    if (isHttp)
                    {
                        if (pendingExtInf is null && pendingStreamInf is null)
                        {
                            malformed++;
                            diagnostics.Add(Diag(
                                M3uParseDiagnosticKind.Malformed, i + 1, line,
                                "URL http/https sem #EXTINF precedente"));
                            continue;
                        }

                        if (valid >= _options.MaxEntries)
                        {
                            parserFailure = true;
                            diagnostics.Add(Diag(
                                M3uParseDiagnosticKind.ParserFailure, i + 1, line,
                                $"número máximo de entradas excedido ({_options.MaxEntries})"));
                            break;
                        }

                        streams.Add(BuildStream(line, pendingExtInf?.Line, pendingStreamInf?.Line));
                        valid++;
                        pendingExtInf = null;
                        pendingStreamInf = null;
                        continue;
                    }

                    // Esquema absoluto não http/https (ex.: rtmp://).
                    if (pendingExtInf != null || pendingStreamInf != null)
                    {
                        unusable++;
                        diagnostics.Add(Diag(
                            M3uParseDiagnosticKind.UnusableTarget, i + 1, line,
                            $"alvo com esquema '{uri!.Scheme}' não é http/https"));
                        pendingExtInf = null;
                        pendingStreamInf = null;
                    }
                    else
                    {
                        malformed++;
                        diagnostics.Add(Diag(
                            M3uParseDiagnosticKind.Malformed, i + 1, line,
                            "URL absoluta sem esquema http/https e sem #EXTINF precedente"));
                    }

                    continue;
                }

                if (httpPrefixButBroken)
                {
                    malformed++;
                    diagnostics.Add(Diag(
                        M3uParseDiagnosticKind.Malformed, i + 1, line,
                        "URL http/https inválida ou incompleta"));
                    pendingExtInf = null;
                    pendingStreamInf = null;
                    continue;
                }

                // Texto que não é '#' nem URL absoluto.
                if (pendingExtInf is { } stranded)
                {
                    malformed++;
                    diagnostics.Add(Diag(
                        M3uParseDiagnosticKind.Malformed, stranded.LineNumber, stranded.Line,
                        "entrada #EXTINF seguida de conteúdo que não é URL absoluto"));
                    pendingExtInf = null;
                }
                else
                {
                    ignored++;
                }
            }

            if (pendingExtInf is { } dangling)
            {
                malformed++;
                diagnostics.Add(Diag(
                    M3uParseDiagnosticKind.Malformed, dangling.LineNumber, dangling.Line,
                    "entrada #EXTINF sem URL no fim do documento"));
            }

            var result = Build(streams, diagnostics, valid, malformed, unusable, benign, ignored);
            if (parserFailure)
            {
                result.Status = M3uPlaylistStatus.Failed;
            }

            return result;
        }

        private static M3uParseResult Build(
            List<M3uStream> streams,
            List<M3uParseDiagnostic> diagnostics,
            int valid,
            int malformed,
            int unusable,
            int benign,
            int ignored)
        {
            var status = valid == 0
                ? M3uPlaylistStatus.Failed
                : (malformed > 0 || unusable > 0 ? M3uPlaylistStatus.Partial : M3uPlaylistStatus.Success);

            return new M3uParseResult
            {
                Streams = streams,
                Diagnostics = diagnostics,
                Status = status,
                ValidCount = valid,
                MalformedCount = malformed,
                UnusableTargetCount = unusable,
                BenignMetadataCount = benign,
                IgnoredCount = ignored,
            };
        }

        private static string NormalizeContent(string content)
        {
            var value = content;
            if (value.Length > 0 && value[0] == '\uFEFF')
            {
                value = value[1..];
            }

            return value.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static string CleanLine(string raw)
            => raw.Trim().TrimStart('\uFEFF').Trim();

        private static bool IsHeader(string line)
            => line.Equals(Header, StringComparison.OrdinalIgnoreCase);

        private static bool IsDirective(string line, string directive)
            => line.StartsWith(directive, StringComparison.OrdinalIgnoreCase);

        private static bool IsHttpScheme(string scheme)
            => scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
               || scheme.Equals("https", StringComparison.OrdinalIgnoreCase);

        private static bool TryClassifyUrl(
            string line, out Uri? uri, out bool isHttp, out bool httpPrefixButBroken)
        {
            uri = null;
            isHttp = false;
            httpPrefixButBroken = false;

            var hasSchemeSeparator = line.Contains("://", StringComparison.Ordinal);
            var parsed = hasSchemeSeparator && Uri.TryCreate(line, UriKind.Absolute, out uri) && uri is not null;
            if (parsed)
            {
                isHttp = IsHttpScheme(uri!.Scheme);
                return true;
            }

            if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                httpPrefixButBroken = true;
            }

            return false;
        }

        private static M3uStream BuildStream(string url, string? extInf, string? streamInf)
        {
            var stream = new M3uStream
            {
                Url = url,
                OriginalExtInf = extInf ?? string.Empty,
            };

            if (!string.IsNullOrWhiteSpace(extInf))
            {
                stream.Title = ExtractTitle(extInf);
                stream.Group = ExtractAttribute(extInf, "group-title");
                stream.Logo = ExtractAttribute(extInf, "tvg-logo");
                stream.OriginalTvgId = ExtractAttribute(extInf, "tvg-id");
            }
            else if (!string.IsNullOrWhiteSpace(streamInf))
            {
                stream.Title = ExtractAttribute(streamInf, "tvg-name");
                stream.Group = ExtractAttribute(streamInf, "group-title");
                stream.Logo = ExtractAttribute(streamInf, "tvg-logo");
                stream.OriginalTvgId = ExtractAttribute(streamInf, "tvg-id");
            }

            if (string.IsNullOrWhiteSpace(stream.Title))
            {
                stream.Title = ExtractFileName(url);
            }

            return stream;
        }

        private static M3uParseDiagnostic Diag(
            M3uParseDiagnosticKind kind, int lineNumber, string rawLine, string reason)
            => new()
            {
                Kind = kind,
                LineNumber = lineNumber,
                SanitizedLine = Sanitize(rawLine),
                Reason = reason,
            };

        private static string Sanitize(string line)
            => CredentialSanitizer.SanitizeUrl(CredentialSanitizer.SanitizeText(line ?? string.Empty));

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
