using System.Collections.Concurrent;

namespace m3uCrawler.Services.Validation;

/// <summary>
/// Cache de resultados recentes por URL. Concorrência segura, TTLs separados
/// para sucesso e falha. Usado para evitar pedidos repetidos quando a mesma
/// URL aparece em várias execuções.
/// </summary>
public sealed class StreamValidationCache
{
    private sealed record Entry(StreamTestOutcome Outcome, DateTime ExpiresAtUtc);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    // Nao readonly: <see cref="StreamValidationState.ReloadPolicy"/> pode
    // substituir as options para alinhar com novos TTLs. As entradas
    // existentes NAO sao reescritas (o TTL e' congelado em ExpiresAtUtc).
    private StreamValidationOptions _options;

    public StreamValidationCache(StreamValidationOptions options)
    {
        _options = options;
    }

    public bool TryGet(string url, out StreamTestOutcome outcome)
    {
        outcome = StreamTestOutcome.Empty(url);
        if (!_entries.TryGetValue(url, out var entry)) return false;
        if (entry.ExpiresAtUtc <= DateTime.UtcNow)
        {
            _entries.TryRemove(url, out _);
            return false;
        }
        outcome = entry.Outcome with { FromCache = true };
        return true;
    }

    public void Store(string url, StreamTestOutcome outcome)
    {
        var ttl = outcome.IsWorking
            ? _options.SuccessCacheTtl
            : _options.FailureCacheTtl;

        if (ttl <= TimeSpan.Zero) return; // Cache desativado.

        _entries[url] = new Entry(outcome, DateTime.UtcNow.Add(ttl));
    }

    public void Invalidate(string url) => _entries.TryRemove(url, out _);

    public void Clear() => _entries.Clear();

    public int Count => _entries.Count;

    /// <summary>
    /// Substitui as options internas por novas (recarga de policy). As
    /// entradas existentes nao sao reescritas — o TTL e' congelado em
    /// <see cref="Entry.ExpiresAtUtc"/>, e novas entradas usam os novos
    /// TTLs.
    /// </summary>
    public void ApplyNewOptions(StreamValidationOptions newOptions)
    {
        _options = newOptions ?? throw new ArgumentNullException(nameof(newOptions));
    }
}
