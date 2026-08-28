using m3uCrawler.Models;
using System.Text;
using System.Text.Json;

namespace m3uCrawler.Services
{
    public class ImportHistoryService
    {
        private readonly string _historyPath;

        public ImportHistoryService(string outputDir)
        {
            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            _historyPath = Path.Combine(outputDir, "import_history.json");
        }

        public async Task RecordImportAsync(ImportHistoryEntry entry)
        {
            var entries = await LoadAllEntriesAsync();
            entries.Add(entry);
            await SaveAllEntriesAsync(entries);
        }

        public async Task<List<ImportHistoryEntry>> GetRecentAsync(TimeSpan window)
        {
            var entries = await LoadAllEntriesAsync();
            var cutoff = DateTime.UtcNow.Subtract(window);
            return entries.Where(e => e.Timestamp >= cutoff)
                          .OrderByDescending(e => e.Timestamp)
                          .ToList();
        }

        private async Task<List<ImportHistoryEntry>> LoadAllEntriesAsync()
        {
            if (!File.Exists(_historyPath))
                return new List<ImportHistoryEntry>();

            var json = await File.ReadAllTextAsync(_historyPath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
                return new List<ImportHistoryEntry>();

            try
            {
                return JsonSerializer.Deserialize<List<ImportHistoryEntry>>(json, JsonOptions) ?? new List<ImportHistoryEntry>();
            }
            catch
            {
                // Fallback for case-insensitive older JSON entries
                var options = new JsonSerializerOptions(JsonOptions)
                {
                    PropertyNameCaseInsensitive = true
                };
                return JsonSerializer.Deserialize<List<ImportHistoryEntry>>(json, options) ?? new List<ImportHistoryEntry>();
            }
        }

        private async Task SaveAllEntriesAsync(List<ImportHistoryEntry> entries)
        {
            var json = JsonSerializer.Serialize(entries, JsonOptions);
            await File.WriteAllTextAsync(_historyPath, json, Encoding.UTF8);
        }

        private static JsonSerializerOptions JsonOptions => new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
}
