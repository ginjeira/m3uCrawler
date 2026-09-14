using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Validation;
using TL;

namespace m3uCrawler.Services
{
    public class TelegramScraperService
    {
        private readonly WTelegram.Client? _client;
        private readonly M3uCandidateDetector _detector = new();

        public RunReport? LastRunReport { get; private set; }

        /// <summary>
        /// Construtor padrão: lê <c>wtelegram.config</c> e instancia o
        /// <see cref="WTelegram.Client"/> a partir dele. Requer credenciais reais
        /// para descoberta em produção.
        /// </summary>
        public TelegramScraperService()
        {
            _client = new WTelegram.Client(Config);
        }

        /// <summary>
        /// Construtor para testes — permite passar um <c>WTelegram.Client</c>
        /// injectado (ou <c>null</c> em testes unitários que não precisam de
        /// autenticação Telegram). Os métodos públicos não dependem do cliente
        /// para <c>IngestIntoCatalogAsync</c>.
        /// </summary>
        public TelegramScraperService(WTelegram.Client? client)
        {
            _client = client;
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
                    var me = await _client!.LoginUserIfNeeded();
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
        public async Task<List<string>> SearchM3UInTelegram(string keyword, int limit = 200, int historyHours = 24)
        {
            // Compatibilidade de API antiga: delega via canal sem producer
            // (apenas para display da lista de candidatos).
            var captured = new List<CandidatePlaylist>();
            await SearchM3UInTelegramInternal(keyword, limit, historyHours, onCandidateProduced: c => captured.Add(c));
            return captured
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
            int historyHours = 24,
            string countryCode = "pt",
            string? countriesDir = null,
            RunReport? report = null,
            PipelineIngestionService? pipelineIngestor = null,
            string? pipelineSourceKey = null,
            CancellationToken cancellationToken = default)
        {
            var rep = report ?? new RunReport();
            rep.StartedAt = DateTime.UtcNow;
            rep.Status = "running";

            // ==== PIPELINE-INC (2026-09-14): processamento incremental ====
            // Cada candidate descoberto entra imediatamente no processamento,
            // sem esperar que todos os dialogos sejam enumerados. A 110751 era
            // detectada as 10:50:16 mas o primeiro teste de playlist so'
            // corria as 11:03:30 (latencia artificial de ~13 min). Com esta
            // mudanca o latency reduz-se a (download+teste) / parallelism.
            //
            // Topologia:
            //   producer:  SearchM3UInTelegramInternal(...) emite
            //              cada candidate via callback 'onCandidateProduced'
            //              para o writer do channel.
            //   consumer:  worker Task.Run le do channel.Reader e processa
            //              cada candidate com concurrency = maxConcurrency.
            //
            // Invariantes preservados (R1/R2/9A):
            // - Mesmo validator/parser/tester/integration usados.
            // - Order de processador dos candidates NEM sempre preservada
            //   (esta e' a aceitavel trade-off; definida como 'no máximo
            //   o limite da janela').
            // - Counter rep.CandidatesFound e' actualizado incrementalmente
            //   em ProcessOneTelegramMessageAsync (mantem-se a semantica).
            var candidateChannel = System.Threading.Channels.Channel.CreateUnbounded<CandidatePlaylist>(
                new System.Threading.Channels.UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false
                });

            var countriesRoot = countriesDir
                ?? Path.Combine(Directory.GetCurrentDirectory(), "runtime-data", "countries");
            var validator = new CountryChannelValidator(countriesRoot);
            var parser = new M3uParserService();
            // 9A-PROD-WIRING: tester criado via factory. Quando existe
            // um stream_validation_policy.json no runtime-data, este
            // tester usa a policy persistida. Caso contrario, usa
            // defaults.
            var tester = StreamValidationTesterFactory.CreateTester(
                TryLoadSharedValidationState() ?? StreamValidationTesterFactory.CreateIsolatedState());

            var working = new List<M3uStream>();
            var workingLock = new object();
            var processingDone = new TaskCompletionSource();

            // Consumer/worker: le do canal e processa com maxConcurrency.
            var worker = Task.Run(async () =>
            {
                var semaphore = new SemaphoreSlim(Math.Max(1, maxConcurrency));
                var activeProcessing = new List<Task>();
                try
                {
                    await foreach (var c in candidateChannel.Reader.ReadAllAsync(cancellationToken))
                    {
                        await semaphore.WaitAsync(cancellationToken);
                        var t = Task.Run(async () =>
                        {
                            try
                            {
                                await ProcessCandidateAsync(
                                    c, tester, parser, validator, countryCode, rep,
                                    maxUrlsToTest, maxConcurrency,
                                    candidateChannel.Writer, working, workingLock,
                                    cancellationToken);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                // Cancelamento explicito; nada a registar.
                            }
                            catch (Exception ex)
                            {
                                // Falha no processamento de UM candidate NAO mata o pipeline.
                                // Continua para os proximos.
                                rep.RejectionReasons.Add($"{Display(c)}: processing exception {ex.GetType().Name}");
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        }, cancellationToken);
                        activeProcessing.Add(t);
                    }
                    await Task.WhenAll(activeProcessing);
                }
                finally
                {
                    semaphore.Dispose();
                    processingDone.TrySetResult();
                }
            }, cancellationToken);

            // Producer: arranca a enumearacao Telegram com callback de emissao.
            int messagesAnalyzed = 0;
            Exception? enumEx = null;
            try
            {
                messagesAnalyzed = await SearchM3UInTelegramInternal(
                    keyword, limit, historyHours, rep,
                    onCandidateProduced: c =>
                    {
                        // channel.Writer.TryWrite e' non-blocking (unboundedChannel).
                        if (!candidateChannel.Writer.TryWrite(c))
                        {
                            rep.RejectionReasons.Add($"{Display(c)}: candidate channel write failed");
                        }
                    });
            }
            catch (Exception ex)
            {
                enumEx = ex;
            }
            finally
            {
                candidateChannel.Writer.Complete();
            }

            // Esperar pelo worker terminar.
            await processingDone.Task;
            if (enumEx != null) Console.WriteLine($"⚠️ {nameof(SearchM3UInTelegramInternal)} exit: {enumEx.GetType().Name}: {enumEx.Message}");

            rep.MessagesAnalyzed = messagesAnalyzed;
            // rep.CandidatesFound e' incrementado dentro de ProcessOneTelegramMessageAsync.
            LastRunReport = rep;

            try
            {
                // PIPELINE-INC: o worker acima ja' consumiu e processou cada
                // candidate dentro de ProcessCandidateAsync. Nada mais a iterar aqui.
                _ = worker;
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

            // PHASE-Bridge — Ingerir no catálogo persistente. Só é chamado
            // se o caller fornecer um ingestor (parâmetro opcional para
            // preservar compatibilidade com callers de teste que não
            // precisam de catálogo).
            if (pipelineIngestor != null)
            {
                var sourceKey = pipelineSourceKey
                    ?? $"telegram-{Slugify(keyword)}";
                try
                {
                    var ingestionResult = await pipelineIngestor.IngestAsync(
                        working, sourceKey, "Telegram", countryCode, cancellationToken);
                    Console.WriteLine(
                        $"📥 Ingestão no catálogo: {ingestionResult.IngestedCount}/{ingestionResult.ReceivedCount} " +
                        $"streams → source='{sourceKey}', matched={ingestionResult.MatchedCount}, " +
                        $"auto-created={ingestionResult.AutoCreatedCount}");
                }
                catch (Exception ex)
                {
                    // Falha na ingestão não aborta o pipeline — o catálogo
                    // é uma camada adicional, não substitui o ficheiro
                    // playlist.m3u existente.
                    Console.WriteLine($"⚠️ Ingestão no catálogo falhou (não fatal): {ex.Message}");
                }
            }

            return (working, rep);
        }

        // Wrapper que preserva a assinatura pública anterior (devolve só os streams funcionais).
        public async Task<List<M3uStream>> SearchAndTestM3UInTelegram(
            string keyword, int limit = 200, int maxConcurrency = 5, int maxUrlsToTest = 500, int historyHours = 24)
        {
            var (working, _) = await SearchAndTestM3UInTelegramAsync(
                keyword, limit, maxConcurrency, maxUrlsToTest, historyHours, "pt", null, null);
            return working;
        }

        // ==== PIPELINE-INC (2026-09-14): processador de UM candidate ====
        // Extrado do loop legado de SearchAndTestM3UInTelegramAsync. Agora corre
        // dentro do worker (consumo do Channel<CandidatePlaylist>). Promove
        // novos candidates Xtream (fan-out de HTML) voltando a submete-los
        // ao writer do canal, preservando a semantica online.
        private async Task ProcessCandidateAsync(
            CandidatePlaylist candidate,
            M3uTesterService tester,
            M3uParserService parser,
            CountryChannelValidator validator,
            string countryCode,
            RunReport rep,
            int maxUrlsToTest,
            int maxConcurrency,
            System.Threading.Channels.ChannelWriter<CandidatePlaylist> writer,
            List<M3uStream> working,
            object workingLock,
            CancellationToken cancellationToken)
        {
            // PIPELINE-INC-HARDENING (2026-09-14): cada instancia de ProcessCandidateAsync
            // corre na sua propria task; ate `maxConcurrency` tasks em paralelo
            // escrevem em counters/listas deste mesmo RunReport.
            //
            // Contadores inteiros:
            //   ++ NAO e' atomico em C# (sem volatile, sem memory barrier). Em
            //   loops onde duas tasks fazem ++x simultaneamente, uma das
            //   actualizacoes pode perder-se. Usamos Interlocked.Increment
            //   para garantir atomicidade.
            //
            // List<>.Add:
            //   NAO e' thread-safe (internamente chama Array.Resize). Se
            //   duas tasks adicionarem em paralelo, pode corromper o array
            //   (InvalidOperationException) ou perder items. Lock no
            //   RunReport.SyncRoot antes de Add.
            //
            // Nao tocamos:
            //   - CountryChannelValidator (per-candidate, nao partilhado
            //     em mutacao). O validador pode ser reentrant; e' imutavel.
            //   - M3uParserService.Parse (estatico, sem estado mutavel).
            //   - StreamValidationTesterFactory e StreamValidationCache
            //     (os testes de streams sao single-threaded dentro de
            //     TestStreamsAsync que tem `maxConcurrency` interno).
            //   - ChannelWriter.TryWrite (NAO pede lock; e' lock-free).
            string? content = candidate.Content;
            if (content == null)
            {
                content = await DownloadPlaylistContentAsync(candidate.Url, tester);
            }

            // URL sem extensao (.m3u/.m3u8): detetada por heuristica. So' tratada
            // como playlist se o conteudo HTTP for de facto #EXTM3U. Caso
            // contrario pode ser uma publicacao HTML com cards Xtream.
            if (candidate.RequiresContentVerification && content != null && !_detector.LooksLikePlaylistContent(content))
            {
                if (LooksLikeHtmlPublication(content))
                {
                    var accounts = XtreamPublicationResolver.ResolveFromHtml(
                        content!, candidate.Url ?? string.Empty);
                    if (accounts.Count == 0)
                    {
                        AddRejection(rep, $"{CredentialSanitizer.SanitizeUrl(candidate.Url) ?? candidate.Source}: no xtream cards found");
                        return;
                    }
                    foreach (var acc in accounts)
                    {
                        var playlistUrl = acc.M3uUrl ?? BuildXtreamPlaylistUrl(acc);
                        var promoted = PromoteXtreamAccount(playlistUrl, candidate.Url ?? string.Empty);
                        if (promoted != null)
                        {
                            // Re-injecta no canal. ChannelWriter.TryWrite e' non-blocking.
                            if (!writer.TryWrite(promoted))
                            {
                                AddRejection(rep, $"{Display(promoted)}: channel write failed");
                            }
                        }
                    }
                    return;
                }
                Interlocked.Increment(ref rep._PlaylistsInvalid);
                AddRejection(rep, $"{CredentialSanitizer.SanitizeUrl(candidate.Url) ?? candidate.Source}: conteúdo não é uma playlist M3U");
                return;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                Interlocked.Increment(ref rep._PlaylistsInvalid);
                AddRejection(rep, $"{Display(candidate)}: playlist indisponível ou vazia");
                return;
            }

            Interlocked.Increment(ref rep._PlaylistsDownloaded);

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
                Interlocked.Increment(ref rep._PlaylistsRejected);
                AddRejection(rep,
                    $"{discovered.Name}: país {countryCode.ToUpperInvariant()} não corresponde " +
                    $"(canais reconhecidos {analysis.RecognizedChannelCount}/3)");
                AddDiscovered(rep, discovered);
                return;
            }

            Interlocked.Increment(ref rep._CountryMatches);
            Interlocked.Add(ref rep._ChannelsRecognized, analysis.RecognizedChannelCount);

            var streams = parser.Parse(content);
            discovered.StreamCount = streams.Count;
            Interlocked.Add(ref rep._StreamsExtracted, streams.Count);

            // Gate per-stream (pipeline per-canal/per-stream, desde 2026-08-30).
            // AnalyzePlaylist actua apenas como fast-reject acima; a aprovacao final
            // dos streams exige que cada um seja individualmente validado contra os
            // aliases do pais. Streams rejeitados aqui nunca chegam a TestStreamsAsync.
            var (countryStreams, countryRejected) = FilterStreamsByCountry(
                validator, streams, countryCode);
            discovered.StreamsAfterCountryFilter = countryStreams.Count;
            Interlocked.Add(ref rep._StreamsAfterCountryFilter, countryStreams.Count);
            Interlocked.Add(ref rep._StreamsRejectedByCountry, countryRejected);

            if (countryStreams.Count == 0)
            {
                Interlocked.Increment(ref rep._PlaylistsRejected);
                AddRejection(rep,
                    $"{discovered.Name}: país {countryCode.ToUpperInvariant()} validado na playlist " +
                    $"(aliases={analysis.RecognizedChannelCount}) mas nenhum stream individual do país " +
                    $"(matched={streams.Count - countryRejected}/{streams.Count})");
                AddDiscovered(rep, discovered);
                return;
            }

            var tested = await TestStreamsAsync(tester, countryStreams, maxConcurrency, maxUrlsToTest);
            Interlocked.Add(ref rep._StreamsTested, tested.Count);
            Interlocked.Add(ref rep._StreamsWorking, tested.Count(s => s.IsWorking));
            Interlocked.Add(ref rep._StreamsFailed, tested.Count(s => !s.IsWorking));
            discovered.WorkingStreams = tested.Count(s => s.IsWorking);

            lock (workingLock)
            {
                working.AddRange(tested.Where(s => s.IsWorking));
            }
            // discovered precisa de ser adicionado ao RunReport sob lock
            // (lista partilhada).
            AddDiscovered(rep, discovered);
        }

        // PIPELINE-INC-HARDENING: helpers thread-safe para escrita em
        // List<>.Add de RunReport. Usam RunReport.SyncRoot.
        private static void AddRejection(RunReport rep, string reason)
        {
            lock (rep.SyncRoot)
            {
                rep.RejectionReasons.Add(reason);
            }
        }

        private static void AddDiscovered(RunReport rep, DiscoveredPlaylist playlist)
        {
            lock (rep.SyncRoot)
            {
                rep.DiscoveredPlaylists.Add(playlist);
            }
        }

        private async Task<int> SearchM3UInTelegramInternal(
            string keyword, int limit = 200, int historyHours = 24, RunReport? report = null,
            Action<CandidatePlaylist>? onCandidateProduced = null)
        {
            var candidates = new List<CandidatePlaylist>();
            // Publicacoes descobertas em qualquer mensagem: referencias Telegram
            // (t.me/c/...) e URLs HTTP publicas nao capturadas pelo detector.
            // Sao resolvidas apos o loop principal, usando o cache de access_hash
            // construido a partir dos dialogos.
            var discoveredPublications = new List<TelegramPublicationRef>();
            int messagesAnalyzed = 0;

            // Idempotente: se já autenticado, não faz nada
            var me = await _client!.LoginUserIfNeeded();
            Console.WriteLine($"Autenticado como: {(me?.username ?? me?.first_name ?? "(sem nome)")}");

            var dialogsBase = await _client.Messages_GetAllDialogs();

            Dialog[] dialogList;
            Dictionary<long, ChatBase> chatsDict;
            Dictionary<long, User> usersDict;

            Channel? ResolveChannel(long channelId)
            {
                if (chatsDict.TryGetValue(channelId, out var ch) && ch is Channel c) return c;
                return null;
            }

            switch (dialogsBase)
            {
                case Messages_Dialogs md:
                    dialogList = md.dialogs.OfType<Dialog>().ToArray();
                    chatsDict = md.chats;
                    usersDict = md.users;
                    break;
                default:
                    Console.WriteLine($"Nenhum diálogo encontrado (tipo de resposta: {dialogsBase?.GetType().Name}).");
                    return 0;
            }

            // ==== R1 (2026-09-13): cutoff UNICO por ciclo ====
            // A implementacao anterior calculava DateTime.UtcNow.AddHours(-historyHours)
            // dentro de foreach (var dialog in dialogList), o que deslocava o cutoff
            // por dialogo. Agora o cutoff e' calculado UMA unica vez antes de iterar.
            // Se um dialogo demora muito a processar, mensagens no limite da janela
            // continuam elegiveis.
            var cycleCutoff = DateTime.UtcNow.AddHours(-historyHours);
            if (report != null) report.DialogsTotal = dialogList.Length;

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

                // Identifica peer id e tipo para telemetria em caso de erro.
                long? peerId = peer switch
                {
                    PeerUser pu => pu.user_id,
                    PeerChat pc => pc.chat_id,
                    PeerChannel pch => pch.channel_id,
                    _ => null
                };
                string peerType = peer switch
                {
                    PeerUser => "User",
                    PeerChat => "Chat",
                    PeerChannel => "Channel",
                    _ => "Unknown"
                };

                int processedBeforeDialog = messagesAnalyzed;

                // ==== R2 (2026-09-13): isolar erros de Messages_GetHistory ====
                // Antes, qualquer excepcao nao-FLOOD_WAIT (e.g. RpcError 500) matava
                // o ciclo. Agora o try/catch interno ao dialogo permite continuar
                // para os dialogos seguintes. O dialogo e' registado como incompleto
                // em RunReport.DialogErrors.
                try
                {
                    await EnumerateDialogHistoryAsync(
                        resolvedPeer,
                        chatTitle,
                        cycleCutoff,
                        async (offsetId) =>
                        {
                            return resolvedPeer switch
                            {
                                User user => await _client.Messages_GetHistory(
                                    user, offset_id: offsetId, offset_date: default,
                                    add_offset: 0, limit: 100, max_id: 0, min_id: 0),
                                ChatBase chat => await _client.Messages_GetHistory(
                                    chat, offset_id: offsetId, offset_date: default,
                                    add_offset: 0, limit: 100, max_id: 0, min_id: 0),
                                _ => null
                            };
                        },
                        async (msg) =>
                        {
                            // Processa uma mensagem que passou o filtro temporal.
                            messagesAnalyzed++;
                            await ProcessOneTelegramMessageAsync(
                                msg, chatTitle, report, discoveredPublications, candidates,
                                onCandidateProduced);
                        });
                }
                catch (WTelegram.WTException ex) when (ex.Message.Contains("FLOOD_WAIT"))
                {
                    // FLOOD_WAIT e' tratado no helper interno (com retry). Se
                    // escapar ate aqui, e' um caso muito excepcional; regista
                    // como incompleto mas NAO mata o ciclo.
                    RecordDialogIncomplete(report, chatTitle, peerId, peerType, ex,
                        offsetId: -1, processedBeforeDialog, messagesAnalyzed);
                }
                catch (Exception ex)
                {
                    // RpcError 500, IOException, SocketException, RpcException,
                    // qualquer outra falha de enumacao. Regista e continua.
                    int lastOffset = ExtractLastOffsetFromExceptionMessage(ex.Message);
                    RecordDialogIncomplete(report, chatTitle, peerId, peerType, ex,
                        offsetId: lastOffset, processedBeforeDialog, messagesAnalyzed);
                }

                await Task.Delay(500);
            }

            // Resolver publicacoes Telegram descobertas (t.me/c/...) usando o
            // canal cache construido a partir dos dialogos. URLs HTTP publicas
            // capturadas pelo discovery sao processadas pelo loop principal
            // (download HTTP -> XtreamPublicationResolver). Apenas referencias
            // Telegram precisam do passo de resolucao aqui.
            var telegramRefs = discoveredPublications
                .Where(p => p.ChannelId.HasValue && p.MessageId.HasValue)
                .ToList();
            if (telegramRefs.Count > 0)
            {
                var fetcher = BuildTelegramFetcher(ResolveChannel);
                var resolutions = await TelegramPublicationResolver.ResolveAsync(
                    telegramRefs, fetcher);

                // Promover cada conta Xtream descoberta a CandidatePlaylist e
                // anexar ao mesmo loop do pipeline.
                foreach (var res in resolutions)
                {
                    if (res.XtreamAccounts.Count == 0) continue;
                    foreach (var acc in res.XtreamAccounts)
                    {
                        var playlistUrl = acc.M3uUrl ?? BuildXtreamPlaylistUrl(acc);
                        var promoted = PromoteXtreamAccount(playlistUrl, res.ReferenceUrl);
                        if (promoted != null)
                        {
                            candidates.Add(promoted);
                        }
                    }
                }
            }

            return messagesAnalyzed;
        }

