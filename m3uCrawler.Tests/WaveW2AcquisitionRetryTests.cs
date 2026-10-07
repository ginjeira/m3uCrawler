using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Validation;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W2 (2026-09-19) — retry técnico e checks de corpo na aquisição
/// (<c>docs/Reestructure/19-FAILURE-MODEL.md §6</c>). Retryable:
/// Network, DNS, timeout, 429, 5xx. Terminal: 401/403/404, vazio,
/// malformado, encoding inválido, oversized.
///
/// Os testes usam um <see cref="HttpMessageHandler"/> stub e URL com IP
/// público literal (dispensa DNS), provando que o retry acontece DENTRO da
/// mesma operação (nunca cria Run/RunId) e que o observador de falha é
/// notificado exactamente uma vez.
/// </summary>
public class WaveW2AcquisitionRetryTests
{
    private const string PublicUrl = "http://93.184.216.34/playlist.m3u";

    // ════════════════════════════════════════════════════════════════
    // Retryable → retried
    // ════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task Retryable_http_status_is_retried_then_succeeds(int status)
    {
        var handler = new ScriptedHandler(call =>
            call == 1
                ? new HttpResponseMessage((HttpStatusCode)status)
                : OkPlaylist());
        var tester = NewTester(handler, maxRetries: 1);
        var observer = new RecordingObserver();
        tester.SetAcquisitionFailureObserver(observer);

        var (content, ok) = await tester.DownloadPlaylistContentAsync(PublicUrl);

        Assert.True(ok);
        Assert.Equal("#EXTM3U\n", content);
        Assert.Equal(2, handler.Calls);
        Assert.Empty(observer.Failures); // sucesso não persiste falha
    }

    [Fact]
    public async Task Timeout_is_retried_then_succeeds()
    {
        var handler = new ScriptedHandler(call =>
            call == 1
                ? throw new TaskCanceledException("timeout", new TimeoutException("connect"))
                : OkPlaylist());
        var tester = NewTester(handler, maxRetries: 1);

        var (content, ok) = await tester.DownloadPlaylistContentAsync(PublicUrl);

        Assert.True(ok);
        Assert.Equal(2, handler.Calls);
        Assert.Equal("#EXTM3U\n", content);
    }

    [Fact]
    public async Task Network_failure_is_retried_then_succeeds()
    {
        var handler = new ScriptedHandler(call =>
            call == 1
                ? throw new HttpRequestException("net", new SocketException((int)SocketError.ConnectionRefused))
                : OkPlaylist());
        var tester = NewTester(handler, maxRetries: 1);

        var (content, ok) = await tester.DownloadPlaylistContentAsync(PublicUrl);

        Assert.True(ok);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Dns_failure_is_retried_then_succeeds()
    {
        var handler = new ScriptedHandler(call =>
            call == 1
                ? throw new SocketException((int)SocketError.HostNotFound)
                : OkPlaylist());
        var tester = NewTester(handler, maxRetries: 1);

        var (content, ok) = await tester.DownloadPlaylistContentAsync(PublicUrl);

        Assert.True(ok);
        Assert.Equal(2, handler.Calls);
    }

    // ════════════════════════════════════════════════════════════════
    // Terminal → sem retry
    // ════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task Terminal_http_status_is_not_retried(int status)
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage((HttpStatusCode)status));
        var tester = NewTester(handler, maxRetries: 3);
        var observer = new RecordingObserver();
        tester.SetAcquisitionFailureObserver(observer);

        var (content, ok) = await tester.DownloadPlaylistContentAsync(PublicUrl);

