using m3uCrawler.Build;
using m3uCrawler.Models;
using m3uCrawler.Services.Sync;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace m3uCrawler.Services
{
    public static class WebDashboardService
    {
        public static async Task RunDashboardAsync(string outputDir, int port, ImportHistoryService historyService, string? webToken = null, CancellationToken cancellationToken = default)
        {
            var listener = new HttpListener();
            var prefix = $"http://+:{port}/";
            listener.Prefixes.Add(prefix);
            listener.AuthenticationSchemes = AuthenticationSchemes.Anonymous;

            try
            {
                listener.Start();
                Console.WriteLine($"🌐 Dashboard iniciado em {prefix}");
                if (!string.IsNullOrWhiteSpace(webToken))
                {
                    Console.WriteLine($"🔐 Dashboard protegido por token partilhado (Authorization: Bearer <token> ou ?token=).");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Não foi possível iniciar o dashboard web: {ex.Message}");
                Console.WriteLine(ex);
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    _ = Task.Run(async () => await HandleRequestAsync(context, outputDir, historyService, webToken));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Erro no dashboard web: {ex.Message}");
                }
            }

            listener.Stop();
        }

        private static async Task HandleRequestAsync(HttpListenerContext context, string outputDir, ImportHistoryService historyService, string? webToken = null)
        {
            var requestPath = context.Request.Url?.AbsolutePath ?? "/";
            var query = context.Request.Url?.Query ?? string.Empty;

            // Protecção opcional por token partilhado: se --web-token foi configurado,
            // todos os endpoints (incluindo /api/playlist* que servem a playlist funcional
            // com URLs Xtream reais) exigem o token via header Authorization: Bearer
            // ou query ?token=. Se não configurado, mantém-se o comportamento aberto
            // (compatibilidade com deployments locais).
            if (!IsRequestAuthorized(context.Request, webToken))
            {
                await WriteUnauthorizedAsync(context.Response);
                return;
            }

            if (requestPath.Equals("/api/history", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(context.Response, await historyService.GetRecentAsync(TimeSpan.FromHours(72)));
                return;
            }

            if (requestPath.Equals("/api/version", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(context.Response, BuildVersionPayload());
                return;
            }

            if (requestPath.Equals("/api/countries", StringComparison.OrdinalIgnoreCase))
            {
                var service = new CountryChannelListService(Path.Combine(Directory.GetCurrentDirectory(), "runtime-data", "countries"));
                await WriteJsonAsync(context.Response, service.GetAllCountries());
                return;
            }

            if (requestPath.Equals("/api/country", StringComparison.OrdinalIgnoreCase))
            {
                var countryCode = context.Request.QueryString["country"];
                if (string.IsNullOrWhiteSpace(countryCode))
                {
                    await WriteJsonAsync(context.Response, new { error = "Parâmetro country é obrigatório." });
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    return;
                }

                var service = new CountryChannelListService(Path.Combine(Directory.GetCurrentDirectory(), "runtime-data", "countries"));
                await WriteJsonAsync(context.Response, service.GetCountry(countryCode));
                return;
            }

            if (requestPath.Equals("/api/country/validate", StringComparison.OrdinalIgnoreCase))
            {
                var countryCode = context.Request.QueryString["country"] ?? "pt";
                var countryList = new CountryChannelListService(Path.Combine(Directory.GetCurrentDirectory(), "runtime-data", "countries"));
                var validator = new CountryChannelValidator(Path.Combine(Directory.GetCurrentDirectory(), "runtime-data", "countries"));
                var playlistPath = Path.Combine(outputDir, "playlist.m3u");
                var playlistText = File.Exists(playlistPath) ? await File.ReadAllTextAsync(playlistPath, Encoding.UTF8) : string.Empty;

                var result = validator.AnalyzePlaylist(playlistText, countryCode, 3);
                var country = countryList.GetCountry(countryCode);

                await WriteJsonAsync(context.Response, new
                {
                    country = result.Country,
                    displayName = country.DisplayName,
                    isMatch = result.IsTargetCountry,
                    matchedAliases = result.MatchedAliases,
                    recognizedChannelCount = result.RecognizedChannelCount,
                    threshold = result.Threshold,
                    totalChannels = country.Channels.Count,
                    playlistLength = playlistText.Length,
                    sample = playlistText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(10)
                });
                return;
            }

            if (requestPath.Equals("/api/country/save", StringComparison.OrdinalIgnoreCase) && context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    var country = JsonSerializer.Deserialize<CountryChannelList>(body);
                    if (country == null || string.IsNullOrWhiteSpace(country.Country))
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteTextAsync(context.Response, "Payload inválido", HttpStatusCode.BadRequest);
                        return;
                    }

                    var service = new CountryChannelListService(Path.Combine(Directory.GetCurrentDirectory(), "runtime-data", "countries"));
                    service.SaveCountry(country);
                    await WriteJsonAsync(context.Response, country);
                    return;
                }
                catch (Exception ex)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    await WriteTextAsync(context.Response, ex.Message, HttpStatusCode.InternalServerError);
                    return;
                }
            }

            if (requestPath.Equals("/api/playlist", StringComparison.OrdinalIgnoreCase))
            {
                var mainPath = Path.Combine(outputDir, "playlist.m3u");
                if (!File.Exists(mainPath))
                {
                    await WriteTextAsync(context.Response, "Playlist não encontrada", HttpStatusCode.NotFound);
                    return;
                }

                var content = await File.ReadAllTextAsync(mainPath, Encoding.UTF8);
                await WriteTextAsync(context.Response, content, HttpStatusCode.OK, "audio/x-mpegurl");
                return;
            }

            // Pré-visualização de diagnóstico: devolve a playlist com URLs sanitizadas para que
            // a interface web nunca exponha credenciais Xtream. A playlist funcional continua
            // disponível no endpoint /api/playlist.
            if (requestPath.Equals("/api/playlist/preview", StringComparison.OrdinalIgnoreCase))
            {
                var mainPath = Path.Combine(outputDir, "playlist.m3u");
                if (!File.Exists(mainPath))
                {
                    await WriteTextAsync(context.Response, "Playlist não encontrada", HttpStatusCode.NotFound);
                    return;
                }

                var content = await File.ReadAllTextAsync(mainPath, Encoding.UTF8);
                var sanitized = CredentialSanitizer.SanitizeM3uContent(content);
                await WriteTextAsync(context.Response, sanitized, HttpStatusCode.OK, "audio/x-mpegurl");
                return;
            }

            if (requestPath.Equals("/api/playlist_temp", StringComparison.OrdinalIgnoreCase))
            {
                var tempPath = Path.Combine(outputDir, "playlist_temp.m3u");
                if (!File.Exists(tempPath))
                {
                    await WriteTextAsync(context.Response, "Playlist temporária não encontrada", HttpStatusCode.NotFound);
                    return;
                }

                var content = await File.ReadAllTextAsync(tempPath, Encoding.UTF8);
                await WriteTextAsync(context.Response, content, HttpStatusCode.OK, "audio/x-mpegurl");
                return;
            }

            if (requestPath.Equals("/api/playlist_temp/preview", StringComparison.OrdinalIgnoreCase))
            {
                var tempPath = Path.Combine(outputDir, "playlist_temp.m3u");
                if (!File.Exists(tempPath))
                {
                    await WriteTextAsync(context.Response, "Playlist temporária não encontrada", HttpStatusCode.NotFound);
                    return;
                }

                var content = await File.ReadAllTextAsync(tempPath, Encoding.UTF8);
                var sanitized = CredentialSanitizer.SanitizeM3uContent(content);
                await WriteTextAsync(context.Response, sanitized, HttpStatusCode.OK, "audio/x-mpegurl");
                return;
            }

            if (requestPath.Equals("/api/run-report", StringComparison.OrdinalIgnoreCase))
            {
                var reportPath = Path.Combine(outputDir, "telegram_run_report.json");
                if (!File.Exists(reportPath))
                {
                    await WriteJsonAsync(context.Response, new { error = "Sem relatório de execução disponível." });
                    return;
                }

                var report = JsonSerializer.Deserialize<RunReport>(await File.ReadAllTextAsync(reportPath, Encoding.UTF8), JsonOptions);
                await WriteJsonAsync(context.Response, report ?? new RunReport());
                return;
            }

            if (requestPath.Equals("/api/discovered-playlists", StringComparison.OrdinalIgnoreCase))
            {
                var reportPath = Path.Combine(outputDir, "telegram_run_report.json");
                if (!File.Exists(reportPath))
                {
                    await WriteJsonAsync(context.Response, new { error = "Sem relatório de execução disponível." });
                    return;
                }

                var report = JsonSerializer.Deserialize<RunReport>(await File.ReadAllTextAsync(reportPath, Encoding.UTF8), JsonOptions);
                await WriteJsonAsync(context.Response, report?.DiscoveredPlaylists ?? new List<DiscoveredPlaylist>());
                return;
            }

            // Sumário de descoberta: dedup por (source, name) e ordenação determinística.
            if (requestPath.Equals("/api/discovery/summary", StringComparison.OrdinalIgnoreCase))
            {
                var reportPath = Path.Combine(outputDir, "telegram_run_report.json");
                if (!File.Exists(reportPath))
                {
                    await WriteJsonAsync(context.Response, new { error = "Sem relatório de execução disponível." });
                    return;
                }
                var report = JsonSerializer.Deserialize<RunReport>(await File.ReadAllTextAsync(reportPath, Encoding.UTF8), JsonOptions);
                var raw = report?.DiscoveredPlaylists ?? new List<DiscoveredPlaylist>();
                var dedup = DashboardMetrics.DeduplicateBySourceName(raw);
                await WriteJsonAsync(context.Response, new
                {
                    total = raw.Count,
                    distinct = dedup.Count,
                    duplicatesCollapsed = raw.Count - dedup.Count,
                    items = dedup,
                });
                return;
            }

            // Sumário de classificação do último MatchPlan publicado.
            // Lê o ficheiro dispatcharr_plan_<ts>.json mais recente e
            // devolve as contagens por ChannelKind + uma amostra das
            // entradas excluídas (sem URLs nem credenciais).
            if (requestPath.Equals("/api/classification-summary", StringComparison.OrdinalIgnoreCase))
            {
                var planPath = LatestDispatcharrPlanPath(outputDir);
                if (planPath == null)
                {
                    await WriteJsonAsync(context.Response, new { error = "Sem plano de classificação disponível." });
                    return;
                }

                var plan = MatchPlanSerializer.Deserialize(await File.ReadAllTextAsync(planPath, Encoding.UTF8));
                if (plan == null)
                {
                    await WriteJsonAsync(context.Response, new { error = "Plano vazio." });
                    return;
                }

                var sample = plan.ClassifiedExclusions
                    .Take(50)
                    .Select(e => new
                    {
                        title = e.Title,
                        group = e.Group,
                        kind = e.Kind.ToString(),
                        reason = e.Reason,
                    })
                    .ToList();

                await WriteJsonAsync(context.Response, new
                {
                    classification = plan.Counts.Classification,
                    excludedCount = plan.ClassifiedExclusions.Count,
                    sample = sample,
                    planGeneratedAtUtc = plan.GeneratedAtUtc,
                    planSourcePlaylistPath = plan.SourcePlaylistPath,
                });
                return;
            }

            // Run report normalizado (métricas com semântica correcta).
            if (requestPath.Equals("/api/run-report/summary", StringComparison.OrdinalIgnoreCase))
            {
                var reportPath = Path.Combine(outputDir, "telegram_run_report.json");
                if (!File.Exists(reportPath))
                {
                    await WriteJsonAsync(context.Response, new { error = "Sem relatório de execução disponível." });
                    return;
                }
                var report = JsonSerializer.Deserialize<RunReport>(await File.ReadAllTextAsync(reportPath, Encoding.UTF8), JsonOptions);
                await WriteJsonAsync(context.Response, report == null
                    ? new { error = "Relatório vazio." }
                    : DashboardMetrics.SummarizeRun(report));
                return;
            }

            // Detalhe de uma execução do histórico (1-based).
            if (requestPath.StartsWith("/api/execution/", StringComparison.OrdinalIgnoreCase))
            {
                var tail = requestPath.Substring("/api/execution/".Length);
                if (!int.TryParse(tail, out var idx) || idx < 1)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteTextAsync(context.Response, "Índice inválido", HttpStatusCode.BadRequest);
                    return;
                }
                var history = await historyService.GetRecentAsync(TimeSpan.FromDays(365));
                if (idx > history.Count)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    await WriteTextAsync(context.Response, "Execução inexistente", HttpStatusCode.NotFound);
                    return;
                }
                await WriteJsonAsync(context.Response, history[idx - 1]);
                return;
            }

            // Estado da última sincronização Dispatcharr (lê do filesystem, sem rede).
            if (requestPath.Equals("/api/dispatcharr/state", StringComparison.OrdinalIgnoreCase))
            {
                var state = DashboardMetrics.ReadLatestDispatcharrSync(outputDir);
                if (state == null)
                {
                    await WriteJsonAsync(context.Response, new
                    {
                        enabled = false,
                        reason = "Sem plan/report encontrado em output/. Sync opt-in ou nunca correu."
                    });
                    return;
                }
                await WriteJsonAsync(context.Response, state);
                return;
            }

            // Ficheiros disponíveis na pasta de output (preview/sanity).
            // Devolve um objecto por ficheiro com {present, size, exists}, para distinguir
            // "ficheiro ausente" de "ficheiro presente mas vazio".
            if (requestPath.Equals("/api/output/inventory", StringComparison.OrdinalIgnoreCase))
            {
                var inv = new Dictionary<string, object>();
                if (Directory.Exists(outputDir))
                {
                    foreach (var name in new[] { "playlist.m3u", "playlist_temp.m3u", "telegram_run_report.json", "telegram_maintain_report.json", "import_history.json" })
                    {
                        var p = Path.Combine(outputDir, name);
                        if (File.Exists(p))
                        {
                            var fi = new FileInfo(p);
                            inv[name] = new { present = true, size = fi.Length, lastWriteUtc = fi.LastWriteTimeUtc.ToString("o") };
                        }
                        else
                        {
                            inv[name] = new { present = false, size = 0, lastWriteUtc = (string?)null };
                        }
                    }
                }
                await WriteJsonAsync(context.Response, inv);
                return;
            }

            await WriteHtmlAsync(context.Response, BuildHtmlPage());
        }

        // Opções JSON partilhadas por todos os endpoints do dashboard: serializam
        // com camelCase (alinhado com o que o JavaScript inlined lê) e permitem
        // deserializar JSON camelCase (como o telegram_run_report.json escrito
        // por Program.cs com a mesma política). Sem isto o frontend recebe
        // PascalCase e renderiza "undefined" / "Invalid Date".
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Constrói o payload JSON do endpoint <c>/api/version</c>. Mantém-se
        /// como função interna pura para ser testada directamente, sem
        /// precisar do <c>HttpListener</c>.
        /// </summary>
        internal static object BuildVersionPayload()
        {
            var info = BuildInfo.Current;
            return new
            {
                application = BuildInfo.Application,
                version = info.Version,
                commit = info.Commit,
                build = info.BuildNumber,
                buildDate = info.BuildDate.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            };
        }

        private static async Task WriteJsonAsync(HttpListenerResponse response, object data)
        {
            var json = JsonSerializer.Serialize(data, JsonOptions);
            response.ContentType = "application/json; charset=utf-8";
            var buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.Close();
        }

        /// <summary>
        /// Returns the most recent <c>dispatcharr_plan_*.json</c>
        /// file under <paramref name="outputDir"/>, or <c>null</c> when
        /// no plan exists yet. Filename ordering is the same as the
        /// writer: <c>dispatcharr_plan_{yyyyMMdd_HHmmss}.json</c>;
        /// we use <see cref="File.GetLastWriteTimeUtc"/> to break
        /// filename-collision ties deterministically.
        /// </summary>
        private static string? LatestDispatcharrPlanPath(string outputDir)
        {
            if (string.IsNullOrEmpty(outputDir) || !Directory.Exists(outputDir))
            {
                return null;
            }
            try
            {
                var files = Directory
                    .EnumerateFiles(outputDir, "dispatcharr_plan_*.json", SearchOption.TopDirectoryOnly)
                    .Select(p => new { Path = p, Ticks = File.GetLastWriteTimeUtc(p).Ticks })
                    .OrderByDescending(x => x.Ticks)
                    .ThenByDescending(x => x.Path, StringComparer.Ordinal)
                    .Select(x => x.Path)
                    .ToList();
                return files.Count == 0 ? null : files[0];
            }
            catch
            {
                return null;
            }
        }

        private static async Task WriteTextAsync(HttpListenerResponse response, string text, HttpStatusCode statusCode = HttpStatusCode.OK, string contentType = "text/plain; charset=utf-8")
        {
            response.StatusCode = (int)statusCode;
            response.ContentType = contentType;
            var buffer = Encoding.UTF8.GetBytes(text);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.Close();
        }

        private static async Task WriteHtmlAsync(HttpListenerResponse response, string html)
        {
            response.ContentType = "text/html; charset=utf-8";
            var buffer = Encoding.UTF8.GetBytes(html);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.Close();
        }

        private static string BuildHtmlPage()
        {
            return BuildDashboardHtml();
        }

        private static string BuildDashboardHtml()
        {
            string s = """
<!doctype html>
<html lang='pt'>
<head>
  <meta charset='utf-8'>
  <meta name='viewport' content='width=device-width,initial-scale=1'>
  <title>m3uCrawler Dashboard</title>
  <style>
    :root {
      --bg: #0d1117;
      --panel: #161b22;
      --panel-2: #1c232c;
      --border: #30363d;
      --text: #e6edf3;
      --muted: #8b949e;
      --accent: #2f81f7;
      --accent-2: #218bff;
      --ok: #3fb950;
      --warn: #d29922;
      --err: #f85149;
      --info: #79c0ff;
    }
    * { box-sizing: border-box; }
    html, body { margin: 0; padding: 0; background: var(--bg); color: var(--text); font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Arial, sans-serif; font-size: 14px; line-height: 1.5; }
    a { color: var(--accent-2); text-decoration: none; }
    a:hover { text-decoration: underline; }
    header { background: var(--panel); border-bottom: 1px solid var(--border); padding: 12px 20px; display: flex; align-items: center; gap: 16px; }
    header h1 { font-size: 16px; margin: 0; font-weight: 600; }
    header .meta { color: var(--muted); font-size: 12px; }
    nav { background: var(--panel); border-bottom: 1px solid var(--border); padding: 0 12px; display: flex; gap: 4px; flex-wrap: wrap; }
    nav button { background: transparent; color: var(--muted); border: none; padding: 12px 16px; cursor: pointer; font: inherit; border-bottom: 2px solid transparent; }
    nav button:hover { color: var(--text); }
    nav button.active { color: var(--text); border-bottom-color: var(--accent); }
    main { padding: 20px; max-width: 1400px; margin: 0 auto; }
    section[hidden] { display: none; }
    .grid { display: grid; gap: 16px; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); margin-bottom: 20px; }
    .card { background: var(--panel); border: 1px solid var(--border); border-radius: 8px; padding: 16px; }
    .card h3 { margin: 0 0 8px 0; font-size: 13px; color: var(--muted); font-weight: 500; text-transform: uppercase; letter-spacing: 0.05em; }
    .card .value { font-size: 28px; font-weight: 600; }
    .card .sub { color: var(--muted); font-size: 12px; margin-top: 6px; }
    .card .help { color: var(--muted); font-size: 11px; margin-top: 8px; border-top: 1px solid var(--border); padding-top: 8px; }
    table { border-collapse: collapse; width: 100%; background: var(--panel); border: 1px solid var(--border); border-radius: 8px; overflow: hidden; }
    th, td { padding: 10px 12px; text-align: left; border-bottom: 1px solid var(--border); }
    th { background: var(--panel-2); font-weight: 500; font-size: 12px; color: var(--muted); text-transform: uppercase; letter-spacing: 0.04em; }
    tr:last-child td { border-bottom: none; }
    tr:hover td { background: rgba(56, 139, 253, 0.06); }
    .badge { display: inline-block; padding: 2px 8px; border-radius: 999px; font-size: 11px; }
    .badge.ok { background: rgba(63, 185, 80, 0.18); color: var(--ok); }
    .badge.warn { background: rgba(210, 153, 34, 0.18); color: var(--warn); }
    .badge.err { background: rgba(248, 81, 73, 0.18); color: var(--err); }
    .badge.info { background: rgba(121, 192, 255, 0.18); color: var(--info); }
    .badge.muted { background: rgba(139, 148, 158, 0.18); color: var(--muted); }
    .toolbar { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; margin-bottom: 12px; }
    .toolbar select, .toolbar input { background: var(--panel); color: var(--text); border: 1px solid var(--border); padding: 6px 10px; border-radius: 6px; font: inherit; }
    .toolbar button { background: var(--accent); color: white; border: none; padding: 6px 12px; border-radius: 6px; cursor: pointer; font: inherit; }
    .toolbar button.secondary { background: var(--panel-2); color: var(--text); border: 1px solid var(--border); }
    textarea { width: 100%; background: var(--panel); color: var(--text); border: 1px solid var(--border); border-radius: 6px; padding: 10px; font: inherit; }
    .muted { color: var(--muted); }
    .bar { background: var(--panel-2); border-radius: 4px; overflow: hidden; height: 8px; display: flex; border: 1px solid var(--border); }
    .bar > div { height: 100%; }
    .bar .ok { background: var(--ok); }
    .bar .err { background: var(--err); }
    pre { background: var(--panel-2); padding: 14px; overflow: auto; border-radius: 6px; border: 1px solid var(--border); font-size: 12px; }
    .row-counts { color: var(--muted); font-size: 12px; margin-top: 8px; }
    details { background: var(--panel); border: 1px solid var(--border); border-radius: 6px; padding: 8px 12px; margin-top: 8px; }
    summary { cursor: pointer; font-weight: 500; }
  </style>
</head>
<body>
  <header>
    <h1>m3uCrawler Dashboard</h1>
    <span class='meta' id='metaLine'>a carregar…</span>
  </header>

  <nav id='nav'>
    <button data-view='overview' class='active'>Overview</button>
    <button data-view='executions'>Execuções</button>
    <button data-view='discovery'>Descoberta</button>
    <button data-view='countries'>Canais / Países</button>
    <button data-view='playlist'>Playlist</button>
    <button data-view='dispatcharr'>Dispatcharr</button>
    <button data-view='diagnostics'>Diagnóstico</button>
  </nav>

  <main>
    <!-- OVERVIEW -->
    <section id='view-overview'>
      <h2 style='font-size:18px;margin-top:0;'>Resumo do sistema</h2>
      <div class='grid' id='overviewCards'></div>
      <h3 style='font-size:14px;margin-top:24px;'>Relações da última execução</h3>
      <div class='card'>
        <div id='runMath'></div>
      </div>
    </section>

    <!-- EXECUÇÕES -->
    <section id='view-executions' hidden>
      <h2 style='font-size:18px;margin-top:0;'>Histórico de execuções (últimas 72h)</h2>
      <div class='toolbar'>
        <span class='muted' id='historyCount'></span>
        <button class='secondary' onclick='loadHistory()'>Recarregar</button>
      </div>
      <div id='historyTable'></div>
      <div id='historyDetail'></div>
    </section>

    <!-- DESCOBERTA -->
    <section id='view-discovery' hidden>
      <h2 style='font-size:18px;margin-top:0;'>Descoberta</h2>
      <div class='toolbar'>
        <label class='muted'>Filtro estado:</label>
        <select id='discState'>
          <option value=''>todas</option>
          <option value='accepted'>aceites</option>
          <option value='rejected'>rejeitadas</option>
        </select>
        <label class='muted'>Origem:</label>
        <select id='discSource'><option value=''>todas</option></select>
        <label class='muted'>País:</label>
        <select id='discCountry'><option value=''>todos</option></select>
        <span class='muted' id='discMeta'></span>
      </div>
      <div id='discoveryTable'></div>
      <details>
        <summary>Ver entradas brutas (sem agregação)</summary>
        <div id='discoveryRaw' style='margin-top:8px;'></div>
      </details>
    </section>

    <!-- CANAIS / PAÍSES -->
    <section id='view-countries' hidden>
      <h2 style='font-size:18px;margin-top:0;'>Canais / Países</h2>
      <h3 style='font-size:14px;'>Validação da playlist atual</h3>
      <div class='toolbar'>
        <label class='muted'>País:</label>
        <select id='countrySelect'></select>
        <button class='secondary' onclick='loadCountryValidation()'>Re-validar</button>
      </div>
      <div id='countryValidationResult'></div>
      <h3 style='font-size:14px;margin-top:24px;'>Listas de canais por país (editáveis)</h3>
      <div id='countrySection'></div>
    </section>

    <!-- PLAYLIST -->
    <section id='view-playlist' hidden>
      <h2 style='font-size:18px;margin-top:0;'>Playlist atual</h2>
      <div class='toolbar'>
        <a href='/api/playlist' target='_blank'>playlist.m3u (funcional)</a>
        <a href='/api/playlist_temp' target='_blank'>playlist_temp.m3u</a>
        <span class='muted' id='playlistInventory'></span>
      </div>
      <div class='card' style='margin-bottom:12px;'>
        <div class='muted' id='playlistMath'></div>
      </div>
      <h3 style='font-size:14px;'>Pré-visualização (URLs sanitizadas — sem credenciais Xtream)</h3>
      <pre id='playlistPreview'>a carregar…</pre>
    </section>

    <!-- DISPATCHARR -->
    <section id='view-dispatcharr' hidden>
      <h2 style='font-size:18px;margin-top:0;'>Dispatcharr</h2>
      <div id='dispatcharrOverview'></div>
      <h3 style='font-size:14px;margin-top:24px;'>Detalhes da última sincronização</h3>
      <div id='dispatcharrDetail'></div>
    </section>

    <!-- DIAGNÓSTICO -->
    <section id='view-diagnostics' hidden>
      <h2 style='font-size:18px;margin-top:0;'>Diagnóstico</h2>
      <div id='diagLastRun'></div>
      <h3 style='font-size:14px;margin-top:24px;'>Inventário de output/</h3>
      <div id='diagInventory'></div>
      <h3 style='font-size:14px;margin-top:24px;'>End-to-end (raw RunReport)</h3>
      <details><summary>Ver RunReport completo</summary><pre id='diagRawRunReport'>a carregar…</pre></details>
      <h3 style='font-size:14px;margin-top:24px;'>Glossário de métricas</h3>
      <div id='diagGlossary'></div>
    </section>
  </main>

  <script>
  (function(){
    const fmt = (v) => (v === undefined || v === null) ? '—' : v;
    const nfmt = (n) => new Intl.NumberFormat('pt-PT').format(n);
    const pct = (n) => new Intl.NumberFormat('pt-PT', { maximumFractionDigits: 1, minimumFractionDigits: 1 }).format(n) + '%';
    const tsLocal = (s) => s ? new Date(s).toLocaleString() : '—';

    function metricCard(title, value, sub, help) {
      return `<div class='card'><h3>${title}</h3><div class='value'>${value}</div>${sub ? `<div class='sub'>${sub}</div>` : ''}${help ? `<div class='help'>${help}</div>` : ''}</div>`;
    }

    function helpFor(key) {
      const map = {
        candidates: 'URLs/attachments identificados como potenciais M3U antes do download.',
        playlists: 'Playlists distintas descarregadas com sucesso (passaram o gate #EXTM3U).',
        streams: 'Entradas EXTINF extraídas, antes do filtro por país.',
        streamsAfter: 'Streams que sobraram depois do filtro por país (candidatos a teste).',
        tested: 'Streams aos quais foi feito um teste HTTP.',
        working: 'Streams que responderam OK.',
        failed: 'Streams cujo teste falhou.',
        durationMs: 'Duração total da execução em ms.'
      };
      return map[key] || '';
    }

    async function safeFetchJson(url, fallback) {
      try { const r = await fetch(url); if (!r.ok) return fallback || { error: `HTTP ${r.status}` }; return await r.json(); }
      catch (e) { return fallback || { error: e.message }; }
    }

    async function loadOverview() {
      const [run, hist, dispatcharr, inv] = await Promise.all([
        safeFetchJson('/api/run-report/summary', null),
        safeFetchJson('/api/history', []),
        safeFetchJson('/api/dispatcharr/state', null),
        safeFetchJson('/api/output/inventory', {})
      ]);

      // Cabeçalho
      const lastRunTs = (run && run.startedAtUtc) ? tsLocal(run.startedAtUtc) : '—';
      document.getElementById('metaLine').textContent = `Última execução: ${lastRunTs}`;

      const last = (hist && hist.length) ? hist[0] : null;
      const finalCount = last ? last.finalPlaylistCount : '—';
      const histoHours = last ? (last.historyHours + 'h') : '—';

      const cards = [];
      cards.push(metricCard('Última execução', lastRunTs, run ? ('estado: ' + (run.status || '—')) : 'sem run report', ''));
      cards.push(metricCard('Duração', run ? (run.durationMs != null ? (nfmt(run.durationMs) + ' ms') : '—') : '—', run && run.durationMs ? ((run.durationMs/1000).toFixed(1) + ' s') : '', helpFor('durationMs')));
      cards.push(metricCard('playlist.m3u', finalCount + ' streams', last ? ('janela ' + histoHours) : '', 'Streams actualmente publicados.'));
      cards.push(metricCard('playlist.m3u (bytes)', inv['playlist.m3u'] ? nfmt(inv['playlist.m3u']) + ' B' : '—', '', 'Tamanho do ficheiro actual.'));
      if (run) {
        const w = run.streamsWorking, f = run.streamsFailed, t = run.streamsTested;
        const rate = (w + f) > 0 ? (100 * w / (w + f)) : null;
        const sub = rate != null ? (pct(rate) + ' de sucesso · ' + nfmt(w) + ' OK / ' + nfmt(f) + ' KO') : '—';
        cards.push(metricCard('Última: testados / funcionais', nfmt(t) + ' · ' + nfmt(w), sub, helpFor('working')));
        cards.push(metricCard('Última: candidatos', nfmt(run.candidates || 0), 'playlists: ' + nfmt(run.playlistsDownloaded || 0), helpFor('candidates')));
      } else {
        cards.push(metricCard('Última execução (resumo)', '—', 'sem run report disponível', ''));
      }
      cards.push(metricCard('Última sync Dispatcharr', dispatcharr && dispatcharr.startedAtUtc ? tsLocal(dispatcharr.startedAtUtc) : '—',
        dispatcharr && dispatcharr.dispatchedDetailDisabled ? '' :
          (dispatcharr && dispatcharr.dispatcharrVersion ? ('versão ' + dispatcharr.dispatcharrVersion) : (dispatcharr ? (dispatcharr.reason || '—') : '')),
        'Sincronização opt-in (dispatcharr_enabled=true em wtelegram.config).'));
      document.getElementById('overviewCards').innerHTML = cards.join('');

      // Relações matemáticas
      let math = '<p class="muted">Sem dados do último run.</p>';
      if (run) {
        const w = run.streamsWorking || 0, f = run.streamsFailed || 0, t = run.streamsTested || 0;
        const tested = w + f;
        const balanced = tested === t;
        const rate = tested > 0 ? (100 * w / tested) : null;
        const failRate = tested > 0 ? (100 * f / tested) : null;
        const candidates = run.candidates || 0;
        const playlistsDl = run.playlistsDownloaded || 0;
        const playlistsRej = run.playlistsRejected || 0;
        const streams = run.streamsExtracted || 0;
        const streamsAfter = run.streamsAfterCountryFilter || 0;
        const rejectedByCountry = run.streamsRejectedByCountry || 0;
        const messages = run.messages || 0;

        // NOTA: a seta "→" representa uma CONTAGEM ao longo do pipeline, não igualdade.
        //   Messages -> Candidates           (sub-conjunto,scan)
        //   Candidates = PlaylistsDownloaded + PlaylistsInvalid
        //                                       (PlaylistsInvalid = #EXTM3U gate falhado)
        //   PlaylistsDownloaded = CountryMatches + PlaylistsRejected
        //                                       (PlaylistsRejected mistura
        //                                        fast-reject E 0-streams-após-filtro)
        //   StreamsExtracted conta APENAS streams de playlists COM país-alvo aceite.
        //   Por isso StreamsExtracted << PlaylistsDownloaded quando há playlists rejeitadas.
        math = `
          <p><strong>Cadeia (cada → não é igualdade):</strong></p>
          <p>${nfmt(messages)} mensagens Telegram → ${nfmt(candidates)} candidatos → ${nfmt(playlistsDl)} playlists OK (+ ${nfmt(playlistsRej)} rejeitadas) → ${nfmt(streams)} streams (em playlists OK) → ${nfmt(streamsAfter)} após filtro por país → ${nfmt(t)} testados</p>
          <h4 style='margin:12px 0 4px;'>Testes</h4>
          ${t > 0 ? `
            <p>${nfmt(t)} streams testados = ${nfmt(w)} funcionais + ${nfmt(f)} falhados
              ${balanced ? '' : `<span class='badge warn' title='Funcionais+Falhados ≠ Testados (streams pulados)'>⚠ ${nfmt(t - tested)} não testados / pulados</span>`}
            </p>
            <div class='bar' title='${pct(rate ?? 0)} de sucesso'>
              <div class='ok' style='width:${rate}%;'></div>
              <div class='err' style='width:${failRate}%;'></div>
            </div>
            <p class='row-counts'>taxa de sucesso: ${rate != null ? pct(rate) : '—'} · taxa de falha: ${failRate != null ? pct(failRate) : '—'}</p>
          ` : '<p class="muted">Sem streams testados neste run.</p>'}
          <h4 style='margin:16px 0 4px;'>Filtro por país (per-stream)</h4>
          <p>${nfmt(streamsAfter)} streams seleccionados · ${nfmt(rejectedByCountry)} rejeitados por país</p>
          <p class='row-counts'>Sumido entre passos: ${nfmt(playlistsRej)} playlists não passaram o gate (incl. fast-reject) não contribuem para ${nfmt(streams)}.</p>
        `;
      }
      document.getElementById('runMath').innerHTML = math;
    }

    let historyCache = [];
    async function loadHistory() {
      const items = await safeFetchJson('/api/history', []);
      historyCache = Array.isArray(items) ? items : [];
      document.getElementById('historyCount').textContent = historyCache.length + ' execuções listadas.';
      if (!historyCache.length) { document.getElementById('historyTable').innerHTML = '<p class="muted">Nenhum histórico encontrado.</p>'; return; }
      const rows = historyCache.map((e, idx) => {
        const ts = new Date(e.timestamp).toLocaleString();
        const modeBadge = `<span class='badge ${e.mode === 'TelegramMaintenance' ? 'info' : 'muted'}'>${e.mode || ''}</span>`;
        const ratio = e.existingRetestedCount > 0 ? (e.existingStillWorkingCount + '/' + e.existingRetestedCount) : '—';
        return `<tr data-idx='${idx}' style='cursor:pointer'>
          <td>${ts}</td>
          <td>${modeBadge}</td>
          <td>${e.searchTerm || '—'}</td>
          <td>${e.historyHours}h</td>
          <td>${e.maxStreams || '—'}</td>
          <td>${e.newFunctionalCount}</td>
          <td>${ratio}</td>
          <td>${e.finalPlaylistCount}</td>
        </tr>`;
      }).join('');
      document.getElementById('historyTable').innerHTML =
        '<table><thead><tr><th>Quando</th><th>Modo</th><th>Pesquisa</th><th>História</th><th>Máx</th><th>Novos</th><th>Retestados</th><th>Total final</th></tr></thead><tbody>' + rows + '</tbody></table>';
      document.querySelectorAll('#historyTable tr[data-idx]').forEach(tr => tr.addEventListener('click', () => showHistoryDetail(parseInt(tr.getAttribute('data-idx'), 10))));
    }

    async function showHistoryDetail(idx) {
      if (idx < 0 || idx >= historyCache.length) return;
      const entry = historyCache[idx];
      const detail = await safeFetchJson(`/api/execution/${idx + 1}`, null);
      const e = detail || entry;
      const html = `
        <div class='card' style='margin-top:12px;'>
          <h3>Execução #${idx + 1} — ${new Date(e.timestamp).toLocaleString()}</h3>
          <table>
            <tr><th>Modo</th><td>${fmt(e.mode)}</td><th>Pesquisa</th><td>${fmt(e.searchTerm)}</td></tr>
            <tr><th>Janela</th><td>${fmt(e.historyHours)} h</td><th>Máx streams</th><td>${fmt(e.maxStreams)}</td></tr>
            <tr><th>Mensagens analisadas</th><td>${nfmt(e.messagesAnalyzed || 0)}</td><th>Candidatos</th><td>${nfmt(e.candidatesFound || 0)}</td></tr>
            <tr><th>Playlists</th><td>${nfmt(e.playlistsDownloaded || 0)}</td><th>Rejeitadas</th><td>${nfmt(e.playlistsRejected || 0)}</td></tr>
            <tr><th>Country matches</th><td>${nfmt(e.countryMatches || 0)}</td><th>Streams extraídos</th><td>${nfmt(e.streamsExtracted || 0)}</td></tr>
            <tr><th>Após filtro país</th><td>${nfmt(e.streamsAfterCountryFilter || 0)}</td><th>Rejeitados país</th><td>${nfmt(e.streamsRejectedByCountry || 0)}</td></tr>
            <tr><th>Testados</th><td>${nfmt(e.streamsTested || 0)}</td><th>Funcionais</th><td>${nfmt(e.streamsWorking || 0)}</td></tr>
            <tr><th>Falhados</th><td>${nfmt(e.streamsFailed || 0)}</td><th>Total final</th><td>${nfmt(e.finalPlaylistCount || 0)}</td></tr>
          </table>
          <p class='row-counts'>Detalhes crus (apenas leitura) preservados para auditoria.</p>
        </div>`;
      document.getElementById('historyDetail').innerHTML = html;
    }

    let discoveryCache = { total: 0, distinct: 0, items: [] };
    async function loadDiscovery() {
      const sum = await safeFetchJson('/api/discovery/summary', { items: [] });
      discoveryCache = sum || { items: [] };
      const items = discoveryCache.items || [];
      document.getElementById('discMeta').textContent =
        `${items.length} entradas distintas (de ${discoveryCache.total || items.length} brutas, ${discoveryCache.duplicatesCollapsed || 0} duplicações agregadas)`;

      const sources = [...new Set(items.map(i => i.source).filter(Boolean))].sort();
      const countries = [...new Set(items.map(i => i.countryDetected).filter(Boolean))].sort();
      const srcSel = document.getElementById('discSource');
      const coSel = document.getElementById('discCountry');
      srcSel.innerHTML = '<option value="">todas</option>' + sources.map(s => `<option value="${s.replace(/"/g,'&quot;')}">${s}</option>`).join('');
      coSel.innerHTML = '<option value="">todos</option>' + countries.map(c => `<option value="${c.replace(/"/g,'&quot;')}">${c}</option>`).join('');

      renderDiscovery();
      const raw = await safeFetchJson('/api/discovered-playlists', []);
      if (Array.isArray(raw)) {
        document.getElementById('discoveryRaw').innerHTML = '<pre>' + JSON.stringify(raw, null, 2).slice(0, 20000) + '</pre>';
      }
    }

    function renderDiscovery() {
      const state = document.getElementById('discState').value;
      const source = document.getElementById('discSource').value;
      const country = document.getElementById('discCountry').value;
      const items = (discoveryCache.items || []).filter(i =>
        (!state || i.state === state) &&
        (!source || i.source === source) &&
        (!country || i.countryDetected === country)
      );
      const rows = items.map((p, idx) => {
        const stateBadge = p.state === 'accepted'
          ? '<span class="badge ok">aceite</span>'
          : '<span class="badge err">rejeitada</span>';
        return `<tr>
          <td>${p.source || '—'}</td>
          <td>${p.name || '—'}</td>
          <td>${p.countryDetected || '—'}</td>
          <td>${nfmt(p.channelsRecognized || 0)}</td>
          <td>${nfmt(p.streamCount || 0)}</td>
          <td>${nfmt(p.streamsAfterCountryFilter || 0)}</td>
          <td>${nfmt(p.workingStreams || 0)}</td>
          <td>${stateBadge}</td>
          <td>${p.occurrences > 1 ? '<span class="badge warn" title="Mesma (origem, nome) em múltiplas linhas do RunReport">×' + p.occurrences + '</span>' : '—'}</td>
        </tr>`;
      }).join('');
      document.getElementById('discoveryTable').innerHTML = items.length
        ? `<table><thead><tr><th>Origem</th><th>Nome</th><th>País</th><th>Canais</th><th>Streams</th><th>Após país</th><th>Funcionais</th><th>Estado</th><th>Notas</th></tr></thead><tbody>${rows}</tbody></table>`
        : '<p class="muted">Nenhuma playlist encontrada para os filtros escolhidos.</p>';
    }

    let countryOptions = [];
    async function loadCountries() {
      const res = await safeFetchJson('/api/countries', []);
      countryOptions = res || [];
      const countrySelect = document.getElementById('countrySelect');
      countrySelect.innerHTML = countryOptions.map(c => `<option value='${c.country}'>${c.displayName || c.country}</option>`).join('');
      countrySelect.value = countryOptions[0]?.country || 'pt';
      if (!countryOptions.length) {
        document.getElementById('countrySection').innerHTML = '<p class="muted">Nenhuma lista de canais encontrada.</p>';
        document.getElementById('countryValidationResult').innerHTML = '<p class="muted">Sem país disponível para validação.</p>';
        return;
      }
      const countryCards = countryOptions.map(c => `
        <div class='card' style='margin-bottom:12px;'>
          <h3>${c.displayName || c.country}</h3>
          <textarea id='country-${c.country}' rows='6'>${(c.channels || []).join('\n')}</textarea>
          <div style='margin-top:8px;'><button class='secondary' data-country='${c.country}'>Guardar</button></div>
        </div>`).join('');
      document.getElementById('countrySection').innerHTML = countryCards;
      document.querySelectorAll('button[data-country]').forEach(btn => {
        btn.addEventListener('click', async () => {
          const code = btn.getAttribute('data-country');
          const t = document.getElementById(`country-${code}`);
          const channels = t.value.split(/\r?\n/).map(x => x.trim()).filter(Boolean);
          const r = await fetch('/api/country/save', { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify({ country: code, displayName: code.toUpperCase(), channels }) });
          if (r.ok) { alert('Lista guardada.'); await loadCountries(); } else { alert('Erro: ' + (await r.text())); }
        });
      });
      await loadCountryValidation();
    }

    async function loadCountryValidation() {
      const code = document.getElementById('countrySelect').value;
      const res = await safeFetchJson(`/api/country/validate?country=${encodeURIComponent(code)}`, null);
      const target = document.getElementById('countryValidationResult');
      if (!res || res.error) { target.innerHTML = '<p class="muted">Sem dados de validação.</p>'; return; }
      const detail = res.isMatch
        ? `<p><span class='badge ok'>correspondência</span> em ${res.displayName || res.country}.</p><p>Aliases detectados: ${(res.matchedAliases || []).join(', ') || '—'}</p>`
        : `<p><span class='badge warn'>sem correspondência</span> em ${res.displayName || res.country}.</p><p>Aliases esperados: ${(res.matchedAliases || []).join(', ') || '—'}</p>`;
      // As três grandezas NÃO representam a mesma colecção de coisas:
      //   recognized  = nº de canais canónicos distintos reconhecidos NA PLAYLIST actual
      //   threshold   = mínimo para o país ser considerado alvo (gate do AnalyzePlaylist)
      //   totalChannels = nº de aliases configurados em runtime-data/countries/<code>.json
      // Por isso não calculamos "cobertura" como recognized/total — esse rácio mistura
      // colecções diferentes. Cada número é apresentado em separado.
      target.innerHTML = `
        <div class='card'>
          <h3>Resultado da validação — ${res.displayName || res.country}</h3>
          ${detail}
          <p><strong>Reconhecidos na playlist:</strong> ${nfmt(res.recognizedChannelCount || 0)} canais canónicos distintos</p>
          <p><strong>Threshold (gate do país-alvo):</strong> ${res.threshold || 3}</p>
          <p><strong>Aliases configurados:</strong> ${nfmt(res.totalChannels || 0)} (em <code>runtime-data/countries/${code}.json</code>)</p>
          <p class='row-counts'>tamanho playlist: ${nfmt(res.playlistLength || 0)} bytes</p>
        </div>`;
    }

    async function loadPlaylist() {
      try {
        const inv = await safeFetchJson('/api/output/inventory', {});
const rows = Object.entries(inv).map(([k, v]) => {
          if (!v || !v.present) return `<tr><td>${k}</td><td><span class="badge muted">ausente</span></td></tr>`;
          return `<tr><td>${k}</td><td>${nfmt(v.size)} B <span class="row-counts">(${new Date(v.lastWriteUtc).toLocaleString()})</span></td></tr>`;
        }).join('');
        document.getElementById('playlistInventory').innerHTML = '· ' + Object.entries(inv).map(([k, v]) => v && v.present ? `${k}=${nfmt(v.size)}B` : `${k}=ausente`).join(' · ');
        document.getElementById('diagInventory').innerHTML = `<table><thead><tr><th>Ficheiro</th><th>Tamanho</th></tr></thead><tbody>${rows}</tbody></table>`;
      } catch (e) {}
      try {
        const r = await fetch('/api/playlist/preview');
        const txt = r.ok ? (await r.text()) : 'Playlist não disponível.';
        const lines = txt.split('\n').filter(Boolean).slice(0, 80);
        const streamCount = (txt.match(/^#EXTINF/gm) || []).length;
        document.getElementById('playlistMath').innerHTML = `
          <p><strong>${nfmt(streamCount)}</strong> entradas #EXTINF na pré-visualização.
          URLs foram sanitizadas neste endpoint — credenciais Xtream nunca aparecem.</p>`;
        document.getElementById('playlistPreview').textContent = lines.join('\n');
      } catch (e) {
        document.getElementById('playlistPreview').textContent = 'Erro ao carregar playlist.';
      }
    }

    async function loadDispatcharr() {
      const s = await safeFetchJson('/api/dispatcharr/state', null);
      const target = document.getElementById('dispatcharrOverview');
      const detail = document.getElementById('dispatcharrDetail');
      if (!s || s.reason) {
        target.innerHTML = `
          <div class='card'>
            <h3>Sincronização Dispatcharr</h3>
            <p><span class='badge warn'>não activa</span></p>
            <p>${s && s.reason ? s.reason : 'Sem dados.'}</p>
            <p class='row-counts'>A integração é opt-in (wtelegram.config: <code>dispatcharr_enabled=true</code>).
            Nenhuma chave ainda foi escrita, ou o sync opt-in nunca correu.</p>
          </div>`;
        detail.innerHTML = '';
        return;
      }
      const cards = [];
      cards.push(metricCard('Última execução', tsLocal(s.startedAtUtc), s.dryRun ? '<span class="badge info">dry-run</span>' : '<span class="badge warn">apply</span>', ''));
      cards.push(metricCard('Versão Dispatcharr', s.dispatcharrVersion || '—', '', 'Obtida de GET /api/core/version/ no início da run (best-effort).'));
      cards.push(metricCard('Total de canais no plano', nfmt(s.totalChannels || 0), 'matched + new + unchanged + ...', ''));
      cards.push(metricCard('Matched (canais)', nfmt(s.matched || 0), 'canal existente reutilizado', ''));
      cards.push(metricCard('New channels', nfmt(s.newChannels || 0), 'canais novos a criar', ''));
      cards.push(metricCard('New streams', nfmt(s.newStreams || 0), 'streams novos a anexar', ''));
      cards.push(metricCard('Removed streams', nfmt(s.removedStreams || 0), 'a desassociar e apagar', ''));
      cards.push(metricCard('Ambiguous', nfmt(s.ambiguous || 0), 'nunca aplicados automaticamente', ''));
      cards.push(metricCard('Skipped', nfmt(s.skipped || 0), 'streams não-workings na playlist', ''));
      cards.push(metricCard('Failed', nfmt(s.failed || 0), '', ''));
      target.innerHTML = `<div class='grid'>${cards.join('')}</div>`;

      // Sinaliza se plan e report pertencem à mesma execução (por timestamp).
      const pairingNote = s.planReportPaired
        ? 'plan + report emparelhados (mesma execução)'
        : '<span class="badge warn">plan e report NÃO foram emparelhados pelo timestamp</span>';

      detail.innerHTML = `
        <div class='card'>
          <h3>Ficheiros produzidos</h3>
          ${s.latestPlanPath ? `<p><strong>Plano:</strong> <code>${s.latestPlanPath}</code></p>` : '<p><em>Sem plano (sync opt-in nunca correu ou foi interrompido).</em></p>'}
          ${s.latestReportPath ? `<p><strong>Relatório:</strong> <code>${s.latestReportPath}</code></p>` : '<p><em>Sem relatório.</em></p>'}
          <p>${pairingNote}</p>
          ${(s.planValid === false || s.reportValid === false) ? `<p class='row-counts'><span class='badge err'>ficheiro(s) com JSON inválido</span>: ${s.error || ''}</p>` : ''}
          <p class='row-counts'>Os ficheiros ficam em <code>output/</code> (bind mount <code>/opt/playlists</code>).
          Ver <code>dispatcharr_plan_*.json</code> e <code>dispatcharr_report_*.json</code> mais recentes.
          Estes ficheiros nunca contêm credenciais em claro (sanitização automática).</p>
        </div>`;
    }

    async function loadDiagnostics() {
      const r = await safeFetchJson('/api/run-report/summary', null);
      const target = document.getElementById('diagLastRun');
      if (!r || r.error) { target.innerHTML = '<p class="muted">Sem run report.</p>'; }
      else {
        target.innerHTML = `
          <div class='card'>
            <h3>Última execução (resumo)</h3>
            <table>
              <tr><th>Início</th><td>${tsLocal(r.startedAtUtc)}</td><th>Fim</th><td>${tsLocal(r.finishedAtUtc)}</td></tr>
              <tr><th>Estado</th><td><span class='badge ${r.status === "ok" ? "ok" : (r.status === "sem-streams" ? "warn" : "err")}'>${r.status}</span></td><th>Duração</th><td>${nfmt(r.durationMs || 0)} ms</td></tr>
              <tr><th>Mensagens</th><td>${nfmt(r.messages || 0)}</td><th>Candidatos</th><td>${nfmt(r.candidates || 0)}</td></tr>
              <tr><th>Playlists OK</th><td>${nfmt(r.playlistsDownloaded || 0)}</td><th>Playlists inválidas</th><td>${nfmt(r.playlistsInvalid || 0)}</td></tr>
              <tr><th>Rejeitadas por país</th><td>${nfmt(r.playlistsRejected || 0)}</td><th>Country matches</th><td>${nfmt(r.countryMatches || 0)}</td></tr>
              <tr><th>Streams extraídos</th><td>${nfmt(r.streamsExtracted || 0)}</td><th>Após país</th><td>${nfmt(r.streamsAfterCountryFilter || 0)}</td></tr>
              <tr><th>Rejeitados país</th><td>${nfmt(r.streamsRejectedByCountry || 0)}</td><th>Testados</th><td>${nfmt(r.streamsTested || 0)}</td></tr>
              <tr><th>Funcionais</th><td>${nfmt(r.streamsWorking || 0)}</td><th>Falhados</th><td>${nfmt(r.streamsFailed || 0)}</td></tr>
              <tr><th>Taxa de sucesso</th><td>${r.successRatePercent != null ? pct(r.successRatePercent) : '—'}</td><th>Testados balanceados</th><td>${r.testsBalanced ? '<span class="badge ok">sim</span>' : '<span class="badge warn">não (W+F≠T)</span>'}</td></tr>
            </table>
          </div>`;
        try {
          const raw = await fetch('/api/run-report');
          if (raw.ok) document.getElementById('diagRawRunReport').textContent = JSON.stringify(await raw.json(), null, 2);
        } catch(e) {}
      }
      const inv = await safeFetchJson('/api/output/inventory', {});
      const rows = Object.keys(inv).map(k => `<tr><td>${k}</td><td>${typeof inv[k] === 'number' ? (nfmt(inv[k]) + ' B') : (inv[k] === false ? 'ausente' : inv[k])}</td></tr>`).join('');
      document.getElementById('diagInventory').innerHTML = `<table><thead><tr><th>Ficheiro</th><th>Tamanho</th></tr></thead><tbody>${rows}</tbody></table>`;

      // Glossário
      const gloss = Object.entries({
        candidates: 'URLs/attachments identificados como potenciais M3U antes do download.',
        playlistsDownloaded: 'Playlists distintas descarregadas com sucesso (passaram o gate #EXTM3U).',
        playlistsInvalid: 'Descartadas por não serem playlists M3U válidas.',
        playlistsRejected: 'Rejeitadas pelo filtro por país (fast-reject).',
        streamsExtracted: 'Entradas EXTINF extraídas, antes do filtro por país.',
        streamsAfterCountryFilter: 'Streams sobreviventes ao filtro por país (candidatos a teste).',
        streamsRejectedByCountry: 'Streams removidos pelo filtro por país.',
        streamsTested: 'Streams testados via HTTP.',
        streamsWorking: 'Streams que responderam OK no teste.',
        streamsFailed: 'Streams cujo teste falhou.',
        successRatePercent: 'streamsWorking / (streamsWorking + streamsFailed) × 100.',
        testsBalanced: 'Verdadeiro se working + failed == tested; usado para detectar streams pulados.',
        coverage: 'recognizedChannelCount / totalChannels × 100 — só calculado se houver base comparável.',
      });
      document.getElementById('diagGlossary').innerHTML = `<table><thead><tr><th>Métrica</th><th>Definição</th></tr></thead><tbody>${gloss.map(([k,v]) => `<tr><td><code>${k}</code></td><td>${v}</td></tr>`).join('')}</tbody></table>`;
    }

    function showView(name) {
      document.querySelectorAll('main > section').forEach(s => s.hidden = true);
      document.getElementById('view-' + name).hidden = false;
      document.querySelectorAll('nav button').forEach(b => b.classList.toggle('active', b.dataset.view === name));
      switch (name) {
        case 'overview': loadOverview(); break;
        case 'executions': loadHistory(); break;
        case 'discovery': loadDiscovery(); break;
        case 'countries': loadCountries(); break;
        case 'playlist': loadPlaylist(); break;
        case 'dispatcharr': loadDispatcharr(); break;
        case 'diagnostics': loadDiagnostics(); break;
      }
    }

    document.querySelectorAll('nav button').forEach(b => b.addEventListener('click', () => showView(b.dataset.view)));
    document.getElementById('countrySelect').addEventListener('change', () => loadCountryValidation());
    ['discState','discSource','discCountry'].forEach(id => document.getElementById(id).addEventListener('change', renderDiscovery));

    showView('overview');
  })();
  </script>
</body>
</html>
""";
            return s;
        }

        // ---------- Autenticação opcional por token partilhado ----------

        /// <summary>
        /// Verifica se um pedido HTTP está autorizado quando o dashboard foi iniciado
        /// com um token (<paramref name="expectedToken"/> não vazio). Se o token não
        /// foi configurado, devolve true (compatibilidade com deployments locais).
        /// Caso contrário exige o token via header <c>Authorization: Bearer &lt;token&gt;</c>
        /// ou query string <c>?token=&lt;token&gt;</c>.
        /// </summary>
        public static bool IsRequestAuthorized(HttpListenerRequest request, string? expectedToken)
        {
            if (string.IsNullOrWhiteSpace(expectedToken)) return true;
            return IsAuthorized(request.Headers?["Authorization"], request.QueryString?["token"], expectedToken);
        }

        /// <summary>
        /// Lógica pura (testável sem HttpListener): valida token via header Bearer
        /// ou query <c>?token=</c>. Comparação em tempo constante.
        /// </summary>
        public static bool IsAuthorized(string? authorizationHeader, string? queryToken, string? expectedToken)
        {
            if (string.IsNullOrWhiteSpace(expectedToken)) return true;

            if (!string.IsNullOrEmpty(authorizationHeader))
            {
                const string prefix = "Bearer ";
                if (authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var presented = authorizationHeader.Substring(prefix.Length).Trim();
                    if (FixedEquals(presented, expectedToken)) return true;
                }
            }

            if (!string.IsNullOrEmpty(queryToken))
            {
                var presented = queryToken.Trim();
                if (FixedEquals(presented, expectedToken)) return true;
            }

            return false;
        }

        private static bool FixedEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            var ab = Encoding.UTF8.GetBytes(a);
            var bb = Encoding.UTF8.GetBytes(b);
            return CryptographicOperations.FixedTimeEquals(ab, bb);
        }

        private static async Task WriteUnauthorizedAsync(HttpListenerResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.Unauthorized;
            response.Headers["WWW-Authenticate"] = "Bearer realm=\"m3uCrawler\"";
            await WriteTextAsync(response, "Não autorizado. Forneça o token via 'Authorization: Bearer <token>' ou '?token=<token>'.", HttpStatusCode.Unauthorized);
        }
    }
}
