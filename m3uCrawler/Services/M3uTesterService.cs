using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using m3uCrawler.Models;
using m3uCrawler.Services.Validation;

namespace m3uCrawler.Services;

/// <summary>
/// Validador de streams HTTP. Esta versão é a PHASE 9A (URL/Stream Validation
/// Performance) do <see cref="M3uTesterService"/>: reusa um HttpClient,
/// propaga CancellationToken, suporta concorrência configurável, retries
/// apenas em falhas retryable, classificação de falhas, cache TTL, early-exit
/// opcional e short-circuit por host. As decisões operacionais vivem em
/// <see cref="StreamValidationOptions"/> e são persistidas pelo
/// <see cref="StreamValidationPolicyStore"/>.
/// </summary>
public sealed class M3uTesterService : IDisposable
{
    private static readonly HttpClient SharedHttpClient = CreateSharedClient();

    /// <summary>
    /// Acesso read-only ao HttpClient partilhado para instrumentação
    /// de testes (autópsia de timeouts, contagem de requests, etc.).
    /// Não deve ser usado em código de produção — usar as APIs
    /// <see cref="TestM3u8Stream"/>, <see cref="TestMultipleStreams"/>
    /// ou <see cref="RunAsync"/>.
    /// </summary>
    internal static HttpClient SharedHttpClientForTest => SharedHttpClient;

    private readonly StreamValidationOptions _options;
    private readonly StreamValidationCache _cache;
    private readonly HostFailureTracker _hostTracker = new();
    private StreamValidationMetrics? _lastMetrics;

    public M3uTesterService(StreamValidationOptions? options = null)
    {
        _options = (options ?? new StreamValidationOptions()).Clone();
        _options.Sanitize();
        _cache = new StreamValidationCache(_options);
    }

    public StreamValidationOptions Options => _options.Clone();
    public StreamValidationMetrics? LastMetrics => _lastMetrics;

