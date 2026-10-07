using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Automation;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using m3uCrawler.Services.Telegram;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Wave W5 — Arranque seguro sem Telegram e reutilização do cliente
/// autenticado pela aplicação.
///
/// <para>
/// Cobre: (1) uma instalação nova sem <c>wtelegram.config</c>/sessão não
/// termina o arranque e o dashboard continua a responder; (2) a capacidade
/// Telegram fica bloqueada pelo gate até à autenticação; (3) depois de
/// autenticado, a pipeline usa o MESMO cliente vivo do
/// <see cref="TelegramAuthService"/> (não um cliente de consola novo);
/// (4) o caminho legacy de CLI sem serviço de aplicação continua a
/// resolver um cliente; (5) re-auth/reset liberta o cliente anterior e o
/// run seguinte usa o novo; (6) o ficheiro de configuração legacy é lido
/// sem snapshot estático.
/// </para>
/// </summary>
[Collection("DashboardStaticState")]
public sealed class WaveW5TelegramClientReuseTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _outputDir;

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;
    private PlaylistComposerService _composer = null!;
    private ImportHistoryService _history = null!;

    public WaveW5TelegramClientReuseTests()
    {
        _root = TestTempDb.SuitePath($"wave-w5-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _outputDir = Path.Combine(_root, "output");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_outputDir);
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();

        _resolver = new CatalogResolver(_factory, _dbPath);
        _composer = new PlaylistComposerService(_factory);
        _history = new ImportHistoryService(_outputDir);
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        TestTempDb.CleanupDirectory(_root);
        return Task.CompletedTask;
    }

    // (1) Fresh install: sem config, o arranque não lança e o dashboard
    // continua a responder.
    [Fact]
    public async Task Fresh_install_without_telegram_config_does_not_throw_and_dashboard_responds()
    {
        var configPath = Path.Combine(_root, "absent-wtelegram.config");
        var store = new WtelegramConfigStore(configPath);
        var auth = new TelegramAuthService(
            store,
            _ => new FakeBackend(begin: _ => "verification_code"),
            sessionFileExists: _ => false);
        var scraper = new TelegramScraperService(auth);

        var ready = await Program.TryAuthenticateTelegramForStartupAsync(
            scraper, auth, TimeSpan.FromSeconds(5));

        Assert.False(ready);
        Assert.False(auth.IsAuthenticated);

        // O dashboard continua a poder servir pedidos (o processo não
        // terminaria neste ponto).
        await using var harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history,
            lifecycle: null, auth: null, bootstrap: null, webToken: null, standalone: true);

        var version = await harness.Client.GetAsync("/api/version");
        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
    }

    // (2)+(3) Capacidade bloqueada até autenticar; depois a pipeline usa o
    // mesmo cliente vivo (não um cliente de consola novo).
    [Fact]
    public async Task Telegram_capability_unblocks_after_auth_and_pipeline_uses_same_live_client()
    {
        var liveClient = CreateTestClient();
        var backend = new FakeBackend(
            begin: _ => "verification_code",
            submit: _ => null,
            client: liveClient);
        var store = new WtelegramConfigStore(Path.Combine(_root, "app-auth.config"));
        var auth = new TelegramAuthService(store, _ => backend, sessionFileExists: _ => false);

        var gate = new ActionCapabilityGate(
            _ => Task.FromResult(ReadinessSnapshot(auth.IsAuthenticated)));

        // Antes de autenticar: bloqueado.
        Assert.False(auth.IsAuthenticated);
        Assert.False(await gate.IsReadyForAsync(ScheduledActionCapabilities.Telegram));

        var started = await auth.StartAsync("12345", "hash-value", "+351900000000");
        Assert.Equal(TelegramAuthState.WaitingCode, started.State);
        Assert.False(await gate.IsReadyForAsync(ScheduledActionCapabilities.Telegram));

        var done = await auth.SubmitCodeAsync("54321");
        Assert.Equal(TelegramAuthState.Authenticated, done.State);

        // Autenticado: capacidade satisfeita.
        Assert.True(await gate.IsReadyForAsync(ScheduledActionCapabilities.Telegram));

        // A pipeline reutiliza exactamente o cliente vivo do serviço de
        // aplicação — não constrói um segundo cliente de consola.
        var scraper = new TelegramScraperService(auth);
        Assert.Same(liveClient, auth.LiveClient);
        Assert.Same(auth.LiveClient, scraper.RequireClient());
        Assert.Same(liveClient, scraper.RequireClient());
    }

    // (4) Caminho legacy: sem serviço de aplicação, um cliente injectado
    // continua a ser usado tal como antes.
    [Fact]
    public void Legacy_path_without_app_auth_uses_injected_client()
    {
        var injected = CreateTestClient();
        var legacy = new TelegramScraperService(injected);
        Assert.Same(injected, legacy.RequireClient());
    }

    // (4b) O construtor padrão (CLI interactiva) é preguiçoso: não cria
    // cliente nem lê consola no arranque.
    [Fact]
    public void Default_constructor_is_lazy()
    {
        var scraper = new TelegramScraperService();
        Assert.NotNull(scraper);
    }

    // (5) Reset/re-auth liberta o cliente anterior; o run seguinte usa o novo.
    [Fact]
    public async Task Reauth_reset_disposes_previous_client_and_next_run_uses_new_one()
    {
        var client1 = CreateTestClient();
        var client2 = CreateTestClient();

        var backends = new Queue<FakeBackend>(new[]
        {
            new FakeBackend(begin: _ => null, client: client1),
            new FakeBackend(begin: _ => null, client: client2),
        });
        FakeBackend? lastCreated = null;

        var store = new WtelegramConfigStore(Path.Combine(_root, "reauth.config"));
        var auth = new TelegramAuthService(
            store,
            _ =>
            {
                lastCreated = backends.Dequeue();
                return lastCreated;
            },
            sessionFileExists: _ => false);

        await auth.StartAsync("12345", "hash-value", "+351900000000");
        Assert.True(auth.IsAuthenticated);
        Assert.Same(client1, auth.LiveClient);
        var firstBackend = lastCreated!;

        var scraper = new TelegramScraperService(auth);
        Assert.Same(client1, scraper.RequireClient());

        auth.Reset();

        Assert.True(firstBackend.Disposed);
        Assert.Null(auth.LiveClient);
        Assert.False(auth.IsAuthenticated);

        await auth.StartAsync("12345", "hash-value", "+351900000000");
        Assert.True(auth.IsAuthenticated);
        Assert.Same(client2, auth.LiveClient);
        Assert.Same(client2, scraper.RequireClient());
    }

    // (6) Sem snapshot estático: o ficheiro legacy é relido em cada pedido.
    [Fact]
    public void Legacy_config_file_is_read_fresh_without_static_snapshot()
    {
        var dir = Path.Combine(_root, "cfg");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "wtelegram.config");

        File.WriteAllLines(file, new[] { "api_id=111", "phone_number=+351900000000" });
        var first = TelegramScraperService.LoadConfigFileFrom(dir);
        Assert.Equal("111", first["api_id"]);
        Assert.Equal("+351900000000", first["phone_number"]);

        File.WriteAllLines(file, new[] { "api_id=222" });
        var second = TelegramScraperService.LoadConfigFileFrom(dir);
        Assert.Equal("222", second["api_id"]);
        Assert.False(second.ContainsKey("phone_number"));
    }

    // O construtor real da WTelegram valida api_id/api_hash no construtor,
    // pelo que os testes fornecem credenciais fictícias (sem rede: nunca
    // há login).
    private WTelegram.Client CreateTestClient()
        => new(what => what switch
        {
            "api_id" => "12345",
            "api_hash" => "0123456789abcdef0123456789abcdef",
            "phone_number" => "+351900000000",
            "session_pathname" => Path.Combine(_root, $"session-{Guid.NewGuid():N}.dat"),
            _ => null!,
        });

    private static OperationalReadinessSnapshot ReadinessSnapshot(bool telegramAuthenticated)
    {
        return new OperationalReadinessSnapshot(
            BootstrapReady: true,
            HasAdmin: true,
            TelegramAuthenticated: telegramAuthenticated,
            DispatcharrEnabled: false,
            DispatcharrValid: true,
            CatalogOk: true,
            CountryDataOk: true,
            OutputOk: true,
            SourcesCount: 1,
            SetupComplete: telegramAuthenticated,
            OperationalReady: telegramAuthenticated,
            AdoptedFromLegacy: false,
            Items: Array.Empty<OperationalReadinessItem>(),
            MissingRequired: telegramAuthenticated
                ? Array.Empty<string>()
                : new[] { OperationalReadinessService.KeyTelegram });
    }

    private sealed class FakeBackend : ITelegramAuthBackend
    {
        private readonly Func<TelegramBackendOptions, string?> _begin;
        private readonly Func<string, string?> _submit;

        public FakeBackend(
            Func<TelegramBackendOptions, string?>? begin = null,
            Func<string, string?>? submit = null,
            WTelegram.Client? client = null)
        {
            _begin = begin ?? (_ => "verification_code");
            _submit = submit ?? (_ => null);
            Client = client;
        }

        public bool IsAuthenticated { get; private set; }

        public string? UserName { get; set; } = "fake-user";

        public WTelegram.Client? Client { get; }

        public bool Disposed { get; private set; }

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
            Disposed = true;
            Client?.Dispose();
        }
    }
}
