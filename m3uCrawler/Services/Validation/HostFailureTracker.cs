using System.Collections.Concurrent;

namespace m3uCrawler.Services.Validation;

/// <summary>
/// Contabiliza falhas consecutivas por host. Quando a política está activa
/// e o limite é atingido, URLs subsequentes do mesmo host são marcadas
/// como <see cref="StreamFailureKind.Network"/> sem novo request.
/// </summary>
public sealed class HostFailureTracker
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    public int RecordFailure(string host)
    {
        if (string.IsNullOrEmpty(host)) return 0;
        return _counts.AddOrUpdate(host, 1, (_, v) => v + 1);
    }

    public void RecordSuccess(string host)
    {
        if (string.IsNullOrEmpty(host)) return;
        _counts.TryRemove(host, out _);
    }

    public bool IsHostTripped(string host, int threshold)
    {
        if (string.IsNullOrEmpty(host) || threshold <= 0) return false;
        return _counts.TryGetValue(host, out var v) && v >= threshold;
    }

    public void Reset() => _counts.Clear();

    public int CountFor(string host) =>
        string.IsNullOrEmpty(host) ? 0 : _counts.GetValueOrDefault(host);
}
