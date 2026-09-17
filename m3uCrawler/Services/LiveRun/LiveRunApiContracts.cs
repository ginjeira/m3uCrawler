using System;
using System.Collections.Generic;
using System.Text.Json;
using m3uCrawler.Services.Catalog;

namespace m3uCrawler.Services.LiveRun;

/// <summary>
/// PHASE 9C.4 — Levantada quando o dashboard pede uma execução mas o
/// executor da pipeline Telegram ainda não foi registado
/// (<c>--web</c> standalone sem <c>--telegram</c>). Mapeada para 503.
/// </summary>
public sealed class LiveRunPipelineNotConfiguredException : InvalidOperationException
{
    public LiveRunPipelineNotConfiguredException()
        : base("Telegram pipeline is not configured in this process.")
    {
    }
}

/// <summary>
/// PHASE 9C.4 — Payload aceite por <c>POST /api/run/start</c>.
/// Todos os campos são opcionais; o default coincide com o do CLI.
/// </summary>
public sealed class LiveRunStartPayload
{
    public string? Mode { get; set; }
    public string? Keyword { get; set; }
    public int? HistoryHours { get; set; }
    public int? MaxStreams { get; set; }
}

/// <summary>
/// PHASE 9C.4 — Helpers para converter entre <see cref="LiveRunSnapshot"/>
/// e o JSON exposto pela API. Apenas dados operacionais seguros (sem
/// URLs, credenciais ou títulos).
/// </summary>
internal static class LiveRunApiMappings
{
    /// <summary>
    /// Lista canónica de fases do monitor, exposta como <c>phases[]</c>
    /// na resposta. A ordem reflete a enumeração
    /// <see cref="LiveRunPhase"/>.
    /// </summary>
    public static readonly string[] AllPhases =
    {
        "idle",
        "reading-telegram",
        "discovering",
        "downloading",
        "analyzing",
        "validating",
        "composing",
        "syncing-dispatcharr",
        "completed",
        "error",
    };

    /// <summary>
    /// Mapeia um snapshot + estado do host para o envelope de
    /// resposta usado pelo <c>GET /api/run/status</c>.
    /// </summary>
    public static object ToStatusPayload(
        LiveRunSnapshot? live,
        LiveRunSnapshot? recentFinished,
        bool pipelineConfigured,
        bool webAllowTrigger,
        bool coordinatorRunning = false,
        IReadOnlyList<LiveRunSnapshot>? recentRuns = null)
    {
        if (!pipelineConfigured)
        {
            return new
            {
                isRunning = false,
                status = "pipeline-not-configured",
                lastRun = (object?)null,
                recentRuns = RecentRunsToPayload(recentRuns),
                webAllowTrigger,
            };
        }

        if (live is not null)
        {
            // O snapshot em memória pode já estar terminal enquanto o
            // coordinator ainda detém o lock (janela de milissegundos
            // entre persistir TerminalStatus e libertar o flag). Nesse
            // caso reportamos o estado terminal, nunca "idle".
            if (live.TerminalStatus is LiveRunTerminalStatus.Completed or LiveRunTerminalStatus.Failed)
            {
                return SnapshotToFinishedPayload(live, webAllowTrigger, recentRuns);
            }

            if (live.IsRunning)
            {
                return SnapshotToRunningPayload(live, webAllowTrigger, recentRuns);
            }
        }

        if (coordinatorRunning)
        {
            // O coordinator já reservou o run (flag activo) mas ainda
            // não publicou o snapshot inicial (persistência/monitor a
            // arrancar). Reportamos "running" em vez de "idle" para que
            // o contrato nunca observe uma regressão idle↔running.
            return new
            {
                isRunning = true,
                runId = (string?)null,
                mode = (string?)null,
                status = "running",
                source = "live",
                startedAtUtc = (string?)null,
                lastUpdatedAtUtc = (string?)null,
                finishedAtUtc = (string?)null,
                durationMs = 0L,
                phase = (string?)null,
                phaseIndex = 0,
                phaseStartedAtUtc = (string?)null,
                phases = AllPhases,
                counts = (object?)null,
                recentActivities = (object?)null,
                lastMessage = (string?)null,
                lastRun = (object?)null,
                recentRuns = RecentRunsToPayload(recentRuns),
                webAllowTrigger,
            };
        }

        if (recentFinished is not null)
        {
            return SnapshotToFinishedPayload(recentFinished, webAllowTrigger, recentRuns);
        }

        return new
        {
            isRunning = false,
            status = "idle",
            lastRun = (object?)null,
            recentRuns = RecentRunsToPayload(recentRuns),
            webAllowTrigger,
        };
    }

    private static object? RecentRunsToPayload(IReadOnlyList<LiveRunSnapshot>? recentRuns)
    {
        if (recentRuns is null || recentRuns.Count == 0) return null;
        var items = new List<object>(recentRuns.Count);
        foreach (var run in recentRuns)
        {
            items.Add(SnapshotToRecentRunItem(run));
        }
        return items;
    }

    private static object SnapshotToRunningPayload(
        LiveRunSnapshot s,
        bool webAllowTrigger,
        IReadOnlyList<LiveRunSnapshot>? recentRuns)
    {
        return new
        {
            isRunning = true,
            runId = s.RunId,
            mode = s.Mode.ToWireName(),
            status = "running",
            source = "live",
            startedAtUtc = s.StartedAtUtc.ToString("o"),
            lastUpdatedAtUtc = s.UpdatedAtUtc.ToString("o"),
            finishedAtUtc = (string?)null,
            durationMs = (long)(DateTime.UtcNow - s.StartedAtUtc).TotalMilliseconds,
            phase = s.CurrentPhase?.ToString().ToLowerInvariant(),
            phaseIndex = s.PhaseIndex,
            phaseStartedAtUtc = s.PhaseStartedAtUtc?.ToString("o"),
            phases = AllPhases,
            counts = CountsToPayload(s.Counts),
            recentActivities = ActivitiesToPayload(s.RecentActivities),
            lastMessage = s.LastMessage,
            lastRun = (object?)null,
            recentRuns = RecentRunsToPayload(recentRuns),
            webAllowTrigger,
        };
    }

