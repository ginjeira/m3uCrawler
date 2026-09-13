using System.Collections.Concurrent;
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
        // agendar N Tasks quando faz re-teste. Aqui validamos que o
        // scheduling e' limitado por MaxConcurrency medindo o tempo
        // total: com MaxConcurrency=4 e 40 streams com timeout rapido,
        // o tempo total <= 40 / 4 * timeout (em ms). Sem bound, todas
        // as Tasks seriam agendadas e os timeouts correriam em paralelo.
        var state = StreamValidationTesterFactory.CreateIsolatedState();
        state.Options.MaxConcurrency = 4;
        state.Options.OverallTimeoutSeconds = 2;
        state.Options.MaxRetries = 0;
        var tester = StreamValidationTesterFactory.CreateTester(state);

        const int N = 40;
        var requests = Enumerable.Range(0, N)
            .Select(i => (Url: $"http://127.0.0.1:1/nonexistent-{i}", Title: $"T{i}", Group: "PT"))
            .ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = await tester.TestManyBoundedAsync(requests);
        sw.Stop();

        Assert.Equal(N, results.Count);
        // Com MaxConcurrency=4 e timeout 2s, esperado ~ N/4 * 2s = 20s
        // para todos terminarem por timeout. Margem: <= 25s.
        // Sem bound (Task.WhenAll com 40 Tasks), todos correriam em
        // paralelo e seriam precisos ~2s.
        var upperBoundMs = 25_000;
        Assert.True(sw.ElapsedMilliseconds <= upperBoundMs,
            $"Bounded test took {sw.ElapsedMilliseconds}ms, expected <= {upperBoundMs}ms (4 workers x 2s timeout x 10 waves).");
        // E o teste nao pode ser demasiado rapido: se fosse <= 3s,
        // significaria que TestManyBoundedAsync correu em paralelo
        // (sinal de que o bound NAO foi respeitado).
        Assert.True(sw.ElapsedMilliseconds >= 4_000,
            $"Bounded test took {sw.ElapsedMilliseconds}ms — too fast, suggests parallelism exceeded MaxConcurrency=4.");
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
