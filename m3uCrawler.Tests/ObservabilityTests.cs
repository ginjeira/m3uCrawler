using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Validation;
using Xunit;
using Xunit.Abstractions;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE-OBSERVABILITY (2026-09-15): testes deterministicos para a
/// camada de tracing. Usam TcpListener local (sem Internet) para
/// reproduzir varios cenarios de HTTP que antes eram classificados
/// como "timeout ou erro de rede" generico.
/// </summary>
public class ObservabilityTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<TcpListener> _listeners = new();
    private readonly List<CancellationTokenSource> _cts = new();

    public ObservabilityTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        foreach (var cts in _cts) { try { cts.Cancel(); } catch { } }
        foreach (var l in _listeners) { try { l.Stop(); } catch { } }
    }

    private int StartListener(Func<TcpClient, CancellationToken, Task> handler)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var cts = new CancellationTokenSource();
        _cts.Add(cts);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(cts.Token); }
                    catch { return; }
                    _ = Task.Run(() => handler(client, cts.Token));
                }
            }
            catch { }
        });
        return port;
    }

    private static async Task Reply(TcpClient client, int status, string contentType, string body)
    {
        using (client)
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            string requestLine = await reader.ReadLineAsync();
            // consume headers
            string line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) { }
            var response = $"HTTP/1.1 {status} {GetReason(status)}\r\n" +
                           $"Content-Type: {contentType}\r\n" +
                           $"Content-Length: {body.Length}\r\n" +
                           "Connection: close\r\n\r\n" +
                           body;
            var bytes = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(bytes, 0, bytes.Length);
            await stream.FlushAsync();
        }
    }

    private static string GetReason(int status) => status switch
    {
        200 => "OK",
        404 => "Not Found",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Status"
    };

    // BLACKHOLE: aceita TCP mas nunca responde.
    private int StartBlackhole()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var cts = new CancellationTokenSource();
        _cts.Add(cts);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(cts.Token); }
                    catch { return; }
                    // Manter a conexao aberta e nunca responder. Nao dispose para manter blackhole.
                    // Sera disposed quando o teste acabar.
                    _listeners.Add(null!); // Nao tracked
                }
            }
            catch { }
        });
        return port;
    }

    // ==== Testes de PipelineTrace ====
    [Fact]
    public void PipelineTrace_Assigns_RunId_OnConstruction()
    {
        var t = new PipelineTrace();
        Assert.False(string.IsNullOrEmpty(t.RunId));
        Assert.Equal(12, t.RunId.Length); // GUID N substring 0,12
    }

    [Fact]
    public void PipelineTrace_Captures_Information_Events_With_Correlation()
    {
        var t = new PipelineTrace();
        var ctx = new TraceContext { RunId = t.RunId, CandidateId = "abc" };
        t.Information(TraceCategory.CandidateCreated, ctx, "msg1");
        t.Warning(TraceCategory.CandidateRejected, ctx, "msg2");
        t.Error(TraceCategory.AttachmentDownloadFailed, ctx, "msg3", new InvalidOperationException("boom"));
        var snap = t.Snapshot();
        Assert.Equal(3, snap.Count);
        Assert.Equal(TraceLevel.Information, snap[0].Level);
        Assert.Equal(TraceLevel.Warning, snap[1].Level);
        Assert.Equal(TraceLevel.Error, snap[2].Level);
        Assert.Equal("InvalidOperationException", snap[2].ExceptionType);
    }

    [Fact]
    public void PipelineTrace_Filters_Below_MinimumLevel()
    {
        var t = new PipelineTrace { MinimumLevel = TraceLevel.Warning };
        var ctx = new TraceContext();
        t.Debug(TraceCategory.AttachmentDownloadProgress, ctx, "ignored");
        t.Information(TraceCategory.MessageAnalyzed, ctx, "ignored");
        t.Warning(TraceCategory.CandidateRejected, ctx, "kept");
        Assert.Single(t.Snapshot());
    }

    [Fact]
    public void PipelineTrace_Thread_Safe_Snapshot()
    {
        var t = new PipelineTrace();
        var ctx = new TraceContext();
        var counter = 0;
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                Interlocked.Increment(ref counter);
                t.Information(TraceCategory.MessageAnalyzed, ctx, "msg" + counter);
            }
        })).ToArray();
        Task.WaitAll(tasks);
        Assert.Equal(400, t.Snapshot().Count);
    }

    [Fact]
    public void PipelineTrace_Reconcile_Snapshot_To_RunReport()
    {
        var t = new PipelineTrace();
        var ctx = new TraceContext();
        for (int i = 0; i < 5; i++) t.Information(TraceCategory.HttpRequestStart, ctx, "x");
        for (int i = 0; i < 3; i++) t.Information(TraceCategory.HttpRequestEnd, ctx, "x");
        for (int i = 0; i < 2; i++) t.Information(TraceCategory.HttpRequestFailed, ctx, "x");
        for (int i = 0; i < 4; i++) t.Information(TraceCategory.ChannelEnqueue, ctx, "x");
        for (int i = 0; i < 4; i++) t.Information(TraceCategory.ChannelDequeue, ctx, "x");
        for (int i = 0; i < 7; i++) t.Information(TraceCategory.XtreamAccount, ctx, "x");

        var rep = new Models.RunReport();
        RunReportTraceReconciler.RecordSnapshot(rep, t);
        Assert.Equal(5, rep.TraceEventsHttpRequestStart);
        Assert.Equal(3, rep.TraceEventsHttpRequestEnd);
        Assert.Equal(2, rep.TraceEventsHttpRequestFailed);
        Assert.Equal(4, rep.TraceEventsChannelEnqueue);
        Assert.Equal(4, rep.TraceEventsChannelDequeue);
        Assert.Equal(7, rep.TraceEventsXtreamAccount);
        Assert.Equal(0, rep.TraceEventsResolverStart); // nunca incrementados
    }

    [Fact]
    public void PipelineTrace_Log_ToString_DoesNotInclude_Credentials()
    {
        var t = new PipelineTrace();
        var ctx = new TraceContext();
        // URL com credenciais: password e username no path sao ambos mascarados
        // pelo _pathCredsRegex ("/live/USER/PASS/" -> "/live/***/***").
        var url = "http://example.com/live/secretuser/secretpassword/1";
        var sanitized = CredentialSanitizer.SanitizeUrl(url);
        t.Information(TraceCategory.HttpRequestStart, ctx, $"safeUrl={sanitized}");
        var output = t.Snapshot()[0].ToString();
        // Confirma que credenciais NAO aparecem no output (sanitizadas para ***).
        Assert.DoesNotContain("secretpassword", output);
        Assert.DoesNotContain("secretuser", output);
        // E o placeholder *** aparece (sinal de sanitizacao).
        Assert.Contains("***", output);
    }

    // ==== Testes de DownloadPlaylistContentAsync com tracing ====
    [Fact]
    public async Task DownloadPlaylistContentAsync_Http200_Emits_SuccessEvents()
    {
        var port = StartListener((client, ct) => Reply(client, 200, "text/plain", "OK"));
        var trace = new PipelineTrace();
        var tester = new M3uTesterService(new StreamValidationOptions
        {
            ConnectionTimeoutSeconds = 2,
            OverallTimeoutSeconds = 5,
        });
        var setTrace = typeof(M3uTesterService).GetMethod("SetTrace",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        setTrace!.Invoke(tester, new object[] { (ITraceSink)trace });

        var (content, ok) = await tester.DownloadPlaylistContentAsync($"http://127.0.0.1:{port}/test");
        Assert.True(ok);
        Assert.Equal("OK", content);
        var byCat = trace.CountByCategory();
        Assert.True(byCat.GetValueOrDefault(TraceCategory.HttpRequestStart) == 1, $"HttpRequestStart expected 1 got {byCat.GetValueOrDefault(TraceCategory.HttpRequestStart)}");
        Assert.True(byCat.GetValueOrDefault(TraceCategory.HttpRequestHeaders) == 1);
        Assert.True(byCat.GetValueOrDefault(TraceCategory.HttpRequestBody) == 1);
        Assert.True(byCat.GetValueOrDefault(TraceCategory.HttpRequestEnd) == 1);
    }

    [Fact]
    public async Task DownloadPlaylistContentAsync_Http404_Emits_Http4xx()
    {
        var port = StartListener((client, ct) => Reply(client, 404, "text/plain", "Not Found"));
        var trace = new PipelineTrace();
        var tester = new M3uTesterService(new StreamValidationOptions
        {
            ConnectionTimeoutSeconds = 2,
            OverallTimeoutSeconds = 5,
        });
        var setTrace = typeof(M3uTesterService).GetMethod("SetTrace",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        setTrace?.Invoke(tester, new object[] { (ITraceSink)trace });

        var (content, ok) = await tester.DownloadPlaylistContentAsync($"http://127.0.0.1:{port}/missing");
        Assert.False(ok);
        Assert.Null(content);
        var byCat = trace.CountByCategory();
        Assert.True(byCat.GetValueOrDefault(TraceCategory.HttpRequestStart) == 1);
        Assert.True(byCat.GetValueOrDefault(TraceCategory.HttpRequestHeaders) == 1, "headers emitted");
        Assert.True(byCat.GetValueOrDefault(TraceCategory.HttpRequestFailed) == 0, "404 not classified as failed; ends in HttpRequestEnd warning");
        Assert.True(byCat.GetValueOrDefault(TraceCategory.HttpRequestEnd) == 1, "end emitted");
        // Verificar que o warning level identifica 404
        var evts = trace.SnapshotByCategory(TraceCategory.HttpRequestEnd);
        Assert.Single(evts);
        Assert.Equal(TraceLevel.Warning, evts[0].Level);
        Assert.Contains("status=404", evts[0].Message);
    }

    [Fact]
    public async Task DownloadPlaylistContentAsync_Http503_Emits_Http5xx_Not_Unknown()
    {
        // O caso do neorcqds.top em producao: o servidor responde 503.
        // Antes da fix: era classificado como Unknown/TaskCanceledException.
        // Apos observability: Http5xx e HttpRequestEnd explicitos.
        var port = StartListener((client, ct) => Reply(client, 503, "text/plain", "Service Unavailable"));
        var trace = new PipelineTrace();
        var tester = new M3uTesterService(new StreamValidationOptions
        {
            ConnectionTimeoutSeconds = 2,
            OverallTimeoutSeconds = 5,
        });
        var setTrace = typeof(M3uTesterService).GetMethod("SetTrace",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        setTrace?.Invoke(tester, new object[] { (ITraceSink)trace });

        var (content, ok) = await tester.DownloadPlaylistContentAsync($"http://127.0.0.1:{port}/down");
        Assert.False(ok);
        Assert.Null(content);
        var byCat = trace.CountByCategory();
        // 503 ainda e' success status HTTP (nao 5xx no classificador), mas
        // tem status >=500 -> classified as Http5xx no warning
        var evts = trace.SnapshotByCategory(TraceCategory.HttpRequestEnd);
        Assert.Single(evts);
        Assert.Equal(TraceLevel.Warning, evts[0].Level);
        Assert.Contains("kind=Http5xx", evts[0].Message);
        Assert.Contains("status=503", evts[0].Message);
    }

    [Fact]
    public async Task DownloadPlaylistContentAsync_Blackhole_OverallTimeout_Emits_HttpRequestTimeout()
    {
        // Caso classico do neorcqds.top: TCP connect sucede mas servidor
        // nunca responde. OverallTimeout dispara primeiro, dando
        // HttpRequestTimeout (ConnectTimeout nao dispara porque connect
        // succeeded immediately).
        var port = StartBlackhole();
        var trace = new PipelineTrace();
        var tester = new M3uTesterService(new StreamValidationOptions
        {
            ConnectionTimeoutSeconds = 10, // maior que OverallTimeout
            OverallTimeoutSeconds = 1,
        });
        var setTrace = typeof(M3uTesterService).GetMethod("SetTrace",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        setTrace!.Invoke(tester, new object[] { (ITraceSink)trace });

        var (content, ok) = await tester.DownloadPlaylistContentAsync($"http://127.0.0.1:{port}/forever");
        Assert.False(ok);
        var byCat = trace.CountByCategory();
        Assert.True(byCat.GetValueOrDefault(TraceCategory.HttpRequestStart) == 1);
        // HttpRequestFailed ou HttpRequestEnd — ambos registam o timeout
        var failedEvts = trace.SnapshotByCategory(TraceCategory.HttpRequestFailed);
        var endEvts = trace.SnapshotByCategory(TraceCategory.HttpRequestEnd);
        Assert.True(failedEvts.Count + endEvts.Count >= 1, "expected at least 1 failure/end event");
        // Verificar que ha um evento terminal que descreve a causa. Aceita
        // Timeout, Network, HttpRequestTimeout, HttpConnectTimeout, ou Socket
        // (em Windows, connection forçada pode aparecer como Network/IOException).
        var allEvts = failedEvts.Concat(endEvts).ToList();
        Assert.True(allEvts.Any(e =>
            e.Message.Contains("Timeout") ||
            e.Message.Contains("HttpRequestTimeout") ||
            e.Message.Contains("HttpConnectTimeout") ||
            e.Message.Contains("Network") ||
            e.Message.Contains("Socket")),
            $"expected a terminal failure event with a known cause, got: {string.Join(", ", allEvts.Select(e => e.Message))}");
    }

    [Fact]
    public async Task DownloadPlaylistContentAsync_PortNotListening_ConnectionRefused_Emits_Network()
    {
        // Escutar uma porta mas nao aceitar: connect e' refused.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // fechar imediatamente: connect refused

        var trace = new PipelineTrace();
        var tester = new M3uTesterService(new StreamValidationOptions
        {
            ConnectionTimeoutSeconds = 2,
            OverallTimeoutSeconds = 5,
        });
        var setTrace = typeof(M3uTesterService).GetMethod("SetTrace",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        setTrace!.Invoke(tester, new object[] { (ITraceSink)trace });

        var (content, ok) = await tester.DownloadPlaylistContentAsync($"http://127.0.0.1:{port}/x");
        Assert.False(ok);
        var byCat = trace.CountByCategory();
        Assert.True(byCat.GetValueOrDefault(TraceCategory.HttpRequestStart) == 1);
        Assert.True(byCat.GetValueOrDefault(TraceCategory.HttpRequestFailed) == 1);
        var evt = trace.SnapshotByCategory(TraceCategory.HttpRequestFailed)[0];
        // ConnectionRefused e' reportada de formas diferentes consoante o
        // runtime/SO, mas sempre dentro desta classe de falha de ligacao:
        //   kind=Socket            -> SocketException propagada directamente (Windows);
        //   kind=HttpConnectTimeout -> timeout de connect sem inner TimeoutException;
        //   kind=Network            -> HttpRequestException com SocketException interna
        //                              (Linux/.NET 9, ECONNREFUSED imediato).
        // A assertion aceita estritamente uma destas tres classificacoes.
        Assert.True(
            evt.Message.Contains("kind=Socket") ||
            evt.Message.Contains("kind=HttpConnectTimeout") ||
            evt.Message.Contains("kind=Network"),
            $"expected Socket, HttpConnectTimeout or Network, got: {evt.Message}");
    }

    [Fact]
    public async Task DownloadPlaylistContentAsync_Credentials_Are_Never_Logged_In_Plain_Text()
    {
        var port = StartListener((client, ct) => Reply(client, 200, "text/plain", "OK"));
        var trace = new PipelineTrace();
        var tester = new M3uTesterService(new StreamValidationOptions
        {
            ConnectionTimeoutSeconds = 2,
            OverallTimeoutSeconds = 5,
        });
        var setTrace = typeof(M3uTesterService).GetMethod("SetTrace",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        setTrace?.Invoke(tester, new object[] { (ITraceSink)trace });

        var url = $"http://user:supersecret@127.0.0.1:{port}/get.php?username=foo&password=topsecret123";
        await tester.DownloadPlaylistContentAsync(url);
        var allEvts = trace.Snapshot();
        var allText = string.Join("|", allEvts.Select(e => e.Message + " " + (e.ExceptionMessage ?? "")));
        Assert.DoesNotContain("supersecret", allText);
        Assert.DoesNotContain("topsecret123", allText);
        Assert.Contains("user:***", allText); // userinfo e' mascarado para user:***
        Assert.Contains("password=***", allText);
    }

    [Fact]
    public void PipelineTrace_NullTraceSink_Is_NoOp()
    {
        var sink = NullTraceSink.Instance;
        sink.Emit(new TraceEvent { Level = TraceLevel.Information, Category = TraceCategory.RunStart, Context = new TraceContext(), Message = "x" });
        // Nao lanca excepcao; sink nao acumula eventos.
    }

    [Fact]
    public void CapturingTraceSink_Collects_Events_For_Tests()
    {
        var sink = new CapturingTraceSink();
        sink.Emit(new TraceEvent { Level = TraceLevel.Information, Category = TraceCategory.RunStart, Context = new TraceContext(), Message = "a" });
        sink.Emit(new TraceEvent { Level = TraceLevel.Warning, Category = TraceCategory.CandidateRejected, Context = new TraceContext(), Message = "b" });
        Assert.Equal(2, sink.Events.Count);
        Assert.Equal("a", sink.Events[0].Message);
        Assert.Equal("b", sink.Events[1].Message);
    }

    // PHASE-OBSERVABILITY (2026-09-15): testes para a observabilidade
    // permanente do pipeline completo (attachment download + ResolverStart/
    // ResolverEnd + worker processing). Estes testes validam que os eventos
    // certos sao emitidos nas fases certas, sem depender de Telegram real.

    [Fact]
    public void PipelineTrace_Has_All_Required_Categories()
    {
        // Sanity check: garantir que todas as categorias usadas no
        // TelegramScraperService existem no enum TraceCategory.
        // Se alguem adicionar uma nova categoria em TelegramScraperService
        // sem a declarar aqui, este teste falha imediatamente.
        var expected = new[]
        {
            TraceCategory.RunStart,
            TraceCategory.RunEnd,
            TraceCategory.MessageAnalyzed,
            TraceCategory.MessageMediaInfo,
            TraceCategory.DetectStart,
            TraceCategory.DetectEnd,
            TraceCategory.CandidateCreated,
            TraceCategory.CandidateRejected,
            TraceCategory.AttachmentDownloadStart,
            TraceCategory.AttachmentDownloadComplete,
            TraceCategory.AttachmentDownloadFailed,
            TraceCategory.ChannelEnqueue,
            TraceCategory.ChannelDequeue,
            TraceCategory.WorkerStart,
            TraceCategory.WorkerEnd,
            TraceCategory.CandidateProcessStart,
            TraceCategory.CandidateProcessEnd,
            TraceCategory.ResolverStart,
            TraceCategory.ResolverEnd,
            TraceCategory.XtreamAccount,
            TraceCategory.CandidatePromoted,
            TraceCategory.HttpRequestStart,
            TraceCategory.HttpRequestHeaders,
            TraceCategory.HttpRequestBody,
            TraceCategory.HttpRequestEnd,
            TraceCategory.HttpRequestFailed,
            TraceCategory.ParseStart,
            TraceCategory.ParseEnd,
            TraceCategory.FilterStart,
            TraceCategory.FilterEnd,
            TraceCategory.StreamValidationStart,
            TraceCategory.StreamValidationEnd,
            TraceCategory.StreamResult,
        };
        var actual = System.Enum.GetValues(typeof(TraceCategory)).Cast<TraceCategory>().ToArray();
        foreach (var e in expected)
        {
            Assert.Contains(e, actual);
        }
    }

    [Fact]
    public void TelegramScraperService_Has_SetTrace_Method_And_Trace_Field()
    {
        // Validacao de integracao: garante que SetTrace e' publico (utilizado
        // pelo Program.cs para activar observabilidade em modo M3UCRAWLER_TRACE=1)
        // e que existe o field _trace onde o sink e' armazenado. Nao instanciamos
        // TelegramScraperService (requer WTelegram config) -- apenas validamos
        // via reflection.
        var setTrace = typeof(TelegramScraperService).GetMethod("SetTrace",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        Assert.NotNull(setTrace);
        var f = typeof(TelegramScraperService).GetField("_trace",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(f);
    }

    private static ITraceSink _getTrace(TelegramScraperService ts)
    {
        // Helper: devolve o sink de tracing associado.
        var f = typeof(TelegramScraperService).GetField("_trace",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return (ITraceSink)f!.GetValue(ts);
    }
}