        // ==== Helper R1+R2 (2026-09-13): iteracao testavel ====
        // EnumerateDialogHistoryAsync itera paginacao de um dialogo ate'
        // atingir o cutoff temporal. Isolado para ser testado sem rede.
        //
        // Parametros:
        //   - resolvedPeer: ignorado (mantido por simetria da API anterior);
        //     a identificacao real do peer ja' foi feita no caller.
        //   - chatTitle: identificador legivel (usado apenas em logs).
        //   - cutoffDate: cutoff temporal UNICO por ciclo (R1).
        //   - pageFetcher: delegate que devolve a proxima pagina de
        //     mensagens para um dado offsetId. Pode lancar excepcoes
        //     (RpcError, IOException, FLOOD_WAIT, etc.).
        //   - onMessage: callback async invocado por cada mensagem que
        //     passou o filtro temporal. NAO e' invocado para mensagens
        //     anteriores ao cutoff.
        //
        // Comportamento:
        //   - Paginas sao obtidas em batches de 100.
        //   - Mensagens sao processadas em ordem descendente de data
        //     (ordem natural do Messages_GetHistory).
        //   - O loop termina quando:
        //     (a) uma mensagem com m.date < cutoffDate e' encontrada, ou
        //     (b) o servidor devolve pagina vazia / menor que 100, ou
        //     (c) pageFetcher devolve null, ou
        //     (d) pageFetcher lanca uma excepcao (propagada ao caller).
        //   - FLOOD_WAIT e' tratado dentro do helper (com retry).
        //   - Outras excepcoes sao propagadas.
        internal static async Task EnumerateDialogHistoryAsync(
            object resolvedPeer,
            string chatTitle,
            DateTime cutoffDate,
            Func<int, Task<Messages_MessagesBase?>> pageFetcher,
            Func<Message, Task> onMessage)
        {
            int offsetId = 0;
            bool reachedCutoff = false;

            while (!reachedCutoff)
            {
                Messages_MessagesBase? history = null;

                while (history == null)
                {
                    try
                    {
                        history = await pageFetcher(offsetId);
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

                    await onMessage(m);
                }

                offsetId = history.Messages.Last().ID;

                if (reachedCutoff || history.Messages.Length < 100) break;

                await Task.Delay(300);
            }
        }

        // ==== Processamento de UMA mensagem (R1+R2 refactor) ====
        // Extraido do foreach original para manter o helper de iteracao
        // pequeno e testavel. Contem toda a logica que era inline:
        // telemetria de media, detector, ProcessAttachmentCandidates,
        // adicao de candidates e publications descobertas.
        private async Task ProcessOneTelegramMessageAsync(
            Message m,
            string chatTitle,
            RunReport? report,
            List<TelegramPublicationRef> discoveredPublications,
            List<CandidatePlaylist> candidates,
            Action<CandidatePlaylist>? onCandidateProduced)
        {
            // Em TL atual a legenda de um media é o próprio texto da mensagem.
            string text = m.message ?? "";
            string filename = "";

            // ==== Telemetria 2026-09-12: diagnosticar silent-drop de documentos HTML ====
            string mediaType = m.media switch
            {
                MessageMediaDocument => "Document",
                MessageMediaPhoto => "Photo",
                null => "None",
                _ => "Other"
            };
            Document? telemetryDoc = null;
            long docSize = -1;
            string? docMime = null;
            int docAttributes = 0;

            if (m.media is MessageMediaDocument mediaDocTelemetry &&
                mediaDocTelemetry.document is Document docTelemetry)
            {
                telemetryDoc = docTelemetry;
                docSize = docTelemetry.size;
                docMime = docTelemetry.mime_type;
                docAttributes = docTelemetry.attributes?.Length ?? 0;
            }

            if (mediaType != "None")
            {
                if (report != null) report.MessagesWithMedia++;
                if (mediaType == "Document") { if (report != null) report.MessagesWithDocumentMedia++; }
                else if (mediaType == "Photo") { if (report != null) report.MessagesWithPhotoMedia++; }
            }

            if (m.media is MessageMediaDocument mediaDoc &&
                mediaDoc.document is Document doc)
            {
                foreach (var attr in doc.attributes)
                {
                    if (attr is DocumentAttributeFilename fn)
                        filename = fn.file_name ?? "";
                }

                if (!string.IsNullOrWhiteSpace(filename))
                {
                    if (report != null) report.DocumentsWithFilename++;
                }
                else
                {
                    if (report != null) report.DocumentsWithoutFilename++;
                }
            }

            // [TelegramDetect]
            if (m.media != null)
            {
                Console.WriteLine(
                    $"[TelegramDetect] messageId={m.ID} date={m.date:o} " +
                    $"mediaType={mediaType} documentType={telemetryDoc?.GetType().Name ?? "<n/a>"} " +
                    $"filename='{TruncateForLog(filename, 128)}' " +
                    $"mimeType='{docMime ?? "<n/a>"}' size={docSize} " +
                    $"captionLength={text.Length} docAttributes={docAttributes}");

                if (IsTelemetryTarget(filename, text))
                {
                    Console.WriteLine(
                        $"[TelegramDetectTarget] messageId={m.ID} date={m.date:o} " +
                        $"mediaType={mediaType} filename='{TruncateForLog(filename, 128)}' " +
                        $"mimeType='{docMime ?? "<n/a>"}' size={docSize}");
                }
            }

            // Descoberta NAO depende da keyword.
            var found = _detector.DetectFromMessage(text, filename).ToList();

            if (m.media != null)
            {
                var htmlCount = found.Count(c => c.DetectedFrom == "html attachment");
                if (report != null) report.HtmlCandidatesCreated += htmlCount;
                Console.WriteLine(
                    $"[TelegramDetectResult] messageId={m.ID} filename='{TruncateForLog(filename, 128)}' " +
                    $"candidates={found.Count}");
            }

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

            var pubs = TelegramPublicationDiscovery.DiscoverFromText(text, chatTitle);
            foreach (var p in pubs)
            {
                if (!discoveredPublications.Any(d =>
                        d.ReferenceUrl == p.ReferenceUrl &&
                        d.ChannelId == p.ChannelId &&
                        d.MessageId == p.MessageId))
                {
                    discoveredPublications.Add(p);
                }
            }

            bool hasAttachment = m.media is MessageMediaDocument media2 && media2.document is Document;
            Document? attachmentDocument = hasAttachment
                ? (Document)((MessageMediaDocument)m.media!).document
                : null;

            // Note: ProcessAttachmentCandidatesAsync is async.
            await ProcessAttachmentCandidatesAsync(
                found, hasAttachment, filename, text, async () =>
                {
                    if (attachmentDocument == null) return null;
                    return await DownloadTelegramDocumentTextAsync(
                        attachmentDocument,
                        filenameForLog: filename,
                        messageIdForLog: m.ID);
                },
                report: report,
                messageId: m.ID);

            foreach (var candidate in found)
            {
                candidate.Source = chatTitle;
                candidates.Add(candidate);
                // Candidados devem ser contados incrementalmente (consistente com
                // a semantica anterior: rep.CandidatesFound = candidates.Count
                // no fim do pipeline; agora definido no momento da producao).
                if (report != null) report.CandidatesFound++;
                onCandidateProduced?.Invoke(candidate);
            }
        }

        // ==== Registo de dialogo incompleto (R2) ====
        // Chamado quando o EnumerateDialogHistoryAsync lanca uma excepcao
        // nao-FLOOD_WAIT. Incrementa DialogsIncomplete e adiciona um
        // DialogError ao RunReport com o contexto da falha.
        private static void RecordDialogIncomplete(
            RunReport? report,
            string chatTitle,
            long? peerId,
            string peerType,
            Exception ex,
            int offsetId,
            int messagesBeforeDialog,
            int currentMessagesAnalyzed)
        {
            int processed = currentMessagesAnalyzed - messagesBeforeDialog;

            if (report != null)
            {
                report.DialogsIncomplete++;
                report.DialogErrors.Add(new DialogError
                {
                    ChatTitle = chatTitle,
                    PeerId = peerId,
                    PeerType = peerType,
                    ExceptionType = ex.GetType().Name,
                    ExceptionMessage = TruncateForLog(ex.Message, 256),
                    FailedAtOffsetId = offsetId,
                    MessagesProcessedInDialog = processed,
                    TimestampUtc = DateTime.UtcNow
                });
            }

            Console.WriteLine(
                $"[TelegramDialogError] chatTitle='{TruncateForLog(chatTitle, 64)}' " +
                $"peerType={peerType} peerId={peerId?.ToString() ?? "<n/a>"} " +
                $"failedAtOffsetId={offsetId} messagesProcessedInDialog={processed} " +
                $"exceptionType={ex.GetType().Name} message='{TruncateForLog(ex.Message, 128)}'");
        }

        // Tenta extrair o offsetId do contexto de uma mensagem de exceccao
        // do WTelegram. Devolve -1 se nao for possivel.
        private static int ExtractLastOffsetFromExceptionMessage(string exceptionMessage)
        {
            // O WTelegram normalmente inclui o codigo da mensagem afectada
            // (e.g. "AFFECTED_MSG_ID=12345"). E' heuristico e tolerante
            // a falhas - devolve -1 quando nao reconhece o padrao.
            if (string.IsNullOrEmpty(exceptionMessage)) return -1;

            const string tag = "AFFECTED_MSG_ID=";
            int idx = exceptionMessage.IndexOf(tag, StringComparison.Ordinal);
            if (idx >= 0)
            {
                int start = idx + tag.Length;
                int end = start;
                while (end < exceptionMessage.Length && char.IsDigit(exceptionMessage[end]))
                    end++;
                if (end > start && int.TryParse(exceptionMessage[start..end], out var v))
                    return v;
            }
            return -1;
        }

        /// <summary>
        /// Constrói um TelegramMessageFetcher que invoca WTelegram
        /// Channels_GetMessages / Messages_GetMessages com o (channelId, messageId)
        /// recebido, usando o cache de dialogos para obter access_hash. Devolve
        /// null se a mensagem nao for acessivel (canal nao nos dialogos, FLOOD_WAIT
        /// persistente, etc.).
        /// </summary>
        private TelegramMessageFetcher BuildTelegramFetcher(Func<long, Channel?> resolveChannel)
        {
            return async (long channelId, int messageId, CancellationToken ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var channel = resolveChannel(channelId);
                if (channel == null) return null;
                try
                {
                    var inputChannel = new InputChannel(channel.id, channel.access_hash);
                    var ids = new InputMessage[] { new InputMessageID { id = messageId } };
                    var response = await _client.Channels_GetMessages(inputChannel, ids);
                    if (response is Messages_ChannelMessages mcm && mcm.messages != null && mcm.messages.Length > 0)
                    {
                        var msg = mcm.messages[0] as Message;
                        if (msg == null) return null;
                        return await BuildResolvedFromMessage(msg);
                    }
                    return null;
                }
                catch (WTelegram.WTException ex) when (ex.Message.Contains("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase))
                {
                    // Re-throw para que o resolver trate FLOOD_WAIT com retry.
                    throw;
                }
                catch (WTelegram.WTException)
                {
                    // CHANNEL_INVALID, MESSAGE_ID_INVALID, etc. Devolve null
                    // para que o resolver marque a publicacao como ResolutionFailed.
                    return null;
                }
            };
        }

        /// <summary>
        /// Converte um TL.Message (obtido via WTelegram) num ResolvedPublication
        /// para o TelegramPublicationResolver. Suporta texto + media (Document).
        /// </summary>
        private async Task<ResolvedPublication?> BuildResolvedFromMessage(Message m)
        {
            var text = m.message ?? string.Empty;

            string? filename = null;
            byte[]? mediaContent = null;
            string kind = "text";

            if (m.media is MessageMediaDocument mediaDoc && mediaDoc.document is Document doc)
            {
                foreach (var attr in doc.attributes)
                {
                    if (attr is DocumentAttributeFilename fn)
                    {
                        filename = fn.file_name ?? string.Empty;
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(filename))
                {
                    kind = ClassifyAttachmentForFetcher(filename);
                    try
                    {
                        // Usa o mesmo helper com telemetria completa que o
                        // caminho principal de download, garantindo que
                        // downloads truncados NAO chegam ao parser.
                        var content = await DownloadTelegramDocumentTextAsync(
                            doc,
                            filenameForLog: filename,
                            messageIdForLog: m.ID);
                        if (content != null)
                        {
                            // Re-encoda em bytes para o resolver (compat).
                            mediaContent = System.Text.Encoding.UTF8.GetBytes(content);
                        }
                        else
                        {
                            // truncated/failed -> sem media content.
                            mediaContent = null;
                        }
                    }
                    catch
                    {
                        // Download falhou; sem media content. O resolver fara'
                        // o seu trabalho so' com o texto.
                        mediaContent = null;
                    }
                }
            }

            return new ResolvedPublication
            {
                Text = text,
                Filename = filename,
                MediaContent = mediaContent,
                Kind = kind,
                ChannelId = m.Peer is PeerChannel pc ? pc.channel_id : null,
                MessageId = m.ID
            };
        }

        private static string ClassifyAttachmentForFetcher(string filename)
        {
            var f = filename.ToLowerInvariant();
            if (System.Text.RegularExpressions.Regex.IsMatch(f, @"\.html?$")) return "html attachment";
            if (System.Text.RegularExpressions.Regex.IsMatch(f, @"\.m3u8?$")) return "m3u attachment";
            return "other attachment";
        }

        internal static async Task ProcessAttachmentCandidatesAsync(
            List<CandidatePlaylist> found,
            bool hasAttachment,
            string filename,
            string text,
            Func<Task<string?>> downloader,
            RunReport? report = null,
            int? messageId = null)
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

                    // ==== Telemetria 2026-09-12 ====
                    // A linha detalhada com expected/actual/status e' emitida
                    // por DownloadTelegramDocumentTextAsync. Aqui apenas
                    // incrementamos o counter e marcamos como success
                    // (a linha de truncamento ja' foi emitida pelo helper).
                    if (report != null) report.DocumentDownloadSuccesses++;

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
                    // ==== Telemetria 2026-09-12 ====
                    // O counter e o log detalhado ja' foram emitidos pelo
                    // helper DownloadTelegramDocumentTextAsync quando
                    // o erro vem do caminho principal. Aqui apenas
                    // incrementamos o counter e damos uma mensagem
                    // humana. Para o caminho de publicacao (t.me/c/),
                    // o helper ja' foi chamado dentro do delegate.
                    if (report != null) report.DocumentDownloadFailures++;
                    Console.WriteLine($"Falha ao processar anexo '{filename}': {ex.Message}");
                }
            }
        }

        // Trunca uma string para logging, sem expor credenciais.
        // Conservative: tambem remove newlines que quebrariam o formato key=value.
        internal static string TruncateForLog(string? value, int maxLen)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var compact = value.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
            if (compact.Length <= maxLen) return compact;
            return compact[..maxLen] + "...";
        }

