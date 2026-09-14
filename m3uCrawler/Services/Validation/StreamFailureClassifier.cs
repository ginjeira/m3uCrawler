using System.Collections.Concurrent;

namespace m3uCrawler.Services.Validation;

public enum StreamFailureKind
{
    None = 0,
    Deterministic = 1,
    Authentication = 2,
    Transient = 3,
    Network = 4,
    DnsFailure = 5,
    ConnectionRefused = 6,
    TlsFailure = 7,
    Timeout = 8,
    HttpStatus4xx = 9,
    HttpStatus5xx = 10,
    HttpStatus429 = 11,
    Unknown = 99,
}

/// <summary>
/// Resultado de uma única tentativa de validação de URL.
/// </summary>
public sealed record StreamTestOutcome(
    string Url,
    bool IsWorking,
    long? HttpStatus,
    StreamFailureKind FailureKind,
    long DurationMs,
    bool FromCache,
    int Attempts,
    bool WasShortCircuited)
{
    public static StreamTestOutcome Empty(string url) =>
        new(url, false, null, StreamFailureKind.Unknown, 0, false, 0, false);
}

/// <summary>
/// Classifica o resultado de um request HTTP ou excepção numa categoria
/// reutilizável que decide se retry faz sentido.
/// </summary>
public static class StreamFailureClassifier
{
    public static StreamFailureKind ClassifyException(Exception ex)
    {
        if (ex is OperationCanceledException || ex is TaskCanceledException || ex is TimeoutException)
        {
            return StreamFailureKind.Timeout;
        }

        if (ex is System.Net.Sockets.SocketException se)
        {
            // 11001 = WSATRY_AGAIN (DNS), 10061 = connection refused,
            // 10054 = connection reset, 10060 = timeout.
            return se.SocketErrorCode switch
            {
                System.Net.Sockets.SocketError.HostNotFound => StreamFailureKind.DnsFailure,
                System.Net.Sockets.SocketError.TryAgain => StreamFailureKind.DnsFailure,
                System.Net.Sockets.SocketError.NoData => StreamFailureKind.DnsFailure,
                System.Net.Sockets.SocketError.HostUnreachable => StreamFailureKind.Network,
                System.Net.Sockets.SocketError.NetworkUnreachable => StreamFailureKind.Network,
                System.Net.Sockets.SocketError.ConnectionRefused => StreamFailureKind.ConnectionRefused,
                System.Net.Sockets.SocketError.ConnectionReset => StreamFailureKind.Network,
                System.Net.Sockets.SocketError.TimedOut => StreamFailureKind.Timeout,
                _ => StreamFailureKind.Network,
            };
        }

        var msg = ex.Message ?? string.Empty;
        if (msg.Contains("SSL", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("TLS", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("authentication", StringComparison.OrdinalIgnoreCase))
        {
            return StreamFailureKind.TlsFailure;
        }

        return StreamFailureKind.Network;
    }

    public static StreamFailureKind ClassifyHttpStatus(System.Net.HttpStatusCode status)
    {
        var code = (int)status;
        if (status == System.Net.HttpStatusCode.NotFound)
            return StreamFailureKind.Deterministic;
        if (status == System.Net.HttpStatusCode.Unauthorized)
            return StreamFailureKind.Authentication;
        if (status == System.Net.HttpStatusCode.Forbidden)
            return StreamFailureKind.Authentication;
        if (status == (System.Net.HttpStatusCode)429)
            return StreamFailureKind.HttpStatus429;
        if (code >= 500 && code < 600)
            return StreamFailureKind.HttpStatus5xx;
        if (code >= 400 && code < 500)
            return StreamFailureKind.HttpStatus4xx;
        return StreamFailureKind.None;
    }

    /// <summary>
    /// Determina se uma falha justifica uma nova tentativa automática.
    /// Nunca se repetem erros determinísticos nem autenticação.
    /// </summary>
    public static bool IsRetryable(StreamFailureKind kind)
    {
        return kind is StreamFailureKind.Timeout
            or StreamFailureKind.Network
            or StreamFailureKind.ConnectionRefused
            or StreamFailureKind.HttpStatus429
            or StreamFailureKind.HttpStatus5xx;
    }
}
