using m3uCrawler.Build;
using m3uCrawler.Services;
using m3uCrawler.Services.Audit;
using m3uCrawler.Services.Auth;
using m3uCrawler.Services.Automation;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using m3uCrawler.Services.Dispatcharr;
using m3uCrawler.Services.LiveRun;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using m3uCrawler.Services.SourceSelection;
using m3uCrawler.Services.Sync;
using m3uCrawler.Services.Telegram;
using m3uCrawler.Services.Validation;
using m3uCrawler.Models;
using m3uCrawler.Services.Epg;
using System.Text;
using System.Net;
using System.IO.Compression;
using System.Xml;

namespace m3uCrawler
{
    class Program
    {
        static async Task Main(string[] args)
        {
            if (args.Any(a => a == "--version" || a == "-V"))
            {
                Console.WriteLine(BuildInfo.Current.ToCliLine());
                return;
            }

            // W10a — Recuperação de password do administrador (host-only).
            // Tratado antes do banner/dashboard/telegram para que este caminho
            // imprima apenas mensagens funcionais e saia de seguida.
            if (args.Contains("--admin-reset-password"))
            {
                Environment.ExitCode = await RunAdminResetPasswordAsync(args);
                return;
            }

            Console.WriteLine("=== m3uCrawler - Pesquisador de Streams M3U8 ===");
            Console.WriteLine(BuildInfo.Current.ToCliLine());
            Console.WriteLine();

            var domainFilter = GetOptionValue(args, "--domain");
            var countryCode = GetOptionValue(args, "--country") ?? "pt";
            if (!string.IsNullOrWhiteSpace(domainFilter))
            {
                Console.WriteLine($"🌐 Filtro de domínio ativo: {domainFilter}");
            }
            Console.WriteLine($"🇵🇹 País em validação: {countryCode}");

            // Wave C — Store único de settings operacionais, partilhado por
            // CLI, dashboard e scheduler. Fonte de verdade em
            // runtime-data/app_settings.json (secção "discovery").
            var appSettingsRuntimeDataDir = Path.Combine(Directory.GetCurrentDirectory(), "runtime-data");
            var appSettingsStore = new AppSettingsStore(appSettingsRuntimeDataDir);
            var discoverySettingsProvider = new DiscoverySettingsProvider(appSettingsStore);

            // Dashboard web: parsing e arranque no top-level para que --web
            // funcione standalone (sem --telegram). A pipeline Telegram
            // continua condicionada a args.Contains("--telegram") mais abaixo.
            bool webEnabled = args.Contains("--web");
            int webPort = 5000;
            var webPortArg = GetOptionValue(args, "--web-port");
            if (int.TryParse(webPortArg, out var parsedWebPort) && parsedWebPort > 0)
            {
                webPort = parsedWebPort;
            }
            string? webToken = webEnabled ? GetOptionValue(args, "--web-token") : null;
            // PHASE 9C.4 — opt-in para o botão "Run now" no dashboard.
            // Default: false. Sem esta flag, POST /api/run/start devolve
            // 503 web-allow-trigger-disabled (regra congelada).
            bool webAllowTrigger = webEnabled && args.Contains("--web-allow-trigger");

            // Lifecycle do processo residente: token partilhado entre
            // Dashboard, Scheduler e mecanismo de shutdown (Ctrl+C / SIGTERM).
            var processCts = new System.Threading.CancellationTokenSource();

            Task? webTask = null;
            CatalogResolver? webCatalogResolver = null;
            ScheduledAutomationHost? automationHost = null;
            // PHASE 9C.4 — Host que detém o RunCoordinator único. Construído
            // quando o catálogo está disponível (i.e. --web foi passado
            // e o InitializeCatalogAsync foi bem-sucedido). O executor da
            // pipeline Telegram é registado dentro do bloco --telegram.
            LiveRunHost? liveRunHost = null;
            // Wave W5 — Serviço de autenticação Telegram de aplicação
            // (dashboard/scheduler). É construído no bloco --web e
            // reutilizado pelo bloco --telegram para que o cliente WTelegram
            // vivo e autenticado seja partilhado, em vez de a pipeline
            // construir um segundo cliente com login de consola.
            TelegramAuthService? applicationTelegramAuth = null;
            if (webEnabled)
            {
                var dashboardOutputDir = GetOptionValue(args, "--output-dir") ?? "output";
                var dashboardHistoryService = new ImportHistoryService(dashboardOutputDir);

                try
                {
                    webCatalogResolver = await InitializeCatalogAsync(
                        ResolveCatalogDbPath(args), CancellationToken.None);
                    WebDashboardService.SetCatalogResolver(webCatalogResolver);

                    // DL-130 (Phase 5) — Cursores de publicação do catálogo.
                    // Construído a partir da mesma factory que o resolver
                    // (partilham o mesmo SQLite file). Se a construção
                    // falhar (improvável: factory já existe), o endpoint
                    // responde 503, mesmo padrão de _liveRunHost.
                    WebDashboardService.SetPublicationStatusService(
                        new PublicationStatusService(webCatalogResolver.GetFactory()));

                    // PHASE 9C.1 — Lifecycle de configuração. O dashboard
                    // fica sempre acessível; o estado é reportado em
                    // /api/configuration/lifecycle e o gate bloqueia
                    // discovery/scheduler automáticos em NOT_CONFIGURED.
                    var lifecycle = BuildConfigurationLifecycle(
                        ResolveCatalogDbPath(args),
                        dashboardOutputDir,
                        webCatalogResolver);
                    WebDashboardService.SetConfigurationLifecycle(lifecycle);
                    try
                    {
                        LogLifecycleState(await lifecycle.EnsureInitializedAsync(CancellationToken.None));
                    }
                    catch (Exception lifecycleEx)
                    {
                        Console.WriteLine($"⚠️ Não foi possível inicializar o estado de configuração: {lifecycleEx.Message}");
                    }

                    // PHASE 12 — Construir e arrancar o scheduler. As actions
                    // concretas ficam registadas para o formulário do Dashboard
                    // e o runner entra em loop respeitando shutdown via Ctrl+C
                    // (CancellationToken propagado pelo _cts interno).
                    var dispatcharrConfig = DispatcharrConfigLoader.Load();

                    // PHASE 9C.2 — Autenticação/bootstrap. Numa instalação
                    // nova o wizard cria o primeiro administrador e só depois
                    // o lifecycle passa a READY. Numa instalação legacy
                    // adoptada READY sem administrador (PHASE 9C.5,
                    // BOOTSTRAP_REQUIRED), o wizard fica activo apenas para
                    // criar o primeiro administrador, sem reconfigurar nem
                    // alterar o estado; após a criação passa a UserAuth.
                    var adminUsers = new AdminUserStore(webCatalogResolver.GetFactory());
                    var sessions = new SessionStore(webCatalogResolver.GetFactory());
                    var authService = new AuthService(adminUsers, sessions);
                    var bootstrapValidator = new BootstrapConfigurationValidator(
                        webCatalogResolver.GetFactory(), dashboardOutputDir, dispatcharrConfig);
                    var bootstrapService = new BootstrapService(
                        lifecycle, adminUsers, bootstrapValidator);
                    WebDashboardService.SetAuth(authService, bootstrapService);

                    // W6a — Auditoria administrativa persistida no mesmo catálogo
                    // SQLite. Best-effort; nunca bloqueia a mutação.
                    WebDashboardService.SetAuditService(
                        new AuditService(webCatalogResolver.GetFactory()));

                    // Wave 4 (PHASE 9C) — Prontidão operacional. O gate do
                    // scheduler exige, além do lifecycle READY, que os
                    // componentes obrigatórios estejam funcionais (admin,
                    // Telegram, Dispatcharr quando activado, catálogo,
                    // output). Sources NÃO bloqueia o SetupComplete — de
                    // outra forma o discovery nunca arrancaria. Instalações
                    // adoptadas como legacy ficam grandfathered (ver
                    // OperationalReadinessService).
                    var wtelegramStore = new WtelegramConfigStore();
                    // W6c — o resultado do último teste Dispatcharr é persistido
                    // no settings store único (runtime-data/app_settings.json) e
                    // consumido pela prontidão operacional.
                    var dispatcharrTestStore = new DispatcharrConnectionTestStore(appSettingsStore);
                    var telegramAuth = new TelegramAuthService(wtelegramStore);
                    // Wave W5 — expor o serviço ao bloco --telegram (abaixo)
                    // para partilha do cliente autenticado.
                    applicationTelegramAuth = telegramAuth;
                    var dispatcharrService = new DispatcharrConfigurationService(wtelegramStore);

                    // Wave 5 (PHASE 9C) — Hidratação da sessão Telegram em
                    // background: um session.dat persistido válido deve marcar
                    // o processo como autenticado logo após restart, sem
                    // bloquear o arranque do dashboard nem exigir rede. Falha
                    // e timeout são não-fatais (a prontidão fica false).
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var status = await telegramAuth
                                .GetStatusAsync(CancellationToken.None)
                                .WaitAsync(TimeSpan.FromSeconds(10));
                            Console.WriteLine(
                                $"🔐 Telegram auth: state={status.State} configured={status.Configured}");
                        }
                        catch (Exception hydrateEx)
                        {
                            Console.WriteLine(
                                $"⚠️ Hidratação da sessão Telegram falhou: {hydrateEx.GetType().Name}");
                        }
                    });
                    var operationalReadiness = new OperationalReadinessService(
                        lifecycle,
                        adminUsers.HasActiveAdminAsync,
                        () => telegramAuth.IsAuthenticated,
                        dispatcharrService.Get,
                        ct => HasCanonicalChannelsAsync(webCatalogResolver, ct),
                        ct => Task.FromResult(CountryConfigProvisioner.IsCountryDataAvailable(
                            Path.Combine(Directory.GetCurrentDirectory(), "runtime-data", "countries"),
                            countryCode)),
                        () => IsOutputWritable(dashboardOutputDir),
                        ct => CountChannelSourcesAsync(webCatalogResolver, ct),
                        dispatcharrTestStore.Load);

                    // Wave 5 (PHASE 9C) — Expor os serviços de setup/config
                    // à API do dashboard (Telegram config/login, Dispatcharr
                    // config/teste, prontidão). Sem estes serviços os
                    // endpoints respondem 503 <serviço>-unavailable.
                    WebDashboardService.SetSetupServices(
                        telegramAuth,
                        dispatcharrService,
                        new DispatcharrConnectionTester(),
                        operationalReadiness,
                        dispatcharrTestStore);

