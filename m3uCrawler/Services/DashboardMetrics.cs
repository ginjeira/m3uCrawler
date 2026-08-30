using System.Text.Json;
using m3uCrawler.Models;

namespace m3uCrawler.Services
{
    /// <summary>
    /// Pure helpers for the dashboard. Distinct from <see cref="WebDashboardService"/>
    /// so the math/semantics can be unit-tested without an HttpListener.
    /// </summary>
    public static class DashboardMetrics
    {
        public static readonly IReadOnlyDictionary<string, string> FieldHelp =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["candidates"]      = "Número de URLs/attachments identificados como potenciais M3U antes do download.",
                ["playlists"]       = "Playlists distintas descarregadas com sucesso (passaram o gate #EXTM3U).",
                ["streams"]         = "Entradas EXTINF extraídas das playlists válidas, antes do filtro por país e do teste.",
                ["streamsAfter"]    = "Streams que sobraram depois do filtro por país; são os candidatos a teste.",
                ["rejectedByCountry"] = "Streams removidos pelo filtro de país (não pertencem ao país alvo).",
                ["tested"]          = "Streams aos quais foi feito um teste HTTP (HEAD/GET).",
                ["working"]         = "Streams que responderam OK no teste (IsWorking=true).",
                ["failed"]          = "Streams cujo teste falhou (IsWorking=false).",
                ["durationMs"]      = "Duração total da execução em milissegundos.",
                ["candidatesFound"] = "Sinónimo de 'candidates' (RunReport.CandidatesFound).",
                ["messages"]        = "Mensagens Telegram analisadas no ciclo.",
                ["countryMatches"]  = "Playlists que ultrapassaram o fast-reject por país.",
                ["playlistsRejected"] = "Playlists rejeitadas pelo filtro por país.",
            };

        /// <summary>
        /// Testados = funcionais + falhados. Devolve a taxa (0..100) ou null se testados &lt;= 0.
        /// </summary>
        public static double? SuccessRate(int working, int failed)
        {
            int total = checked(working + failed);
            return total <= 0 ? (double?)null : Math.Round(100.0 * working / total, 1, MidpointRounding.AwayFromZero);
        }

        public static double? Coverage(int recognized, int expectedTotal)
        {
            if (expectedTotal <= 0) return null;
            return Math.Round(100.0 * recognized / expectedTotal, 1, MidpointRounding.AwayFromZero);
        }

