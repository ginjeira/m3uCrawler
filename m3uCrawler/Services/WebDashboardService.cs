using m3uCrawler.Models;
using System.Net;
using System.Text;
using System.Text.Json;

namespace m3uCrawler.Services
{
    public static class WebDashboardService
    {
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

            await WriteHtmlAsync(context.Response, BuildHtmlPage());
        }

        private static async Task WriteJsonAsync(HttpListenerResponse response, object data)
        {
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
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
  <h2>Playlist atual</h2>
  <p><a href='/api/playlist' target='_blank'>Ver playlist.m3u</a> · <a href='/api/playlist_temp' target='_blank'>Ver playlist_temp.m3u</a></p>
  <pre id='playlistPreview'>Carregando...</pre>
  <script>
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
        </tr>`).join('');
      document.getElementById('history').innerHTML = `<table><thead><tr><th>Quando</th><th>Modo</th><th>Pesquisa</th><th>História</th><th>Máx</th><th>Novos</th><th>Retestados</th><th>Total final</th></tr></thead><tbody>${rows}</tbody></table>`;
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

    loadHistory();
    loadPlaylistPreview();
  </script>
</body>
</html>";
        }
    }
}