                    try
                    {
                        var readinessSnapshot = await operationalReadiness.EvaluateAsync(CancellationToken.None);
                        if (!readinessSnapshot.SetupComplete)
                        {
                            var missing = readinessSnapshot.MissingRequired.Count == 0
                                ? "(sem componentes obrigatórios em falta)"
                                : string.Join(", ", readinessSnapshot.MissingRequired);
                            Console.WriteLine($"⚠️ setup operacional incompleto: {missing}");
                        }
                    }
                    catch (Exception readinessEx)
                    {
                        Console.WriteLine(
                            $"⚠️ Não foi possível avaliar a prontidão operacional: {readinessEx.Message}");
                    }

                    // PHASE 9C.4 — Host do Live Run (RunCoordinator único).
                    // É construído aqui (antes de automationHost.Start) e
                    // partilhado entre o dashboard e o bloco --telegram
                    // abaixo. Sem este host, o dashboard responde
                    // pipeline-not-configured (GET) e 503 (POST).
                    liveRunHost = new LiveRunHost(webCatalogResolver.GetFactory());
                    WebDashboardService.SetLiveRunHost(liveRunHost);
                    WebDashboardService.SetWebAllowTrigger(webAllowTrigger);

                    automationHost = ScheduledAutomationHost.Build(
                        webCatalogResolver,
                        dashboardOutputDir,
                        dispatcharrConfig,
                        gate: new ConfigurationGate(lifecycle),
                        liveRunHost: liveRunHost,
                        dispatcharrConfigLoader: DispatcharrConfigLoader.Load,
                        discoverySettings: discoverySettingsProvider,
                        capabilityGate: new ActionCapabilityGate(operationalReadiness));
                    WebDashboardService.SetScheduledActions(automationHost.RegisteredActions);
                    automationHost.Start();
                    Console.WriteLine("📅 Scheduler iniciado (polling de scheduled jobs).");
                    Console.WriteLine(
                        $"🕒 ScheduledJobRunner activo ({automationHost.RegisteredActions.Count} actions registadas).");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Catálogo não disponível para o dashboard: {ex.Message}");
                }

