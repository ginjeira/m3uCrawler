using System;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services.Catalog;
using Microsoft.EntityFrameworkCore;

namespace m3uCrawler.Services.Auth;

/// <summary>
/// PHASE 9C.2 — Store server-side de sessões persistido em SQLite.
///
/// <para>
/// O cookie transporta apenas o <c>SessionId</c> opaco (256 bits). A sessão
/// sobrevive a restart, é revogável (logout), expira e é renovada de forma
/// deslizante com um limite absoluto. O <c>CsrfToken</c> é por sessão.
/// </para>
/// </summary>
public sealed class SessionStore
{
    private readonly IDbContextFactory<ChannelCatalogDbContext> _factory;
    private readonly Func<DateTime> _clock;

    /// <summary>Duração absoluta máxima de uma sessão.</summary>
    public TimeSpan AbsoluteLifetime { get; }

    /// <summary>Janela de renovação deslizante a cada pedido autenticado.</summary>
    public TimeSpan SlidingLifetime { get; }

    public SessionStore(
        IDbContextFactory<ChannelCatalogDbContext> factory,
        TimeSpan? absoluteLifetime = null,
        TimeSpan? slidingLifetime = null,
        Func<DateTime>? clock = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        AbsoluteLifetime = absoluteLifetime ?? TimeSpan.FromHours(12);
        SlidingLifetime = slidingLifetime ?? TimeSpan.FromHours(12);
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public async Task<AdminSessionEntity> CreateAsync(
        long adminUserId,
        CancellationToken cancellationToken = default)
    {
        var now = _clock();
        var session = new AdminSessionEntity
        {
            SessionId = TokenGenerator.NewToken(),
            AdminUserId = adminUserId,
            CsrfToken = TokenGenerator.NewToken(),
            CreatedAtUtc = now,
            ExpiresAtUtc = now + SlidingLifetime,
            LastSeenAtUtc = now,
        };

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        context.AdminSessions.Add(session);
        await context.SaveChangesAsync(cancellationToken);
        return session;
    }

    /// <summary>
    /// Devolve a sessão válida associada ao id, ou <c>null</c> se inexistente,
    /// expirada, ou com o administrador inactivo. Renova a janela deslizante
    /// (sem exceder a duração absoluta).
    /// </summary>
    public async Task<AdminSessionEntity?> GetValidAsync(
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        var now = _clock();
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);

        var session = await context.AdminSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId, cancellationToken);
        if (session == null) return null;

        if (session.ExpiresAtUtc <= now)
        {
            context.AdminSessions.Remove(session);
            await context.SaveChangesAsync(cancellationToken);
            return null;
        }

        var user = await context.AdminUsers
            .FirstOrDefaultAsync(u => u.Id == session.AdminUserId, cancellationToken);
        if (user == null || !user.IsEnabled)
        {
            return null;
        }

        var absoluteCap = session.CreatedAtUtc + AbsoluteLifetime;
        var renewed = now + SlidingLifetime;
        session.LastSeenAtUtc = now;
        session.ExpiresAtUtc = renewed < absoluteCap ? renewed : absoluteCap;
        await context.SaveChangesAsync(cancellationToken);

        return session;
    }

    public async Task DeleteAsync(string? sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var session = await context.AdminSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId, cancellationToken);
        if (session == null) return;
        context.AdminSessions.Remove(session);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock();
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.AdminSessions
            .Where(s => s.ExpiresAtUtc <= now)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
