using System.Net;
using System.Net.Http;
using System.Text.Json;
using m3uCrawler.Services.Dispatcharr;
using Xunit;

namespace m3uCrawler.Tests;

public class DispatcharrAuthHandlerTests
{
    [Fact]
    public async Task Adds_bearer_token_when_available()
    {
        var state = new DispatcharrAuthState();
        state.Set("access-tok", null);
        var login = new DispatcharrLoginApi(new HttpClient()) { Username = null, Password = null };
        var handler = new DispatcharrAuthHandler(state, login)
        {
            InnerHandler = new CaptureHandler(),
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://x/api/") };
        var resp = await client.GetAsync("/foo");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Refresh_on_401_then_retry_succeeds()
    {
        var state = new DispatcharrAuthState();
        state.Set("stale", "refresh-tok");
        var refreshCalls = 0;
        var login = new StubLoginApi
        {
            RefreshHandler = _ => { refreshCalls++; return Task.FromResult(new LoginResponse { Access = "fresh", Refresh = "refresh-tok" }); },
        };
        var handler = new DispatcharrAuthHandler(state, login)
        {
            InnerHandler = new SequenceHandler(
                new HttpResponseMessage(HttpStatusCode.Unauthorized),
                new HttpResponseMessage(HttpStatusCode.OK)),
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://x/api/") };
        var resp = await client.GetAsync("/x");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, refreshCalls);
        Assert.Equal("fresh", state.AccessToken);
    }

    [Fact]
    public async Task No_double_retry_loop()
    {
        var state = new DispatcharrAuthState();
        state.Set("a", "r");
        var login = new StubLoginApi
        {
            RefreshHandler = _ => Task.FromResult(new LoginResponse { Access = "a2", Refresh = "r" }),
        };
        var inner = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var handler = new DispatcharrAuthHandler(state, login) { InnerHandler = inner };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://x/api/") };
        var resp = await client.GetAsync("/x");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(2, inner.Count);
    }

    [Fact]
    public async Task Api_key_mode_attaches_X_Api_Key_header()
    {
        var state = new DispatcharrAuthState();
        state.Set("API-KEY-VALUE", null);
        var login = new StubLoginApi();
        var capture = new ApiKeyCaptureHandler();
        var handler = new DispatcharrAuthHandler(state, login, useApiKey: true) { InnerHandler = capture };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://x/api/") };
        var resp = await client.GetAsync("/foo");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("API-KEY-VALUE", capture.LastXApiKey);
    }

    [Fact]
    public async Task Api_key_mode_returns_401_without_retry_or_login()
    {
        var state = new DispatcharrAuthState();
        state.Set("API-KEY-VALUE", null);
        var login = new StubLoginApi
        {
            RefreshHandler = _ => { throw new InvalidOperationException("refresh should not be called in api-key mode"); },
        };
        var inner = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            new HttpResponseMessage(HttpStatusCode.Unauthorized),
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var handler = new DispatcharrAuthHandler(state, login, useApiKey: true) { InnerHandler = inner };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://x/api/") };
        var resp = await client.GetAsync("/x");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(1, inner.Count);
    }
}

internal sealed class CaptureHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        Assert.NotNull(req.Headers.Authorization);
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}

internal sealed class ApiKeyCaptureHandler : HttpMessageHandler
{
    public string? LastXApiKey { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        Assert.Null(req.Headers.Authorization);
        if (req.Headers.TryGetValues("X-API-Key", out var values))
        {
            var joined = string.Join(",", values);
            LastXApiKey = string.IsNullOrEmpty(joined) ? null : joined;
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}

internal sealed class SequenceHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses;
    public int Count { get; private set; }
    public SequenceHandler(params HttpResponseMessage[] responses) { _responses = new Queue<HttpResponseMessage>(responses); }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        Count++;
        if (_responses.Count == 0) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        return Task.FromResult(_responses.Dequeue());
    }
}

internal sealed class StubLoginApi : DispatcharrLoginApi
{
    public Func<string, Task<LoginResponse>>? RefreshHandler { get; set; }
    public StubLoginApi() : base(new HttpClient()) { }

    public override Task<LoginResponse> RefreshAsync(string refresh, CancellationToken ct)
    {
        if (RefreshHandler == null) throw new InvalidOperationException("no refresh");
        return RefreshHandler(refresh);
    }
}
