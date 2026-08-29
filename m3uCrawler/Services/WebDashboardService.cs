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
            var query = context.Request.Url?.Query ?? string.Empty;

            if (requestPath.Equals("/api/history", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(context.Response, await historyService.GetRecentAsync(TimeSpan.FromHours(72)));
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

                var result = validator.ValidatePlaylist(playlistText, countryCode);
                var country = countryList.GetCountry(countryCode);

                await WriteJsonAsync(context.Response, new
                {
                    country = result.Country,
                    displayName = country.DisplayName,
                    isMatch = result.IsMatch,
                    matchedAliases = result.MatchedAliases,
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
    textarea { width: 100%; margin-top: 8px; background: #0f172a; color: #e2e8f0; border: 1px solid #334155; border-radius: 6px; padding: 10px; }
    button { background: #2563eb; color: white; border: none; border-radius: 6px; padding: 8px 12px; cursor: pointer; }
    .countryCard { margin-bottom: 16px; border: 1px solid #333; padding: 12px; border-radius: 8px; }
  </style>
</head>
<body>
  <h1>m3uCrawler Dashboard</h1>
  <p>Visualize o histórico de importações, as listas por país e o conteúdo atual da playlist.</p>
  <p>Últimas 72 horas:</p>
  <div id='history'></div>

  <h2>Validação por país</h2>
  <div style='margin-bottom: 16px;'>
    <label for='countrySelect'>País:</label>
    <select id='countrySelect' style='margin-left: 8px; padding: 8px; min-width: 180px;'></select>
  </div>
  <div id='countryValidationResult'></div>

  <h2>Listas de canais por país</h2>
  <div id='countrySection'></div>

  <h2>Playlist atual</h2>
  <p><a href='/api/playlist' target='_blank'>Ver playlist.m3u</a> · <a href='/api/playlist_temp' target='_blank'>Ver playlist_temp.m3u</a></p>
  <pre id='playlistPreview'>Carregando...</pre>
  <script>
    let countryOptions = [];

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

    async function loadCountries() {
      const res = await fetch('/api/countries');
      const countries = await res.json();
      countryOptions = countries;

      const countrySelect = document.getElementById('countrySelect');
      countrySelect.innerHTML = countries.map(country => `<option value='${country.country}'>${country.displayName || country.country}</option>`).join('');
      countrySelect.value = countries[0]?.country || 'pt';

      if (!countries.length) {
        document.getElementById('countrySection').innerHTML = '<p>Nenhuma lista de canais encontrada.</p>';
        document.getElementById('countryValidationResult').innerHTML = '<p>Sem país disponível para validação.</p>';
        return;
      }

      const countryCards = countries.map(country => `
        <div class='countryCard'>
          <strong>${country.displayName || country.country}</strong>
          <textarea id='country-${country.country}' rows='6'>${(country.channels || []).join('\n')}</textarea>
          <div style='margin-top:8px;'><button data-country='${country.country}'>Guardar</button></div>
        </div>`).join('');

      document.getElementById('countrySection').innerHTML = countryCards;
      loadCountryValidation(countrySelect.value);

      document.querySelectorAll('button[data-country]').forEach(button => {
        button.addEventListener('click', async () => {
          const countryCode = button.getAttribute('data-country');
          const textarea = document.getElementById(`country-${countryCode}`);
          const channels = textarea.value.split(/\r?\n/).map(x => x.trim()).filter(Boolean);
          const payload = JSON.stringify({ country: countryCode, displayName: countryCode.toUpperCase(), channels });

          const response = await fetch('/api/country/save', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: payload
          });

          if (response.ok) {
            alert('Lista guardada com sucesso.');
            await loadCountries();
          } else {
            const text = await response.text();
            alert('Erro ao guardar: ' + text);
          }
        });
      });
    }

    async function loadCountryValidation(country) {
      const res = await fetch(`/api/country/validate?country=${encodeURIComponent(country)}`);
      const result = await res.json();

      const details = result.isMatch
        ? `<p><strong>✅ Correspondência encontrada</strong> em ${result.displayName || result.country}.</p><p>Aliases: ${result.matchedAliases.join(', ') || 'Nenhum'}</p>`
        : `<p><strong>⚠️ Nenhuma correspondência</strong> em ${result.displayName || result.country}.</p><p>Aliases esperados: ${result.matchedAliases.join(', ') || 'Nenhum'}</p>`;

      document.getElementById('countryValidationResult').innerHTML = `
        <div class='countryCard'>
          <strong>Resultado da validação</strong>
          ${details}
          <p>Canais na lista: ${result.totalChannels}</p>
          <p>Tamanho da playlist: ${result.playlistLength} bytes</p>
        </div>`;
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

    document.getElementById('countrySelect').addEventListener('change', (event) => {
      loadCountryValidation(event.target.value);
    });

    loadHistory();
    loadCountries();
    loadPlaylistPreview();
  </script>
</body>
</html>";
        }
    }
}
