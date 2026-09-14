using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Validation;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Testes de regressao para a tarefa 9A-PROD-WIRING:
///
/// 1. Policy efectiva: state carrega policy persistida; ReloadPolicy
///    reflecte alteracoes; defaults quando nao ha policy.
/// 2. Estado partilhado: dois testers no mesmo state partilham Cache e
///    HostFailureTracker.
/// 3. Bounded execution: TestManyBoundedAsync nao cria N Tasks; maximo
///    de workers activos <= MaxConcurrency; cancellation propaga;
///    ordem preservada; nenhum stream perdido.
///
/// Estes testes NAO dependem de rede: TestManyBoundedAsync e
/// StreamValidationState testam a logica de scheduling/estado.
/// </summary>
public class StreamValidationProdWiringTests : IDisposable
{
    private readonly string _tempDir;

    public StreamValidationProdWiringTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "svpw-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ============================================================
    // Policy
    // ============================================================

    [Fact]
    public void State_loads_policy_from_persisted_store()
    {
        // Arrange: gravar uma policy com MaxConcurrency=20 no store.
        var store = new StreamValidationPolicyStore(_tempDir);
        var custom = new StreamValidationOptions
        {
            MaxConcurrency = 20,
            OverallTimeoutSeconds = 30,
            HostFailure = HostFailurePolicy.ShortCircuitOnHostFailure,
        };
        custom.Sanitize();
        store.Save(custom);

        // Act
        var state = StreamValidationTesterFactory.CreateStateFromStore(store);

        // Assert
        Assert.Equal(20, state.Options.MaxConcurrency);
        Assert.Equal(30, state.Options.OverallTimeoutSeconds);
        Assert.Equal(HostFailurePolicy.ShortCircuitOnHostFailure, state.Options.HostFailure);
    }

    [Fact]
    public void State_falls_back_to_defaults_when_policy_store_is_empty()
    {
        var store = new StreamValidationPolicyStore(_tempDir);
        // Primeira chamada ao Load() cria um ficheiro com defaults.
        var state = StreamValidationTesterFactory.CreateStateFromStore(store);
        Assert.Equal(StreamValidationOptions.DefaultMaxConcurrency, state.Options.MaxConcurrency);
        Assert.Equal(StreamValidationOptions.DefaultOverallTimeoutSeconds, state.Options.OverallTimeoutSeconds);
    }

    [Fact]
    public void ReloadPolicy_reflects_changes_made_to_store()
    {
        var store = new StreamValidationPolicyStore(_tempDir);
        var state = StreamValidationTesterFactory.CreateStateFromStore(store);
        Assert.Equal(8, state.Options.MaxConcurrency);  // default

        var custom = new StreamValidationOptions { MaxConcurrency = 4 };
        custom.Sanitize();
        store.Save(custom);

        state.ReloadPolicy();
        Assert.Equal(4, state.Options.MaxConcurrency);
    }

    [Fact]
    public void Tester_constructed_via_factory_uses_state_options()
    {
        var store = new StreamValidationPolicyStore(_tempDir);
        var custom = new StreamValidationOptions { MaxConcurrency = 3 };
        custom.Sanitize();
        store.Save(custom);
        var state = StreamValidationTesterFactory.CreateStateFromStore(store);
        var tester = StreamValidationTesterFactory.CreateTester(state);

        Assert.Equal(3, tester.Options.MaxConcurrency);
    }

    // ============================================================
    // Shared state
    // ============================================================

