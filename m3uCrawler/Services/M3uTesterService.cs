using m3uCrawler.Models;

namespace m3uCrawler.Services
{
    public class M3uTesterService
    {
        private readonly HttpClient _httpClient;

        public M3uTesterService()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
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
                
                var response = await _httpClient.GetAsync(url);
                stopwatch.Stop();
                
                stream.ResponseTime = stopwatch.ElapsedMilliseconds;
                stream.IsWorking = response.IsSuccessStatusCode;

                if (stream.IsWorking)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    
                    // Verificar se é um arquivo M3U8 válido
                    if (content.Contains("#EXTM3U") || content.Contains("#EXT-X-VERSION"))
                    {
                        Console.WriteLine($"✓ Stream funcional: {url} ({stream.ResponseTime}ms)");
                    }
                    else
                    {
                        stream.IsWorking = false;
                        Console.WriteLine($"✗ URL não é um M3U8 válido: {url}");
                    }
                }
                else
                {
                    Console.WriteLine($"✗ Stream não funcional: {url} (Status: {response.StatusCode})");
                }
            }
            catch (TaskCanceledException)
            {
                stream.IsWorking = false;
                Console.WriteLine($"✗ Timeout: {url}");
            }
            catch (Exception ex)
            {
                stream.IsWorking = false;
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