                webTask = WebDashboardService.RunDashboardAsync(dashboardOutputDir, webPort, dashboardHistoryService, webToken, processCts.Token);
                _ = webTask.ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                    {
                        Console.WriteLine($"❌ Dashboard task falhou: {t.Exception.GetBaseException().Message}");
                    }
                }, TaskContinuationOptions.OnlyOnFaulted);
            }

            // Lifecycle — Shutdown coerente: Ctrl+C e SIGTERM sinalizam o
            // token do processo, depois fazem drain do Scheduler (com
            // limite documentado para actions que não respeitam
            // cancellation), e por fim param o listener do Dashboard. Esta
            // ordem evita tarefas órfãs e garante que GetContextAsync()
            // não fica bloqueado indefinidamente.
            //
            // Limite de drain do Scheduler: se uma action pendurar e não
            // respeitar cancellation, o shutdown termina por timeout e é
            // registado explicitamente como terminado por força — nunca
            // mascarado como shutdown normal silencioso.
            const int SchedulerDrainTimeoutSeconds = 10;
            int shutdownInvocationCount = 0;

            void CoherentShutdown(string reason)
            {
                int invocation = System.Threading.Interlocked.Increment(ref shutdownInvocationCount);
                if (invocation > 1)
                {
                    return; // Shutdown já em curso; sinal único.
                }

                Console.WriteLine($"🛑 Shutdown solicitado ({reason}).");

                // 1-2) Sinalizar o token global e impedir novos trabalhos.
                processCts.Cancel();
                Console.WriteLine("🛑 Token de processo cancelado; novas execuções bloqueadas.");

                // 3-4) Pedir ao Scheduler para parar; drain cooperativo das
                //       actions em curso (recebem o token do runner).
                if (automationHost is not null)
                {
                    Console.WriteLine("🛑 Scheduler stopping...");
                    try
                    {
                        var stopTask = automationHost.StopAsync();
                        if (!stopTask.Wait(SchedulerDrainTimeoutSeconds * 1000))
                        {
                            Console.WriteLine(
                                "🛑 ⚠️ Scheduler não terminou dentro do limite de drain " +
                                $"({SchedulerDrainTimeoutSeconds}s). Shutdown continua por força — " +
                                "o Main não aguarda mais pelo runner.");
                        }
                        else
                        {
                            Console.WriteLine("🛑 Scheduler stopping... parado.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠️ Erro ao parar ScheduledJobRunner: {ex.Message}");
                    }
                }

                // 5) Parar o HttpListener — desbloqueia GetContextAsync()
                //    pendente e permite que o webTask devolva.
                try
                {
                    WebDashboardService.StopDashboard();
                    Console.WriteLine("🛑 Dashboard stopping... listener parado.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Erro ao parar listener do dashboard: {ex.Message}");
                }

                Console.WriteLine("🛑 Shutdown: cleanup orquestrado concluído; Main vai terminar.");
            }

            // Ctrl+C: sinaliza ao mecanismo de lifecycle — o shutdown
            // ordenado corre no callback sincronamente, mantendo o processo
            // vivo até cleanup estar concluído.
            ConsoleCancelEventHandler cancelHandler = (_, e) =>
            {
                e.Cancel = true;
                CoherentShutdown("Ctrl+C");
            };
            Console.CancelKeyPress += cancelHandler;

            // SIGTERM (docker stop, kill -TERM, systemd): ProcessExit corre
            // sincronamente e dá a oportunidade de cleanup ordenado.
            AppDomain.CurrentDomain.ProcessExit += (s, e) => CoherentShutdown("SIGTERM/ProcessExit");

            if (args.Contains("--telegram"))
            {
                // Wave W5 — Caminho de aplicação (dashboard/scheduler):
                // a pipeline reutiliza o cliente WTelegram autenticado do
                // TelegramAuthService. Sem serviço de aplicação (CLI
                // interactiva), mantém-se o fallback legacy que constrói
                // um cliente a partir de wtelegram.config.
                var scraper = applicationTelegramAuth is not null
                    ? new TelegramScraperService(applicationTelegramAuth)
                    : new TelegramScraperService();
                // PHASE-OBSERVABILITY (2026-09-15): activar tracing automaticamente
                // quando a env var M3UCRAWLER_TRACE esta' definida. Em modo
                // observabilidade (--telegram + M3UCRAWLER_TRACE=1), o pipeline
                // emite eventos estruturados com TraceContext, permitindo
                // reconstruir o percurso completo de qualquer mensagem/candidate.
                // Por defeito (sem a env var) o tracing e' NullTraceSink.Instance
                // (no-op), preservando o comportamento de producao.
                string? traceEnv = Environment.GetEnvironmentVariable("M3UCRAWLER_TRACE");
                if (!string.IsNullOrEmpty(traceEnv) && traceEnv != "0" && traceEnv.ToLowerInvariant() != "false")
                {
                    var pipelineTrace = new m3uCrawler.Services.Validation.PipelineTrace();
                    pipelineTrace.MinimumLevel = m3uCrawler.Services.Validation.TraceLevel.Debug;
                    scraper.SetTrace(pipelineTrace);
                    Console.WriteLine($"[OBSERVABILITY] PipelineTrace active runId={pipelineTrace.RunId} minimumLevel=Debug");
                }
                // Wave W5 — Arranque seguro numa instalação nova: o
                // Telegram pode ainda não estar configurado/autenticado.
                // A autenticação é uma operação de aplicação (dashboard),
                // pelo que o arranque não pode terminar por causa dela.
                // Se não estiver pronta, registamos um aviso não sensível,
                // não corremos o ciclo CLI e mantemos o dashboard vivo
                // para o Setup. O scheduler continua a respeitar o gate
                // por capacidade (W2) até estar autenticado.
                bool telegramReady = await TryAuthenticateTelegramForStartupAsync(
                    scraper, applicationTelegramAuth);

                var catalogDbPath = ResolveCatalogDbPath(args);
                IReadOnlyDictionary<string, IEnumerable<string>> countryAffinityMembers =
                    new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
                CatalogResolver? catalogForAffinity = null;
                try
                {
                    catalogForAffinity = await InitializeCatalogAsync(catalogDbPath, CancellationToken.None);
                    countryAffinityMembers = await LoadCountryAffinityMembersAsync(catalogForAffinity);
                    scraper.SetCountryAffinityMembers(countryAffinityMembers);
                    // W2-FU-1 (2026-09-22) — wire do resolver para o
                    // observer de falhas de aquisição. Sem catalog
                    // disponível, o scraper fica sem observer (no-op,
                    // preserva o comportamento legacy). Reutiliza a
                    // mesma instância de catalogForAffinity (já criada
                    // para afinidades) para evitar inicialização dupla.
                    scraper.SetCatalogResolver(catalogForAffinity);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Catálogo não disponível para injeção de afinidades: {ex.Message}");
                }

                // === Wave C — Discovery settings (fonte de verdade única) ===
                // Os parâmetros de discovery vivem em runtime-data/app_settings.json
                // (secção "discovery"). Um override explícito de CLI é persistido,
                // para que a próxima execução (CLI, dashboard ou scheduler) leia o
                // mesmo valor. Sem override, lê-se o valor persistido (default PT).
                // Termo explícito: argumentos após --telegram até à próxima opção.
                string? cliTerm = null;
                int telegramIndex = Array.IndexOf(args, "--telegram");
                if (telegramIndex >= 0 && telegramIndex < args.Length - 1)
                {
                    var remainingArgs = new List<string>();
                    for (int i = telegramIndex + 1; i < args.Length; i++)
                    {
                        if (args[i].StartsWith("--")) break;
                        remainingArgs.Add(args[i]);
                    }

                    if (remainingArgs.Any())
                    {
                        cliTerm = string.Join(" ", remainingArgs).Trim();
                    }
                }

                int? cliMaxStreams = null;
                var telegramMaxArg = GetOptionValue(args, "--max-streams");
                if (int.TryParse(telegramMaxArg, out int telegramParsedMax) && telegramParsedMax > 0)
                {
                    cliMaxStreams = Math.Min(telegramParsedMax, DiscoverySettings.OverrideMaxStreamsCeiling);
                }

                int? cliHistoryHours = null;
                var historyArg = GetOptionValue(args, "--history-hours");
                if (int.TryParse(historyArg, out int parsedHistoryHours)
                    && parsedHistoryHours >= DiscoverySettings.MinValidHistoryHours)
                {
                    cliHistoryHours = Math.Min(parsedHistoryHours, DiscoverySettings.MaxValidHistoryHours);
                }

                int? cliMinHistoryHours = null;
                var minHistoryArg = GetOptionValue(args, "--min-history-hours");
                if (int.TryParse(minHistoryArg, out int parsedMinHistoryHours)
                    && parsedMinHistoryHours >= 0)
                {
                    cliMinHistoryHours = Math.Min(parsedMinHistoryHours, DiscoverySettings.MaxValidHistoryHours);
                }

                // CLI alimenta a mesma configuração: só persiste o que foi
                // explicitamente indicado; os restantes campos mantêm-se.
                if (!string.IsNullOrWhiteSpace(cliTerm) || cliMaxStreams.HasValue || cliHistoryHours.HasValue || cliMinHistoryHours.HasValue)
                {
                    var persistedForWrite = appSettingsStore.Load();
                    if (!string.IsNullOrWhiteSpace(cliTerm)) persistedForWrite.Discovery.Keyword = cliTerm!;
                    if (cliHistoryHours.HasValue) persistedForWrite.Discovery.HistoryHours = cliHistoryHours.Value;
                    if (cliMinHistoryHours.HasValue) persistedForWrite.Discovery.MinHistoryHours = cliMinHistoryHours.Value;
                    if (cliMaxStreams.HasValue) persistedForWrite.Discovery.MaxStreams = cliMaxStreams.Value;
                    appSettingsStore.Save(persistedForWrite);
                }

                // Sem override explícito, o valor vem do store persistido
                // (nunca de um snapshot em memória capturado no arranque).
                var resolvedDiscovery = discoverySettingsProvider.Load();
                string term = resolvedDiscovery.Keyword;
                int telegramMaxStreams = resolvedDiscovery.MaxStreams;
                int telegramHistoryHours = resolvedDiscovery.HistoryHours;
                int telegramMinHistoryHours = resolvedDiscovery.MinHistoryHours;
                Console.WriteLine($"🔎 Termo de pesquisa Telegram: {term}");
                if (telegramMinHistoryHours > 0)
                {
                    Console.WriteLine($"🕒 Janela de pesquisa Telegram: mensagens com idade entre {telegramMinHistoryHours}h e {telegramHistoryHours}h");
                }
                else
                {
                    Console.WriteLine($"🕒 Janela de pesquisa Telegram: últimas {telegramHistoryHours}h");
                }
                Console.WriteLine($"🎯 Limite de streams Telegram: {telegramMaxStreams}");


                bool maintenanceMode = args.Contains("--telegram-maintain");

                int loopHours = 0;
                var loopArg = GetOptionValue(args, "--loop-hours");
                if (int.TryParse(loopArg, out int parsedLoop) && parsedLoop > 0)
                {
                    loopHours = parsedLoop;
                }

                var telegramPlaylistManager = new PlaylistManagerService();
                var outputDir = GetOptionValue(args, "--output-dir") ?? "output";
                telegramPlaylistManager.CreateOutputDirectory(outputDir);
                var importHistoryService = new ImportHistoryService(outputDir);
                var countryChannelValidator = new CountryChannelValidator(
                    Path.Combine(Directory.GetCurrentDirectory(), "runtime-data", "countries"),
                    countryAffinityMembers);
                Console.WriteLine($"📂 Pasta de saída das playlists: {Path.GetFullPath(outputDir)}");

                // PHASE-Bridge — Inicializar o ingestor de catálogo para o pipeline
                // Telegram. Se o catálogo falhar a inicializar (e.g. SQLite
                // bloqueada), o pipeline continua sem catalogação — a playlist
                // M3U continua a ser produzida.
                PipelineIngestionService? pipelineIngestor = null;
                CatalogResolver? catalogForIngestion = null;
                m3uCrawler.Services.Recognition.RecognitionPolicyResolver? recognitionPolicyResolver = null;
                try
                {
                    catalogForIngestion = await InitializeCatalogAsync(catalogDbPath, CancellationToken.None);
                    // R1 — O ingestor requer um CountryChannelValidator para
                    // garantir que streams REJECTED pelo country policy não
                    // são persistidos. O validator já está construído acima
                    // (linha 186), partilhando configuração com o scraper.
                    pipelineIngestor = new PipelineIngestionService(
                        catalogForIngestion, countryChannelValidator);
                    // D-M4-02 — resolver de policies partilhado por
                    // scraper (propagação) e RunCoordinator (criação do
                    // snapshot). Sem catálogo disponível ⇒ null ⇒ o
                    // wiring continua a funcionar sem snapshot machinery.
                    recognitionPolicyResolver = new m3uCrawler.Services.Recognition.RecognitionPolicyResolver(
                        catalogForIngestion);
                    scraper.SetRecognitionPolicyResolver(recognitionPolicyResolver);
                    Console.WriteLine("📦 Ingestor de catálogo inicializado.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Ingestor de catálogo não disponível: {ex.Message}");
                }

                // PHASE 9C.1 — Gate de configuração para discovery automático.
                // Manutenção (--telegram-maintain) e loop (--loop-hours) são
                // caminhos automáticos: em NOT_CONFIGURED/CONFIGURING ficam
                // bloqueados. Uma invocação manual de um único ciclo
                // (--telegram sem loop nem manutenção) é operador-iniciada e
                // não é afectada nesta wave.
                //
                // Wave 4 (PHASE 9C) — O caminho CLI manual/automático mantém
                // deliberadamente apenas o gate de lifecycle (sem readiness
                // operacional): continua a ser o operador a decidir quando
                // correr.
                //
                // Wave W2 — O scheduler/dashboard usa o gate global de
                // bootstrap (lifecycle) mais um gate POR CAPACIDADE
                // (ActionCapabilityGate): acções não-Telegram não ficam
                // bloqueadas só porque o Telegram não está autenticado.
                bool automaticDiscovery = maintenanceMode || loopHours > 0;
                var configurationLifecycle = BuildConfigurationLifecycle(
                    catalogDbPath, outputDir, catalogForIngestion);
                if (automaticDiscovery)
                {
                    try
                    {
                        LogLifecycleState(await configurationLifecycle.EnsureInitializedAsync(CancellationToken.None));
                    }
                    catch (Exception lifecycleEx)
                    {
                        Console.WriteLine($"⚠️ Não foi possível inicializar o estado de configuração: {lifecycleEx.Message}");
                    }
                }
                var configurationGate = new ConfigurationGate(configurationLifecycle);

                // PHASE 9C.4 — Live Run Monitor. O RunCoordinator é o
                // único ponto de orquestração da execução Telegram: CLI,
                // scheduler e dashboard convergem aqui. O coordinator é
                // obtido de duas formas:
                //  1. Via LiveRunHost, quando --web está activo (o host
                //     foi construído no top-level e é partilhado com o
                //     dashboard — um único coordinator para tudo).
                //  2. Via construção local, apenas quando --telegram corre
                //     sem --web (degradação graciosa, comportamento
                //     preservado).
                RunCoordinator? liveRunCoordinator = null;
                Exception? liveRunException = null;
                var countriesDirectory = Path.Combine(
                    Directory.GetCurrentDirectory(), "runtime-data", "countries");

                // Wave W2 — Serviço de publicação único: country gate →
                // selecção de fontes (política persistida) → publicação
                // atómica → run report → histórico → Dispatcharr. É a única
                // implementação da cauda do pipeline Telegram; CLI,
                // manutenção, dashboard e scheduler convergem aqui.
                var dispatcharrSyncCoordinator = new DispatcharrSyncCoordinator(
                    DispatcharrConfigLoader.Load);
                var publicationService = new RunPublicationService(
                    outputDir,
                    telegramPlaylistManager,
                    importHistoryService,
                    countryChannelValidator,
                    catalogForIngestion,
                    dispatcharrSyncCoordinator);

                // W6 — Expor a sincronização Dispatcharr ao Dashboard. É o
                // MESMO coordenador que serve o RunPublicationService, para
                // não existir um segundo caminho/source-of-truth. O gate
                // dedicado serializa os pedidos HTTP /dry-run e /sync
                // (segunda tentativa concorrente → 409). Additivo: o
                // comportamento do RunPublicationService e do scheduler
                // permanece inalterado. Em --web standalone (sem --telegram)
                // não há coordenador e os endpoints respondem 503
                // dispatcharr-unavailable — o que é o esperado.
                if (webEnabled)
                {
                    WebDashboardService.SetDispatcharrSync(
                        dispatcharrSyncCoordinator,
                        new DispatcharrConcurrencyGate());
                }

                // Wave W2 — Executor único da Live Run Telegram. Faz a
                // discovery e delega a cauda no publicationService. É o
                // mesmo executor usado pelo dashboard e pelo scheduler (via
                // RunCoordinator) e pela CLI quando --web está activo.
                var telegramLiveRunExecutor = new TelegramLiveRunExecutor(
                    discover: async (effectiveDiscovery, progress, ct) =>
                    {
                        var (streams, acquiredStreams, report) = await scraper.SearchAndTestM3UInTelegramAsync(
                            effectiveDiscovery.Keyword,
                            limit: 200,
                            maxConcurrency: 5,
                            maxUrlsToTest: effectiveDiscovery.MaxStreams,
                            historyHours: effectiveDiscovery.HistoryHours,
                            minHistoryHours: effectiveDiscovery.MinHistoryHours,
                            countryCode: countryCode,
                            countriesDir: countriesDirectory,
                            pipelineIngestor: pipelineIngestor,
                            pipelineSourceKey: $"telegram-{Slugify(effectiveDiscovery.Keyword)}",
                            liveRunProgress: progress,
                            cancellationToken: ct,
                            feedCanonicalFallback: effectiveDiscovery.FeedCanonicalFallback);
                        return new TelegramDiscoveryResult(streams, report, acquiredStreams);
                    },
                    maintain: (effectiveDiscovery, progress, ct) => RunTelegramMaintenanceCycle(
                        scraper,
                        telegramPlaylistManager,
                        importHistoryService,
                        publicationService,
                        effectiveDiscovery.Keyword,
                        outputDir,
                        effectiveDiscovery.MaxStreams,
                        domainFilter,
                        effectiveDiscovery.HistoryHours,
                        effectiveDiscovery.MinHistoryHours,
                        args,
                        pipelineIngestor,
                        countryCode,
                        countriesDirectory,
                        effectiveDiscovery.FeedCanonicalFallback,
                        progress,
                        ct),
                    publication: publicationService,
                    discoverySettings: discoverySettingsProvider,
                    countryCode: countryCode,
                    domainFilter: domainFilter);

                if (catalogForIngestion is not null)
                {
                    Func<LiveRunRequest, IRunPipeline> liveRunPipelineFactory = _ =>
                        new TelegramRunPipeline(async (request, progress, ct) =>
                        {
                            liveRunException = null;
                            try
                            {
                                await telegramLiveRunExecutor
                                    .ExecuteAsync(request, progress, ct);
                            }
                            catch (Exception ex)
                            {
                                // Preserva a semântica CLI anterior: a
                                // excepção é re-lançada depois de o
                                // coordinator fechar o run (Failed) e
                                // libertar o lock.
                                liveRunException = ex;
                                throw;
                            }
                        });

                    if (liveRunHost is not null)
                    {
                        liveRunCoordinator = liveRunHost.ConfigureExecutor(liveRunPipelineFactory);
                        // D-M4-02 — host devolve a instância única do
                        // coordinator; injectamos o resolver de policies
                        // para que o caminho dashboard herde o snapshot
                        // machinery sem alterar a superfície do host.
                        liveRunCoordinator.SetRecognitionPolicyResolver(
                            recognitionPolicyResolver);
                        Console.WriteLine("🔁 RunCoordinator partilhado com o dashboard (LiveRunHost).");
                    }
                    else
                    {
                        liveRunCoordinator = new RunCoordinator(
                            catalogForIngestion.GetFactory(), liveRunPipelineFactory,
                            recognitionPolicyResolver: recognitionPolicyResolver);
                    }

                    // PHASE 9C.4 — Recuperar runs interrompidos por crash
                    // anterior: marca-os como Failed antes de iniciar
                    // qualquer nova execução. Idempotente.
                    try
                    {
                        var recovered = await liveRunCoordinator
                            .RecoverInterruptedRunsAsync(CancellationToken.None);
                        if (recovered > 0)
                        {
                            Console.WriteLine(
                                $"🩹 RecoverInterruptedRunsAsync: {recovered} run(s) marcado(s) como Failed.");
                        }
                    }
                    catch (Exception recoveryEx)
                    {
                        // Não bloquear o startup por causa de falha de
                        // recovery: o coordinator continua utilizável.
                        Console.WriteLine(
                            $"⚠️ Falha em RecoverInterruptedRunsAsync: {recoveryEx.GetType().Name}: {recoveryEx.Message}");
                    }
                }

                if (!telegramReady)
                {
                    // Wave W5 — O executor já foi registado acima (quando o
                    // catálogo está disponível), pelo que uma autenticação
                    // concluída no dashboard habilita execuções
                    // agendadas/manuais no MESMO processo, sem reiniciar.
                    // Aqui apenas não corremos o ciclo CLI imediato.
                    if (webEnabled && webTask is not null)
                    {
                        await AwaitResidentDashboardAsync(webTask, automationHost,
                            $"🌐 Dashboard activo em http://+:{webPort}/ (Telegram pendente de Setup). " +
                            "CTRL+C para encerrar.");
                    }

                    return;
                }

                do
                {
                    if (automaticDiscovery && !await configurationGate.IsReadyAsync())
                    {
                        Console.WriteLine(
                            $"⛔ automatic discovery blocked: not configured (state={configurationGate.State.ToWireName()})");
                        if (loopHours <= 0)
                        {
                            if (webEnabled && webTask is not null)
                            {
                                await AwaitResidentDashboardAsync(webTask, automationHost,
                                    "⛔ Discovery automática bloqueada (lifecycle não READY). " +
                                    $"Processo residente activo: Dashboard+Scheduler em http://+:{webPort}/. CTRL+C para encerrar.");
                            }
                            return;
                        }
                        Console.WriteLine();
                        Console.WriteLine($"⏳ Próxima execução em {loopHours} hora(s)...");
                        await Task.Delay(TimeSpan.FromHours(loopHours));
                        continue;
                    }

                    // Wave C — Recarregar a configuração de discovery a cada
                    // iteração: uma edição no dashboard aplica-se ao ciclo
                    // seguinte sem reiniciar o processo.
                    var cycleDiscovery = discoverySettingsProvider.Load();

                    if (maintenanceMode)
                    {
                        if (liveRunCoordinator is not null)
                        {
                            liveRunException = null;
                            await liveRunCoordinator.StartAsync(new LiveRunRequest
                            {
                                Mode = LiveRunMode.TelegramMaintain,
                                Source = LiveRunSource.Cli,
                                Keyword = cycleDiscovery.Keyword,
                                HistoryHours = cycleDiscovery.HistoryHours,
                                MaxStreams = cycleDiscovery.MaxStreams,
                            }, CancellationToken.None);

                            if (liveRunException is not null)
                            {
                                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                                    .Capture(liveRunException).Throw();
                            }
                        }
                        else
                        {
                            await RunTelegramMaintenanceCycle(
                                scraper,
                                telegramPlaylistManager,
                                importHistoryService,
                                publicationService,
                                cycleDiscovery.Keyword,
                                outputDir,
                                cycleDiscovery.MaxStreams,
                                domainFilter,
                                cycleDiscovery.HistoryHours,
                                cycleDiscovery.MinHistoryHours,
                                args,
                                pipelineIngestor,
                                countryCode,
                                countriesDirectory,
                                cycleDiscovery.FeedCanonicalFallback);
                        }
                    }
                    else
                    {
                        if (liveRunCoordinator is not null)
                        {
                            liveRunException = null;
                            await liveRunCoordinator.StartAsync(new LiveRunRequest
                            {
                                Mode = LiveRunMode.Telegram,
                                Source = LiveRunSource.Cli,
                                Keyword = cycleDiscovery.Keyword,
                                HistoryHours = cycleDiscovery.HistoryHours,
                                MaxStreams = cycleDiscovery.MaxStreams,
                            }, CancellationToken.None);

                            if (liveRunException is not null)
                            {
                                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                                    .Capture(liveRunException).Throw();
                            }
                        }
                        else
                        {
                            // Sem coordinator (catálogo indisponível): a CLI
                            // executa a discovery e publica pelo MESMO serviço
                            // partilhado usado pelo dashboard e scheduler.
                            var (directStreams, directAcquiredStreams, directReport) = await scraper.SearchAndTestM3UInTelegramAsync(
                                cycleDiscovery.Keyword,
                                limit: 200,
                                maxConcurrency: 5,
                                maxUrlsToTest: cycleDiscovery.MaxStreams,
                                historyHours: cycleDiscovery.HistoryHours,
                                minHistoryHours: cycleDiscovery.MinHistoryHours,
                                countryCode: countryCode,
                                countriesDir: countriesDirectory,
                                pipelineIngestor: pipelineIngestor,
                                pipelineSourceKey: $"telegram-{Slugify(cycleDiscovery.Keyword)}",
                                feedCanonicalFallback: cycleDiscovery.FeedCanonicalFallback);

                            await publicationService.PublishAsync(new RunPublicationRequest
                            {
                                Streams = directStreams,
                                AcquiredStreams = directAcquiredStreams,
                                Report = directReport,
                                Keyword = cycleDiscovery.Keyword,
                                HistoryHours = cycleDiscovery.HistoryHours,
                                MaxStreams = cycleDiscovery.MaxStreams,
                                DomainFilter = domainFilter,
                                CountryCode = countryCode,
                                HistoryMode = "TelegramSearch",
                            }, null, CancellationToken.None);
                        }
                    }

                    if (loopHours > 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"⏳ Próxima execução em {loopHours} hora(s)...");
                        await Task.Delay(TimeSpan.FromHours(loopHours));
                    }
                }
                while (loopHours > 0);

                // Lifecycle residente — O ciclo Telegram one-shot terminou
                // (loopHours == 0). COM --web, o processo permanece vivo:
                // Dashboard + Scheduler + LiveRunHost continuam disponíveis
                // para execuções agendadas/manuais. SEM --web, termina
                // imediatamente (comportamento CLI one-shot preservado).
                if (webEnabled && webTask is not null)
                {
                    await AwaitResidentDashboardAsync(webTask, automationHost,
                        $"🌐 Ciclo Telegram concluído. Processo residente activo: " +
                        $"Dashboard+Scheduler em http://+:{webPort}/. CTRL+C para encerrar.");
                }

                return;
            }


            // BOT MODE
            if (args.Contains("--bot"))
            {
                Console.WriteLine("🤖 Iniciando Telegram Bot...");

                var botToken = GetOptionValue(args, "--bot-token")
                    ?? Environment.GetEnvironmentVariable("M3U_BOT_TOKEN");
                if (string.IsNullOrWhiteSpace(botToken))
                {
                    Console.Error.WriteLine("❌ Token do bot Telegram em falta. Fornece via --bot-token <token> ou variavel de ambiente M3U_BOT_TOKEN.");
                    return;
                }

                var botcrawler = new M3uCrawlerService();
                var bot = new TelegramBotService(botToken, botcrawler);

                bot.Start();

                Console.WriteLine("Bot ativo. Prima CTRL+C para sair.");
                await Task.Delay(-1); // Mantém o bot a correr
                return;
            }

            // DISPATCHARR STANDALONE SYNC
            if (args.Contains("--dispatcharr-sync"))
            {
                var outputDir = GetOptionValue(args, "--output-dir") ?? "output";
                var playlistPath = GetOptionValue(args, "--playlist")
                    ?? Path.Combine(outputDir, "playlist.m3u");

                if (!File.Exists(playlistPath))
                {
                    Console.WriteLine($"❌ Playlist não encontrada: {playlistPath}");
                    return;
                }

                // Legacy standalone path: sem stage de selecção nesta execução,
                // portanto selection fica null (sem correlação heurística).
                var standaloneConfig = DispatcharrConfigLoader.Load();
                CatalogResolver? standaloneCatalog = null;
                if (standaloneConfig.Enabled)
                {
                    try
                    {
                        standaloneCatalog = await InitializeCatalogAsync(
                            ResolveCatalogDbPath(args), CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(
                            $"❌ Catálogo indisponível. Sincronização abortada antes de qualquer " +
                            $"escrita HTTP. Erro: {ex.GetType().Name}");
                        return;
                    }
                }
                var standaloneSync = new DispatcharrSyncCoordinator(() => standaloneConfig);
                await standaloneSync.RunAsync(
                    playlistPath, outputDir, standaloneCatalog, selection: null,
                    liveRunProgress: null, CancellationToken.None);
                return;
            }

            // IMPORTAÇÃO DE EPG (XMLTV) → tvg-id curado dos canais canónicos.
            // One-shot, apenas catálogo (sem HTTP de escrita no Dispatcharr).
            if (args.Contains("--import-epg-tvg-ids"))
            {
                Environment.ExitCode = await RunImportEpgTvgIdsAsync(args);
                return;
            }

            // Check for help
            if (args.Contains("--help") || args.Contains("-h"))
            {
                ShowHelp();
                return;
            }

            if (args.Contains("--scan-domain"))
            {
                var scanDomain = GetOptionValue(args, "--scan-domain");
                if (string.IsNullOrWhiteSpace(scanDomain))
                {
                    Console.WriteLine("❌ Indica um domínio: --scan-domain exemplo.com");
                    return;
                }

                var crawlerScan = new M3uCrawlerService();
                try
                {
                    int maxResults = 200;
                    var maxArg = GetOptionValue(args, "--max-streams");
                    if (int.TryParse(maxArg, out int parsedMax) && parsedMax > 0)
                    {
                        maxResults = Math.Min(parsedMax, 2000);
                    }

                    var scanUser = GetOptionValue(args, "--user");
                    var scanPass = GetOptionValue(args, "--pass");

                    var result = await crawlerScan.ScanDomainForPlaylists(scanDomain, maxResults, scanUser, scanPass);

                    Console.WriteLine();

                    if (result.Playlists.Count > 0)
                    {
                        Console.WriteLine($"✅ {result.Playlists.Count} playlist(s) encontrada(s) em {scanDomain}:");
                        foreach (var url in result.Playlists.Take(20))
                            Console.WriteLine($"  • {CredentialSanitizer.SanitizeUrl(url)}");
                    }
                    else
                    {
                        Console.WriteLine($"ℹ️  Nenhuma playlist aberta encontrada em {scanDomain}.");
                    }

                    if (result.IptvPanels.Count > 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"🖥️  {result.IptvPanels.Count} painel(éis) IPTV detetado(s) — servidor está ativo mas requer credenciais:");
                        foreach (var url in result.IptvPanels)
                            Console.WriteLine($"  • {CredentialSanitizer.SanitizeUrl(url)}");
                    }

                    if (result.PlaylistTemplates.Count > 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine("🔑 Para obter a playlist com credenciais, usa:");
                        Console.WriteLine($"  dotnet run -- --scan-domain {scanDomain} --user SEU_USER --pass SEU_PASS");
                        Console.WriteLine();
                        Console.WriteLine("   ou acede diretamente a:");
                        foreach (var tpl in result.PlaylistTemplates)
                            Console.WriteLine($"  {CredentialSanitizer.SanitizeUrl(tpl)}");
                    }

                    if (result.Playlists.Count == 0 && result.IptvPanels.Count == 0)
                    {
                        Console.WriteLine("Não foram encontrados nem playlists nem painéis IPTV neste domínio.");
                    }
                }
                finally
                {
                    crawlerScan.Dispose();
                }

                return;
            }

            var crawler = new M3uCrawlerService();
            // 9A-PROD-WIRING: o tester é criado via factory a partir de um
            // state partilhado. Mesmo no modo M3U legacy isto alinha com o
            // resto do processo — se houver um tester state configurado
            // pelo dashboard, é o mesmo.
            var tester = StreamValidationTesterFactory.CreateTester(
                StreamValidationTesterFactory.CreateIsolatedState());
            var playlistManager = new PlaylistManagerService();

            // Se o dashboard standalone está activo, manter o processo vivo
            // enquanto o listener aceita pedidos. Caso contrário, cair no
            // modo M3U8-search legacy (prompts interactivos).
            if (webEnabled && webTask is not null)
            {
                await AwaitResidentDashboardAsync(webTask, automationHost,
                    $"🌐 Modo dashboard standalone activo. Aguardando pedidos em http://+:{webPort}/. CTRL+C para encerrar.");
                return;
            }

            Console.WriteLine("Iniciando m3uCrawler...");

            try
            {
                // Configurações
                var outputDir = GetOptionValue(args, "--output-dir") ?? "output";
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var playlistPath = Path.Combine(outputDir, $"playlist_{timestamp}.m3u");
                var reportPath = Path.Combine(outputDir, $"report_{timestamp}.json");

                playlistManager.CreateOutputDirectory(outputDir);
                Console.WriteLine($"📂 Pasta de saída das playlists: {Path.GetFullPath(outputDir)}");                // Obter termo de pesquisa do usuário ou argumentos
                string searchTerm;
                
                // Filter out known options from args to get search term
                var searchArgs = new List<string>();
                var skipWithValue = new HashSet<string> { "--max-streams", "--domain", "--web-port", "--web-token", "--bot-token", "--loop-hours", "--history-hours", "--min-history-hours", "--max-results", "--user", "--pass" };
                for (int i = 0; i < args.Length; i++)
                {
                    if (skipWithValue.Contains(args[i]))
                    {
                        i++; // Skip option value
                        continue;
                    }

                    if (!args[i].StartsWith("--"))
                    {
                        searchArgs.Add(args[i]);
                    }
                }
                
                if (searchArgs.Any())
                {
                    searchTerm = string.Join(" ", searchArgs);
                    Console.WriteLine($"Usando termo de pesquisa dos argumentos: {searchTerm}");
                }
                else
                {
                    Console.Write("Digite o termo de pesquisa para streams M3U8: ");
                    searchTerm = Console.ReadLine() ?? "";
                }

                if (string.IsNullOrWhiteSpace(searchTerm))
                {
                    Console.WriteLine("Termo de pesquisa não pode estar vazio!");
                    return;
                }                
                Console.WriteLine($"🔍 Procurando streams M3U8 para: {searchTerm}");                
                // Check for performance flags
                bool fastMode = args.Contains("--fast") || args.Contains("--high-performance");
                int concurrency = fastMode ? 20 : 10;
                
                    if (fastMode)
                {
                    Console.WriteLine("⚡ Modo alta performance ativado (20 conexões paralelas)");
                }
                
                // Configurar limite de streams
                int maxStreams = 500; // Increased default
                
                // Check for command line argument --max-streams
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i] == "--max-streams" && int.TryParse(args[i + 1], out int cmdMaxStreams))
                    {
                        maxStreams = Math.Min(cmdMaxStreams, 1000);
                        Console.WriteLine($"🎯 Limite de streams definido via comando: {maxStreams}");
                        break;
                    }
                }
                
                // Interactive prompt (unless set via command line)
                if (!args.Contains("--max-streams"))
                {
                    Console.Write($"Quantos streams testar? (padrão: {maxStreams}, máx: 1000): ");
                    var input = Console.ReadLine();
                    if (int.TryParse(input, out int userLimit) && userLimit > 0 && userLimit <= 1000)
                    {
                        maxStreams = userLimit;
                    }
                }
                Console.WriteLine($"🎯 Configurado para testar {maxStreams} streams");
                
                // Pesquisar URLs M3U8
                var foundUrls = await crawler.SearchM3u8Files(searchTerm, maxStreams);
                Console.WriteLine($"📋 Encontradas {foundUrls.Count} URLs M3U8");

                if (!string.IsNullOrWhiteSpace(domainFilter))
                {
                    int beforeFilter = foundUrls.Count;
                    foundUrls = foundUrls
                        .Where(url => UrlMatchesDomain(url, domainFilter))
                        .ToList();
                    Console.WriteLine($"🌐 Após filtro de domínio: {foundUrls.Count}/{beforeFilter} URLs");
                }

                if (foundUrls.Count == 0)
                {
                    Console.WriteLine("Nenhuma URL M3U8 encontrada. Tente um termo diferente.");
                    return;
                }                
                // Testar streams
                Console.WriteLine("\n🧪 Testando streams...");
                var testedStreams = await tester.TestMultipleStreams(foundUrls, concurrency);

                var workingStreams = testedStreams.Where(s => s.IsWorking).ToList();
                Console.WriteLine($"\n✅ Streams funcionais: {workingStreams.Count}/{testedStreams.Count}");

                if (workingStreams.Count > 0)
                {
                    // Guardar playlist M3U
                    await playlistManager.SaveToM3uPlaylist(testedStreams, playlistPath);
                    
                    // Guardar relatório JSON
                    await playlistManager.SaveToJsonReport(testedStreams, reportPath);

                    Console.WriteLine("\n📊 Estatísticas:");
                    Console.WriteLine($"   • Total testado: {testedStreams.Count}");
                    Console.WriteLine($"   • Funcionais: {workingStreams.Count}");
                    Console.WriteLine($"   • Não funcionais: {testedStreams.Count - workingStreams.Count}");
                    if (workingStreams.Any())
                        Console.WriteLine($"   • Tempo médio resposta: {workingStreams.Average(s => s.ResponseTime):F0}ms");
                    
                    Console.WriteLine($"\n✨ Arquivos gerados:");
                    Console.WriteLine($"   • Playlist: {playlistPath}");
                    Console.WriteLine($"   • Relatório: {reportPath}");
                }
                else
                {
                    Console.WriteLine("❌ Nenhum stream funcional encontrado.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Erro: {ex.Message}");
            }
            finally
            {
                crawler.Dispose();
                tester.Dispose();                Console.WriteLine("m3uCrawler finalizado.");
            }

            // Only prompt for key press if running interactively
            if (Environment.UserInteractive && !Console.IsInputRedirected)
            {
                Console.WriteLine("\nPressione qualquer tecla para sair...");
                Console.ReadKey();
            }
        }

        static void ShowHelp()
        {
            Console.WriteLine("m3uCrawler - Ferramenta para buscar e testar streams M3U8");
            Console.WriteLine();
            Console.WriteLine("USO:");
            Console.WriteLine("  m3uCrawler [TERMO_PESQUISA] [OPÇÕES]");
            Console.WriteLine();
            Console.WriteLine("PARÂMETROS:");
            Console.WriteLine("  TERMO_PESQUISA    Termo para buscar streams (ex: \"iptv portugal\")");
            Console.WriteLine();
            Console.WriteLine("OPÇÕES:");
            Console.WriteLine("  --max-streams N   Número máximo de streams para testar (1-1000)");
            Console.WriteLine("  --domain DOMINIO  Filtra resultados por domínio (ex: cdn.exemplo.com)");
            Console.WriteLine("  --country CODE    Valida playlists contra canais de um país (ex: pt, es, br) ");
            Console.WriteLine("  --output-dir PATH  Diretório onde guardar playlists e relatórios (padrão: output)");
            Console.WriteLine("  --web             Ativa uma interface web para ver histórico e playlist");
            Console.WriteLine("  --web-port N      Porta do servidor web (padrão: 5000)");
            Console.WriteLine("  --web-token TOKEN Bearer token para autorização no dashboard (protege timing-attack via FixedTimeEquals)");
            Console.WriteLine("  --web-allow-trigger   Permite POST /api/run/start a partir do dashboard (opt-in; default: 503)");
            Console.WriteLine("  --bot             Modo bot Telegram (legacy M3U8-search)");
            Console.WriteLine("  --bot-token TOKEN Token do bot Telegram (também via M3U_BOT_TOKEN); obrigatorio com --bot");
            Console.WriteLine("  --scan-domain D   Faz scan direto ao domínio para procurar playlists (sem Telegram)");
            Console.WriteLine("  --telegram-maintain Mantém output/playlist.m3u com base no Telegram e remove links mortos");
            Console.WriteLine("  --history-hours N Limite superior (em horas) da janela de pesquisa Telegram (padrão: 24; máximo: 1440)");
            Console.WriteLine("  --min-history-hours N Limite inferior (em horas) da idade das mensagens (padrão: 0 = legacy)");
            Console.WriteLine("  --loop-hours N    Repete execução a cada N horas (ex: 24)");
            Console.WriteLine("  --fast            Modo alta performance (20 conexões paralelas)");
            Console.WriteLine("  --high-performance Mesmo que --fast");
            Console.WriteLine("  --dispatcharr-sync  Sincroniza uma playlist M3U já existente com Dispatcharr (sem Telegram)");
            Console.WriteLine("  --playlist PATH    Caminho da playlist a sincronizar (default: <output-dir>/playlist.m3u)");
            Console.WriteLine("  --import-epg-tvg-ids URL|PATH  Importa uma EPG XMLTV (.xml/.xml.gz) e grava os ids dos canais como tvg-id curado dos canais canónicos do país");
            Console.WriteLine("  --epg-country CODE  País da EPG a importar (default: o país activo de --country, senão pt)");
            Console.WriteLine("  --admin-reset-password USERNAME  Recupera a password do administrador (host-only; password lida do stdin, nunca de argv)");
            Console.WriteLine("  --help, -h        Mostra esta ajuda");
            Console.WriteLine("  --version, -V     Mostra versão (SemVer + commit SHA + build number + data) e sai");
            Console.WriteLine();
            Console.WriteLine("EXEMPLOS:");
            Console.WriteLine("  m3uCrawler \"iptv portugal\"");
            Console.WriteLine("  m3uCrawler \"tv streams\" --max-streams 500");
            Console.WriteLine("  m3uCrawler \"canais tv\" --fast --max-streams 1000");
            Console.WriteLine("  m3uCrawler \"iptv\" --domain exemplo.com");
            Console.WriteLine("  m3uCrawler --scan-domain exemplo.com --max-streams 300");
            Console.WriteLine("  m3uCrawler --telegram portugal --telegram-maintain --loop-hours 24 --history-hours 24");
            Console.WriteLine();
            Console.WriteLine("CONFIGURAÇÃO:");
            Console.WriteLine("  • Edite config.json para configurações avançadas");
            Console.WriteLine("  • Limite padrão: 500 streams");
            Console.WriteLine("  • Conexões padrão: 10 paralelas (20 no modo --fast)");
            Console.WriteLine();
        }

        static string? GetOptionValue(string[] args, string optionName)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == optionName)
                    return args[i + 1];
            }

            return null;
        }

        /// <summary>
        /// Lifecycle residente — mantém o processo vivo enquanto o
        /// Dashboard/Scheduler estiverem activos. Junta-se ao webTask
        /// (que termina quando o token de processo for cancelado ou o
        /// listener for parado) e depois liberta o Scheduler. É o padrão
        /// único usado nos QUATRO caminhos residentes:
        ///   - <c>--telegram --web</c> com Telegram pendente de Setup;
        ///   - <c>--telegram --web --telegram-maintain</c> com lifecycle
        ///     não READY (discovery automática bloqueada, loopHours==0);
        ///   - <c>--telegram --web</c> após o ciclo one-shot (loopHours==0);
        ///   - <c>--web</c> standalone.
        /// </summary>
        internal static async Task AwaitResidentDashboardAsync(
            Task? webTask,
            ScheduledAutomationHost? automationHost,
            string modeMessage)
        {
            Console.WriteLine(modeMessage);
            if (webTask is null)
            {
                return;
            }

            try
            {
                await webTask;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Dashboard task falhou: {ex.GetBaseException().Message}");
            }

            automationHost?.Dispose();
        }

        /// <summary>
        /// Wave W5 — Prepara o Telegram no arranque sem terminar o processo.
        ///
        /// <para>
        /// No caminho de aplicação (<paramref name="applicationAuth"/> não
        /// nulo) consulta o <see cref="TelegramAuthService"/>; uma
        /// instalação nova sem <c>wtelegram.config</c>/<c>session.dat</c>
        /// resolve rapidamente para <c>false</c> sem rede nem consola. No
        /// caminho legacy (CLI interactiva) autentica o cliente de consola.
        /// </para>
        ///
        /// <para>
        /// Qualquer falha é convertida num aviso não sensível e em
        /// <c>false</c>: o chamador mantém o dashboard vivo para o Setup.
        /// </para>
        /// </summary>
        internal static async Task<bool> TryAuthenticateTelegramForStartupAsync(
            TelegramScraperService scraper,
            TelegramAuthService? applicationAuth,
            TimeSpan? timeout = null)
        {
            try
            {
                if (applicationAuth is not null)
                {
                    var status = await applicationAuth
                        .GetStatusAsync(CancellationToken.None)
                        .WaitAsync(timeout ?? TimeSpan.FromSeconds(20));

                    if (status.State == TelegramAuthState.Authenticated)
                    {
                        Console.WriteLine(
                            $"🔐 Telegram autenticado: {status.UserName ?? "(conta)"}");
                        return true;
                    }

                    Console.WriteLine(
                        "⚠️ Telegram ainda não autenticado " +
                        $"(estado={status.State}). Conclua o Setup no dashboard; " +
                        "o ciclo Telegram automático fica bloqueado até lá.");
                    return false;
                }

                await scraper.LoginAsync();
                return true;
            }
            catch (Exception ex)
            {
                // Nunca expor segredos: apenas o tipo da excepção.
                Console.WriteLine(
                    "⚠️ Telegram indisponível no arranque " +
                    $"({ex.GetType().Name}). O dashboard permanece disponível " +
                    "para Setup; o ciclo Telegram automático fica bloqueado.");
                return false;
            }
        }

        /// <summary>
        /// W10a — Recuperação de password do administrador via host (CLI).
        ///
        /// <para>
        /// A password NUNCA é um argumento de linha de comandos (ficaria no
        /// histórico do shell e na lista de processos). Só o username é lido de
        /// <c>argv</c>; a nova password e a confirmação são lidas do stdin.
        /// </para>
        /// <para>
        /// Leitura: se <see cref="Console.IsInputRedirected"/> for verdadeiro
        /// (input canalizado/automação) usa-se <c>Console.ReadLine()</c>; caso
        /// contrário usa-se <c>Console.ReadKey(intercept:true)</c> com eco
        /// mascarado (<c>*</c>), para a password não aparecer no terminal.
        /// Nota/limitação: em alguns contentores sem TTY reconhecido pelo .NET,
        /// <c>IsInputRedirected</c> é <c>true</c> mesmo com <c>docker run -it</c>,
        /// caindo-se no caminho <c>ReadLine</c> onde o eco canónico do terminal
        /// mostra os caracteres; a máscara não é garantida nesse cenário.
        /// </para>
        /// </summary>
        static async Task<int> RunAdminResetPasswordAsync(string[] args)
        {
            var username = GetOptionValue(args, "--admin-reset-password");
            if (string.IsNullOrWhiteSpace(username))
            {
                Console.Error.WriteLine("Uso: m3uCrawler --admin-reset-password <username>");
                return 1;
            }

            CatalogResolver catalog;
            try
            {
                catalog = await InitializeCatalogAsync(
                    ResolveCatalogDbPath(args), CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Mensagem de erro sem qualquer valor de password/hash.
                Console.Error.WriteLine($"❌ Catálogo não disponível: {ex.Message}");
                return 1;
            }

            var users = new AdminUserStore(catalog.GetFactory());
            var service = new AdminPasswordResetService(users);

            var promptIndex = 0;
            Func<string> passwordReader = () =>
            {
                Console.Write(promptIndex++ == 0
                    ? "Nova password: "
                    : "Confirmar nova password: ");

                if (Console.IsInputRedirected)
                {
                    return Console.ReadLine() ?? string.Empty;
                }

                var masked = ReadMaskedPassword();
                Console.WriteLine();
                return masked;
            };

            AdminPasswordResetOutcome outcome;
            try
            {
                outcome = await service.ResetAsync(username, passwordReader, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Nunca imprimir valores da password; apenas tipo/mensagem.
                Console.Error.WriteLine($"❌ Falha ao alterar a password: {ex.GetType().Name}");
                return 1;
            }

            switch (outcome)
            {
                case AdminPasswordResetOutcome.Changed:
                    Console.WriteLine("password alterada");
                    return 0;
                case AdminPasswordResetOutcome.UserNotFound:
                    Console.WriteLine("utilizador não encontrado");
                    return 2;
                case AdminPasswordResetOutcome.InvalidPassword:
                    Console.WriteLine("password inválida");
                    return 3;
                default:
                    Console.WriteLine("passwords não coincidem");
                    return 4;
            }
        }

        /// <summary>
        /// Lê uma linha sem eco, escrevendo <c>*</c> por cada carácter. Suporta
        /// backspace. Usado apenas em terminais interactivos (quando
        /// <see cref="Console.IsInputRedirected"/> é falso).
        /// </summary>
        static string ReadMaskedPassword()
        {
            var buffer = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    return buffer.ToString();
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                        Console.Write("\b \b");
                    }
                    continue;
                }

                if (key.KeyChar != '\0')
                {
                    buffer.Append(key.KeyChar);
                    Console.Write('*');
                }
            }
        }

        static bool UrlMatchesDomain(string url, string domainFilter)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

            string host = uri.Host;
            return host.Equals(domainFilter, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith($".{domainFilter}", StringComparison.OrdinalIgnoreCase);
        }

        static string Slugify(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "unknown";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (c == ' ' || c == '-' || c == '_') sb.Append('-');
            }
            var slug = sb.ToString().Trim('-');
            return string.IsNullOrEmpty(slug) ? "unknown" : slug;
        }

        static async Task RunTelegramMaintenanceCycle(
            TelegramScraperService scraper,
            PlaylistManagerService playlistManager,
            ImportHistoryService importHistory,
            IRunPublicationService publication,
            string term,
            string outputDir,
            int telegramMaxStreams,
            string? domainFilter,
            int telegramHistoryHours,
            int telegramMinHistoryHours,
            string[] args,
            PipelineIngestionService? pipelineIngestor,
            string countryCode = "pt",
            string? countriesDir = null,
            bool feedCanonicalFallback = true,
            ILiveRunProgress? liveRunProgress = null,
            CancellationToken cancellationToken = default)
        {
            var mainPath = Path.Combine(outputDir, "playlist.m3u");

            Console.WriteLine();
            Console.WriteLine("🧹 Início do ciclo de manutenção Telegram...");

            var (freshStreams, acquiredStreams, runReport) = await scraper.SearchAndTestM3UInTelegramAsync(
                term,
                limit: 200,
                maxConcurrency: 5,
                maxUrlsToTest: telegramMaxStreams,
                historyHours: telegramHistoryHours,
                minHistoryHours: telegramMinHistoryHours,
                countryCode: countryCode,
                countriesDir: countriesDir,
                report: new RunReport(),
                pipelineIngestor: pipelineIngestor,
                pipelineSourceKey: $"telegram-{Slugify(term)}",
                liveRunProgress: liveRunProgress,
                cancellationToken: cancellationToken,
                feedCanonicalFallback: feedCanonicalFallback);

            liveRunProgress?.ReportCounts(runReport);

            if (!string.IsNullOrWhiteSpace(domainFilter))
            {
                int beforeFilter = freshStreams.Count;
                freshStreams = freshStreams
                    .Where(s => UrlMatchesDomain(s.Url, domainFilter))
                    .ToList();
                Console.WriteLine($"🌐 Após filtro de domínio: {freshStreams.Count}/{beforeFilter} streams");
            }

            var existingMain = await playlistManager.LoadFromM3uPlaylist(mainPath);
            Console.WriteLine($"📄 playlist.m3u atual: {existingMain.Count} stream(s) a retestar");

            List<M3uStream> stillWorkingMain;
            if (existingMain.Count > 0)
            {
                // 9A-PROD-WIRING: tester criado via factory, ligado a um
                // state partilhado para que cache e HostFailureTracker
                // sobrevivam entre ciclos. Em producao o state vem do
                // StreamValidationPolicyStore (carregado abaixo).
                StreamValidationState? state = null;
                try
                {
                    var runtimeDir = Path.Combine(Directory.GetCurrentDirectory(), "runtime-data");
                    var store = new StreamValidationPolicyStore(runtimeDir);
                    state = StreamValidationTesterFactory.CreateStateFromStore(store);
                }
                catch
                {
                    // Em testes ou em modo M3U legacy, o runtime-data
                    // pode nao existir. Fallback para state isolado.
                    state = StreamValidationTesterFactory.CreateIsolatedState();
                }

                var tester = StreamValidationTesterFactory.CreateTester(state);
                try
                {
                    Console.WriteLine($"🔁 Re-testando {existingMain.Count} stream(s) existentes de playlist.m3u...");

                    // 9A-PROD-WIRING: TestManyBoundedAsync substitui o
                    // Task.WhenAll explosivo. Cria no maximo
                    // options.MaxConcurrency workers activos em vez de
                    // N Tasks. Mantem a ORDEM dos resultados.
                    var requests = existingMain
                        .Select(s => (Url: s.Url, Title: s.Title, Group: s.Group))
                        .ToList();
                    var retested = await tester.TestManyBoundedAsync(requests);

                    // Soft-filter: working OR retryable -> mantido.
                    // Apenas falhas deterministicas/terminais removem o stream.
                    var filterResult = TelegramScraperService.FilterRetainedStreams(
                        retested.Select(x => (x.Stream, x.Outcome.FailureKind)));

                    stillWorkingMain = filterResult.Preserved;

                    Console.WriteLine($"✅ Streams existentes preservados: {stillWorkingMain.Count}/{existingMain.Count}");
                    Console.WriteLine($"   • Working: {filterResult.PreservedWorking}");
                    Console.WriteLine($"   • Retryable (preservados): {filterResult.PreservedRetryable}");
                    Console.WriteLine($"   • Terminal (removidos): {filterResult.RemovedTerminal}");
                    if (filterResult.RemovedByKind.Count > 0)
                    {
                        Console.WriteLine("   • Detalhe de remoções terminais: " +
                            string.Join(", ", filterResult.RemovedByKind.Select(kv => $"{kv.Key}={kv.Value}")));
                    }
                    var retryableBreakdown = string.Join(", ",
                        filterResult.PreservedByKind.Where(kv => kv.Key != "Working")
                                                    .Select(kv => $"{kv.Key}={kv.Value}"));
                    if (!string.IsNullOrEmpty(retryableBreakdown))
                    {
                        Console.WriteLine($"   • Detalhe de preservações retryable: {retryableBreakdown}");
                    }
                }
                finally
                {
                    tester.Dispose();
                }
            }
            else
            {
                stillWorkingMain = new List<M3uStream>();
            }

            // Se não houve novas descobertas, NÃO se apagam os streams existentes.
            // PHASE 9C.4 — COMPOSING: composição da playlist alvo
            // (merge dos streams retestados com as novas descobertas).
            if (liveRunProgress is not null)
            {
                await liveRunProgress.EnterPhaseAsync(
                    LiveRunPhase.Composing, "composing target playlist", cancellationToken)
                    .ConfigureAwait(false);
            }
            var finalStreams = TelegramScraperService.MergeStreams(stillWorkingMain, freshStreams);

            // Wave W2 — A selecção de fontes, a publicação atómica, o run
            // report, o histórico e o sync Dispatcharr passam pelo serviço
            // de publicação único. Zero duplicação entre manutenção e ciclo
            // único: ambos produzem o mesmo contrato.
            var published = await publication.PublishAsync(
                new RunPublicationRequest
                {
                    Streams = finalStreams,
                    AcquiredStreams = acquiredStreams,
                    Report = runReport,
                    Keyword = term,
                    HistoryHours = telegramHistoryHours,
                    MaxStreams = telegramMaxStreams,
                    CountryCode = countryCode,
                    HistoryMode = "TelegramMaintenance",
                    PlaylistFileName = "playlist.m3u",
                    JsonReportFileName = "telegram_maintain_report.json",
                    RunCountryGate = false,
                    Verbose = false,
                    NewFunctionalCountOverride = freshStreams.Count(s => s.IsWorking),
                    ExistingRetestedCount = existingMain.Count,
                    ExistingStillWorkingCount = stillWorkingMain.Count,
                    TrackFinalPlaylistCount = true,
                },
                liveRunProgress,
                cancellationToken).ConfigureAwait(false);

            liveRunProgress?.ReportCounts(counts =>
            {
                counts.TargetPlaylistEntries = published.Published.Count;
                counts.ExistingPlaylistRetested = existingMain.Count;
            });

            Console.WriteLine("✅ Ciclo concluído.");
            Console.WriteLine($"   • Mantidas de playlist.m3u: {stillWorkingMain.Count}");
            Console.WriteLine($"   • Novas funcionais descobertas: {freshStreams.Count(s => s.IsWorking)}");
            Console.WriteLine($"   • Total final em playlist.m3u: {published.Published.Count}");
            Console.WriteLine($"   • playlist_temp.m3u: {published.IntermediatePlaylistPath}");
            Console.WriteLine($"   • playlist.m3u: {mainPath}");
            Console.WriteLine($"   • Relatório de execução: {Path.Combine(outputDir, "telegram_run_report.json")}");
        }

        /// <summary>
        /// Carrega os membros de afinidade <c>Kind=Country</c> do catálogo
        /// como um mapa país→aliases. São injectados na construção de cada
        /// <see cref="CountryChannelValidator"/>; não existe estado estático
        /// partilhado. Estes membros são classificadores de país e não criam
        /// identidade de canal.
        /// </summary>
        static async Task<IReadOnlyDictionary<string, IEnumerable<string>>> LoadCountryAffinityMembersAsync(CatalogResolver catalog)
        {
            var result = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
            var groups = await catalog.ListAffinityGroupsAsync();
            var byCountry = groups
                .Where(g => g.Kind == AffinityKind.Country
                    && !string.IsNullOrWhiteSpace(g.CountryCode))
                .GroupBy(g => g.CountryCode!.ToLowerInvariant());
            foreach (var group in byCountry)
            {
                var members = group
                    .SelectMany(g => g.Members)
                    .Select(m => m.NormalizedMember)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (members.Count > 0)
                {
                    result[group.Key] = members;
                    Console.WriteLine($"  [{group.Key}] {members.Count} membro(s) de afinidade injetados");
                }
            }

            return result;
        }

        /// <summary>
        /// Caminho por defeito do ficheiro SQLite do catálogo de
        /// canais. Em produção (container) é <c>/data/channel-catalog.db</c>
        /// (mesmo directório de <c>wtelegram.config</c> e
        /// <c>session.dat</c>, montado como bind mount). Pode ser
        /// sobreposto por <c>--catalog-db PATH</c> em testes.
        /// </summary>
        const string DefaultCatalogDbPath = "/data/channel-catalog.db";

        static string ResolveCatalogDbPath(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--catalog-db")
                {
                    return args[i + 1];
                }
            }
            return DefaultCatalogDbPath;
        }

        /// <summary>
        /// Inicializa o catálogo persistente e devolve um
        /// <see cref="CatalogResolver"/> pronto a injectar no
        /// <c>ChannelMatcher</c> e no <c>DispatcharrSyncService</c>.
        /// </summary>
        /// <remarks>
        /// Se o bootstrap ou a migration falhar, aborta com
        /// exception — não continuamos em modo "legacy" silencioso.
        /// Em produção a primeira falha de migration é tratada
        /// como erro fatal (ver requisito 2 do brief).
        /// </remarks>
        static async Task<CatalogResolver> InitializeCatalogAsync(
            string dbPath, CancellationToken ct)
        {
            Console.WriteLine($"📦 Inicializando catálogo persistente: {dbPath}");

            // Provisiona a baseline de países antes de qualquer
            // CountryChannelValidator/CountryChannelListService ser
            // construído (vale para --web e --telegram).
            CountryConfigProvisioner.EnsureProvisioned(
                Path.Combine(Directory.GetCurrentDirectory(), "runtime-data", "countries"));

            var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
            await using var context = await bootstrapper.InitializeAsync(ct);
            await context.DisposeAsync();
            var factory = new RuntimeChannelCatalogDbContextFactory(dbPath);
            // W5.4 — lifecycle de Review auditado na camada de serviço.
            return new CatalogResolver(factory, dbPath, new AuditService(factory));
        }

        /// <summary>
        /// One-shot: importa os <c>id</c>s de uma EPG XMLTV como identidade
        /// externa <c>tvg-id</c> (<see cref="ExternalIdentityNamespaces.TvgId"/>)
        /// dos canais canónicos do país, para o sync do Dispatcharr passar a
        /// emitir <c>tvg_id</c>. Requer catálogo. Não faz HTTP de escrita no
        /// Dispatcharr. Código de saída: 0 em sucesso (mesmo com conflitos),
        /// ≠ 0 em erro de catálogo/rede/parse.
        /// </summary>
        internal static async Task<int> RunImportEpgTvgIdsAsync(string[] args)
        {
            var source = GetOptionValue(args, "--import-epg-tvg-ids");
            if (string.IsNullOrWhiteSpace(source))
            {
                Console.Error.WriteLine("❌ Indica a origem da EPG: --import-epg-tvg-ids <url|path>");
                return 2;
            }

            // O país da EPG herda o país activo da CLI (--country; default "pt"),
            // sobreponível por --epg-country.
            var activeCountry = GetOptionValue(args, "--country") ?? "pt";
            var epgCountry = (GetOptionValue(args, "--epg-country") ?? activeCountry)
                .Trim().ToLowerInvariant();

            CatalogResolver catalog;
            try
            {
                catalog = await InitializeCatalogAsync(ResolveCatalogDbPath(args), CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"❌ Catálogo indisponível. Importação de EPG abortada antes de qualquer escrita. " +
                    $"Erro: {ex.GetType().Name}");
                return 3;
            }

            IReadOnlyList<EpgChannel> epgChannels;
            try
            {
                epgChannels = await LoadAndParseEpgAsync(source, CancellationToken.None);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                or XmlException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Console.Error.WriteLine($"❌ Falha ao obter/parsar a EPG: {ex.Message}");
                return 4;
            }

            var allChannels = await catalog.ListCanonicalChannelsAsync(CancellationToken.None);
            var countryChannels = allChannels
                .Where(c => string.IsNullOrWhiteSpace(c.Country)
                            || string.Equals(c.Country.Trim(), epgCountry, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var plan = EpgChannelMapper.Plan(countryChannels, epgChannels, epgCountry);

            int created = 0, unchanged = 0, conflicts = 0, ignored = 0;
            var conflictDetails = new List<string>();
            foreach (var mapping in plan.Mappings)
            {
                var outcome = await catalog.RecordExternalIdentityAsync(
                    mapping.CanonicalChannelId,
                    providerId: null,
                    @namespace: ExternalIdentityNamespaces.TvgId,
                    rawValue: mapping.EpgId,
                    origin: "epg-import",
                    confidence: 1.0,
                    CancellationToken.None);

                switch (outcome)
                {
                    case RecordExternalIdentityOutcome.Created:
                        created++;
                        break;
                    case RecordExternalIdentityOutcome.Unchanged:
                        unchanged++;
                        break;
                    case RecordExternalIdentityOutcome.Conflict:
                        conflicts++;
                        conflictDetails.Add(
                            $"{mapping.ChannelKey} → '{mapping.EpgId}' (já pertence a outro canal; não sobreposto)");
                        break;
                    default:
                        ignored++;
                        break;
                }
            }

            Console.WriteLine();
            Console.WriteLine($"📺 Mapeamento EPG → tvg-id (país '{epgCountry}')");
            Console.WriteLine($"  EPG: {epgChannels.Count} canal(ais) lido(s) de {DescribeSource(source)}");
            Console.WriteLine($"  Canais canónicos do país: {countryChannels.Count}");
            Console.WriteLine(
                $"  Mapeados: {plan.Mappings.Count}  |  Não casados: {plan.Unmatched.Count}  |  " +
                $"Ambíguos: {plan.Ambiguous.Count}  |  Incertos: {plan.Uncertain.Count}");
            Console.WriteLine(
                $"  Identidades tvg-id: {created} criada(s), {unchanged} já existente(s), " +
                $"{conflicts} conflito(s), {ignored} ignorada(s)");

            foreach (var warning in plan.Warnings)
            {
                Console.WriteLine($"  ⚠️  {warning}");
            }

            foreach (var conflict in conflictDetails)
            {
                Console.WriteLine($"  ⚠️  conflito: {conflict}");
            }

            if (plan.Ambiguous.Count > 0)
            {
                Console.WriteLine("  Ambíguos (não mapeados):");
                foreach (var item in plan.Ambiguous.Take(20))
                {
                    Console.WriteLine($"    • {item.ChannelKey}: {string.Join(", ", item.CandidateIds)}");
                }
            }

            if (plan.Uncertain.Count > 0)
            {
                Console.WriteLine("  Incertos (variante não-normalizada; não mapeados):");
                foreach (var item in plan.Uncertain.Take(20))
                {
                    Console.WriteLine($"    • {item.ChannelKey}: {item.CandidateId}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"✅ Importação de EPG concluída: {created} nova(s) identidade(s) tvg-id.");
            return 0;
        }

        /// <summary>
        /// Obtém o XMLTV de <paramref name="source"/> (URL http/https ou
        /// ficheiro), descomprime gzip (por extensão <c>.gz</c>, cabeçalho
        /// <c>Content-Encoding</c> ou <i>magic bytes</i>) e extrai os canais.
        /// </summary>
        private static async Task<IReadOnlyList<EpgChannel>> LoadAndParseEpgAsync(
            string source, CancellationToken ct)
        {
            if (LooksLikeUrl(source))
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                using var response = await client.GetAsync(
                    source, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                await using var network = await response.Content.ReadAsStreamAsync(ct);
                var prefix = new byte[2];
                var prefixCount = await ReadAtMostAsync(network, prefix, ct);
                var gzip = source.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                    || response.Content.Headers.ContentEncoding.Any(
                        e => e.Contains("gzip", StringComparison.OrdinalIgnoreCase))
                    || LooksGzip(prefix, prefixCount);

                await using var content = new PrefixStream(prefix, prefixCount, network);
                if (gzip)
                {
                    await using var decompressed = new GZipStream(content, CompressionMode.Decompress);
                    return await EpgChannelMapper.ParseAsync(decompressed, ct);
                }

                return await EpgChannelMapper.ParseAsync(content, ct);
            }

            await using var file = new FileStream(
                source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            var magic = new byte[2];
            var magicCount = await ReadAtMostAsync(file, magic, ct);
            var isGzip = source.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                || LooksGzip(magic, magicCount);

            await using var body = new PrefixStream(magic, magicCount, file);
            if (isGzip)
            {
                await using var decompressed = new GZipStream(body, CompressionMode.Decompress);
                return await EpgChannelMapper.ParseAsync(decompressed, ct);
            }

            return await EpgChannelMapper.ParseAsync(body, ct);
        }

        private static bool LooksLikeUrl(string source)
            => source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        private static string DescribeSource(string source)
            => LooksLikeUrl(source) ? CredentialSanitizer.SanitizeUrl(source) : source;

        private static bool LooksGzip(byte[] prefix, int count)
            => count >= 2 && prefix[0] == 0x1F && prefix[1] == 0x8B;

        private static async Task<int> ReadAtMostAsync(Stream stream, byte[] buffer, CancellationToken ct)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
                if (read == 0) break;
                total += read;
            }

            return total;
        }

        /// <summary>
        /// Devolve primeiro os bytes já lidos (prefixo) e depois o resto do
        /// stream — permite detetar gzip por <i>magic bytes</i> sem perder o
        /// início do conteúdo.
        /// </summary>
        private sealed class PrefixStream : Stream
        {
            private readonly byte[] _prefix;
            private readonly int _prefixCount;
            private readonly Stream _inner;
            private int _prefixPosition;
            private bool _disposed;

            public PrefixStream(byte[] prefix, int prefixCount, Stream inner)
            {
                _prefix = prefix;
                _prefixCount = prefixCount;
                _inner = inner;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_prefixPosition < _prefixCount)
                {
                    var available = Math.Min(count, _prefixCount - _prefixPosition);
                    Array.Copy(_prefix, _prefixPosition, buffer, offset, available);
                    _prefixPosition += available;
                    return available;
                }

                return _inner.Read(buffer, offset, count);
            }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_prefixPosition < _prefixCount)
                {
                    var available = Math.Min(buffer.Length, _prefixCount - _prefixPosition);
                    _prefix.AsMemory(_prefixPosition, available).CopyTo(buffer);
                    _prefixPosition += available;
                    return available;
                }

                return await _inner.ReadAsync(buffer, cancellationToken);
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing)
                {
                    _inner.Dispose();
                    _disposed = true;
                }

                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                if (!_disposed)
                {
                    await _inner.DisposeAsync();
                    _disposed = true;
                }

                GC.SuppressFinalize(this);
            }
        }

        /// <summary>
        /// PHASE 9C.1 — Constrói o serviço de lifecycle de configuração.
        /// O estado é persistido ao lado do <c>channel-catalog.db</c>
        /// (mesmo volume persistente). Não faz I/O; o bootstrap é
        /// explícito via <c>EnsureInitializedAsync</c>.
        /// </summary>
        static ConfigurationLifecycleService BuildConfigurationLifecycle(
            string catalogDbPath, string outputDir, CatalogResolver? catalog)
        {
            var store = ConfigurationLifecycleStore.ForCatalogDatabase(catalogDbPath);
            return new ConfigurationLifecycleService(store, catalog?.GetFactory(), outputDir);
        }

        /// <summary>
        /// Wave 4 (PHASE 9C) — Probe de prontidão: o catálogo canónico tem
        /// pelo menos um canal. Fail-safe (nunca lança).
        /// </summary>
        static async Task<bool> HasCanonicalChannelsAsync(CatalogResolver catalog, CancellationToken ct)
        {
            var stats = await catalog.GetStatsAsync(ct);
            return stats.CanonicalChannels >= 1;
        }

        /// <summary>
        /// Wave 4 (PHASE 9C) — Probe de prontidão: número de
        /// <c>channel_sources</c> realmente ingeridas. Fail-safe (devolve 0
        /// em erro). Não é requisito de SetupComplete.
        /// </summary>
        static async Task<int> CountChannelSourcesAsync(CatalogResolver catalog, CancellationToken ct)
        {
            var stats = await catalog.GetStatsAsync(ct);
            return stats.ChannelSources;
        }

        /// <summary>
        /// Wave 4 (PHASE 9C) — Probe de output: a pasta existe e é
        /// gravável. Cria a pasta se necessário; nunca lança.
        /// </summary>
        static bool IsOutputWritable(string outputDir)
        {
            if (string.IsNullOrWhiteSpace(outputDir))
            {
                return false;
            }

            try
            {
                Directory.CreateDirectory(outputDir);
                var probe = Path.Combine(outputDir, $".m3ucrawler-readiness-probe-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }

        /// <summary>
        /// PHASE 9C.1 — Log não sensível do estado de configuração
        /// (nome do estado e, quando aplicável, a marca de adopção legacy).
        /// </summary>
        static void LogLifecycleState(ConfigurationLifecycleSnapshot snapshot)
        {
            if (snapshot.AdoptedFromLegacy)
            {
                Console.WriteLine(
                    $"🧭 Configuration lifecycle: {snapshot.State.ToWireName()} (legacy adoption: {snapshot.LastReason})");
            }
            else
            {
                Console.WriteLine(
                    $"🧭 Configuration lifecycle: {snapshot.State.ToWireName()}");
            }
        }
    }
}
