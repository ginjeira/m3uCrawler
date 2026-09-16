using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace m3uCrawler.Services.LiveRun;

/// <summary>
/// PHASE 9C.4 — Monitor de execução de uma única Live Run.
///
/// <para>
/// Recebe eventos da pipeline (<see cref="ILiveRunProgress"/>),
/// persiste <see cref="LiveRunStepEntity"/> por transição de fase,
/// actualiza os campos de progresso de <see cref="LiveRunEntity"/>
/// (LastMessage, UpdatedAtUtc, CountsJson) e mantém o estado vivo em
/// memória (snapshot + ring buffer de actividades).
/// </para>
///
/// <para>
/// <b>Resiliência:</b> nenhuma operação de instrumentação pode
/// alterar o comportamento do pipeline. Todas as escritas em SQLite
/// são tolerantes a falha (log + continua) — a pipeline nunca vê uma
/// excepção originada na instrumentação.
/// </para>
///
/// <para>
/// <b>Concorrência:</b> os métodos de reporte podem ser chamados de
/// múltiplas tasks worker. O estado em memória é protegido por um
/// lock de instância; as escritas em BD são serializadas por um
/// <see cref="SemaphoreSlim"/> de instância. Não há locks estáticos.
/// </para>
/// </summary>
public sealed class LiveRunMonitor : ILiveRunProgress
{
    private readonly IDbContextFactory<ChannelCatalogDbContext> _dbFactory;
    private readonly long _liveRunId;
    private readonly LiveRunMode _mode;
    private readonly LiveRunSource _source;
    private readonly DateTime _startedAtUtc;
    private readonly Action<LiveRunSnapshot>? _onSnapshot;
    private readonly ILogger<LiveRunMonitor> _logger;
    private readonly LiveRunActivityFeed _feed;
    private readonly SemaphoreSlim _persistGate = new(1, 1);

    private readonly object _gate = new();
    private readonly LiveRunCounts _counts = new();
    private LiveRunPhase _currentPhase = LiveRunPhase.Idle;
    private DateTime _phaseStartedAtUtc;
    private DateTime _updatedAtUtc;
    private string? _lastMessage;
    private string _countsJson = "{}";
    private int _lastPhaseIndex = -1;
    private long? _currentStepId;

