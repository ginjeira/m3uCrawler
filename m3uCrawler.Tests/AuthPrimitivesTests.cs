using System;
using m3uCrawler.Services.Auth;
using m3uCrawler.Services.Configuration;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9C.2 — Primitivas de autenticação: hashing, política de credenciais,
/// resolução de modo e throttle de login.
/// </summary>
public class AuthPrimitivesTests
{
    [Fact]
    public void Hash_uses_versioned_phc_format_and_is_salted()
    {
        var first = PasswordHasher.Hash("correct horse battery staple");
        var second = PasswordHasher.Hash("correct horse battery staple");

        Assert.NotEqual(first, second);
        var parts = first.Split('$');
        Assert.Equal(4, parts.Length);
        Assert.Equal(PasswordHasher.AlgorithmId, parts[0]);
        Assert.Equal(PasswordHasher.DefaultIterations.ToString(), parts[1]);
        Assert.True(Convert.FromBase64String(parts[2]).Length >= 16);
        Assert.Equal(32, Convert.FromBase64String(parts[3]).Length);
    }

    [Fact]
    public void Verify_accepts_correct_password_and_rejects_others()
    {
        var hash = PasswordHasher.Hash("a-very-strong-password");

        Assert.True(PasswordHasher.Verify("a-very-strong-password", hash));
        Assert.False(PasswordHasher.Verify("wrong-password-here", hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("pbkdf2-sha256$0$AAAA$BBBB")]
    [InlineData("md5$1000$AAAA$BBBB")]
    [InlineData("pbkdf2-sha256$1000$notbase64$alsobad")]
    public void Verify_rejects_malformed_or_unknown_hashes(string stored)
    {
        Assert.False(PasswordHasher.Verify("whatever-password", stored));
    }

    [Fact]
    public void VerifyDummy_is_false_and_does_not_throw()
    {
        Assert.False(PasswordHasher.VerifyDummy("anything"));
        Assert.False(PasswordHasher.VerifyDummy(string.Empty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    [InlineData("elevenchars")]
    public void Password_policy_rejects_weak_or_short_passwords(string? password)
    {
        Assert.NotNull(CredentialPolicy.ValidatePassword(password));
    }

    [Fact]
    public void Password_policy_accepts_twelve_characters_without_complexity_rules()
    {
        Assert.Null(CredentialPolicy.ValidatePassword("abcabcabcabc"));
        Assert.Null(CredentialPolicy.ValidatePassword("aaaaaaaaaaaa"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad name")]
    [InlineData("sla/sh")]
    public void Username_policy_rejects_invalid_names(string? username)
    {
        Assert.NotNull(CredentialPolicy.ValidateUsername(username));
    }

    [Fact]
    public void Username_policy_accepts_reasonable_names()
    {
        Assert.Null(CredentialPolicy.ValidateUsername("admin"));
        Assert.Null(CredentialPolicy.ValidateUsername("ops.user_1"));
        Assert.Null(CredentialPolicy.ValidateUsername("a@b"));
    }

    [Theory]
    [InlineData(ConfigurationLifecycleState.NotConfigured, false, AuthMode.Bootstrap)]
    [InlineData(ConfigurationLifecycleState.NotConfigured, true, AuthMode.Bootstrap)]
    [InlineData(ConfigurationLifecycleState.Configuring, false, AuthMode.Bootstrap)]
    [InlineData(ConfigurationLifecycleState.Configuring, true, AuthMode.Bootstrap)]
    [InlineData(ConfigurationLifecycleState.Ready, true, AuthMode.UserAuth)]
    [InlineData(ConfigurationLifecycleState.Ready, false, AuthMode.Legacy)]
    public void Auth_mode_resolution_is_deterministic(
        ConfigurationLifecycleState state,
        bool hasAdmin,
        AuthMode expected)
    {
        Assert.Equal(expected, AuthModeResolver.Resolve(state, hasAdmin));
    }

    [Fact]
    public void Login_throttle_blocks_after_repeated_failures_and_recovers_on_reset()
    {
        var throttle = new LoginThrottle(maxFailures: 3, window: TimeSpan.FromMinutes(5), lockout: TimeSpan.FromMinutes(5));
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(throttle.IsBlocked("ip", now, out _));
        throttle.RecordFailure("ip", now);
        throttle.RecordFailure("ip", now);
        Assert.False(throttle.IsBlocked("ip", now, out _));
        throttle.RecordFailure("ip", now);

        Assert.True(throttle.IsBlocked("ip", now, out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);

        throttle.Reset("ip");
        Assert.False(throttle.IsBlocked("ip", now, out _));
    }

    [Fact]
    public void Login_throttle_lockout_expires()
    {
        var throttle = new LoginThrottle(maxFailures: 2, window: TimeSpan.FromMinutes(5), lockout: TimeSpan.FromMinutes(1));
        var now = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

        throttle.RecordFailure("ip", now);
        throttle.RecordFailure("ip", now);
        Assert.True(throttle.IsBlocked("ip", now, out _));
        Assert.False(throttle.IsBlocked("ip", now.AddMinutes(2), out _));
    }
}
