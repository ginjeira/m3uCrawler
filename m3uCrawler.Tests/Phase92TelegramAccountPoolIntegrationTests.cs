using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Validation;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9A.3 (2026-09-16): integracao REAL do caminho Telegram com o
/// <see cref="AccountGateCoordinator"/>.
///
/// Estes testes conduzem o metodo real
/// <see cref="TelegramScraperService.TestStreamsAsync"/> (seam interno) com
/// um delegate de validacao sintetico (sem rede), mas com o coordinator REAL
/// partilhado. O delegate substitui apenas o HTTP (AccountValidator), nao o
/// scheduling por AccountId.
///
/// PHASE 9A.2 usava um pool local por chamada; estes testes demonstrariam o
/// bug (dois candidates da mesma conta em paralelo) se o coordinator nao
/// fosse partilhado.
/// </summary>
[Collection("Phase92Concurrency")]
[CollectionDefinition("Phase92Concurrency", DisableParallelization = true)]
public class Phase92TelegramAccountConcurrencyIntegrationTests
{
    private static M3uStream Stream(string url) =>
        new() { Url = url, Title = "Canal", Group = "PT" };

    private static Task<AccountValidationResult> AllWorkingAsync(
        AccountValidationWork work, CancellationToken ct)
    {
        var outcomes = work.Streams
            .Select(s => new StreamTestOutcome(
                s.Url, true, 200, StreamFailureKind.None, 5, false, 1, false))
            .ToArray();

        return Task.FromResult(new AccountValidationResult(
            work, outcomes, outcomes.Length, 0, 0, outcomes.Length));
    }

    /// <summary>
    /// Reproducao do bug 9A.2 ao nivel do caminho real: dois "candidate
    /// workers" invocam TestStreamsAsync para o MESMO AccountId (mesma
    /// playlist URL). O segundo NAO pode entrar no teste enquanto o primeiro
    /// esta em execucao.
    /// </summary>
    [Fact]
    public async Task Two_TestStreamsAsync_calls_same_account_never_overlap()
    {
        const string playlistUrl =
            "http://host.example:8080/get.php?username=u1&password=p1&type=m3u_plus";

        using var coordinator = new AccountGateCoordinator(4);
        var service = new TelegramScraperService((WTelegram.Client?)null);

        var inFlight = 0;
        var maxInFlight = 0;
        var entered = 0;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<AccountValidationResult> Validate(AccountValidationWork work, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref inFlight);
            if (current > maxInFlight) Interlocked.Exchange(ref maxInFlight, current);
            if (Interlocked.Increment(ref entered) == 1)
            {
                firstEntered.TrySetResult();
                await release.Task.ConfigureAwait(false);
            }
            Interlocked.Decrement(ref inFlight);
            return await AllWorkingAsync(work, ct).ConfigureAwait(false);
        }

        var first = service.TestStreamsAsync(
            Validate, coordinator, playlistUrl,
            new List<M3uStream> { Stream("http://host.example/live/u/p/1.ts") },
            0, default);

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = service.TestStreamsAsync(
            Validate, coordinator, playlistUrl,
            new List<M3uStream> { Stream("http://host.example/live/u/p/2.ts") },
            0, default);

        // Se a serializacao global nao existisse, o segundo entrava aqui.
        await Task.Delay(200);
        Assert.Equal(1, Volatile.Read(ref entered));

        release.TrySetResult();

        var firstResult = await first;
        var secondResult = await second;

