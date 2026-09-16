using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace m3uCrawler.Services.Validation;

/// <summary>
/// PHASE 9A.1 (2026-09-16): verdadeiro bounded worker pool com
/// serializacao por identidade de account.
///
/// Em vez de criar N Tasks para N work items (como o legacy
/// <c>TestManyBoundedAsync</c>), esta classe lanca EXACTAMENTE
/// <c>workerCount</c> workers pinned. Cada worker le do channel
/// ate' este ficar vazio.
///
/// Para 48k work items com <c>workerCount=8</c>, **48k Tasks NAO
/// sao criadas** — apenas 8. Memory consumption e' O(workerCount).
///
/// <para>
/// Invariante fundamental (Phase 9A.1): dois work items com o mesmo
/// <see cref="IAccountWork.AccountId"/> sao SEMPRE serializados (max
/// 1 in-flight por account), sem global lock.
///
///   - Account A + Account B → paralelo
///   - Account A + Account A → serial
///   - Account A + Account B + Account C → paralelo entre accounts
///
/// O pool mantem um <c>ConcurrentDictionary&lt;string, AccountGate&gt;</c>
/// keyed por AccountId. Cada worker, antes de processar um item,
/// espera pelo semaforo correspondente. Reference counting no dict
/// garante cleanup automatico (a entry e' removida quando nenhum
/// worker a referencia).
/// </para>
/// </summary>
public sealed class AccountBoundedWorkerPool<TWork, TResult>
    where TWork : class, IAccountWork
{
    /// <summary>
    /// Gate per-AccountId. 1 semaphore permite no maximo 1 in-flight
    /// por conta; refcount rastreia quantos workers estao a usar o gate
    /// para cleanup seguro (Remove s' o refcount chegar a zero).
    /// </summary>
    private sealed class AccountGate
    {
        public AccountGate(SemaphoreSlim semaphore)
        {
            Semaphore = semaphore;
        }
        public SemaphoreSlim Semaphore { get; }
        public int RefCount;
    }

    private readonly Channel<TWork> _channel;
    private readonly int _workerCount;
    private readonly Func<TWork, CancellationToken, ValueTask<TResult>> _handler;

    private readonly List<TResult> _results = new();
    private readonly object _resultsLock = new();

    private readonly ConcurrentDictionary<string, AccountGate> _gates
        = new ConcurrentDictionary<string, AccountGate>(StringComparer.Ordinal);

    public int WorkerCount => _workerCount;

    public AccountBoundedWorkerPool(
        int workerCount,
        Channel<TWork> channel,
        Func<TWork, CancellationToken, ValueTask<TResult>> handler)
    {
        if (workerCount < 1) throw new ArgumentOutOfRangeException(nameof(workerCount));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _workerCount = workerCount;
    }

    /// <summary>
    /// Lanca <see cref="WorkerCount"/> tasks pinned. Cada uma consome
    /// ate' o channel fechar. Devolve uma Task que completa quando
    /// todos os workers terminam (ou quando o channel e' completado
    /// e esvaziado).
    /// </summary>
    public async Task<IReadOnlyList<TResult>> RunAsync(CancellationToken externalCt)
    {
        var workers = new Task[_workerCount];
        for (var i = 0; i < _workerCount; i++)
        {
            workers[i] = Task.Run(() => WorkerLoopAsync(externalCt), CancellationToken.None);
        }

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (externalCt.IsCancellationRequested)
        {
            // Esperado quando o caller faz cancelamento.
        }

        lock (_resultsLock)
        {
            return _results.ToArray();
        }
    }

    private async Task WorkerLoopAsync(CancellationToken externalCt)
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(externalCt).ConfigureAwait(false))
            {
                var accountId = item.AccountId;
                var gate = AcquireGate(accountId);

                bool acquired = false;
                try
                {
                    await gate.Semaphore.WaitAsync(externalCt).ConfigureAwait(false);
                    acquired = true;
                }
                catch (OperationCanceledException) when (externalCt.IsCancellationRequested)
                {
                    // Cancellation antes de adquirir semaforo: apenas
                    // libertar refcount (nao houve Release no semaforo).
                    ReleaseGate(accountId, gate);
                    return;
                }
                catch
                {
                    // Outro erro inesperado durante acquire: libertar
                    // refcount e propagar.
                    ReleaseGate(accountId, gate);
                    throw;
                }

                TResult result = default!;
                bool hasResult = false;
                try
                {
                    result = await _handler(item, externalCt).ConfigureAwait(false);
                    hasResult = true;
                }
                catch (OperationCanceledException) when (externalCt.IsCancellationRequested)
                {
                    // Cancellation durante o handler: Release + cleanup
                    // + termina worker.
                    gate.Semaphore.Release();
                    ReleaseGate(accountId, gate);
                    return;
                }
                catch (Exception)
                {
                    // Erros individuais NAO matam o worker; continuam
                    // para o proximo item.
                }
                finally
                {
                    if (acquired)
                    {
                        gate.Semaphore.Release();
                        ReleaseGate(accountId, gate);
                    }
                }

                if (hasResult && result is not null)
                {
                    lock (_resultsLock)
                    {
                        _results.Add(result);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Channel fechado ou token cancelado.
        }
        catch (Exception)
        {
            // O worker morre silenciosamente.
        }
    }

    /// <summary>
    /// Incrementa o RefCount do gate ou cria novo gate.
    /// </summary>
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

    /// <summary>
    /// Decrementa o RefCount; se ficar 0, remove o gate do mapa.
    /// Race-safe: se outro worker re-acquire entre Decrement e Remove,
    /// o Remove falha e mantemos a entry.
    /// </summary>
    private void ReleaseGate(string accountId, AccountGate gate)
    {
        var remaining = Interlocked.Decrement(ref gate.RefCount);
        if (remaining > 0) return;

        if (((ICollection<KeyValuePair<string, AccountGate>>)_gates).Remove(
            KeyValuePair.Create(accountId, gate)))
        {
            try { gate.Semaphore.Dispose(); } catch { /* ignore */ }
        }
        // Se Remove falhou, outra thread re-adicionou a entry com o
        // mesmo accountId antes do nosso Remove. Mantemos a entry
        // intacta.
    }
}