        public static string FormatNumber(long n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

        public static string FormatPercent(double pct) => pct.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";

        /// <summary>
        /// Deriva o estado do run a partir do RunReport: nunca correu, ok, sem streams,
        /// ou falhou (PlaylistsInvalid &gt; 0 com PlaylistsDownloaded=0).
        /// </summary>
        public static string DeriveRunStatus(RunReport? r)
        {
            if (r == null) return "sem-relatorio";
            if (r.Status == "completed" && r.StreamsWorking > 0) return "ok";
            if (r.Status == "completed" && r.StreamsTested == 0) return "sem-streams";
            if (r.PlaylistsDownloaded == 0 && r.PlaylistsInvalid > 0) return "falhou";
            return r.Status;
        }

        /// <summary>
        /// Resumo matemático seguro de RunReport. Não assume que working + failed == tested
        /// (pode haver streams untested / skipped). Apenas relata o que existe.
        /// </summary>
        public static object SummarizeRun(RunReport r)
        {
            var rate = SuccessRate(r.StreamsWorking, r.StreamsFailed);
            return new
            {
                startedAtUtc = r.StartedAt,
                finishedAtUtc = r.FinishedAt,
                durationMs = r.DurationMs,
                status = DeriveRunStatus(r),
                messages = r.MessagesAnalyzed,
                candidates = r.CandidatesFound,
                playlistsDownloaded = r.PlaylistsDownloaded,
                playlistsInvalid = r.PlaylistsInvalid,
                playlistsRejected = r.PlaylistsRejected,
                countryMatches = r.CountryMatches,
                streamsExtracted = r.StreamsExtracted,
                streamsAfterCountryFilter = r.StreamsAfterCountryFilter,
                streamsRejectedByCountry = r.StreamsRejectedByCountry,
                streamsTested = r.StreamsTested,
                streamsWorking = r.StreamsWorking,
                streamsFailed = r.StreamsFailed,
                successRatePercent = rate,
                testsBalanced = r.StreamsWorking + r.StreamsFailed == r.StreamsTested,
            };
        }

        /// <summary>
        /// Descobertas agrupadas por source+name; colapsa duplicações verdadeiras
        /// (mesmo source E mesmo name) e mantém contagem. Discrimina do que é
        /// efectivamente uma entrada distinta (ex.: dois chats Telegram diferentes).
        /// </summary>
        public static IReadOnlyList<DiscoveredPlaylistSummary> DeduplicateBySourceName(
            IEnumerable<DiscoveredPlaylist> items)
        {
            var rawKeys = items
                .Select(p => new
                {
                    Item = p,
                    Key = ((p.Source ?? string.Empty).Trim() + "\u0001" + (p.Name ?? string.Empty).Trim()).ToLowerInvariant(),
                })
                .ToList();

            var groups = rawKeys
                .GroupBy(x => x.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    // Duplicate entries (same source+name) are almost certainly the same
                    // playlist scanned more than once in the run (e.g. two Telegram messages
                    // pointing to the same URL). Aggregating with SUM over-counts the values
                    // because each duplicate carries the same streams/working streams.
                    // Use MAX for the *-per-playlist quantities (StreamCount,
                    // StreamsAfterCountryFilter, WorkingStreams, ChannelsRecognized) so the
                    // dedup result reflects the playlist, not the duplicate count.
                    // Occurrences is preserved separately so the UI can surface the dup.
                    var list = g.Select(x => x.Item).ToList();
                    var first = list[0];
                    return new DiscoveredPlaylistSummary
                    {
                        Source = (first.Source ?? string.Empty).Trim(),
                        Name = (first.Name ?? string.Empty).Trim(),
                        CountryDetected = first.CountryDetected,
                        ChannelsRecognized = list.Max(p => p.ChannelsRecognized),
                        StreamCount = list.Max(p => p.StreamCount),
                        StreamsAfterCountryFilter = list.Max(p => p.StreamsAfterCountryFilter),
                        WorkingStreams = list.Max(p => p.WorkingStreams),
                        State = list.Any(p => string.Equals(p.State, "accepted", StringComparison.OrdinalIgnoreCase)) ? "accepted" : "rejected",
                        Occurrences = list.Count,
                    };
                })
                .OrderByDescending(s => s.WorkingStreams)
                .ThenByDescending(s => s.StreamCount)
                .ThenBy(s => s.Source, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return groups;
        }

        /// <summary>
        /// Lê do filesystem os relatórios Dispatcharr (plan + report) mais recentes sem
        /// tocar em rede. Emparelha plan + report pelo timestamp incluído no nome
        /// do ficheiro (YYYYMMDD_HHMMSS) — fundamental para não mostrar um plan
        /// recente emparelhado com um report antigo de uma run diferente. Devolve
        /// null se não existir nenhum dos dois (sync opt-in, disabled, ou nunca correu).
        /// </summary>
        public static object? ReadLatestDispatcharrSync(string outputDir)
        {
            if (string.IsNullOrWhiteSpace(outputDir) || !Directory.Exists(outputDir))
                return null;

            var plans = Directory.EnumerateFiles(outputDir, "dispatcharr_plan_*.json")
                .Select(p => new DispatcharrArtifact(p, TryExtractTimestamp(p)))
                .Where(a => a.Timestamp.HasValue)
                .OrderByDescending(a => a.Timestamp)
                .ToList();
            var reports = Directory.EnumerateFiles(outputDir, "dispatcharr_report_*.json")
                .Select(p => new DispatcharrArtifact(p, TryExtractTimestamp(p)))
                .Where(a => a.Timestamp.HasValue)
                .OrderByDescending(a => a.Timestamp)
                .ToList();

            if (plans.Count == 0 && reports.Count == 0) return null;

            // Emparelhar plan + report pelo mesmo timestamp. Se não houver par para a
            // última plan, fica reportado como "plan sem report" e vice-versa.
            DispatcharrArtifact? latestPlan = plans.FirstOrDefault();
            DispatcharrArtifact? latestReport = null;

            if (latestPlan?.Timestamp != null)
            {
                latestReport = reports.FirstOrDefault(r => r.Timestamp == latestPlan.Timestamp);
            }
            if (latestReport == null)
            {
                // Fallback: o report mais recente que não esteja emparelhado com uma plan mais recente.
                latestReport = reports.FirstOrDefault();
            }

            string? counts = null;
            bool? dryRun = null;
            string? dispatcharrVersion = null;
            string? startedAtUtc = null;
            int totalChannels = 0, matched = 0, newChannels = 0, newStreams = 0, removed = 0, skipped = 0, ambiguous = 0, failed = 0;
            bool planValid = false, reportValid = false;

            try
            {
                if (latestPlan != null)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(latestPlan.Path));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("dryRun", out var dr)) dryRun = dr.GetBoolean();
                    if (root.TryGetProperty("counts", out var c))
                    {
                        matched = c.TryGetProperty("matched", out var m) ? m.GetInt32() : 0;
                        newChannels = c.TryGetProperty("newChannels", out var x) ? x.GetInt32() : 0;
                        newStreams = c.TryGetProperty("newStreams", out var s) ? s.GetInt32() : 0;
                        removed = c.TryGetProperty("removedStreams", out var r2) ? r2.GetInt32() : 0;
                        skipped = c.TryGetProperty("skipped", out var sk) ? sk.GetInt32() : 0;
                        ambiguous = c.TryGetProperty("ambiguous", out var am) ? am.GetInt32() : 0;
                        failed = c.TryGetProperty("failed", out var f2) ? f2.GetInt32() : 0;
                        if (c.TryGetProperty("totalChannels", out var tc)) totalChannels = tc.GetInt32();
                    }
                    planValid = true;
                }
            }
            catch (Exception ex)
            {
                counts = $"erro-leitura-plan:{ex.GetType().Name}";
            }

            try
            {
                if (latestReport != null)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(latestReport.Path));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("dispatcharrVersion", out var v)) dispatcharrVersion = v.GetString();
                    if (root.TryGetProperty("startedAtUtc", out var sa)) startedAtUtc = sa.GetString();
                    reportValid = true;
                }
            }
            catch (Exception ex)
            {
                counts = (counts ?? "") + $" erro-leitura-report:{ex.GetType().Name}";
            }

            return new
            {
                latestPlanPath = latestPlan?.Path,
                latestReportPath = latestReport?.Path,
                planReportPaired = latestPlan?.Timestamp != null && latestReport?.Timestamp == latestPlan?.Timestamp,
                planValid,
                reportValid,
                startedAtUtc,
                dispatcharrVersion,
                dryRun,
                totalChannels,
                matched,
                newChannels,
                newStreams,
                removedStreams = removed,
                skipped,
                ambiguous,
                failed,
                error = counts,
            };
        }

        /// <summary>
        /// Extrai o timestamp UTC (yyyyMMdd_HHmmss) do nome do ficheiro
        /// "dispatcharr_{plan|report}_YYYYMMDD_HHMMSS.json". Devolve null se o
        /// padrão não for reconhecido.
        /// </summary>
        private static DateTime? TryExtractTimestamp(string filePath)
        {
            var name = Path.GetFileNameWithoutExtension(filePath);
            // Captura os últimos 15 caracteres do nome (yyyyMMdd_HHmmss).
            if (name.Length < 15) return null;
            var tail = name[^15..];
            if (!DateTime.TryParseExact(tail, "yyyyMMdd_HHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
                return null;
            return dt;
        }

        private sealed record DispatcharrArtifact(string Path, DateTime? Timestamp);
    }

    public sealed class DiscoveredPlaylistSummary
    {
        public string Source { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string CountryDetected { get; set; } = string.Empty;
        public int ChannelsRecognized { get; set; }
        public int StreamCount { get; set; }
        public int StreamsAfterCountryFilter { get; set; }
        public int WorkingStreams { get; set; }
        public string State { get; set; } = string.Empty;
        public int Occurrences { get; set; }
    }
}