        // Determina se uma mensagem deve ser marcada como alvo prioritario
        // na telemetria (i.e., potencialmente relacionada com a publicacao
        // de IPTV reportada). NAO filtra — apenas sinaliza.
        internal static bool IsTelemetryTarget(string? filename, string? caption)
        {
            if (!string.IsNullOrEmpty(filename))
            {
                if (filename.Contains("204.52.191.254", StringComparison.Ordinal)) return true;
                if (filename.Contains("RATTENPAPST", StringComparison.Ordinal)) return true;
                if (filename.Contains("97111a99", StringComparison.Ordinal)) return true;
                if (filename.Contains("6dcd9b4bd6", StringComparison.Ordinal)) return true;
            }
            if (!string.IsNullOrEmpty(caption))
            {
                if (caption.Contains("204.52.191.254", StringComparison.Ordinal)) return true;
                if (caption.Contains("RATTENPAPST", StringComparison.Ordinal)) return true;
                if (caption.Contains("97111a99ffbe", StringComparison.Ordinal)) return true;
                if (caption.Contains("6dcd9b4bd6", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private async Task<string?> DownloadPlaylistContentAsync(string? url, M3uTesterService tester)
        {
            // PHASE 9A: reusa o HttpClient partilhado e o OverallTimeout
            // do M3uTesterService em vez de criar um HttpClient com
            // timeout fixo de 30s sem CancellationToken (que era o
            // comportamento original e podia bloquear a iteração
            // quando o servidor remoto aceitava a conexão mas não
            // respondia).
            var (content, ok) = await tester.DownloadPlaylistContentAsync(url);
            if (!ok)
            {
                if (!string.IsNullOrWhiteSpace(url))
                {
                    Console.WriteLine($"Falha ao descarregar playlist '{CredentialSanitizer.SanitizeUrl(url)}': timeout ou erro de rede");
                }
                return null;
            }
            return content;
        }

        // Faz download de um Document via WTelegram.Client.DownloadFileAsync
        // e devolve o texto (UTF-8) se o download for COMPLETO.
        //
        // Telemetria 2026-09-14 (investigacao de truncamento):
        //   expectedBytes  = document.size (declarado pelo Telegram)
        //   actualBytes    = ms.Length (recebido)
        //   status         = complete | truncated | unexpected | failed
        //   - complete:    actualBytes == expectedBytes (> 0)
        //   - truncated:   actualBytes <  expectedBytes (download incompleto)
        //   - unexpected:  actualBytes >  expectedBytes (improvavel)
        //   - failed:      exception ou actualBytes == 0
        //
        // Devolve null em todos os casos que nao sejam complete para
        // que o caller nao faca parse de HTML truncado.
        //
        // NOTA: passamos fileSize=document.size para activar a
        // validacao interna do WTelegram.Client.
        private async Task<string?> DownloadTelegramDocumentTextAsync(
            Document document,
            string filenameForLog,
            int? messageIdForLog = null)
        {
            var expected = document.size;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            long chunks = 0;
            long lastTransmitted = 0;

            try
            {
                using var ms = new MemoryStream();
                using var cts = new CancellationTokenSource();
                // Hard timeout 5 minutos para o download completo.
                // Documento de 22 MB a 1 MB/s -> ~22s; deixamos margem
                // generosa para evitar falsos positivos.
                cts.CancelAfter(TimeSpan.FromMinutes(5));

                // Progress callback: cada chamada = 1 chunk completo.
                // transmitted e' cumulativo (desde 0).
                // Cancelamento via cts.ThrowIfCancellationRequested() — a
                // WTelegram propaga a excepcao como WTException.
                WTelegram.Client.ProgressCallback progress = (transmitted, total) =>
                {
                    chunks++;
                    lastTransmitted = transmitted;
                    if (transmitted >= expected && expected > 0) return;
                    cts.Token.ThrowIfCancellationRequested();
                };

                // Usamos a overload base InputFileLocationBase para passar
                // fileSize explicitamente (activa a validacao interna da
                // lib: cada chunk deve ter FilePartSize bytes excepto o
                // ultimo) e o progress callback.
                //
                // document.ToFileLocation() devolve um
                // InputDocumentFileLocation que implementa
                // InputFileLocationBase. Tambem ha overloads Document-typed
                // mas nao suportam fileSize.
                await _client!.DownloadFileAsync(
                    fileLocation: document.ToFileLocation(),
                    outputStream: ms,
                    dc_id: 0,
                    fileSize: expected,
                    progress: progress).ConfigureAwait(false);

                sw.Stop();
                var actual = ms.Length;

                if (actual == 0)
                {
                    LogDownloadOutcome(filenameForLog, messageIdForLog, expected, 0,
                        status: "failed", durationMs: sw.ElapsedMilliseconds,
                        chunks: (int)chunks, error: "empty stream");
                    return null;
                }

                if (expected > 0 && actual < expected)
                {
                    LogDownloadOutcome(filenameForLog, messageIdForLog, expected, actual,
                        status: "truncated", durationMs: sw.ElapsedMilliseconds,
                        chunks: (int)chunks, error: null);
                    return null;
                }

                if (expected > 0 && actual > expected)
                {
                    LogDownloadOutcome(filenameForLog, messageIdForLog, expected, actual,
                        status: "unexpected", durationMs: sw.ElapsedMilliseconds,
                        chunks: (int)chunks, error: null);
                    // Em unexpected ainda tentamos usar o conteudo.
                }

                LogDownloadOutcome(filenameForLog, messageIdForLog, expected, actual,
                    status: "complete", durationMs: sw.ElapsedMilliseconds,
                    chunks: (int)chunks, error: null);

                ms.Position = 0;
                using var reader = new StreamReader(ms, Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true);
                return await reader.ReadToEndAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                LogDownloadOutcome(filenameForLog, messageIdForLog, expected, lastTransmitted,
                    status: "failed", durationMs: sw.ElapsedMilliseconds,
                    chunks: (int)chunks, error: "OperationCanceledException");
                return null;
            }
            catch (Exception ex)
            {
                sw.Stop();
                LogDownloadOutcome(filenameForLog, messageIdForLog, expected, lastTransmitted,
                    status: "failed", durationMs: sw.ElapsedMilliseconds,
                    chunks: (int)chunks, error: ex.GetType().Name);
                return null;
            }
        }

        // Imprime UMA linha estruturada por download com status,
        // bytes esperados/recebidos, duracao e motivo. Usada por
        // DownloadTelegramDocumentTextAsync e pela outra chamada
        // directa em BuildResolvedFromMessage (caso da publicacao
        // t.me/c/).
        internal static void LogDownloadOutcome(
            string filename,
            int? messageId,
            long expected,
            long actual,
            string status,
            long durationMs,
            int chunks,
            string? error)
        {
            Console.WriteLine(
                $"[TelegramDocumentDownload] messageId={messageId} " +
                $"filename='{TruncateForLog(filename, 128)}' " +
                $"expected={expected} actual={actual} " +
                $"status={status} durationMs={durationMs} chunks={chunks}" +
                (error is null ? "" : $" error={error}"));
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

        // Filtra streams existentes re-testados para retencao na playlist.
        //
        // Semantica (PHASE 9A): um stream existente e' mantido se
        //   - o teste actual passou (IsWorking == true); OU
        //   - o teste falhou com uma falha classificada como retryable
        //     por StreamFailureClassifier (Timeout, Network,
        //     ConnectionRefused, DnsFailure, HttpStatus429,
        //     HttpStatus5xx).
        //
        // Apenas falhas terminais/deterministic (404, 401/403, TLS,
        // Auth, etc.) levam a remocao. Esta regra evita que um
        // timeout transitorio de 12s apague a playlist inteira,
        // como observado em 2026-09-11 (48579 streams -> 25
        // streams apos reteste).
        //
        // Este helper e' isolado para ser testavel sem HTTP:
        // recebe uma sequencia de (M3uStream, StreamFailureKind)
        // e devolve as duas particoes (preserved, removedByKind).
        internal static FilterRetainedResult FilterRetainedStreams(
            IEnumerable<(M3uStream Stream, StreamFailureKind FailureKind)> retestOutcomes)
        {
            var preserved = new List<M3uStream>();
            var removedByKind = new Dictionary<string, int>(StringComparer.Ordinal);
            var preservedByKind = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var (stream, kind) in retestOutcomes)
            {
                var kindName = kind.ToString();
                if (stream.IsWorking || Validation.StreamFailureClassifier.IsRetryable(kind))
                {
                    preserved.Add(stream);
                    // Distingue Working de Retryable dentro dos preservados
                    // para o relatorio final.
                    var bucketKey = stream.IsWorking ? "Working" : kindName;
                    preservedByKind.TryGetValue(bucketKey, out var n);
                    preservedByKind[bucketKey] = n + 1;
                }
                else
                {
                    removedByKind.TryGetValue(kindName, out var n);
                    removedByKind[kindName] = n + 1;
                }
            }

            return new FilterRetainedResult(preserved, preservedByKind, removedByKind);
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

        // Tenta carregar o StreamValidationState partilhado a partir do
        // runtime-data. Devolve null se o directório não existir (e.g.
        // em testes).
        private static StreamValidationState? TryLoadSharedValidationState()
        {
            try
            {
                var runtimeDir = Path.Combine(Directory.GetCurrentDirectory(), "runtime-data");
                if (!Directory.Exists(runtimeDir)) return null;
                var store = new StreamValidationPolicyStore(runtimeDir);
                return StreamValidationTesterFactory.CreateStateFromStore(store);
            }
            catch
            {
                return null;
            }
        }

        // ====================================================================
        // Publication HTML -> Xtream accounts (descoberta por fan-out)
        // ====================================================================

        // URLs HTTP publicas normais. Exclui t.me/c/<channel>/<message> porque
        // essas referencias sao tratadas pelo TelegramPublicationResolver
        // (atribuida a publicacao Telegram, nao a pagina HTML publica).
        private static readonly Regex _httpUrlRegexPublication = new(
            @"https?://(?!t\.me/)[^\s<>""'()]+",
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
        internal static string? BuildXtreamPlaylistUrl(XtreamAccountInfo acc)
        {
            return BuildPlaylistUrlForTest(acc);
        }

        /// <summary>
        /// Wrapper publico-interno (mantido para testes) com nome mais curto.
        /// </summary>
        internal static string? BuildPlaylistUrlForTest(XtreamAccountInfo acc)
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

        /// <summary>
        /// Slugifica uma string para uso como parte de uma chave de Source.
        /// </summary>
        private static string Slugify(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "unknown";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (c == ' ' || c == '-' || c == '_') sb.Append('-');
            }
            var slug = sb.ToString().Trim('-');
            return string.IsNullOrEmpty(slug) ? "unknown" : slug;
        }

        /// <summary>
        /// PHASE-Bridge — Ingere os streams testados no catálogo
        /// persistente (Source/ChannelSource). Reutiliza o
        /// <see cref="m3uCrawler.Services.Catalog.PipelineIngestionService"/>
        /// já existente; este wrapper existe para que o caller do
        /// pipeline Telegram não precise de construir o ingestor.
        ///
        /// Idempotente e seguro em pipelines concorrentes (o
        /// <c>EnsureSourceAsync</c> e o <c>RecordChannelSourceAsync</c>
        /// são ambos upserts por chave natural).
        /// </summary>
        public Task<PipelineIngestionService.IngestionResult> IngestIntoCatalogAsync(
            IReadOnlyList<M3uStream> streams,
            string sourceKey,
            string sourceKindName,
            string countryCode,
            PipelineIngestionService ingestor,
            CancellationToken cancellationToken = default)
        {
            if (ingestor == null) throw new ArgumentNullException(nameof(ingestor));
            return ingestor.IngestAsync(streams, sourceKey, sourceKindName, countryCode, cancellationToken);
        }
    }

    /// <summary>
    /// Resultado de <see cref="TelegramScraperService.FilterRetainedStreams"/>:
    /// as particoes de streams preservados vs removidos apos re-teste,
    /// com contadores por StreamFailureKind para telemetria.
    /// </summary>
    public sealed record FilterRetainedResult(
        List<M3uStream> Preserved,
        Dictionary<string, int> PreservedByKind,
        Dictionary<string, int> RemovedByKind)
    {
        public int PreservedRetryable =>
            PreservedByKind.Where(kv => kv.Key != "Working").Sum(kv => kv.Value);

        public int PreservedWorking =>
            PreservedByKind.TryGetValue("Working", out var n) ? n : 0;

        public int RemovedTerminal => RemovedByKind.Sum(kv => kv.Value);
    }
}
