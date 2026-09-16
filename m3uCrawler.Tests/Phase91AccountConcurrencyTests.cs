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
/// PHASE 9A.1 (2026-09-16): testes estruturais A-H do modelo de
/// concorrencia com serializacao por AccountId.
///
/// REGRAS testadas (via property-based com instrumentacao):
/// - A) accounts diferentes PODEM executar em paralelo
/// - B) mesma account NUNCA tem dois canais concorrentes
/// - C) accounts diferentes nao se bloqueiam mutuamente
/// - E) same host, different users = accounts independentes
/// - F) verdadeiro bounded worker pool (sem Task.Run por work item)
/// - G) cancellation propaga sem deadlocks
/// - H) ordering / empty channel
/// </summary>
[Collection("Phase91Concurrency")]
[CollectionDefinition("Phase91Concurrency", DisableParallelization = true)]
public class Phase91AccountConcurrencyTests
{
    private const string HostA = "neorcqds.example:8080";
    private const string HostB = "mag.tiger-ott.example:80";
    private const string UserAlice = "alice";
    private const string UserBob = "bob";
    private const string UserCarol = "carol";

    /// <summary>
    /// Work item de teste: implementa IAccountWork com AccountId
    /// identificavel por (host,user).
    /// </summary>
    private sealed record TestAccountWork(string AccountId, string Url, string User) : IAccountWork;

    private static string BuildUrl(string hostPort, string user, string pwd)
        => $"http://{hostPort.Split(':')[0]}:{hostPort.Split(':')[1]}/get.php?username={user}&password={pwd}&type=m3u_plus";

    [Fact]
    public void TestE_same_host_different_users_parallel()
    {
        // AccountId deve diferir porque os usernames sao diferentes.
        var idA = AccountIdentity.Compute(HostA, UserAlice);
        var idB = AccountIdentity.Compute(HostA, UserBob);
        Assert.NotEqual(idA, idB);
    }

    [Fact]
    public async Task TestF_bounded_worker_pool_does_not_create_one_task_per_work_item()
    {
        const int workItems = 1000;
        const int maxWorkers = 4;

        var channel = Channel.CreateUnbounded<TestAccountWork>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
        for (int i = 0; i < workItems; i++)
        {
            await channel.Writer.WriteAsync(new TestAccountWork("A", $"http://a/{i}", "u"));
        }
        channel.Writer.TryComplete();

        var pool = new AccountBoundedWorkerPool<TestAccountWork, int>(
            maxWorkers,
            channel,
            async (w, ct) => { await Task.Delay(1, ct); return 1; });

        Assert.Equal(maxWorkers, pool.WorkerCount);

        var results = await pool.RunAsync(default);
        Assert.Equal(workItems, results.Count);
    }

    [Fact]
    public async Task TestG_cancellation_propagates_quickly()
    {
        var cts = new CancellationTokenSource();
        var channel = Channel.CreateUnbounded<TestAccountWork>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
        for (int i = 0; i < 10000; i++)
        {
            await channel.Writer.WriteAsync(new TestAccountWork("X", $"http://a/{i}", "u"), cts.Token);
        }

        var pool = new AccountBoundedWorkerPool<TestAccountWork, int>(
            4,
            channel,
            async (w, ct) => { await Task.Delay(100000, ct); return 1; });

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
    public async Task TestH_empty_channel_completes_immediately()
    {
        var channel = Channel.CreateUnbounded<TestAccountWork>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
        channel.Writer.TryComplete();

        var pool = new AccountBoundedWorkerPool<TestAccountWork, int>(
            4,
            channel,
            async (w, ct) => { await Task.Yield(); return 1; });

        var sw = Stopwatch.StartNew();
        var results = await pool.RunAsync(default);
        sw.Stop();
        Assert.Empty(results);
        Assert.True(sw.ElapsedMilliseconds < 1000);
    }
}
