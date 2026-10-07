using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using m3uCrawler.Services.Auth;
using m3uCrawler.Services.Catalog;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W10a — <see cref="AdminPasswordResetService"/>: fluxo host-only de
/// recuperação de password (reader injectado, sem consola nem HTTP).
/// </summary>
public class AdminPasswordResetServiceTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;

    public AdminPasswordResetServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"auth-reset-{Guid.NewGuid():N}");
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

    private static Func<string> ReaderOf(params string[] values)
    {
        var queue = new Queue<string>(values);
        return () => queue.Count > 0 ? queue.Dequeue() : string.Empty;
    }

    [Fact]
    public async Task Matching_valid_passwords_change_the_password()
    {
        var users = new AdminUserStore(_factory);
        await users.CreateFirstAdminAsync("admin", "a-strong-password-12");

        var calls = 0;
        Func<string> reader = () =>
        {
            calls++;
            return calls == 1 ? "new-strong-password-12" : "new-strong-password-12";
        };

        var service = new AdminPasswordResetService(users);
        var outcome = await service.ResetAsync("admin", reader);

        Assert.Equal(AdminPasswordResetOutcome.Changed, outcome);
        Assert.Equal(2, calls);
        Assert.True(PasswordHasher.Verify(
            "new-strong-password-12", (await users.FindByUsernameAsync("admin"))!.PasswordHash));
        Assert.Null(await users.VerifyCredentialsAsync("admin", "a-strong-password-12"));
    }

    [Fact]
    public async Task Mismatched_confirmation_is_rejected_without_changes()
    {
        var users = new AdminUserStore(_factory);
        await users.CreateFirstAdminAsync("admin", "a-strong-password-12");
        var hashBefore = (await users.FindByUsernameAsync("admin"))!.PasswordHash;

        var service = new AdminPasswordResetService(users);
        var outcome = await service.ResetAsync(
            "admin", ReaderOf("new-strong-password-12", "different-password-12"));

        Assert.Equal(AdminPasswordResetOutcome.Mismatch, outcome);
        Assert.Equal(hashBefore, (await users.FindByUsernameAsync("admin"))!.PasswordHash);
        Assert.NotNull(await users.VerifyCredentialsAsync("admin", "a-strong-password-12"));
    }

    [Fact]
    public async Task Invalid_new_password_is_rejected_without_changes()
    {
        var users = new AdminUserStore(_factory);
        await users.CreateFirstAdminAsync("admin", "a-strong-password-12");
        var hashBefore = (await users.FindByUsernameAsync("admin"))!.PasswordHash;

        var service = new AdminPasswordResetService(users);
        var outcome = await service.ResetAsync("admin", ReaderOf("short", "short"));

        Assert.Equal(AdminPasswordResetOutcome.InvalidPassword, outcome);
        Assert.Equal(hashBefore, (await users.FindByUsernameAsync("admin"))!.PasswordHash);
        Assert.NotNull(await users.VerifyCredentialsAsync("admin", "a-strong-password-12"));
    }

    [Fact]
    public async Task Unknown_username_returns_user_not_found()
    {
        var users = new AdminUserStore(_factory);
        await users.CreateFirstAdminAsync("admin", "a-strong-password-12");

        var service = new AdminPasswordResetService(users);
        var outcome = await service.ResetAsync(
            "ghost", ReaderOf("new-strong-password-12", "new-strong-password-12"));

        Assert.Equal(AdminPasswordResetOutcome.UserNotFound, outcome);
        Assert.NotNull(await users.VerifyCredentialsAsync("admin", "a-strong-password-12"));
    }

    [Fact]
    public void Reset_reader_is_argument_free()
    {
        // O serviço consome Func<string> — a password nunca é passada como
        // argumento a um callback (e nunca vem de argv).
        var reset = typeof(AdminPasswordResetService)
            .GetMethod(nameof(AdminPasswordResetService.ResetAsync))!;
        var parameters = reset.GetParameters();

        Assert.Equal(typeof(Func<string>), parameters[1].ParameterType);
        var invoke = typeof(Func<string>).GetMethod("Invoke")!;
        Assert.Empty(invoke.GetParameters());
    }

    [Fact]
    public async Task Downstream_failure_message_does_not_leak_the_password()
    {
        const string secret = "super-secret-password-12";
        // Store cuja BD rebenta: o erro propaga-se e não pode conter a password.
        var failingUsers = new AdminUserStore(new ThrowingDbContextFactory());
        var service = new AdminPasswordResetService(failingUsers);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ResetAsync("admin", ReaderOf(secret, secret)));

        Assert.DoesNotContain(secret, ex.Message);
        Assert.DoesNotContain("pbkdf2", ex.Message);
    }

    private sealed class ThrowingDbContextFactory : Microsoft.EntityFrameworkCore.IDbContextFactory<ChannelCatalogDbContext>
    {
        public ChannelCatalogDbContext CreateDbContext()
            => throw new InvalidOperationException("database unavailable");
    }
}
