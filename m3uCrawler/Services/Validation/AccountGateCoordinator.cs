using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace m3uCrawler.Services.Validation;

/// <summary>
/// PHASE 9A.3 (2026-09-16): coordenador GLOBAL de concorrência por account,
/// com lifetime igual ao run Telegram.
///
/// A Phase 9A.2 usava um <see cref="AccountBoundedWorkerPool{TWork,TResult}"/>
/// local por chamada de <c>TestStreamsAsync</c>. Consequência: dois candidates
/// com o mesmo <c>AccountId</c> processados por workers diferentes usavam
/// pools diferentes e podiam testar streams da MESMA account em paralelo.
///
/// Este coordenador resolve isso vivendo ao nível do run e sendo partilhado
/// por todos os candidate workers:
///
///   - <c>Account X + Account X</c> → serial (gate por AccountId)
///   - <c>Account X + Account Y</c> → paralelo
///   - no máximo <see cref="MaxConcurrentAccounts"/> accounts em execução
///     simultânea (semáforo global)
///
/// A identidade continua a ser definida por <see cref="AccountIdentity"/>
/// ((URL sem password) + username). Password nunca participa.
/// </summary>
public sealed class AccountGateCoordinator : IDisposable
{
    private sealed class AccountGate
    {
        public AccountGate(SemaphoreSlim semaphore) => Semaphore = semaphore;
        public SemaphoreSlim Semaphore { get; }
        public int RefCount;
    }

    private readonly SemaphoreSlim _global;
    private readonly ConcurrentDictionary<string, AccountGate> _gates
        = new ConcurrentDictionary<string, AccountGate>(StringComparer.Ordinal);
    private int _disposed;

    /// <summary>
    /// Limite GLOBAL de accounts a executar testes de streams em simultâneo.
    /// </summary>
    public int MaxConcurrentAccounts { get; }

    public AccountGateCoordinator(int maxConcurrentAccounts)
    {
        var n = maxConcurrentAccounts < 1 ? 1 : maxConcurrentAccounts;
        MaxConcurrentAccounts = n;
        _global = new SemaphoreSlim(n, n);
    }

    /// <summary>
    /// Número de gates actualmente no dicionário. Zero quando não há
    /// operações em voo (usado para validar cleanup determinístico).
    /// </summary>
    internal int ActiveGateCount => _gates.Count;

    /// <summary>
    /// Executa <paramref name="work"/> com exclusão por <paramref name="accountId"/>
    /// e limite global de accounts concorrentes.
    ///
    /// Ordem de aquisição: slot global → gate da account. Todas as libertações
    /// (gate + slot) são feitas em <c>finally</c>, pelo que uma falha ou
    /// cancelamento nunca deixa a account bloqueada nem o slot global preso.
    /// </summary>
    public async Task<T> RunExclusiveAsync<T>(
        string accountId,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        if (accountId == null) throw new ArgumentNullException(nameof(accountId));
        if (work == null) throw new ArgumentNullException(nameof(work));
        ThrowIfDisposed();

        // 1. Slot global (limite MaxConcurrentAccounts). Se o run for
        //    cancelado enquanto aguarda admissão, a operação não entra.
        await _global.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // 2. Gate da account: no máximo 1 operação por AccountId.
            var gate = AcquireGate(accountId);
            var gateAcquired = false;
            try
            {
                await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                gateAcquired = true;

                // 3. Executar.
                return await work(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // 4. Libertar gate.
                if (gateAcquired) gate.Semaphore.Release();
                // 6. Cleanup do gate quando o refcount chega a zero.
                ReleaseGate(accountId, gate);
            }
        }
        finally
        {
            // 5. Libertar slot global.
            _global.Release();
        }
    }

    private AccountGate AcquireGate(string accountId)
    {
        return _gates.AddOrUpdate(
            accountId,
            _ => new AccountGate(new SemaphoreSlim(1, 1)) { RefCount = 1 },
            (_, existing) =>
            {
                Interlocked.Increment(ref existing.RefCount);
                return existing;
            });
    }

    private void ReleaseGate(string accountId, AccountGate gate)
    {
        var remaining = Interlocked.Decrement(ref gate.RefCount);
        if (remaining > 0) return;

        // Remove apenas se ninguém re-adquiriu entretanto. Se outra thread
        // voltou a fazer AcquireGate, o Remove falha e a entry mantém-se.
        if (((ICollection<KeyValuePair<string, AccountGate>>)_gates).Remove(
            KeyValuePair.Create(accountId, gate)))
        {
            try { gate.Semaphore.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Dispose determinístico. Deve ser chamado apenas depois de não existirem
    /// operações em voo (no fim do run).
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _global.Dispose();

        foreach (var pair in _gates)
        {
            try { pair.Value.Semaphore.Dispose(); } catch { /* ignore */ }
        }
        _gates.Clear();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(AccountGateCoordinator));
        }
    }
}
