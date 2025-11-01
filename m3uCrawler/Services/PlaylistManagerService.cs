using m3uCrawler.Models;
using System.Text;
using System.Text.Json;

namespace m3uCrawler.Services
{
    public class PlaylistManagerService
    {
        public async Task SaveToM3uPlaylist(List<M3uStream> streams, string filePath)
        {
            var workingStreams = streams.Where(s => s.IsWorking).ToList();
            
            var m3uContent = new StringBuilder();
            m3uContent.AppendLine("#EXTM3U");
            m3uContent.AppendLine($"#PLAYLIST:m3uCrawler - Generated on {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            m3uContent.AppendLine();

            foreach (var stream in workingStreams)
            {
                m3uContent.AppendLine(stream.ToString());
                m3uContent.AppendLine();
            }

            await File.WriteAllTextAsync(filePath, m3uContent.ToString(), Encoding.UTF8);
            Console.WriteLine($"Playlist M3U guardada em: {filePath}");
            Console.WriteLine($"Total de streams funcionais: {workingStreams.Count}");
        }

        public async Task SaveToJsonReport(List<M3uStream> streams, string filePath)
        {
            var report = new
            {
                GeneratedAt = DateTime.Now,
                TotalStreams = streams.Count,
                WorkingStreams = streams.Count(s => s.IsWorking),
                NonWorkingStreams = streams.Count(s => !s.IsWorking),
                AverageResponseTime = streams.Where(s => s.IsWorking).Average(s => s.ResponseTime),
                Streams = streams
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var json = JsonSerializer.Serialize(report, options);
            await File.WriteAllTextAsync(filePath, json, Encoding.UTF8);
            
            Console.WriteLine($"Relatório JSON guardado em: {filePath}");
        }

        public async Task<List<M3uStream>> LoadFromJsonReport(string filePath)
        {
            if (!File.Exists(filePath))
                return new List<M3uStream>();

            var json = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
            var report = JsonSerializer.Deserialize<dynamic>(json);
            
            // Implementar deserialização se necessário
            return new List<M3uStream>();
        }

        public void CreateOutputDirectory(string outputPath)
        {
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                Console.WriteLine($"Diretório criado: {directory}");
            }
        }
    }
}
