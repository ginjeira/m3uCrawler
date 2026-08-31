using System.Net.Http.Headers;
using m3uCrawler.Services.Dispatcharr;
using Xunit;

namespace m3uCrawler.Tests;

[Trait("Category", "Live")]
public class DispatcharrLiveReadonlyIntegrationTests
{
    private static (string baseUrl, string apiKey)? TryLoadEnv()
    {
        var baseUrl = Environment.GetEnvironmentVariable("DISPATCHARR_BASE_URL");
        var apiKey = Environment.GetEnvironmentVariable("DISPATCHARR_API_KEY");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
            return null;
        return (baseUrl.Trim(), apiKey.Trim());
    }

    [Fact]
    public async Task Api_key_mode_reads_version_channels_and_streams_from_real_dispatcharr()
    {
        var env = TryLoadEnv();
        if (env is null)
        {
            return;
        }

        var (baseUrl, apiKey) = env.Value;
        using var inner = new HttpClient(new SocketsHttpHandler(), disposeHandler: true);
        var capture = new HeaderCaptureHandler(inner);
        var (http, _, _, channels, streams, m3u) = DispatcharrClientFactory.BuildWithTransport(
            baseUrl, apiKey, username: null, password: null, transport: capture);

        try
        {
            var version = await m3u.GetVersionAsync(CancellationToken.None);
            var channelList = await channels.ListAsync(CancellationToken.None);
            var streamList = await streams.ListAsync(CancellationToken.None);

            Assert.True(version == null || version.Length > 0);
            Assert.NotNull(channelList);
            Assert.NotNull(streamList);

            Assert.Equal(3, capture.Count);
            foreach (var (sentKey, sentAuth) in capture.Zipped())
            {
                Assert.Equal(apiKey, sentKey);
                Assert.Null(sentAuth);
            }
        }
        finally
        {
            http.Dispose();
        }
    }

    [Fact]
    public async Task Api_key_mode_returns_401_terminal_when_key_invalid()
    {
        var env = TryLoadEnv();
        if (env is null)
        {
            return;
        }

        var (baseUrl, _) = env.Value;
        using var inner = new HttpClient(new SocketsHttpHandler(), disposeHandler: true);
        var capture = new HeaderCaptureHandler(inner);
        var (http, _, _, channels, _, _) = DispatcharrClientFactory.BuildWithTransport(
            baseUrl, apiKey: "definitely-not-a-real-key", username: null, password: null, transport: capture);

        try
        {
            await Assert.ThrowsAsync<DispatcharrException>(async () =>
                await channels.ListAsync(CancellationToken.None));

            Assert.Equal(1, capture.Count);
            Assert.Null(capture.LastAuthorization);
        }
        finally
        {
            http.Dispose();
        }
    }

    private sealed class HeaderCaptureHandler : HttpMessageHandler
    {
        private readonly HttpClient _forwarder;
        public int Count { get; private set; }
        public List<string> SeenXApiKeyValues { get; } = new();
        public List<AuthenticationHeaderValue?> SeenAuthorizations { get; } = new();
        public AuthenticationHeaderValue? LastAuthorization => SeenAuthorizations.Count == 0 ? null : SeenAuthorizations[^1];
        public string? LastXApiKey => SeenXApiKeyValues.Count == 0 ? null : SeenXApiKeyValues[^1];

        public HeaderCaptureHandler(HttpClient forwarder) { _forwarder = forwarder; }

        public IEnumerable<(string? Key, AuthenticationHeaderValue? Auth)> Zipped()
        {
            for (int i = 0; i < SeenXApiKeyValues.Count; i++)
            {
                yield return (SeenXApiKeyValues[i], SeenAuthorizations[i]);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Count++;
            SeenAuthorizations.Add(req.Headers.Authorization);
            string? sent = null;
            if (req.Headers.TryGetValues("X-API-Key", out var values))
            {
                sent = string.Join(",", values);
            }
            SeenXApiKeyValues.Add(sent ?? string.Empty);
            var clone = await CloneAsync(req, req.RequestUri, ct);
            return await _forwarder.SendAsync(clone, ct);
        }

        private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage src, Uri? overrideUri, CancellationToken ct)
        {
            var clone = new HttpRequestMessage(src.Method, overrideUri ?? src.RequestUri);
            if (src.Content != null)
            {
                var ms = new MemoryStream();
                await src.Content.CopyToAsync(ms, ct);
                ms.Position = 0;
                clone.Content = new StreamContent(ms);
                foreach (var h in src.Content.Headers)
                    clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            foreach (var h in src.Headers)
                clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return clone;
        }
    }
}

