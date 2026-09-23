using System;
using System.Threading;

namespace m3uCrawler.Services.Sync;

/// <summary>
/// W-API-DISPATCHARR-HTTP-IMPLEMENTATION (DL-128 D8):
/// Gate de concorrência dedicado ao Dispatcharr. Garante no máximo
/// uma sincronização Dispatcharr activa por runtime, independente do
/// gate Telegram (<see cref="LiveRun.RunCoordinator"/>) e sem
/// reutilizar o seu estado.
///
/// <para>
/// Princípios:
/// <list type="bullet">
///   <item>Aquisição atómica via <see cref="Interlocked.CompareExchange(ref int, int, int)"/>
///         — não depende apenas de um flag <c>IsRunning</c>.</item>
///   <item>Segunda tentativa concorrente lança
///         <see cref="DispatcharrConcurrencyConflictException"/> (mapeada para 409).</item>
///   <item>Libertação garantida em <c>finally</c> via <see cref="DispatcharrSyncLease.Dispose"/>.</item>
/// </list>
/// </para>
/// </summary>
public sealed class DispatcharrConcurrencyGate
{
    private int _active;

    public bool IsActive => Volatile.Read(ref _active) == 1;

    public DispatcharrSyncLease Acquire()
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            throw new DispatcharrConcurrencyConflictException(
                "Another Dispatcharr sync is already in progress.");
        }
        return new DispatcharrSyncLease(this);
    }

    internal void Release() => Interlocked.Exchange(ref _active, 0);
}

/// <summary>
/// Lease devolvido por <see cref="DispatcharrConcurrencyGate.Acquire"/>.
/// Implementa <see cref="IDisposable"/> para que o chamador use
/// <c>using</c> e garanta a libertação do gate mesmo em caso de
/// excepção.
/// </summary>
public sealed class DispatcharrSyncLease : IDisposable
{
    private readonly DispatcharrConcurrencyGate _gate;
    private bool _disposed;

    internal DispatcharrSyncLease(DispatcharrConcurrencyGate gate)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _gate.Release();
        _disposed = true;
    }
}

/// <summary>
/// Excepção lançada por <see cref="DispatcharrConcurrencyGate.Acquire"/>
/// quando já existe uma sincronização Dispatcharr activa no mesmo
/// processo. Mapeada para HTTP 409 no handler.
/// </summary>
public sealed class DispatcharrConcurrencyConflictException : Exception
{
    public DispatcharrConcurrencyConflictException(string message) : base(message) { }
}