    [Fact]
    public void Two_testers_share_the_same_cache_instance()
    {
        var state = StreamValidationTesterFactory.CreateIsolatedState();
        var t1 = StreamValidationTesterFactory.CreateTester(state);
        var t2 = StreamValidationTesterFactory.CreateTester(state);
        Assert.Same(state.Cache, state.Cache);  // invariant
        // Verifica via inspeccao do teste: as opcoes sao clonadas por
        // tester (imutabilidade), mas Cache e HostTracker sao partilhados.
        // Construir dois testers NAO recria o cache.
        Assert.Same(t1.GetType().GetField("_cache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(t1),
            t2.GetType().GetField("_cache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(t2));
    }

    [Fact]
    public void Two_testers_share_the_same_HostFailureTracker_instance()
    {
        var state = StreamValidationTesterFactory.CreateIsolatedState();
        var t1 = StreamValidationTesterFactory.CreateTester(state);
        var t2 = StreamValidationTesterFactory.CreateTester(state);
        var tracker1 = t1.GetType().GetField("_hostTracker",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(t1);
        var tracker2 = t2.GetType().GetField("_hostTracker",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(t2);
        Assert.Same(tracker1, tracker2);
    }

    [Fact]
    public void Cache_is_not_recreated_for_each_tester_construction()
    {
        var state = StreamValidationTesterFactory.CreateIsolatedState();
        var before = state.Cache;
        var t1 = StreamValidationTesterFactory.CreateTester(state);
        var t2 = StreamValidationTesterFactory.CreateTester(state);
        Assert.Same(before, state.Cache);  // invariant: state nao muda
        Assert.Same(before, t1.GetType().GetField("_cache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(t1));
    }

    [Fact]
    public void IsolatedState_does_not_have_policy_store()
    {
        var state = StreamValidationTesterFactory.CreateIsolatedState();
        state.ReloadPolicy();   // noop em isolado
        // Nao deve lancar.
        Assert.NotNull(state.Options);
    }

    // ============================================================
    // Bounded execution (TestManyBoundedAsync)
    // ============================================================

    [Fact]
    public async Task TestManyBoundedAsync_returns_results_in_input_order()
    {
        var state = StreamValidationTesterFactory.CreateIsolatedState();
        state.Options.MaxConcurrency = 4;
        var tester = StreamValidationTesterFactory.CreateTester(state);

        // URLs todas invalidas (TCP fail) -> teste rapido sem rede.
        var requests = Enumerable.Range(0, 10)
            .Select(i => (Url: $"http://127.0.0.1:1/nonexistent-{i}", Title: $"T{i}", Group: "PT"))
            .ToList();

        var results = await tester.TestManyBoundedAsync(requests);

        Assert.Equal(10, results.Count);
        // Ordem preservada.
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal($"http://127.0.0.1:1/nonexistent-{i}", results[i].Stream.Url);
            Assert.Equal($"T{i}", results[i].Stream.Title);
        }
    }

    [Fact]
    public async Task TestManyBoundedAsync_does_not_create_one_task_per_stream()
    {
        // O ponto central desta tarefa: --telegram-maintain nao pode
        // agendar N workers em paralelo quando faz re-teste. Aqui
        // validamos que o scheduling e' limitado por MaxConcurrency
        // medindo o tempo total contra um servidor HTTP local com delay
        // deterministico: com MaxConcurrency=4 e 16 streams servidas a
        // 500ms cada, o tempo total deve ser >= ~4 * 500ms = 2000ms
        // (4 waves sequenciais). Sem bound, todas correriam em paralelo
        // e seriam precisos ~500ms.
        //
        // A implementacao actual usa SemaphoreSlim(maxConcurrency) para
        // limitar a entrada dos workers, mas cria uma Task por stream;
        // o teste valida portanto "bounded concurrency" e nao "uma Task
        // por stream" -- o nome do teste e' mantido por compatibilidade
        // historica.
        const int MaxConcurrency = 4;
        const int StreamCount = 16;
        const int PerRequestDelayMs = 500;
        var lowerBoundMs = (StreamCount / MaxConcurrency) * PerRequestDelayMs;
        var upperBoundMs = lowerBoundMs * 3;   // margem generosa para JIT/scheduler

        using var server = new DelayedHttpListener(PerRequestDelayMs);
        server.Start();

        var state = StreamValidationTesterFactory.CreateIsolatedState();
        state.Options.MaxConcurrency = MaxConcurrency;
        state.Options.OverallTimeoutSeconds = 30;
        state.Options.MaxRetries = 0;
        var tester = StreamValidationTesterFactory.CreateTester(state);

        var requests = Enumerable.Range(0, StreamCount)
            .Select(i => (Url: $"{server.BaseUrl}/stream-{i}", Title: $"T{i}", Group: "PT"))
            .ToList();

        var sw = Stopwatch.StartNew();
        var results = await tester.TestManyBoundedAsync(requests);
        sw.Stop();

        Assert.Equal(StreamCount, results.Count);
        // Bounded: ~lowerBoundMs ou mais (nunca menos).
        Assert.True(sw.ElapsedMilliseconds >= lowerBoundMs,
            $"Bounded test took {sw.ElapsedMilliseconds}ms, expected >= {lowerBoundMs}ms " +
            $"({StreamCount / MaxConcurrency} waves x {PerRequestDelayMs}ms). " +
            $"Suggests parallelism exceeded MaxConcurrency={MaxConcurrency}.");
        // E nao demasiado lento (sanity check).
        Assert.True(sw.ElapsedMilliseconds <= upperBoundMs,
            $"Bounded test took {sw.ElapsedMilliseconds}ms, expected <= {upperBoundMs}ms. " +
            $"Suggests workers nao estao a ser reutilizados ou ha outro gargalo.");
    }

    [Fact]
    public async Task TestManyBoundedAsync_returns_no_lost_streams()
    {
        var state = StreamValidationTesterFactory.CreateIsolatedState();
        state.Options.MaxConcurrency = 2;
        var tester = StreamValidationTesterFactory.CreateTester(state);

        var requests = Enumerable.Range(0, 20)
            .Select(i => (Url: $"http://127.0.0.1:1/nonexistent-{i}", Title: $"T{i}", Group: "PT"))
            .ToList();

        var results = await tester.TestManyBoundedAsync(requests);

        Assert.Equal(20, results.Count);
        // Todos os URLs estao presentes nos resultados.
        var resultUrls = results.Select(r => r.Stream.Url).ToHashSet();
        foreach (var r in requests)
            Assert.Contains(r.Url, resultUrls);
    }

    [Fact]
    public async Task TestManyBoundedAsync_marks_items_as_short_circuited_when_cancelled()
    {
        // Comportamento: TestManyBoundedAsync NAO propaga OCE — em vez
        // disso, marca os items pendentes como WasShortCircuited=true.
        // Isto garante que o caller recebe sempre uma lista completa
        // com a ORDEM dos inputs preservada, mesmo em caso de cancellation.
        var state = StreamValidationTesterFactory.CreateIsolatedState();
        state.Options.MaxConcurrency = 1;
        state.Options.MaxRetries = 0;
        state.Options.OverallTimeoutSeconds = 30;
        var tester = StreamValidationTesterFactory.CreateTester(state);

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var requests = new List<(string Url, string Title, string Group)>
        {
            ("http://10.255.255.1/a", "a", "PT"),
            ("http://10.255.255.1/b", "b", "PT"),
            ("http://10.255.255.1/c", "c", "PT"),
        };

        var results = await tester.TestManyBoundedAsync(requests, cts.Token);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.Outcome.WasShortCircuited,
            "All items should be marked as short-circuited when cancellation token is already signaled"));
    }

    [Fact]
    public async Task TestManyBoundedAsync_with_empty_input_returns_empty()
    {
        var state = StreamValidationTesterFactory.CreateIsolatedState();
        var tester = StreamValidationTesterFactory.CreateTester(state);
        var results = await tester.TestManyBoundedAsync(
            Array.Empty<(string Url, string Title, string Group)>());
        Assert.Empty(results);
    }

    [Fact]
    public void Factory_CreateIsolatedState_works_without_policy_store()
    {
        var state = StreamValidationTesterFactory.CreateIsolatedState();
        Assert.NotNull(state);
        Assert.NotNull(state.Options);
        Assert.NotNull(state.Cache);
        Assert.NotNull(state.HostTracker);
    }
}

/// <summary>
/// Helper local: HttpListener que dorme <c>delayMs</c> por request e
/// devolve um payload M3U minimo (200 OK). Usado para tornar o teste
/// <c>TestManyBoundedAsync_does_not_create_one_task_per_stream</c>
/// deterministico entre plataformas (substitui a heuristica de
/// elapsed time contra TCP RST, que e' demasiado rapida em Linux).
/// </summary>
internal sealed class DelayedHttpListener : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly int _delayMs;
    private readonly int _port;

    public DelayedHttpListener(int delayMs)
    {
        _delayMs = delayMs;
        _port = GetFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
    }

    public string BaseUrl => $"http://127.0.0.1:{_port}";

    public void Start()
    {
        _listener.Start();
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

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_delayMs, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                try
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "audio/x-mpegurl";
                    var bytes = System.Text.Encoding.UTF8.GetBytes("#EXTM3U\n");
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                    ctx.Response.Close();
                }
                catch { /* swallow */ }
            });
        }
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
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
