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
    // PHASE 9A-FIX (2026-09-14): cache keyed em vez de HttpClient
    // estatico. Cada combinacao distinta de (ConnectionTimeoutSeconds,
    // OverallTimeoutSeconds) obtem o seu proprio HttpClient partilhado
    // atraves de <see cref="HttpClientFactory"/>. O factory e' internal
    // mas tem uma API publica para testes deterministicos que inspeccionam
    // o SocketsHttpHandler.ConnectTimeout e o HttpClient.Timeout sem
    // reflection. Isto permite que a policy persistida controle os
    // timeouts sem deixar de garantir que existe no MAXIMO um HttpClient
    // por chave (i.e. um HttpClient por valor distinto de
    // ConnectionTimeout/OverallTimeout efectivamente usado em todo o
    // processo). Nao ha um HttpClient por stream/tester.

    /// <summary>
    /// Acesso read-only ao HttpClient partilhado default para
    /// instrumentação de testes (autópsia de timeouts, contagem de
    /// requests, etc.). Não deve ser usado em código de produção —
    /// usar as APIs <see cref="TestM3u8Stream"/>, <see cref="TestMultipleStreams"/>
    /// ou <see cref="RunAsync"/>.
    /// </summary>
    internal static HttpClient SharedHttpClientForTest =>
        HttpClientFactory.DefaultSharedClient;

    private readonly StreamValidationOptions _options;
    private readonly StreamValidationCache _cache;
    private readonly HostFailureTracker _hostTracker;
    private readonly HttpClient _client;
    private readonly SsrfGuard _guard;
    private StreamValidationMetrics? _lastMetrics;

    // W2 (2026-09-19): hook de persistência de falhas de aquisição. Null por
    // defeito (sem persistência); o caller liga-o via
    // SetAcquisitionFailureObserver.
    private IAcquisitionFailureObserver? _failureObserver;

    // PHASE-OBSERVABILITY (2026-09-15): sink de tracing por instancia. Por
    // defeito e' NullTraceSink.Instance (no-op) para nao alterar comportamento
    // dos testes que instanciam M3uTesterService directamente. Pode ser
    // substituido via constructor adicional ou SetTrace(ITraceSink).
    private Validation.ITraceSink _trace = Validation.NullTraceSink.Instance;

    /// <summary>
    /// Construtor legacy. Cria options, cache e hostTracker PRIVADOS.
    /// Mantido para retro-compatibilidade com o Dashboard e com testes
    /// que precisam de tester isolado. Em produção, prefira o construtor
    /// que recebe <see cref="StreamValidationState"/>.
    ///
    /// <paramref name="guard"/> (W2) é injectável apenas para testes; o
    /// default é a política SSRF estrita de produção.
    /// </summary>
    public M3uTesterService(StreamValidationOptions? options = null, SsrfGuard? guard = null)
    {
        _options = (options ?? new StreamValidationOptions()).Clone();
        _options.Sanitize();
        _cache = new StreamValidationCache(_options);
        _hostTracker = new HostFailureTracker();
        _guard = guard ?? SsrfGuard.Default;
        _client = HttpClientFactory.ResolveClient(_options.ConnectionTimeoutSeconds, _options.OverallTimeoutSeconds, _guard).Client;
    }

    /// <summary>
    /// Construtor que recebe o <see cref="StreamValidationState"/>
    /// partilhado do processo. Tester fica "ligado" ao state: usa as
    /// mesmas options, o mesmo cache e o mesmo host-tracker que outros
    /// testers criados a partir do mesmo state.
    ///
    /// Introduzido em 2026-09-13 pela 9A-PROD-WIRING.
    /// </summary>
    public M3uTesterService(StreamValidationState state, SsrfGuard? guard = null)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        _options = state.Options.Clone();
        _cache = state.Cache;
        _hostTracker = state.HostTracker;
        _guard = guard ?? SsrfGuard.Default;
        _client = HttpClientFactory.ResolveClient(_options.ConnectionTimeoutSeconds, _options.OverallTimeoutSeconds, _guard).Client;
    }

    /// <summary>W2 — liga um observador de falhas de aquisição (persistência/agregação).</summary>
    public void SetAcquisitionFailureObserver(IAcquisitionFailureObserver? observer)
        => _failureObserver = observer;

    /// <summary>
    /// W2 — seam de teste: injecta um <see cref="HttpClient"/> com handler
    /// stub para exercitar retry/redirect/body sem rede. Não é usado em
    /// produção.
    /// </summary>
    internal M3uTesterService(StreamValidationOptions options, SsrfGuard guard, HttpClient client)
    {
        _options = (options ?? new StreamValidationOptions()).Clone();
        _options.Sanitize();
        _cache = new StreamValidationCache(_options);
        _hostTracker = new HostFailureTracker();
        _guard = guard ?? SsrfGuard.Default;
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    // PHASE-OBSERVABILITY (2026-09-15): permite associar um sink de tracing
    // ao tester. Por defeito o sink e' NullTraceSink (no-op). O tester
    // permanece em conformidade com a sua API publica (nao ha mudanca
    // de comportamento, apenas de observabilidade).
    public void SetTrace(Validation.ITraceSink trace)
    {
        _trace = trace ?? Validation.NullTraceSink.Instance;
    }

    public StreamValidationOptions Options => _options.Clone();
    public StreamValidationMetrics? LastMetrics => _lastMetrics;

    /// <summary>
    /// Indica se uma excepcao foi causada por um timeout INTERNO do
    /// <see cref="HttpClient"/> ou do <see cref="SocketsHttpHandler"/>
    /// (ConnectTimeout ou HttpClient.Timeout), distinguindo-a de uma
    /// cancellation externa do caller.
    ///
    /// Heuristica observada em .NET 9 (validada por documentacao
    /// Microsoft Learn + dotnet/runtime issues #47484, #63706, #78070,
    /// #103134):
    /// - Quando SocketsHttpHandler.ConnectTimeout dispara, lanca
    ///   <see cref="TaskCanceledException"/> com inner
    ///   <see cref="TimeoutException"/> cuja mensagem comeca por
    ///   "A connection could not be established within the configured
    ///   ConnectTimeout." (eventualmente wrappeado em mais camadas).
    /// - Quando HttpClient.Timeout dispara, lanca
    ///   <see cref="TaskCanceledException"/> com mensagem
    ///   "The request was canceled due to the configured HttpClient.Timeout
    ///   of N seconds elapsing." A cadeia interna tambem traz
    ///   <see cref="TimeoutException"/>.
    /// - Cancellation externa (via token do caller) lanca
    ///   <see cref="OperationCanceledException"/> / <see cref="TaskCanceledException"/>
    ///   SEM <see cref="TimeoutException"/> na cadeia de InnerExceptions.
    ///
    /// Em resumo: a presenca de <see cref="TimeoutException"/> em qq
    /// nivel de InnerException indica timeout interno do HttpClient. A
    /// ausencia indica cancellation externa. Esta heuristica e' mais
    /// robusta do que comparar o CancellationToken da excepcao com
    /// default(CancellationToken) (que pode gerar falsos positivos quando
    /// o caller passa CancellationToken.None).
    /// </summary>
    internal static bool IsHttpClientInternalTimeout(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is TimeoutException) return true;
        }
        return false;
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

    /// <summary>
    /// Variante de <see cref="TestM3u8Stream"/> que devolve também o
    /// <see cref="StreamTestOutcome"/> completo, para que o caller
    /// possa distinguir <c>working</c> de <c>retryable</c> (Timeout,
    /// Network, HTTP 5xx, etc.) usando <see cref="StreamFailureClassifier"/>.
    ///
    /// Foi adicionada para suportar a semântica de retenção da
    /// playlist Telegram (PHASE 9A): um stream existente cujo teste
    /// falhe com uma falha retryable NAO deve ser removido da
    /// playlist. Apenas falhas terminais (404, 401/403) justificam
    /// remoção.
    /// </summary>
    public async Task<(M3uStream Stream, StreamTestOutcome Outcome)> TestM3u8StreamWithOutcomeAsync(
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
        return (BuildStreamFromOutcome(url, title, group, outcome), outcome);
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

    /// <summary>
    /// Testa N streams com backpressure explícita. Resolve o problema
    /// de scheduling explosivo quando o caller precisa de testar streams
    /// individualmente (e.g. <c>--telegram-maintain</c>) — em vez de
    /// <c>existingMain.Select(...).Task.WhenAll</c>, que cria N Tasks
    /// simultâneas, este método cria no máximo
    /// <see cref="StreamValidationOptions.MaxConcurrency"/> workers
    /// activos e processa a lista de forma incremental.
    ///
    /// Semelhante a <see cref="RunAsync"/> em semântica, mas devolve
    /// <see cref="M3uStream"/> em vez de <see cref="StreamTestOutcome"/>
    /// e mantém a ORDEM dos inputs (necessário para o caller mapear
    /// resultado → stream original sem usar dicionário).
    ///
    /// Introduzido em 2026-09-13 pela 9A-PROD-WIRING para resolver o
    /// "48k Tasks" do ciclo <c>--telegram-maintain</c>.
    /// </summary>
    public async Task<List<(M3uStream Stream, StreamTestOutcome Outcome)>> TestManyBoundedAsync(
        IReadOnlyList<(string Url, string Title, string Group)> requests,
        CancellationToken cancellationToken = default)
    {
        var results = new List<(M3uStream, StreamTestOutcome)>(requests.Count);
        if (requests.Count == 0) return results;

        var maxConcurrency = Math.Max(1, _options.MaxConcurrency);
        var semaphore = new SemaphoreSlim(maxConcurrency);
        var metrics = new StreamValidationMetrics { TotalUrls = requests.Count };
        _lastMetrics = metrics;

        var hostCacheStatus = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        var tasks = new List<Task<(M3uStream Stream, StreamTestOutcome Outcome)>>(requests.Count);

        // Materializa indices antes de iterar para preservar a ORDEM
        // dos resultados mesmo que o scheduling dos workers seja
        // nao-determinico.
        var orderedResults = new (M3uStream, StreamTestOutcome)?[requests.Count];

        for (var i = 0; i < requests.Count; i++)
        {
            var idx = i;
            var (url, title, group) = requests[idx];

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    orderedResults[idx] = (
                        BuildStreamFromOutcome(url, title, group,
                            StreamTestOutcome.Empty(url) with { WasShortCircuited = true }),
                        StreamTestOutcome.Empty(url) with { WasShortCircuited = true });
                    metrics.IncrementSkipped();
                    return orderedResults[idx]!.Value;
                }

                try
                {
                    var outcome = await TestSingleInternalAsync(
                        url, _options, hostCacheStatus, metrics, cancellationToken)
                        .ConfigureAwait(false);
                    var stream = BuildStreamFromOutcome(url, title, group, outcome);
                    var pair = (stream, outcome);
                    orderedResults[idx] = pair;
                    return pair;
                }
                finally
                {
                    semaphore.Release();
                }
            }, CancellationToken.None));
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation coop: marca como skipped os que faltam.
            for (var i = 0; i < orderedResults.Length; i++)
            {
                if (orderedResults[i] == null)
                {
                    var (u, t, g) = requests[i];
                    orderedResults[i] = (
                        BuildStreamFromOutcome(u, t, g, StreamTestOutcome.Empty(u) with { WasShortCircuited = true }),
                        StreamTestOutcome.Empty(u) with { WasShortCircuited = true });
                    metrics.IncrementSkipped();
                }
            }
        }

        foreach (var item in orderedResults)
        {
            results.Add(item ?? throw new InvalidOperationException("TestManyBoundedAsync invariant violated."));
        }
        return results;
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

    /// <summary>
    /// PHASE 9A.1: helper para AccountValidator. Fornece um metrics e
    /// hostCacheStatus externos. Mantem semantica identica a
    /// TestSingleInternalAsync. Utilizado pelo AccountValidator para
    /// agregar metrics por account sem criar dict descartavel por stream.
    /// </summary>
    internal async Task<StreamTestOutcome> TestStreamForAccountAsync(
        string url,
        ConcurrentDictionary<string, bool> hostCacheStatus,
        StreamValidationMetrics metrics,
        CancellationToken cancellationToken)
    {
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

    private async Task<StreamTestOutcome> ProbeOnceAsync(string url, StreamValidationOptions options, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Resolve (ou reusa) o HttpClient partilhado que corresponde
            // as options deste tester. Em producao isto devolve sempre
            // o mesmo client para a mesma combinacao de timeouts.
            var client = HttpClientFactory.ResolveClient(options.ConnectionTimeoutSeconds, options.OverallTimeoutSeconds, _guard).Client;

            // W2 — send com redirects manuais reclassificados pela política
            // SSRF. O ConnectCallback do handler valida/fixa o endereço
            // efectivo (anti-rebinding).
            var send = await GuardedHttpRequest.SendFollowingRedirectsAsync(
                client,
                url,
                _guard,
                options,
                uri =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, uri);
                    req.Headers.UserAgent.ParseAdd(options.UserAgent);
                    return req;
                },
                cancellationToken).ConfigureAwait(false);

            sw.Stop();
            if (send.FailureKind != AcquisitionFailureKind.None)
            {
                return new StreamTestOutcome(
                    url, false, null,
                    AcquisitionFailureClassifier.ToStreamFailureKind(send.FailureKind),
                    sw.ElapsedMilliseconds, false, 0, false);
            }

            using var response = send.Response!;
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
    /// Faz download de uma URL de playlist usando o HttpClient partilhado
    /// da PHASE 9A (configurado por
    /// <see cref="StreamValidationOptions.ConnectionTimeoutSeconds"/> e
    /// <see cref="StreamValidationOptions.OverallTimeoutSeconds"/>) e
    /// respeita o <see cref="StreamValidationOptions.OverallTimeout"/>
    /// como timer adicional cooperativo. Usado pelo pipeline Telegram para
    /// obter o conteúdo de uma playlist remota sem criar um
    /// <c>HttpClient</c> próprio por chamada.
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
        // PIPELINE-INC-DIAG (2026-09-14): Classificador de causa de falha.
        // PHASE 9A-FIX (2026-09-14): distingue timeout interno do
        // HttpClient (ConnectTimeout ou HttpClient.Timeout) de uma
        // cancellation externa, atraves de IsHttpClientInternalTimeout.
        // PHASE-OBSERVABILITY (2026-09-15): adicionado tracing detalhado
        // (requestId, request headers, response headers, body timing, dispose).
        // Sem alteracao de comportamento HTTP nem do contrato publico.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(url))
        {
            LogPlaylistDownloadOutcome(url, kind: "InvalidUrl", durationMs: 0, status: 0);
            return (null, false);
        }

        // PHASE-OBSERVABILITY: tracing por request. PipelineTrace.Null se nao
        // disponivel (back-compat com testes legacy).
        var trace = _trace ?? Validation.NullTraceSink.Instance;
        var requestId = "req_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var ctx = new Validation.TraceContext
        {
            RequestId = requestId,
        };
        var safeUrl = CredentialSanitizer.SanitizeUrl(url);
        trace.Information(Validation.TraceCategory.HttpRequestStart, ctx,
            $"host={SafeHost(url)} path={SafePath(url)} safeUrl={safeUrl}");

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(_options.OverallTimeout);
        var headersSw = Stopwatch.StartNew();

        // W2 — retry técnico DENTRO da mesma operação. Nunca cria nova Run
        // nem novo RunId (13-RUNS §8, 19-FAILURE-MODEL §4). Tecto finito:
        // MaxRetries + 1 tentativas (MaxRetries é clamped em Sanitize).
        var maxAttempts = Math.Max(1, Math.Min(_options.MaxRetries + 1, 11));
        var lastKind = AcquisitionFailureKind.Unknown;
        int? lastStatus = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                var send = await GuardedHttpRequest.SendFollowingRedirectsAsync(
                    _client,
                    url,
                    _guard,
                    _options,
                    uri =>
                    {
                        var request = new HttpRequestMessage(HttpMethod.Get, uri);
                        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
                        request.Headers.Add("X-Request-Id", requestId);
                        return request;
                    },
                    attemptCts.Token).ConfigureAwait(false);

                if (send.FailureKind != AcquisitionFailureKind.None)
                {
                    lastKind = send.FailureKind;
                    var elapsedFail = (int)sw.ElapsedMilliseconds;
                    trace.Warning(Validation.TraceCategory.HttpRequestFailed, ctx,
                        $"kind={send.FailureKind} durationMs={elapsedFail} detail={send.Detail}");
                    if (CanRetryAcquisition(lastKind, attempt, maxAttempts, attemptCts, cancellationToken))
                    {
                        await DelayBeforeRetryAsync(attemptCts.Token);
                        continue;
                    }
                    await CompleteAcquisitionFailureAsync(url, lastKind, null, send.Detail, attempt + 1, sw.ElapsedMilliseconds);
                    return (null, false);
                }

                using var response = send.Response!;
                var elapsedHeaders = (int)headersSw.ElapsedMilliseconds;
                var contentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty;
                var contentLength = response.Content.Headers.ContentLength?.ToString() ?? "unknown";
                trace.Information(Validation.TraceCategory.HttpRequestHeaders, ctx,
                    $"status={(int)response.StatusCode} timeToHeadersMs={elapsedHeaders} contentType={contentType} contentLength={contentLength}");

                var elapsed = (int)sw.ElapsedMilliseconds;
                if (!response.IsSuccessStatusCode)
                {
                    int status = (int)response.StatusCode;
                    lastStatus = status;
                    lastKind = AcquisitionFailureClassifier.FromHttpStatus(response.StatusCode);
                    var kind = status == 429
                        ? "Http429"
                        : (status >= 500 ? "Http5xx" : "Http4xx");
                    trace.Warning(Validation.TraceCategory.HttpRequestEnd, ctx,
                        $"kind={kind} status={status} durationMs={elapsed}");
                    if (CanRetryAcquisition(lastKind, attempt, maxAttempts, attemptCts, cancellationToken))
                    {
                        await DelayBeforeRetryAsync(attemptCts.Token);
                        continue;
                    }
                    await CompleteAcquisitionFailureAsync(url, lastKind, status, null, attempt + 1, sw.ElapsedMilliseconds);
                    return (null, false);
                }

                // Body read com timer separado + validação W2:
                // vazio/oversized/encoding inválido são terminais.
                var bodySw = Stopwatch.StartNew();
                var body = await GuardedHttpRequest.ReadBodyAsync(response, _options, attemptCts.Token).ConfigureAwait(false);
                var bodyMs = (int)bodySw.ElapsedMilliseconds;
                if (body.FailureKind != AcquisitionFailureKind.None)
                {
                    lastKind = body.FailureKind;
                    lastStatus = (int)response.StatusCode;
                    trace.Warning(Validation.TraceCategory.HttpRequestFailed, ctx,
                        $"kind={body.FailureKind} durationMs={elapsed} detail={body.Detail}");
                    if (CanRetryAcquisition(lastKind, attempt, maxAttempts, attemptCts, cancellationToken))
                    {
                        await DelayBeforeRetryAsync(attemptCts.Token);
                        continue;
                    }
                    await CompleteAcquisitionFailureAsync(url, lastKind, lastStatus, body.Detail, attempt + 1, sw.ElapsedMilliseconds);
                    return (null, false);
                }

                var bytesRead = body.Content?.Length ?? 0;
                trace.Information(Validation.TraceCategory.HttpRequestBody, ctx,
                    $"bodyBytes={bytesRead} bodyReadMs={bodyMs}");
                trace.Information(Validation.TraceCategory.HttpRequestEnd, ctx,
                    $"kind=HttpSuccess status=200 durationMs={elapsed}");
                return (body.Content, true);
            }
            catch (Exception ex) when (ex is OperationCanceledException && IsHttpClientInternalTimeout(ex))
            {
                var elapsed = (int)sw.ElapsedMilliseconds;
                var connectMs = _options.ConnectionTimeoutSeconds * 1000;
                var kind = elapsed < connectMs + 500
                    ? "HttpConnectTimeout"
                    : "HttpRequestTimeout";
                trace.Warning(Validation.TraceCategory.HttpRequestFailed, ctx,
                    $"kind={kind} durationMs={elapsed} innerException={ex.InnerException?.GetType().Name} innerMessage='{(ex.InnerException?.Message ?? "").Substring(0, Math.Min(120, (ex.InnerException?.Message ?? "").Length))}'",
                    ex);
                lastKind = AcquisitionFailureKind.Timeout;
                if (CanRetryAcquisition(lastKind, attempt, maxAttempts, attemptCts, cancellationToken))
                {
                    await DelayBeforeRetryAsync(attemptCts.Token);
                    continue;
                }
                await CompleteAcquisitionFailureAsync(url, lastKind, null, kind, attempt + 1, sw.ElapsedMilliseconds);
                return (null, false);
            }
            catch (OperationCanceledException) when (attemptCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                var elapsed = (int)sw.ElapsedMilliseconds;
                trace.Warning(Validation.TraceCategory.HttpRequestFailed, ctx,
                    $"kind=Timeout durationMs={elapsed} attemptCts fired");
                await CompleteAcquisitionFailureAsync(url, AcquisitionFailureKind.Timeout, null, "overall-timeout", attempt + 1, sw.ElapsedMilliseconds);
                return (null, false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var elapsed = (int)sw.ElapsedMilliseconds;
                trace.Warning(Validation.TraceCategory.HttpRequestFailed, ctx,
                    $"kind=Cancellation durationMs={elapsed} caller cancellation");
                LogPlaylistDownloadOutcome(url, kind: "Cancellation", durationMs: elapsed, status: 0);
                return (null, false);
            }
            catch (System.Security.Authentication.AuthenticationException ex)
            {
                var elapsed = (int)sw.ElapsedMilliseconds;
                trace.Error(Validation.TraceCategory.HttpRequestFailed, ctx,
                    $"kind=TlsOrConnection durationMs={elapsed} ex={ex.GetType().Name} message='{ex.Message.Substring(0, Math.Min(120, ex.Message.Length))}'", ex);
                lastKind = AcquisitionFailureKind.Network;
                if (CanRetryAcquisition(lastKind, attempt, maxAttempts, attemptCts, cancellationToken))
                {
                    await DelayBeforeRetryAsync(attemptCts.Token);
                    continue;
                }
                await CompleteAcquisitionFailureAsync(url, lastKind, null, "tls", attempt + 1, sw.ElapsedMilliseconds);
                return (null, false);
            }
            catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.HostNotFound)
            {
                var elapsed = (int)sw.ElapsedMilliseconds;
                trace.Error(Validation.TraceCategory.HttpRequestFailed, ctx,
                    $"kind=Dns durationMs={elapsed} socketError={ex.SocketErrorCode}", ex);
                lastKind = AcquisitionFailureKind.Dns;
                if (CanRetryAcquisition(lastKind, attempt, maxAttempts, attemptCts, cancellationToken))
                {
                    await DelayBeforeRetryAsync(attemptCts.Token);
                    continue;
                }
                await CompleteAcquisitionFailureAsync(url, lastKind, null, "dns", attempt + 1, sw.ElapsedMilliseconds);
                return (null, false);
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                var elapsed = (int)sw.ElapsedMilliseconds;
                trace.Error(Validation.TraceCategory.HttpRequestFailed, ctx,
                    $"kind=Socket durationMs={elapsed} socketError={ex.SocketErrorCode}", ex);
                lastKind = AcquisitionFailureKind.Network;
                if (CanRetryAcquisition(lastKind, attempt, maxAttempts, attemptCts, cancellationToken))
                {
                    await DelayBeforeRetryAsync(attemptCts.Token);
                    continue;
                }
                await CompleteAcquisitionFailureAsync(url, lastKind, null, "socket", attempt + 1, sw.ElapsedMilliseconds);
                return (null, false);
            }
            catch (HttpRequestException ex)
            {
                var elapsed = (int)sw.ElapsedMilliseconds;
                var inner = ex.InnerException;
                trace.Error(Validation.TraceCategory.HttpRequestFailed, ctx,
                    $"kind=Network durationMs={elapsed} innerType={inner?.GetType().Name} innerMessage='{(inner?.Message ?? "").Substring(0, Math.Min(120, (inner?.Message ?? "").Length))}'", ex);
                lastKind = AcquisitionFailureClassifier.FromStreamFailureKind(StreamFailureClassifier.ClassifyException(ex));
                if (CanRetryAcquisition(lastKind, attempt, maxAttempts, attemptCts, cancellationToken))
                {
                    await DelayBeforeRetryAsync(attemptCts.Token);
                    continue;
                }
                await CompleteAcquisitionFailureAsync(url, lastKind, null, "network", attempt + 1, sw.ElapsedMilliseconds);
                return (null, false);
            }
            catch (Exception ex)
            {
                var elapsed = (int)sw.ElapsedMilliseconds;
                trace.Error(Validation.TraceCategory.HttpRequestFailed, ctx,
                    $"kind=Unknown durationMs={elapsed} ex={ex.GetType().Name} message='{ex.Message.Substring(0, Math.Min(120, ex.Message.Length))}'", ex);
                lastKind = AcquisitionFailureClassifier.FromStreamFailureKind(StreamFailureClassifier.ClassifyException(ex));
                if (CanRetryAcquisition(lastKind, attempt, maxAttempts, attemptCts, cancellationToken))
                {
                    await DelayBeforeRetryAsync(attemptCts.Token);
                    continue;
                }
                await CompleteAcquisitionFailureAsync(url, lastKind, null, "unknown", attempt + 1, sw.ElapsedMilliseconds);
                return (null, false);
            }
        }

        // Defensivo: garante terminação e registo mesmo que o loop não
        // tenha produzido uma saída (não deve acontecer).
        await CompleteAcquisitionFailureAsync(url, lastKind, lastStatus, null, maxAttempts, sw.ElapsedMilliseconds);
        return (null, false);
    }

    private static bool CanRetryAcquisition(
        AcquisitionFailureKind kind,
        int attempt,
        int maxAttempts,
        CancellationTokenSource attemptCts,
        CancellationToken callerToken)
        => attempt + 1 < maxAttempts
           && !callerToken.IsCancellationRequested
           && !attemptCts.IsCancellationRequested
           && AcquisitionFailureClassifier.IsRetryable(kind);

    private async Task DelayBeforeRetryAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_options.RetryDelay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // O tecto global da operação foi atingido: a próxima iteração
            // termina via cancellation.
        }
    }

    /// <summary>
    /// W2 — terminaliza uma falha de aquisição: notifica o observador (que
    /// persiste na Source e agrega na Run) e regista o diagnóstico. A falha
    /// persistida e a agregação representam a MESMA falha
    /// (19-FAILURE-MODEL §6).
    /// </summary>
    private async Task CompleteAcquisitionFailureAsync(
        string url,
        AcquisitionFailureKind kind,
        int? status,
        string? detail,
        int attempts,
        long durationMs)
    {
        if (_failureObserver is not null && kind != AcquisitionFailureKind.None)
        {
            try
            {
                var info = new AcquisitionFailureInfo(
                    url, kind, status, detail,
                    RetryExhausted: AcquisitionFailureClassifier.IsRetryable(kind),
                    Attempts: attempts);
                await _failureObserver.OnAcquisitionFailureAsync(info, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Persistência nunca pode quebrar a aquisição.
            }
        }
        LogPlaylistDownloadOutcome(url, kind: kind.ToString(), durationMs: (int)durationMs, status: status ?? 0);
    }

    private static string SafeHost(string url)
    {
        try
        {
            return new Uri(url).Host;
        }
        catch
        {
            return "?";
        }
    }

    private static string SafePath(string url)
    {
        try
        {
            var u = new Uri(url);
            var path = u.AbsolutePath ?? "/";
            return path.Length > 128 ? path[..128] + "..." : path;
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>
    /// Diagnostico estruturado de DownloadPlaylistContentAsync. Nao emite
    /// username, password, query string com credenciais, ou URL integral;
    /// apenas host + path. Use para distinguir timeout, cancellation,
    /// rate-limit (429), 5xx, 4xx, erros de rede/TLS/DNS sem perder a
    /// privacidade dos parametros de autenticacao.
    /// </summary>
    private static void LogPlaylistDownloadOutcome(string? url, string kind, int durationMs, int status, string? errorName = null)
    {
        if (string.IsNullOrEmpty(url)) { Console.WriteLine($"[DownloadPlaylist] kind={kind} durationMs={durationMs} status={status}"); return; }
        // Mask the URL: keep scheme + host + path; remove query string fully.
        // (Esta funcao jah preserva as credenciais no caller; ainda assim
        // sanitizamos por defesa em profundidade.)
        string safeUrl;
        try
        {
            var u = new Uri(url);
            safeUrl = $"{u.Scheme}://{u.Host}:{u.Port}{u.AbsolutePath}";
        }
        catch
        {
            // Fallback: tirar tudo apos '?'.
            int q = url.IndexOf('?');
            safeUrl = q > 0 ? url[..q] : url;
        }
        // Cortar path muito longo: 128 chars max.
        if (safeUrl.Length > 200) safeUrl = safeUrl[..200] + "...";
        Console.WriteLine(
            $"[DownloadPlaylist] kind={kind} durationMs={durationMs} status={status}{(errorName == null ? "" : " error=" + errorName)} url={safeUrl}");
    }

    /// <summary>
    /// PHASE 9A.2: exposicao do mapeamento outcome -> M3uStream para o
    /// caminho AccountValidator (TelegramScraperService). Mantem a semantica
    /// exacta de <see cref="TestM3u8Stream"/> (title fallback, LastTested,
    /// ResponseTime).
    /// </summary>
    internal static M3uStream BuildStreamForAccountFromOutcome(
        string url, string title, string group, StreamTestOutcome outcome)
        => BuildStreamFromOutcome(url, title, group, outcome);

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