    private static HttpClient CreateSharedClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(StreamValidationOptions.DefaultConnectionTimeoutSeconds),
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(StreamValidationOptions.DefaultOverallTimeoutSeconds),
        };
        client.DefaultRequestHeaders.Add("User-Agent", StreamValidationOptions.DefaultUserAgent);
        return client;
    }

    public async Task<M3uStream> TestM3u8Stream(
        string url,
        string title = "",
        string group = "Unknown",
        CancellationToken cancellationToken = default)
    {
        var outcome = await TestSingleInternalAsync(
            url,
            _options,
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
            new StreamValidationMetrics { TotalUrls = 1 },
            cancellationToken);
        _lastMetrics = new StreamValidationMetrics { TotalUrls = 1 };
        return BuildStreamFromOutcome(url, title, group, outcome);
    }

    public async Task<List<M3uStream>> TestMultipleStreams(
        List<string> urls,
        int maxConcurrency = -1,
        CancellationToken cancellationToken = default)
    {
        var effective = new StreamValidationOptions
        {
            MaxConcurrency = maxConcurrency > 0 ? maxConcurrency : _options.MaxConcurrency,
        };
        effective.Sanitize();

        var outcomes = await RunAsync(urls, effective, cancellationToken);
        var list = new List<M3uStream>(outcomes.Count);
        foreach (var o in outcomes)
        {
            list.Add(BuildStreamFromOutcome(o.Url, ExtractTitleFromUrl(o.Url), "Unknown", o));
        }
        return list;
    }

    public async Task<IReadOnlyList<StreamTestOutcome>> RunAsync(
        IReadOnlyList<string> urls,
        StreamValidationOptions? overrideOptions = null,
        CancellationToken cancellationToken = default)
    {
        var effective = (overrideOptions ?? _options).Clone();
        effective.Sanitize();

        var metrics = new StreamValidationMetrics
        {
            TotalUrls = urls.Count,
            HostCount = urls.Select(ExtractHost)
                .Where(h => !string.IsNullOrEmpty(h))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
        };
        _lastMetrics = metrics;

        var results = new StreamTestOutcome[urls.Count];
        var validCount = 0;
        var semaphore = new SemaphoreSlim(effective.MaxConcurrency);
        var currentActive = 0;
        var hostCacheStatus = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var tasks = new List<Task>(urls.Count);
        for (var i = 0; i < urls.Count; i++)
        {
            var idx = i;
            var url = urls[idx];

            if (ShouldEarlyExit(effective, Interlocked.CompareExchange(ref validCount, 0, 0)))
            {
                results[idx] = StreamTestOutcome.Empty(url) with { WasShortCircuited = true };
                metrics.IncrementSkipped();
                metrics.IncrementEarlyExits();
                continue;
            }

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await semaphore.WaitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    results[idx] = StreamTestOutcome.Empty(url) with { WasShortCircuited = true };
                    metrics.IncrementSkipped();
                    return;
                }
                try
                {
                    var active = Interlocked.Increment(ref currentActive);
                    metrics.TrackConcurrency(active);

                    var outcome = await TestSingleInternalAsync(url, effective, hostCacheStatus, metrics, cts.Token);
                    results[idx] = outcome;
                    if (outcome.IsWorking)
                    {
                        var vc = Interlocked.Increment(ref validCount);
                        if (effective.EarlyExit == EarlyExitPolicy.StopAfterFirstValid
                            || (effective.EarlyExit == EarlyExitPolicy.StopAfterNValid
                                && effective.EarlyExitThreshold > 0
                                && vc >= effective.EarlyExitThreshold))
                        {
                            cts.Cancel();
                        }
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref currentActive);
                    semaphore.Release();
                }
            }, CancellationToken.None));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // cancellation cooperativo entre workers (early-exit) — esperada.
        }

        for (var i = 0; i < results.Length; i++)
        {
            if (results[i] == null)
            {
                results[i] = StreamTestOutcome.Empty(urls[i]) with { WasShortCircuited = true };
                metrics.IncrementSkipped();
            }
        }

        return results;
    }

    private static bool ShouldEarlyExit(StreamValidationOptions options, int validCount)
    {
        return options.EarlyExit switch
        {
            EarlyExitPolicy.StopAfterFirstValid => validCount > 0,
            EarlyExitPolicy.StopAfterNValid => options.EarlyExitThreshold > 0 && validCount >= options.EarlyExitThreshold,
            _ => false,
        };
    }

    public async Task<StreamTestOutcome> TestSingleAsync(string url, CancellationToken cancellationToken = default)
    {
        var metrics = new StreamValidationMetrics { TotalUrls = 1 };
        _lastMetrics = metrics;
        var hostCacheStatus = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        return await TestSingleInternalAsync(url, _options, hostCacheStatus, metrics, cancellationToken);
    }

    private async Task<StreamTestOutcome> TestSingleInternalAsync(
        string url,
        StreamValidationOptions options,
        ConcurrentDictionary<string, bool> hostCacheStatus,
        StreamValidationMetrics metrics,
        CancellationToken cancellationToken)
    {
        var host = ExtractHost(url);

        if (host != null
            && options.HostFailure == HostFailurePolicy.ShortCircuitOnHostFailure
            && _hostTracker.IsHostTripped(host, options.HostFailureThreshold))
        {
            metrics.IncrementSkipped();
            metrics.IncrementShortCircuited();
            return new StreamTestOutcome(url, false, null, StreamFailureKind.Network, 0, false, 0, true);
        }

        if (_cache.TryGet(url, out var cached))
        {
            metrics.IncrementCached();
            metrics.IncrementTested();
            if (cached.IsWorking) metrics.IncrementSucceeded(); else metrics.IncrementFailed();
            metrics.AddTestDuration(0);
            metrics.TrackHost(host ?? string.Empty, 0);
            return cached;
        }

        metrics.IncrementTested();
        StreamTestOutcome? lastOutcome = null;

        for (var attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            if (attempt > 0) metrics.IncrementRetries();

            var attemptStopwatch = Stopwatch.StartNew();
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(options.OverallTimeout);

            var outcome = await ProbeOnceAsync(url, options, attemptCts.Token);
            attemptStopwatch.Stop();
            metrics.AddTestDuration(attemptStopwatch.ElapsedMilliseconds);
            metrics.TrackHost(host ?? string.Empty, attemptStopwatch.ElapsedMilliseconds);

            lastOutcome = outcome with { Attempts = attempt + 1 };
            if (outcome.IsWorking)
            {
                if (host != null) _hostTracker.RecordSuccess(host);
                metrics.IncrementSucceeded();
                break;
            }

            if (!StreamFailureClassifier.IsRetryable(outcome.FailureKind)
                || attempt == options.MaxRetries
                || cancellationToken.IsCancellationRequested
                || attemptCts.IsCancellationRequested)
            {
                if (outcome.FailureKind == StreamFailureKind.Timeout) metrics.IncrementTimeouts();
                if (host != null) _hostTracker.RecordFailure(host);
                metrics.IncrementFailed();
                break;
            }

            try
            {
                await Task.Delay(options.RetryDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        var final = lastOutcome ?? StreamTestOutcome.Empty(url);
        _cache.Store(url, final);
        return final;
    }

    private static async Task<StreamTestOutcome> ProbeOnceAsync(string url, StreamValidationOptions options, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(options.UserAgent);

            using var response = await SharedHttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            sw.Stop();
            if (!response.IsSuccessStatusCode)
            {
                return new StreamTestOutcome(
                    url, false, (long)response.StatusCode,
                    StreamFailureClassifier.ClassifyHttpStatus(response.StatusCode),
                    sw.ElapsedMilliseconds, false, 0, false);
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var isPlaylist = contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
                || url.Contains(".m3u", StringComparison.OrdinalIgnoreCase);

            if (isPlaylist)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (content.Contains("#EXTM3U") || content.Contains("#EXT-X-VERSION") || content.Length > 0)
                {
                    return new StreamTestOutcome(url, true, (long)response.StatusCode, StreamFailureKind.None, sw.ElapsedMilliseconds, false, 0, false);
                }
                return new StreamTestOutcome(url, false, (long)response.StatusCode, StreamFailureKind.Deterministic, sw.ElapsedMilliseconds, false, 0, false);
            }

            return new StreamTestOutcome(url, true, (long)response.StatusCode, StreamFailureKind.None, sw.ElapsedMilliseconds, false, 0, false);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var kind = StreamFailureClassifier.ClassifyException(ex);
            return new StreamTestOutcome(url, false, null, kind, sw.ElapsedMilliseconds, false, 0, false);
        }
    }

    /// <summary>
    /// Faz download de uma URL de playlist usando o
    /// <see cref="SharedHttpClient"/> da PHASE 9A e respeita o
    /// <see cref="StreamValidationOptions.OverallTimeout"/>. Usado
    /// pelo pipeline Telegram para obter o conteúdo de uma playlist
    /// remota sem criar um <c>HttpClient</c> próprio por chamada.
    /// </summary>
    /// <returns>
    /// Tuplo (conteúdo, sucesso). Se o download falhar ou exceder o
    /// timeout, devolve <c>(null, false)</c>. Nunca bloqueia
    /// indefinidamente: termina dentro de
    /// <see cref="StreamValidationOptions.OverallTimeout"/> + uma
    /// pequena tolerância para drenar o socket.
    /// </returns>
    public async Task<(string? Content, bool IsSuccess)> DownloadPlaylistContentAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return (null, false);

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(_options.OverallTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(_options.UserAgent);
            using var response = await SharedHttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (null, false);
            }

            var content = await response.Content
                .ReadAsStringAsync(attemptCts.Token)
                .ConfigureAwait(false);
            return (content, true);
        }
        catch (OperationCanceledException) when (attemptCts.IsCancellationRequested)
        {
            return (null, false);
        }
        catch (Exception)
        {
            return (null, false);
        }
    }

    private static M3uStream BuildStreamFromOutcome(string url, string title, string group, StreamTestOutcome outcome)
    {
        return new M3uStream
        {
            Url = url,
            Title = string.IsNullOrEmpty(title) ? ExtractTitleFromUrl(url) : title,
            Group = group,
            LastTested = DateTime.Now,
            IsWorking = outcome.IsWorking,
            ResponseTime = outcome.DurationMs,
        };
    }

    private static string? ExtractHost(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        return uri.Host;
    }

    private static string ExtractTitleFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var filename = Path.GetFileNameWithoutExtension(uri.LocalPath);
            return string.IsNullOrEmpty(filename) ? "Unknown Stream" : filename;
        }
        catch
        {
            return "Unknown Stream";
        }
    }

    public void Dispose()
    {
        // HttpClient partilhado é processo-wide — não disposed aqui.
    }
}
