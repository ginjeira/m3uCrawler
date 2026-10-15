using System;
using System.IO;
using System.Security.Cryptography;
using m3uCrawler.Services.Security;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Collection que serializa os testes que manipulam a variável de
/// ambiente <c>M3UCRAWLER_ENCRYPTION_KEY</c> (estado de processo),
/// impedindo-os de correr em paralelo com os restantes testes.
/// </summary>
[CollectionDefinition("SecretProtection", DisableParallelization = true)]
public sealed class SecretProtectionCollection
{
}

[Collection("SecretProtection")]
public sealed class SecretProtectorTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dir2;

    public SecretProtectorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"channel-catalog-tests-sp-{Guid.NewGuid():N}");
        _dir2 = Path.Combine(Path.GetTempPath(), $"channel-catalog-tests-sp2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_dir2);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_dir2, recursive: true); } catch { /* best effort */ }
    }

    private string KeyPath => Path.Combine(_dir, SecretProtector.KeyFileName);

    [Fact]
    public void Protect_and_unprotect_round_trip()
    {
        var protector = new SecretProtector(_dir);

        var payload = protector.Protect("playlist-token-123");

        Assert.NotEqual("playlist-token-123", payload);
        Assert.Equal("playlist-token-123", protector.Unprotect(payload));
    }

    [Fact]
    public void Protect_uses_a_fresh_nonce_per_call()
    {
        var protector = new SecretProtector(_dir);

        var first = protector.Protect("same-value");
        var second = protector.Protect("same-value");

        Assert.NotEqual(first, second);
        Assert.Equal("same-value", protector.Unprotect(first));
        Assert.Equal("same-value", protector.Unprotect(second));
    }

    [Fact]
    public void Tampered_payload_returns_null()
    {
        var protector = new SecretProtector(_dir);
        var payload = protector.Protect("do-not-tamper");

        var raw = Convert.FromBase64String(payload);
        raw[^1] ^= 0xFF;

        Assert.Null(protector.Unprotect(Convert.ToBase64String(raw)));
    }

    [Fact]
    public void Wrong_key_returns_null()
    {
        var protector = new SecretProtector(_dir);
        var other = new SecretProtector(_dir2);

        var payload = protector.Protect("cross-key");

        Assert.Null(other.Unprotect(payload));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!!!")]
    [InlineData("AAAA")]
    public void Invalid_payload_returns_null(string? payload)
    {
        var protector = new SecretProtector(_dir);

        Assert.Null(protector.Unprotect(payload!));
    }

    [Fact]
    public void Key_file_is_created_with_32_bytes()
    {
        _ = new SecretProtector(_dir);

        Assert.True(File.Exists(KeyPath));
        var decoded = Convert.FromBase64String(File.ReadAllText(KeyPath).Trim());
        Assert.Equal(32, decoded.Length);
    }

    [Fact]
    public void Unix_key_file_mode_is_owner_only()
    {
        if (OperatingSystem.IsWindows())
            return;

        _ = new SecretProtector(_dir);

        var mode = File.GetUnixFileMode(KeyPath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void File_key_is_persisted_and_reloaded()
    {
        var protector = new SecretProtector(_dir);
        var payload = protector.Protect("persisted-secret");

        var reloaded = new SecretProtector(_dir);

        Assert.Equal("file", reloaded.KeySource);
        Assert.Equal("persisted-secret", reloaded.Unprotect(payload));
    }

    [Fact]
    public void Valid_env_key_is_preferred_over_file()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);

        WithEnv(Convert.ToBase64String(key), () =>
        {
            var protector = new SecretProtector(_dir);

            Assert.Equal("env", protector.KeySource);
            var payload = protector.Protect("env-secret");
            Assert.Equal("env-secret", protector.Unprotect(payload));
            Assert.Equal("env-secret", new SecretProtector(_dir).Unprotect(payload));
        });
    }

    [Fact]
    public void Invalid_env_key_throws_without_fallback()
    {
        WithEnv("not-valid-base64!!!", () =>
        {
            Assert.Throws<InvalidOperationException>(() => new SecretProtector(_dir));
        });

        WithEnv(Convert.ToBase64String(new byte[16]), () =>
        {
            Assert.Throws<InvalidOperationException>(() => new SecretProtector(_dir));
        });
    }

    private static void WithEnv(string value, Action action)
    {
        var original = Environment.GetEnvironmentVariable(SecretProtector.EnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(SecretProtector.EnvironmentVariableName, value);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretProtector.EnvironmentVariableName, original);
        }
    }
}
