using System;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services.Catalog;
using Microsoft.EntityFrameworkCore;

namespace m3uCrawler.Services.Auth;

public enum CreateAdminResult
{
    Created = 0,
    AlreadyExists = 1,
}

/// <summary>
/// PHASE 9C.2 — Acesso a administradores no catálogo SQLite.
///
/// <para>
/// A criação do <b>primeiro</b> administrador é transaccional e recusa-se a
/// criar um segundo: verifica dentro da transacção que a tabela está vazia e
/// a <c>UNIQUE(Username)</c> actua como salvaguarda adicional contra corridas.
/// </para>
/// </summary>
public sealed class AdminUserStore
{
    private readonly IDbContextFactory<ChannelCatalogDbContext> _factory;

    public AdminUserStore(IDbContextFactory<ChannelCatalogDbContext> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task<bool> HasAnyAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.AdminUsers.AnyAsync(cancellationToken);
    }

    public async Task<bool> HasActiveAdminAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.AdminUsers.AnyAsync(u => u.IsEnabled, cancellationToken);
    }

    public async Task<AdminUserEntity?> FindByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.AdminUsers
            .FirstOrDefaultAsync(u => u.Username == username, cancellationToken);
    }

    /// <summary>
    /// Cria o primeiro administrador de forma transaccional. Se já existir
    /// qualquer administrador, devolve <see cref="CreateAdminResult.AlreadyExists"/>
    /// sem alterar nada (idempotente).
    /// </summary>
    public async Task<CreateAdminResult> CreateFirstAdminAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        if (await context.AdminUsers.AnyAsync(cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return CreateAdminResult.AlreadyExists;
        }

        var now = DateTime.UtcNow;
        context.AdminUsers.Add(new AdminUserEntity
        {
            Username = username,
            PasswordHash = PasswordHasher.Hash(password),
            IsEnabled = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return CreateAdminResult.Created;
        }
        catch (DbUpdateException)
        {
            // Corrida com outro pedido: a unique de username (ou o duplicado)
            // impediu a inserção. Nada foi criado.
            await transaction.RollbackAsync(cancellationToken);
            return CreateAdminResult.AlreadyExists;
        }
    }

    /// <summary>
    /// Verifica credenciais. Para utilizador inexistente ou inactivo executa
    /// a derivação dummy (tempo uniforme, sem enumeração) e devolve <c>null</c>.
    /// </summary>
    public async Task<AdminUserEntity?> VerifyCredentialsAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var user = await FindByUsernameAsync(username, cancellationToken);
        if (user == null || !user.IsEnabled)
        {
            PasswordHasher.VerifyDummy(password);
            return null;
        }

        if (!PasswordHasher.Verify(password, user.PasswordHash))
        {
            return null;
        }

        return user;
    }

    public async Task MarkLoginAsync(long adminUserId, DateTime atUtc, CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var user = await context.AdminUsers.FirstOrDefaultAsync(u => u.Id == adminUserId, cancellationToken);
        if (user == null) return;
        user.LastLoginAtUtc = atUtc;
        user.UpdatedAtUtc = atUtc;
        await context.SaveChangesAsync(cancellationToken);
    }
}
