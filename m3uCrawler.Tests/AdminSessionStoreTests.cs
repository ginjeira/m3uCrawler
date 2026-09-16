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
/// PHASE 9C.2 — Stores de administrador e sessão em SQLite.
/// </summary>
public class AdminSessionStoreTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;

    public AdminSessionStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"auth-store-{Guid.NewGuid():N}");
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
    public async Task Create_first_admin_is_idempotent_and_stores_only_a_hash()
    {
        var store = new AdminUserStore(_factory);

        var first = await store.CreateFirstAdminAsync("admin", "a-strong-password-12");
        var second = await store.CreateFirstAdminAsync("other", "another-strong-pw-12");

        Assert.Equal(CreateAdminResult.Created, first);
        Assert.Equal(CreateAdminResult.AlreadyExists, second);

        await using var context = _factory.CreateDbContext();
        var users = context.AdminUsers.ToList();
        var user = Assert.Single(users);
        Assert.Equal("admin", user.Username);
        Assert.DoesNotContain("a-strong-password-12", user.PasswordHash);
        Assert.StartsWith(PasswordHasher.AlgorithmId, user.PasswordHash);
    }

    [Fact]
    public async Task Concurrent_create_first_admin_never_creates_two()
    {
        var store = new AdminUserStore(_factory);

        var results = await Task.WhenAll(
            store.CreateFirstAdminAsync("admin-a", "a-strong-password-12"),
            store.CreateFirstAdminAsync("admin-b", "a-strong-password-12"));

        await using var context = _factory.CreateDbContext();
        Assert.Equal(1, context.AdminUsers.Count());
        Assert.Contains(CreateAdminResult.Created, results);
    }

    [Fact]
    public async Task Verify_credentials_handles_wrong_unknown_and_disabled()
    {
        var store = new AdminUserStore(_factory);
        await store.CreateFirstAdminAsync("admin", "a-strong-password-12");

        Assert.NotNull(await store.VerifyCredentialsAsync("admin", "a-strong-password-12"));
        Assert.Null(await store.VerifyCredentialsAsync("admin", "wrong-password-12"));
        Assert.Null(await store.VerifyCredentialsAsync("ghost", "a-strong-password-12"));

        await using (var context = _factory.CreateDbContext())
        {
            var user = context.AdminUsers.Single();
            user.IsEnabled = false;
            context.SaveChanges();
        }

        Assert.Null(await store.VerifyCredentialsAsync("admin", "a-strong-password-12"));
        Assert.False(await store.HasActiveAdminAsync());
    }

    [Fact]
    public async Task Session_survives_restart_and_is_revocable()
    {
        var users = new AdminUserStore(_factory);
        await users.CreateFirstAdminAsync("admin", "a-strong-password-12");
        var user = await users.FindByUsernameAsync("admin");

        var store = new SessionStore(_factory);
        var session = await store.CreateAsync(user!.Id);

        // Simula restart: nova instância de store sobre a mesma BD.
        var rehydrated = await new SessionStore(_factory).GetValidAsync(session.SessionId);
        Assert.NotNull(rehydrated);
        Assert.Equal(session.CsrfToken, rehydrated!.CsrfToken);

        await store.DeleteAsync(session.SessionId);
        Assert.Null(await store.GetValidAsync(session.SessionId));
    }

    [Fact]
    public async Task Expired_session_is_invalid_and_removed()
    {
        var users = new AdminUserStore(_factory);
        await users.CreateFirstAdminAsync("admin", "a-strong-password-12");
        var user = await users.FindByUsernameAsync("admin");

        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        var clockValue = now;
        var store = new SessionStore(
            _factory,
            absoluteLifetime: TimeSpan.FromHours(1),
            slidingLifetime: TimeSpan.FromMinutes(30),
            clock: () => clockValue);

        var session = await store.CreateAsync(user!.Id);

        clockValue = now.AddHours(2);
        Assert.Null(await store.GetValidAsync(session.SessionId));

        await using var context = _factory.CreateDbContext();
        Assert.Empty(context.AdminSessions.Where(s => s.Id == session.Id));
    }

    [Fact]
    public async Task Session_is_invalid_when_admin_is_disabled()
    {
        var users = new AdminUserStore(_factory);
        await users.CreateFirstAdminAsync("admin", "a-strong-password-12");
        var user = await users.FindByUsernameAsync("admin");

        var store = new SessionStore(_factory);
        var session = await store.CreateAsync(user!.Id);

        await using (var context = _factory.CreateDbContext())
        {
            var entity = context.AdminUsers.Single();
            entity.IsEnabled = false;
            context.SaveChanges();
        }

        Assert.Null(await store.GetValidAsync(session.SessionId));
    }

    [Fact]
    public async Task Each_login_rotates_the_session_id_and_csrf_token()
    {
        var users = new AdminUserStore(_factory);
        await users.CreateFirstAdminAsync("admin", "a-strong-password-12");
        var auth = new AuthService(users, new SessionStore(_factory));

        var first = await auth.LoginAsync("admin", "a-strong-password-12", "ip");
        var second = await auth.LoginAsync("admin", "a-strong-password-12", "ip");

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.NotEqual(first.Session!.SessionId, second.Session!.SessionId);
        Assert.NotEqual(first.Session.CsrfToken, second.Session.CsrfToken);
    }

    [Fact]
    public async Task Login_returns_generic_error_for_wrong_or_unknown_credentials()
    {
        var users = new AdminUserStore(_factory);
        await users.CreateFirstAdminAsync("admin", "a-strong-password-12");
        var auth = new AuthService(users, new SessionStore(_factory));

        var wrong = await auth.LoginAsync("admin", "wrong-password-12", "ip-a");
        var unknown = await auth.LoginAsync("ghost", "a-strong-password-12", "ip-b");

        Assert.False(wrong.Success);
        Assert.False(unknown.Success);
        Assert.Equal("invalid-credentials", wrong.Error);
        Assert.Equal(unknown.Error, wrong.Error);
    }

    [Fact]
    public async Task Login_is_throttled_after_repeated_failures()
    {
        var users = new AdminUserStore(_factory);
        await users.CreateFirstAdminAsync("admin", "a-strong-password-12");
        var auth = new AuthService(
            users,
            new SessionStore(_factory),
            new LoginThrottle(maxFailures: 2, window: TimeSpan.FromMinutes(5), lockout: TimeSpan.FromMinutes(5)));

        await auth.LoginAsync("admin", "wrong-password-12", "ip");
        await auth.LoginAsync("admin", "wrong-password-12", "ip");
        var blocked = await auth.LoginAsync("admin", "a-strong-password-12", "ip");

        Assert.False(blocked.Success);
        Assert.Equal("too-many-attempts", blocked.Error);
    }

    [Fact]
    public async Task Logout_invalidates_the_session()
    {
        var users = new AdminUserStore(_factory);
        await users.CreateFirstAdminAsync("admin", "a-strong-password-12");
        var auth = new AuthService(users, new SessionStore(_factory));

        var login = await auth.LoginAsync("admin", "a-strong-password-12", "ip");
        Assert.NotNull(await auth.ValidateSessionAsync(login.Session!.SessionId));

        await auth.LogoutAsync(login.Session.SessionId);
        Assert.Null(await auth.ValidateSessionAsync(login.Session.SessionId));
    }
}
