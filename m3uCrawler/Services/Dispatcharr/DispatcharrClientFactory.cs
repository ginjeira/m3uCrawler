using System.Net;
using System.Net.Http;

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

            var transport = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                AutomaticDecompression = DecompressionMethods.All,
            };

            var auth = new DispatcharrAuthState();

            var loginClient = new HttpClient(transport, disposeHandler: false);
            var login = new DispatcharrLoginApi(loginClient)
            {
                ApiKey = apiKey,
                Username = username,
                Password = password,
            };

            bool useApiKey = !string.IsNullOrWhiteSpace(apiKey);

            var authHandler = new DispatcharrAuthHandler(auth, login, useApiKey)
            {
                InnerHandler = transport,
            };

            var inner = new HttpClient(authHandler, disposeHandler: true)
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

            if (useApiKey)
                Console.WriteLine("🔑 Dispatcharr auth: X-API-Key mode active.");

            return (inner, auth, login, channels, streams, m3u);
        }

        internal static (HttpClient Http, DispatcharrAuthState Auth, DispatcharrLoginApi Login,
                         DispatcharrChannelClient Channels, DispatcharrStreamClient Streams,
                         DispatcharrM3UClient M3U)
            BuildWithTransport(string baseUrl, string? apiKey, string? username, string? password,
                               HttpMessageHandler transport)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("baseUrl required", nameof(baseUrl));
            if (transport == null)
                throw new ArgumentNullException(nameof(transport));

            var auth = new DispatcharrAuthState();

            var loginClient = new HttpClient(transport, disposeHandler: false);
            var login = new DispatcharrLoginApi(loginClient)
            {
                ApiKey = apiKey,
                Username = username,
                Password = password,
            };

            bool useApiKey = !string.IsNullOrWhiteSpace(apiKey);

            var authHandler = new DispatcharrAuthHandler(auth, login, useApiKey)
            {
                InnerHandler = transport,
            };

            var inner = new HttpClient(authHandler, disposeHandler: true)
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
