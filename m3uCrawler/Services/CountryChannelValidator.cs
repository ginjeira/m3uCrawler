using System.Text.Json;
using m3uCrawler.Models;

namespace m3uCrawler.Services
{
    public class CountryChannelValidationResult
    {
        public bool IsMatch { get; set; }
        public string Country { get; set; } = string.Empty;
        public List<string> MatchedAliases { get; set; } = new();
        public string RawContent { get; set; } = string.Empty;
    }

    public class CountryStreamMatch
    {
        public M3uStream Stream { get; set; } = new();
        public string Country { get; set; } = string.Empty;
        public List<string> MatchedAliases { get; set; } = new();
    }

    public class CountryChannelValidator
    {
        private readonly string _rootDirectory;
        private readonly Dictionary<string, List<string>> _cache = new(StringComparer.OrdinalIgnoreCase);

        public CountryChannelValidator(string? rootDirectory = null)
        {
            _rootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
                ? Path.Combine(AppContext.BaseDirectory, "runtime-data", "countries")
                : rootDirectory;

            Directory.CreateDirectory(_rootDirectory);
        }

        public CountryChannelValidationResult ValidatePlaylist(string playlistContent, string countryCode)
        {
            var aliases = LoadCountryAliases(countryCode);
            if (aliases.Count == 0)
            {
                return new CountryChannelValidationResult
                {
                    IsMatch = false,
                    Country = countryCode,
                    MatchedAliases = new List<string>(),
                    RawContent = playlistContent
                };
            }

            var normalized = NormalizeText(playlistContent);
            var matches = new List<string>();

            foreach (var alias in aliases)
            {
                var candidate = NormalizeText(alias);
                if (!string.IsNullOrWhiteSpace(candidate) && normalized.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(alias);
                }
            }

            var result = new CountryChannelValidationResult
            {
                IsMatch = matches.Count > 0,
                Country = countryCode,
                MatchedAliases = matches.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                RawContent = playlistContent
            };

            return result;
        }

        public List<CountryStreamMatch> ValidateStreams(List<M3uStream> streams, string countryCode)
        {
            var matches = new List<CountryStreamMatch>();
            var aliases = LoadCountryAliases(countryCode);
            if (aliases.Count == 0)
            {
                return matches;
            }

            foreach (var stream in streams)
            {
                var combined = new[] { stream.Title, stream.Group, stream.Url, stream.OriginalExtInf }.Where(x => !string.IsNullOrWhiteSpace(x));
                var text = string.Join(" ", combined);
                var matched = new List<string>();

                foreach (var alias in aliases)
                {
                    var normalizedAlias = NormalizeText(alias);
                    if (!string.IsNullOrWhiteSpace(normalizedAlias) && NormalizeText(text).Contains(normalizedAlias, StringComparison.OrdinalIgnoreCase))
                    {
                        matched.Add(alias);
                    }
                }

                if (matched.Count > 0)
                {
                    matches.Add(new CountryStreamMatch
                    {
                        Stream = stream,
                        Country = countryCode,
                        MatchedAliases = matched.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    });
                }
            }

            return matches;
        }

        private List<string> LoadCountryAliases(string countryCode)
        {
            if (_cache.TryGetValue(countryCode, out var cached))
            {
                return cached;
            }

            var filePath = Path.Combine(_rootDirectory, $"{countryCode}.json");
            if (!File.Exists(filePath))
            {
                var fallback = BuildFallbackCountryAliases(countryCode);
                _cache[countryCode] = fallback;
                return fallback;
            }

            try
            {
                var json = File.ReadAllText(filePath);
                var payload = JsonSerializer.Deserialize<CountryListFile>(json);
                var aliases = payload?.Channels ?? new List<string>();
                if (aliases.Count == 0)
                {
                    aliases = BuildFallbackCountryAliases(countryCode);
                }

                _cache[countryCode] = aliases;
                return aliases;
            }
            catch
            {
                var fallback = BuildFallbackCountryAliases(countryCode);
                _cache[countryCode] = fallback;
                return fallback;
            }
        }

        private static List<string> BuildFallbackCountryAliases(string countryCode)
        {
            return countryCode.ToLowerInvariant() switch
            {
                "pt" => new List<string>
                {
                    "RTP1","RTP 1","RTP1 HD","RTP 1 HD","SIC","SIC HD","TVI","TVI HD","SportTV","SPORT TV","SPORT TV 1","SPORTTV1","BTV","BTV HD","BENFICATV","BENFICA TV","Canal 11","TVI24","RTP2","RTP 2"
                },
                "es" => new List<string> { "La 1","TVE","Antena 3","Telecinco","Cuatro","LaSexta" },
                "br" => new List<string> { "Globo","SBT","Record","Band","Rede TV","TV Brasil" },
                _ => new List<string>()
            };
        }

        private static string NormalizeText(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            var text = input.Replace("-", " ")
                .Replace("_", " ")
                .Replace("/", " ")
                .Replace(".", " ")
                .Replace("\t", " ")
                .Replace("\r", " ")
                .Replace("\n", " ");

            var sb = new System.Text.StringBuilder();
            foreach (var ch in text)
            {
                if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
                {
                    sb.Append(ch);
                }
            }

            return sb.ToString().Trim();
        }

        private sealed class CountryListFile
        {
            public string Country { get; set; } = string.Empty;
            public List<string> Channels { get; set; } = new();
        }
    }
}
