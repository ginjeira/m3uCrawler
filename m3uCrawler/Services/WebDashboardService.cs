using m3uCrawler.Models;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace m3uCrawler.Services
{
    public static class WebDashboardService
    {
        private static readonly ConcurrentDictionary<string, string> _runState = new(StringComparer.OrdinalIgnoreCase);

        public static async Task RunDashboardAsync(string outputDir, int port, ImportHistoryService historyService, CancellationToken cancellationToken)
        {
            var listener = new HttpListener();
            var prefix = $"http://+:{port}/";
            listener.Prefixes.Add(prefix);
            listener.AuthenticationSchemes = AuthenticationSchemes.Anonymous;

            try
            {
                listener.Start();
                Console.WriteLine($"🌐 Dashboard iniciado em {prefix}");
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
                    _ = Task.Run(async () => await HandleRequestAsync(context, outputDir, historyService));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Erro no dashboard web: {ex.Message}");
                }
            }

            listener.Stop();
        }

        private static async Task HandleRequestAsync(HttpListenerContext context, string outputDir, ImportHistoryService historyService)
        {
            var requestPath = context.Request.Url?.AbsolutePath ?? "/";
            if (requestPath.Equals("/api/history", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(context.Response, await historyService.GetRecentAsync(TimeSpan.FromHours(72)));
                return;
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

            if (requestPath.Equals("/api/playlist_view", StringComparison.OrdinalIgnoreCase))
            {
                var playlistPath = Path.Combine(outputDir, "playlist.m3u");
                if (!File.Exists(playlistPath))
                {
                    await WriteTextAsync(context.Response, "Playlist não encontrada", HttpStatusCode.NotFound);
                    return;
                }

                var content = await File.ReadAllTextAsync(playlistPath, Encoding.UTF8);
                var html = $"<html><head><meta charset='utf-8'><title>playlist.m3u</title></head><body><pre>{WebUtility.HtmlEncode(content)}</pre></body></html>";
                await WriteHtmlAsync(context.Response, html);
                return;
            }

            if (requestPath.Equals("/api/playlist_temp_view", StringComparison.OrdinalIgnoreCase))
            {
                var tempPath = Path.Combine(outputDir, "playlist_temp.m3u");
                if (!File.Exists(tempPath))
                {
                    await WriteTextAsync(context.Response, "Playlist temporária não encontrada", HttpStatusCode.NotFound);
                    return;
                }

                var content = await File.ReadAllTextAsync(tempPath, Encoding.UTF8);
                var html = $"<html><head><meta charset='utf-8'><title>playlist_temp.m3u</title></head><body><pre>{WebUtility.HtmlEncode(content)}</pre></body></html>";
                await WriteHtmlAsync(context.Response, html);
                return;
            }

            if (requestPath.Equals("/api/playlist_report", StringComparison.OrdinalIgnoreCase))
            {
                var reportPath = Path.Combine(outputDir, "playlist_report.json");
                if (!File.Exists(reportPath))
                {
                    await WriteTextAsync(context.Response, "Relatório de playlist não encontrado", HttpStatusCode.NotFound);
                    return;
                }

                var json = await File.ReadAllTextAsync(reportPath, Encoding.UTF8);
                context.Response.ContentType = "application/json; charset=utf-8";
                var buffer = Encoding.UTF8.GetBytes(json);
                context.Response.ContentLength64 = buffer.Length;
                await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                context.Response.Close();
                return;
            }

            if (requestPath.Equals("/api/telegram_search_result", StringComparison.OrdinalIgnoreCase))
            {
                var reportPath = Path.Combine(outputDir, "telegram_search_result.json");
                if (!File.Exists(reportPath))
                {
                    await WriteTextAsync(context.Response, "Relatório de pesquisa Telegram não encontrado", HttpStatusCode.NotFound);
                    return;
                }

                var json = await File.ReadAllTextAsync(reportPath, Encoding.UTF8);
                context.Response.ContentType = "application/json; charset=utf-8";
                var buffer = Encoding.UTF8.GetBytes(json);
                context.Response.ContentLength64 = buffer.Length;
                await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                context.Response.Close();
                return;
            }

            if (requestPath.Equals("/api/run-now", StringComparison.OrdinalIgnoreCase))
            {
                _ = Task.Run(() => RunImmediateTelegramCycleAsync(outputDir, historyService));
                await WriteJsonAsync(context.Response, new { ok = true, message = "Execução iniciada." });
                return;
            }

            if (requestPath.Equals("/api/run-status", StringComparison.OrdinalIgnoreCase))
            {
                var state = _runState.TryGetValue("telegram", out var value) ? value : "idle";
                await WriteJsonAsync(context.Response, new { status = state });
                return;
            }

            await WriteHtmlAsync(context.Response, BuildHtmlPage());
        }

        private static async Task RunImmediateTelegramCycleAsync(string outputDir, ImportHistoryService historyService)
        {
            _runState["telegram"] = "running";
            try
            {
                var scraper = new TelegramScraperService();
                await scraper.LoginAsync();

                var term = "portugal";
                var telegramMaxStreams = 500;
                var telegramHistoryHours = 72;
                var playlistManager = new PlaylistManagerService();
                playlistManager.CreateOutputDirectory(outputDir);

                await RunTelegramMaintenanceCycle(
                    scraper,
                    playlistManager,
                    historyService,
                    term,
                    outputDir,
                    telegramMaxStreams,
                    null,
                    telegramHistoryHours);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Execução imediata falhou: {ex.Message}");
            }
            finally
            {
                _runState["telegram"] = "idle";
            }
        }

        private static async Task RunTelegramMaintenanceCycle(
            TelegramScraperService scraper,
            PlaylistManagerService playlistManager,
            ImportHistoryService importHistory,
            string term,
            string outputDir,
            int telegramMaxStreams,
            string? domainFilter,
            int telegramHistoryHours)
        {
            var tempPath = Path.Combine(outputDir, "playlist_temp.m3u");
            var mainPath = Path.Combine(outputDir, "playlist.m3u");
            var reportPath = Path.Combine(outputDir, "telegram_maintain_report.json");

            Console.WriteLine();
            Console.WriteLine("🧹 Início do ciclo de manutenção Telegram...");

            await File.WriteAllTextAsync(tempPath, "#EXTM3U" + Environment.NewLine, Encoding.UTF8);

            var freshSearchResult = await scraper.SearchAndTestM3UInTelegram(
                term,
                limit: 200,
                maxConcurrency: 5,
                maxUrlsToTest: telegramMaxStreams,
                historyHours: telegramHistoryHours);

            var freshStreams = freshSearchResult.WorkingStreams;

            if (!string.IsNullOrWhiteSpace(domainFilter))
            {
                int beforeFilter = freshStreams.Count;
                freshStreams = freshStreams
                    .Where(s => UrlMatchesDomain(s.Url, domainFilter))
                    .ToList();
                Console.WriteLine($"🌐 Após filtro de domínio: {freshStreams.Count}/{beforeFilter} streams");
            }

            await playlistManager.SaveToM3uPlaylist(freshStreams, tempPath);
            Console.WriteLine($"🔔 Novos canais funcionais em playlist_temp.m3u: {freshStreams.Count}");
            Console.WriteLine($"   • Escrito: {tempPath}");

            var existingMain = await playlistManager.LoadFromM3uPlaylist(mainPath);
            Console.WriteLine($"📄 playlist.m3u atual: {existingMain.Count} stream(s) a retestar");

            List<M3uStream> stillWorkingMain;
            if (existingMain.Count > 0)
            {
                var tester = new M3uTesterService();
                try
                {
                    Console.WriteLine($"🔁 Re-testando {existingMain.Count} stream(s) existentes de playlist.m3u...");
                    var retestTasks = existingMain.Select(async stream =>
                    {
                        var tested = await tester.TestM3u8Stream(stream.Url, stream.Title, stream.Group);
                        tested.OriginalExtInf = stream.OriginalExtInf;
                        tested.Logo = stream.Logo;
                        return tested;
                    });

                    var retested = await Task.WhenAll(retestTasks);
                    stillWorkingMain = retested.Where(s => s.IsWorking).ToList();
                    Console.WriteLine($"✅ Streams existentes ainda funcionais: {stillWorkingMain.Count}/{existingMain.Count}");
                }
                finally
                {
                    tester.Dispose();
                }
            }
            else
            {
                stillWorkingMain = new List<M3uStream>();
            }

            var mergedByUrl = new Dictionary<string, M3uStream>(StringComparer.OrdinalIgnoreCase);

            foreach (var stream in stillWorkingMain)
            {
                mergedByUrl[stream.Url] = stream;
            }

            foreach (var stream in freshStreams.Where(s => s.IsWorking))
            {
                mergedByUrl[stream.Url] = stream;
            }

            var finalStreams = mergedByUrl.Values.ToList();
            await playlistManager.SaveToM3uPlaylist(finalStreams, mainPath);
            await playlistManager.SaveToJsonReport(finalStreams, reportPath);

            var stablePlaylistReportPath = Path.Combine(outputDir, "playlist_report.json");
            await playlistManager.SaveToJsonReport(finalStreams, stablePlaylistReportPath);

            var telegramSearchResultPath = Path.Combine(outputDir, "telegram_search_result.json");
            var searchOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            await File.WriteAllTextAsync(telegramSearchResultPath, JsonSerializer.Serialize(freshSearchResult, searchOptions), Encoding.UTF8);

            var historyEntry = new ImportHistoryEntry
            {
                Timestamp = DateTime.UtcNow,
                Mode = "TelegramMaintenance",
                SearchTerm = term,
                HistoryHours = telegramHistoryHours,
                MaxStreams = telegramMaxStreams,
                NewFunctionalCount = freshStreams.Count(s => s.IsWorking),
                ExistingRetestedCount = existingMain.Count,
                ExistingStillWorkingCount = stillWorkingMain.Count,
                FinalPlaylistCount = finalStreams.Count
            };
            await importHistory.RecordImportAsync(historyEntry);

            Console.WriteLine("✅ Ciclo concluído.");
            Console.WriteLine($"   • Mantidas de playlist.m3u: {stillWorkingMain.Count}");
            Console.WriteLine($"   • Novas funcionais de playlist_temp.m3u: {freshStreams.Count(s => s.IsWorking)}");
            Console.WriteLine($"   • Total final em playlist.m3u: {finalStreams.Count}");
            Console.WriteLine($"   • playlist_temp.m3u: {tempPath}");
            Console.WriteLine($"   • playlist.m3u: {mainPath}");
        }

        private static bool UrlMatchesDomain(string url, string domainFilter)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

            string host = uri.Host;
            return host.Equals(domainFilter, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith($".{domainFilter}", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task WriteJsonAsync(HttpListenerResponse response, object data)
        {
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            response.ContentType = "application/json; charset=utf-8";
            var buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.Close();
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
            return @"<!doctype html>
<html lang='pt'>
<head>
  <meta charset='utf-8'>
  <title>m3uCrawler Dashboard</title>
  <style>
    body { font-family: Arial, sans-serif; margin: 20px; background: #121212; color: #e9ecef; }
    a { color: #1bb0ff; }
    table { border-collapse: collapse; width: 100%; margin-top: 16px; }
    th, td { border: 1px solid #333; padding: 12px; }
    th { background: #1f2937; }
    tr:nth-child(even) { background: #1b1f26; }
    pre { background: #111827; padding: 14px; overflow: auto; }
    .badge { display: inline-block; padding: 4px 10px; border-radius: 999px; background: #2563eb; color: #fff; margin-right: 6px; }
  </style>
</head>
<body>
  <h1>m3uCrawler Dashboard</h1>
  <p>Visualize o histórico de importações e o conteúdo atual da playlist.</p>
  <p>Últimas 72 horas:</p>
  <div id='history'></div>
  <h2>Execução imediata</h2>
  <p><button id='runNowButton' onclick='runNow()'>Executar agora</button> <span id='runStatus'>idle</span></p>
  <h2>Playlist atual</h2>
  <p><a href='/api/playlist' target='_blank'>Ver playlist.m3u</a> · <a href='/api/playlist_temp' target='_blank'>Ver playlist_temp.m3u</a> · <a href='/api/playlist_report' target='_blank'>Ver relatório JSON</a></p>
  <pre id='playlistPreview'>Carregando...</pre>
  <h2>Relatório de playlist</h2>
  <div id='playlistReport'>Carregando relatório...</div>
  <h2>Diagnóstico de pesquisa Telegram</h2>
  <div id='searchResult'>Carregando diagnóstico...</div>
  <script>
    function renderTable(headers, rows) {
      return `<table><thead><tr>${headers.map(h => `<th>${h}</th>`).join('')}</tr></thead><tbody>${rows.join('')}</tbody></table>`;
    }

    async function runNow() {
      const button = document.getElementById('runNowButton');
      button.disabled = true;
      document.getElementById('runStatus').textContent = 'iniciando...';
      await fetch('/api/run-now', { method: 'POST' });
      setTimeout(checkRunStatus, 1000);
    }

    async function checkRunStatus() {
      const res = await fetch('/api/run-status');
      const data = await res.json();
      document.getElementById('runStatus').textContent = data.status || 'idle';
      if (data.status === 'running') {
        setTimeout(checkRunStatus, 2000);
      } else {
        document.getElementById('runNowButton').disabled = false;
      }
    }

    async function loadHistory() {
      const res = await fetch('/api/history');
      const items = await res.json();
      if (!items.length) {
        document.getElementById('history').innerHTML = '<p>Nenhum histórico encontrado.</p>';
        return;
      }
      const rows = items.map(entry => `
        <tr>
          <td>${new Date(entry.timestamp).toLocaleString()}</td>
          <td>${entry.mode}</td>
          <td>${entry.searchTerm}</td>
          <td>${entry.historyHours}h</td>
          <td>${entry.maxStreams}</td>
          <td>${entry.newFunctionalCount}</td>
          <td>${entry.existingStillWorkingCount}/${entry.existingRetestedCount}</td>
          <td>${entry.finalPlaylistCount}</td>
        </tr>`);
      document.getElementById('history').innerHTML = renderTable(
        ['Quando', 'Modo', 'Pesquisa', 'História', 'Máx', 'Novos', 'Retestados', 'Total final'],
        rows);
    }

    async function loadPlaylistPreview() {
      try {
        const res = await fetch('/api/playlist');
        if (!res.ok) {
          document.getElementById('playlistPreview').textContent = 'Playlist não disponível.';
          return;
        }
        const text = await res.text();
        document.getElementById('playlistPreview').textContent = text.split('\n').slice(0, 40).join('\n');
      } catch (err) {
        document.getElementById('playlistPreview').textContent = 'Erro ao carregar playlist.';
      }
    }

    async function loadPlaylistReport() {
      try {
        const res = await fetch('/api/playlist_report');
        if (!res.ok) {
          document.getElementById('playlistReport').textContent = 'Relatório não disponível.';
          return;
        }
        const json = await res.json();
        const summary = `
          <p><strong>Gerado em:</strong> ${new Date(json.generatedAt).toLocaleString()}</p>
          <p><strong>Total de streams:</strong> ${json.totalStreams}</p>
          <p><strong>Streams funcionais:</strong> ${json.workingStreams}</p>
          <p><strong>Streams não funcionais:</strong> ${json.nonWorkingStreams}</p>
          <p><strong>Tempo médio de resposta:</strong> ${json.averageResponseTime.toFixed(1)} ms</p>`;
        const rows = json.streams.slice(0, 20).map(stream => `
          <tr>
            <td>${stream.title || '(sem título)'}</td>
            <td>${stream.group || '(sem grupo)'}</td>
            <td>${stream.responseTime} ms</td>
            <td>${stream.failureReason || 'OK'}</td>
            <td><a href='${stream.url}' target='_blank'>link</a></td>
          </tr>`);
        document.getElementById('playlistReport').innerHTML = summary + renderTable(['Título', 'Grupo', 'Tempo', 'Falha', 'URL'], rows);
      } catch (err) {
        document.getElementById('playlistReport').textContent = 'Erro ao carregar relatório.';
      }
    }

    async function loadSearchResult() {
      try {
        const res = await fetch('/api/telegram_search_result');
        if (!res.ok) {
          document.getElementById('searchResult').textContent = 'Diagnóstico não disponível.';
          return;
        }
        const json = await res.json();
        const report = json.searchReport || json;
        const summary = `
          <p><strong>Termo:</strong> ${report.searchTerm}</p>
          <p><strong>Gerado em:</strong> ${new Date(report.generatedAt).toLocaleString()}</p>
          <p><strong>Mensagens correspondentes:</strong> ${report.messagesMatched}</p>
          <p><strong>URLs encontradas:</strong> ${report.foundUrls}</p>
          <p><strong>URLs testadas:</strong> ${report.testedUrls}</p>
          <p><strong>Streams funcionais:</strong> ${report.workingStreams}</p>
          <p><strong>Streams falhadas:</strong> ${report.failedStreams}</p>
          <p><strong>Xtream detectado:</strong> ${report.xtreamCredentialsDetected}</p>`;
        const sampleRows = (report.sampleFoundUrls || []).slice(0, 30).map(urlInfo => `
          <tr>
            <td>${urlInfo.chatTitle || '(sem chat)'}</td>
            <td><a href='${urlInfo.url}' target='_blank'>link</a></td>
            <td>${urlInfo.originalExtInf || '(sem extinf)'}</td>
            <td>${urlInfo.sourceText ? urlInfo.sourceText.substring(0, 120) : ''}</td>
          </tr>`);
        document.getElementById('searchResult').innerHTML = summary + renderTable(['Chat', 'URL', 'EXTINF', 'Contexto'], sampleRows);
      } catch (err) {
        document.getElementById('searchResult').textContent = 'Erro ao carregar diagnóstico.';
      }
    }

    checkRunStatus();
    loadHistory();
    loadPlaylistPreview();
    loadPlaylistReport();
    loadSearchResult();
  </script>
</body>
</html>";
        }
    }
}