    private static object SnapshotToFinishedPayload(
        LiveRunSnapshot s,
        bool webAllowTrigger,
        IReadOnlyList<LiveRunSnapshot>? recentRuns)
    {
        var terminal = s.TerminalStatus == LiveRunTerminalStatus.Completed
            ? "completed"
            : (s.TerminalStatus == LiveRunTerminalStatus.Failed ? "failed" : "unknown");

        var status = terminal == "unknown" ? "idle" : terminal;

        return new
        {
            isRunning = false,
            status,
            lastRun = new
            {
                runId = s.RunId,
                mode = s.Mode.ToWireName(),
                source = s.Source.ToWireName(),
                startedAtUtc = s.StartedAtUtc.ToString("o"),
                finishedAtUtc = s.FinishedAtUtc?.ToString("o"),
                durationMs = s.FinishedAtUtc.HasValue
                    ? (long)(s.FinishedAtUtc.Value - s.StartedAtUtc).TotalMilliseconds
                    : 0L,
                phase = s.CurrentPhase?.ToString().ToLowerInvariant(),
                phaseIndex = s.PhaseIndex,
                phaseStartedAtUtc = s.PhaseStartedAtUtc?.ToString("o"),
                phases = AllPhases,
                counts = CountsToPayload(s.Counts),
                recentActivities = ActivitiesToPayload(s.RecentActivities),
                terminalStatus = terminal,
                lastMessage = s.LastMessage,
            },
            recentRuns = RecentRunsToPayload(recentRuns),
            webAllowTrigger,
        };
    }

    /// <summary>
    /// Últimas execuções terminadas, em forma compacta, para a lista
    /// "últimas execuções" do dashboard. Não inclui actividades (o feed
    /// só existe para o run corrente/last in-process).
    /// </summary>
    private static object SnapshotToRecentRunItem(LiveRunSnapshot s)
    {
        var terminal = s.TerminalStatus == LiveRunTerminalStatus.Completed
            ? "completed"
            : (s.TerminalStatus == LiveRunTerminalStatus.Failed ? "failed" : "unknown");

        return new
        {
            runId = s.RunId,
            mode = s.Mode.ToWireName(),
            source = s.Source.ToWireName(),
            terminalStatus = terminal,
            startedAtUtc = s.StartedAtUtc.ToString("o"),
            finishedAtUtc = s.FinishedAtUtc?.ToString("o"),
            durationMs = s.FinishedAtUtc.HasValue
                ? (long)(s.FinishedAtUtc.Value - s.StartedAtUtc).TotalMilliseconds
                : 0L,
            lastMessage = s.LastMessage,
        };
    }

    private static object? ActivitiesToPayload(IReadOnlyList<LiveRunActivity>? activities)
    {
        if (activities is null || activities.Count == 0) return null;

        var items = new List<object>(activities.Count);
        foreach (var a in activities)
        {
            items.Add(new
            {
                timestampUtc = a.TimestampUtc.ToString("o"),
                category = a.Category.ToString().ToLowerInvariant(),
                level = a.Level.ToString().ToLowerInvariant(),
                message = a.Message,
            });
        }
        return items;
    }

    private static object? CountsToPayload(LiveRunCounts? counts)
    {
        if (counts is null) return null;
        // A JSON camelCase reflecte exactamente o contrato tipado.
        var json = counts.ToJson();
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Mapeia o payload de <c>POST /api/run/start</c> para
    /// <see cref="LiveRunRequest"/>. Devolve <c>null</c> se o payload
    /// for inválido.
    /// </summary>
    public static LiveRunRequest? ParseStartPayload(LiveRunStartPayload? payload)
    {
        if (payload is null) return null;

        LiveRunMode mode = LiveRunMode.Telegram;
        if (!string.IsNullOrWhiteSpace(payload.Mode))
        {
            if (!Enum.TryParse<LiveRunMode>(payload.Mode, ignoreCase: true, out mode))
            {
                // Rejeita explicitamente modos desconhecidos.
                return null;
            }
        }

        int? historyHours = payload.HistoryHours is > 0 and <= 24 * 30 ? payload.HistoryHours : null;
        int? maxStreams = payload.MaxStreams is > 0 and <= 5000 ? payload.MaxStreams : null;

        return new LiveRunRequest
        {
            Mode = mode,
            Source = LiveRunSource.Manual,
            Keyword = string.IsNullOrWhiteSpace(payload.Keyword) ? null : payload.Keyword,
            HistoryHours = historyHours,
            MaxStreams = maxStreams,
        };
    }

    public static object ToStartAcceptedPayload(LiveRunSnapshot initialSnapshot)
    {
        return new
        {
            runId = initialSnapshot.RunId,
            startedAtUtc = initialSnapshot.StartedAtUtc.ToString("o"),
            mode = initialSnapshot.Mode.ToWireName(),
            message = "Run started.",
        };
    }

    public static object ToAlreadyRunningPayload(LiveRunSnapshot current)
    {
        return new
        {
            error = "already-running",
            currentRunId = current.RunId,
            startedAtUtc = current.StartedAtUtc.ToString("o"),
            mode = current.Mode.ToWireName(),
        };
    }
}
