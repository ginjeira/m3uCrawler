using System;

namespace m3uCrawler.Services.LiveRun;

/// <summary>
/// PHASE 9C.4 — Source (origem) de uma execução operacional
/// (Live Run). Não é uma máquina de estados; é um atributo
/// estático que identifica quem pediu o run.
/// </summary>
public enum LiveRunSource
{
    /// <summary>Invocação CLI explícita (<c>--telegram</c>, <c>--telegram-maintain</c>).</summary>
    Cli = 0,

    /// <summary>Disparo automático via <c>ScheduledJobRunner</c>.</summary>
    Scheduler = 1,

    /// <summary>Trigger manual via API (wave 9C.4.4).</summary>
    Manual = 2,
}

/// <summary>
/// PHASE 9C.4 — Mode (modo) de uma execução operacional. Reflecte
/// o parâmetro CLI equivalente sem ambiguidade.
/// </summary>
public enum LiveRunMode
{
    /// <summary>Ciclo Telegram único (não-maintain).</summary>
    Telegram = 0,

    /// <summary>Ciclo Telegram com manutenção (loop, merge, etc.).</summary>
    TelegramMaintain = 1,
}

/// <summary>
/// PHASE 9C.4 — Snapshot imutável exposto pelo
/// <see cref="RunCoordinator"/>. Não confundir com
/// <see cref="Models.RunReport"/>: este é o estado operacional
/// corrente/terminado recente; o <c>RunReport</c> mantém-se
/// como contrato persistente de saída da pipeline Telegram.
/// </summary>
public sealed class LiveRunSnapshot
{
    public string RunId { get; init; } = string.Empty;
    public LiveRunMode Mode { get; init; }
    public LiveRunSource Source { get; init; }
    public DateTime StartedAtUtc { get; init; }
    public DateTime? FinishedAtUtc { get; init; }
    public Catalog.LiveRunTerminalStatus TerminalStatus { get; init; }
    public string? LastMessage { get; init; }
    public string CountsJson { get; init; } = "{}";

    /// <summary>
    /// Fase corrente do run. <c>null</c> se o run terminou (fase
    /// terminal <c>Completed</c>/<c>Error</c>).
    /// </summary>
    public Catalog.LiveRunPhase? CurrentPhase { get; init; }

    /// <summary>
    /// Run ainda em curso. Quando <c>true</c>,
    /// <see cref="FinishedAtUtc"/> é <c>null</c> e
    /// <see cref="CurrentPhase"/> é não-nulo.
    /// </summary>
    public bool IsRunning { get; init; }

    public TimeSpan Duration =>
        (FinishedAtUtc ?? DateTime.UtcNow) - StartedAtUtc;
}

/// <summary>
/// PHASE 9C.4 — Pedido de execução operacional. Produzido por
/// qualquer um dos três pontos de entrada (CLI, scheduler, manual).
/// </summary>
public sealed class LiveRunRequest
{
    public LiveRunMode Mode { get; init; } = LiveRunMode.Telegram;
    public LiveRunSource Source { get; init; } = LiveRunSource.Cli;
    public string? Keyword { get; init; }
    public int? HistoryHours { get; init; }
    public int? MaxStreams { get; init; }
}

/// <summary>
/// PHASE 9C.4 — Resultado de uma execução operacional.
/// </summary>
public sealed class LiveRunOutcome
{
    public LiveRunSnapshot Snapshot { get; init; } = new();
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// PHASE 9C.4 — Excepção lançada quando outra execução está
/// activa. A futura API mapeia esta excepção para 409.
/// </summary>
public sealed class RunAlreadyInProgressException : InvalidOperationException
{
    public string CurrentRunId { get; }
    public DateTime StartedAtUtc { get; }
    public LiveRunMode RunMode { get; }

    /// <summary>
    /// Origem do run activo. Nome <c>RunSource</c> (e não
    /// <c>Source</c>) para não colidir com
    /// <see cref="Exception.Source"/>.
    /// </summary>
    public LiveRunSource RunSource { get; }

    public RunAlreadyInProgressException(string currentRunId, DateTime startedAtUtc, LiveRunMode mode, LiveRunSource source)
        : base($"Run already in progress: runId={currentRunId} mode={mode} source={source} startedAt={startedAtUtc:O}")
    {
        CurrentRunId = currentRunId;
        StartedAtUtc = startedAtUtc;
        RunMode = mode;
        RunSource = source;
    }
}

/// <summary>
/// PHASE 9C.4 — Mapeamento explícito entre os valores das
/// enumerações e a sua representação persistida/wire. O
/// contrato congelado é:
/// <list type="bullet">
///   <item>Mode: <c>telegram</c> | <c>telegram-maintain</c></item>
///   <item>Source: <c>cli</c> | <c>scheduler</c> | <c>manual</c></item>
/// </list>
/// <c>ToString().ToLowerInvariant()</c> não produz estes valores
/// (e.g. <c>TelegramMaintain</c> → "telegrammaintain"), por isso o
/// mapeamento é explícito e é o único ponto de normalização.
/// </summary>
public static class LiveRunWireNames
{
    public const string ModeTelegram = "telegram";
    public const string ModeTelegramMaintain = "telegram-maintain";

    public const string SourceCli = "cli";
    public const string SourceScheduler = "scheduler";
    public const string SourceManual = "manual";

    public static string ToWireName(this LiveRunMode mode) => mode switch
    {
        LiveRunMode.TelegramMaintain => ModeTelegramMaintain,
        _ => ModeTelegram,
    };

    public static string ToWireName(this LiveRunSource source) => source switch
    {
        LiveRunSource.Scheduler => SourceScheduler,
        LiveRunSource.Manual => SourceManual,
        _ => SourceCli,
    };

    /// <summary>
    /// Normaliza uma representação textual de Mode. Valores
    /// desconhecidos (ou <c>null</c>) caem no default
    /// <see cref="LiveRunMode.Telegram"/>.
    /// </summary>
    public static LiveRunMode ParseMode(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            ModeTelegramMaintain or "telegrammaintain" => LiveRunMode.TelegramMaintain,
            _ => LiveRunMode.Telegram,
        };

    /// <summary>
    /// Normaliza uma representação textual de Source. Valores
    /// desconhecidos (ou <c>null</c>) caem no default
    /// <see cref="LiveRunSource.Cli"/>.
    /// </summary>
    public static LiveRunSource ParseSource(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            SourceScheduler => LiveRunSource.Scheduler,
            SourceManual => LiveRunSource.Manual,
            _ => LiveRunSource.Cli,
        };
}
