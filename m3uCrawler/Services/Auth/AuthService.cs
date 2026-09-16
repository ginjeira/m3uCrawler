using System;
using System.Threading;
using System.Threading.Tasks;

namespace m3uCrawler.Services.Auth;

public sealed record LoginOutcome(bool Success, AdminSessionEntity? Session, string? Error);

/// <summary>
/// PHASE 9C.2 — Serviço de autenticação humana: login, validação de sessão e
/// logout. Erros são sempre genéricos (<c>invalid-credentials</c>) para não
/// revelar existência de utilizador; o tempo é uniformizado pelo
/// <see cref="AdminUserStore"/> (hash dummy).
/// </summary>
public sealed class AuthService
{
    private readonly AdminUserStore _users;
    private readonly SessionStore _sessions;
    private readonly LoginThrottle _throttle;
    private readonly Func<DateTime> _clock;

    public AuthService(
        AdminUserStore users,
        SessionStore sessions,
        LoginThrottle? throttle = null,
        Func<DateTime>? clock = null)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _throttle = throttle ?? new LoginThrottle();
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public Task<bool> HasActiveAdminAsync(CancellationToken cancellationToken = default)
        => _users.HasActiveAdminAsync(cancellationToken);

    public TimeSpan SessionLifetime => _sessions.AbsoluteLifetime;

    public async Task<LoginOutcome> LoginAsync(
        string? username,
        string? password,
        string? throttleKey,
        CancellationToken cancellationToken = default)
    {
        var key = string.IsNullOrWhiteSpace(throttleKey) ? "unknown" : throttleKey;
        var now = _clock();

        if (_throttle.IsBlocked(key, now, out _))
        {
            return new LoginOutcome(false, null, "too-many-attempts");
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            _throttle.RecordFailure(key, now);
            return new LoginOutcome(false, null, "invalid-credentials");
        }

        var user = await _users.VerifyCredentialsAsync(username.Trim(), password, cancellationToken);
        if (user == null)
        {
            _throttle.RecordFailure(key, now);
            return new LoginOutcome(false, null, "invalid-credentials");
        }

        _throttle.Reset(key);
        await _users.MarkLoginAsync(user.Id, now, cancellationToken);

        // Novo id de sessão em cada login (anti session-fixation).
        var session = await _sessions.CreateAsync(user.Id, cancellationToken);
        return new LoginOutcome(true, session, null);
    }

    public Task<AdminSessionEntity?> ValidateSessionAsync(
        string? sessionId,
        CancellationToken cancellationToken = default)
        => _sessions.GetValidAsync(sessionId, cancellationToken);

    public Task LogoutAsync(string? sessionId, CancellationToken cancellationToken = default)
        => _sessions.DeleteAsync(sessionId, cancellationToken);
}
