using System.Net;

namespace m3uCrawler.Services.Validation;

/// <summary>
/// W2 — classificação técnica de uma falha de aquisição de conteúdo
/// (<c>docs/Reestructure/19-FAILURE-MODEL.md §6</c>).
///
/// Esta classificação é técnica e não configurável: não é uma Acquisition
/// Policy. Retryable: Network, DNS, timeout, HTTP 429, HTTP 5xx.
/// Terminal: HTTP 401/403/404, resposta vazia, conteúdo malformado, encoding
/// inválido e resposta oversized.
/// </summary>
public enum AcquisitionFailureKind
{
    None = 0,
    Network = 1,
    Dns = 2,
    Timeout = 3,
    Http429 = 4,
    Http5xx = 5,
    Http4xx = 6,
    Authentication = 7,
    NotFound = 8,
    Empty = 9,
    Malformed = 10,
    InvalidEncoding = 11,
    Oversized = 12,
    Security = 13,
    Redirect = 14,
    Cancelled = 15,
    Unknown = 99,
}

/// <summary>
/// Mapeia a classificação técnica de aquisição para
/// <see cref="StreamFailureClassifier"/>, que continua a ser a autoridade
/// sobre "retryable?".
/// </summary>
public static class AcquisitionFailureClassifier
{
    public static StreamFailureKind ToStreamFailureKind(AcquisitionFailureKind kind) => kind switch
    {
        AcquisitionFailureKind.None => StreamFailureKind.None,
        AcquisitionFailureKind.Network => StreamFailureKind.Network,
        AcquisitionFailureKind.Dns => StreamFailureKind.DnsFailure,
        AcquisitionFailureKind.Timeout => StreamFailureKind.Timeout,
        AcquisitionFailureKind.Http429 => StreamFailureKind.HttpStatus429,
        AcquisitionFailureKind.Http5xx => StreamFailureKind.HttpStatus5xx,
        AcquisitionFailureKind.Http4xx => StreamFailureKind.HttpStatus4xx,
        AcquisitionFailureKind.Authentication => StreamFailureKind.Authentication,
        AcquisitionFailureKind.NotFound => StreamFailureKind.Deterministic,
        AcquisitionFailureKind.Empty => StreamFailureKind.Deterministic,
        AcquisitionFailureKind.Malformed => StreamFailureKind.Deterministic,
        AcquisitionFailureKind.InvalidEncoding => StreamFailureKind.Deterministic,
        AcquisitionFailureKind.Oversized => StreamFailureKind.Deterministic,
        AcquisitionFailureKind.Security => StreamFailureKind.Security,
        AcquisitionFailureKind.Redirect => StreamFailureKind.Deterministic,
        AcquisitionFailureKind.Cancelled => StreamFailureKind.Timeout,
        _ => StreamFailureKind.Unknown,
    };

    /// <summary>Autoridade de retry: delega em <see cref="StreamFailureClassifier.IsRetryable"/>.</summary>
    public static bool IsRetryable(AcquisitionFailureKind kind)
        => StreamFailureClassifier.IsRetryable(ToStreamFailureKind(kind));

    public static AcquisitionFailureKind FromStreamFailureKind(StreamFailureKind kind) => kind switch
    {
        StreamFailureKind.None => AcquisitionFailureKind.None,
        StreamFailureKind.Network => AcquisitionFailureKind.Network,
        StreamFailureKind.DnsFailure => AcquisitionFailureKind.Dns,
        StreamFailureKind.Timeout => AcquisitionFailureKind.Timeout,
        StreamFailureKind.HttpStatus429 => AcquisitionFailureKind.Http429,
        StreamFailureKind.HttpStatus5xx => AcquisitionFailureKind.Http5xx,
        StreamFailureKind.HttpStatus4xx => AcquisitionFailureKind.Http4xx,
        StreamFailureKind.Authentication => AcquisitionFailureKind.Authentication,
        StreamFailureKind.Deterministic => AcquisitionFailureKind.NotFound,
        StreamFailureKind.Security => AcquisitionFailureKind.Security,
        _ => AcquisitionFailureKind.Unknown,
    };

    public static AcquisitionFailureKind FromHttpStatus(HttpStatusCode status)
    {
        var streamKind = StreamFailureClassifier.ClassifyHttpStatus(status);
        return streamKind switch
        {
            StreamFailureKind.Authentication => AcquisitionFailureKind.Authentication,
            StreamFailureKind.Deterministic => AcquisitionFailureKind.NotFound,
            _ => FromStreamFailureKind(streamKind),
        };
    }

    /// <summary>Rótulo estável e não sensível persistido em <c>Source.LastAcquisitionFailureKind</c>.</summary>
    public static string ToPersistedKind(AcquisitionFailureKind kind) => kind.ToString();
}
