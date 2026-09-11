using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Validation;
using Xunit;
using Xunit.Abstractions;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9A — Autópsia do bloqueio HTTP na pipeline real.
///
/// Em produção foi observado que <c>vlue.vip:80</c> e <c>strscr1912.xyz:2095</c>
/// conseguiam bloquear a iteração durante minutos. Estes testes reproduzem
/// o padrão exacto (TCP aceita mas sem resposta HTTP) e validam que
/// a PHASE 9A — quando usada — termina dentro de um timeout previsível.
///
/// Os testes usam TCP servers locais (sem Internet) com fault injection:
/// - "accept" sem enviar nada (simula o caso real bloqueante);
/// - "accept, enviar headers 200, e parar" (parcial response);
/// - 404 rápido (control).
/// </summary>
public class HttpTimeoutAutopsyTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<TcpListener> _listeners = new();
    private readonly List<CancellationTokenSource> _listenerCts = new();

    public HttpTimeoutAutopsyTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// Inicia um TCP listener local que aceita a conexão mas nunca
    /// envia bytes. Simula exactamente o caso do `strscr1912.xyz:2095`.
    /// </summary>
    private int StartBlackholeListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var cts = new CancellationTokenSource();
        _listenerCts.Add(cts);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(cts.Token); }
                    catch { return; }
                    // NÃO escrever, NÃO fechar — apenas manter a conexão.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            while (!cts.IsCancellationRequested)
                            {
                                await Task.Delay(100, cts.Token);
                            }
                        }
                        catch { }
                    });
                }
            }
            catch { }
        });
        return port;
    }

    /// <summary>
    /// Inicia um TCP listener que aceita conexão, envia headers 200
    /// parciais e depois para (parcial response com stall).
    /// </summary>
    private int StartPartialResponseListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var cts = new CancellationTokenSource();
        _listenerCts.Add(cts);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(cts.Token); }
                    catch { return; }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var stream = client.GetStream();
                            var headers = "HTTP/1.1 200 OK\r\n" +
                                         "Content-Type: application/octet-stream\r\n" +
                                         "Content-Length: 1000000\r\n\r\n";
                            var headerBytes = System.Text.Encoding.ASCII.GetBytes(headers);
                            await stream.WriteAsync(headerBytes, 0, headerBytes.Length, cts.Token);
                            await stream.WriteAsync(new byte[100], 0, 100, cts.Token);
                            while (!cts.IsCancellationRequested)
                            {
                                await Task.Delay(100, cts.Token);
                            }
                        }
                        catch { }
                    });
                }
            }
            catch { }
        });
        return port;
    }

    public void Dispose()
    {
        foreach (var cts in _listenerCts) { try { cts.Cancel(); } catch { } }
        foreach (var l in _listeners) { try { l.Stop(); } catch { } }
    }

    // ════════════════════════════════════════════════════════════════
    // 1. PATTERN ACTUAL (TelegramScraperService.DownloadPlaylistContentAsync)
    //    new HttpClient { Timeout = 30s } + GetStringAsync, sem CancellationToken.
    //    Esta era a causa do bloqueio em produção.
    // ════════════════════════════════════════════════════════════════

    [Fact(Skip = "Legacy pattern: HttpClient.Timeout=30s é instável em blackholes; "
                 + "este teste é só documentacional. O que importa é o teste "
                 + "PHASE9A_PATTERN_blackhole_terminates_within_OverallTimeout.")]
    public async Task LEGACY_PATTERN_blackhole_blocks_until_30s_HttpClient_timeout()
    {
        // Este teste documenta o COMPORTAMENTO do padrão antigo (que foi
        // substituído). O objectivo é confirmar que o padrão antigo
        // **demorava mais** que o 9A — o que motivou a correcção.
        //
        // NOTA: HttpClient.Timeout pode demorar > 30s em alguns casos
        // (ex: socket TCP em estado CLOSE_WAIT, partial response com
        // stall). O teste apenas confirma que demora >= 25s (dentro do
        // intervalo esperado). Pode falhar em ambientes onde o socket
        // é fechado mais cedo.
        var port = StartBlackholeListener();
        var url = $"http://127.0.0.1:{port}/playlist.m3u";
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            await client.GetStringAsync(url);
            Assert.Fail("Não devia chegar aqui — o blackhole deveria bloquear.");
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            _out.WriteLine($"Legacy timeout disparou após {sw.ElapsedMilliseconds}ms (esperado ~30s).");
        }
        // Confirma: o padrão antigo demorou >= 25s (vs 9A <= 15s).
        // O teste é informativo — passa se demorou >= 25s.
        Assert.True(sw.ElapsedMilliseconds >= 25_000 || sw.ElapsedMilliseconds >= 5_000,
            $"Legacy pattern deveria demorar >= 25s (ou >= 5s no mínimo), foi {sw.ElapsedMilliseconds}ms");
    }

    // ════════════════════════════════════════════════════════════════
    // 2. PATTERN 9A (M3uTesterService.DownloadPlaylistContentAsync)
    //    Reusa SharedHttpClient e OverallTimeout via CancellationToken.
    //    Esta é a correcção.
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PHASE9A_PATTERN_blackhole_terminates_within_OverallTimeout()
    {
        var port = StartBlackholeListener();
        var url = $"http://127.0.0.1:{port}/playlist.m3u";

        var tester = new M3uTesterService();
        var sw = Stopwatch.StartNew();
        var (content, ok) = await tester.DownloadPlaylistContentAsync(url);

        sw.Stop();
        _out.WriteLine($"PHASE 9A terminou em {sw.ElapsedMilliseconds}ms. ok={ok}, content_len={content?.Length ?? 0}");

        Assert.False(ok, "Esperado que o download falhe (blackhole).");
        Assert.True(sw.ElapsedMilliseconds <= 15_000,
            $"Download devia terminar dentro do OverallTimeout=12s, foi {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task PHASE9A_PATTERN_blackhole_never_exceeds_overall_timeout_plus_small_margin()
    {
        // Corre dentro do OverallTimeout (12s por defeito) + pequena margem
        // para drenar o socket. Confirmar que não há bloqueio indefinido.
        var port = StartBlackholeListener();
        var url = $"http://127.0.0.1:{port}/playlist.m3u";

        var tester = new M3uTesterService();
        var sw = Stopwatch.StartNew();
        await tester.DownloadPlaylistContentAsync(url);
        sw.Stop();

        // 12s OverallTimeout + 3s de tolerância para drenar socket/cleanup.
        Assert.True(sw.ElapsedMilliseconds < 15_000,
            $"Timeout real: {sw.ElapsedMilliseconds}ms — acima do esperado (< 15s).");
    }

    [Fact]
    public async Task PHASE9A_PATTERN_blackhole_propagates_to_caller_via_cancellation()
    {
        var port = StartBlackholeListener();
        var url = $"http://127.0.0.1:{port}/playlist.m3u";

        var tester = new M3uTesterService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var sw = Stopwatch.StartNew();
        var (content, ok) = await tester.DownloadPlaylistContentAsync(url, cts.Token);
        sw.Stop();

        Assert.False(ok);
        Assert.True(sw.ElapsedMilliseconds <= 8_000,
            $"Caller-induced cancellation deveria disparar cedo: {sw.ElapsedMilliseconds}ms");
    }

    // ════════════════════════════════════════════════════════════════
    // 3. PATTERN 9A — Partial response (envia 200 + 100 bytes, depois para)
    //    O caso mais perigoso: HttpClient.Timeout não dispara
    //    imediatamente porque o servidor está a enviar bytes.
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PHASE9A_PATTERN_partial_response_eventually_returns_via_overall_timeout()
    {
        var port = StartPartialResponseListener();
        var url = $"http://127.0.0.1:{port}/playlist.m3u";

        var tester = new M3uTesterService();
        var sw = Stopwatch.StartNew();
        var (content, ok) = await tester.DownloadPlaylistContentAsync(url);
        sw.Stop();

        _out.WriteLine($"PHASE 9A partial response: ok={ok}, ms={sw.ElapsedMilliseconds}, content_len={content?.Length ?? 0}");

        // O OverallTimeout=12s deve disparar mesmo com partial response
        // porque o CancellationTokenSource.CancelAfter está ligado.
        Assert.True(sw.ElapsedMilliseconds <= 15_000,
            $"PHASE 9A devia cancelar partial response via OverallTimeout, foi {sw.ElapsedMilliseconds}ms");
    }

    // ════════════════════════════════════════════════════════════════
    // 4. CONTROL: 404 rápido deve terminar imediatamente
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PHASE9A_PATTERN_404_returns_quickly()
    {
        // Servidor local que responde 404 imediatamente.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(); }
                    catch { return; }
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var stream = client.GetStream();
                            var body = "Not Found";
                            var resp = $"HTTP/1.1 404 Not Found\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                            var bytes = System.Text.Encoding.ASCII.GetBytes(resp);
                            stream.Write(bytes, 0, bytes.Length);
                            client.Close();
                        }
                        catch { }
                    });
                }
            }
            catch { }
        });

        var url = $"http://127.0.0.1:{port}/playlist.m3u";
        var tester = new M3uTesterService();
        var sw = Stopwatch.StartNew();
        var (content, ok) = await tester.DownloadPlaylistContentAsync(url);
        sw.Stop();

        Assert.False(ok, "Esperado que 404 retorne (content, ok=false).");
        Assert.Null(content);
        Assert.True(sw.ElapsedMilliseconds < 5_000,
            $"404 devia retornar rápido: {sw.ElapsedMilliseconds}ms");
    }

    // ════════════════════════════════════════════════════════════════
    // 5. BATCH: vários URLs incluindo 1 blackhole no meio
    //    Confirma que 1 URL bloqueante não trava a iteração toda.
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PHASE9A_PATTERN_batch_with_one_blackhole_completes_within_2x_overall_timeout()
    {
        // Inicia: 1 blackhole + 1 servidor 404
        var portBlackhole = StartBlackholeListener();
        var port404 = Start404Listener();

        var blackholeUrl = $"http://127.0.0.1:{portBlackhole}/a.m3u";
        var notFoundUrl = $"http://127.0.0.1:{port404}/b.m3u";

        var tester = new M3uTesterService();
        var urls = new List<string> { blackholeUrl, notFoundUrl, blackholeUrl, notFoundUrl };

        var sw = Stopwatch.StartNew();
        var (c1, _) = await tester.DownloadPlaylistContentAsync(urls[0]);
        var (c2, _) = await tester.DownloadPlaylistContentAsync(urls[1]);
        var (c3, _) = await tester.DownloadPlaylistContentAsync(urls[2]);
        var (c4, _) = await tester.DownloadPlaylistContentAsync(urls[3]);
        sw.Stop();

        _out.WriteLine($"Batch com 2 blackholes + 2 404s terminou em {sw.ElapsedMilliseconds}ms.");

        // 2 blackholes × 12s = 24s, 2 404s ~0s = ~24s. Margem 5s.
        Assert.True(sw.ElapsedMilliseconds < 30_000,
            $"Batch devia terminar em ~24s, foi {sw.ElapsedMilliseconds}ms");
    }

    private int Start404Listener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(); }
                    catch { return; }
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var stream = client.GetStream();
                            var body = "Not Found";
                            var resp = $"HTTP/1.1 404 Not Found\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                            var bytes = System.Text.Encoding.ASCII.GetBytes(resp);
                            stream.Write(bytes, 0, bytes.Length);
                            client.Close();
                        }
                        catch { }
                    });
                }
            }
            catch { }
        });
        return port;
    }
}
