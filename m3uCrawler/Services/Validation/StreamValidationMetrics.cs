using System.Diagnostics;

namespace m3uCrawler.Services.Validation;

/// <summary>
/// Recolha de métricas sobre uma corrida de validação. Os contadores são
/// thread-safe e desenhados para serem serializados em JSON sem credenciais.
/// </summary>
public sealed class StreamValidationMetrics
{
    private long _tested;
    private long _succeeded;
    private long _failed;
    private long _timeouts;
    private long _retries;
    private long _cached;
    private long _skipped;
    private long _earlyExits;
    private long _shortCircuited;
    private long _totalDurationMs;
    private long _maxTestDurationMs;
    private long _actualPeakConcurrency;

    public int TotalUrls { get; set; }
    public int HostCount { get; set; }
    public Stopwatch Clock { get; } = Stopwatch.StartNew();

    public long Tested => Interlocked.Read(ref _tested);
    public long Succeeded => Interlocked.Read(ref _succeeded);
    public long Failed => Interlocked.Read(ref _failed);
    public long Timeouts => Interlocked.Read(ref _timeouts);
    public long Retries => Interlocked.Read(ref _retries);
    public long Cached => Interlocked.Read(ref _cached);
    public long Skipped => Interlocked.Read(ref _skipped);
    public long EarlyExits => Interlocked.Read(ref _earlyExits);
    public long ShortCircuited => Interlocked.Read(ref _shortCircuited);
    public long TotalDurationMs => Interlocked.Read(ref _totalDurationMs);
    public long MaxTestDurationMs => Interlocked.Read(ref _maxTestDurationMs);
    public long ActualPeakConcurrency => Interlocked.Read(ref _actualPeakConcurrency);

    public long AverageTestDurationMs =>
        Tested == 0 ? 0 : TotalDurationMs / Tested;

    public Dictionary<string, long> HostsByDuration { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> HostsByRequests { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void IncrementTested() => Interlocked.Increment(ref _tested);
    public void IncrementSucceeded() => Interlocked.Increment(ref _succeeded);
    public void IncrementFailed() => Interlocked.Increment(ref _failed);
    public void IncrementTimeouts() => Interlocked.Increment(ref _timeouts);
    public void IncrementRetries() => Interlocked.Increment(ref _retries);
    public void IncrementCached() => Interlocked.Increment(ref _cached);
    public void IncrementSkipped() => Interlocked.Increment(ref _skipped);
    public void IncrementEarlyExits() => Interlocked.Increment(ref _earlyExits);
    public void IncrementShortCircuited() => Interlocked.Increment(ref _shortCircuited);

    public void AddTestDuration(long ms)
    {
        Interlocked.Add(ref _totalDurationMs, ms);
        long initial, observed;
        do
        {
            initial = Interlocked.Read(ref _maxTestDurationMs);
            observed = Math.Max(initial, ms);
        } while (Interlocked.CompareExchange(ref _maxTestDurationMs, observed, initial) != initial);
    }

    public void TrackConcurrency(int current)
    {
        long initial, observed;
        do
        {
            initial = Interlocked.Read(ref _actualPeakConcurrency);
            observed = Math.Max(initial, current);
        } while (Interlocked.CompareExchange(ref _actualPeakConcurrency, observed, initial) != initial);
    }

    public void TrackHost(string host, long durationMs)
    {
        if (string.IsNullOrEmpty(host)) return;
        lock (HostsByDuration)
        {
            HostsByDuration.TryGetValue(host, out var prev);
            HostsByDuration[host] = prev + durationMs;
        }
        lock (HostsByRequests)
        {
            HostsByRequests.TryGetValue(host, out var prev);
            HostsByRequests[host] = prev + 1;
        }
    }

    public object ToAnonymousObject()
    {
        return new
        {
            totalUrls = TotalUrls,
            tested = Tested,
            succeeded = Succeeded,
            failed = Failed,
            timeouts = Timeouts,
            retries = Retries,
            cached = Cached,
            skipped = Skipped,
            earlyExits = EarlyExits,
            shortCircuited = ShortCircuited,
            totalDurationMs = TotalDurationMs,
            averageTestDurationMs = AverageTestDurationMs,
            maxTestDurationMs = MaxTestDurationMs,
            actualPeakConcurrency = ActualPeakConcurrency,
            hostCount = HostCount,
            topHostsByDuration = HostsByDuration
                .OrderByDescending(kv => kv.Value)
                .Take(10)
                .Select(kv => new { host = kv.Key, totalDurationMs = kv.Value, requests = HostsByRequests.GetValueOrDefault(kv.Key, 0) })
                .ToList(),
        };
    }
}
