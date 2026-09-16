using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace m3uCrawler.Services.Auth;

/// <summary>
/// PHASE 9C.2 — Utilitários de tokens opacos (sessão e CSRF).
/// </summary>
public static class TokenGenerator
{
    public const int TokenSizeBytes = 32;

    /// <summary>Gera um token opaco de 256 bits codificado em base64url (sem padding).</summary>
    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenSizeBytes);
        return Base64UrlEncode(bytes);
    }

    public static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

/// <summary>
/// PHASE 9C.2 — Limitação de tentativas de login em memória, por chave
/// (IP/cliente). Determinística quanto a tempo via <c>nowUtc</c> injectável,
/// o que a torna testável sem esperas reais.
/// </summary>
public sealed class LoginThrottle
{
    private sealed class Entry
    {
        public int Failures;
        public DateTime WindowStartUtc;
        public DateTime? LockedUntilUtc;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _maxFailures;
    private readonly TimeSpan _window;
    private readonly TimeSpan _lockout;

    public LoginThrottle(
        int maxFailures = 5,
        TimeSpan? window = null,
        TimeSpan? lockout = null)
    {
        _maxFailures = maxFailures > 0 ? maxFailures : 5;
        _window = window ?? TimeSpan.FromMinutes(5);
        _lockout = lockout ?? TimeSpan.FromMinutes(5);
    }

    public bool IsBlocked(string key, DateTime nowUtc, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (string.IsNullOrEmpty(key) || !_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (entry.LockedUntilUtc is { } until)
        {
            if (until > nowUtc)
            {
                retryAfter = until - nowUtc;
                return true;
            }

            entry.LockedUntilUtc = null;
            entry.Failures = 0;
            entry.WindowStartUtc = nowUtc;
        }

        return false;
    }

    public void RecordFailure(string key, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(key)) return;

        var entry = _entries.GetOrAdd(key, _ => new Entry { WindowStartUtc = nowUtc });
        lock (entry)
        {
            if (nowUtc - entry.WindowStartUtc > _window)
            {
                entry.Failures = 0;
                entry.WindowStartUtc = nowUtc;
            }

            entry.Failures++;
            if (entry.Failures >= _maxFailures)
            {
                entry.LockedUntilUtc = nowUtc + _lockout;
            }
        }
    }

    public void Reset(string key)
    {
        if (!string.IsNullOrEmpty(key))
        {
            _entries.TryRemove(key, out _);
        }
    }
}
