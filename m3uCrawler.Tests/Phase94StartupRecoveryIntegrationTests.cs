using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.LiveRun;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9C.4 (F-002) — Teste de integração da chamada de recovery
/// no startup.
///
/// <para>
/// Documenta e valida o padrão que <c>Program.cs</c> implementa:
/// após criar o <see cref="RunCoordinator"/>, chama
/// <see cref="RunCoordinator.RecoverInterruptedRunsAsync"/> antes
/// de qualquer <c>StartAsync</c>. Runs interrompidos por crash
/// anterior são marcados como <c>Failed</c> e ficam imediatamente
/// visíveis no histórico (24h).
/// </para>
/// </summary>
public class Phase94StartupRecoveryIntegrationTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;
    private LiveRunHost? _host;

    public Phase94StartupRecoveryIntegrationTests()
    {
        _dbPath = TestTempDb.SuitePath($"phase94-startup-recov-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
    }

    public async Task DisposeAsync()
    {
        _host = null;
        await Task.CompletedTask;
        TestTempDb.Cleanup(_dbPath);
    }

    /// <summary>
    /// Simula o fluxo de startup que Program.cs executa após o
    /// LiveRunHost estar configurado:
    ///   1. Coordinator está disponível.
    ///   2. Antes de aceitar runs, chama RecoverInterruptedRunsAsync.
    ///   3. Runs interrompidos aparecem no histórico como Failed.
    /// </summary>
    [Fact]
    public async Task Startup_wiring_marks_interrupted_runs_as_failed_before_new_runs()
    {
        // 1) Simular um run interrompido: linha em SQLite com
        //    FinishedAtUtc=null e TerminalStatus=Unknown.
        await using (var ctx = _factory.CreateDbContext())
        {
            ctx.LiveRuns.Add(new LiveRunEntity
            {
                RunId = Guid.NewGuid().ToString(),
                Mode = LiveRunWireNames.ModeTelegram,
                Source = LiveRunWireNames.SourceCli,
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                FinishedAtUtc = null,
                TerminalStatus = LiveRunTerminalStatus.Unknown,
                CountsJson = "{}",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            });
            await ctx.SaveChangesAsync();
        }

        // 2) Startup wiring: o coordinator é configurado e a
        //    recuperação corre.
        _host = new LiveRunHost(_factory);
        _host.ConfigureExecutor(_ => new BlockingPipeline());
        Assert.NotNull(_host.Coordinator);

        var recovered = await _host.Coordinator!
            .RecoverInterruptedRunsAsync(CancellationToken.None);
        Assert.Equal(1, recovered);

        // 3) O run interrompido aparece no histórico como Failed.
        await using var readCtx = _factory.CreateDbContext();
        var stored = await readCtx.LiveRuns.SingleAsync();
        Assert.Equal(LiveRunTerminalStatus.Failed, stored.TerminalStatus);
        Assert.NotNull(stored.FinishedAtUtc);

        // 4) Após o recovery, o coordinator está pronto para
        //    aceitar novas runs (não está preso).
        Assert.False(_host.Coordinator.IsRunning);

        var snapshot = await _host.Coordinator
            .GetRecentFinishedSnapshotAsync(CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Equal(LiveRunTerminalStatus.Failed, snapshot.TerminalStatus);
    }

    [Fact]
    public async Task Startup_recovery_is_idempotent_when_no_interrupted_runs_exist()
    {
        _host = new LiveRunHost(_factory);
        _host.ConfigureExecutor(_ => new BlockingPipeline());

        var firstCall = await _host.Coordinator!
            .RecoverInterruptedRunsAsync(CancellationToken.None);
        var secondCall = await _host.Coordinator!
            .RecoverInterruptedRunsAsync(CancellationToken.None);

        Assert.Equal(0, firstCall);
        Assert.Equal(0, secondCall);
    }

    /// <summary>
    /// Pipeline mínima: bloqueia até ser libertada. Usada apenas para
    /// instanciar <see cref="LiveRunHost.ConfigureExecutor"/> sem
    /// iniciar nada. <c>ExecuteAsync</c> nunca é invocado nos testes
    /// desta classe.
    /// </summary>
    private sealed class BlockingPipeline : IRunPipeline
    {
        public Task ExecuteAsync(LiveRunRequest request, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
