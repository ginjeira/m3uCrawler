using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using m3uCrawler.Models;
using TL;

namespace m3uCrawler.Services
{
    public class TelegramScraperService
    {
        private readonly WTelegram.Client _client;
        private readonly M3uCandidateDetector _detector = new();

        public RunReport? LastRunReport { get; private set; }

        public TelegramScraperService()
        {
            _client = new WTelegram.Client(Config);
        }

        private static readonly Dictionary<string, string> _fileConfig = LoadConfigFile();

        private static Dictionary<string, string> LoadConfigFile()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Procura o ficheiro junto ao executável e, em alternativa, na
            // pasta atual de trabalho (útil ao correr via "dotnet run").
            string[] candidatePaths =
            {
                Path.Combine(AppContext.BaseDirectory, "wtelegram.config"),
                Path.Combine(Directory.GetCurrentDirectory(), "wtelegram.config")
            };

            string? path = candidatePaths.FirstOrDefault(File.Exists);
            if (path == null) return dict;

            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

                int idx = trimmed.IndexOf('=');
                if (idx <= 0) continue;

                string key = trimmed[..idx].Trim();
                string value = trimmed[(idx + 1)..].Trim();
                dict[key] = value;
            }

            return dict;
        }

        private static string? Config(string what)
        {
            // "ask" no ficheiro significa pedir interativamente na consola
            if (_fileConfig.TryGetValue(what, out var value))
            {
                if (value.Equals("ask", StringComparison.OrdinalIgnoreCase))
                    return AskConsole($"{what}: ");
                return value;
            }

            return what switch
            {
                "verification_code" => AskConsole("Código de verificação: "),
                "password" => AskConsole("Password 2FA (se aplicável): "),
                _ => null
            };
        }

        private static string AskConsole(string prompt)
        {
            Console.Write(prompt);
            return Console.ReadLine() ?? "";
        }

        // Chamado pelo Program.cs para autenticar antes de pesquisar
        public async Task LoginAsync()
        {
            const int maxAttempts = 3;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var me = await _client.LoginUserIfNeeded();
                    Console.WriteLine($"Autenticado como: {(me?.username ?? me?.first_name ?? "(sem nome)")}");
                    return;
                }
                catch (RpcException ex) when (ex.Code == 420 && ex.Message.Contains("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase))
                {
                    int waitSeconds = ExtractFloodWaitSeconds(ex.Message);
                    Console.WriteLine($"Flood control no login: a aguardar {waitSeconds}s antes de tentar novamente ({attempt}/{maxAttempts})...");
                    await Task.Delay(TimeSpan.FromSeconds(waitSeconds + 1));
                }
            }

            throw new Exception("Falha de autenticação no Telegram devido a FLOOD_WAIT após múltiplas tentativas. Aguarde alguns minutos e tente novamente.");
        }

        // Resultado legível de uma pesquisa (mantido para compatibilidade de API).
        public async Task<List<string>> SearchM3UInTelegram(string keyword, int limit = 200, int historyHours = 48)
        {
            var (_, candidates) = await SearchM3UInTelegramInternal(keyword, limit, historyHours);
            return candidates
                .Select(c => $"{c.Source} :: {Display(c)}")
                .ToList();
        }

        // Representação segura de um candidato para logs/relatórios/UI (sanitiza credenciais).
        private static string Display(CandidatePlaylist c)
        {
            if (!string.IsNullOrEmpty(c.FileName)) return c.FileName;
            if (!string.IsNullOrWhiteSpace(c.Url)) return CredentialSanitizer.SanitizeUrl(c.Url);
            return "(inline)";
        }

        // Pipeline principal: descobre candidatos, obtém conteúdo, valida país e
        // testa os streams. Devolve os streams funcionais E o relatório detalhado.
        public async Task<(List<M3uStream> Working, RunReport Report)> SearchAndTestM3UInTelegramAsync(
            string keyword,
            int limit = 200,
            int maxConcurrency = 5,
            int maxUrlsToTest = 500,
            int historyHours = 48,
            string countryCode = "pt",
            string? countriesDir = null,
            RunReport? report = null)
        {
            var rep = report ?? new RunReport();
            rep.StartedAt = DateTime.UtcNow;
            rep.Status = "running";

            var (messagesAnalyzed, candidates) = await SearchM3UInTelegramInternal(keyword, limit, historyHours);
            rep.MessagesAnalyzed = messagesAnalyzed;
            rep.CandidatesFound = candidates.Count;
            LastRunReport = rep;

            var countriesRoot = countriesDir
                ?? Path.Combine(Directory.GetCurrentDirectory(), "runtime-data", "countries");
            var validator = new CountryChannelValidator(countriesRoot);
            var parser = new M3uParserService();
            var tester = new M3uTesterService();

            var working = new List<M3uStream>();

            try
            {
                for (int ci = 0; ci < candidates.Count; ci++)
                {
                    var candidate = candidates[ci];
                    string? content = candidate.Content;
                    if (content == null)
                    {
                        content = await DownloadPlaylistContentAsync(candidate.Url);
                    }

                    // URLs sem extensão (.m3u/.m3u8) detetados por heurística só são tratados como
                    // playlist se o conteúdo HTTP for efectivamente #EXTM3U.
                    // Caso contrario, pode ser uma publicacao HTML com cards Xtream
                    // (resolver dedicado identifica contas e faz fan-out).
                    if (candidate.RequiresContentVerification && !_detector.LooksLikePlaylistContent(content))
                    {
                        if (LooksLikeHtmlPublication(content))
                        {
                            var accounts = XtreamPublicationResolver.ResolveFromHtml(
                                content!, candidate.Url ?? string.Empty);
                            if (accounts.Count == 0)
                            {
                                // Pagina HTML sem cards Xtream validas: nao incrementa
                                // PlaylistsInvalid (a pagina existe; simplesmente nao e
                                // uma publicacao Xtream). Apenas diagnostico sanitizado.
                                rep.RejectionReasons.Add(
                                    $"{CredentialSanitizer.SanitizeUrl(candidate.Url) ?? candidate.Source}: no xtream cards found");
                                continue;
                            }

                            // Fan-out: cada conta -> CandidatePlaylist com playlist Xtream.
                            foreach (var acc in accounts)
                            {
                                var playlistUrl = acc.M3uUrl ?? BuildXtreamPlaylistUrl(acc);
                                var promoted = PromoteXtreamAccount(playlistUrl, candidate.Url ?? string.Empty);
                                if (promoted != null)
                                {
                                    candidates.Add(promoted);
                                }
                            }
                            continue;
                        }

                        rep.PlaylistsInvalid++;
                        rep.RejectionReasons.Add($"{CredentialSanitizer.SanitizeUrl(candidate.Url) ?? candidate.Source}: conteúdo não é uma playlist M3U");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(content))
                    {
                        rep.PlaylistsInvalid++;
                        rep.RejectionReasons.Add($"{Display(candidate)}: playlist indisponível ou vazia");
                        continue;
                    }

                    rep.PlaylistsDownloaded++;

                    var analysis = validator.AnalyzePlaylist(content, countryCode, 3);
                    var discovered = new DiscoveredPlaylist
                    {
                        Source = candidate.Source,
                        Name = Display(candidate),
                        CountryDetected = analysis.IsTargetCountry ? countryCode : string.Empty,
                        ChannelsRecognized = analysis.RecognizedChannelCount,
                        State = analysis.IsTargetCountry ? "accepted" : "rejected"
                    };

                    if (!analysis.IsTargetCountry)
                    {
                        rep.PlaylistsRejected++;
                        rep.RejectionReasons.Add(
                            $"{discovered.Name}: país {countryCode.ToUpperInvariant()} não corresponde " +
                            $"(canais reconhecidos {analysis.RecognizedChannelCount}/3)");
                        rep.DiscoveredPlaylists.Add(discovered);
                        continue;
                    }

                    rep.CountryMatches++;
                    rep.ChannelsRecognized += analysis.RecognizedChannelCount;

                    var streams = parser.Parse(content);
                    discovered.StreamCount = streams.Count;
                    rep.StreamsExtracted += streams.Count;

                    // Gate per-stream (pipeline per-canal/per-stream, desde 2026-08-30).
                    // AnalyzePlaylist actua apenas como fast-reject acima; a aprovação final
                    // dos streams exige que cada um seja individualmente validado contra os
                    // aliases do país. Streams rejeitados aqui nunca chegam a TestStreamsAsync.
                    var (countryStreams, countryRejected) = FilterStreamsByCountry(
                        validator, streams, countryCode);
                    discovered.StreamsAfterCountryFilter = countryStreams.Count;
                    rep.StreamsAfterCountryFilter += countryStreams.Count;
                    rep.StreamsRejectedByCountry += countryRejected;

                    if (countryStreams.Count == 0)
                    {
                        rep.PlaylistsRejected++;
                        rep.RejectionReasons.Add(
                            $"{discovered.Name}: país {countryCode.ToUpperInvariant()} validado na playlist " +
                            $"(aliases={analysis.RecognizedChannelCount}) mas nenhum stream individual do país " +
                            $"(matched={streams.Count - countryRejected}/{streams.Count})");
                        rep.DiscoveredPlaylists.Add(discovered);
                        continue;
                    }

                    var tested = await TestStreamsAsync(tester, countryStreams, maxConcurrency, maxUrlsToTest);
                    rep.StreamsTested += tested.Count;
                    rep.StreamsWorking += tested.Count(s => s.IsWorking);
                    rep.StreamsFailed += tested.Count(s => !s.IsWorking);
                    discovered.WorkingStreams = tested.Count(s => s.IsWorking);

                    working.AddRange(tested.Where(s => s.IsWorking));
                    rep.DiscoveredPlaylists.Add(discovered);
                }
            }
            finally
            {
                tester.Dispose();
            }

            rep.FinishedAt = DateTime.UtcNow;
            rep.DurationMs = (long)(rep.FinishedAt - rep.StartedAt).TotalMilliseconds;
            rep.Status = "completed";

            Console.WriteLine(
                $"Pipeline Telegram: mensagens={rep.MessagesAnalyzed} candidatos={rep.CandidatesFound} " +
                $"playlists={rep.PlaylistsDownloaded} país={rep.CountryMatches} " +
                $"streams extraídos={rep.StreamsExtracted} após filtro país={rep.StreamsAfterCountryFilter} " +
                $"rejeitados país={rep.StreamsRejectedByCountry} testados={rep.StreamsTested} funcionais={rep.StreamsWorking}");

            return (working, rep);
        }

        // Wrapper que preserva a assinatura pública anterior (devolve só os streams funcionais).
        public async Task<List<M3uStream>> SearchAndTestM3UInTelegram(
            string keyword, int limit = 200, int maxConcurrency = 5, int maxUrlsToTest = 500, int historyHours = 48)
        {
            var (working, _) = await SearchAndTestM3UInTelegramAsync(
                keyword, limit, maxConcurrency, maxUrlsToTest, historyHours, "pt", null, null);
            return working;
        }

        private async Task<(int MessagesAnalyzed, List<CandidatePlaylist> Candidates)> SearchM3UInTelegramInternal(
            string keyword, int limit = 200, int historyHours = 48)
        {
            var candidates = new List<CandidatePlaylist>();
            int messagesAnalyzed = 0;

            // Idempotente: se já autenticado, não faz nada
            var me = await _client.LoginUserIfNeeded();
            Console.WriteLine($"Autenticado como: {(me?.username ?? me?.first_name ?? "(sem nome)")}");

            var dialogsBase = await _client.Messages_GetAllDialogs();

            Dialog[] dialogList;
            Dictionary<long, ChatBase> chatsDict;
            Dictionary<long, User> usersDict;

            switch (dialogsBase)
            {
                case Messages_Dialogs md:
                    dialogList = md.dialogs.OfType<Dialog>().ToArray();
                    chatsDict = md.chats;
                    usersDict = md.users;
                    break;
                default:
                    Console.WriteLine($"Nenhum diálogo encontrado (tipo de resposta: {dialogsBase?.GetType().Name}).");
                    return (0, candidates);
            }

            foreach (var dialog in dialogList)
            {
                var peer = dialog.Peer;

                object? resolvedPeer = peer switch
                {
                    PeerUser pu when usersDict.TryGetValue(pu.user_id, out var u) => u,
                    PeerChat pc when chatsDict.TryGetValue(pc.chat_id, out var c) => c,
                    PeerChannel pch when chatsDict.TryGetValue(pch.channel_id, out var c) => c,
                    _ => null
                };

                if (resolvedPeer == null) continue;

                string chatTitle = resolvedPeer switch
                {
                    ChatBase chat => chat.Title,
                    User user => user.username ?? user.first_name ?? "Utilizador",
                    _ => "Chat"
                };

                int offsetId = 0;
                var cutoffDate = DateTime.UtcNow.AddHours(-historyHours);
                bool reachedCutoff = false;

                while (!reachedCutoff)
                {
                    Messages_MessagesBase? history = null;

                    while (history == null)
                    {
                        try
                        {
                            history = resolvedPeer switch
                            {
                                User user => await _client.Messages_GetHistory(
                                    user, offset_id: offsetId, offset_date: default,
                                    add_offset: 0, limit: 100, max_id: 0, min_id: 0),
                                ChatBase chat => await _client.Messages_GetHistory(
                                    chat, offset_id: offsetId, offset_date: default,
                                    add_offset: 0, limit: 100, max_id: 0, min_id: 0),
                                _ => null
                            };
                        }
                        catch (WTelegram.WTException ex) when (ex.Message.Contains("FLOOD_WAIT"))
                        {
                            int waitSeconds = ExtractFloodWaitSeconds(ex.Message);
                            Console.WriteLine($"Flood control: a aguardar {waitSeconds}s antes de continuar...");
                            await Task.Delay(TimeSpan.FromSeconds(waitSeconds + 1));
                        }
                    }

                    if (history?.Messages == null || history.Messages.Length == 0)
                        break;

                    foreach (var msgBase in history.Messages)
                    {
                        if (msgBase is not Message m) continue;

                        if (m.date < cutoffDate)
                        {
                            reachedCutoff = true;
                            break;
                        }

                        messagesAnalyzed++;

                        // Em TL atual a legenda de um media é o próprio texto da mensagem.
                        string text = m.message ?? "";
                        string filename = "";

                        if (m.media is MessageMediaDocument mediaDoc &&
                            mediaDoc.document is Document doc)
                        {
                            foreach (var attr in doc.attributes)
                            {
                                if (attr is DocumentAttributeFilename fn)
                                    filename = fn.file_name ?? "";
                            }
                        }

                        // Descoberta NÃO depende da keyword: deteta por URL, nome de anexo ou conteúdo.
                        var found = _detector.DetectFromMessage(text, filename).ToList();

                        // URLs HTTP genericas (nao captadas pelo detector) sao candidatas a
                        // publicacao HTML com cards Xtream. Marcadas para que o loop principal
                        // encaminhe para XtreamPublicationResolver quando o conteudo nao for
                        // #EXTM3U.
                        foreach (var pubUrl in ExtractRemainingHttpUrls(text, found))
                        {
                            found.Add(new CandidatePlaylist
                            {
                                Kind = CandidateSourceKind.Url,
                                Url = pubUrl,
                                SourceText = text,
                                DetectedFrom = "xtream publication url",
                                RequiresContentVerification = true
                            });
                        }

                        bool hasAttachment = m.media is MessageMediaDocument media2 && media2.document is Document;
                        Document? attachmentDocument = hasAttachment
                            ? (Document)((MessageMediaDocument)m.media!).document
                            : null;

                        await ProcessAttachmentCandidatesAsync(
                            found, hasAttachment, filename, text, async () =>
                            {
                                if (attachmentDocument == null) return null;
                                return await DownloadTelegramDocumentTextAsync(attachmentDocument);
                            });

                        foreach (var candidate in found)
                        {
                            candidate.Source = chatTitle;
                            candidates.Add(candidate);
                        }
                    }

                    offsetId = history.Messages.Last().ID;

                    if (reachedCutoff || history.Messages.Length < 100) break;

                    await Task.Delay(300);
                }

                await Task.Delay(500);
            }

            return (messagesAnalyzed, candidates);
        }

        internal static async Task ProcessAttachmentCandidatesAsync(
            List<CandidatePlaylist> found,
            bool hasAttachment,
            string filename,
            string text,
            Func<Task<string?>> downloader)
        {
            // Materializa a vista filtrada ANTES de iterar para que o `Add` que ocorre dentro
            // do loop (quando o conteúdo do anexo começa por #EXTM3U) não invalide o enumerador
            // activo. Sem esta materialização, mutar `found` durante o `foreach (var c in found.Where(...))`
            // dispara InvalidOperationException: Collection was modified; enumeration operation may not execute.
            var attachmentsNeedingDownload = found
                .Where(c => c.Kind == CandidateSourceKind.Attachment && c.Content == null)
                .ToList();

            if (!hasAttachment) return;

            foreach (var candidate in attachmentsNeedingDownload)
            {
                try
                {
                    var attachmentText = await downloader();
                    candidate.Content = attachmentText;

                    if (!string.IsNullOrWhiteSpace(attachmentText) &&
                        new M3uCandidateDetector().LooksLikePlaylistContent(attachmentText) &&
                        !found.Any(x => x.DetectedFrom == "#EXTM3U content"))
                    {
                        found.Add(new CandidatePlaylist
                        {
                            Kind = CandidateSourceKind.Attachment,
                            FileName = filename,
                            SourceText = text,
                            Content = attachmentText,
                            DetectedFrom = "#EXTM3U content"
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Falha ao processar anexo '{filename}': {ex.Message}");
                }
            }
        }

        private async Task<string?> DownloadPlaylistContentAsync(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                client.DefaultRequestHeaders.Add("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                return await client.GetStringAsync(url);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Falha ao descarregar playlist '{CredentialSanitizer.SanitizeUrl(url)}': {ex.Message}");
                return null;
            }
        }

        private async Task<string?> DownloadTelegramDocumentTextAsync(Document document)
        {
            using var ms = new MemoryStream();
            await _client.DownloadFileAsync(document, ms);

            ms.Position = 0;
            using var reader = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync();
        }

        private async Task<List<M3uStream>> TestStreamsAsync(
            M3uTesterService tester, List<M3uStream> streams, int maxConcurrency, int maxUrlsToTest)
        {
            var toTest = (maxUrlsToTest > 0 ? streams.Take(maxUrlsToTest) : streams).ToList();
            if (toTest.Count == 0) return new List<M3uStream>();

            var semaphore = new SemaphoreSlim(maxConcurrency);
            var tasks = toTest.Select(async s =>
            {
                await semaphore.WaitAsync();
                try
                {
                    return await tester.TestM3u8Stream(s.Url, s.Title, s.Group);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            return (await Task.WhenAll(tasks)).ToList();
        }

        // Filtra streams individuais pelo país alvo usando CountryChannelValidator.ValidateStreams.
        // Devolve a lista de streams aceites (na MESMA REFERÊNCIA dos originais — sem cópias)
        // e o número de streams rejeitados. Usada pelo pipeline desde 2026-08-30 para que
        // apenas streams pertencentes ao país pesquisado cheguem a TestStreamsAsync.
        internal static (List<M3uStream> Accepted, int Rejected) FilterStreamsByCountry(
            CountryChannelValidator validator, List<M3uStream> streams, string countryCode)
{
    var matches = validator.ValidateStreams(streams, countryCode);
    var accepted = matches.Select(m => m.Stream).ToList();
    int rejected = streams.Count - accepted.Count;
    return (accepted, rejected);
}

        // Funde streams existentes (re-testados) com os novos funcionais, dedupundivos por URL
        // e priorizando os funcionais. Usado por --telegram-maintain e testável sem Telegram.
        public static List<M3uStream> MergeStreams(List<M3uStream> existing, List<M3uStream> fresh)
        {
            var merged = new Dictionary<string, M3uStream>(StringComparer.OrdinalIgnoreCase);

            foreach (var stream in existing)
            {
                if (!string.IsNullOrWhiteSpace(stream.Url))
                    merged[stream.Url] = stream;
            }

            foreach (var stream in fresh.Where(s => s.IsWorking))
            {
                if (!string.IsNullOrWhiteSpace(stream.Url))
                    merged[stream.Url] = stream;
            }

            return merged.Values.ToList();
        }

        private static int ExtractFloodWaitSeconds(string message)
        {
            // Mensagens do tipo "FLOOD_WAIT_26" ou "A wait of 26 seconds is required"
            var digits = new string(message.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var seconds) && seconds > 0)
            {
                return seconds;
            }

            // Algumas exceções chegam mascaradas como FLOOD_WAIT_X sem número.
            return message.Contains("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase) ? 180 : 30;
        }

        // ====================================================================
        // Publication HTML -> Xtream accounts (descoberta por fan-out)
        // ====================================================================

        private static readonly Regex _httpUrlRegexPublication = new(
            @"https?://[^\s<>""'()]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Quando o M3uCandidateDetector captura uma URL Xtream (/live/USER/PASS/...),
        // emite um candidato com Url=<get.php resolvido>. Para evitar republicar
        // a URL original como candidata a publicacao, tambem a marcamos como
        // "ja detetada".
        private static readonly Regex _xtreamServerUrlForPublication = new(
            @"https?://[^/\s]+/(live|movie|series)/[^/\s]+/[^/\s]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Extrai URLs HTTP/HTTPS do texto que NAO foram capturadas pelo detector.
        /// Usado para identificar URLs de publicacao HTML que o detector, por
        /// design, nao classifica como playlist (URLs sem pista 'xtream|playlist|m3u|...'
        /// no path/query). Apenas estas URLs chegam ao XtreamPublicationResolver.
        /// </summary>
        internal static IReadOnlyList<string> ExtractRemainingHttpUrls(
            string? text,
            IReadOnlyList<CandidatePlaylist> alreadyDetected)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

            var detectedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in alreadyDetected)
            {
                if (!string.IsNullOrWhiteSpace(c.Url)) detectedUrls.Add(c.Url);
            }

            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in _httpUrlRegexPublication.Matches(text))
            {
                var url = m.Value.TrimEnd('.', ',', ')', ']', ';');
                if (url.Length == 0) continue;
                if (seen.Contains(url)) continue;
                // Ja detetada directamente (m3u, xtream playlist).
                if (detectedUrls.Contains(url)) continue;
                // Ja detetada indirectamente (xtream server: o Url do candidato e'
                // a playlist resolvida, mas a URL original ja foi consumida).
                if (_xtreamServerUrlForPublication.IsMatch(url)) continue;
                seen.Add(url);
                result.Add(url);
            }
            return result;
        }

        /// <summary>
        /// Reconhece se um conteudo descarregado parece uma publicacao HTML.
        /// NAO valida estrutura Xtream (isso e' tarefa do XtreamPublicationResolver).
        /// </summary>
        internal static bool LooksLikeHtmlPublication(string? content)
        {
            if (string.IsNullOrWhiteSpace(content)) return false;
            var trimmed = content.TrimStart();
            return trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<HTML", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Constroi a URL da playlist Xtream para uma conta, reutilizando a logica
        /// canonica existente em M3uCandidateDetector.ResolveXtreamPlaylistUrl.
        /// Cria uma URL de servidor sintetica (/live/USER/PASS/0.ts) e deixa o
        /// resolver canonico produzir a URL de playlist (get.php). Garante que
        /// existe uma UNICA forma de construir URLs Xtream no projeto.
        /// </summary>
        private static string? BuildXtreamPlaylistUrl(XtreamAccountInfo acc)
        {
            var detector = new M3uCandidateDetector();
            var scheme = string.IsNullOrWhiteSpace(acc.Scheme) ? "http" : acc.Scheme.ToLowerInvariant();
            var syntheticServer = $"{scheme}://{acc.Host}:{acc.Port}/live/{Uri.EscapeDataString(acc.Username)}/{Uri.EscapeDataString(acc.Password)}/0.ts";
            return detector.ResolveXtreamPlaylistUrl(syntheticServer);
        }

        /// <summary>
        /// Promove uma conta Xtream descoberta a CandidatePlaylist, pronta para
        /// entrar no pipeline Xtream/M3U existente. O Source e' o URL publico da
        /// publicacao (sem credenciais) para que DiscoveredPlaylists/RunReport nao
        /// exponham segredos.
        /// </summary>
        internal static CandidatePlaylist? PromoteXtreamAccount(string? playlistUrl, string publicationUrl)
        {
            if (string.IsNullOrWhiteSpace(playlistUrl)) return null;
            return new CandidatePlaylist
            {
                Kind = CandidateSourceKind.Url,
                Url = playlistUrl,
                Source = $"xtream publication: {publicationUrl}",
                DetectedFrom = "xtream publication",
                RequiresContentVerification = true
            };
        }
    }
}
