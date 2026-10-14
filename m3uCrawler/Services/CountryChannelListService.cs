using System.Text.Json;

namespace m3uCrawler.Services
{
    public class CountryChannelList
    {
        public string Country { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public List<string> Channels { get; set; } = new();
    }

    /// <summary>
    /// W6 — O país é <b>configuração/validação</b>, não uma entidade de
    /// domínio: não existe entidade <c>Country</c> na base de dados. Cada
    /// país vive como um ficheiro JSON de aliases em
    /// <c>runtime-data/countries/&lt;code&gt;.json</c>. Este serviço é a
    /// única fonte de verdade para criar/ler/actualizar/eliminar essa
    /// configuração; a validação da playlist é feita por
    /// <see cref="CountryChannelValidator"/> sobre a mesma configuração.
    /// O separador "Canais / Países" do Dashboard é, por isso, uma
    /// ferramenta de configuração/validação e não um CRUD de país.
    /// </summary>
    public class CountryChannelListService
    {
        private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

        /// <summary>
        /// Padrão seguro para códigos de país usados como nome de ficheiro
        /// (<c>&lt;code&gt;.json</c>): 1 a 10 caracteres alfanuméricos
        /// minúsculos, com hífens interiores. Rejeita <c>/</c>, <c>\</c>,
        /// <c>..</c>, espaços e qualquer caminho (L11).
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex CountryCodePattern =
            new(@"^[a-z0-9]([a-z0-9-]{0,8}[a-z0-9])?$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private readonly string _rootDirectory;

        public CountryChannelListService(string? rootDirectory = null)
        {
            _rootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
                ? Path.Combine(Directory.GetCurrentDirectory(), "runtime-data", "countries")
                : rootDirectory;
        }

        /// <summary>
        /// <c>true</c> quando <paramref name="countryCode"/> é um código
        /// seguro para usar como nome de ficheiro. Nunca permite caminhos
        /// (<c>/</c>, <c>\</c>, <c>..</c>), pelo que impede path traversal.
        /// </summary>
        public static bool IsValidCountryCode(string? countryCode)
            => countryCode is not null && CountryCodePattern.IsMatch(countryCode);

        public List<CountryChannelList> GetAllCountries()
        {
            if (!Directory.Exists(_rootDirectory))
            {
                return new List<CountryChannelList>();
            }

            var files = Directory.EnumerateFiles(_rootDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var items = new List<CountryChannelList>();
            foreach (var file in files)
            {
                try
                {
                    var content = File.ReadAllText(file);
                    var item = JsonSerializer.Deserialize<CountryChannelList>(content, ReadOptions);
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

            // Leitura pura: nunca cria nem sobrepõe ficheiros. Wave A — as
            // listas de país são 100% do utilizador: não há provisionamento
            // automático no arranque; gravações são explícitas via SaveCountry
            // (POST /api/country/save) ou --import-country-config.
            return items;
        }

        public CountryChannelList GetCountry(string countryCode)
        {
            var normalized = NormalizeCountryCode(countryCode);
            if (!IsValidCountryCode(normalized))
            {
                throw new ArgumentException(
                    "Código de país inválido.", nameof(countryCode));
            }

            var config = GetAllCountries().FirstOrDefault(x => NormalizeCountryCode(x.Country) == normalized);
            if (config is not null)
            {
                return config;
            }

            // Sem ficheiro, devolve um resultado vazio sem persistir nada.
            return new CountryChannelList
            {
                Country = normalized,
                DisplayName = GetDisplayName(normalized),
                Channels = new List<string>(),
            };
        }

        public void SaveCountry(CountryChannelList country)
        {
            var normalizedCountry = NormalizeCountryCode(country.Country);
            if (string.IsNullOrWhiteSpace(normalizedCountry))
            {
                throw new ArgumentException("Country is required.");
            }

            if (!IsValidCountryCode(normalizedCountry))
            {
                throw new ArgumentException("Country code is invalid.", nameof(country));
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

            Directory.CreateDirectory(_rootDirectory);
            var filePath = Path.Combine(_rootDirectory, $"{normalizedCountry}.json");
            var json = JsonSerializer.Serialize(country, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }

        /// <summary>
        /// W6 — Elimina a configuração de país
        /// (<c>&lt;code&gt;.json</c>). Devolve <c>true</c> quando um ficheiro
        /// foi eliminado; <c>false</c> quando não existia (ou o código é
        /// vazio). Não toca na playlist publicada nem no catálogo.
        /// </summary>
        public bool DeleteCountry(string countryCode)
        {
            var normalized = NormalizeCountryCode(countryCode);
            if (string.IsNullOrWhiteSpace(normalized) || !IsValidCountryCode(normalized))
            {
                return false;
            }

            var filePath = Path.Combine(_rootDirectory, $"{normalized}.json");
            if (!File.Exists(filePath))
            {
                return false;
            }

            File.Delete(filePath);
            return true;
        }

        public string GetCountryDirectory() => _rootDirectory;

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
