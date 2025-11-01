using m3uCrawler.Services;
using m3uCrawler.Models;

namespace m3uCrawler
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== m3uCrawler - Pesquisador de Streams M3U8 ===");
            Console.WriteLine("Versão 1.0 - Novembro 2025");
            Console.WriteLine();

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

                playlistManager.CreateOutputDirectory(outputDir);

                // Obter termo de pesquisa do usuário ou argumentos
                string searchTerm;
                if (args.Length > 0)
                {
                    searchTerm = string.Join(" ", args);
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
                
                // Pesquisar URLs M3U8
                var foundUrls = await crawler.SearchM3u8Files(searchTerm, 100);
                Console.WriteLine($"📋 Encontradas {foundUrls.Count} URLs M3U8");

                if (foundUrls.Count == 0)
                {
                    Console.WriteLine("Nenhuma URL M3U8 encontrada. Tente um termo diferente.");
                    return;
                }

                // Testar streams
                Console.WriteLine("\n🧪 Testando streams...");
                var testedStreams = await tester.TestMultipleStreams(foundUrls, 10);

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
                tester.Dispose();
                Console.WriteLine("m3uCrawler finalizado.");
            }

            Console.WriteLine("\nPressione qualquer tecla para sair...");
            Console.ReadKey();
        }
    }
}