        Assert.False(ok);
        Assert.Null(content);
        Assert.Equal(1, handler.Calls);
        var failure = Assert.Single(observer.Failures);
        Assert.False(failure.IsRetryable);
        Assert.Equal(status, failure.HttpStatus);
    }

    [Fact]
    public async Task Empty_body_is_terminal_and_not_retried()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "application/x-mpegurl"),
        });
        var tester = NewTester(handler, maxRetries: 3);
        var observer = new RecordingObserver();
        tester.SetAcquisitionFailureObserver(observer);

        var (content, ok) = await tester.DownloadPlaylistContentAsync(PublicUrl);

        Assert.False(ok);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(AcquisitionFailureKind.Empty, Assert.Single(observer.Failures).FailureKind);
    }

    [Fact]
    public async Task Oversized_body_is_terminal_and_not_retried()
    {
        var big = new string('A', 4096);
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(big, Encoding.UTF8, "application/x-mpegurl"),
        });
        var tester = NewTester(handler, maxRetries: 3, maxResponseBytes: 1024);
        var observer = new RecordingObserver();
        tester.SetAcquisitionFailureObserver(observer);

        var (content, ok) = await tester.DownloadPlaylistContentAsync(PublicUrl);

        Assert.False(ok);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(AcquisitionFailureKind.Oversized, Assert.Single(observer.Failures).FailureKind);
    }

    [Fact]
    public async Task Invalid_encoding_is_terminal_and_not_retried()
    {
        var handler = new ScriptedHandler(_ =>
        {
            var content = new ByteArrayContent(new byte[] { 0xFF, 0xFE, 0x41 });
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-mpegurl") { CharSet = "utf-8" };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var tester = NewTester(handler, maxRetries: 3);
        var observer = new RecordingObserver();
        tester.SetAcquisitionFailureObserver(observer);

        var (content, ok) = await tester.DownloadPlaylistContentAsync(PublicUrl);

        Assert.False(ok);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(AcquisitionFailureKind.InvalidEncoding, Assert.Single(observer.Failures).FailureKind);
    }

    // ════════════════════════════════════════════════════════════════
    // Retry esgotado → uma única falha persistida/agregada
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Exhausted_retryable_failure_is_reported_once_for_the_whole_operation()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var tester = NewTester(handler, maxRetries: 2);
        var observer = new RecordingObserver();
        tester.SetAcquisitionFailureObserver(observer);

        var (content, ok) = await tester.DownloadPlaylistContentAsync(PublicUrl);

        Assert.False(ok);
        Assert.Null(content);
        Assert.Equal(3, handler.Calls);                 // 1 + 2 retries, mesma operação
        var failure = Assert.Single(observer.Failures); // NÃO uma por tentativa
        Assert.Equal(AcquisitionFailureKind.Http5xx, failure.FailureKind);
        Assert.True(failure.IsRetryable);
        Assert.True(failure.RetryExhausted);
        Assert.Equal(3, failure.Attempts);
    }

    [Fact]
    public async Task Guarded_acquisition_blocks_prohibited_literal_before_any_request()
    {
        var handler = new ScriptedHandler(_ => OkPlaylist());
        var tester = NewTester(handler, maxRetries: 0, guard: SsrfGuard.CreateDefault());
        var observer = new RecordingObserver();
        tester.SetAcquisitionFailureObserver(observer);

        var (content, ok) = await tester.DownloadPlaylistContentAsync("http://127.0.0.1/playlist.m3u");

        Assert.False(ok);
        Assert.Null(content);
        Assert.Equal(0, handler.Calls); // guard bloqueou antes do send
        Assert.Equal(AcquisitionFailureKind.Security, Assert.Single(observer.Failures).FailureKind);
    }

    // ════════════════════════════════════════════════════════════════
    // Classificação
    // ════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(AcquisitionFailureKind.Network, true)]
    [InlineData(AcquisitionFailureKind.Dns, true)]
    [InlineData(AcquisitionFailureKind.Timeout, true)]
    [InlineData(AcquisitionFailureKind.Http429, true)]
    [InlineData(AcquisitionFailureKind.Http5xx, true)]
    [InlineData(AcquisitionFailureKind.Authentication, false)]
    [InlineData(AcquisitionFailureKind.NotFound, false)]
    [InlineData(AcquisitionFailureKind.Empty, false)]
    [InlineData(AcquisitionFailureKind.Malformed, false)]
    [InlineData(AcquisitionFailureKind.InvalidEncoding, false)]
    [InlineData(AcquisitionFailureKind.Oversized, false)]
    [InlineData(AcquisitionFailureKind.Security, false)]
    [InlineData(AcquisitionFailureKind.Redirect, false)]
    public void Acquisition_classifier_matches_normative_retry_semantics(
        AcquisitionFailureKind kind, bool retryable)
    {
        Assert.Equal(retryable, AcquisitionFailureClassifier.IsRetryable(kind));
    }

    // ════════════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════════════

    private static M3uTesterService NewTester(
        HttpMessageHandler handler,
        int maxRetries,
        long maxResponseBytes = StreamValidationOptions.DefaultMaxResponseBytes,
        SsrfGuard? guard = null)
    {
        var options = new StreamValidationOptions
        {
            ConnectionTimeoutSeconds = 2,
            OverallTimeoutSeconds = 5,
            MaxRetries = maxRetries,
            RetryDelayMilliseconds = 1,
            MaxResponseBytes = maxResponseBytes,
        };
        var client = new HttpClient(handler);
        return new M3uTesterService(options, guard ?? SsrfGuard.CreateDefault(), client);
    }

    private static HttpResponseMessage OkPlaylist()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent("#EXTM3U\n", Encoding.UTF8, "application/x-mpegurl"),
        };

    private sealed class RecordingObserver : IAcquisitionFailureObserver
    {
        public List<AcquisitionFailureInfo> Failures { get; } = new();
        private readonly object _gate = new();

        public Task OnAcquisitionFailureAsync(AcquisitionFailureInfo failure, CancellationToken cancellationToken)
        {
            lock (_gate) Failures.Add(failure);
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _responder;
        public int Calls;

        public ScriptedHandler(Func<int, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref Calls);
            var response = _responder(call);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
