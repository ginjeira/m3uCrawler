using System.Text.Json;
using m3uCrawler.Services.Configuration;
using m3uCrawler.Services.Telegram;
using Xunit;

namespace m3uCrawler.Tests;

public sealed class TelegramAuthServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _configPath;

    public TelegramAuthServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "m3ucrawler-telegram-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _configPath = Path.Combine(_dir, "wtelegram.config");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private TelegramAuthService CreateService(
        Func<TelegramBackendOptions, ITelegramAuthBackend> factory,
        Func<string, bool>? sessionFileExists = null)
        => new(new WtelegramConfigStore(_configPath), factory, sessionFileExists ?? (_ => false));

    private IReadOnlyDictionary<string, string> ReadRaw()
        => new WtelegramConfigStore(_configPath).Read();

    [Fact]
    public async Task Start_returns_waiting_code_then_submit_code_authenticates()
    {
        var service = CreateService(_ => new FakeBackend(
            begin: _ => "verification_code",
            submit: _ => null));

        var started = await service.StartAsync("12345", "hash-value", "+351900000000");
        Assert.Equal(TelegramAuthState.WaitingCode, started.State);
        Assert.False(service.IsAuthenticated);

        var done = await service.SubmitCodeAsync("54321");
        Assert.Equal(TelegramAuthState.Authenticated, done.State);
        Assert.Equal("fake-user", done.UserName);
        Assert.True(service.IsAuthenticated);
    }

    [Fact]
    public async Task Start_returns_waiting_password_then_submit_password_authenticates()
    {
        var backend = new FakeBackend(
            begin: _ => "password",
            submit: _ => null);
        var service = CreateService(_ => backend);

        var started = await service.StartAsync("12345", "hash-value", "+351900000000");
        Assert.Equal(TelegramAuthState.WaitingPassword, started.State);

        var done = await service.SubmitPasswordAsync("s3cret-2fa");
        Assert.Equal(TelegramAuthState.Authenticated, done.State);
        Assert.True(service.IsAuthenticated);
    }

    [Fact]
    public async Task Invalid_code_returns_error_without_leaking_the_code()
    {
        var service = CreateService(_ => new FakeBackend(
            begin: _ => "verification_code",
            submit: _ => throw new Exception("PHONE_CODE_INVALID")));

        await service.StartAsync("12345", "hash-value", "+351900000000");
        var status = await service.SubmitCodeAsync("999999");

        Assert.Equal(TelegramAuthState.Error, status.State);
        Assert.Contains("PHONE_CODE_INVALID", status.Detail);
        Assert.DoesNotContain("999999", status.Detail);
        Assert.False(service.IsAuthenticated);
    }

    [Fact]
    public async Task Invalid_code_error_detail_redacts_submitted_secret()
    {
        var service = CreateService(_ => new FakeBackend(
            begin: _ => "verification_code",
            submit: value => throw new Exception($"PHONE_CODE_INVALID:{value}")));

        await service.StartAsync("12345", "hash-value", "+351900000000");
        var status = await service.SubmitCodeAsync("SUPER-SECRET-CODE");

        Assert.Equal(TelegramAuthState.Error, status.State);
        Assert.DoesNotContain("SUPER-SECRET-CODE", status.Detail);
        Assert.Contains("[redacted]", status.Detail);
    }

    [Fact]
    public async Task Wrong_two_factor_password_returns_error()
    {
        var service = CreateService(_ => new FakeBackend(
            begin: _ => "password",
            submit: _ => throw new Exception("PASSWORD_HASH_INVALID")));

        await service.StartAsync("12345", "hash-value", "+351900000000");
        var status = await service.SubmitPasswordAsync("wrong-password");

        Assert.Equal(TelegramAuthState.Error, status.State);
        Assert.Contains("PASSWORD_HASH_INVALID", status.Detail);
        Assert.DoesNotContain("wrong-password", status.Detail);
        Assert.False(service.IsAuthenticated);
    }

    [Fact]
    public async Task Flood_wait_returns_parsed_seconds()
    {
        var service = CreateService(_ => new FakeBackend(
            begin: _ => "verification_code",
            submit: _ => throw new Exception("FLOOD_WAIT_42")));

        await service.StartAsync("12345", "hash-value", "+351900000000");
        var status = await service.SubmitCodeAsync("123456");

        Assert.Equal(TelegramAuthState.Error, status.State);
        Assert.Equal("flood-wait:42", status.Detail);
    }

    [Fact]
    public async Task Already_authenticated_when_begin_returns_null()
    {
        var service = CreateService(_ => new FakeBackend(begin: _ => null));

        var status = await service.StartAsync("12345", "hash-value", "+351900000000");

        Assert.Equal(TelegramAuthState.Authenticated, status.State);
        Assert.True(service.IsAuthenticated);
    }

    [Fact]
    public async Task Submit_without_active_login_returns_error()
    {
        var service = CreateService(_ => new FakeBackend());

        var status = await service.SubmitCodeAsync("123456");

        Assert.Equal(TelegramAuthState.Error, status.State);
        Assert.Equal("no-active-login", status.Detail);
    }

    [Fact]
    public async Task Reset_returns_not_configured()
    {
        var service = CreateService(_ => new FakeBackend(begin: _ => null));
        await service.StartAsync("12345", "hash-value", "+351900000000");
        Assert.True(service.IsAuthenticated);

        service.Reset();

        Assert.False(service.IsAuthenticated);
        var status = await service.GetStatusAsync();
        Assert.Equal(TelegramAuthState.NotConfigured, status.State);
    }

    [Fact]
    public async Task Config_display_never_contains_api_hash_value()
    {
        var service = CreateService(_ => new FakeBackend());
        await service.StartAsync("12345", "SECRET-HASH-VALUE", "+351900000000");

        var display = service.GetConfigForDisplay();
        var json = JsonSerializer.Serialize(display);

        Assert.DoesNotContain("SECRET-HASH-VALUE", json);
        Assert.True(display.HasApiHash);
        Assert.False(display.WouldPromptConsole);
        Assert.Equal("12345", display.ApiId);
        Assert.Equal("+351900000000", display.PhoneNumber);
        Assert.Equal(TelegramAuthService.DefaultSessionPath, display.SessionPath);
    }

    [Fact]
    public async Task Start_persists_config_and_preserves_unrelated_keys()
    {
        File.WriteAllLines(_configPath, new[]
        {
            "custom_unknown=keep-me",
            "dispatcharr_enabled=true",
        });

        var service = CreateService(_ => new FakeBackend());
        await service.StartAsync("12345", "hash-value", "+351900000000");

        var raw = ReadRaw();
        Assert.Equal("12345", raw["api_id"]);
        Assert.Equal("hash-value", raw["api_hash"]);
        Assert.Equal("+351900000000", raw["phone_number"]);
        Assert.Equal(TelegramAuthService.DefaultSessionPath, raw["session_pathname"]);
        Assert.Equal("keep-me", raw["custom_unknown"]);
        Assert.Equal("true", raw["dispatcharr_enabled"]);
    }

    [Fact]
    public async Task GetStatus_resumes_persisted_session_and_authenticates()
    {
        File.WriteAllLines(_configPath, new[]
        {
            "api_id=12345",
            "api_hash=hash-value",
            "phone_number=+351900000000",
            "session_pathname=persisted.session",
        });

        var beginCalls = 0;
        var service = CreateService(
            _ =>
            {
                Interlocked.Increment(ref beginCalls);
                return new FakeBackend(begin: _ => null);
            },
            sessionFileExists: _ => true);

        var status = await service.GetStatusAsync();

        Assert.Equal(TelegramAuthState.Authenticated, status.State);
        Assert.True(status.Configured);
        Assert.Equal("fake-user", status.UserName);
        Assert.Equal(1, beginCalls);
    }

    [Fact]
    public async Task GetStatus_caches_resume_probe()
    {
        File.WriteAllLines(_configPath, new[]
        {
            "api_id=12345",
            "api_hash=hash-value",
            "phone_number=+351900000000",
        });

        var beginCalls = 0;
        var service = CreateService(
            _ =>
            {
                Interlocked.Increment(ref beginCalls);
                return new FakeBackend(begin: _ => "verification_code");
            },
            sessionFileExists: _ => true);

        var first = await service.GetStatusAsync();
        var second = await service.GetStatusAsync();

        Assert.Equal(TelegramAuthState.NotConfigured, first.State);
        Assert.Equal(TelegramAuthState.NotConfigured, second.State);
        Assert.Equal(1, beginCalls);
    }

    [Fact]
    public async Task GetStatus_unconfigured_returns_not_configured()
    {
        var service = CreateService(_ => new FakeBackend(), sessionFileExists: _ => true);

        var status = await service.GetStatusAsync();

        Assert.Equal(TelegramAuthState.NotConfigured, status.State);
        Assert.False(status.Configured);
    }

    [Fact]
    public async Task Concurrent_start_calls_are_serialized_and_consistent()
    {
        var service = CreateService(_ => new FakeBackend(begin: _ =>
        {
            Thread.Sleep(50);
            return "verification_code";
        }));

        var first = service.StartAsync("12345", "hash-value", "+351900000000");
        var second = service.StartAsync("12345", "hash-value", "+351900000000");

        var results = await Task.WhenAll(first, second);

        Assert.All(results, r => Assert.Equal(TelegramAuthState.WaitingCode, r.State));
        Assert.False(service.IsAuthenticated);
    }

    private sealed class FakeBackend : ITelegramAuthBackend
    {
        private readonly Func<TelegramBackendOptions, string?> _begin;
        private readonly Func<string, string?> _submit;

        public FakeBackend(
            Func<TelegramBackendOptions, string?>? begin = null,
            Func<string, string?>? submit = null)
        {
            _begin = begin ?? (_ => "verification_code");
            _submit = submit ?? (_ => null);
        }

        public bool IsAuthenticated { get; private set; }

        public string? UserName { get; set; } = "fake-user";

        public Task<string?> BeginLoginAsync(TelegramBackendOptions options)
        {
            var next = _begin(options);
            IsAuthenticated = next is null;
            return Task.FromResult(next);
        }

        public Task<string?> SubmitAsync(string value)
            => Task.FromResult(_submit(value));

        public void Dispose()
        {
        }
    }
}