        Assert.Equal(1, maxInFlight);
        Assert.Equal(2, Volatile.Read(ref entered));
        Assert.Single(firstResult);
        Assert.Single(secondResult);
        Assert.True(firstResult[0].IsWorking);
        Assert.True(secondResult[0].IsWorking);
    }

    /// <summary>
    /// Contas diferentes (playlists diferentes) podem executar TestStreamsAsync
    /// em paralelo. Cada handler espera pelo outro: se fossem serializados,
    /// a espera expira e o teste falha.
    /// </summary>
    [Fact]
    public async Task TestStreamsAsync_different_accounts_run_in_parallel()
    {
        using var coordinator = new AccountGateCoordinator(4);
        var service = new TelegramScraperService((WTelegram.Client?)null);

        var enteredA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<AccountValidationResult> Validate(
            TaskCompletionSource mine, TaskCompletionSource other,
            AccountValidationWork work, CancellationToken ct)
        {
            mine.TrySetResult();
            await other.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return await AllWorkingAsync(work, ct).ConfigureAwait(false);
        }

        var a = service.TestStreamsAsync(
            (w, ct) => Validate(enteredA, enteredB, w, ct), coordinator,
            "http://a.example:8080/get.php?username=ua&password=pa&type=m3u_plus",
            new List<M3uStream> { Stream("http://a.example/live/ua/pa/1.ts") },
            0, default);

        var b = service.TestStreamsAsync(
            (w, ct) => Validate(enteredB, enteredA, w, ct), coordinator,
            "http://b.example:8080/get.php?username=ub&password=pb&type=m3u_plus",
            new List<M3uStream> { Stream("http://b.example/live/ub/pb/1.ts") },
            0, default);

        var results = await Task.WhenAll(a, b);
        Assert.All(results, r => Assert.True(r[0].IsWorking));
    }

    /// <summary>
    /// MaxConcurrentAccounts e' um limite GLOBAL de accounts em teste,
    /// mesmo quando varias chamadas TestStreamsAsync concorrem.
    /// </summary>
    [Fact]
    public async Task TestStreamsAsync_respects_global_MaxConcurrentAccounts()
    {
        const int accounts = 5;
        const int limit = 2;
        using var coordinator = new AccountGateCoordinator(limit);
        var service = new TelegramScraperService((WTelegram.Client?)null);

        var inFlight = 0;
        var maxInFlight = 0;

        async Task<AccountValidationResult> Validate(AccountValidationWork work, CancellationToken ct)
        {
            var current = Interlocked.Increment(ref inFlight);
            if (current > maxInFlight) Interlocked.Exchange(ref maxInFlight, current);
            try { await Task.Delay(60, ct).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref inFlight); }
            return await AllWorkingAsync(work, ct).ConfigureAwait(false);
        }

        var calls = Enumerable.Range(0, accounts).Select(i =>
            service.TestStreamsAsync(
                Validate, coordinator,
                $"http://host{i}.example:8080/get.php?username=u{i}&password=p{i}&type=m3u_plus",
                new List<M3uStream> { Stream($"http://host{i}.example/live/u{i}/p{i}/1.ts") },
                0, default)).ToList();

        var results = await Task.WhenAll(calls);
        Assert.Equal(accounts, results.Length);
        Assert.True(maxInFlight <= limit,
            $"maxInFlight={maxInFlight}; expected <= {limit}");
        Assert.Equal(0, coordinator.ActiveGateCount);
    }

    /// <summary>
    /// Cancelamento nao admitido: TestStreamsAsync devolve a mesma forma
    /// (streams nao-funcionais) e liberta o slot global, sem prender gates.
    /// </summary>
    [Fact]
    public async Task TestStreamsAsync_cancellation_is_not_admitted_and_releases_slot()
    {
        using var coordinator = new AccountGateCoordinator(1);
        var service = new TelegramScraperService((WTelegram.Client?)null);

        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var blocker = service.TestStreamsAsync(
            async (work, ct) =>
            {
                entered.TrySetResult();
                await hold.Task.ConfigureAwait(false);
                return await AllWorkingAsync(work, ct).ConfigureAwait(false);
            },
            coordinator,
            "http://x.example:8080/get.php?username=ux&password=px&type=m3u_plus",
            new List<M3uStream> { Stream("http://x.example/live/ux/px/1.ts") },
            0, default);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var cancelled = await service.TestStreamsAsync(
            AllWorkingAsync, coordinator,
            "http://y.example:8080/get.php?username=uy&password=py&type=m3u_plus",
            new List<M3uStream> { Stream("http://y.example/live/uy/py/1.ts") },
            0, cts.Token);

        Assert.Single(cancelled);
        Assert.False(cancelled[0].IsWorking);

        hold.TrySetResult();
        var blockerResult = await blocker;
        Assert.True(blockerResult[0].IsWorking);

        // O slot global foi libertado: uma nova operacao entra sem bloqueio.
        var after = await service.TestStreamsAsync(
            AllWorkingAsync, coordinator,
            "http://z.example:8080/get.php?username=uz&password=pz&type=m3u_plus",
            new List<M3uStream> { Stream("http://z.example/live/uz/pz/1.ts") },
            0, default);

        Assert.True(after[0].IsWorking);
        Assert.Equal(0, coordinator.ActiveGateCount);
    }
}
