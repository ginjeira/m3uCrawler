using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using m3uCrawler.Services.Dispatcharr;
using Xunit;

namespace m3uCrawler.Tests;

public class DispatcharrClientFactoryTests
{
    [Fact]
    public async Task Build_wires_inner_handler_so_first_request_does_not_throw()
    {
        var transport = new CaptureTransport();
        var (http, _, _, _, _, _) = DispatcharrClientFactory.BuildWithTransport(
            "http://dispatcharr.local",
            apiKey: "PLACEHOLDER-API-KEY",
            username: null,
            password: null,
            transport: transport);

        try
        {
            using var resp = await http.GetAsync("/api/core/version/");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.True(transport.Hit, "transport handler must receive the request when factory is used");
        }
        finally
        {
            http.Dispose();
        }
    }

    [Fact]
    public async Task Build_attaches_X_Api_Key_when_in_api_key_mode()
    {
        var transport = new CaptureTransport();
        var (http, auth, _, _, _, _) = DispatcharrClientFactory.BuildWithTransport(
            "http://dispatcharr.local",
            apiKey: "PLACEHOLDER-API-KEY",
            username: null,
            password: null,
            transport: transport);

        try
        {
            using var resp = await http.GetAsync("/api/core/version/");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Null(transport.LastAuthorization);
            Assert.Equal("PLACEHOLDER-API-KEY", transport.LastXApiKey);
            Assert.Equal("PLACEHOLDER-API-KEY", auth.AccessToken);
        }
        finally
        {
            http.Dispose();
        }
    }

    [Fact]
    public async Task Build_attaches_no_auth_header_when_in_jwt_mode_without_token()
    {
        var transport = new CaptureTransport();
        var (http, auth, _, _, _, _) = DispatcharrClientFactory.BuildWithTransport(
            "http://dispatcharr.local",
            apiKey: null,
            username: "u",
            password: "p",
            transport: transport);

        try
        {
            using var resp = await http.GetAsync("/api/core/version/");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Null(transport.LastAuthorization);
            Assert.Null(transport.LastXApiKey);
            Assert.Null(auth.AccessToken);
        }
        finally
        {
            http.Dispose();
        }
    }

    [Fact]
    public async Task Build_prefers_api_key_mode_when_both_configured()
    {
        var transport = new CaptureTransport();
        var (http, auth, _, _, _, _) = DispatcharrClientFactory.BuildWithTransport(
            "http://dispatcharr.local",
            apiKey: "KEY-FROM-CONFIG",
            username: "u",
            password: "p",
            transport: transport);

        try
        {
            using var resp = await http.GetAsync("/api/core/version/");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Null(transport.LastAuthorization);
            Assert.Equal("KEY-FROM-CONFIG", transport.LastXApiKey);
            Assert.Equal("KEY-FROM-CONFIG", auth.AccessToken);
        }
        finally
        {
            http.Dispose();
        }
    }

    [Fact]
    public void Build_sets_expected_base_address()
    {
        var transport = new CaptureTransport();
        var (http, _, _, _, _, _) = DispatcharrClientFactory.BuildWithTransport(
            "http://dispatcharr.local",
            apiKey: "K",
            username: null,
            password: null,
            transport: transport);

        try
        {
            Assert.Equal(new Uri("http://dispatcharr.local/api/"), http.BaseAddress);
        }
        finally
        {
            http.Dispose();
        }
    }

    [Fact]
    public void Build_appends_api_suffix_when_missing()
    {
        var transport = new CaptureTransport();
        var (http, _, _, _, _, _) = DispatcharrClientFactory.BuildWithTransport(
            "http://dispatcharr.local/",
            apiKey: "K",
            username: null,
            password: null,
            transport: transport);

        try
        {
            Assert.Equal(new Uri("http://dispatcharr.local/api/"), http.BaseAddress);
        }
        finally
        {
            http.Dispose();
        }
    }

    [Fact]
    public void Build_keeps_trailing_slash_when_api_suffix_present()
    {
        var transport = new CaptureTransport();
        var (http, _, _, _, _, _) = DispatcharrClientFactory.BuildWithTransport(
            "http://dispatcharr.local/api",
            apiKey: "K",
            username: null,
            password: null,
            transport: transport);

        try
        {
            Assert.Equal(new Uri("http://dispatcharr.local/api/"), http.BaseAddress);
        }
        finally
        {
            http.Dispose();
        }
    }

    [Fact]
    public void Build_throws_on_empty_base_url()
    {
        var transport = new CaptureTransport();
        Assert.Throws<ArgumentException>(() =>
            DispatcharrClientFactory.BuildWithTransport(
                "",
                apiKey: "K",
                username: null,
                password: null,
                transport: transport));
    }

    [Fact]
    public void Build_throws_on_null_transport()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DispatcharrClientFactory.BuildWithTransport(
                "http://dispatcharr.local",
                apiKey: "K",
                username: null,
                password: null,
                transport: null!));
    }

    [Fact]
    public void Build_disposes_chain_when_outer_httpclient_disposed()
    {
        var transport = new CaptureTransport();
        var (http, _, _, _, _, _) = DispatcharrClientFactory.BuildWithTransport(
            "http://dispatcharr.local",
            apiKey: "K",
            username: null,
            password: null,
            transport: transport);

        http.Dispose();
        Assert.True(transport.DisposeCount >= 1, "disposing the factory-built HttpClient must dispose the shared transport at least once");
    }

    private sealed class CaptureTransport : HttpMessageHandler
    {
        public bool Hit { get; private set; }
        public AuthenticationHeaderValue? LastAuthorization { get; private set; }
        public string? LastXApiKey { get; private set; }
        public int DisposeCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Hit = true;
            LastAuthorization = req.Headers.Authorization;
            if (req.Headers.TryGetValues("X-API-Key", out var values))
            {
                var joined = string.Join(",", values);
                LastXApiKey = string.IsNullOrEmpty(joined) ? null : joined;
            }
            else
            {
                LastXApiKey = null;
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            base.Dispose(disposing);
        }
    }
}
