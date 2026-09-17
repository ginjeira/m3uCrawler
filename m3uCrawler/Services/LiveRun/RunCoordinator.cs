using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace m3uCrawler.Services.LiveRun;

/// <summary>
/// PHASE 9C.4 — Coordenador único para iniciar e gerir uma
/// execução operacional do Telegram (Live Run).
///
/// <para>
/// <b>Princípio arquitectural:</b> existe um único caminho de
/// orquestração de uma execução Telegram. CLI, scheduler e
/// futura API convergem aqui; não há três mecanismos de
/// execução paralelos nem run coordinator implícito dentro da
/// pipeline. O pipeline Telegram permanece em
/// <c>TelegramScraperService</c> — este coordinator delega-lhe o
/// trabalho, sem o duplicar.
/// </para>
///
/// <para>
/// <b>Lifetime:</b> <see cref="RunCoordinator"/> é
/// process-wide e não mantém estado para além do que persiste
/// em <see cref="LiveRunEntity"/>. Pode ser instanciado uma vez
/// e partilhado por todos os entry points. Não usa lock
/// estático: a serialização é feita por
/// <see cref="System.Threading.Interlocked.CompareExchange(ref int, int, int)"/>
/// sobre um campo de instância, suficiente para single-process
/// (o deployment actual é single-instance — ver
/// <c>docker-compose.yml</c>).
/// </para>
///
/// <para>
/// <b>Persistência:</b> cada execução cria uma
/// <see cref="LiveRunEntity"/> antes de começar; transições de
/// fase criam <see cref="LiveRunStepEntity"/>s. Não há ficheiro
/// em disco — a fonte de verdade é SQLite, mesmo motor dos
/// SyncRuns. Runs sem <c>FinishedAtUtc</c> após restart são
/// marcados como terminal <see cref="LiveRunTerminalStatus.Failed"/>
/// em <see cref="RecoverInterruptedRunsAsync"/> (regra do plano,
/// sem estado <c>unknown</c> distinto).
/// </para>
/// </summary>
public sealed class RunCoordinator
{
    /// <summary>
    /// Janela (em horas) durante a qual um run terminado é
    /// considerado "recente" pela API/UI futura. Runs terminados
    /// há mais de <see cref="RecentRunWindowHours"/> são
    /// considerados idle para efeitos de exposição operacional.
    /// </summary>
    public const int RecentRunWindowHours = 24;

    private readonly IDbContextFactory<ChannelCatalogDbContext> _dbFactory;
    private readonly Func<LiveRunRequest, IRunPipeline> _pipelineFactory;
    private readonly ILogger<RunCoordinator> _logger;

    // Estado vivo (in-memory). Atomic CAS protege contra dois
    // entry points a iniciarem runs em simultâneo.
    private int _isRunningFlag; // 0 = idle, 1 = running
    private LiveRunSnapshot? _currentSnapshot;
    private readonly object _snapshotLock = new();

