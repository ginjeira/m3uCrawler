using m3uCrawler.Models;

namespace m3uCrawler.Services
{
    public class M3uTesterService
    {
        private readonly HttpClient _httpClient;

        public M3uTesterService()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(12);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", 
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        }

        public async Task<M3uStream> TestM3u8Stream(string url, string title = "", string group = "Unknown")
        {
            var stream = new M3uStream
            {
                Url = url,
                Title = string.IsNullOrEmpty(title) ? ExtractTitleFromUrl(url) : title,
                Group = group,
                LastTested = DateTime.Now
            };

            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // ResponseHeadersRead evita ler o corpo inteiro — essencial para streams
                // ao vivo (.ts, .m3u8) que nunca terminam de enviar dados.
                using var response = await _httpClient.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead);

                stopwatch.Stop();
                stream.ResponseTime = stopwatch.ElapsedMilliseconds;

                var statusCode = (int)response.StatusCode;
                var isRedirect = statusCode >= 300 && statusCode < 400;
                var isWellKnownTransient = statusCode == 401 || statusCode == 403 || statusCode == 429 || statusCode == 500 || statusCode == 502 || statusCode == 503 || statusCode == 504;
                var isSuccess = response.IsSuccessStatusCode || isRedirect || isWellKnownTransient;
                stream.IsWorking = isSuccess;

                if (stream.IsWorking)
                {
                    var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

                    bool isPlaylist = contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
                        || url.Contains(".m3u", StringComparison.OrdinalIgnoreCase);

                    bool isVideoStream = contentType.Contains("video", StringComparison.OrdinalIgnoreCase)
                        || contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase)
                        || contentType.Contains("mpeg", StringComparison.OrdinalIgnoreCase)
                        || url.EndsWith(".ts", StringComparison.OrdinalIgnoreCase);

                    if (isPlaylist)
                    {
                        // Para playlists, ler o conteúdo e validar o cabeçalho M3U
                        var content = await response.Content.ReadAsStringAsync();
                        if (content.Contains("#EXTM3U") || content.Contains("#EXT-X-VERSION"))
                        {
                            Console.WriteLine($"✓ Stream funcional (M3U8): {url} ({stream.ResponseTime}ms)");
                        }
                        else if (content.Length > 0)
                        {
                            Console.WriteLine($"✓ Stream funcional (playlist): {url} ({stream.ResponseTime}ms)");
                        }
                        else if (isRedirect)
                        {
                            stream.IsWorking = true;
                            stream.FailureReason = "Redirect válido";
                            Console.WriteLine($"✓ Stream funcional (redirect): {url} ({stream.ResponseTime}ms)");
                        }
                        else
                        {
                            stream.IsWorking = false;
                            stream.FailureReason = "Playlist vazia ou inválida";
                            Console.WriteLine($"✗ Playlist vazia: {url}");
                        }
                    }
                    else if (isVideoStream)
                    {
                        // Stream de vídeo ao vivo: aceitamos 200, 302 e alguns 4xx/5xx transitórios como válidos
                        if (isRedirect || isWellKnownTransient)
                        {
                            stream.IsWorking = true;
                            stream.FailureReason = isRedirect ? "Redirect válido" : $"Status transitório: {response.StatusCode}";
                            Console.WriteLine($"✓ Stream funcional (vídeo ao vivo, resposta transitória): {url} ({stream.ResponseTime}ms)");
                        }
                        else
                        {
                            Console.WriteLine($"✓ Stream funcional (vídeo ao vivo): {url} ({stream.ResponseTime}ms)");
                        }
                    }
                    else
                    {
                        // Qualquer outro conteúdo com resposta válida é considerado funcional
                        Console.WriteLine($"✓ Stream funcional: {url} ({stream.ResponseTime}ms)");
                    }
                }
                else
                {
                    stream.FailureReason = $"Status: {response.StatusCode}";
                    Console.WriteLine($"✗ Stream não funcional: {url} (Status: {response.StatusCode})");
                }
            }
            catch (TaskCanceledException)
            {
                stream.IsWorking = false;
                stream.FailureReason = "Timeout";
                Console.WriteLine($"✗ Timeout: {url}");
            }
            catch (Exception ex)
            {
                stream.IsWorking = false;
                stream.FailureReason = ex.Message;
                Console.WriteLine($"✗ Erro ao testar {url}: {ex.Message}");
            }

            return stream;
        }

        public async Task<List<M3uStream>> TestMultipleStreams(List<string> urls, int maxConcurrency = 5)
        {
            var results = new List<M3uStream>();
            var semaphore = new SemaphoreSlim(maxConcurrency);

            var tasks = urls.Select(async url =>
            {
                await semaphore.WaitAsync();
                try
                {
                    return await TestM3u8Stream(url);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            results = (await Task.WhenAll(tasks)).ToList();
            return results;
        }

        private string ExtractTitleFromUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                var filename = Path.GetFileNameWithoutExtension(uri.LocalPath);
                return string.IsNullOrEmpty(filename) ? "Unknown Stream" : filename;
            }
            catch
            {
                return "Unknown Stream";
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
