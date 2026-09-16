namespace m3uCrawler.Services.Validation;

/// <summary>
/// Configuração operacional do teste de streams (PHASE 9A — URL/Stream Validation Performance).
///
/// Todos os campos têm defaults seguros. Os valores podem ser sobrescritos
/// pelo operador (Dashboard → Policies → Stream Validation) e/ou pelo
/// construtor do <see cref="M3uTesterService"/>.
/// </summary>
public sealed class StreamValidationOptions
{
    /// <summary>
    /// Devolve uma cópia fresh com todos os defaults aplicados.
    /// Usado por <see cref="StreamValidationState"/> quando não há
    /// policy persistida.
    /// </summary>
    public static StreamValidationOptions DefaultClone() => new();

    public const int DefaultMaxConcurrency = 8;
    /// <summary>
    /// PHASE 9A.1: numero maximo de accounts/playlists processadas em
    /// paralelo. Canais dentro da MESMA account sao SEMPRE seriais
    /// (hardcoded em <see cref="AccountValidator.MaxConcurrentChannelsPerAccount"/>).
    /// </summary>
    public const int DefaultMaxConcurrentAccounts = 5;
    public const int DefaultConnectionTimeoutSeconds = 5;
    public const int DefaultReadTimeoutSeconds = 8;
    public const int DefaultOverallTimeoutSeconds = 12;
    public const int DefaultMaxRetries = 1;
    public const int DefaultRetryDelayMilliseconds = 250;
    public const int DefaultSuccessCacheTtlSeconds = 60 * 10;
    public const int DefaultFailureCacheTtlSeconds = 60;
    public const int DefaultEarlyExitThreshold = 0;
    public const int DefaultHostFailureThreshold = 3;
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";

    public int MaxConcurrency { get; set; } = DefaultMaxConcurrency;
    /// <summary>
    /// PHASE 9A.1: configuravel; CLAAMPED [1,128].
    /// </summary>
    public int MaxConcurrentAccounts { get; set; } = DefaultMaxConcurrentAccounts;
    public int ConnectionTimeoutSeconds { get; set; } = DefaultConnectionTimeoutSeconds;
    public int ReadTimeoutSeconds { get; set; } = DefaultReadTimeoutSeconds;
    public int OverallTimeoutSeconds { get; set; } = DefaultOverallTimeoutSeconds;
    public int MaxRetries { get; set; } = DefaultMaxRetries;
    public int RetryDelayMilliseconds { get; set; } = DefaultRetryDelayMilliseconds;
    public int SuccessCacheTtlSeconds { get; set; } = DefaultSuccessCacheTtlSeconds;
    public int FailureCacheTtlSeconds { get; set; } = DefaultFailureCacheTtlSeconds;
    public EarlyExitPolicy EarlyExit { get; set; } = EarlyExitPolicy.TestAll;
    public int EarlyExitThreshold { get; set; } = DefaultEarlyExitThreshold;
    public HostFailurePolicy HostFailure { get; set; } = HostFailurePolicy.Off;
    public int HostFailureThreshold { get; set; } = DefaultHostFailureThreshold;
    public string UserAgent { get; set; } = DefaultUserAgent;

    public StreamValidationOptions Clone() => new()
    {
        MaxConcurrency = MaxConcurrency,
        MaxConcurrentAccounts = MaxConcurrentAccounts,
        ConnectionTimeoutSeconds = ConnectionTimeoutSeconds,
        ReadTimeoutSeconds = ReadTimeoutSeconds,
        OverallTimeoutSeconds = OverallTimeoutSeconds,
        MaxRetries = MaxRetries,
        RetryDelayMilliseconds = RetryDelayMilliseconds,
        SuccessCacheTtlSeconds = SuccessCacheTtlSeconds,
        FailureCacheTtlSeconds = FailureCacheTtlSeconds,
        EarlyExit = EarlyExit,
        EarlyExitThreshold = EarlyExitThreshold,
        HostFailure = HostFailure,
        HostFailureThreshold = HostFailureThreshold,
        UserAgent = UserAgent,
    };

    public TimeSpan ConnectionTimeout => TimeSpan.FromSeconds(Math.Max(1, ConnectionTimeoutSeconds));
    public TimeSpan ReadTimeout => TimeSpan.FromSeconds(Math.Max(1, ReadTimeoutSeconds));
    public TimeSpan OverallTimeout => TimeSpan.FromSeconds(Math.Max(1, OverallTimeoutSeconds));
    public TimeSpan RetryDelay => TimeSpan.FromMilliseconds(Math.Max(0, RetryDelayMilliseconds));
    public TimeSpan SuccessCacheTtl => TimeSpan.FromSeconds(Math.Max(0, SuccessCacheTtlSeconds));
    public TimeSpan FailureCacheTtl => TimeSpan.FromSeconds(Math.Max(0, FailureCacheTtlSeconds));

    public void Sanitize()
    {
        MaxConcurrency = Math.Clamp(MaxConcurrency, 1, 256);
        MaxConcurrentAccounts = Math.Clamp(MaxConcurrentAccounts, 1, 128);
        ConnectionTimeoutSeconds = Math.Clamp(ConnectionTimeoutSeconds, 1, 300);
        ReadTimeoutSeconds = Math.Clamp(ReadTimeoutSeconds, 1, 300);
        OverallTimeoutSeconds = Math.Clamp(OverallTimeoutSeconds, 1, 600);
        MaxRetries = Math.Clamp(MaxRetries, 0, 10);
        RetryDelayMilliseconds = Math.Clamp(RetryDelayMilliseconds, 0, 60_000);
        SuccessCacheTtlSeconds = Math.Clamp(SuccessCacheTtlSeconds, 0, 24 * 60 * 60);
        FailureCacheTtlSeconds = Math.Clamp(FailureCacheTtlSeconds, 0, 24 * 60 * 60);
        EarlyExitThreshold = Math.Clamp(EarlyExitThreshold, 0, 10_000);
        HostFailureThreshold = Math.Clamp(HostFailureThreshold, 1, 100);
        if (string.IsNullOrWhiteSpace(UserAgent)) UserAgent = DefaultUserAgent;
    }

    /// <summary>
    /// Versão fluent: aplica <see cref="Sanitize"/> in-place e devolve
    /// <c>this</c> para encadear. Usado por <see cref="StreamValidationState"/>
    /// no construtor para legibilidade.
    /// </summary>
    public StreamValidationOptions Sanitized()
    {
        Sanitize();
        return this;
    }
}

public enum EarlyExitPolicy
{
    TestAll = 0,
    StopAfterFirstValid = 1,
    StopAfterNValid = 2,
}

public enum HostFailurePolicy
{
    Off = 0,
    ShortCircuitOnHostFailure = 1,
}
