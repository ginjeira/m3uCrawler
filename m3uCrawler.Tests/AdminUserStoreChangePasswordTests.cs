using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using m3uCrawler.Services.Auth;
using m3uCrawler.Services.Catalog;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W10a — Alteração de password de administrador no <see cref="AdminUserStore"/>:
/// rotação de hash, validação da política e revogação de sessões.
/// </summary>
public class AdminUserStoreChangePasswordTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;

    public AdminUserStoreChangePasswordTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"auth-chpw-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Change_password_rotates_hash_and_revokes_user_sessions()
    {
        var store = new AdminUserStore(_factory);
        await store.CreateFirstAdminAsync("admin", "a-strong-password-12");
        var user = await store.FindByUsernameAsync("admin");
        var previousHash = user!.PasswordHash;

        var sessions = new SessionStore(_factory);
        var firstSession = await sessions.CreateAsync(user.Id);
        var secondSession = await sessions.CreateAsync(user.Id);

        var result = await store.ChangePasswordAsync((int)user.Id, "another-strong-pw-12");

        Assert.Equal(ChangePasswordResult.Changed, result);

        var reloaded = await store.FindByUsernameAsync("admin");
        Assert.NotNull(reloaded);
        // O hash mudou (salt novo) e a nova password verifica-a; a antiga não.
        Assert.NotEqual(previousHash, reloaded!.PasswordHash);
        Assert.True(PasswordHasher.Verify("another-strong-pw-12", reloaded.PasswordHash));
        Assert.False(PasswordHasher.Verify("a-strong-password-12", reloaded.PasswordHash));

        Assert.Null(await store.VerifyCredentialsAsync("admin", "a-strong-password-12"));
        Assert.NotNull(await store.VerifyCredentialsAsync("admin", "another-strong-pw-12"));

        // Todas as sessões do utilizador são revogadas na mesma transacção.
        await using var context = _factory.CreateDbContext();
        Assert.Empty(context.AdminSessions.Where(s => s.AdminUserId == user.Id));
        Assert.Empty(context.AdminSessions.Where(
            s => s.SessionId == firstSession.SessionId || s.SessionId == secondSession.SessionId));
    }

    [Fact]
    public async Task Change_password_with_invalid_password_is_rejected_without_changes()
    {
        var store = new AdminUserStore(_factory);
        await store.CreateFirstAdminAsync("admin", "a-strong-password-12");
        var before = await store.FindByUsernameAsync("admin");
        var hashBefore = before!.PasswordHash;

        var sessions = new SessionStore(_factory);
        var session = await sessions.CreateAsync(before.Id);

        var result = await store.ChangePasswordAsync((int)before.Id, "short");

        Assert.Equal(ChangePasswordResult.InvalidPassword, result);

        var after = await store.FindByUsernameAsync("admin");
        Assert.Equal(hashBefore, after!.PasswordHash);
        Assert.Equal(before.UpdatedAtUtc, after.UpdatedAtUtc);
        Assert.True(PasswordHasher.Verify("a-strong-password-12", after.PasswordHash));

        // Sessões não são revogadas quando a nova password é inválida.
        await using var context = _factory.CreateDbContext();
        Assert.Single(context.AdminSessions.Where(s => s.AdminUserId == before.Id));
        Assert.NotNull(await sessions.GetValidAsync(session.SessionId));
    }

    [Fact]
    public async Task Change_password_returns_user_not_found_for_unknown_id_and_username()
    {
        var store = new AdminUserStore(_factory);
        await store.CreateFirstAdminAsync("admin", "a-strong-password-12");

        var byId = await store.ChangePasswordAsync(987654, "another-strong-pw-12");
        var byUsername = await store.ChangePasswordByUsernameAsync("ghost", "another-strong-pw-12");

        Assert.Equal(ChangePasswordResult.UserNotFound, byId);
        Assert.Equal(ChangePasswordResult.UserNotFound, byUsername);

        // O administrador existente permanece intacto.
        Assert.NotNull(await store.VerifyCredentialsAsync("admin", "a-strong-password-12"));
    }

    [Fact]
    public async Task Change_password_by_username_rotates_hash_and_revokes_user_sessions()
    {
        var store = new AdminUserStore(_factory);
        await store.CreateFirstAdminAsync("admin", "a-strong-password-12");
        var user = await store.FindByUsernameAsync("admin");
        var previousHash = user!.PasswordHash;

        var sessions = new SessionStore(_factory);
        var session = await sessions.CreateAsync(user.Id);

        var result = await store.ChangePasswordByUsernameAsync("admin", "another-strong-pw-12");

        Assert.Equal(ChangePasswordResult.Changed, result);

        var reloaded = await store.FindByUsernameAsync("admin");
        Assert.NotNull(reloaded);
        Assert.NotEqual(previousHash, reloaded!.PasswordHash);
        Assert.True(PasswordHasher.Verify("another-strong-pw-12", reloaded.PasswordHash));
        Assert.Null(await store.VerifyCredentialsAsync("admin", "a-strong-password-12"));

        await using var context = _factory.CreateDbContext();
        Assert.Empty(context.AdminSessions.Where(s => s.AdminUserId == user.Id));
        Assert.Null(await sessions.GetValidAsync(session.SessionId));
    }

    [Fact]
    public async Task Change_password_by_username_returns_invalid_for_weak_password_without_changes()
    {
        var store = new AdminUserStore(_factory);
        await store.CreateFirstAdminAsync("admin", "a-strong-password-12");
        var before = await store.FindByUsernameAsync("admin");

        var result = await store.ChangePasswordByUsernameAsync("admin", "elevenchars");

        Assert.Equal(ChangePasswordResult.InvalidPassword, result);
        var after = await store.FindByUsernameAsync("admin");
        Assert.Equal(before!.PasswordHash, after!.PasswordHash);
    }
}