    public LiveRunMonitor(
        IDbContextFactory<ChannelCatalogDbContext> dbFactory,
        long liveRunId,
        string runId,
        LiveRunMode mode,
        LiveRunSource source,
        DateTime startedAtUtc,
        Action<LiveRunSnapshot>? onSnapshot = null,
        ILogger<LiveRunMonitor>? logger = null,
        int activityCapacity = LiveRunActivityFeed.DefaultCapacity)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _liveRunId = liveRunId;
        RunId = string.IsNullOrWhiteSpace(runId) ? throw new ArgumentException("runId is required", nameof(runId)) : runId;
        _mode = mode;
        _source = source;
        _startedAtUtc = startedAtUtc;
        _onSnapshot = onSnapshot;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<LiveRunMonitor>.Instance;
        _feed = new LiveRunActivityFeed(activityCapacity);
        _updatedAtUtc = startedAtUtc;
        _phaseStartedAtUtc = startedAtUtc;
    }

    public string RunId { get; }

    public LiveRunPhase CurrentPhase
    {
        get { lock (_gate) return _currentPhase; }
    }

    public int PhaseIndex
    {
        get { lock (_gate) return Math.Max(0, _lastPhaseIndex); }
    }

    public DateTime PhaseStartedAtUtc
    {
        get { lock (_gate) return _phaseStartedAtUtc; }
    }

    public DateTime UpdatedAtUtc
    {
        get { lock (_gate) return _updatedAtUtc; }
    }

    public LiveRunCounts CountsSnapshot
    {
        get { lock (_gate) return _counts.Clone(); }
    }

    public IReadOnlyList<LiveRunActivity> ActivitiesSnapshot => _feed.Snapshot();

    public LiveRunActivityFeed ActivityFeed => _feed;

    /// <summary>
    /// Regista a fase inicial <see cref="LiveRunPhase.Idle"/> e cria o
    /// primeiro <see cref="LiveRunStepEntity"/>. Não lança em caso de
    /// falha de persistência.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var message = LiveRunSanitizer.Message("run started");
        string countsJson;
        lock (_gate)
        {
            _currentPhase = LiveRunPhase.Idle;
            _lastPhaseIndex = (int)LiveRunPhase.Idle;
            _phaseStartedAtUtc = _startedAtUtc;
            _updatedAtUtc = _startedAtUtc;
            _lastMessage = message;
            _countsJson = _counts.ToJson();
            countsJson = _countsJson;
        }

        PublishActivity(LiveRunPhase.Idle, message);
        NotifySnapshot();

        await PersistPhaseTransitionAsync(
            LiveRunPhase.Idle,
            (int)LiveRunPhase.Idle,
            message,
            _startedAtUtc,
            previousStepId: null,
            countsJson,
            result: "ok",
            terminal: false,
            cancellationToken).ConfigureAwait(false);

        NotifySnapshot();
    }

    public async Task EnterPhaseAsync(
        LiveRunPhase phase,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        var index = (int)phase;

        long? previousStepId;
        string sanitizedMessage;
        DateTime now;
        string countsJson;

        lock (_gate)
        {
            // Timeline monotónico: transições repetidas ou retroactivas
            // são ignoradas (sem step duplicado para a mesma fase).
            if (index <= _lastPhaseIndex) return;

            previousStepId = _currentStepId;
            _lastPhaseIndex = index;
            _currentPhase = phase;
            now = DateTime.UtcNow;
            _phaseStartedAtUtc = now;
            _updatedAtUtc = now;

            var candidate = LiveRunSanitizer.Message(message);
            sanitizedMessage = candidate.Length > 0 ? candidate : $"phase: {phase}";
            _lastMessage = sanitizedMessage;
            _countsJson = _counts.ToJson();
            countsJson = _countsJson;
        }

        var terminal = phase is LiveRunPhase.Completed or LiveRunPhase.Error;
        var result = phase == LiveRunPhase.Error ? "error" : "ok";

        PublishActivity(phase, sanitizedMessage);
        NotifySnapshot();

        await PersistPhaseTransitionAsync(
            phase,
            index,
            sanitizedMessage,
            now,
            previousStepId,
            countsJson,
            result,
            terminal,
            cancellationToken).ConfigureAwait(false);

        NotifySnapshot();
    }

    public void ReportCounts(Action<LiveRunCounts> mutate)
    {
        if (mutate is null) return;
        lock (_gate)
        {
            mutate(_counts);
            _countsJson = _counts.ToJson();
            _updatedAtUtc = DateTime.UtcNow;
        }
        NotifySnapshot();
    }

    public void ReportCounts(Models.RunReport report)
    {
        if (report is null) return;
        lock (_gate)
        {
            _counts.MirrorFrom(report);
            _countsJson = _counts.ToJson();
            _updatedAtUtc = DateTime.UtcNow;
        }
        NotifySnapshot();
    }

    public void ReportMessage(string message)
    {
        lock (_gate)
        {
            _lastMessage = LiveRunSanitizer.Message(message);
            _updatedAtUtc = DateTime.UtcNow;
        }
        NotifySnapshot();
    }

    public void ReportActivity(
        LiveRunActivityCategory category,
        LiveRunActivityLevel level,
        string message,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var activity = new LiveRunActivity(
            DateTime.UtcNow,
            category,
            level,
            LiveRunSanitizer.Message(message),
            LiveRunSanitizer.Metadata(metadata));
        _feed.Add(activity);
        NotifySnapshot();
    }

    /// <summary>
    /// Snapshot imutável do estado corrente. Contém apenas contadores,
    /// fases e mensagens sanitizadas — nunca credenciais, URLs ou
    /// títulos de origem.
    /// </summary>
    public LiveRunSnapshot BuildSnapshot()
    {
        LiveRunCounts counts;
        LiveRunPhase phase;
        DateTime phaseStartedAtUtc;
        DateTime updatedAtUtc;
        string? lastMessage;
        string countsJson;
        int phaseIndex;
        bool running;

        lock (_gate)
        {
            counts = _counts.Clone();
            phase = _currentPhase;
            phaseStartedAtUtc = _phaseStartedAtUtc;
            updatedAtUtc = _updatedAtUtc;
            lastMessage = _lastMessage;
            countsJson = _countsJson;
            phaseIndex = Math.Max(0, _lastPhaseIndex);
            running = phase is not (LiveRunPhase.Completed or LiveRunPhase.Error);
        }

        return new LiveRunSnapshot
        {
            RunId = RunId,
            Mode = _mode,
            Source = _source,
            StartedAtUtc = _startedAtUtc,
            FinishedAtUtc = null,
            TerminalStatus = LiveRunTerminalStatus.Unknown,
            LastMessage = lastMessage,
            CountsJson = countsJson,
            CurrentPhase = running ? phase : null,
            IsRunning = running,
            PhaseIndex = phaseIndex,
            PhaseStartedAtUtc = phaseStartedAtUtc,
            UpdatedAtUtc = updatedAtUtc,
            Counts = counts,
            RecentActivities = _feed.Snapshot(20),
            Sanitized = true,
        };
    }

    private void PublishActivity(LiveRunPhase phase, string message)
    {
        var level = phase == LiveRunPhase.Error
            ? LiveRunActivityLevel.Error
            : LiveRunActivityLevel.Info;

        _feed.Add(new LiveRunActivity(
            DateTime.UtcNow,
            LiveRunActivityCategory.Phase,
            level,
            message,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["phase"] = phase.ToString(),
                ["phaseIndex"] = ((int)phase).ToString(System.Globalization.CultureInfo.InvariantCulture),
            }));
    }

    private void NotifySnapshot()
    {
        if (_onSnapshot is null) return;
        try
        {
            _onSnapshot(BuildSnapshot());
        }
        catch
        {
            // O sink de snapshot é externo ao monitor; nunca propagar.
        }
    }

    private async Task PersistPhaseTransitionAsync(
        LiveRunPhase phase,
        int phaseIndex,
        string message,
        DateTime now,
        long? previousStepId,
        string countsJson,
        string result,
        bool terminal,
        CancellationToken cancellationToken)
    {
        try
        {
            await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var context = await _dbFactory
                    .CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

                var run = await context.LiveRuns
                    .FirstOrDefaultAsync(r => r.Id == _liveRunId, cancellationToken)
                    .ConfigureAwait(false);
                if (run is null) return;

                if (previousStepId.HasValue)
                {
                    var previous = await context.LiveRunSteps
                        .FirstOrDefaultAsync(s => s.Id == previousStepId.Value, cancellationToken)
                        .ConfigureAwait(false);
                    if (previous is not null && previous.PhaseFinishedAtUtc is null)
                    {
                        previous.PhaseFinishedAtUtc = now;
                        previous.Result = result;
                    }
                }

                var step = new LiveRunStepEntity
                {
                    LiveRunId = _liveRunId,
                    Phase = phase,
                    PhaseIndex = phaseIndex,
                    PhaseStartedAtUtc = now,
                    PhaseFinishedAtUtc = terminal ? now : null,
                    Message = message,
                    Result = result,
                };
                context.LiveRunSteps.Add(step);

                run.LastMessage = message;
                run.UpdatedAtUtc = now;
                run.CountsJson = countsJson;

                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                lock (_gate)
                {
                    _currentStepId = step.Id;
                }
            }
            finally
            {
                _persistGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelamento do run: a instrumentação não o deve agravar.
        }
        catch (Exception ex)
        {
            // Falha de persistência nunca deve quebrar a pipeline.
            _logger.LogWarning(ex,
                "Live run instrumentation: failed to persist phase {Phase} for runId={RunId}",
                phase, RunId);
        }
    }
}
