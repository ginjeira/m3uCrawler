using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services.Validation;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9A.3 (2026-09-16): testes do <see cref="AccountGateCoordinator"/>.
///
/// O coordinator tem lifetime de RUN e e' partilhado por todos os candidate
/// workers. Garante:
///   - 1 operacao in-flight por AccountId (mesmo entre "workers" distintos);
///   - AccountIds diferentes em paralelo;
///   - limite GLOBAL de MaxConcurrentAccounts accounts;
///   - libertacao de gates/slots em finally (sucesso, falha ou cancelamento).
///
/// Nao duplica os testes do <c>AccountBoundedWorkerPool</c> (Phase 9A.1):
/// estes testam o coordinator, que substitui o pool local da 9A.2 no
/// caminho Telegram.
/// </summary>
[Collection("Phase93Concurrency")]
[CollectionDefinition("Phase93Concurrency", DisableParallelization = true)]
public class Phase93AccountGateCoordinatorTests
{
    /// <summary>
    /// Regressao explicita do bug da Phase 9A.2: dois "candidate workers"
    /// (duas invocacoes concorrentes) com o MESMO AccountId. O segundo nao
    /// pode entrar na operacao enquanto o primeiro esta' em execucao.
    /// </summary>
    [Fact]
    public async Task Two_candidate_workers_same_account_never_overlap()
    {
        using var coordinator = new AccountGateCoordinator(4);

        var inFlight = 0;
        var maxInFlight = 0;
        var entered = 0;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Work(CancellationToken ct)
        {
            var current = Interlocked.Increment(ref inFlight);
            if (current > maxInFlight) Interlocked.Exchange(ref maxInFlight, current);
            if (Interlocked.Increment(ref entered) == 1)
            {
                firstEntered.TrySetResult();
                await release.Task.ConfigureAwait(false);
            }
            Interlocked.Decrement(ref inFlight);
            return 1;
        }

        var first = coordinator.RunExclusiveAsync("X", Work, default);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = coordinator.RunExclusiveAsync("X", Work, default);

        await Task.Delay(200);
        Assert.Equal(1, Volatile.Read(ref entered));

        release.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, maxInFlight);
        Assert.Equal(2, Volatile.Read(ref entered));
    }

    /// <summary>A) mesma conta em candidates diferentes: nunca paralelo.</summary>
    [Fact]
    public async Task A_same_account_never_has_two_operations_in_flight()
    {
        using var coordinator = new AccountGateCoordinator(8);

        var inFlight = 0;
        var maxInFlight = 0;

        async Task<int> Work(CancellationToken ct)
        {
            var current = Interlocked.Increment(ref inFlight);
            if (current > maxInFlight) Interlocked.Exchange(ref maxInFlight, current);
            try { await Task.Delay(15, ct).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref inFlight); }
            return 1;
        }

        var works = Enumerable.Range(0, 40)
            .Select(_ => coordinator.RunExclusiveAsync("X", Work, default))
            .ToArray();

        await Task.WhenAll(works);
        Assert.Equal(1, maxInFlight);
    }

    /// <summary>B) contas diferentes: podem executar em paralelo.</summary>
    [Fact]
    public async Task B_different_accounts_run_in_parallel()
    {
        using var coordinator = new AccountGateCoordinator(4);

        var totalInFlight = 0;
        var maxTotalInFlight = 0;

        async Task<int> Work(CancellationToken ct)
        {
            var current = Interlocked.Increment(ref totalInFlight);
            if (current > maxTotalInFlight) Interlocked.Exchange(ref maxTotalInFlight, current);
            try { await Task.Delay(60, ct).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref totalInFlight); }
            return 1;
        }

        var works = new[]
        {
            coordinator.RunExclusiveAsync("A", Work, default),
            coordinator.RunExclusiveAsync("B", Work, default),
            coordinator.RunExclusiveAsync("C", Work, default),
            coordinator.RunExclusiveAsync("D", Work, default),
        };

        await Task.WhenAll(works);
        Assert.True(maxTotalInFlight >= 2,
            $"maxTotalInFlight={maxTotalInFlight}; expected >= 2");
    }

    /// <summary>C) MaxConcurrentAccounts e' um limite GLOBAL.</summary>
    [Fact]
    public async Task C_max_concurrent_accounts_is_global()
    {
        const int limit = 2;
        using var coordinator = new AccountGateCoordinator(limit);

        var totalInFlight = 0;
        var maxTotalInFlight = 0;

        async Task<int> Work(CancellationToken ct)
        {
            var current = Interlocked.Increment(ref totalInFlight);
            if (current > maxTotalInFlight) Interlocked.Exchange(ref maxTotalInFlight, current);
            try { await Task.Delay(60, ct).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref totalInFlight); }
            return 1;
        }

        var works = Enumerable.Range(0, 8)
            .Select(i => coordinator.RunExclusiveAsync($"A{i}", Work, default))
            .ToArray();

        await Task.WhenAll(works);
        Assert.True(maxTotalInFlight <= limit,
            $"maxTotalInFlight={maxTotalInFlight}; expected <= {limit}");
    }

    /// <summary>D) excepcao numa conta nao bloqueia as outras.</summary>
    [Fact]
    public async Task D_exception_in_one_account_does_not_block_others()
    {
        using var coordinator = new AccountGateCoordinator(4);

        var failing = coordinator.RunExclusiveAsync<int>(
            "A", (ct) => throw new InvalidOperationException("boom"), default);
        var okB = coordinator.RunExclusiveAsync("B", (ct) => Task.FromResult(1), default);
        var okC = coordinator.RunExclusiveAsync("C", (ct) => Task.FromResult(2), default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing);
        Assert.Equal(1, await okB);
        Assert.Equal(2, await okC);

        // O gate da conta A ficou reutilizavel.
        Assert.Equal(7, await coordinator.RunExclusiveAsync("A", (ct) => Task.FromResult(7), default));
        Assert.Equal(0, coordinator.ActiveGateCount);
    }

    /// <summary>
    /// E) cancelamento nao deixa gates nem slots presos.
    /// </summary>
    [Fact]
    public async Task E_cancellation_does_not_leave_gates_stuck()
    {
        // (i) cancelamento à espera do slot GLOBAL.
        using (var coordinator = new AccountGateCoordinator(1))
        {
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = coordinator.RunExclusiveAsync("X", async (ct) =>
            {
                entered.TrySetResult();
                await hold.Task.ConfigureAwait(false);
                return 1;
            }, default);

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => coordinator.RunExclusiveAsync("Y", (ct) => Task.FromResult(1), cts.Token));

            hold.TrySetResult();
            Assert.Equal(1, await holder);

            // O slot global foi libertado.
            Assert.Equal(1, await coordinator.RunExclusiveAsync("Z", (ct) => Task.FromResult(1), default));
            Assert.Equal(0, coordinator.ActiveGateCount);
        }

        // (ii) cancelamento à espera do gate da ACCOUNT.
        using (var coordinator = new AccountGateCoordinator(4))
        {
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = coordinator.RunExclusiveAsync("X", async (ct) =>
            {
                entered.TrySetResult();
                await hold.Task.ConfigureAwait(false);
                return 1;
            }, default);

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => coordinator.RunExclusiveAsync("X", (ct) => Task.FromResult(1), cts.Token));

            hold.TrySetResult();
            Assert.Equal(1, await holder);

            // O gate da conta X foi libertado.
            Assert.Equal(1, await coordinator.RunExclusiveAsync("X", (ct) => Task.FromResult(1), default));
            Assert.Equal(0, coordinator.ActiveGateCount);
        }
    }

    /// <summary>
    /// F) depois de sucesso, falha ou cancelamento, o mesmo AccountId
    /// volta a adquirir o gate.
    /// </summary>
    [Fact]
    public async Task F_same_account_reacquires_gate_after_success_failure_and_cancellation()
    {
        using var coordinator = new AccountGateCoordinator(2);
        var account = "X";

        Assert.Equal(1, await coordinator.RunExclusiveAsync(account, (ct) => Task.FromResult(1), default));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.RunExclusiveAsync<int>(account, (ct) => throw new InvalidOperationException("boom"), default));

        Assert.Equal(2, await coordinator.RunExclusiveAsync(account, (ct) => Task.FromResult(2), default));

        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                coordinator.RunExclusiveAsync(account, (ct) => Task.FromResult(3), cts.Token));
        }

        Assert.Equal(4, await coordinator.RunExclusiveAsync(account, (ct) => Task.FromResult(4), default));
        Assert.Equal(0, coordinator.ActiveGateCount);
    }

    /// <summary>G) cleanup dos gates no fim do run.</summary>
    [Fact]
    public async Task G_gates_are_cleaned_up_after_drain()
    {
        using var coordinator = new AccountGateCoordinator(3);

        var works = Enumerable.Range(0, 30)
            .Select(i => coordinator.RunExclusiveAsync(
                $"A{i % 5}", (ct) => Task.FromResult(i), default))
            .ToArray();

        await Task.WhenAll(works);
        Assert.Equal(0, coordinator.ActiveGateCount);
    }

    /// <summary>
    /// H) o coordinator limita a concorrencia mesmo com callers ilimitados,
    /// sem criar estado proporcional ao numero de work items.
    /// </summary>
    [Fact]
    public async Task H_bounds_concurrency_even_with_unbounded_callers()
    {
        const int limit = 4;
        const int items = 1000;
        using var coordinator = new AccountGateCoordinator(limit);

        var totalInFlight = 0;
        var maxTotalInFlight = 0;

        async Task<int> Work(CancellationToken ct)
        {
            var current = Interlocked.Increment(ref totalInFlight);
            if (current > maxTotalInFlight) Interlocked.Exchange(ref maxTotalInFlight, current);
            try { await Task.Yield(); }
            finally { Interlocked.Decrement(ref totalInFlight); }
            return 1;
        }

        var works = new Task<int>[items];
        for (var i = 0; i < items; i++)
        {
            works[i] = coordinator.RunExclusiveAsync($"A{i % 7}", Work, default);
        }

        await Task.WhenAll(works);

        Assert.True(maxTotalInFlight <= limit,
            $"maxTotalInFlight={maxTotalInFlight}; expected <= {limit}");
        Assert.Equal(0, coordinator.ActiveGateCount);
    }
}
