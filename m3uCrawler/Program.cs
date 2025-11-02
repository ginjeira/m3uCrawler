using m3uCrawler.Services;
using m3uCrawler.Models;

namespace m3uCrawler
{    
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== m3uCrawler - Pesquisador de Streams M3U8 ===");
            Console.WriteLine("Versão 2.1 - Novembro 2025");
            Console.WriteLine();

            // Check for help
            if (args.Contains("--help") || args.Contains("-h"))
            {
                ShowHelp();
                return;
            }

            var crawler = new M3uCrawlerService();
            var tester = new M3uTesterService();
            var playlistManager = new PlaylistManagerService();

            Console.WriteLine("Iniciando m3uCrawler...");

            try
            {
                // Configurações
                var outputDir = "output";
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var playlistPath = Path.Combine(outputDir, $"playlist_{timestamp}.m3u");
                var reportPath = Path.Combine(outputDir, $"report_{timestamp}.json");

                playlistManager.CreateOutputDirectory(outputDir);                // Obter termo de pesquisa do usuário ou argumentos
                string searchTerm;
                
                // Filter out known options from args to get search term
                var searchArgs = args.Where(arg => 
                    !arg.StartsWith("--") && 
                    args.ToList().IndexOf(arg) != (args.ToList().IndexOf("--max-streams") + 1))
                    .ToList();
                
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
            Console.WriteLine("  --fast            Modo alta performance (20 conexões paralelas)");
            Console.WriteLine("  --high-performance Mesmo que --fast");
            Console.WriteLine("  --help, -h        Mostra esta ajuda");
            Console.WriteLine();
            Console.WriteLine("EXEMPLOS:");
            Console.WriteLine("  m3uCrawler \"iptv portugal\"");
            Console.WriteLine("  m3uCrawler \"tv streams\" --max-streams 500");
            Console.WriteLine("  m3uCrawler \"canais tv\" --fast --max-streams 1000");
            Console.WriteLine();
            Console.WriteLine("CONFIGURAÇÃO:");
            Console.WriteLine("  • Edite config.json para configurações avançadas");
            Console.WriteLine("  • Limite padrão: 500 streams");
            Console.WriteLine("  • Conexões padrão: 10 paralelas (20 no modo --fast)");
            Console.WriteLine();
        }
    }
}
