using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using m3uCrawler.Services.Validation;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9A.1 (2026-09-16): valida o NOVO padrao de worker pool.
/// </summary>
public class Phase91BoundedWorkerPoolTests
{
    /// <summary>
    /// Wrapper usado pelos testes para satisfazer a constraint
    /// <c>where TWork : IAccountWork</c> do pool.
    /// </summary>
    private sealed record TestWork(int Value, string AccountId) : IAccountWork;

    private static Channel<TestWork> NewUnboundedChannel()
    {
        return Channel.CreateUnbounded<TestWork>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    }

    [Fact]
    public async Task WorkerCount_matches_configured_value()
    {
        var channel = NewUnboundedChannel();
        await channel.Writer.WriteAsync(new TestWork(1, "A"));
        await channel.Writer.WriteAsync(new TestWork(2, "A"));
        await channel.Writer.WriteAsync(new TestWork(3, "A"));
        channel.Writer.TryComplete();

        var pool = new AccountBoundedWorkerPool<TestWork, int>(
            workerCount: 7,
            channel,
            async (i, ct) => { await Task.Yield(); return i.Value; });

        Assert.Equal(7, pool.WorkerCount);

        var results = await pool.RunAsync(default);
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task WorkerCount_of_1_still_processes_all_items()
    {
        var channel = NewUnboundedChannel();
        for (int i = 0; i < 10; i++)
        {
            await channel.Writer.WriteAsync(new TestWork(i, "A"));
        }
        channel.Writer.TryComplete();

        var pool = new AccountBoundedWorkerPool<TestWork, int>(
            workerCount: 1,
            channel,
            async (i, ct) => { await Task.Delay(10, ct); return i.Value; });

        var results = await pool.RunAsync(default);
        Assert.Equal(10, results.Count);
    }

    [Fact]
    public async Task Empty_channel_completes_immediately()
    {
        var channel = NewUnboundedChannel();
        channel.Writer.TryComplete();

        var pool = new AccountBoundedWorkerPool<TestWork, int>(
            workerCount: 4,
            channel,
            async (i, ct) => { await Task.Yield(); return i.Value; });

        var sw = Stopwatch.StartNew();
        var results = await pool.RunAsync(default);
        sw.Stop();
        Assert.Empty(results);
        Assert.True(sw.ElapsedMilliseconds < 1000);
    }

    [Fact]
    public async Task Cancellation_propagates_quickly()
    {
        var cts = new CancellationTokenSource();
        var channel = NewUnboundedChannel();
        for (int i = 0; i < 10000; i++)
        {
            await channel.Writer.WriteAsync(new TestWork(i, "A"), cts.Token);
        }

        var pool = new AccountBoundedWorkerPool<TestWork, int>(
            4,
            channel,
            async (i, ct) => { await Task.Delay(100000, ct); return i.Value; });

        var sw = Stopwatch.StartNew();
        var runTask = pool.RunAsync(cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        try { await runTask; } catch { }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"Cancellation took {sw.ElapsedMilliseconds}ms; expected < 5s");
    }

    [Fact]
    public async Task Exceptions_in_handler_do_not_kill_other_workers()
    {
        var channel = NewUnboundedChannel();
        for (int i = 0; i < 4; i++)
        {
            await channel.Writer.WriteAsync(new TestWork(i, "A"));
        }
        channel.Writer.TryComplete();

        var pool = new AccountBoundedWorkerPool<TestWork, int>(
            workerCount: 4,
            channel,
            (i, ct) =>
            {
                if (i.Value == 1) throw new InvalidOperationException("bad");
                return new ValueTask<int>(i.Value);
            });

        var results = await pool.RunAsync(default);
        Assert.Equal(3, results.Count);
        Assert.DoesNotContain(1, results);
    }
}
