using System;
using System.Collections.Concurrent;
using System.Threading;

namespace m3uCrawler.Services.Validation;

/// <summary>
/// Estado de conhecimento de validação física de um <c>ValidationKey</c>
/// (<c>sfp1</c>) dentro de um run de descoberta.
/// </summary>
public enum ValidationKeyState
{
    InProgress = 0,
    Working = 1,
    FailedTerminal = 2,
    FailedTransient = 3,
}

/// <summary>
/// Registo de validação física por run.
///
/// <para>
/// <b>Escopo por run.</b> Instanciado uma vez por run de descoberta Telegram
/// (o <see cref="AccountValidator"/> é criado uma vez por run). Não é
/// persistido — vive apenas em memória e o reset é implícito a cada run
/// (nova instância). <see cref="Reset"/> existe para uso explícito.
/// </para>
///
/// <para>
/// <b>Chave = <c>sfp1</c> (ValidationKey).</b> A chave é o fingerprint
/// produzido por <c>StreamFingerprint.TryComputeFingerprint(url)</c>.
/// URL sanitizado NUNCA é usado como chave. URLs não fingerprintáveis
/// (ex.: <c>rtmp://</c>, <c>udp://</c>) nunca entram no registo.
/// </para>
///
/// <para>
/// <b>Falhas nunca são reutilizáveis.</b> Apenas o estado
/// <see cref="ValidationKeyState.Working"/> é conhecimento reutilizável
/// entre AccountKeys. Resultados transitórios, em progresso, ou
/// curto-circuitados nunca dispensam um GET físico.
/// </para>
///
/// <para>
/// A implementação é thread-safe: um <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// com comparer explícito e contadores actualizados via
/// <see cref="Interlocked"/>. Nenhum dado sensível é registado.
/// </para>
/// </summary>
public sealed class ValidationKeyRegistry
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _physicalCount;
    private long _reuseCount;

    /// <summary>
    /// Entrada imutável associada a um ValidationKey.
    /// </summary>
    public sealed record Entry(ValidationKeyState State, StreamFailureKind FailureKind, DateTime RecordedAtUtc);

    /// <summary>
    /// Marca a chave como <see cref="ValidationKeyState.InProgress"/> se
    /// ainda não existir. Devolve <c>false</c> para chave nula/vazia ou
    /// quando a chave já está presente.
    /// </summary>
    public bool TryBegin(string? validationKey)
    {
        if (string.IsNullOrEmpty(validationKey)) return false;
        return _entries.TryAdd(
            validationKey,
            new Entry(ValidationKeyState.InProgress, StreamFailureKind.None, DateTime.UtcNow));
    }

    /// <summary>
    /// Marca a chave como <see cref="ValidationKeyState.Working"/>.
    /// No-op para chave nula/vazia.
    /// </summary>
    public void MarkWorking(string? validationKey)
    {
        if (string.IsNullOrEmpty(validationKey)) return;
        _entries[validationKey] =
            new Entry(ValidationKeyState.Working, StreamFailureKind.None, DateTime.UtcNow);
    }

    /// <summary>
    /// Marca a chave como falhada. A falha é terminal ou transitória
    /// conforme <see cref="StreamFailureClassifier.IsRetryable"/>.
    /// No-op para chave nula/vazia.
    /// </summary>
    public void MarkFailed(string? validationKey, StreamFailureKind kind)
    {
        if (string.IsNullOrEmpty(validationKey)) return;
        var state = StreamFailureClassifier.IsRetryable(kind)
            ? ValidationKeyState.FailedTransient
            : ValidationKeyState.FailedTerminal;
        _entries[validationKey] = new Entry(state, kind, DateTime.UtcNow);
    }

    /// <summary>
    /// Devolve <c>true</c> apenas quando o estado é
    /// <see cref="ValidationKeyState.Working"/>. Chave nula/vazia devolve
    /// <c>false</c>.
    /// </summary>
    public bool IsKnownWorking(string? validationKey)
    {
        if (string.IsNullOrEmpty(validationKey)) return false;
        return _entries.TryGetValue(validationKey, out var entry)
            && entry.State == ValidationKeyState.Working;
    }

    /// <summary>
    /// Tenta obter o estado da chave. Chave nula/vazia ou ausente devolve
    /// <c>false</c> com <paramref name="state"/> a
    /// <see cref="ValidationKeyState.InProgress"/>.
    /// </summary>
    public bool TryGetState(string? validationKey, out ValidationKeyState state)
    {
        state = ValidationKeyState.InProgress;
        if (string.IsNullOrEmpty(validationKey)) return false;
        if (!_entries.TryGetValue(validationKey, out var entry)) return false;
        state = entry.State;
        return true;
    }

    /// <summary>Contabiliza um GET físico efectuado.</summary>
    public void RecordPhysical() => Interlocked.Increment(ref _physicalCount);

    /// <summary>Contabiliza um GET físico evitado por conhecimento Working.</summary>
    public void RecordReuse() => Interlocked.Increment(ref _reuseCount);

    /// <summary>Total de GETs físicos efectuados neste run.</summary>
    public long PhysicalCount => Interlocked.Read(ref _physicalCount);

    /// <summary>Total de GETs físicos evitados por conhecimento Working.</summary>
    public long ReuseCount => Interlocked.Read(ref _reuseCount);

    /// <summary>Total de chaves registadas (qualquer estado).</summary>
    public int Count => _entries.Count;

    /// <summary>Total de chaves em estado <see cref="ValidationKeyState.Working"/>.</summary>
    public int WorkingKeyCount
    {
        get
        {
            var count = 0;
            foreach (var entry in _entries.Values)
            {
                if (entry.State == ValidationKeyState.Working) count++;
            }
            return count;
        }
    }

    /// <summary>Total de chaves em estado terminal ou transitório de falha.</summary>
    public int FailedKeyCount
    {
        get
        {
            var count = 0;
            foreach (var entry in _entries.Values)
            {
                if (entry.State is ValidationKeyState.FailedTerminal or ValidationKeyState.FailedTransient)
                {
                    count++;
                }
            }
            return count;
        }
    }

    /// <summary>Limpa entradas e contadores. Reset explícito (por run).</summary>
    public void Reset()
    {
        _entries.Clear();
        Interlocked.Exchange(ref _physicalCount, 0);
        Interlocked.Exchange(ref _reuseCount, 0);
    }
}
