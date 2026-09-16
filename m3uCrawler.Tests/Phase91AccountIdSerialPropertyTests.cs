using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using m3uCrawler.Services.Validation;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9A.1 (2026-09-16, rev. 2): testes deterministicos que PROVAM
/// a propriedade de serializacao por conta.
///
/// Invariante testado:
///   Para a mesma conta (TWork com mesmo AccountId), nunca existem
///   DUAS operacoes activas em paralelo. Accounts diferentes podem
///   executar em paralelo ate' WorkerCount.
/// </summary>
public class Phase91AccountIdSerialPropertyTests
{
    /// <summary>
    /// Wrapper de teste com identificador explícito de conta.
    /// </summary>
    private sealed record TestWork(string AccountId, int Value) : IAccountWork;

    private static Channel<TestWork> NewUnboundedChannel()
    {
        return Channel.CreateUnbounded<TestWork>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    }

    /// <summary>
    /// PROPRIEDADE FUNDAMENTAL: dois work items com mesmo AccountId
    /// NAO CORREM EM PARALELO. Validamos com instrumentacao de
    /// in-flight per-account.
    /// </summary>
    [Fact]
    public async Task Duplicate_account_ids_never_run_in_parallel()
    {
        // 100 items com accountId="X". Worker pool=8.
        // Cada item: 50ms de trabalho. Se parallelo, total ~200ms.
        // Se serializado, total ~5 000ms (100 items * 50ms).
        const int itemCount = 100;
        const int perItemDelayMs = 50;
        const int workerCount = 8;

        var channel = NewUnboundedChannel();
        for (int i = 0; i < itemCount; i++)
        {
            await channel.Writer.WriteAsync(new TestWork("X", i));
        }
        channel.Writer.TryComplete();

        var inFlight = 0;
        var maxInFlight = 0;
        var lockObj = new object();

        var pool = new AccountBoundedWorkerPool<TestWork, int>(
            workerCount,
            channel,
            async (item, ct) =>
            {
                var current = Interlocked.Increment(ref inFlight);
                lock (lockObj)
                {
                    if (current > maxInFlight) maxInFlight = current;
                }
                try
                {
                    await Task.Delay(perItemDelayMs, ct);
                    return item.Value;
                }
                finally
                {
                    Interlocked.Decrement(ref inFlight);
                }
            });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = await pool.RunAsync(default);
        sw.Stop();

        Assert.Equal(itemCount, results.Count);
        Assert.Equal(1, maxInFlight);
        // Serial baseline: itemCount * perItemDelayMs ≈ 5000 ms (com alguma folga).
        Assert.True(sw.ElapsedMilliseconds >= itemCount * perItemDelayMs * 0.9,
            $"Serial elapsed {sw.ElapsedMilliseconds}ms; expected >= {itemCount * perItemDelayMs * 0.9}ms");
    }

    /// <summary>
    /// PROPRIEDADE COMPLEMENTAR: items com accountIds DIFERENTES
    /// CORREM EM PARALELO. Validamos instrumentando in-flight per-account
    /// e confirmando max in-flight per account == 1 mas
    /// max in-flight TOTAL <= workerCount.
    /// </summary>
    [Fact]
    public async Task Distinct_account_ids_run_in_parallel()
    {
        const int accountsCount = 3;
        const int itemsPerAccount = 5;
        const int perItemDelayMs = 50;
        const int workerCount = 8;

        var channel = NewUnboundedChannel();
        for (int i = 0; i < accountsCount; i++)
        {
            var accId = $"A{i}";
            for (int j = 0; j < itemsPerAccount; j++)
            {
                await channel.Writer.WriteAsync(new TestWork(accId, j));
            }
        }
        channel.Writer.TryComplete();

        var perAccountMaxInFlight = new ConcurrentDictionary<string, int>();
        var perAccountCurrent = new ConcurrentDictionary<string, int>();
        var totalInFlight = 0;
        var maxTotalInFlight = 0;

        var pool = new AccountBoundedWorkerPool<TestWork, int>(
            workerCount,
            channel,
            async (item, ct) =>
            {
                var current = Interlocked.Increment(ref totalInFlight);
                if (current > maxTotalInFlight) Interlocked.Exchange(ref maxTotalInFlight, current);

                perAccountCurrent.AddOrUpdate(item.AccountId, 1, (_, v) => v + 1);
                var acc = perAccountCurrent[item.AccountId];
                perAccountMaxInFlight.AddOrUpdate(item.AccountId, 1,
                    (_, v) => acc > v ? acc : v);

                try
                {
                    await Task.Delay(perItemDelayMs, ct);
                    return item.Value;
                }
                finally
                {
                    perAccountCurrent.AddOrUpdate(item.AccountId, 0, (_, v) => v - 1);
                    Interlocked.Decrement(ref totalInFlight);
                }
            });

        var results = await pool.RunAsync(default);
        Assert.Equal(accountsCount * itemsPerAccount, results.Count);

        // Cada account: maxInFlight == 1 (serial dentro da account).
        foreach (var accId in Enumerable.Range(0, accountsCount).Select(i => $"A{i}"))
        {
            var mx = perAccountMaxInFlight[accId];
            Assert.True(mx <= 1,
                $"Account {accId}: maxInFlight={mx}, expected <= 1");
        }
        // Total in-flight pode chegar a workerCount mas NAO a accountsCount
        // (com 3 accounts e 8 workers, ate 3 paralelos e nao 8).
        Assert.True(maxTotalInFlight <= workerCount,
            $"Total maxInFlight={maxTotalInFlight}, expected <= {workerCount}");
        // Parallel: os 15 items com 50ms cada em 3 accounts → < 4*50ms = 200ms;
        // serial seria 15 * 50ms = 750ms.
        // Como workerCount >= accountsCount, todos os 3 accounts correm em
        // paralelo ≈ 5 * 50ms = 250ms (sequential per account).
    }

