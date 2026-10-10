using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using m3uCrawler.Services.LiveRun;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.Recognition;
using m3uCrawler.Services.Telegram;
using m3uCrawler.Services.Validation;
using TL;

namespace m3uCrawler.Services
{
    public class TelegramScraperService
    {
        private readonly WTelegram.Client? _client;

        // Wave W5 — Caminho de aplicação: o cliente WTelegram vivo é
        // detido pelo TelegramAuthService (dashboard/scheduler) e lido a
        // cada execução. Quando presente, este scraper NUNCA constrói um
        // cliente próprio nem lê da consola.
        private readonly ITelegramClientProvider? _clientProvider;

        // Fallback legacy (CLI interactiva sem serviço de aplicação): o
        // cliente é criado preguiçosamente a partir do wtelegram.config
        // lido no momento (sem snapshot estático).
        private readonly object _fallbackClientGate = new();
        private WTelegram.Client? _fallbackClient;

        private readonly M3uCandidateDetector _detector = new();

        // EXPERIMENT-SERIAL-PER-XTREAM (2026-09-16): singleton do lock
        // manager usado por ProcessCandidateAsync para serializar o download
        // de candidatos Xtream promovidos por (URL, username). Nullable para
        // manter back-compat com testes que instanciam o servico directamente;
        // e' lazy-inicializado na primeira utilizacao.
        private m3uCrawler.Services.Validation.XtreamAccountLockManager? _xtreamAccountLocks;

        // PHASE-OBSERVABILITY (2026-09-15): sink de tracing opcional. Por
        // defeito e' NullTraceSink.Instance (no-op) para nao alterar
        // comportamento dos testes que instanciam directamente.
        private m3uCrawler.Services.Validation.ITraceSink _trace = m3uCrawler.Services.Validation.NullTraceSink.Instance;

        // D-M4-02 — resolvedor de policies (W5.1). Opcional; quando
        // configurado, o scraper lê a policy efectiva do snapshot do Run
        // (ILiveRunProgress.RunId) e propaga-a para a ingestão do
        // catálogo. Sem resolver => sem policy (B1: nunca fallback para
        // policy viva; sem fabrico).
        private m3uCrawler.Services.Recognition.RecognitionPolicyResolver? _recognitionPolicyResolver;

        // W2-FU-1 (2026-09-22) — resolvedor de catálogo opcional para
        // alimentar o observer de falhas de aquisição. Quando configurado
        // E o caller fornece um pipelineIngestor (caminho com ingestion),
        // o scraper instala um CatalogAcquisitionFailureObserver com
        // sourceId=null no tester — agrega em RunReport mas NÃO persiste
        // em Source (a identidade operacional Source<->peer/chat ainda
        // não está ligada; ver W2-FU-2 follow-up). Sem resolver, sem
        // observer: o comportamento legacy é preservado (no-op, sem
        // side-effects).
        private m3uCrawler.Services.Catalog.CatalogResolver? _catalogResolver;

        // Membros de afinidade Kind=Country (classificação de país), por
        // código ISO. Injectados na construção do CountryChannelValidator
        // deste scraper. Escopo de instância: não existe estado estático
        // partilhado entre processos/serviços. Country affinity NÃO cria
        // identidade de canal.
        private IReadOnlyDictionary<string, IEnumerable<string>> _countryAffinityMembers =
            new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);

        public RunReport? LastRunReport { get; private set; }

        // PHASE-OBSERVABILITY (2026-09-15): associa um sink de tracing
        // para observabilidade. Por defeito o sink e' NullTraceSink
        // (no-op). O servico permanece em conformidade com a sua API
        // publica (nao ha mudanca de comportamento).
        public void SetTrace(m3uCrawler.Services.Validation.ITraceSink trace)
        {
            _trace = trace ?? m3uCrawler.Services.Validation.NullTraceSink.Instance;
        }

        /// <summary>
        /// D-M4-02 — Injecta o resolvedor de policies de reconhecimento.
        /// Quando configurado, o scraper resolve a policy efectiva a
        /// partir do snapshot do Run (criado em <c>RunCoordinator</c>) e
        /// propaga-a para <see cref="PipelineIngestionService.IngestAsync"/>.
        /// <c>null</c> (default) preserva o comportamento anterior (sem
        /// policy ⇒ mesma forma da wave W5.1 com policy nula).
        /// </summary>
        public void SetRecognitionPolicyResolver(
            m3uCrawler.Services.Recognition.RecognitionPolicyResolver? resolver)
        {
            _recognitionPolicyResolver = resolver;
        }

        /// <summary>
        /// W2-FU-1 (2026-09-22) — Injecta o resolvedor de catálogo
        /// utilizado pelo observer de falhas de aquisição. Quando
        /// configurado E o caller fornece um <c>pipelineIngestor</c> (caminho
        /// com ingestion), <see cref="SearchAndTestM3UInTelegramAsync"/>
        /// instala um <see cref="Validation.CatalogAcquisitionFailureObserver"/>
        /// com <c>sourceId=null</c> no tester — agrega em
        /// <see cref="RunReport"/> mas NÃO persiste em <c>Source</c>.
        /// Passar <c>null</c> (default) preserva o comportamento legacy
        /// (sem observer, sem side-effects).
        /// </summary>
        public void SetCatalogResolver(
            m3uCrawler.Services.Catalog.CatalogResolver? resolver)
        {
            _catalogResolver = resolver;
        }

