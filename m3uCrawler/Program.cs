using m3uCrawler.Services;
using m3uCrawler.Models;
using System.Text;
using System.Net;

namespace m3uCrawler
{    
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== m3uCrawler - Pesquisador de Streams M3U8 ===");
            Console.WriteLine("Versão 2.1 - Novembro 2025");
            Console.WriteLine();

            var domainFilter = GetOptionValue(args, "--domain");
            var countryCode = GetOptionValue(args, "--country") ?? "pt";
            if (!string.IsNullOrWhiteSpace(domainFilter))
            {
                Console.WriteLine($"🌐 Filtro de domínio ativo: {domainFilter}");
            }
            Console.WriteLine($"🇵🇹 País em validação: {countryCode}");

            if (args.Contains("--telegram"))
            {
                var scraper = new TelegramScraperService();
                await scraper.LoginAsync();

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

                int telegramHistoryHours = 48;
                var historyArg = GetOptionValue(args, "--history-hours");
                if (int.TryParse(historyArg, out int parsedHistoryHours) && parsedHistoryHours > 0)
                {
                    telegramHistoryHours = Math.Min(parsedHistoryHours, 24 * 30);
                }
                Console.WriteLine($"🕒 Janela de pesquisa Telegram: últimas {telegramHistoryHours}h");

                bool maintenanceMode = args.Contains("--telegram-maintain");
                bool webEnabled = args.Contains("--web");
                int webPort = 5000;
                var webPortArg = GetOptionValue(args, "--web-port");
                if (int.TryParse(webPortArg, out var parsedWebPort) && parsedWebPort > 0)
                {
                    webPort = parsedWebPort;
                }

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

                Task? webTask = null;
                if (webEnabled)
                {
                    string? webToken = GetOptionValue(args, "--web-token");
                    webTask = WebDashboardService.RunDashboardAsync(outputDir, webPort, importHistoryService, webToken, CancellationToken.None);
                    _ = webTask.ContinueWith(t =>
                    {
                        if (t.IsFaulted && t.Exception != null)
                        {
                            Console.WriteLine($"❌ Dashboard task falhou: {t.Exception.GetBaseException().Message}");
                        }
                    }, TaskContinuationOptions.OnlyOnFaulted);
                }

                do
                {
                    if (maintenanceMode)
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
                            countryCode,
                            Path.Combine(Directory.GetCurrentDirectory(), "runtime-data", "countries"));
                    }
                    else
                    {
                        var (workingStreams, runReport) = await scraper.SearchAndTestM3UInTelegramAsync(
                            term,
                            limit: 200,
                            maxConcurrency: 5,
                            maxUrlsToTest: telegramMaxStreams,
                            historyHours: telegramHistoryHours,
                            countryCode: countryCode,
                            countriesDir: Path.Combine(Directory.GetCurrentDirectory(), "runtime-data", "countries"));

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

                var botcrawler = new M3uCrawlerService();
                var bot = new TelegramBotService("8959431945:AAG961d6CDUEXKnFMBt_QxaEvElBuy6fIeg", botcrawler);

                bot.Start();

                Console.WriteLine("Bot ativo. Prima CTRL+C para sair.");
                await Task.Delay(-1); // Mantém o bot a correr
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
            var tester = new M3uTesterService();
            var playlistManager = new PlaylistManagerService();

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
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--max-streams" || args[i] == "--domain")
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
            Console.WriteLine("  --scan-domain D   Faz scan direto ao domínio para procurar playlists (sem Telegram)");
            Console.WriteLine("  --telegram-maintain Mantém output/playlist.m3u com base no Telegram e remove links mortos");
            Console.WriteLine("  --history-hours N Janela (em horas) para pesquisar mensagens no Telegram (padrão: 48)");
            Console.WriteLine("  --loop-hours N    Repete execução a cada N horas (ex: 24)");
            Console.WriteLine("  --fast            Modo alta performance (20 conexões paralelas)");
            Console.WriteLine("  --high-performance Mesmo que --fast");
            Console.WriteLine("  --help, -h        Mostra esta ajuda");
            Console.WriteLine();
            Console.WriteLine("EXEMPLOS:");
            Console.WriteLine("  m3uCrawler \"iptv portugal\"");
            Console.WriteLine("  m3uCrawler \"tv streams\" --max-streams 500");
            Console.WriteLine("  m3uCrawler \"canais tv\" --fast --max-streams 1000");
            Console.WriteLine("  m3uCrawler \"iptv\" --domain exemplo.com");
            Console.WriteLine("  m3uCrawler --scan-domain exemplo.com --max-streams 300");
            Console.WriteLine("  m3uCrawler --telegram portugal --telegram-maintain --loop-hours 24 --history-hours 72");
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

        static async Task RunTelegramMaintenanceCycle(
            TelegramScraperService scraper,
            PlaylistManagerService playlistManager,
            ImportHistoryService importHistory,
            string term,
            string outputDir,
            int telegramMaxStreams,
            string? domainFilter,
            int telegramHistoryHours,
            string countryCode = "pt",
            string? countriesDir = null)
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
                countriesDir: countriesDir);

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
                var tester = new M3uTesterService();
                try
                {
                    Console.WriteLine($"🔁 Re-testando {existingMain.Count} stream(s) existentes de playlist.m3u...");
                    var retestTasks = existingMain.Select(async stream =>
                    {
                        var tested = await tester.TestM3u8Stream(stream.Url, stream.Title, stream.Group);
                        tested.OriginalExtInf = stream.OriginalExtInf;
                        tested.Logo = stream.Logo;
                        return tested;
                    });

                    var retested = await Task.WhenAll(retestTasks);
                    stillWorkingMain = retested.Where(s => s.IsWorking).ToList();
                    Console.WriteLine($"✅ Streams existentes ainda funcionais: {stillWorkingMain.Count}/{existingMain.Count}");
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
            var finalStreams = TelegramScraperService.MergeStreams(stillWorkingMain, freshStreams);

            await playlistManager.SaveToM3uPlaylist(finalStreams, mainPath);
            await playlistManager.SaveToJsonReport(finalStreams, reportPath);
            await SaveRunReportAsync(outputDir, runReport);

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
    }
}