using m3uCrawler.Models;
using System.Text;

namespace m3uCrawler.Services
{
    public class LoggingService
    {
        private readonly string _logFilePath;

        public LoggingService(string logDirectory = "output")
        {
            if (!Directory.Exists(logDirectory))
                Directory.CreateDirectory(logDirectory);

            var timestamp = DateTime.Now.ToString("yyyyMMdd");
            _logFilePath = Path.Combine(logDirectory, $"m3uCrawler_{timestamp}.log");
        }

        public async Task LogAsync(string message, LogLevel level = LogLevel.Info)
        {
            var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
            
            // Log para consola
            switch (level)
            {
                case LogLevel.Error:
                    Console.ForegroundColor = ConsoleColor.Red;
                    break;
                case LogLevel.Warning:
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    break;
                case LogLevel.Success:
                    Console.ForegroundColor = ConsoleColor.Green;
                    break;
                default:
                    Console.ForegroundColor = ConsoleColor.White;
                    break;
            }

            Console.WriteLine(logEntry);
            Console.ResetColor();

            // Log para ficheiro
            await File.AppendAllTextAsync(_logFilePath, logEntry + Environment.NewLine, Encoding.UTF8);
        }

        public async Task LogStreamResult(M3uStream stream)
        {
            var status = stream.IsWorking ? "✓" : "✗";
            var message = $"{status} {stream.Url} ({stream.ResponseTime}ms)";
            await LogAsync(message, stream.IsWorking ? LogLevel.Success : LogLevel.Warning);
        }

        public async Task LogSummary(List<M3uStream> streams)
        {
            var working = streams.Count(s => s.IsWorking);
            var total = streams.Count;
            var avgTime = streams.Where(s => s.IsWorking).Any() 
                ? streams.Where(s => s.IsWorking).Average(s => s.ResponseTime) 
                : 0;

            await LogAsync($"=== RESUMO ===", LogLevel.Info);
            await LogAsync($"Total testado: {total}", LogLevel.Info);
            await LogAsync($"Funcionais: {working}", LogLevel.Success);
            await LogAsync($"Não funcionais: {total - working}", LogLevel.Warning);
            await LogAsync($"Tempo médio: {avgTime:F0}ms", LogLevel.Info);
        }
    }

    public enum LogLevel
    {
        Info,
        Success,
        Warning,
        Error
    }
}