        /// <summary>
        /// Define os membros de afinidade <c>Kind=Country</c> usados pelo
        /// <see cref="CountryChannelValidator"/> criado em cada run. Chamado
        /// pela composição (Program.cs) após o catálogo estar disponível.
        /// Passar <c>null</c> repõe o estado vazio.
        /// </summary>
        public void SetCountryAffinityMembers(
            IReadOnlyDictionary<string, IEnumerable<string>>? members)
        {
            _countryAffinityMembers = members
                ?? new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// D-M4-02a — identidade operacional do Run para as ocorrências de
        /// descoberta (W1). Usa exclusivamente
        /// <see cref="ILiveRunProgress.RunId"/> (= <c>RunCoordinator.RunId</c>).
        ///
        /// <para>
        /// Sem Run operacional devolve <c>null</c>: não fabrica identidade e
        /// <b>nunca</b> usa <c>PipelineTrace.RunId</c> (diagnóstico) como
        /// fallback. Nesse caso mantém-se o comportamento conservador de não
        /// aplicar deduplicação baseada em Run (D-M4-01 B1).
        /// </para>
        /// </summary>
        internal static string? ResolveOperationalRunId(ILiveRunProgress? liveRunProgress)
        {
            var runId = liveRunProgress?.RunId;
            return string.IsNullOrWhiteSpace(runId) ? null : runId;
        }

        /// <summary>
        /// Construtor padrão (legacy CLI interactiva): não cria cliente de
        /// imediato. O cliente é criado preguiçosamente a partir do
        /// <c>wtelegram.config</c> lido no momento do login, permitindo
        /// que alterações ao ficheiro se apliquem sem reiniciar o
        /// processo. Requer credenciais reais para produção.
        /// </summary>
        public TelegramScraperService()
        {
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

        /// <summary>
        /// Wave W5 — Caminho de aplicação: reutiliza o <c>WTelegram.Client</c>
        /// vivo e autenticado detido pelo <see cref="ITelegramClientProvider"/>
        /// (o <c>TelegramAuthService</c> do dashboard). Não constrói um
        /// cliente de consola nem lê credenciais de <c>wtelegram.config</c>.
        /// </summary>
        public TelegramScraperService(ITelegramClientProvider clientProvider)
        {
            _clientProvider = clientProvider
                ?? throw new ArgumentNullException(nameof(clientProvider));
        }

        /// <summary>
        /// Wave W5 — Resolve o cliente a usar em cada operação Telegram.
        /// No caminho de aplicação valida a autenticação e devolve o
        /// cliente vivo do provider; no caminho legacy devolve o cliente
        /// injectado ou cria (uma vez) o fallback a partir do ficheiro.
        /// </summary>
        internal WTelegram.Client RequireClient()
        {
            if (_clientProvider is not null)
            {
                if (!_clientProvider.IsAuthenticated)
                    throw new TelegramNotAuthenticatedException();

                return _clientProvider.LiveClient ?? throw new TelegramNotAuthenticatedException();
            }

            if (_client is not null)
                return _client;

            lock (_fallbackClientGate)
            {
                _fallbackClient ??= new WTelegram.Client(Config);
                return _fallbackClient;
            }
        }

        // Wave W5 — somente para testes: leitura de um ficheiro de
        // configuração num directório explícito, sem snapshot estático.
        internal static Dictionary<string, string> LoadConfigFileFrom(string directory)
            => ParseConfigFile(Path.Combine(directory, "wtelegram.config"));

        private static Dictionary<string, string> LoadConfigFile()
        {
            // Procura o ficheiro junto ao executável e, em alternativa, na
            // pasta atual de trabalho (útil ao correr via "dotnet run").
            string[] candidatePaths =
            {
                Path.Combine(AppContext.BaseDirectory, "wtelegram.config"),
                Path.Combine(Directory.GetCurrentDirectory(), "wtelegram.config")
            };

            string? path = candidatePaths.FirstOrDefault(File.Exists);
            return path == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : ParseConfigFile(path);
        }

        private static Dictionary<string, string> ParseConfigFile(string path)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(path)) return dict;

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

        private string? Config(string what)
        {
            // Wave W5 — leitura sem snapshot estático: o ficheiro é
            // relido a cada pedido, pelo que alterações a
            // wtelegram.config se aplicam sem reiniciar o processo.
            var fileConfig = LoadConfigFile();

            // "ask" no ficheiro significa pedir interativamente na consola
            if (fileConfig.TryGetValue(what, out var value))
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
            // Wave W5 — caminho de aplicação: a autenticação já foi
            // conduzida pelo TelegramAuthService (dashboard). Não há
            // login interactivo nem leitura da consola. Se o serviço
            // ainda não estiver autenticado, falhar de forma explícita
            // (o arranque trata esta excepção sem terminar o processo).
            if (_clientProvider is not null)
            {
                if (_clientProvider.IsAuthenticated && _clientProvider.LiveClient is not null)
                    return;
                throw new TelegramNotAuthenticatedException();
            }

            var client = RequireClient();
            const int maxAttempts = 3;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var me = await client.LoginUserIfNeeded();
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
        // testa os streams. Devolve os streams funcionais, os streams
        // adquiridos (playlists funcionais para o país) e o relatório detalhado.
        //
        // `feedCanonicalFallback` (W-FEED): quando activo, dentro de uma
        // playlist que passou o filtro de país, um stream rejeitado por falta
        // de token de país é ainda aceite se resolver para um canal canónico
        // existente. Quando não há resolvedor de catálogo, é no-op.
        public async Task<(List<M3uStream> Working, List<M3uStream> Acquired, RunReport Report)> SearchAndTestM3UInTelegramAsync(
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
            ILiveRunProgress? liveRunProgress = null,
            CancellationToken cancellationToken = default,
            int minHistoryHours = 0,
            bool feedCanonicalFallback = DiscoverySettings.DefaultFeedCanonicalFallback)
        {
            var rep = report ?? new RunReport();
            rep.StartedAt = DateTime.UtcNow;
            rep.Status = "running";

            // PHASE 9C.4 — instrumentação da Live Run. Opcional por
            // design: sem monitor (null) o comportamento é exactamente
            // o actual. Os reportes são feitos nos mesmos pontos onde o
            // RunReport autoritativo já é actualizado.
            if (liveRunProgress is not null)
            {
                await liveRunProgress.EnterPhaseAsync(
                    LiveRunPhase.ReadingTelegram,
                    $"reading telegram messages (keyword='{keyword}', window {minHistoryHours}-{historyHours}h)",
                    cancellationToken)
                    .ConfigureAwait(false);
            }

            // PHASE-OBSERVABILITY (2026-09-15): tracing por run. O trace e'
            // opcional (null -> NullTraceSink) para manter back-compat. Em
            // producao, o Program.cs cria um PipelineTrace e associa-o a este
            // servico via setter; testes podem passar um CapturingTraceSink.
            var trace = _trace ?? m3uCrawler.Services.Validation.NullTraceSink.Instance;
            var runCtx = new m3uCrawler.Services.Validation.TraceContext { };
            trace.Information(m3uCrawler.Services.Validation.TraceCategory.RunStart, runCtx,
                $"keyword='{keyword}' limit={limit} maxConcurrency={maxConcurrency} maxUrlsToTest={maxUrlsToTest} historyHours={historyHours} countryCode={countryCode} minHistoryHours={minHistoryHours}");
            trace.Information(m3uCrawler.Services.Validation.TraceCategory.RunParameters, runCtx,
                $"source=Telegram countriesDir={countriesDir ?? "<default>"} pipelineIngestor={(pipelineIngestor != null ? "set" : "null")} pipelineSourceKey={pipelineSourceKey ?? "<null>"}");

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
            var validator = new CountryChannelValidator(countriesRoot, _countryAffinityMembers);
            var parser = new M3uParserService();
            // 9A-PROD-WIRING: tester criado via factory. Quando existe
            // um stream_validation_policy.json no runtime-data, este
            // tester usa a policy persistida. Caso contrario, usa
            // defaults.
            var validationState = TryLoadSharedValidationState()
                ?? StreamValidationTesterFactory.CreateIsolatedState();
            // W2-FU-1 (2026-09-22) — wire do observer de falhas de
            // aquisição. sourceId é SEMPRE null neste wave: a identidade
            // operacional Source<->peer/chat não está ainda ligada (ver
            // W2-FU-2 follow-up). Só instalamos o observer no caminho
            // COM ingestion (pipelineIngestor != null) E quando o
            // caller injectou um catalog resolver. Caminho legacy sem
            // catalog preserva o comportamento actual (no observer, sem
            // side-effects).
            m3uCrawler.Services.Validation.IAcquisitionFailureObserver? acquisitionFailureObserver = null;
            if (pipelineIngestor != null && _catalogResolver != null)
            {
                // W2-FU-2A (2026-09-23): resolve existing Source.Id by sourceKey
                // before acquisition. Read-only; if Source does not exist,
                // existingSourceId = null and the observer falls back to the
                // W2-FU-1 RunReport-only path (no Source created during failure).
                var sourceKey = pipelineSourceKey ?? $"telegram-{Slugify(keyword)}";
                var existingSourceId = await _catalogResolver.GetSourceIdByKeyAsync(
                    sourceKey, cancellationToken);

                acquisitionFailureObserver = new m3uCrawler.Services.Validation.CatalogAcquisitionFailureObserver(
                    _catalogResolver, sourceId: existingSourceId, report: rep);
            }
            var tester = acquisitionFailureObserver is null
                ? StreamValidationTesterFactory.CreateTester(validationState)
                : StreamValidationTesterFactory.CreateTester(validationState, acquisitionFailureObserver);
            // W-DEDUP (2026-10-01): registo de validação física por run. Criado aqui
            // porque o AccountValidator é criado uma vez por run de descoberta Telegram.
            // In-memory, não persistido, reset implícito a cada run (nova instância).
            var validationKeyRegistry = new ValidationKeyRegistry();
            // PHASE 9A.2 (2026-09-16): o validador de accounts reusa o MESMO
            // state/cache/host-tracker que o tester.
            // PHASE W-DASHBOARD — liga o progresso do Live Run ao AccountValidator
            // (constructor, porque o validator é criado uma vez por run de
            // descoberta e não há mais nenhum call-site de produção).
            var accountValidator = new AccountValidator(
                validationState, tester, validationKeyRegistry, liveRunProgress);
            var maxConcurrentAccounts = validationState.Options.MaxConcurrentAccounts;
            // PHASE 9A.3 (2026-09-16): coordenador GLOBAL por run. A mesma
            // instancia e' partilhada por TODOS os candidate workers deste
            // run, garantindo que um AccountId nunca testa dois streams em
            // paralelo, mesmo que apareca em candidates diferentes. O limite
            // global de accounts em teste e' MaxConcurrentAccounts.
            var accountGateCoordinator = new AccountGateCoordinator(maxConcurrentAccounts);
            // PHASE-OBSERVABILITY (2026-09-15): associa o trace sink ao tester
            // para que todos os HTTP requests do worker tambem sejam observados.
            if (_trace is m3uCrawler.Services.Validation.PipelineTrace traceSink)
            {
                tester.SetTrace(traceSink);
            }

            var working = new List<M3uStream>();
            var workingLock = new object();
            // W-ACQUIRED (2026-10-10): streams parseados das playlists
            // funcionais para o país (passaram o gate e têm >=1 working).
            // Dedup por URL (OrdinalIgnoreCase, primeira ocorrência). A lista
            // e o índice de dedup são partilhados pelos workers.
            var acquiredStreams = new List<M3uStream>();
            var acquiredSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var acquiredLock = new object();
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
                        // PHASE-OBSERVABILITY: ChannelDequeue event.
                        var dequeueTrace = _trace ?? m3uCrawler.Services.Validation.NullTraceSink.Instance;
                        dequeueTrace.Information(m3uCrawler.Services.Validation.TraceCategory.ChannelDequeue, new m3uCrawler.Services.Validation.TraceContext
                        {
                            CandidateId = c.Id,
                            TelegramMessageId = null, // ChannelDequeue pode servir varias mensagens; e' especifico ao candidate
                            ChatTitle = c.Source,
                        }, $"worker-task={Task.CurrentId} kind={c.Kind} source={TruncateForLog(c.Source, 64)} filename='{TruncateForLog(c.FileName, 128)}'");

                        await semaphore.WaitAsync(cancellationToken);
                        var t = Task.Run(async () =>
                        {
                            // PHASE-OBSERVABILITY: WorkerStart + CandidateProcessStart.
                            dequeueTrace.Information(m3uCrawler.Services.Validation.TraceCategory.WorkerStart, new m3uCrawler.Services.Validation.TraceContext
                            {
                                CandidateId = c.Id,
                                ChatTitle = c.Source,
                            }, $"task={Task.CurrentId}");
                            dequeueTrace.Information(m3uCrawler.Services.Validation.TraceCategory.CandidateProcessStart, new m3uCrawler.Services.Validation.TraceContext
                            {
                                CandidateId = c.Id,
                                ChatTitle = c.Source,
                            }, $"kind={c.Kind} url={CredentialSanitizer.SanitizeUrl(c.Url ?? string.Empty)} detectedFrom={c.DetectedFrom}");
                            try
                            {
                                await ProcessCandidateAsync(
                                    c, tester, parser, validator, countryCode, rep,
                                    maxUrlsToTest, accountValidator, accountGateCoordinator,
                                    candidateChannel.Writer, working, workingLock,
                                    acquiredStreams, acquiredSeen, acquiredLock,
                                    feedCanonicalFallback,
                                    liveRunProgress,
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
                                dequeueTrace.Error(m3uCrawler.Services.Validation.TraceCategory.WorkerEnd, new m3uCrawler.Services.Validation.TraceContext
                                {
                                    CandidateId = c.Id,
                                }, $"kind=Exception ex={ex.GetType().Name} message='{ex.Message.Substring(0, Math.Min(120, ex.Message.Length))}'", ex);
                            }
                            finally
                            {
                                // PHASE-OBSERVABILITY: CandidateProcessEnd + WorkerEnd.
                                dequeueTrace.Information(m3uCrawler.Services.Validation.TraceCategory.CandidateProcessEnd, new m3uCrawler.Services.Validation.TraceContext
                                {
                                    CandidateId = c.Id,
                                }, "");
                                dequeueTrace.Information(m3uCrawler.Services.Validation.TraceCategory.WorkerEnd, new m3uCrawler.Services.Validation.TraceContext
                                {
                                    CandidateId = c.Id,
                                }, $"task={Task.CurrentId}");
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
            // W1 — dedup por identidade funcional no mesmo Run: a mesma
            // conta funcional não origina processamento equivalente
            // duplicado. Candidatos sem identidade estável passam intactos.
            // D-M4-02a — identidade operacional do Run (nunca o trace de
            // diagnóstico). Sem Run operacional => null (sem dedup por Run).
            var discoveryRunId = ResolveOperationalRunId(liveRunProgress);
            var seenDiscoveryAccounts =
                new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            try
            {
                messagesAnalyzed = await SearchM3UInTelegramInternal(
                    keyword, limit, historyHours, rep,
                    liveRunProgress: liveRunProgress,
                    minHistoryHours: minHistoryHours,
                    onCandidateProduced: c =>
                    {
                        // PHASE-OBSERVABILITY: ChannelEnqueue event.
                        var enqueueTrace = _trace ?? m3uCrawler.Services.Validation.NullTraceSink.Instance;

                        // W1 — duas ocorrências da mesma conta funcional no
                        // mesmo Run são a mesma unidade: não enfileirar a
                        // segunda (nunca dedup quando não há identidade).
                        var candidateAccountKey = AccountIdentity.ComputeXtreamAccountKey(c.Url);
                        if (candidateAccountKey != null
                            && !string.IsNullOrEmpty(discoveryRunId)
                            && !seenDiscoveryAccounts.TryAdd(candidateAccountKey, 0))
                        {
                            rep.RejectionReasons.Add(
                                $"{Display(c)}: duplicate functional account in same run");
                            enqueueTrace.Warning(
                                m3uCrawler.Services.Validation.TraceCategory.CandidateRejected,
                                new m3uCrawler.Services.Validation.TraceContext
                                {
                                    CandidateId = c.Id,
                                    ChatTitle = c.Source,
                                },
                                "reason=duplicate-functional-account");
                            return;
                        }

                        enqueueTrace.Information(m3uCrawler.Services.Validation.TraceCategory.ChannelEnqueue, new m3uCrawler.Services.Validation.TraceContext
                        {
                            CandidateId = c.Id,
                            ChatTitle = c.Source,
                        }, $"kind={c.Kind} filename='{TruncateForLog(c.FileName, 128)}'");

                        // channel.Writer.TryWrite e' non-blocking (unboundedChannel).
                        if (!candidateChannel.Writer.TryWrite(c))
                        {
                            rep.RejectionReasons.Add($"{Display(c)}: candidate channel write failed");
                            enqueueTrace.Warning(m3uCrawler.Services.Validation.TraceCategory.CandidateRejected, new m3uCrawler.Services.Validation.TraceContext
                            {
                                CandidateId = c.Id,
                            }, "reason=channel-write-failed");
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

            // PHASE 9C.4 — contadores de Telegram (mensagens/dialogos)
            // disponiveis apenas agora, apos a enumeracao do producer.
            liveRunProgress?.ReportCounts(rep);

            try
            {
                // PIPELINE-INC: o worker acima ja' consumiu e processou cada
                // candidate dentro de ProcessCandidateAsync. Nada mais a iterar aqui.
                _ = worker;
            }
            finally
            {
                // PHASE 9A.3: processingDone ja' completou, logo nao ha
                // operacoes em voo no coordenador.
                tester.Dispose();
                accountGateCoordinator.Dispose();
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
                    // W1 — a ocorrência de descoberta é atribuída ao Run
                    // efectivo (RunId opaco), quando disponível.
                    // D-M4-02a — identidade operacional
                    // (ILiveRunProgress.RunId = RunCoordinator.RunId); sem Run
                    // operacional => null. Nunca se usa PipelineTrace.RunId
                    // como identidade de Run.
                    var discoveryRunIdForIngestion = ResolveOperationalRunId(liveRunProgress);

                    // D-M4-02 — propaga a policy efectiva do snapshot do
                    // Run para a ingestion. Sem Run operacional (sem
                    // snapshot a propagar) ⇒ policy = null: B1 (nunca
                    // fallback para policy viva, nunca fabrico). Sem
                    // resolver injectado ⇒ policy = null (sem mudança
                    // face à wave W5.1).
                    RecognitionPolicy? snapshotPolicy = null;
                    if (_recognitionPolicyResolver is not null
                        && !string.IsNullOrEmpty(discoveryRunIdForIngestion))
                    {
                        snapshotPolicy = await _recognitionPolicyResolver
                            .GetSnapshotPolicyAsync(discoveryRunIdForIngestion, null, null, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    var ingestionResult = await pipelineIngestor.IngestAsync(
                        working, sourceKey, "Telegram", countryCode, cancellationToken,
                        discoveryRunIdForIngestion, snapshotPolicy);
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
            else
            {
                // W-REVIEW-04 / D5-C — Modo LEGACY: pipeline Telegram sem
                // ingestion. Quando nenhum PipelineIngestionService é fornecido,
                // ChannelSource e ReviewItem NÃO são persistidos para este run.
                // A playlist funcional continua a ser escrita. A mensagem é
                // deliberadamente opaca (sem URL, credenciais, peer/message
                // identifiers, source key ou RunId) para não introduzir
                // superfície de leak.
                trace.Warning(
                    m3uCrawler.Services.Validation.TraceCategory.RunStart,
                    runCtx,
                    "telegram pipeline running without ingestion (pipelineIngestor=null); " +
                    "ChannelSource/ReviewItem not persisted for this run");
                Console.WriteLine(
                    "⚠️ Pipeline Telegram em modo LEGACY (sem ingestion): " +
                    "ChannelSource/ReviewItem não serão persistidos para este run.");
            }

            // PHASE-OBSERVABILITY (2026-09-15): RunEnd event.
            var traceEnd = _trace ?? m3uCrawler.Services.Validation.NullTraceSink.Instance;
            var endCtx = new m3uCrawler.Services.Validation.TraceContext { };
            traceEnd.Information(m3uCrawler.Services.Validation.TraceCategory.RunEnd, endCtx,
                $"messagesAnalyzed={rep.MessagesAnalyzed} candidatesFound={rep.CandidatesFound} playlistsDownloaded={rep.PlaylistsDownloaded} streamsWorking={rep.StreamsWorking} streamsFailed={rep.StreamsFailed} durationMs={rep.DurationMs}");
            // PHASE-OBSERVABILITY: se o trace e' uma PipelineTrace com runId, copia
            // os contadores de eventos para o RunReport (reconciliacao pos-run).
            if (_trace is m3uCrawler.Services.Validation.PipelineTrace realTrace)
            {
                m3uCrawler.Services.Validation.RunReportTraceReconciler.RecordSnapshot(rep, realTrace);
            }

            // PHASE 9C.4 — snapshot final dos contadores reais.
            if (liveRunProgress is not null)
            {
                liveRunProgress.ReportCounts(rep);
                liveRunProgress.ReportMessage(
                    $"pipeline completed: {rep.StreamsWorking} working / {rep.StreamsTested} tested streams");
                // PHASE W-DASHBOARD — o sumário entra também no feed de
                // actividades (além do LastMessage), incluindo as validações
                // físicas evitadas pela dedup por run.
                liveRunProgress.ReportActivity(
                    LiveRunActivityCategory.System,
                    LiveRunActivityLevel.Info,
                    $"run completed: {rep.StreamsWorking} working / {rep.StreamsTested} tested / {rep.StreamsSkippedAlreadyValidated} reused-dedup");
            }
            return (working, acquiredStreams, rep);
        }

        // Wrapper que preserva a assinatura pública anterior (devolve só os streams funcionais).
        public async Task<List<M3uStream>> SearchAndTestM3UInTelegram(
            string keyword, int limit = 200, int maxConcurrency = 5, int maxUrlsToTest = 500, int historyHours = 24)
        {
            var (working, _, _) = await SearchAndTestM3UInTelegramAsync(
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
            AccountValidator accountValidator,
            AccountGateCoordinator accountGateCoordinator,
            System.Threading.Channels.ChannelWriter<CandidatePlaylist> writer,
            List<M3uStream> working,
            object workingLock,
            List<M3uStream> acquiredStreams,
            HashSet<string> acquiredSeen,
            object acquiredLock,
            bool feedCanonicalFallback,
            ILiveRunProgress? liveRunProgress,
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
            //     (a serializacao por account e' feita pelo
            //     AccountGateCoordinator dentro de TestStreamsAsync).
            //   - ChannelWriter.TryWrite (NAO pede lock; e' lock-free).
            string? content = candidate.Content;
            // PHASE 9C.4 — Downloading: só quando há download HTTP real
            // (candidatos a partir de anexo já trazem conteúdo).
            if (liveRunProgress is not null && content is null)
            {
                await liveRunProgress.EnterPhaseAsync(
                    LiveRunPhase.Downloading,
                    "downloading playlist content",
                    cancellationToken).ConfigureAwait(false);
                // PHASE W-DASHBOARD — activity de download com proveniência
                // (candidateId + messageId de origem). A mensagem da fase fica
                // genérica; a activity identifica o candidate.
                var downloadMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["candidateId"] = candidate.Id,
                };
                if (candidate.SourceMessageId.HasValue)
                {
                    downloadMetadata["messageId"] = candidate.SourceMessageId.Value
                        .ToString(CultureInfo.InvariantCulture);
                }
                liveRunProgress.ReportActivity(
                    LiveRunActivityCategory.Playlist,
                    LiveRunActivityLevel.Info,
                    $"downloading playlist content ({Display(candidate)})",
                    downloadMetadata);
            }
            // EXPERIMENT-SERIAL-PER-XTREAM (2026-09-16): se o candidate foi
            // promovido a partir de uma publicacao Xtream (DetectedFrom
            // == "xtream publication"), serializamos o download por
            // (URL, username) para impedir duas requests concorrentes ao
            // mesmo servidor com a mesma conta. Outras formas de
            // candidates (m3u url, html attachment, telegram media) nao
            // sao afectadas.
            if (content == null)
            {
                var xtreamIdentity = TryExtractXtreamIdentity(candidate);
                if (xtreamIdentity != null)
                {
                    _xtreamAccountLocks ??= new m3uCrawler.Services.Validation.XtreamAccountLockManager();
                    await using (await _xtreamAccountLocks.AcquireAsync(xtreamIdentity, cancellationToken).ConfigureAwait(false))
                    {
                        content = await DownloadPlaylistContentAsync(candidate.Url, tester);
                    }
                }
                else
                {
                    content = await DownloadPlaylistContentAsync(candidate.Url, tester);
                }
            }

            if (liveRunProgress is not null)
            {
                liveRunProgress.ReportCounts(rep);
                var playlistMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["candidateId"] = candidate.Id,
                };
                if (candidate.SourceMessageId.HasValue)
                {
                    playlistMetadata["messageId"] = candidate.SourceMessageId.Value
                        .ToString(CultureInfo.InvariantCulture);
                }
                liveRunProgress.ReportActivity(
                    LiveRunActivityCategory.Playlist,
                    content is null ? LiveRunActivityLevel.Warning : LiveRunActivityLevel.Info,
                    content is null
                        ? $"playlist download failed ({Display(candidate)})"
                        : $"playlist content downloaded ({Display(candidate)})",
                    playlistMetadata);
            }

            // URL sem extensao (.m3u/.m3u8): detetada por heuristica. So' tratada
            // como playlist se o conteudo HTTP for de facto #EXTM3U. Caso
            // contrario pode ser uma publicacao HTML com cards Xtream.
            if (candidate.RequiresContentVerification && content != null && !_detector.LooksLikePlaylistContent(content))
            {
                // PHASE-OBSERVABILITY: ResolverStart.
                var resTrace = _trace ?? m3uCrawler.Services.Validation.NullTraceSink.Instance;
                resTrace.Information(m3uCrawler.Services.Validation.TraceCategory.ResolverStart, new m3uCrawler.Services.Validation.TraceContext
                {
                    CandidateId = candidate.Id,
                    ChatTitle = candidate.Source,
                }, $"resolver=XtreamPublicationResolver contentLength={content?.Length ?? 0}");
                if (LooksLikeHtmlPublication(content))
                {
                    var accounts = XtreamPublicationResolver.ResolveFromHtml(
                        content!, candidate.Url ?? string.Empty);
                    resTrace.Information(m3uCrawler.Services.Validation.TraceCategory.ResolverEnd, new m3uCrawler.Services.Validation.TraceContext
                    {
                        CandidateId = candidate.Id,
                    }, $"resolver=XtreamPublicationResolver accountsDiscovered={accounts.Count}");
                    if (accounts.Count == 0)
                    {
                        AddRejection(rep, $"{CredentialSanitizer.SanitizeUrl(candidate.Url) ?? candidate.Source}: no xtream cards found");
                        resTrace.Warning(m3uCrawler.Services.Validation.TraceCategory.CandidateRejected, new m3uCrawler.Services.Validation.TraceContext
                        {
                            CandidateId = candidate.Id,
                        }, "reason=no-xtream-cards");
                        return;
                    }
                    // PHASE-OBSERVABILITY: emitir um evento por XtreamAccount para reconciliacao.
                    // Nao inclui password, apenas host/port/username hash + parent.
                    int xIdx = 0;
                    foreach (var acc in accounts)
                    {
                        var accId = $"{candidate.Id}#x{xIdx}";
                        resTrace.Information(m3uCrawler.Services.Validation.TraceCategory.XtreamAccount, new m3uCrawler.Services.Validation.TraceContext
                        {
                            CandidateId = candidate.Id,
                            ParentCandidateId = candidate.Id,
                        }, $"accountId={accId} host={acc.Host} port={acc.Port} username={SafeUsername(acc.Username)}");
                        xIdx++;

                        var playlistUrl = acc.M3uUrl ?? BuildXtreamPlaylistUrl(acc);
                        var promoted = PromoteXtreamAccount(
                            playlistUrl,
                            candidate.Url ?? string.Empty,
                            candidate.SourceMessageId,
                            candidate.SourceMessageDateUtc);
                        if (promoted != null)
                        {
                            resTrace.Information(m3uCrawler.Services.Validation.TraceCategory.CandidatePromoted, new m3uCrawler.Services.Validation.TraceContext
                            {
                                CandidateId = candidate.Id,
                                ParentCandidateId = promoted.Id,
                            }, $"promotedCandidateId={promoted.Id} m3uUrl={CredentialSanitizer.SanitizeUrl(playlistUrl ?? string.Empty)}");

                            // PHASE W-DASHBOARD — fan-out Xtream visível no feed.
                            if (liveRunProgress is not null)
                            {
                                liveRunProgress.ReportActivity(
                                    LiveRunActivityCategory.Xtream,
                                    LiveRunActivityLevel.Info,
                                    $"xtream account promoted: candidate {promoted.Id} (parent {candidate.Id})",
                                    new Dictionary<string, string>(StringComparer.Ordinal)
                                    {
                                        ["candidateId"] = promoted.Id,
                                        ["parentCandidateId"] = candidate.Id,
                                    });
                            }

                            // Re-injecta no canal. ChannelWriter.TryWrite e' non-blocking.
                            if (!writer.TryWrite(promoted))
                            {
                                AddRejection(rep, $"{Display(promoted)}: channel write failed");
                                resTrace.Warning(m3uCrawler.Services.Validation.TraceCategory.CandidateRejected, new m3uCrawler.Services.Validation.TraceContext
                                {
                                    CandidateId = promoted.Id,
                                }, "reason=channel-write-failed");
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

            // Contrato W3 — parsing M3U consistente para TODOS os candidates
            // (não apenas RequiresContentVerification): a primeira linha não
            // vazia tem de ser #EXTM3U. Um resultado Failed não é contado como
            // playlist descarregada/válida nem entrega streams; Partial entrega
            // as entradas válidas e é contabilizado como Partial.
            var parseResult = parser.ParseDetailed(content, cancellationToken);
            if (parseResult.Status == M3uPlaylistStatus.Failed)
            {
                Interlocked.Increment(ref rep._PlaylistsInvalid);
                var diagnostic = parseResult.Diagnostics.FirstOrDefault();
                var reason = diagnostic is null
                    ? "playlist M3U inválida"
                    : $"playlist M3U inválida ({diagnostic.Kind} linha {diagnostic.LineNumber}): {diagnostic.Reason}";
                AddRejection(rep, $"{Display(candidate)}: {reason}");
                if (liveRunProgress is not null)
                {
                    liveRunProgress.ReportActivity(
                        LiveRunActivityCategory.Playlist,
                        LiveRunActivityLevel.Warning,
                        $"{reason} ({Display(candidate)})",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["candidateId"] = candidate.Id,
                        });
                }
                return;
            }

            Interlocked.Increment(ref rep._PlaylistsDownloaded);
            if (parseResult.Status == M3uPlaylistStatus.Partial)
            {
                Interlocked.Increment(ref rep._PlaylistsPartial);
            }

            // PHASE 9C.4 — Analyzing: análise de conteúdo da playlist
            // (deteccao de país + parsing M3U). VALIDATING cobre o gate
            // per-stream abaixo.
            if (liveRunProgress is not null)
            {
                await liveRunProgress.EnterPhaseAsync(
                    LiveRunPhase.Analyzing, "analyzing playlist content", cancellationToken)
                    .ConfigureAwait(false);
            }

            var analysis = validator.AnalyzePlaylist(content, countryCode, 3);
            var discovered = new DiscoveredPlaylist
            {
                Source = candidate.Source,
                Name = Display(candidate),
                CountryDetected = analysis.IsTargetCountry ? countryCode : string.Empty,
                ChannelsRecognized = analysis.RecognizedChannelCount,
                State = analysis.IsTargetCountry ? "accepted" : "rejected",
                CandidateId = candidate.Id,
                MessageId = candidate.SourceMessageId,
                MessageDateUtc = candidate.SourceMessageDateUtc,
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

            var streams = parseResult.Streams.ToList();
            discovered.StreamCount = streams.Count;
            Interlocked.Add(ref rep._StreamsExtracted, streams.Count);

            // PHASE 9C.4 — Validating: gate per-canal/per-stream e teste
            // dos streams alvo.
            if (liveRunProgress is not null)
            {
                await liveRunProgress.EnterPhaseAsync(
                    LiveRunPhase.Validating, "validating streams for country", cancellationToken)
                    .ConfigureAwait(false);
                // PHASE W-DASHBOARD — quantos streams entram no gate e para
                // que país, com proveniência do candidate.
                liveRunProgress.ReportActivity(
                    LiveRunActivityCategory.Stream,
                    LiveRunActivityLevel.Info,
                    $"validating {streams.Count} streams for country '{countryCode}'",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["candidateId"] = candidate.Id,
                    });
            }

            // Gate per-stream (pipeline per-canal/per-stream, desde 2026-08-30).
            // AnalyzePlaylist actua apenas como fast-reject acima; a aprovacao final
            // dos streams exige que cada um seja individualmente validado contra os
            // aliases do pais. Streams rejeitados aqui nunca chegam a TestStreamsAsync.
            //
            // W-FEED (2026-10-10): o fallback canónico é aplicado aqui — os
            // streams rejeitados por falta de token de país que resolvem para
            // um canal canónico existente são aceites (nunca auto-criando
            // canais). A ordem do teste físico não muda: continua a ser só
            // dos streams do país (incluindo os aceites pelo fallback).
            var (countryStreams, countryRejected, canonicalFallbackCount) =
                await FilterStreamsByCountryAsync(
                    validator, streams, countryCode,
                    _catalogResolver, feedCanonicalFallback, cancellationToken)
                .ConfigureAwait(false);
            discovered.StreamsAfterCountryFilter = countryStreams.Count;
            Interlocked.Add(ref rep._StreamsAfterCountryFilter, countryStreams.Count);
            Interlocked.Add(ref rep._StreamsRejectedByCountry, countryRejected);
            if (canonicalFallbackCount > 0)
            {
                Interlocked.Add(ref rep._StreamsMatchedViaCanonicalFallback, canonicalFallbackCount);
            }

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

            var tested = await TestStreamsAsync(
                accountValidator.ValidateAccountAsync,
                accountGateCoordinator,
                candidate.Url, countryStreams, maxUrlsToTest, cancellationToken);
            AccumulateValidationCounters(rep, tested);
            // WorkingStreams inclui, por design, streams conhecidos-working
            // reutilizados (LastTested == default): nao se perde nenhum canal
            // ja' validado neste run.
            discovered.WorkingStreams = tested.Count(s => s.IsWorking);

            lock (workingLock)
            {
                working.AddRange(tested.Where(s => s.IsWorking));
            }

            // W-ACQUIRED (2026-10-10): a playlist é funcional para o país
            // (passou o gate e tem >=1 stream working). Registam-se TODOS os
            // streams parseados desta playlist, dedup por URL
            // (OrdinalIgnoreCase, primeira ocorrência). Playlists sem nenhum
            // working não contribuem (o return acima cobre count==0).
            AccumulateAcquiredStreams(
                acquiredStreams, acquiredSeen, acquiredLock, streams, discovered.WorkingStreams);

            // discovered precisa de ser adicionado ao RunReport sob lock
            // (lista partilhada).
            AddDiscovered(rep, discovered);

            if (liveRunProgress is not null)
            {
                liveRunProgress.ReportCounts(rep);
                liveRunProgress.ReportMessage(
                    $"tested {rep.StreamsTested}/{rep.StreamsAfterCountryFilter} streams");
                // PHASE W-DASHBOARD — sumário por playlist com a distinção
                // física vs reutilizada. Usa a MESMA regra do
                // AccumulateValidationCounters (LastTested == default =>
                // reutilizado), extraída para helper testável.
                var (physical, reused) = CountPhysicalAndReused(tested);
                var validatedWorking = tested.Count(s => s.IsWorking);
                liveRunProgress.ReportActivity(
                    LiveRunActivityCategory.Stream,
                    LiveRunActivityLevel.Info,
                    $"validated {validatedWorking}/{tested.Count} streams (physical {physical}, reused {reused})",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["candidateId"] = candidate.Id,
                        ["physical"] = physical.ToString(CultureInfo.InvariantCulture),
                        ["reused"] = reused.ToString(CultureInfo.InvariantCulture),
                    });
            }
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
            Action<CandidatePlaylist>? onCandidateProduced = null,
            ILiveRunProgress? liveRunProgress = null,
            int minHistoryHours = 0)
        {
            var candidates = new List<CandidatePlaylist>();
            // Publicacoes descobertas em qualquer mensagem: referencias Telegram
            // (t.me/c/...) e URLs HTTP publicas nao capturadas pelo detector.
            // Sao resolvidas apos o loop principal, usando o cache de access_hash
            // construido a partir dos dialogos.
            var discoveredPublications = new List<TelegramPublicationRef>();
            int messagesAnalyzed = 0;

            // Wave W5 — cliente vivo único: no caminho de aplicação é o
            // cliente autenticado do dashboard/scheduler.
            var client = RequireClient();

            // Idempotente: se já autenticado, não faz nada
            var me = await client.LoginUserIfNeeded();
            Console.WriteLine($"Autenticado como: {(me?.username ?? me?.first_name ?? "(sem nome)")}");

            var dialogsBase = await client.Messages_GetAllDialogs();

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
            var cycleStartUtc = DateTime.UtcNow;
            var cycleCutoff = cycleStartUtc.AddHours(-historyHours);
            // W-HISTWIN (2026-10-02): limite inferior da janela (idade mínima).
            // Derivado do MESMO instante cycleStartUtc para preservar o
            // invariante R1 (um único par de cutoffs por ciclo). null = sem
            // limite inferior (minHistoryHours = 0, comportamento legacy).
            DateTime? cycleMinCutoff = minHistoryHours > 0
                ? cycleStartUtc.AddHours(-minHistoryHours)
                : null;
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
                                User user => await client.Messages_GetHistory(
                                    user, offset_id: offsetId, offset_date: default,
                                    add_offset: 0, limit: 100, max_id: 0, min_id: 0),
                                ChatBase chat => await client.Messages_GetHistory(
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
                                onCandidateProduced, liveRunProgress);
                        },
                        minCutoffDate: cycleMinCutoff);
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
                        // W-HISTWIN-PROV: a mensagem de origem é a t.me/c referenciada (res.MessageId);
                        // a data não está disponível na resolução — fica null.
                        var promoted = PromoteXtreamAccount(playlistUrl, res.ReferenceUrl, res.MessageId);
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
        //   - cutoffDate: cutoff temporal UNICO por ciclo (R1). Limite
        //     superior (Max) da janela; limites inclusivos.
        //   - pageFetcher: delegate que devolve a proxima pagina de
        //     mensagens para um dado offsetId. Pode lancar excepcoes
        //     (RpcError, IOException, FLOOD_WAIT, etc.).
        //   - onMessage: callback async invocado por cada mensagem que
        //     passou o filtro temporal. NAO e' invocado para mensagens
        //     anteriores ao cutoff nem para mensagens mais recentes que
        //     minCutoffDate.
        //   - minCutoffDate: limite inferior (Min) opcional da janela,
        //     tambem inclusivo. Mensagens mais recentes que este valor
        //     sao saltadas SEM terminar a paginacao (a ordem e'
        //     descendente; as mensagens dentro da faixa vem a seguir).
        //     null = sem limite inferior (comportamento legacy).
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
            Func<Message, Task> onMessage,
            DateTime? minCutoffDate = null)
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

                    // W-HISTWIN: limite inferior da janela. Mensagens mais recentes
                    // que o mínimo são saltadas SEM terminar a paginação (a ordem é
                    // descendente; as mensagens dentro da faixa vêm a seguir).
                    // Limites inclusivos: m.date == minCutoffDate é aceite (idade == Min).
                    if (minCutoffDate.HasValue && m.date > minCutoffDate.Value)
                    {
                        continue;
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
            Action<CandidatePlaylist>? onCandidateProduced,
            ILiveRunProgress? liveRunProgress = null)
        {
            // Em TL atual a legenda de um media é o próprio texto da mensagem.
            string text = m.message ?? "";
            string filename = "";

            // PHASE-OBSERVABILITY (2026-09-15): tracing por mensagem.
            var msgTrace = _trace ?? m3uCrawler.Services.Validation.NullTraceSink.Instance;
            var msgCtx = new m3uCrawler.Services.Validation.TraceContext
            {
                TelegramMessageId = m.ID,
                ChatTitle = chatTitle,
            };
            msgTrace.Information(m3uCrawler.Services.Validation.TraceCategory.MessageAnalyzed, msgCtx,
                $"date={m.date:o} textLength={text.Length}");

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
                msgTrace.Information(m3uCrawler.Services.Validation.TraceCategory.MessageMediaInfo, msgCtx,
                    $"mediaType={mediaType} size={docSize} mimeType='{docMime ?? "<n/a>"}' docAttributes={docAttributes}");
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
            msgTrace.Information(m3uCrawler.Services.Validation.TraceCategory.DetectStart, msgCtx,
                $"filename='{TruncateForLog(filename, 128)}'");
            var found = _detector.DetectFromMessage(text, filename).ToList();
            msgTrace.Information(m3uCrawler.Services.Validation.TraceCategory.DetectEnd, msgCtx,
                $"candidates={found.Count}");

            if (m.media != null)
            {
                var htmlCount = found.Count(c => c.DetectedFrom == "html attachment");
                if (report != null) report.HtmlCandidatesCreated += htmlCount;
                Console.WriteLine(
                    $"[TelegramDetectResult] messageId={m.ID} filename='{TruncateForLog(filename, 128)}' " +
                    $"candidates={found.Count}");

                // PHASE-OBSERVABILITY: emite um evento por candidate criado.
                foreach (var c in found)
                {
                    msgTrace.Information(m3uCrawler.Services.Validation.TraceCategory.CandidateCreated, msgCtx,
                        $"candidateId={c.Id} kind={c.Kind} source={TruncateForLog(c.Source, 64)} filename='{TruncateForLog(c.FileName, 128)}' url={CredentialSanitizer.SanitizeUrl(c.Url ?? string.Empty)} detectedFrom={c.DetectedFrom} requiresContentVerification={c.RequiresContentVerification}");
                }
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

            // PHASE 9C.4 — Discovering: reportado ANTES de enfileirar os
            // candidatos, garantindo que a ordem monotónica do timeline
            // (ReadingTelegram -> Discovering -> Downloading) se mantém
            // mesmo com o producer/consumer concorrente.
            if (liveRunProgress is not null && found.Count > 0)
            {
                await liveRunProgress.EnterPhaseAsync(
                    LiveRunPhase.Discovering,
                    $"detected {found.Count} candidate(s)",
                    CancellationToken.None).ConfigureAwait(false);
                liveRunProgress.ReportCounts(report!);
                // PHASE W-DASHBOARD — a activity Discovering carrega a
                // proveniência da mensagem de origem em metadata (a mensagem da
                // fase fica curta; o messageId não entra no texto).
                liveRunProgress.ReportActivity(
                    LiveRunActivityCategory.Telegram,
                    LiveRunActivityLevel.Info,
                    $"detected {found.Count} candidate(s)",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["messageId"] = m.ID.ToString(CultureInfo.InvariantCulture),
                        ["messageDateUtc"] = m.date.ToString("o", CultureInfo.InvariantCulture),
                        ["chat"] = chatTitle,
                    });
            }

            foreach (var candidate in found)
            {
                ApplyTelegramMessageProvenance(candidate, chatTitle, m.ID, m.date);
                candidates.Add(candidate);
                // Candidados devem ser contados incrementalmente (consistente com
                // a semantica anterior: rep.CandidatesFound = candidates.Count
                // no fim do pipeline; agora definido no momento da producao).
                if (report != null) report.CandidatesFound++;
                onCandidateProduced?.Invoke(candidate);

                // PHASE W-DASHBOARD — uma activity por candidate criado, com
                // a proveniência já aplicada (candidateId/messageId/chat).
                if (liveRunProgress is not null)
                {
                    var (message, metadata) = BuildCandidateCreatedActivity(candidate);
                    liveRunProgress.ReportActivity(
                        LiveRunActivityCategory.Telegram,
                        LiveRunActivityLevel.Info,
                        message,
                        metadata);
                }
            }
        }

        // ==== W-HISTWIN-PROV (2026-10-02): proveniência mensagem → candidate ====
        // Isolado em helper interno estático para ser testado sem rede (mesmo
        // padrão de EnumerateDialogHistoryAsync). Aplica a proveniência da
        // mensagem Telegram de origem a um candidate: chat title (Source),
        // id e data/hora UTC da mensagem.
        internal static void ApplyTelegramMessageProvenance(
            CandidatePlaylist candidate, string chatTitle, long messageId, DateTime messageDateUtc)
        {
            candidate.Source = chatTitle;
            candidate.SourceMessageId = messageId;
            candidate.SourceMessageDateUtc = messageDateUtc;
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
                    var response = await RequireClient().Channels_GetMessages(inputChannel, ids);
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

            // PHASE-OBSERVABILITY (2026-09-15): tracing de download.
            var dlTrace = _trace ?? m3uCrawler.Services.Validation.NullTraceSink.Instance;
            var dlCtx = new m3uCrawler.Services.Validation.TraceContext
            {
                TelegramMessageId = messageIdForLog,
                AttachmentFilename = filenameForLog,
            };
            dlTrace.Information(m3uCrawler.Services.Validation.TraceCategory.AttachmentDownloadStart, dlCtx,
                $"expected={expected}");

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
                    // PHASE-OBSERVABILITY: emitir progress por chunk (Debug).
                    dlTrace.Debug(m3uCrawler.Services.Validation.TraceCategory.AttachmentDownloadProgress, dlCtx,
                        $"transmitted={transmitted} total={total} chunks={chunks}");
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
                await RequireClient().DownloadFileAsync(
                    fileLocation: document.ToFileLocation(),
                    outputStream: ms,
                    dc_id: 0,
                    fileSize: expected,
                    progress: progress).ConfigureAwait(false);

                sw.Stop();
                var actual = ms.Length;

                if (actual == 0)
                {
                    dlTrace.Warning(m3uCrawler.Services.Validation.TraceCategory.AttachmentDownloadFailed, dlCtx,
                        $"status=failed expected={expected} actual=0 chunks={chunks} durationMs={sw.ElapsedMilliseconds} error=empty-stream");
                    LogDownloadOutcome(filenameForLog, messageIdForLog, expected, 0,
                        status: "failed", durationMs: sw.ElapsedMilliseconds,
                        chunks: (int)chunks, error: "empty stream");
                    return null;
                }

                if (expected > 0 && actual < expected)
                {
                    dlTrace.Warning(m3uCrawler.Services.Validation.TraceCategory.AttachmentDownloadFailed, dlCtx,
                        $"status=truncated expected={expected} actual={actual} chunks={chunks} durationMs={sw.ElapsedMilliseconds}");
                    LogDownloadOutcome(filenameForLog, messageIdForLog, expected, actual,
                        status: "truncated", durationMs: sw.ElapsedMilliseconds,
                        chunks: (int)chunks, error: null);
                    return null;
                }

                if (expected > 0 && actual > expected)
                {
                    dlTrace.Warning(m3uCrawler.Services.Validation.TraceCategory.AttachmentDownloadFailed, dlCtx,
                        $"status=unexpected expected={expected} actual={actual} chunks={chunks} durationMs={sw.ElapsedMilliseconds}");
                    LogDownloadOutcome(filenameForLog, messageIdForLog, expected, actual,
                        status: "unexpected", durationMs: sw.ElapsedMilliseconds,
                        chunks: (int)chunks, error: null);
                    // Em unexpected ainda tentamos usar o conteudo.
                }

                dlTrace.Information(m3uCrawler.Services.Validation.TraceCategory.AttachmentDownloadComplete, dlCtx,
                    $"expected={expected} actual={actual} chunks={chunks} durationMs={sw.ElapsedMilliseconds}");
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

        // PHASE 9A.3 (2026-09-16): testa os streams de UM candidate atraves
        // do AccountGateCoordinator GLOBAL do run.
        //
        //   candidate (1 account) -> 1 AccountValidationWork
        //     -> AccountGateCoordinator (slot global + gate por AccountId)
        //       -> validateAccount (AccountValidator.ValidateAccountAsync)
        //         -> tester.TestStreamForAccountAsync (HTTP, cache, host tracker)
        //
        // O coordinator e' partilhado por todos os candidate workers do run,
        // pelo que o MESMO AccountId nunca tem dois streams em teste em
        // paralelo, mesmo aparecendo em candidates diferentes. O numero
        // global de accounts em teste e' MaxConcurrentAccounts.
        //
        // `validateAccount` e' injectado para permitir testes de integracao
        // sem rede; em producao e' sempre AccountValidator.ValidateAccountAsync.
        internal async Task<List<M3uStream>> TestStreamsAsync(
            Func<AccountValidationWork, CancellationToken, Task<AccountValidationResult>> validateAccount,
            AccountGateCoordinator accountGateCoordinator,
            string? playlistUrl,
            List<M3uStream> streams,
            int maxUrlsToTest,
            CancellationToken cancellationToken)
        {
            var toTest = (maxUrlsToTest > 0 ? streams.Take(maxUrlsToTest) : streams).ToList();
            if (toTest.Count == 0) return new List<M3uStream>();

            var work = BuildAccountWork(playlistUrl, toTest);

            AccountValidationResult? result = null;
            try
            {
                // O cancellationToken do run impede a ADMISSAO (slot global e
                // gate da account). Depois de admitida, a operacao corre ate'
                // ao fim: o token cancelavel do run NAO e' propagado ao teste
                // dos streams (semantica anterior a 9A.2).
                result = await accountGateCoordinator.RunExclusiveAsync(
                    work.AccountId,
                    _ => validateAccount(work, CancellationToken.None),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Nao admitida por cancelamento do run: devolver a mesma
                // forma, com todos os streams marcados como nao-funcionais.
            }

            var outcomes = result?.Outcomes
                ?? (IReadOnlyList<StreamTestOutcome>)Array.Empty<StreamTestOutcome>();

            var tested = new List<M3uStream>(toTest.Count);
            for (var i = 0; i < toTest.Count; i++)
            {
                var source = toTest[i];
                var outcome = i < outcomes.Count
                    ? outcomes[i]
                    : StreamTestOutcome.Empty(source.Url) with { WasShortCircuited = true };
                tested.Add(M3uTesterService.BuildStreamForAccountFromOutcome(
                    source.Url, source.Title, source.Group, outcome));
            }
            return tested;
        }

        // PHASE 9A.2: constroi o work item de UMA account a partir dos streams
        // de um candidate. Identity = (URL sem password) + "|" + username.
        // A password nunca participa do fingerprint nem dos logs.
        internal static AccountValidationWork BuildAccountWork(
            string? playlistUrl,
            IReadOnlyList<M3uStream> streams)
        {
            var username = AccountIdentity.ExtractUsername(playlistUrl);
            var accountId = AccountIdentity.Compute(
                AccountIdentity.ComputeSafeUrl(playlistUrl ?? string.Empty), username);

            var accountStreams = new List<AccountStreamWork>(streams.Count);
            foreach (var s in streams)
            {
                accountStreams.Add(new AccountStreamWork(s.Url, s.Title, s.Group));
            }

            return new AccountValidationWork(
                accountId,
                playlistUrl ?? string.Empty,
                username,
                accountStreams);
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

        /// <summary>
        /// W-FEED (2026-10-10) — filtro per-stream com fallback canónico na
        /// aquisição. Depois de <see cref="CountryChannelValidator.ValidateStreams"/>,
        /// os streams <b>rejeitados por falta de token de país</b> são ainda
        /// aceites quando: (a) o setting está ON, (b) existe um
        /// <see cref="CatalogResolver"/>, (c) o título não tem prefixo
        /// estrangeiro (negative evidence preservada) e (d) a identidade
        /// normalizada (<see cref="ChannelNormalizer.Normalize"/>) resolve
        /// para um canal canónico existente. Nunca auto-cria canais.
        ///
        /// <para>
        /// <see cref="Rejected"/> é o valor pós-fallback, de modo a manter o
        /// invariante <c>Rejected == streams.Count - Accepted.Count</c>.
        /// <see cref="MatchedViaCanonicalFallback"/> conta apenas os aceites
        /// por este caminho (subconjunto de <see cref="Accepted"/>).
        /// </para>
        /// </summary>
        internal static async Task<(List<M3uStream> Accepted, int Rejected, int MatchedViaCanonicalFallback)>
            FilterStreamsByCountryAsync(
                CountryChannelValidator validator,
                List<M3uStream> streams,
                string countryCode,
                CatalogResolver? catalogResolver,
                bool feedCanonicalFallback,
                CancellationToken cancellationToken = default)
        {
            var matches = validator.ValidateStreams(streams, countryCode);
            var accepted = matches.Select(m => m.Stream).ToList();
            var matched = new HashSet<M3uStream>(accepted, ReferenceEqualityComparer.Instance);
            int matchedViaFallback = 0;

            if (feedCanonicalFallback && catalogResolver is not null)
            {
                foreach (var stream in streams)
                {
                    if (matched.Contains(stream))
                    {
                        continue;
                    }

                    // Negative evidence (Opção C): um título com prefixo de
                    // país estrangeiro continua rejeitado, mesmo que resolva
                    // para um canal canónico.
                    if (CountryChannelValidator.HasForeignCountryPrefix(stream.Title, countryCode))
                    {
                        continue;
                    }

                    var normalized = ChannelNormalizer.Normalize(stream.Title);
                    if (string.IsNullOrWhiteSpace(normalized))
                    {
                        continue;
                    }

                    if (await catalogResolver
                        .CanonicalChannelExistsByNormalizedIdentityAsync(normalized, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        accepted.Add(stream);
                        matched.Add(stream);
                        matchedViaFallback++;
                    }
                }
            }

            int rejected = streams.Count - accepted.Count;
            return (accepted, rejected, matchedViaFallback);
        }

        /// <summary>
        /// W-ACQUIRED (2026-10-10) — acumula, por run, os streams parseados
        /// de uma playlist <b>funcional para o país</b> na lista de aquisição.
        /// Só contribui quando <paramref name="workingStreams"/> &gt; 0 (a
        /// playlist passou o gate e tem pelo menos um stream a funcionar).
        /// Dedup por URL (<see cref="StringComparer.OrdinalIgnoreCase"/>),
        /// mantendo a primeira ocorrência. Thread-safe (lock interno), porque
        /// os candidate workers correm em paralelo.
        /// </summary>
        internal static void AccumulateAcquiredStreams(
            List<M3uStream> acquiredStreams,
            HashSet<string> acquiredSeen,
            object gate,
            IReadOnlyList<M3uStream> parsedStreams,
            int workingStreams)
        {
            if (workingStreams <= 0)
            {
                return;
            }

            lock (gate)
            {
                foreach (var parsed in parsedStreams)
                {
                    if (acquiredSeen.Add(parsed.Url))
                    {
                        acquiredStreams.Add(parsed);
                    }
                }
            }
        }

        /// <summary>
        /// W-DEDUP (2026-10-01): acumula contadores de validacao fisica a
        /// partir das streams construidas para um candidate.
        ///
        /// <para>
        /// Invariante: <c>StreamsTested == StreamsWorking + StreamsFailed</c>
        /// passa a cobrir apenas validacoes FISICAS. Streams reutilizadas
        /// (<c>LastTested == default</c>, conhecidas-working de outra
        /// AccountKey neste run) nao sao GET fisico e sao contadas em
        /// <c>StreamsSkippedAlreadyValidated</c>. Escreve em <paramref name="rep"/>
        /// de forma atomica (os candidate workers correm em paralelo).
        /// </para>
        /// </summary>
        internal static void AccumulateValidationCounters(RunReport rep, IReadOnlyList<M3uStream> tested)
        {
            int physical = 0, working = 0, failed = 0;
            foreach (var s in tested)
            {
                if (s.LastTested == default) continue;   // reutilizado: não é GET físico
                physical++;
                if (s.IsWorking) working++; else failed++;
            }
            Interlocked.Add(ref rep._StreamsTested, physical);
            Interlocked.Add(ref rep._StreamsWorking, working);
            Interlocked.Add(ref rep._StreamsFailed, failed);
            Interlocked.Add(ref rep._StreamsSkippedAlreadyValidated, tested.Count - physical);
        }

        /// <summary>
        /// PHASE W-DASHBOARD — separa validações FÍSICAS de REUTILIZADAS para
        /// uma lista de streams testados. Regra idêntica à usada por
        /// <see cref="AccumulateValidationCounters"/>: <c>LastTested == default</c>
        /// significa que a stream foi reutilizada (conhecida Working neste run)
        /// e não gerou GET físico. Helper puro, testável sem HTTP.
        /// </summary>
        internal static (int Physical, int Reused) CountPhysicalAndReused(
            IReadOnlyList<M3uStream> tested)
        {
            int physical = 0, reused = 0;
            foreach (var s in tested)
            {
                if (s.LastTested == default) reused++;
                else physical++;
            }
            return (physical, reused);
        }

        /// <summary>
        /// PHASE W-DASHBOARD — mensagem + metadata de proveniência para a
        /// activity emitida quando um candidate é criado a partir de uma
        /// mensagem Telegram enumerada. Puro, testável sem rede. Não inclui
        /// credenciais: <c>chat</c> é o título do chat (Source) e
        /// <c>messageId</c> só entra quando existe.
        /// </summary>
        internal static (string Message, IReadOnlyDictionary<string, string> Metadata) BuildCandidateCreatedActivity(
            CandidatePlaylist candidate)
        {
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["candidateId"] = candidate.Id,
            };
            if (candidate.SourceMessageId.HasValue)
            {
                metadata["messageId"] = candidate.SourceMessageId.Value
                    .ToString(CultureInfo.InvariantCulture);
            }
            if (!string.IsNullOrEmpty(candidate.Source))
            {
                metadata["chat"] = candidate.Source;
            }

            var message =
                $"candidate {candidate.Id} created (kind={candidate.Kind}, from={candidate.DetectedFrom})";
            return (message, metadata);
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
        /// A proveniência da mensagem de origem (id/data) é herdada dos parâmetros opcionais;
        /// o Source mantém o URL público da publicação (sem credenciais).
        /// </summary>
        internal static CandidatePlaylist? PromoteXtreamAccount(
            string? playlistUrl,
            string publicationUrl,
            long? sourceMessageId = null,
            DateTime? sourceMessageDateUtc = null)
        {
            if (string.IsNullOrWhiteSpace(playlistUrl)) return null;
            return new CandidatePlaylist
            {
                Kind = CandidateSourceKind.Url,
                Url = playlistUrl,
                Source = $"xtream publication: {publicationUrl}",
                DetectedFrom = "xtream publication",
                RequiresContentVerification = true,
                SourceMessageId = sourceMessageId,
                SourceMessageDateUtc = sourceMessageDateUtc
            };
        }

        /// <summary>
        /// EXPERIMENT-SERIAL-PER-XTREAM (2026-09-16): extrai a identidade
        /// (URL, username) de um candidate Xtream, ou devolve null se o
        /// candidate nao for Xtream-promoted ou se a URL nao contiver um
        /// username parseavel.
        ///
        /// O discriminator de origem e' <c>DetectedFrom == "xtream publication"</c>
        /// (sinal posto por <see cref="PromoteXtreamAccount"/>).
        ///
        /// DEFINICAO (2026-09-16, revisao apos teste com password diferente):
        /// a identidade e' (host:port/path-without-query + username). A password
        /// e todos os outros parametros de query (type, output) sao REMOVIDOS
        /// antes do hashing para que contas com passwords diferentes mas
        /// mesmo (host:port + username) sejam consideradas a MESMA conta.
        /// Caso contrario, URLs que diferem apenas em `password=` teriam
        /// identidades diferentes, contradizendo a regra experimental.
        ///
        /// A password NUNCA aparece no fingerprint nem nos logs.
        /// </summary>
        internal static string? TryExtractXtreamIdentity(CandidatePlaylist candidate)
        {
            if (candidate == null) return null;
            if (!string.Equals(candidate.DetectedFrom, "xtream publication", StringComparison.Ordinal))
            {
                return null;
            }
            var url = candidate.Url;
            if (string.IsNullOrWhiteSpace(url)) return null;

            string? username = null;
            try
            {
                // Extrair username ANTES de strip da query.
                var qIdx = url.IndexOf('?');
                if (qIdx >= 0)
                {
                    var query = url.Substring(qIdx + 1);
                    foreach (var pair in query.Split('&'))
                    {
                        var eq = pair.IndexOf('=');
                        if (eq <= 0) continue;
                        var k = Uri.UnescapeDataString(pair.Substring(0, eq));
                        if (string.Equals(k, "username", StringComparison.OrdinalIgnoreCase))
                        {
                            username = Uri.UnescapeDataString(pair.Substring(eq + 1));
                            break;
                        }
                    }
                }
            }
            catch
            {
                return null;
            }
            if (string.IsNullOrEmpty(username)) return null;

            // Strip do parametro `password=` da query string para que contas
            // com mesma (host:port/username) mas passwords diferentes tenham
            // a mesma identidade.
            var urlWithoutPassword = StripPasswordFromQuery(url);
            return m3uCrawler.Services.Validation.XtreamAccountLockManager.ComputeIdentity(urlWithoutPassword, username);
        }

        /// <summary>
        /// Remove o parametro <c>password=</c> da query string, mantendo os
        /// restantes parametros pela mesma ordem. Devolve o URL original se
        /// nao tiver query string ou se a sanitizacao falhar.
        /// </summary>
        private static string StripPasswordFromQuery(string url)
        {
            try
            {
                var qIdx = url.IndexOf('?');
                if (qIdx < 0) return url;
                var baseUrl = url.Substring(0, qIdx);
                var query = url.Substring(qIdx + 1);
                var pairs = query.Split('&');
                var kept = new List<string>(pairs.Length);
                foreach (var pair in pairs)
                {
                    if (pair.Length == 0) continue;
                    var eq = pair.IndexOf('=');
                    var key = eq <= 0 ? pair : pair.Substring(0, eq);
                    if (string.Equals(Uri.UnescapeDataString(key), "password", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    kept.Add(pair);
                }
                if (kept.Count == 0) return baseUrl;
                return baseUrl + "?" + string.Join("&", kept);
            }
            catch
            {
                return url;
            }
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
        /// PHASE-OBSERVABILITY (2026-09-15): helper que produz um username
        /// seguro para logs: primeiros 4 chars + tamanho total. Nunca
        /// expõe a string completa para evitar correlação com passwords
        /// reais ou usernames reutilizados.
        /// </summary>
        private static string SafeUsername(string? username)
        {
            if (string.IsNullOrEmpty(username)) return "<empty>";
            if (username.Length <= 4) return new string('*', username.Length);
            return username.Substring(0, 4) + "*(" + username.Length + ")";
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
