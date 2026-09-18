using m3uCrawler.Build;
using m3uCrawler.Services;
using m3uCrawler.Services.Auth;
using m3uCrawler.Services.Automation;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using m3uCrawler.Services.LiveRun;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using m3uCrawler.Services.SourceSelection;
using m3uCrawler.Services.Validation;
using m3uCrawler.Models;
using System.Text;
using System.Net;

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
            Task? webTask = null;
            CatalogResolver? webCatalogResolver = null;
            ScheduledAutomationHost? automationHost = null;
            // PHASE 9C.4 — Host que detém o RunCoordinator único. Construído
            // quando o catálogo está disponível (i.e. --web foi passado
            // e o InitializeCatalogAsync foi bem-sucedido). O executor da
            // pipeline Telegram é registado dentro do bloco --telegram.
            LiveRunHost? liveRunHost = null;
            if (webEnabled)
            {
                var dashboardOutputDir = GetOptionValue(args, "--output-dir") ?? "output";
                var dashboardHistoryService = new ImportHistoryService(dashboardOutputDir);

                try
                {
                    webCatalogResolver = await InitializeCatalogAsync(
                        ResolveCatalogDbPath(args), CancellationToken.None);
                    WebDashboardService.SetCatalogResolver(webCatalogResolver);

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
                        liveRunHost: liveRunHost);
                    WebDashboardService.SetScheduledActions(automationHost.RegisteredActions);
                    automationHost.Start();
                    Console.WriteLine(
                        $"🕒 ScheduledJobRunner activo ({automationHost.RegisteredActions.Count} actions registadas).");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Catálogo não disponível para o dashboard: {ex.Message}");
                }

                webTask = WebDashboardService.RunDashboardAsync(dashboardOutputDir, webPort, dashboardHistoryService, webToken, CancellationToken.None);
                _ = webTask.ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                    {
                        Console.WriteLine($"❌ Dashboard task falhou: {t.Exception.GetBaseException().Message}");
                    }
                }, TaskContinuationOptions.OnlyOnFaulted);
            }

            // PHASE 12 — Shutdown limpo: Ctrl+C pára o runner antes da app sair.
            if (automationHost is not null)
            {
                ConsoleCancelEventHandler cancelHandler = (_, e) =>
                {
                    e.Cancel = true;
                    try
                    {
                        automationHost.StopAsync().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠️ Erro ao parar ScheduledJobRunner: {ex.Message}");
                    }
                };
                Console.CancelKeyPress += cancelHandler;
            }

            if (args.Contains("--telegram"))
            {
                var scraper = new TelegramScraperService();
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
                await scraper.LoginAsync();

                var catalogDbPath = ResolveCatalogDbPath(args);
                CatalogResolver? catalogForAffinity = null;
                try
                {
                    catalogForAffinity = await InitializeCatalogAsync(catalogDbPath, CancellationToken.None);
                    await InjectAffinityMembersToValidatorAsync(catalogForAffinity);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Catálogo não disponível para injeção de afinidades: {ex.Message}");
                }

                // Get search term from arguments or prompt user
                string term = "";
                
                // Find the index of --telegram and get the next argument as search term
                int telegramIndex = Array.IndexOf(args, "--telegram");
                if (telegramIndex >= 0 && telegramIndex < args.Length - 1)
                {
                    // Get arguments after --telegram until the next option
                    var remainingArgs = new List<string>();
                    for (int i = telegramIndex + 1; i < args.Length; i++)
                    {
                        if (args[i].StartsWith("--")) break;
                        remainingArgs.Add(args[i]);
                    }
                    
                    if (remainingArgs.Any())
                    {
                        term = string.Join(" ", remainingArgs);
                    }
                }
                
                // If no term provided via arguments, prompt the user
                if (string.IsNullOrWhiteSpace(term))
                {
                    Console.Write("Termo a procurar no Telegram: ");
                    term = Console.ReadLine() ?? "";
                }

                if (string.IsNullOrWhiteSpace(term))
                {
                    Console.WriteLine("Termo de pesquisa não pode estar vazio!");
                    return;
                }

                int telegramMaxStreams = 500;
                var telegramMaxArg = GetOptionValue(args, "--max-streams");
                if (int.TryParse(telegramMaxArg, out int telegramParsedMax) && telegramParsedMax > 0)
                {
                    telegramMaxStreams = Math.Min(telegramParsedMax, 5000);
                }

                // Janela de pesquisa Telegram: 24h por defeito.
                // 24h cobre ciclos diários sem aumentar desnecessariamente
                // o volume (mensagens analisadas, downloads HTTP, validação).
                // Confirmado em produção: resultados relevantes continuam
                // a aparecer dentro de 24h (ex: 2026-09-11 — m3u@…-HITS_DI_…html).
                int telegramHistoryHours = 24;
                var historyArg = GetOptionValue(args, "--history-hours");
                if (int.TryParse(historyArg, out int parsedHistoryHours) && parsedHistoryHours > 0)
                {
                    telegramHistoryHours = Math.Min(parsedHistoryHours, 24 * 30);
                }
                Console.WriteLine($"🕒 Janela de pesquisa Telegram: últimas {telegramHistoryHours}h");

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
                var countryChannelValidator = new CountryChannelValidator(Path.Combine(Directory.GetCurrentDirectory(), "runtime-data", "countries"));
                Console.WriteLine($"📂 Pasta de saída das playlists: {Path.GetFullPath(outputDir)}");

                // PHASE-Bridge — Inicializar o ingestor de catálogo para o pipeline
                // Telegram. Se o catálogo falhar a inicializar (e.g. SQLite
                // bloqueada), o pipeline continua sem catalogação — a playlist
                // M3U continua a ser produzida.
                PipelineIngestionService? pipelineIngestor = null;
                CatalogResolver? catalogForIngestion = null;
                try
                {
                    catalogForIngestion = await InitializeCatalogAsync(catalogDbPath, CancellationToken.None);
                    // R1 — O ingestor requer um CountryChannelValidator para
                    // garantir que streams REJECTED pelo country policy não
                    // são persistidos. O validator já está construído acima
                    // (linha 186), partilhando configuração com o scraper.
                    pipelineIngestor = new PipelineIngestionService(
                        catalogForIngestion, countryChannelValidator);
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
                List<M3uStream>? liveRunStreams = null;
                RunReport? liveRunReport = null;
                Exception? liveRunException = null;
                var countriesDirectory = Path.Combine(
                    Directory.GetCurrentDirectory(), "runtime-data", "countries");

                if (catalogForIngestion is not null)
                {
                    Func<LiveRunRequest, IRunPipeline> liveRunPipelineFactory = _ =>
                        new TelegramRunPipeline(async (request, progress, ct) =>
                        {
                            liveRunException = null;
                            try
                            {
                                if (request.Mode == LiveRunMode.TelegramMaintain)
                                {
                                    await RunTelegramMaintenanceCycle(
                                        scraper,
                                        telegramPlaylistManager,
                                        importHistoryService,
                                        term,
                                        outputDir,
                                        telegramMaxStreams,
                                        domainFilter,
                                        telegramHistoryHours,
                                        args,
                                        pipelineIngestor,
                                        catalogForIngestion,
                                        countryCode,
                                        countriesDirectory,
                                        progress,
                                        ct);
                                }
                                else
                                {
                                    var (streams, report) = await scraper.SearchAndTestM3UInTelegramAsync(
                                        term,
                                        limit: 200,
                                        maxConcurrency: 5,
                                        maxUrlsToTest: telegramMaxStreams,
                                        historyHours: telegramHistoryHours,
                                        countryCode: countryCode,
                                        countriesDir: countriesDirectory,
                                        pipelineIngestor: pipelineIngestor,
                                        pipelineSourceKey: $"telegram-{Slugify(term)}",
                                        liveRunProgress: progress,
                                        cancellationToken: ct);
                                    liveRunStreams = streams;
                                    liveRunReport = report;
                                }
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
                        Console.WriteLine("🔁 RunCoordinator partilhado com o dashboard (LiveRunHost).");
                    }
                    else
                    {
                        liveRunCoordinator = new RunCoordinator(
                            catalogForIngestion.GetFactory(), liveRunPipelineFactory);
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

                do
                {
                    if (automaticDiscovery && !await configurationGate.IsReadyAsync())
                    {
                        Console.WriteLine(
                            $"⛔ automatic discovery blocked: not configured (state={configurationGate.State.ToWireName()})");
                        if (loopHours <= 0)
                        {
                            return;
                        }
                        Console.WriteLine();
                        Console.WriteLine($"⏳ Próxima execução em {loopHours} hora(s)...");
                        await Task.Delay(TimeSpan.FromHours(loopHours));
                        continue;
                    }

                    if (maintenanceMode)
                    {
                        if (liveRunCoordinator is not null)
                        {
                            await liveRunCoordinator.StartAsync(new LiveRunRequest
                            {
                                Mode = LiveRunMode.TelegramMaintain,
                                Source = LiveRunSource.Cli,
                                Keyword = term,
                                HistoryHours = telegramHistoryHours,
                                MaxStreams = telegramMaxStreams,
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
                                term,
                                outputDir,
                                telegramMaxStreams,
                                domainFilter,
                                telegramHistoryHours,
                                args,
                                pipelineIngestor,
                                catalogForIngestion,
                                countryCode,
                                countriesDirectory);
                        }
                    }
                    else
                    {
                        List<M3uStream> workingStreams;
                        RunReport runReport;
                        if (liveRunCoordinator is not null)
                        {
                            liveRunStreams = null;
                            liveRunReport = null;
                            await liveRunCoordinator.StartAsync(new LiveRunRequest
                            {
                                Mode = LiveRunMode.Telegram,
                                Source = LiveRunSource.Cli,
                                Keyword = term,
                                HistoryHours = telegramHistoryHours,
                                MaxStreams = telegramMaxStreams,
                            }, CancellationToken.None);

                            if (liveRunException is not null)
                            {
                                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                                    .Capture(liveRunException).Throw();
                            }

                            workingStreams = liveRunStreams ?? new List<M3uStream>();
                            runReport = liveRunReport ?? new RunReport();
                        }
                        else
                        {
                            (workingStreams, runReport) = await scraper.SearchAndTestM3UInTelegramAsync(
                                term,
                                limit: 200,
                                maxConcurrency: 5,
                                maxUrlsToTest: telegramMaxStreams,
                                historyHours: telegramHistoryHours,
                                countryCode: countryCode,
                                countriesDir: countriesDirectory,
                                pipelineIngestor: pipelineIngestor,
                                pipelineSourceKey: $"telegram-{Slugify(term)}");
                        }

                        if (!string.IsNullOrWhiteSpace(domainFilter))
                        {
                            int beforeFilter = workingStreams.Count;
                            workingStreams = workingStreams
                                .Where(s => UrlMatchesDomain(s.Url, domainFilter))
                                .ToList();
                            Console.WriteLine($"🌐 Após filtro de domínio: {workingStreams.Count}/{beforeFilter} streams");
                        }

                        Console.WriteLine($"\n✅ Streams funcionais encontradas no Telegram: {workingStreams.Count}");

                        foreach (var stream in workingStreams)
                        {
                            Console.WriteLine($"  • {stream.Title} ({stream.ResponseTime}ms) :: {CredentialSanitizer.SanitizeUrl(stream.Url)}");
                        }

                        var countryMatches = countryChannelValidator.ValidateStreams(workingStreams, countryCode);
                        Console.WriteLine($"📡 Validação por canais {countryCode.ToUpperInvariant()}: {countryMatches.Count} stream(s) correspondentes.");
                        foreach (var match in countryMatches.Take(10))
                        {
                            Console.WriteLine($"  • {countryCode.ToUpperInvariant()} match: {match.Stream.Title} -> {string.Join(", ", match.MatchedAliases)}");
                        }

                        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        var playlistPath = Path.Combine(outputDir, $"telegram_playlist_{timestamp}.m3u");
                        var reportPath = Path.Combine(outputDir, $"telegram_report_{timestamp}.json");

                        // PHASE 13 (Wave 13-3) — selecção de fontes antes da publicação.
                        // PHASE 13 (Wave 13-4) — política global resolvida do catálogo.
                        var singleCyclePolicy = catalogForIngestion is null
                            ? SourceSelectionDefaults.DefaultPolicy
                            : await new SourceSelectionPolicyResolver(catalogForIngestion)
                                .ResolveGlobalAsync(CancellationToken.None);
                        var singleCycleSelection = await new SourceSelectionStage(catalogForIngestion)
                            .ApplyAsync(workingStreams, singleCyclePolicy, CancellationToken.None);
                        runReport.SourceSelection = singleCycleSelection.ToReport();
                        if (singleCycleSelection.Applied)
                        {
                            Console.WriteLine(
                                $"🧩 Selecção Phase 13: selected={singleCycleSelection.Selected.Count} " +
                                $"rejected={singleCycleSelection.Rejected.Count} unmatched={singleCycleSelection.Unmatched.Count} " +
                                $"canais={singleCycleSelection.MatchedChannelCount} ambíguos={singleCycleSelection.AmbiguousCount}");
                        }
                        workingStreams = singleCycleSelection.Published.ToList();

                        await telegramPlaylistManager.SaveToM3uPlaylist(workingStreams, playlistPath);
                        await telegramPlaylistManager.SaveToJsonReport(workingStreams, reportPath);
                        await SaveRunReportAsync(outputDir, runReport);

                        Console.WriteLine($"\n✨ Arquivos gerados:");
                        Console.WriteLine($"   • Playlist: {playlistPath}");
                        Console.WriteLine($"   • Relatório: {reportPath}");
                        Console.WriteLine($"   • Relatório de execução: {Path.Combine(outputDir, "telegram_run_report.json")}");

                        if (workingStreams.Count == 0)
                        {
                            Console.WriteLine("❌ Nenhum stream funcional encontrado no Telegram.");
                        }

                        await TrySyncToDispatcharrAsync(playlistPath, outputDir, args);

                        await importHistoryService.RecordImportAsync(new ImportHistoryEntry
                        {
                            Timestamp = DateTime.UtcNow,
                            Mode = "TelegramSearch",
                            SearchTerm = term,
                            HistoryHours = telegramHistoryHours,
                            MaxStreams = telegramMaxStreams,
                            NewFunctionalCount = workingStreams.Count,
                            MessagesAnalyzed = runReport.MessagesAnalyzed,
                            CandidatesFound = runReport.CandidatesFound,
                            PlaylistsDownloaded = runReport.PlaylistsDownloaded,
                            CountryMatches = runReport.CountryMatches,
                            PlaylistsRejected = runReport.PlaylistsRejected,
                            StreamsExtracted = runReport.StreamsExtracted,
                            StreamsTested = runReport.StreamsTested,
                            StreamsWorking = runReport.StreamsWorking,
                            StreamsFailed = runReport.StreamsFailed
                        });
                    }

                    if (loopHours > 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"⏳ Próxima execução em {loopHours} hora(s)...");
                        await Task.Delay(TimeSpan.FromHours(loopHours));
                    }
                }
                while (loopHours > 0);

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

                await TrySyncToDispatcharrAsync(playlistPath, outputDir, args);
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
                Console.WriteLine("🌐 Modo dashboard standalone activo. Aguardando pedidos em http://+:" + webPort + "/");
                try { await webTask; }
                catch (Exception ex) { Console.WriteLine($"❌ Dashboard task falhou: {ex.GetBaseException().Message}"); }
                automationHost?.Dispose();
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
                var skipWithValue = new HashSet<string> { "--max-streams", "--domain", "--web-port", "--web-token", "--bot-token", "--loop-hours", "--history-hours", "--max-results", "--user", "--pass" };
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
            Console.WriteLine("  --history-hours N Janela (em horas) para pesquisar mensagens no Telegram (padrão: 24)");
            Console.WriteLine("  --loop-hours N    Repete execução a cada N horas (ex: 24)");
            Console.WriteLine("  --fast            Modo alta performance (20 conexões paralelas)");
            Console.WriteLine("  --high-performance Mesmo que --fast");
            Console.WriteLine("  --dispatcharr-sync  Sincroniza uma playlist M3U já existente com Dispatcharr (sem Telegram)");
            Console.WriteLine("  --playlist PATH    Caminho da playlist a sincronizar (default: <output-dir>/playlist.m3u)");
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
            string term,
            string outputDir,
            int telegramMaxStreams,
            string? domainFilter,
            int telegramHistoryHours,
            string[] args,
            PipelineIngestionService? pipelineIngestor,
            CatalogResolver? catalog,
            string countryCode = "pt",
            string? countriesDir = null,
            ILiveRunProgress? liveRunProgress = null,
            CancellationToken cancellationToken = default)
        {
            var tempPath = Path.Combine(outputDir, "playlist_temp.m3u");
            var mainPath = Path.Combine(outputDir, "playlist.m3u");
            var reportPath = Path.Combine(outputDir, "telegram_maintain_report.json");

            Console.WriteLine();
            Console.WriteLine("🧹 Início do ciclo de manutenção Telegram...");

            // Limpa sempre a playlist temporária no início do ciclo.
            await File.WriteAllTextAsync(tempPath, "#EXTM3U" + Environment.NewLine, Encoding.UTF8);

            var (freshStreams, runReport) = await scraper.SearchAndTestM3UInTelegramAsync(
                term,
                limit: 200,
                maxConcurrency: 5,
                maxUrlsToTest: telegramMaxStreams,
                historyHours: telegramHistoryHours,
                countryCode: countryCode,
                countriesDir: countriesDir,
                report: new RunReport(),
                pipelineIngestor: pipelineIngestor,
                pipelineSourceKey: $"telegram-{Slugify(term)}",
                liveRunProgress: liveRunProgress,
                cancellationToken: cancellationToken);

            liveRunProgress?.ReportCounts(runReport);

            if (!string.IsNullOrWhiteSpace(domainFilter))
            {
                int beforeFilter = freshStreams.Count;
                freshStreams = freshStreams
                    .Where(s => UrlMatchesDomain(s.Url, domainFilter))
                    .ToList();
                Console.WriteLine($"🌐 Após filtro de domínio: {freshStreams.Count}/{beforeFilter} streams");
            }

            await playlistManager.SaveToM3uPlaylist(freshStreams, tempPath);
            Console.WriteLine($"🔔 Novos canais funcionais em playlist_temp.m3u: {freshStreams.Count}");
            Console.WriteLine($"   • Escrito: {tempPath}");

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

            // PHASE 13 (Wave 13-3) — selecção de fontes sobre a playlist final.
            // PHASE 13 (Wave 13-4) — política global resolvida do catálogo.
            var maintenancePolicy = catalog is null
                ? SourceSelectionDefaults.DefaultPolicy
                : await new SourceSelectionPolicyResolver(catalog)
                    .ResolveGlobalAsync(cancellationToken);
            var maintenanceSelection = await new SourceSelectionStage(catalog)
                .ApplyAsync(finalStreams, maintenancePolicy, cancellationToken);
            runReport.SourceSelection = maintenanceSelection.ToReport();
            if (maintenanceSelection.Applied)
            {
                Console.WriteLine(
                    $"🧩 Selecção Phase 13: selected={maintenanceSelection.Selected.Count} " +
                    $"rejected={maintenanceSelection.Rejected.Count} unmatched={maintenanceSelection.Unmatched.Count} " +
                    $"canais={maintenanceSelection.MatchedChannelCount} ambíguos={maintenanceSelection.AmbiguousCount}");
            }
            finalStreams = maintenanceSelection.Published.ToList();

            liveRunProgress?.ReportCounts(counts =>
            {
                counts.TargetPlaylistEntries = finalStreams.Count;
                counts.ExistingPlaylistRetested = existingMain.Count;
            });

            await playlistManager.SaveToM3uPlaylist(finalStreams, mainPath);
            await playlistManager.SaveToJsonReport(finalStreams, reportPath);
            await SaveRunReportAsync(outputDir, runReport);

            await TrySyncToDispatcharrAsync(mainPath, outputDir, args, liveRunProgress, cancellationToken);

            var historyEntry = new ImportHistoryEntry
            {
                Timestamp = DateTime.UtcNow,
                Mode = "TelegramMaintenance",
                SearchTerm = term,
                HistoryHours = telegramHistoryHours,
                MaxStreams = telegramMaxStreams,
                NewFunctionalCount = freshStreams.Count(s => s.IsWorking),
                ExistingRetestedCount = existingMain.Count,
                ExistingStillWorkingCount = stillWorkingMain.Count,
                FinalPlaylistCount = finalStreams.Count,
                MessagesAnalyzed = runReport.MessagesAnalyzed,
                CandidatesFound = runReport.CandidatesFound,
                PlaylistsDownloaded = runReport.PlaylistsDownloaded,
                CountryMatches = runReport.CountryMatches,
                PlaylistsRejected = runReport.PlaylistsRejected,
                StreamsExtracted = runReport.StreamsExtracted,
                StreamsTested = runReport.StreamsTested,
                StreamsWorking = runReport.StreamsWorking,
                StreamsFailed = runReport.StreamsFailed
            };
            await importHistory.RecordImportAsync(historyEntry);

            Console.WriteLine("✅ Ciclo concluído.");
            Console.WriteLine($"   • Mantidas de playlist.m3u: {stillWorkingMain.Count}");
            Console.WriteLine($"   • Novas funcionais de playlist_temp.m3u: {freshStreams.Count(s => s.IsWorking)}");
            Console.WriteLine($"   • Total final em playlist.m3u: {finalStreams.Count}");
            Console.WriteLine($"   • playlist_temp.m3u: {tempPath}");
            Console.WriteLine($"   • playlist.m3u: {mainPath}");
            Console.WriteLine($"   • Relatório de execução: {Path.Combine(outputDir, "telegram_run_report.json")}");
        }

        static async Task InjectAffinityMembersToValidatorAsync(CatalogResolver catalog)
        {
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
                    CountryChannelValidator.SetAffinityMembersStatic(group.Key, members);
                    Console.WriteLine($"  [{group.Key}] {members.Count} membro(s) de afinidade injetados");
                }
            }
        }

        static async Task SaveRunReportAsync(string outputDir, RunReport report)
        {
            try
            {
                var path = Path.Combine(outputDir, "telegram_run_report.json");
                var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                });
                await File.WriteAllTextAsync(path, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Não foi possível guardar o relatório de execução: {ex.Message}");
            }
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
            var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
            await using var context = await bootstrapper.InitializeAsync(ct);
            await context.DisposeAsync();
            var factory = new RuntimeChannelCatalogDbContextFactory(dbPath);
            return new CatalogResolver(factory, dbPath);
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

        static async Task TrySyncToDispatcharrAsync(
            string playlistPath, string outputDir, string[] args,
            ILiveRunProgress? liveRunProgress = null,
            CancellationToken cancellationToken = default)
        {
            var cfg = DispatcharrConfigLoader.Load();
            if (!cfg.Enabled)
            {
                // PHASE 9C.4 — SYNCING_DISPATCHARR não ocorre quando a
                // integração está desligada; regista-se apenas o skip.
                if (liveRunProgress is not null)
                {
                    liveRunProgress.ReportCounts(counts => counts.DispatcharrSyncSkipped++);
                    liveRunProgress.ReportActivity(
                        LiveRunActivityCategory.Dispatcharr,
                        LiveRunActivityLevel.Info,
                        "dispatcharr sync skipped (disabled)");
                }
                return;
            }

            if (liveRunProgress is not null)
            {
                await liveRunProgress.EnterPhaseAsync(
                    LiveRunPhase.SyncingDispatcharr, "syncing dispatcharr", cancellationToken)
                    .ConfigureAwait(false);
                liveRunProgress.ReportCounts(counts => counts.DispatcharrSyncAttempted++);
            }

            CatalogResolver catalog;
            try
            {
                catalog = await InitializeCatalogAsync(
                    ResolveCatalogDbPath(args), CancellationToken.None);
            }
            catch (Exception ex)
            {
                if (liveRunProgress is not null)
                {
                    liveRunProgress.ReportCounts(counts => counts.DispatcharrSyncFailed++);
                    liveRunProgress.ReportActivity(
                        LiveRunActivityCategory.Dispatcharr,
                        LiveRunActivityLevel.Error,
                        "dispatcharr sync failed: catalog unavailable");
                }
                Console.WriteLine(
                    $"❌ Catálogo falhou a inicializar em '{ResolveCatalogDbPath(args)}'. " +
                    $"Sincronização Dispatcharr abortada antes de qualquer escrita HTTP. " +
                    $"Erro: {ex.Message}");
                throw;
            }

            try
            {
                var aliases = AliasResolver.FromFile(cfg.AliasFile);
                var ordering = new StreamOrderingPolicy(cfg.ProviderPriority);
                var matcher = new ChannelMatcher(aliases, null, catalog);
                var sync = new m3uCrawler.Services.Sync.DispatcharrSyncService(
                    cfg, outputDir,
                    aliases: aliases,
                    ordering: ordering,
                    matcher: matcher,
                    catalog: catalog);
                await sync.RunAsync(playlistPath);

                if (liveRunProgress is not null)
                {
                    liveRunProgress.ReportCounts(counts => counts.DispatcharrSyncCompleted++);
                    liveRunProgress.ReportActivity(
                        LiveRunActivityCategory.Dispatcharr,
                        LiveRunActivityLevel.Info,
                        "dispatcharr sync completed");
                }
            }
            catch (Exception ex)
            {
                if (liveRunProgress is not null)
                {
                    liveRunProgress.ReportCounts(counts => counts.DispatcharrSyncFailed++);
                    liveRunProgress.ReportActivity(
                        LiveRunActivityCategory.Dispatcharr,
                        LiveRunActivityLevel.Error,
                        "dispatcharr sync failed");
                }
                Console.WriteLine($"⚠️ Falha na sincronização Dispatcharr: {ex.Message}");
            }
        }
    }
}
