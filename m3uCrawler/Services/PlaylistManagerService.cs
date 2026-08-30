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
            var workingStreams = streams.Where(s => s.IsWorking).ToList();

            // JSON de relatório/diagnóstico: as URLs dos streams são sanitizadas para nunca
            // persistir credenciais Xtream (user/password/token). A playlist M3U funcional
            // (SaveToM3uPlaylist) preserva as URLs reais, pois são necessárias para reprodução.
            var sanitizedStreams = streams.Select(s => new
            {
                Url = CredentialSanitizer.SanitizeUrl(s.Url),
                Title = s.Title,
                Group = s.Group,
                Logo = s.Logo,
                IsWorking = s.IsWorking,
                ResponseTime = s.ResponseTime,
                LastTested = s.LastTested,
                OriginalExtInf = s.OriginalExtInf
            }).ToList();

            var report = new
            {
                GeneratedAt = DateTime.Now,
                TotalStreams = streams.Count,
                WorkingStreams = workingStreams.Count,
                NonWorkingStreams = streams.Count(s => !s.IsWorking),
                AverageResponseTime = workingStreams.Any() ? workingStreams.Average(s => s.ResponseTime) : 0,
                Streams = sanitizedStreams
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

        public async Task<List<M3uStream>> LoadFromM3uPlaylist(string filePath)
        {
            var streams = new List<M3uStream>();
            if (!File.Exists(filePath)) return streams;

            var lines = await File.ReadAllLinesAsync(filePath, Encoding.UTF8);
            string pendingExtInf = string.Empty;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
                {
                    pendingExtInf = line;
                    continue;
                }

                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    var stream = new M3uStream
                    {
                        Url = line,
                        IsWorking = true,
                        LastTested = DateTime.Now,
                        OriginalExtInf = pendingExtInf
                    };

                    if (!string.IsNullOrWhiteSpace(pendingExtInf))
                    {
                        int commaIndex = pendingExtInf.LastIndexOf(',');
                        if (commaIndex >= 0 && commaIndex < pendingExtInf.Length - 1)
                        {
                            stream.Title = pendingExtInf[(commaIndex + 1)..].Trim();
                        }
                    }

                    if (string.IsNullOrWhiteSpace(stream.Title))
                    {
                        stream.Title = line;
                    }

                    streams.Add(stream);
                    pendingExtInf = string.Empty;
                }
            }

            return streams;
        }

        public void CreateOutputDirectory(string outputPath)
        {
            var directory = Path.HasExtension(outputPath)
                ? Path.GetDirectoryName(outputPath)
                : outputPath;

            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = outputPath;
            }

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                Console.WriteLine($"Diretório criado: {directory}");
            }
        }
    }
}
