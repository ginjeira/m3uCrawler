using System.Net;
using System.Net.Http;
using System.Text;
using m3uCrawler.Models;
using m3uCrawler.Services.Dispatcharr;
using Xunit;

namespace m3uCrawler.Tests;

public sealed class DispatcharrConnectionTesterTests
{
    private static DispatcharrConfig Config(
        string baseUrl = "http://dispatcharr.local", string? apiKey = "SECRET-API-KEY")
        => new()
        {
            Enabled = true,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
        };

    [Fact]
    public async Task Returns_connected_with_version_on_200()
    {
        var methods = new List<HttpMethod>();
        var tester = new DispatcharrConnectionTester(
            () => new RecordingHandler(_ => Json(HttpStatusCode.OK, "{\"version\":\"1.2.3\"}"), methods));

        var result = await tester.TestAsync(Config());

        Assert.Equal(DispatcharrConnectionStatus.Connected, result.Status);
        Assert.Equal("1.2.3", result.Version);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.NotEmpty(methods);
        Assert.All(methods, m => Assert.Equal(HttpMethod.Get, m));
    }

    [Fact]
    public async Task Returns_authentication_failed_on_401()
    {
        var methods = new List<HttpMethod>();
        var tester = new DispatcharrConnectionTester(
            () => new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized), methods));

        var result = await tester.TestAsync(Config());

        Assert.Equal(DispatcharrConnectionStatus.AuthenticationFailed, result.Status);
        Assert.Equal(401, result.HttpStatusCode);
        Assert.All(methods, m => Assert.Equal(HttpMethod.Get, m));
        Assert.DoesNotContain(HttpMethod.Post, methods);
        Assert.DoesNotContain(HttpMethod.Patch, methods);
        Assert.DoesNotContain(HttpMethod.Delete, methods);
    }

    [Fact]
    public async Task Returns_authentication_failed_on_403()
    {
        var methods = new List<HttpMethod>();
        var tester = new DispatcharrConnectionTester(
            () => new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden), methods));

        var result = await tester.TestAsync(Config());

        Assert.Equal(DispatcharrConnectionStatus.AuthenticationFailed, result.Status);
        Assert.Equal(403, result.HttpStatusCode);
        Assert.All(methods, m => Assert.Equal(HttpMethod.Get, m));
    }

    [Fact]
    public async Task Returns_unreachable_on_http_request_exception()
    {
        var methods = new List<HttpMethod>();
        var tester = new DispatcharrConnectionTester(
            () => new RecordingHandler(_ => throw new HttpRequestException("connection refused"), methods));

        var result = await tester.TestAsync(Config());

        Assert.Equal(DispatcharrConnectionStatus.Unreachable, result.Status);
        Assert.Null(result.HttpStatusCode);
        Assert.All(methods, m => Assert.Equal(HttpMethod.Get, m));
    }

    [Fact]
    public async Task Returns_invalid_configuration_on_malformed_base_url()
    {
        var methods = new List<HttpMethod>();
        var tester = new DispatcharrConnectionTester(
            () => new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"), methods));

        var result = await tester.TestAsync(Config(baseUrl: "http://[invalid"));

        Assert.Equal(DispatcharrConnectionStatus.InvalidConfiguration, result.Status);
        Assert.Empty(methods);
    }

    [Fact]
    public async Task Returns_invalid_configuration_when_disabled()
    {
        var methods = new List<HttpMethod>();
        var tester = new DispatcharrConnectionTester(
            () => new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"), methods));

        var result = await tester.TestAsync(new DispatcharrConfig
        {
            Enabled = false,
            BaseUrl = "http://dispatcharr.local",
            ApiKey = "SECRET-API-KEY",
        });

        Assert.Equal(DispatcharrConnectionStatus.InvalidConfiguration, result.Status);
        Assert.Empty(methods);
    }

    [Fact]
    public async Task Returns_invalid_configuration_when_base_url_empty()
    {
        var methods = new List<HttpMethod>();
        var tester = new DispatcharrConnectionTester(
            () => new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"), methods));

        var result = await tester.TestAsync(Config(baseUrl: "   "));

        Assert.Equal(DispatcharrConnectionStatus.InvalidConfiguration, result.Status);
        Assert.Empty(methods);
    }

    [Fact]
    public async Task Returns_error_on_500()
    {
        var methods = new List<HttpMethod>();
        var tester = new DispatcharrConnectionTester(
            () => new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError), methods));

        var result = await tester.TestAsync(Config());

        Assert.Equal(DispatcharrConnectionStatus.Error, result.Status);
        Assert.Equal(500, result.HttpStatusCode);
        Assert.All(methods, m => Assert.Equal(HttpMethod.Get, m));
    }

    [Fact]
    public async Task Returned_detail_never_contains_api_key()
    {
        var methods = new List<HttpMethod>();
        var tester = new DispatcharrConnectionTester(
            () => new RecordingHandler(_ => throw new HttpRequestException("boom SECRET-API-KEY leaked"), methods));

        var result = await tester.TestAsync(Config(apiKey: "SECRET-API-KEY"));

        Assert.NotNull(result.SanitizedDetail);
        Assert.DoesNotContain("SECRET-API-KEY", result.SanitizedDetail);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        private readonly List<HttpMethod> _methods;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder, List<HttpMethod> methods)
        {
            _responder = responder;
            _methods = methods;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _methods.Add(request.Method);
            return Task.FromResult(_responder(request));
        }
    }
}
