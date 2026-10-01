using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace m3uCrawler.Tests.Helpers;

/// <summary>
/// W-DEDUP (2026-10-01) — servidor Xtream scriptado em loopback para os testes
/// de deduplicação de validação física.
///
/// <para>
/// <see cref="m3uCrawler.Services.M3uTesterService.ProbeOnceAsync"/> resolve o
/// <see cref="System.Net.Http.HttpClient"/> a partir do
/// <see cref="m3uCrawler.Services.Validation.HttpClientFactory"/> (process-wide),
/// pelo que um <c>HttpMessageHandler</c> falso não intercepta os probes. Este
/// listener real é o único ponto onde os GETs físicos podem ser observados e
/// contados.
/// </para>
///
/// <para>
/// Regras:
/// <list type="bullet">
///   <item>default: 200 + <c>Content-Type: application/x-mpegurl</c> +
///         corpo <c>#EXTM3U\n</c> (classifica como Working em
///         <c>ProbeOnceAsync</c>);</item>
///   <item>status configurável por channel id (último segmento do path, sem
///         extensão): ex. id <c>404</c> → 404 terminal, id <c>503</c> → 503
///         retryable;</item>
///   <item>regista cada request como <c>(RawUrl, PathAndQuery)</c> numa lista
///         thread-safe.</item>
/// </list>
/// </para>
///
/// <para>
/// Suporta múltiplas instâncias em portas distintas (prova de "provider
/// diferente"). <see cref="Start"/> tenta re-bind numa porta livre em caso de
/// colisão (corrida entre o <c>TcpListener(0)</c> de descoberta e o
/// <c>HttpListener</c> de bind).
/// </para>
/// </summary>
internal sealed class ScriptedXtreamServer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly IReadOnlyDictionary<string, int> _statusById;

    private HttpListener _listener = new();
    private int _port;

    public ScriptedXtreamServer(IReadOnlyDictionary<string, int>? statusById = null)
    {
        _statusById = statusById ?? new Dictionary<string, int>(StringComparer.Ordinal);
        _port = GetFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
    }

    public int Port => _port;

    public string BaseUrl => $"http://127.0.0.1:{_port}";

    /// <summary>Snapshot ordenado dos requests recebidos.</summary>
    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    /// <summary>Contador de requests recebidos (thread-safe).</summary>
    public int RequestCount => _requests.Count;

    public void Start()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                _listener.Start();
                break;
            }
            catch (HttpListenerException)
            {
                if (attempt >= 5) throw;
                try { _listener.Close(); } catch { /* rebind */ }
                _port = GetFreePort();
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            }
        }

        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var rawUrl = ctx.Request.RawUrl ?? string.Empty;
            var pathAndQuery = ctx.Request.Url?.PathAndQuery ?? string.Empty;
            _requests.Enqueue(new RecordedRequest(rawUrl, pathAndQuery));

            var id = ExtractChannelId(ctx.Request.Url?.AbsolutePath ?? string.Empty);
            var status = id is not null && _statusById.TryGetValue(id, out var configured)
                ? configured
                : 200;

            ctx.Response.StatusCode = status;
            if (status == 200)
            {
                ctx.Response.ContentType = "application/x-mpegurl";
            }

            var body = Encoding.UTF8.GetBytes(status == 200 ? "#EXTM3U\n" : "#fail\n");
            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Último segmento do path sem extensão. <c>/live/u/p/7.ts</c> → <c>7</c>;
    /// <c>/u/p/4242</c> → <c>4242</c>. <c>null</c> quando o path não tem
    /// segmento final.
    /// </summary>
    public static string? ExtractChannelId(string absolutePath)
    {
        var idx = absolutePath.LastIndexOf('/');
        if (idx < 0 || idx == absolutePath.Length - 1) return null;
        var last = absolutePath[(idx + 1)..];
        var dot = last.IndexOf('.');
        return dot >= 0 ? last[..dot] : last;
    }

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        _cts.Dispose();
    }
}

internal sealed record RecordedRequest(string RawUrl, string PathAndQuery);
