using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;

namespace m3uCrawler.Services.Validation;

/// <summary>
/// Factory para <see cref="HttpClient"/> partilhado usado pelo
/// <see cref="Services.M3uTesterService"/>.
///
/// PHASE 9A-FIX (2026-09-14): substituiu o antigo <c>SharedHttpClient</c>
/// static (que tinha ConnectTimeout hardcoded para 5s e HttpClient.Timeout
/// hardcoded para 12s). Agora o factory devolve um HttpClient parametrizado
/// por <see cref="StreamValidationOptions.ConnectionTimeoutSeconds"/> e
/// <see cref="StreamValidationOptions.OverallTimeoutSeconds"/>, com cache
/// keyed para garantir no maximo um HttpClient por combinacao distinta
/// em todo o processo.
///
/// W2 (2026-09-19): o handler é endurecido contra SSRF:
/// <list type="bullet">
///   <item><c>AllowAutoRedirect = false</c> — os redirects são seguidos
///         manualmente pela camada de aquisição, que reclassifica cada
///         salto;</item>
///   <item><c>UseProxy = false</c> — não há proxy que possa contornar a
///         validação de endereço;</item>
///   <item><c>ConnectCallback = SafeConnector</c> — resolve+valida+liga ao
///         endereço efectivo, sem segunda resolução.</item>
/// </list>
///
/// Garantias:
/// - Uma chave (connect, overall, guard) -> no maximo um HttpClient.
/// - Multiplos testers com as mesmas options/guard partilham o mesmo client.
/// - NUNCA ha' um HttpClient por stream/tester.
/// </summary>
internal static class HttpClientFactory
{
    /// <summary>
    /// Cache keyed por (connectTimeoutSeconds, overallTimeoutSeconds, guard).
    /// O guard faz parte da chave para que um guard de teste não partilhe
    /// handler com o guard estrito de produção.
    /// </summary>
    private static readonly ConcurrentDictionary<(int ConnectSeconds, int OverallSeconds, SsrfGuard Guard), (HttpClient Client, SocketsHttpHandler Handler)> Cache = new();

    internal static HttpClient DefaultSharedClient =>
        ResolveClient(
            StreamValidationOptions.DefaultConnectionTimeoutSeconds,
            StreamValidationOptions.DefaultOverallTimeoutSeconds).Client;

    internal static (HttpClient Client, SocketsHttpHandler Handler) ResolveClient(int connectTimeoutSeconds, int overallTimeoutSeconds)
        => ResolveClient(connectTimeoutSeconds, overallTimeoutSeconds, SsrfGuard.Default);

    internal static (HttpClient Client, SocketsHttpHandler Handler) ResolveClient(
        int connectTimeoutSeconds, int overallTimeoutSeconds, SsrfGuard guard)
    {
        var key = (
            Math.Max(1, connectTimeoutSeconds),
            Math.Max(1, overallTimeoutSeconds),
            guard ?? SsrfGuard.Default);
        return Cache.GetOrAdd(key, static k => CreateConfiguredClient(k.Item1, k.Item2, k.Item3));
    }

    /// <summary>
    /// Cria um (HttpClient, SocketsHttpHandler) estrito. Mantém a assinatura
    /// histórica para os testes existentes.
    /// </summary>
    internal static (HttpClient Client, SocketsHttpHandler Handler) CreateConfiguredClient(int connectTimeoutSeconds, int overallTimeoutSeconds)
        => CreateConfiguredClient(connectTimeoutSeconds, overallTimeoutSeconds, SsrfGuard.Default);

    /// <summary>
    /// Cria um (HttpClient, SocketsHttpHandler) com o guard indicado. Exposto
    /// para testes deterministicos inspeccionarem o handler sem reflection.
    /// </summary>
    internal static (HttpClient Client, SocketsHttpHandler Handler) CreateConfiguredClient(
        int connectTimeoutSeconds, int overallTimeoutSeconds, SsrfGuard guard)
    {
        guard ??= SsrfGuard.Default;
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(connectTimeoutSeconds),
            AllowAutoRedirect = false,
            UseProxy = false,
        };
        handler.ConnectCallback = (context, cancellationToken) =>
            SafeConnector.ConnectAsync(context, cancellationToken, guard, SafeConnector.DefaultConnectAsync);

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(overallTimeoutSeconds),
        };
        client.DefaultRequestHeaders.Add("User-Agent", StreamValidationOptions.DefaultUserAgent);
        return (client, handler);
    }
}
