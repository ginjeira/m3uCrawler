using System.Text.Json;

namespace m3uCrawler.Services
{
    public class CountryChannelList
    {
        public string Country { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public List<string> Channels { get; set; } = new();
    }

    public class CountryChannelListService
    {
        private readonly string _rootDirectory;

        public CountryChannelListService(string? rootDirectory = null)
        {
            _rootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
                ? Path.Combine(Directory.GetCurrentDirectory(), "runtime-data", "countries")
                : rootDirectory;

            Directory.CreateDirectory(_rootDirectory);
        }

        public List<CountryChannelList> GetAllCountries()
        {
            var files = Directory.EnumerateFiles(_rootDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var items = new List<CountryChannelList>();
            foreach (var file in files)
            {
                try
                {
                    var content = File.ReadAllText(file);
                    var item = JsonSerializer.Deserialize<CountryChannelList>(content);
                    if (item is not null && !string.IsNullOrWhiteSpace(item.Country))
                    {
                        item.Channels ??= new List<string>();
                        if (string.IsNullOrWhiteSpace(item.DisplayName))
                        {
                            item.DisplayName = item.Country.ToUpperInvariant();
                        }

                        items.Add(item);
                    }
                }
                catch
                {
                    // Ignorar ficheiros inválidos para não bloquear o dashboard.
                }
            }

            if (items.Count == 0)
            {
                var ptFallback = CreateDefaultList("pt", "Portugal");
                SaveCountry(ptFallback);
                items.Add(ptFallback);
            }

            return items;
        }

        public CountryChannelList GetCountry(string countryCode)
        {
            var normalized = NormalizeCountryCode(countryCode);
            var config = GetAllCountries().FirstOrDefault(x => NormalizeCountryCode(x.Country) == normalized);
            if (config is not null)
            {
                return config;
            }

            var fallback = CreateDefaultList(normalized, GetDisplayName(normalized));
            SaveCountry(fallback);
            return fallback;
        }

        public void SaveCountry(CountryChannelList country)
        {
            var normalizedCountry = NormalizeCountryCode(country.Country);
            if (string.IsNullOrWhiteSpace(normalizedCountry))
            {
                throw new ArgumentException("Country is required.");
            }

            country.Country = normalizedCountry;
            country.DisplayName = string.IsNullOrWhiteSpace(country.DisplayName)
                ? GetDisplayName(normalizedCountry)
                : country.DisplayName;

            country.Channels = (country.Channels ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var filePath = Path.Combine(_rootDirectory, $"{normalizedCountry}.json");
            var json = JsonSerializer.Serialize(country, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }

        public string GetCountryDirectory() => _rootDirectory;

        private static CountryChannelList CreateDefaultList(string countryCode, string displayName)
        {
            var fallback = countryCode.ToLowerInvariant() switch
            {
                "pt" => new CountryChannelList
                {
                    Country = "pt",
                    DisplayName = "Portugal",
                    Channels = new List<string>
                    {
                        "RTP1", "RTP 1", "RTP2", "RTP 2", "SIC", "TVI", "SPORT TV 1",
                        "SPORTTV1", "BTV", "BENFICATV", "BENFICA TV", "TVI24", "CANAL 11"
                    }
                },
                "es" => new CountryChannelList
                {
                    Country = "es",
                    DisplayName = "Espanha",
                    Channels = new List<string> { "La 1", "Antena 3", "Telecinco", "LaSexta", "Cuatro" }
                },
                "br" => new CountryChannelList
                {
                    Country = "br",
                    DisplayName = "Brasil",
                    Channels = new List<string> { "Globo", "SBT", "Band", "Record", "Rede TV", "TV Brasil" }
                },
                _ => new CountryChannelList
                {
                    Country = countryCode,
                    DisplayName = displayName,
                    Channels = new List<string>()
                }
            };

            return fallback;
        }

        private static string NormalizeCountryCode(string? countryCode)
        {
            return (countryCode ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string GetDisplayName(string countryCode)
        {
            return countryCode.ToLowerInvariant() switch
            {
                "pt" => "Portugal",
                "es" => "Espanha",
                "br" => "Brasil",
                _ => countryCode.ToUpperInvariant()
            };
        }
    }
}
