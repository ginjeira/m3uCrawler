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
/// Garantias:
/// - Uma chave (connect, overall) -> no maximo um HttpClient (cache estatico).
/// - Multiplos testers com as mesmas options partilham o mesmo HttpClient.
/// - Mudar a policy persistida cria um HttpClient novo para a nova chave
///   sem afectar os testers que ja' referenciem a chave antiga.
/// - NUNCA ha' um HttpClient por stream/tester.
///
/// Exposto via <see cref="CreateConfiguredHttpClient"/> para permitir testes
/// deterministicos que inspeccionam o <see cref="SocketsHttpHandler.ConnectTimeout"/>
/// e o <see cref="HttpClient.Timeout"/> directamente, sem reflection.
/// </summary>
internal static class HttpClientFactory
{
    /// <summary>
    /// Cache keyed por (connectTimeoutSeconds, overallTimeoutSeconds).
    /// O valor e' a SocketsHttpHandler configurada para que testes possam
    /// inspeccionar ConnectTimeout sem reflection.
    /// </summary>
    private static readonly ConcurrentDictionary<(int ConnectSeconds, int OverallSeconds), (HttpClient Client, SocketsHttpHandler Handler)> Cache = new();

    /// <summary>
    /// Acesso read-only ao HttpClient default partilhado, para testes que
    /// precisam de um cliente sem configurar options (e.g.
    /// <c>SharedHttpClientForTest</c> no legacy code). Equivalente a
    /// invocar <see cref="ResolveClient"/> com os defaults de
    /// <see cref="StreamValidationOptions"/>.
    /// </summary>
    internal static HttpClient DefaultSharedClient =>
        ResolveClient(
            StreamValidationOptions.DefaultConnectionTimeoutSeconds,
            StreamValidationOptions.DefaultOverallTimeoutSeconds).Client;

    /// <summary>
    /// Resolve (ou cria uma unica vez) um HttpClient + SocketsHttpHandler
    /// para a combinacao especifica de timeouts. Thread-safe via
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/>.
    /// </summary>
    internal static (HttpClient Client, SocketsHttpHandler Handler) ResolveClient(int connectTimeoutSeconds, int overallTimeoutSeconds)
    {
        var key = (Math.Max(1, connectTimeoutSeconds), Math.Max(1, overallTimeoutSeconds));
        return Cache.GetOrAdd(key, static k => CreateConfiguredClient(k.Item1, k.Item2));
    }

    /// <summary>
    /// Cria um (HttpClient, SocketsHttpHandler) com a configuracao
    /// pedida. Exposto para testes deterministicos (Phase9AFixTests)
    /// poderem inspeccionar o handler sem reflection.
    /// </summary>
    internal static (HttpClient Client, SocketsHttpHandler Handler) CreateConfiguredClient(int connectTimeoutSeconds, int overallTimeoutSeconds)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(connectTimeoutSeconds),
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(overallTimeoutSeconds),
        };
        client.DefaultRequestHeaders.Add("User-Agent", StreamValidationOptions.DefaultUserAgent);
        return (client, handler);
    }
}
