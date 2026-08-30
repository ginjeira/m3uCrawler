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

        // Campos aditivos para o pipeline de país (não quebram consumidores existentes).
        public int Threshold { get; set; } = 1;
        public int RecognizedChannelCount { get; set; }
        public List<string> RecognizedChannels { get; set; } = new();
        public bool IsTargetCountry => RecognizedChannelCount >= Threshold;
    }

    public class CountryStreamMatch
    {
        public M3uStream Stream { get; set; } = new();
        public string Country { get; set; } = string.Empty;
        public List<string> MatchedAliases { get; set; } = new();

        /// <summary>
        /// Indica se a correspondência foi obtida via fallback de group-title em vez de
        /// título. Útil para auditoria: matches só por group-title são suspeita e devem
        /// ser reportadas para revisão humana.
        /// </summary>
        public bool MatchedViaGroup { get; set; }
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

        /// <summary>
        /// Analisa o conteúdo de uma playlist e determina se pertence ao país alvo, contando
        /// canais DISTINTOS reconhecidos (variantes do mesmo canal, ex.: "RTP1" e "RTP 1", contam
        /// como um só). O país é considerado alvo quando o número de canais distintos atinge o
        /// threshold (3 por defeito).
        /// </summary>
        public CountryChannelValidationResult AnalyzePlaylist(string playlistContent, string countryCode, int threshold = 3)
        {
            var result = new CountryChannelValidationResult
            {
                Country = countryCode,
                Threshold = threshold,
                RawContent = playlistContent
            };

            var aliases = LoadCountryAliases(countryCode);
            if (aliases.Count == 0)
            {
                return result;
            }

            // A correspondência é feita contra os TÍTULOS dos canais extraídos dos #EXTINF
            // (via M3uParserService), e não contra o conteúdo bruto da playlist. Isto evita
            // falsos positivos de aliases curtos dentro de palavras não relacionadas
            // (ex.: "SIC" não deve corresponder a "basics").
            var parser = new M3uParserService();
            var matchedAliases = new List<string>();
            var recognizedChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var stream in parser.Parse(playlistContent))
            {
                var titleTokens = Tokenize(NormalizeText(stream.Title));
                if (titleTokens.Count == 0) continue;

                foreach (var alias in aliases)
                {
                    var aliasTokens = Tokenize(NormalizeText(alias));
                    if (aliasTokens.Count == 0) continue;

                    // O conjunto de tokens do alias tem de estar contido nos tokens do título.
                    if (aliasTokens.All(t => titleTokens.Contains(t, StringComparer.OrdinalIgnoreCase)))
                    {
                        matchedAliases.Add(alias);
                        recognizedChannels.Add(CanonicalChannelKey(alias));
                    }
                }
            }

            result.MatchedAliases = matchedAliases.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            result.RecognizedChannels = recognizedChannels.ToList();
            result.RecognizedChannelCount = recognizedChannels.Count;
            result.IsMatch = recognizedChannels.Count >= threshold;

            return result;
        }

        /// <summary>
        /// Valida individualmente cada stream contra os aliases do país alvo.
        ///
        /// Semântica (consistente com AnalyzePlaylist):
        /// 1) Tentar correspondência pelos TOKENS do título do canal. É o sinal primário.
        /// 2) Se não houver match pelo título, tentar como FALLBACK pelos TOKENS do
        ///    group-title — usado apenas para categorias explícitas do país-alvo
        ///    (ex.: "Portugal", "PT", "🇵🇹"). O canal devolvido desta forma fica marcado
        ///    com <see cref="CountryStreamMatch.MatchedViaGroup"/> = true para auditoria.
        ///    NÃO transforma qualquer group-title em aprovação automática: o group-title
        ///    tem de casar com uma das categorias explícitas do país (ver
        ///    <see cref="LoadCountryGroupTokens"/>).
        ///
        /// Importante: NÃO usa string.Contains sobre o texto combinado — usa-se a mesma
        /// tokenização e as mesmas regras de normalização que AnalyzePlaylist, de modo a
        /// evitar falsos positivos como "basics"→"sic" ou "atvinew"→"tvi".
        /// </summary>
        public List<CountryStreamMatch> ValidateStreams(List<M3uStream> streams, string countryCode)
        {
            var matches = new List<CountryStreamMatch>();
            var aliases = LoadCountryAliases(countryCode);
            if (aliases.Count == 0)
            {
                return matches;
            }

            var groupTokens = LoadCountryGroupTokens(countryCode);

            foreach (var stream in streams)
            {
                var matchedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var matchedViaGroup = false;

                var titleTokens = Tokenize(NormalizeText(stream.Title ?? string.Empty));
                if (titleTokens.Count > 0)
                {
                    foreach (var alias in aliases)
                    {
                        if (AliasMatchesTokens(alias, titleTokens))
                        {
                            matchedAliases.Add(alias);
                        }
                    }
                }

                if (matchedAliases.Count == 0 && groupTokens.Count > 0 && !string.IsNullOrWhiteSpace(stream.Group))
                {
                    var groupTokenSet = Tokenize(NormalizeText(stream.Group));
                    var groupMatchesCategory = groupTokenSet.Count > 0
                        && groupTokenSet.Any(gt => groupTokens.Contains(gt, StringComparer.OrdinalIgnoreCase));

                    if (groupMatchesCategory)
                    {
                        // Segurança contra canais estrangeiros mal categorizados pelo fornecedor:
                        // o fallback só é activado se o TÍTULO também contiver pelo menos um token
                        // da categoria PT (portugal, pt, 🇵🇹). Isto significa que um stream cujo
                        // nome não tem qualquer sinal PT (ex.: "Sky TG24" listado num grupo
                        // "Portugal") é rejeitado, mesmo que o group-title seja PT.
                        //
                        // Casos legítimos que continuam a funcionar:
                        //   - "PT || JimJam"     -> título contém "PT"       -> aceite
                        //   - "Canal Portugal"   -> título contém "Portugal" -> aceite
                        //   - "RTP1" com group="Portugal" -> título já bate por alias (matchedAliases.Count > 0)
                        //
                        // Casos bloqueados:
                        //   - "Sky TG24" com group-title="Portugal"   -> título sem token PT -> rejeitado
                        //   - "RandomChannel" com group-title="Portugal" -> título sem token PT -> rejeitado
                        var titleTokenSet = Tokenize(NormalizeText(stream.Title ?? string.Empty));
                        var titleHasCountryToken = titleTokenSet.Count > 0
                            && titleTokenSet.Any(tt => groupTokens.Contains(tt, StringComparer.OrdinalIgnoreCase));

                        if (titleHasCountryToken)
                        {
                            matchedAliases.Add("group-title");
                            matchedViaGroup = true;
                        }
                    }
                }

                if (matchedAliases.Count > 0)
                {
                    matches.Add(new CountryStreamMatch
                    {
                        Stream = stream,
                        Country = countryCode,
                        MatchedAliases = matchedAliases.ToList(),
                        MatchedViaGroup = matchedViaGroup
                    });
                }
            }

            return matches;
        }

        /// <summary>
        /// Verifica se um alias bate nos tokens do título. O conjunto de tokens do alias
        /// (versão normalizada) tem de estar totalmente contido nos tokens fornecidos.
        /// Ignora aliases que não tenham tokens após normalização.
        /// </summary>
        private static bool AliasMatchesTokens(string alias, List<string> tokens)
        {
            var aliasTokens = Tokenize(NormalizeText(alias));
            if (aliasTokens.Count == 0) return false;
            return aliasTokens.All(t => tokens.Contains(t, StringComparer.OrdinalIgnoreCase));
        }

        private List<string> LoadCountryAliases(string countryCode)
        {
            if (_cache.TryGetValue(countryCode, out var cached))
            {
                return cached;
            }

            var filePath = Path.Combine(_rootDirectory, $"{countryCode}.json");
            List<string> aliases;
            if (!File.Exists(filePath))
            {
                aliases = BuildFallbackCountryAliases(countryCode);
            }
            else
            {
                try
                {
                    var json = File.ReadAllText(filePath);
                    var payload = JsonSerializer.Deserialize<CountryListFile>(json);
                    aliases = payload?.Channels ?? new List<string>();
                    if (aliases.Count == 0)
                    {
                        aliases = BuildFallbackCountryAliases(countryCode);
                    }
                }
                catch
                {
                    aliases = BuildFallbackCountryAliases(countryCode);
                }
            }

            // Carrega indicadores suplementares (channel-indicators.json) para o mesmo país.
            // Não duplica entradas: union case-insensitive. Esta é a forma conservadora de
            // integrar o ficheiro sem assumir uma nova arquitectura.
            var supplementary = LoadSupplementaryIndicators(countryCode);
            if (supplementary.Count > 0)
            {
                var combined = new List<string>(aliases);
                var seen = new HashSet<string>(
                    aliases.Select(a => NormalizeText(a)),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var s in supplementary)
                {
                    var key = NormalizeText(s);
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    if (seen.Add(key)) combined.Add(s);
                }
                aliases = combined;
            }

            _cache[countryCode] = aliases;
            return aliases;
        }

        private List<string> LoadSupplementaryIndicators(string countryCode)
        {
            // O ficheiro channel-indicators.json existe em runtime-data/ e é específico
            // de Portugal (lista de variantes regionais, desportos, sub-canais). É carregado
            // apenas para PT; para outros países, retorna vazio. Se uma fonte mais
            // genérica surgir, esta função pode ser estendida sem mexer nos consumidores.
            if (!string.Equals(countryCode, "pt", StringComparison.OrdinalIgnoreCase))
            {
                return new List<string>();
            }

            // Locais candidatos (por ordem de preferência):
            // 1) Junto ao rootDirectory configurado pelo consumidor (mais explícito).
            // 2) Irmão do rootDirectory (runtime-data/channel-indicators.json).
            // 3) runtime-data/channel-indicators.json junto de AppContext.BaseDirectory.
            // 4) runtime-data/channel-indicators.json junto de Directory.GetCurrentDirectory().
            var candidates = new List<string>
            {
                Path.Combine(_rootDirectory, "channel-indicators.json")
            };
            try
            {
                var parent = Directory.GetParent(_rootDirectory)?.FullName;
                if (!string.IsNullOrEmpty(parent))
                {
                    candidates.Add(Path.Combine(parent, "channel-indicators.json"));
                }
            }
            catch { }
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "runtime-data", "channel-indicators.json"));
            candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "runtime-data", "channel-indicators.json"));

            foreach (var path in candidates)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var json = File.ReadAllText(path);
                    var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("indicators", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        var list = new List<string>();
                        foreach (var item in arr.EnumerateArray())
                        {
                            var v = item.GetString();
                            if (!string.IsNullOrWhiteSpace(v)) list.Add(v.Trim());
                        }
                        return list;
                    }
                }
                catch
                {
                    // Tenta o próximo candidato.
                }
            }
            return new List<string>();
        }

        private static List<string> LoadCountryGroupTokens(string countryCode)
        {
            // Tokens que um group-title tem de conter para o fallback por categoria ser
            // considerado. Mantém-se restrito: nomes oficiais do país, códigos ISO curtos
            // (PT, ES, BR) e a bandeira emoji. Não usamos palavras vagas que pudessem
            // casar acidentalmente (ex.: "TV", "Grupo").
            var tokens = new List<string>();
            switch (countryCode.ToLowerInvariant())
            {
                case "pt":
                    tokens.AddRange(new[] { "portugal", "pt", "🇵🇹" });
                    break;
                case "es":
                    tokens.AddRange(new[] { "españa", "espanol", "es", "🇪🇸" });
                    break;
                case "br":
                    tokens.AddRange(new[] { "brasil", "brazil", "br", "🇧🇷" });
                    break;
                default:
                    tokens.Add(countryCode.ToLowerInvariant());
                    break;
            }
            return tokens;
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

        private static List<string> Tokenize(string normalized)
        {
            return normalized
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }

        private static string CanonicalChannelKey(string alias)
        {
            if (string.IsNullOrWhiteSpace(alias)) return string.Empty;

            var sb = new System.Text.StringBuilder();
            foreach (var ch in alias)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                }
                // Separadores (-, _, ., espaço, /, etc.) são ignorados para agrupar variantes.
            }

            return sb.ToString();
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
