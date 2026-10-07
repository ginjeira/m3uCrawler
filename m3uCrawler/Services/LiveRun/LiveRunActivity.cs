using System;
using System.Collections.Generic;

namespace m3uCrawler.Services.LiveRun;

/// <summary>
/// PHASE 9C.4 — Categoria de uma actividade do feed em memória.
/// Não existe tabela de actividades: o feed é volátil (process-wide)
/// e destina-se à futura API de polling.
/// </summary>
public enum LiveRunActivityCategory
{
    Run = 0,
    Phase = 1,
    Telegram = 2,
    Playlist = 3,
    Xtream = 4,
    Stream = 5,
    Dispatcharr = 6,
    System = 7,
}

/// <summary>Nível de severidade de uma actividade.</summary>
public enum LiveRunActivityLevel
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// PHASE 9C.4 — Entrada do feed de actividades. A mensagem e os
/// valores de <see cref="Metadata"/> já estão sanitizados
/// (<see cref="LiveRunSanitizer"/>) antes de entrarem no feed.
/// </summary>
public sealed record LiveRunActivity(
    DateTime TimestampUtc,
    LiveRunActivityCategory Category,
    LiveRunActivityLevel Level,
    string Message,
    IReadOnlyDictionary<string, string>? Metadata);

/// <summary>
/// PHASE 9C.4 — Ring buffer limitado e thread-safe para actividades
/// de uma execução. Capacidade fixa (default
/// <see cref="DefaultCapacity"/>); quando cheio, a entrada mais
/// antiga é descartada. Nunca toca em disco nem em SQLite.
///
/// <para>
/// A capacidade default foi fixada em 200: suficiente para cobrir
/// todas as transições de fase e um número útil de eventos por
/// candidato durante uma janela de polling, sem crescimento
/// ilimitado. Esta é a capacidade definida para a subwave 3.
/// </para>
/// </summary>
public sealed class LiveRunActivityFeed
{
    public const int DefaultCapacity = 200;

    private readonly LiveRunActivity?[] _buffer;
    private readonly object _gate = new();
    private long _added;

    public LiveRunActivityFeed(int capacity = DefaultCapacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be >= 1.");
        Capacity = capacity;
        _buffer = new LiveRunActivity?[capacity];
    }

    public int Capacity { get; }

    /// <summary>Total de actividades adicionadas desde o início (inclui descartadas).</summary>
    public long TotalAdded
    {
        get { lock (_gate) return _added; }
    }

    /// <summary>Número de entradas actualmente retidas (≤ <see cref="Capacity"/>).</summary>
    public int Count
    {
        get { lock (_gate) return (int)Math.Min(_added, Capacity); }
    }

    public void Add(LiveRunActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        lock (_gate)
        {
            _buffer[_added % Capacity] = activity;
            _added++;
        }
    }

    /// <summary>
    /// Cópia imutável das actividades retidas, da mais recente para
    /// a mais antiga.
    /// </summary>
    public IReadOnlyList<LiveRunActivity> Snapshot() => Snapshot(int.MaxValue);

    /// <summary>
    /// Cópia imutável das <paramref name="max"/> actividades mais
    /// recentes, da mais recente para a mais antiga.
    /// </summary>
    public IReadOnlyList<LiveRunActivity> Snapshot(int max)
    {
        if (max <= 0) return Array.Empty<LiveRunActivity>();

        lock (_gate)
        {
            var retained = (int)Math.Min(_added, Capacity);
            var take = Math.Min(retained, max);
            var result = new List<LiveRunActivity>(take);
            for (var i = 0; i < take; i++)
            {
                var index = (_added - 1 - i) % Capacity;
                if (index < 0) index += Capacity;
                var entry = _buffer[index];
                if (entry is not null) result.Add(entry);
            }
            return result;
        }
    }
}
