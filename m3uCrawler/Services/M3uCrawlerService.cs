using HtmlAgilityPack;
using System.Text.RegularExpressions;
using m3uCrawler.Models;

namespace m3uCrawler.Services
{
    public class M3uCrawlerService
    {
        private readonly HttpClient _httpClient;
        private readonly List<string> _searchEngines;
        private readonly Regex _m3u8Regex;

        public M3uCrawlerService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", 
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            
            _searchEngines = new List<string>
            {
                "https://www.google.com/search?q=filetype:m3u8+{0}",
                "https://www.bing.com/search?q=filetype:m3u8+{0}",
            };

            _m3u8Regex = new Regex(@"https?://[^\s]+\.m3u8[^\s]*", RegexOptions.IgnoreCase);
        }

        public async Task<List<string>> SearchM3u8Files(string searchTerm, int maxResults = 50)
        {
            var foundUrls = new HashSet<string>();
            
            foreach (var searchEngine in _searchEngines)
            {
                try
                {
                    var searchUrl = string.Format(searchEngine, Uri.EscapeDataString(searchTerm));
                    var response = await _httpClient.GetStringAsync(searchUrl);
                    
                    var matches = _m3u8Regex.Matches(response);
                    foreach (Match match in matches)
                    {
                        foundUrls.Add(match.Value);
                        if (foundUrls.Count >= maxResults) break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Erro ao pesquisar em {searchEngine}: {ex.Message}");
                }

                if (foundUrls.Count >= maxResults) break;
            }

            return foundUrls.ToList();
        }

        public async Task<List<string>> CrawlWebsiteForM3u8(string websiteUrl)
        {
            var foundUrls = new HashSet<string>();
            
            try
            {
                var response = await _httpClient.GetStringAsync(websiteUrl);
                var matches = _m3u8Regex.Matches(response);
                
                foreach (Match match in matches)
                {
                    foundUrls.Add(match.Value);
                }

                // Também procurar em links da página
                var doc = new HtmlDocument();
                doc.LoadHtml(response);
                
                var links = doc.DocumentNode.SelectNodes("//a[@href]");
                if (links != null)
                {
                    foreach (var link in links)
                    {
                        var href = link.GetAttributeValue("href", "");
                        if (_m3u8Regex.IsMatch(href))
                        {
                            foundUrls.Add(href);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao fazer crawl do website {websiteUrl}: {ex.Message}");
            }

            return foundUrls.ToList();
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
