namespace m3uCrawler.Services.Dispatcharr
{
    public sealed class DispatcharrClientFactory
    {
        public static (HttpClient Http, DispatcharrAuthState Auth, DispatcharrLoginApi Login,
                       DispatcharrChannelClient Channels, DispatcharrStreamClient Streams,
                       DispatcharrM3UClient M3U)
            Build(string baseUrl, string? apiKey, string? username, string? password)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("baseUrl required", nameof(baseUrl));

            var auth = new DispatcharrAuthState();
            var login = new DispatcharrLoginApi(new HttpClient())
            {
                ApiKey = apiKey,
                Username = username,
                Password = password,
            };

            var inner = new HttpClient(new DispatcharrAuthHandler(auth, login))
            {
                BaseAddress = new Uri(NormalizeBase(baseUrl)),
                Timeout = TimeSpan.FromSeconds(30),
            };
            inner.DefaultRequestHeaders.UserAgent.ParseAdd("m3uCrawler/2.1 (+Dispatcharr sync)");

            var channels = new DispatcharrChannelClient(inner);
            var streams = new DispatcharrStreamClient(inner);
            var m3u = new DispatcharrM3UClient(inner);

            if (!string.IsNullOrWhiteSpace(apiKey))
                auth.Set(apiKey, refresh: null);

            return (inner, auth, login, channels, streams, m3u);
        }

        private static string NormalizeBase(string baseUrl)
        {
            var b = baseUrl.TrimEnd('/');
            return b.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? b + "/" : b + "/api/";
        }
    }
}