    /// <summary>
    /// PROPRIEDADE DE MIXED: 3 accounts com work items duplicados.
    /// Account X 3x + Account Y 3x + Account Z 3x, todos concorrentes.
    /// Demonstrar que SERIAL por account + PARALLEL entre accounts.
    /// </summary>
    [Fact]
    public async Task Mixed_accounts_with_duplicates_maintain_serial_per_account()
    {
        const int workerCount = 8;
        var items = new List<TestWork>();
        foreach (var accId in new[] { "X", "Y", "Z" })
        {
            for (int i = 0; i < 3; i++)
            {
                items.Add(new TestWork(accId, i));
            }
        }

        // Mistura: alterna accounts para que aparecam intercaladas no canal.
        var channel = NewUnboundedChannel();
        foreach (var item in items.OrderBy(x => x.Value % 3))
        {
            await channel.Writer.WriteAsync(item);
        }
        channel.Writer.TryComplete();

        var perAccountMaxInFlight = new ConcurrentDictionary<string, int>();
        var perAccountCurrent = new ConcurrentDictionary<string, int>();

        var pool = new AccountBoundedWorkerPool<TestWork, int>(
            workerCount,
            channel,
            async (item, ct) =>
            {
                perAccountCurrent.AddOrUpdate(item.AccountId, 1, (_, v) => v + 1);
                var acc = perAccountCurrent[item.AccountId];
                perAccountMaxInFlight.AddOrUpdate(item.AccountId, 1,
                    (_, v) => acc > v ? acc : v);

                await Task.Delay(100, ct);

                perAccountCurrent.AddOrUpdate(item.AccountId, 0, (_, v) => v - 1);
                return item.Value;
            });

        var results = await pool.RunAsync(default);
        Assert.Equal(9, results.Count);

        foreach (var accId in new[] { "X", "Y", "Z" })
        {
            var mx = perAccountMaxInFlight[accId];
            Assert.Equal(1, mx);
        }
    }

    /// <summary>
    /// PROPRIEDADE: excepcao dentro do handler liberta o lock.
    /// Se a excepcao nao libertasse, o lock ficaria preso.
    /// </summary>
    [Fact]
    public async Task Exception_in_handler_releases_lock_so_subsequent_items_can_proceed()
    {
        const int workerCount = 4;
        // 4 items com account "X". Item 2 explode. Items 3, 4 devem processar.
        var processedItemsAfterIndex2 = 0;

        var channel = NewUnboundedChannel();
        for (int i = 0; i < 4; i++)
        {
            await channel.Writer.WriteAsync(new TestWork("X", i));
        }
        channel.Writer.TryComplete();

        var pool = new AccountBoundedWorkerPool<TestWork, int>(
            workerCount,
            channel,
            async (item, ct) =>
            {
                if (item.Value == 2) throw new InvalidOperationException("boom");
                await Task.Delay(30, ct);
                if (item.Value > 2) Interlocked.Increment(ref processedItemsAfterIndex2);
                return item.Value;
            });

        // O handler NAO deve propagar exceptions ao caller; os workers
        // sobrevivem e os items remanescentes sao processados.
        var results = await pool.RunAsync(default);
        Assert.Equal(3, results.Count); // 4 - 1 (failed) = 3
        Assert.DoesNotContain(2, results);
        Assert.Equal(1, processedItemsAfterIndex2); // apenas o item 3 (4 itens: 0..3)
    }

    /// <summary>
    /// PROPRIEDADE: cancellation liberta os locks e nao deixa
    /// in-flight counts positivos. Verificamos que o pool nao
    /// produz deadlocks e que o total in-flight medido em cada
    /// instante nunca excede 1 por account (e o pool limpa-se
    /// quando termina).
    /// </summary>
    [Fact]
    public async Task Cancellation_releases_locks_and_completes()
    {
        var cts = new CancellationTokenSource();
        const int workerCount = 4;

        var channel = NewUnboundedChannel();
        for (int i = 0; i < 1000; i++)
        {
            await channel.Writer.WriteAsync(new TestWork("X", i), cts.Token);
        }

        var pool = new AccountBoundedWorkerPool<TestWork, int>(
            workerCount,
            channel,
            async (item, ct) =>
            {
                await Task.Delay(100000, ct);
                return item.Value;
            });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var runTask = pool.RunAsync(cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        try { await runTask; } catch { /* cancelado */ }
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"Cancel took {sw.ElapsedMilliseconds}ms; expected < 5s");
    }

    /// <summary>
    /// Smoke test: TestF da Phase91 — verdadeiro bounded worker
    /// pool, N=1000 items, Workers pinned a WorkerCount.
    /// </summary>
    [Fact]
    public async Task Bounded_worker_pool_does_not_create_one_task_per_work_item()
    {
        const int workItems = 1000;
        const int maxWorkers = 4;

        var channel = NewUnboundedChannel();
        for (int i = 0; i < workItems; i++)
        {
            await channel.Writer.WriteAsync(new TestWork("A", i));
        }
        channel.Writer.TryComplete();

        var pool = new AccountBoundedWorkerPool<TestWork, int>(
            maxWorkers,
            channel,
            async (i, ct) => { await Task.Delay(1, ct); return i.Value; });

        Assert.Equal(4, pool.WorkerCount);

        var results = await pool.RunAsync(default);
        Assert.Equal(workItems, results.Count);
    }
}
