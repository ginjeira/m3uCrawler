using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Validation;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Validação do ramo NÃO-playlist de <c>M3uTesterService.ProbeOnceAsync</c>.
///
/// Antes: qualquer 2xx de um stream não-playlist era marcado
/// <c>IsWorking=true</c> sem ler o corpo — o que introduzia canais mortos na
/// playlist final (200 com HTML/erro, corpo vazio, ou corpo que estanca).
///
/// Agora: exige evidência mínima de mídia no prefixo do corpo. Estes testes
/// usam o construtor interno com um <see cref="HttpMessageHandler"/> scriptado
/// (o seam de teste que faz <c>ProbeOnceAsync</c> usar o <c>_client</c>
/// injectado), sem rede nem URLs/segredos reais.
/// </summary>
public class M3uTesterProbeNonPlaylistTests
{
    private const string Url = "http://93.184.216.34/live/channel.ts";

    [Fact]
    public async Task NonPlaylist_2xx_empty_body_is_not_working()
    {
        var handler = new ScriptedHandler(() => Response(
            new ByteArrayContent(Array.Empty<byte>()), "application/octet-stream"));
        var tester = NewTester(handler);

        var outcome = await tester.TestSingleAsync(Url);

        Assert.False(outcome.IsWorking);
        Assert.Equal(StreamFailureKind.NoMedia, outcome.FailureKind);
        Assert.False(StreamFailureClassifier.IsRetryable(outcome.FailureKind));
    }

    [Fact]
    public async Task NonPlaylist_2xx_text_html_without_extm3u_is_not_working()
    {
        var handler = new ScriptedHandler(() => Response(
            new StringContent("<html><body>Not found</body></html>", Encoding.UTF8, "text/html"),
            "text/html"));
        var tester = NewTester(handler);

        var outcome = await tester.TestSingleAsync(Url);

        Assert.False(outcome.IsWorking);
        Assert.Equal(StreamFailureKind.NoMedia, outcome.FailureKind);
        Assert.False(StreamFailureClassifier.IsRetryable(outcome.FailureKind));
    }

    [Fact]
    public async Task NonPlaylist_2xx_mpegts_sync_is_working()
    {
        // 0x47 no offset 0 e no offset 188 → sincronismo MPEG-TS. Content-Type
        // neutro (octet-stream) para provar que é o sincronismo, não o tipo.
        var body = new byte[4096];
        body[0] = 0x47;
        body[MpegTsOffset] = 0x47;
        var handler = new ScriptedHandler(() => Response(
            new ByteArrayContent(body), "application/octet-stream"));
        var tester = NewTester(handler);

        var outcome = await tester.TestSingleAsync(Url);

        Assert.True(outcome.IsWorking);
        Assert.Equal(StreamFailureKind.None, outcome.FailureKind);
    }

    [Fact]
    public async Task NonPlaylist_2xx_media_content_type_with_enough_bytes_is_working()
    {
        var body = new byte[4096];
        var handler = new ScriptedHandler(() => Response(
            new ByteArrayContent(body), "video/mp2t"));
        var tester = NewTester(handler);

        var outcome = await tester.TestSingleAsync(Url);

        Assert.True(outcome.IsWorking);
    }

    [Fact]
    public async Task NonPlaylist_2xx_media_content_type_below_minimum_bytes_is_not_working()
    {
        // Só 100 bytes de um corpo de mídia: abaixo do mínimo exigido → NoMedia.
        var body = new byte[100];
        var handler = new ScriptedHandler(() => Response(
            new ByteArrayContent(body), "video/mp2t"));
        var tester = NewTester(handler);

        var outcome = await tester.TestSingleAsync(Url);

        Assert.False(outcome.IsWorking);
        Assert.Equal(StreamFailureKind.NoMedia, outcome.FailureKind);
    }

    [Fact]
    public async Task NonPlaylist_2xx_extm3u_in_body_is_working()
    {
        var handler = new ScriptedHandler(() => Response(
            new StringContent("#EXTM3U\n#EXTINF:-1,x\n", Encoding.UTF8, "application/octet-stream"),
            "application/octet-stream"));
        var tester = NewTester(handler);

        var outcome = await tester.TestSingleAsync(Url);

        Assert.True(outcome.IsWorking);
    }

    [Fact]
    public async Task NonPlaylist_2xx_stalling_body_is_not_working_and_retryable_timeout()
    {
        // O corpo nunca entrega bytes: a leitura do prefixo estanca até ao
        // OverallTimeout e tem de ser classificada como Timeout (retryable),
        // preservando a retenção de streams existentes.
        var handler = new ScriptedHandler(() => Response(new StallingContent(), "video/mp2t"));
        var tester = NewTester(handler, overallTimeoutSeconds: 1);

        var outcome = await tester.TestSingleAsync(Url);

        Assert.False(outcome.IsWorking);
        Assert.Equal(StreamFailureKind.Timeout, outcome.FailureKind);
        Assert.True(StreamFailureClassifier.IsRetryable(outcome.FailureKind));
    }

    // ────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────

    private const int MpegTsOffset = 188;

    private static M3uTesterService NewTester(HttpMessageHandler handler, int overallTimeoutSeconds = 3)
    {
        var options = new StreamValidationOptions
        {
            ConnectionTimeoutSeconds = 1,
            OverallTimeoutSeconds = overallTimeoutSeconds,
            MaxRetries = 0,
            RetryDelayMilliseconds = 1,
        };
        var client = new HttpClient(handler);
        return new M3uTesterService(options, SsrfGuard.CreateDefault(), client);
    }

    private static HttpResponseMessage Response(HttpContent content, string mediaType)
    {
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _responder;
        public int Calls;

        public ScriptedHandler(Func<HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var response = _responder();
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class StallingContent : HttpContent
    {
        public StallingContent()
        {
            Headers.ContentType = new MediaTypeHeaderValue("video/mp2t");
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new StallingStream());

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.Delay(System.Threading.Timeout.Infinite);

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0; // inalcançável: o Delay infinite só termina por cancelamento.
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