    public RunCoordinator(
        IDbContextFactory<ChannelCatalogDbContext> dbFactory,
        Func<LiveRunRequest, IRunPipeline> pipelineFactory,
        ILogger<RunCoordinator>? logger = null)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _pipelineFactory = pipelineFactory ?? throw new ArgumentNullException(nameof(pipelineFactory));
        _logger = logger ?? NullLogger<RunCoordinator>.Instance;
    }

    /// <summary>
    /// Indica se uma execução está activa neste processo. Fonte
    /// de verdade é o flag em memória (mais recente que a BD).
    /// </summary>
    public bool IsRunning => Volatile.Read(ref _isRunningFlag) == 1;

    /// <summary>
    /// Snapshot do run corrente, ou <c>null</c> se idle.
    /// </summary>
    public LiveRunSnapshot? CurrentSnapshot
    {
        get { lock (_snapshotLock) return _currentSnapshot; }
    }

    /// <summary>
    /// Inicia uma execução operacional. Atómico: se já houver um
    /// run activo, lança <see cref="RunAlreadyInProgressException"/>
    /// em vez de iniciar uma segunda execução.
    /// </summary>
    public async Task<LiveRunOutcome> StartAsync(LiveRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // CAS atómico: garante que dois entry points não consigam
        // ambos entrar no bloco "running" em simultâneo.
        if (Interlocked.CompareExchange(ref _isRunningFlag, 1, 0) != 0)
        {
            var current = CurrentSnapshot;
            throw new RunAlreadyInProgressException(
                current?.RunId ?? string.Empty,
                current?.StartedAtUtc ?? DateTime.UtcNow,
                request.Mode,
                request.Source);
        }

        LiveRunEntity? entity = null;
        LiveRunMonitor? monitor = null;
        var startedAtUtc = DateTime.UtcNow;
        var runId = Guid.NewGuid().ToString();
        try
        {
            entity = await PersistRunStartAsync(runId, request, startedAtUtc, cancellationToken)
                .ConfigureAwait(false);

            // Instrumentação: monitor da execução. Falhas de
            // persistência são toleradas internamente pelo monitor —
            // nunca quebram a pipeline.
            monitor = new LiveRunMonitor(
                _dbFactory,
                entity.Id,
                entity.RunId,
                request.Mode,
                request.Source,
                entity.StartedAtUtc,
                SetCurrentSnapshot,
                null,
                LiveRunActivityFeed.DefaultCapacity);
            await monitor.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var initialSnapshot = monitor.BuildSnapshot();
            SetCurrentSnapshot(initialSnapshot);

            _logger.LogInformation(
                "Live run started: runId={RunId} mode={Mode} source={Source}",
                runId, request.Mode, request.Source);

            return await RunCoreAsync(entity, request, monitor, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SafeEnterPhaseAsync(monitor, Catalog.LiveRunPhase.Error, "cancelled").ConfigureAwait(false);
            // CancellationToken.None: o token do run já está cancelado,
            // mas o estado terminal tem de ficar persistido (FinishedAtUtc).
            await MarkTerminalAsync(entity, LiveRunTerminalStatus.Failed,
                "cancelled", CancellationToken.None).ConfigureAwait(false);
            var snapshot = BuildLiveSnapshot(entity!, isRunning: false, currentPhase: null, monitor);
            SetCurrentSnapshot(snapshot);
            throw;
        }
        catch (Exception ex)
        {
            // Mensagem sanitizada: nunca inclui URLs com credenciais,
            // tokens ou paths absolutos. O log inclui ex.ToString()
            // (servidor-side, não exposto pela API futura).
            _logger.LogError(ex, "Live run failed: runId={RunId}", runId);
            await SafeEnterPhaseAsync(monitor, Catalog.LiveRunPhase.Error,
                "failed: pipeline exception").ConfigureAwait(false);
            await MarkTerminalAsync(entity, LiveRunTerminalStatus.Failed,
                "failed: pipeline exception", CancellationToken.None).ConfigureAwait(false);
            var snapshot = BuildLiveSnapshot(entity!, isRunning: false, currentPhase: null, monitor);
            SetCurrentSnapshot(snapshot);
            return new LiveRunOutcome
            {
                Snapshot = snapshot,
                Succeeded = false,
                ErrorMessage = "pipeline exception",
            };
        }
        finally
        {
            Volatile.Write(ref _isRunningFlag, 0);
        }
    }

    /// <summary>
    /// PHASE 9C.4 — Inicia um run e devolve IMEDIATAMENTE o snapshot
    /// inicial (com runId, startedAtUtc). A execução da pipeline corre
    /// em background. O resultado final é consultável em
    /// <see cref="CurrentSnapshot"/> e persistido em SQLite.
    ///
    /// <para>
    /// Adequado para o endpoint <c>POST /api/run/start</c> (HTTP 202
    /// "Accepted"). Lança <see cref="RunAlreadyInProgressException"/>
    /// se já houver um run activo (mapeado para HTTP 409).
    /// </para>
    ///
    /// <para>
    /// O lock é libertado quando a tarefa em background termina; quem
    /// quiser observar a conclusão deve ler
    /// <see cref="CurrentSnapshot"/> ou
    /// <see cref="GetRecentFinishedSnapshotAsync"/>.
    /// </para>
    /// </summary>
    public async Task<LiveRunOutcome> KickStartAsync(LiveRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Interlocked.CompareExchange(ref _isRunningFlag, 1, 0) != 0)
        {
            var current = CurrentSnapshot;
            throw new RunAlreadyInProgressException(
                current?.RunId ?? string.Empty,
                current?.StartedAtUtc ?? DateTime.UtcNow,
                request.Mode,
                request.Source);
        }

        LiveRunEntity? entity = null;
        LiveRunMonitor? monitor = null;
        var startedAtUtc = DateTime.UtcNow;
        var runId = Guid.NewGuid().ToString();
        try
        {
            entity = await PersistRunStartAsync(runId, request, startedAtUtc, cancellationToken)
                .ConfigureAwait(false);

            monitor = new LiveRunMonitor(
                _dbFactory,
                entity.Id,
                entity.RunId,
                request.Mode,
                request.Source,
                entity.StartedAtUtc,
                SetCurrentSnapshot,
                null,
                LiveRunActivityFeed.DefaultCapacity);
            await monitor.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var initialSnapshot = monitor.BuildSnapshot();
            SetCurrentSnapshot(initialSnapshot);

            _logger.LogInformation(
                "Live run kick-started: runId={RunId} mode={Mode} source={Source}",
                runId, request.Mode, request.Source);

            // Dispara a pipeline em background. A gestão do terminal
            // (Completed/Failed) segue a mesma lógica de StartAsync: o
            // lock é libertado quando a tarefa termina.
            _ = Task.Run(async () =>
            {
                try
                {
                    await RunCoreAsync(entity!, request, monitor!, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Live run background task failed: runId={RunId}", runId);
                    try
                    {
                        await SafeEnterPhaseAsync(monitor, Catalog.LiveRunPhase.Error,
                            "failed: pipeline exception").ConfigureAwait(false);
                        await MarkTerminalAsync(entity, LiveRunTerminalStatus.Failed,
                            "failed: pipeline exception", CancellationToken.None).ConfigureAwait(false);
                        var snapshot = BuildLiveSnapshot(entity!, isRunning: false, currentPhase: null, monitor);
                        SetCurrentSnapshot(snapshot);
                    }
                    catch (Exception innerEx)
                    {
                        _logger.LogError(innerEx, "Failed to mark terminal: runId={RunId}", runId);
                    }
                }
                finally
                {
                    Volatile.Write(ref _isRunningFlag, 0);
                }
            }, CancellationToken.None);

            return new LiveRunOutcome
            {
                Snapshot = initialSnapshot,
                Succeeded = true,
            };
        }
        catch
        {
            // Em caso de falha síncrona (e.g. persistência falhou antes
            // de podermos devolver 202), libertar o lock para não ficar preso.
            Volatile.Write(ref _isRunningFlag, 0);
            throw;
        }
    }

    /// <summary>
    /// Corpo principal partilhado por <see cref="StartAsync"/> e
    /// <see cref="KickStartAsync"/>: invoca a pipeline, marca
    /// <c>Completed</c>/<c>Failed</c> e devolve o resultado terminal.
    /// </summary>
    private async Task<LiveRunOutcome> RunCoreAsync(
        LiveRunEntity entity,
        LiveRunRequest request,
        LiveRunMonitor monitor,
        CancellationToken cancellationToken)
    {
        var pipeline = _pipelineFactory(request);
        if (pipeline is ILiveRunProgressAware progressAware)
        {
            progressAware.Progress = monitor;
        }

        await pipeline.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

        await monitor.EnterPhaseAsync(Catalog.LiveRunPhase.Completed, "completed", cancellationToken)
            .ConfigureAwait(false);
        await MarkCompletedAsync(entity, cancellationToken).ConfigureAwait(false);

        var finalSnapshot = BuildLiveSnapshot(entity, isRunning: false, currentPhase: null, monitor);
        SetCurrentSnapshot(finalSnapshot);

        return new LiveRunOutcome
        {
            Snapshot = finalSnapshot,
            Succeeded = true,
        };
    }


    /// <summary>
    /// Transição terminal best-effort: usa <see cref="CancellationToken.None"/>
    /// (o token do run pode já estar cancelado) e nunca propaga
    /// excepções da instrumentação.
    /// </summary>
    private static async Task SafeEnterPhaseAsync(
        LiveRunMonitor? monitor,
        Catalog.LiveRunPhase phase,
        string message)
    {
        if (monitor is null) return;
        try
        {
            await monitor.EnterPhaseAsync(phase, message, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Instrumentação nunca quebra o coordinator.
        }
    }

    /// <summary>
    /// Hidrata o estado de runs interrompidos. Chamada única no
    /// arranque. Runs sem <c>FinishedAtUtc</c> são marcados como
    /// <see cref="LiveRunTerminalStatus.Failed"/> (regra do plano:
    /// "run interrompido sem conclusão persistida" ⇒ failed, sem
    /// criar estado <c>unknown</c>).
    /// </summary>
    public async Task<int> RecoverInterruptedRunsAsync(CancellationToken cancellationToken)
    {
        var recovered = 0;
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var interrupted = await context.LiveRuns
            .Where(r => r.FinishedAtUtc == null && r.TerminalStatus == LiveRunTerminalStatus.Unknown)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var now = DateTime.UtcNow;
        foreach (var run in interrupted)
        {
            run.TerminalStatus = LiveRunTerminalStatus.Failed;
            run.FinishedAtUtc = now;
            run.LastMessage = "recovered after restart: run interrupted without completion";
            run.UpdatedAtUtc = now;
            recovered++;
        }

        if (recovered > 0)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "Live run recovery: marked {Count} interrupted runs as failed",
                recovered);
        }

        return recovered;
    }

    /// <summary>
    /// Devolve o snapshot mais recente terminado dentro da janela
    /// de 24h, ou <c>null</c> se não existir. Runs terminados há
    /// mais de <see cref="RecentRunWindowHours"/> não contam.
    /// </summary>
    public async Task<LiveRunSnapshot?> GetRecentFinishedSnapshotAsync(
        CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow.AddHours(-RecentRunWindowHours);
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await context.LiveRuns
            .AsNoTracking()
            .Where(r => r.FinishedAtUtc != null && r.FinishedAtUtc >= cutoff)
            .OrderByDescending(r => r.FinishedAtUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? null : BuildLiveSnapshot(entity, isRunning: false, currentPhase: null);
    }

    // ===================== Helpers internos =====================

    private async Task<LiveRunEntity> PersistRunStartAsync(
        string runId,
        LiveRunRequest request,
        DateTime startedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = new LiveRunEntity
        {
            RunId = runId,
            Mode = request.Mode.ToWireName(),
            Source = request.Source.ToWireName(),
            StartedAtUtc = startedAtUtc,
            FinishedAtUtc = null,
            TerminalStatus = LiveRunTerminalStatus.Unknown,
            LastMessage = null,
            CountsJson = "{}",
            CreatedAtUtc = startedAtUtc,
            UpdatedAtUtc = startedAtUtc,
        };
        context.LiveRuns.Add(entity);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return entity;
    }

    private async Task MarkCompletedAsync(LiveRunEntity entity, CancellationToken cancellationToken)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var attached = await context.LiveRuns.FirstAsync(r => r.Id == entity.Id, cancellationToken).ConfigureAwait(false);
        var finishedAt = DateTime.UtcNow;
        attached.TerminalStatus = LiveRunTerminalStatus.Completed;
        attached.FinishedAtUtc = finishedAt;
        attached.LastMessage = "completed";
        attached.UpdatedAtUtc = finishedAt;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        entity.TerminalStatus = attached.TerminalStatus;
        entity.FinishedAtUtc = attached.FinishedAtUtc;
        entity.LastMessage = attached.LastMessage;
        entity.UpdatedAtUtc = attached.UpdatedAtUtc;
    }

    private async Task MarkTerminalAsync(LiveRunEntity? entity, LiveRunTerminalStatus status,
        string lastMessage, CancellationToken cancellationToken)
    {
        if (entity is null) return;
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var attached = await context.LiveRuns.FirstOrDefaultAsync(r => r.Id == entity.Id, cancellationToken)
            .ConfigureAwait(false);
        if (attached is null) return;

        var finishedAt = DateTime.UtcNow;
        attached.TerminalStatus = status;
        attached.FinishedAtUtc = finishedAt;
        attached.LastMessage = lastMessage;
        attached.UpdatedAtUtc = finishedAt;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        entity.TerminalStatus = status;
        entity.FinishedAtUtc = finishedAt;
        entity.LastMessage = lastMessage;
        entity.UpdatedAtUtc = finishedAt;
    }

    private static LiveRunSnapshot BuildLiveSnapshot(
        LiveRunEntity entity,
        bool isRunning,
        Catalog.LiveRunPhase? currentPhase,
        LiveRunMonitor? monitor = null)
    {
        return new LiveRunSnapshot
        {
            RunId = entity.RunId,
            Mode = LiveRunWireNames.ParseMode(entity.Mode),
            Source = LiveRunWireNames.ParseSource(entity.Source),
            StartedAtUtc = entity.StartedAtUtc,
            FinishedAtUtc = entity.FinishedAtUtc,
            TerminalStatus = entity.TerminalStatus,
            LastMessage = entity.LastMessage,
            CountsJson = entity.CountsJson,
            CurrentPhase = currentPhase,
            IsRunning = isRunning,
            PhaseIndex = monitor?.PhaseIndex ?? 0,
            PhaseStartedAtUtc = monitor?.PhaseStartedAtUtc,
            UpdatedAtUtc = monitor?.UpdatedAtUtc ?? entity.UpdatedAtUtc,
            Counts = monitor?.CountsSnapshot,
            RecentActivities = monitor?.ActivitiesSnapshot ?? Array.Empty<LiveRunActivity>(),
            Sanitized = true,
        };
    }

    private void SetCurrentSnapshot(LiveRunSnapshot snapshot)
    {
        lock (_snapshotLock)
        {
            // PHASE 9C.4 — Invariante de transição terminal: enquanto o
            // run está activo (flag=1) nunca publicamos um snapshot
            // "não-running" cujo estado terminal ainda é Unknown. O
            // monitor publica a fase terminal (Completed/Error) antes
            // de o coordinator persistir TerminalStatus/FinishedAtUtc;
            // aceitar esse snapshot intermédio faria a API
            // /api/run/status reportar "idle" durante alguns
            // milissegundos. O coordinator publica depois o snapshot
            // terminal definitivo (com TerminalStatus conhecido), que
            // é aceite porque a condição deixa de se aplicar.
            if (Volatile.Read(ref _isRunningFlag) == 1
                && !snapshot.IsRunning
                && snapshot.TerminalStatus == LiveRunTerminalStatus.Unknown)
            {
                return;
            }

            _currentSnapshot = snapshot;
        }
    }
}
