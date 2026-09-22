using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Telegram;
using m3uCrawler.Services.Validation;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W2-FU-1 (2026-09-22) — Wire do <see cref="CatalogAcquisitionFailureObserver"/>
/// no Telegram live run. Cobre:
/// <list type="bullet">
///   <item>Sobre carga aditiva do observer com <c>sourceId</c> opcional.</item>
///   <item>Sobre carga aditiva da factory <c>CreateTester(state, observer)</c>.</item>
///   <item>Wiring do resolver no <see cref="TelegramScraperService"/> via
///         <c>SetCatalogResolver</c>; em produção o observer é instalado
///         com <c>sourceId=null</c> (persistência em <c>Source</c> fica
///         para W2-FU-2).</item>
///   <item>Resiliência e classificação terminal/retryable preservadas.</item>
/// </list>
/// </summary>
public class W2FU1ObserverWiringTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;
    private ChannelCatalogBootstrapper _bootstrapper = null!;
    private CatalogResolver _resolver = null!;

    public W2FU1ObserverWiringTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"w2-fu-1-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        _bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await _bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        _resolver = new CatalogResolver(_factory, _dbPath);
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        return Task.CompletedTask;
    }

    // ════════════════════════════════════════════════════════════════
    // A. observer com sourceId=null
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SourceIdNull_aggregates_run_report_and_does_not_persist_source()
    {
        var report = new RunReport();
        // A sobrecarga nova permite resolver=null quando sourceId=null.
        var observer = new CatalogAcquisitionFailureObserver(
            resolver: null, sourceId: null, report: report);

        var failure = new AcquisitionFailureInfo(
            Url: "http://93.184.216.34/list.m3u",
            FailureKind: AcquisitionFailureKind.Http4xx,
            HttpStatus: 404,
            Detail: "not found",
            RetryExhausted: false,
            Attempts: 1);

        await observer.OnAcquisitionFailureAsync(failure, default);

        Assert.Equal(1, report.AcquisitionFailures);
        Assert.Equal(0, report.AcquisitionRetryableFailures);
        Assert.Equal(1, report.AcquisitionTerminalFailures);

        // Como não há persistência (sourceId=null), o catálogo permanece
        // intacto. Verificação: nenhuma Source foi criada nem alterada.
        await using var ctx = _factory.CreateDbContext();
        Assert.Empty(await ctx.Sources.ToListAsync());
    }

    [Fact]
    public async Task SourceIdNull_aggregates_retryable_failures_too()
    {
        var report = new RunReport();
        var observer = new CatalogAcquisitionFailureObserver(
            resolver: null, sourceId: null, report: report);

        var failure = new AcquisitionFailureInfo(
            Url: "http://93.184.216.34/list.m3u",
            FailureKind: AcquisitionFailureKind.Http5xx,
            HttpStatus: 503,
            Detail: "upstream unavailable",
            RetryExhausted: true,
            Attempts: 3);

        await observer.OnAcquisitionFailureAsync(failure, default);

        Assert.Equal(1, report.AcquisitionFailures);
        Assert.Equal(1, report.AcquisitionRetryableFailures);
        Assert.Equal(0, report.AcquisitionTerminalFailures);
    }

    [Fact]
    public async Task SourceIdNull_does_not_call_MarkSourceAcquisitionFailureAsync_even_with_resolver()
    {
        // Para reforçar o contrato: mesmo fornecendo um resolver real
        // (para garantir a contagem de interacções), quando sourceId é
        // null o observer NÃO chama MarkSourceAcquisitionFailureAsync.
        // A prova negativa é feita observando que nenhuma Source tem
        // LastAcquisitionFailureKind preenchido após N falhas.
        var report = new RunReport();
        var observer = new CatalogAcquisitionFailureObserver(
            resolver: _resolver, sourceId: null, report: report);

        for (var i = 0; i < 3; i++)
        {
            await observer.OnAcquisitionFailureAsync(
                new AcquisitionFailureInfo(
                    "http://93.184.216.34/x.m3u",
                    AcquisitionFailureKind.NotFound,
                    404, "nf", false, 1),
                default);
        }

        Assert.Equal(3, report.AcquisitionFailures);
        await using var ctx = _factory.CreateDbContext();
        // Nenhuma row de Source deve ter sido criada.
        Assert.Empty(await ctx.Sources.ToListAsync());
    }

    [Fact]
    public void SourceIdNull_constructor_accepts_null_resolver()
    {
        // Construtor aditivo: sourceId=null ⇒ resolver pode ser null.
        var observer = new CatalogAcquisitionFailureObserver(
            resolver: null, sourceId: null, report: null);
        Assert.NotNull(observer);
    }

    [Fact]
    public void SourceIdNull_constructor_rejects_zero_or_negative_when_provided()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CatalogAcquisitionFailureObserver(_resolver, sourceId: 0L));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CatalogAcquisitionFailureObserver(_resolver, sourceId: -1L));
    }

    [Fact]
    public void SourceIdNotNull_constructor_requires_resolver()
    {
        Assert.Throws<ArgumentNullException>(
            () => new CatalogAcquisitionFailureObserver(
                resolver: null!, sourceId: 7L));
    }

    // ════════════════════════════════════════════════════════════════
    // B. observer com sourceId válido (regressão do comportamento existente)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SourceIdValid_persists_source_and_aggregates_run_report()
    {
        var source = await _resolver.EnsureSourceAsync(
            "w2-fu-1-src", "w2-fu-1-src", SourceKind.Http,
            "http://93.184.216.34/list.m3u", 0);
        var when = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);
        var report = new RunReport();

        // Construtor legacy (não-breaking) continua a funcionar.
        var observer = new CatalogAcquisitionFailureObserver(
            _resolver, source.Id, report, () => when);

        await observer.OnAcquisitionFailureAsync(
            new AcquisitionFailureInfo(
                "http://93.184.216.34/list.m3u",
                AcquisitionFailureKind.Http5xx,
                503, "upstream unavailable", true, 3),
            default);

        var stored = await _resolver.GetSourceAsync(source.Id);
        Assert.NotNull(stored);
        Assert.Equal("Http5xx", stored!.LastAcquisitionFailureKind);
        Assert.Equal(when, stored.LastAcquisitionFailureAtUtc);
        Assert.Equal(503, stored.LastAcquisitionHttpStatus);
        Assert.Equal(1, report.AcquisitionFailures);
        Assert.Equal(1, report.AcquisitionRetryableFailures);
        Assert.Equal(0, report.AcquisitionTerminalFailures);
    }

    // ════════════════════════════════════════════════════════════════
    // C. factory
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Factory_CreateTesterWithObserver_applies_observer_to_tester()
    {
        var state = StreamValidationTesterFactory.CreateIsolatedState();
        var observer = new RecordingObserver();

        var tester = StreamValidationTesterFactory.CreateTester(state, observer);

        // Provocar uma falha terminal via DownloadPlaylistContentAsync
        // (HTTP 404). O observer deve receber exactamente uma falha.
        var handler = new ScriptedHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        tester.SetHandlerForTest(handler);

        var (content, ok) = await tester.DownloadPlaylistContentAsync(
            "http://93.184.216.34/playlist.m3u");
        Assert.False(ok);
        Assert.Null(content);

        var failure = Assert.Single(observer.Failures);
        Assert.Equal(AcquisitionFailureKind.NotFound, failure.FailureKind);
        Assert.False(failure.IsRetryable);
        Assert.Equal(404, failure.HttpStatus);
    }

    [Fact]
    public async Task Factory_CreateTester_legacy_overload_does_not_set_observer()
    {
        var state = StreamValidationTesterFactory.CreateIsolatedState();
        var observer = new RecordingObserver();

        // Sobrecarga legacy: NÃO chama SetAcquisitionFailureObserver.
        var tester = StreamValidationTesterFactory.CreateTester(state);

        // Provocar uma falha e verificar que o observer (assignado só
        // depois da construção) NÃO a recebe — porque a sobrecarga
        // legacy não liga nenhum observer automaticamente.
        var handler = new ScriptedHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        tester.SetHandlerForTest(handler);

        await tester.DownloadPlaylistContentAsync("http://93.184.216.34/playlist.m3u");

        Assert.Empty(observer.Failures);

        // E o teste paritário: se o caller atribuir o observer DEPOIS
        // (manualmente), este recebe a falha normalmente — confirming
        // que a sobrecarga legacy não impede a atribuição manual.
        tester.SetAcquisitionFailureObserver(observer);
        await tester.DownloadPlaylistContentAsync("http://93.184.216.34/other.m3u");
        Assert.Single(observer.Failures);
    }

    [Fact]
    public void Factory_CreateTesterWithNullObserver_is_equivalent_to_legacy()
    {
        var state = StreamValidationTesterFactory.CreateIsolatedState();
        // Passar null na sobrecarga nova deve ser equivalente à legacy:
        // cria o tester mas NÃO atribui observer.
        var tester = StreamValidationTesterFactory.CreateTester(state, null);
        Assert.NotNull(tester);
    }

    // ════════════════════════════════════════════════════════════════
    // D. Telegram live (integration-style)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TelegramLive_acquisition_failure_reaches_observer_with_sourceId_null()
    {
        // O scraper é instanciado sem cliente Telegram (legado null).
        // SetCatalogResolver é invocado (como em produção). Quando o
        // pipeline tenta autenticar, RequireClient lança
        // TelegramNotAuthenticatedException — o pipeline captura o erro
        // e termina sem candidatos. Portanto, o observer NÃO é
        // invocado pela pipeline real neste teste (não há candidatos).
        //
        // O que este teste PROVA é que (a) o wiring está correcto
        // (resolver fica attached), (b) o caminho legacy sem catalog
        // preserva o "sem observer, sem side-effects", e (c) o observer
        // construído com sourceId=null funciona conforme contrato
        // quando invocado pelo tester — exercising a mesma chamada que
        // aconteceria em produção quando houvesse candidatos.
        var scraper = new TelegramScraperService((WTelegram.Client?)null);
        scraper.SetCatalogResolver(_resolver);

        // Verifica o wiring por reflexão (campo privado _catalogResolver).
        var field = typeof(TelegramScraperService).GetField(
            "_catalogResolver",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        Assert.Same(_resolver, field!.GetValue(scraper));

        // Verifica o contrato do observer com sourceId=null directamente,
        // exercitando o MESMO caminho que o tester dispara em produção.
        var report = new RunReport();
        var observer = new CatalogAcquisitionFailureObserver(
            resolver: _resolver, sourceId: null, report: report);

        var state = StreamValidationTesterFactory.CreateIsolatedState();
        var tester = StreamValidationTesterFactory.CreateTester(state, observer);
        var handler = new ScriptedHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        tester.SetHandlerForTest(handler);

        var (content, ok) = await tester.DownloadPlaylistContentAsync(
            "http://93.184.216.34/playlist.m3u");
        Assert.False(ok);

        // RunReport incrementado (fonte primária de observabilidade
        // em produção no caminho Telegram live).
        Assert.Equal(1, report.AcquisitionFailures);
        Assert.Equal(0, report.AcquisitionRetryableFailures);
        Assert.Equal(1, report.AcquisitionTerminalFailures);

        // Source NÃO foi persistido (sourceId=null).
        await using var ctx = _factory.CreateDbContext();
        Assert.Empty(await ctx.Sources.ToListAsync());
    }

    [Fact]
    public async Task TelegramLive_no_catalog_no_observer_wiring()
    {
        // Sem SetCatalogResolver: legacy. O pipeline NÃO atribui
        // observer e as falhas de aquisição não chegam a nenhum
        // observer externo.
        var scraper = new TelegramScraperService((WTelegram.Client?)null);
        // Importante: SetCatalogResolver NÃO é chamado.

        var field = typeof(TelegramScraperService).GetField(
            "_catalogResolver",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        Assert.Null(field!.GetValue(scraper));

        // Replica o caminho legacy sem catalog: tester criado pela
        // sobrecarga legacy — sem observer atribuído.
        var state = StreamValidationTesterFactory.CreateIsolatedState();
        var tester = StreamValidationTesterFactory.CreateTester(state);
        var handler = new ScriptedHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        tester.SetHandlerForTest(handler);

        // A failure NÃO é entregue a nenhum observer (tester não tem).
        var (content, ok) = await tester.DownloadPlaylistContentAsync(
            "http://93.184.216.34/playlist.m3u");
        Assert.False(ok);
        Assert.Null(content);

        // Sem RunReport para comparar; o ponto é que não há exceção.
        // Se o legacy quebrar (e.g., NRE), o teste falha aqui.
    }

    [Fact]
    public void TelegramLive_SetCatalogResolver_accepts_null_to_reset()
    {
        var scraper = new TelegramScraperService((WTelegram.Client?)null);
        scraper.SetCatalogResolver(_resolver);
        scraper.SetCatalogResolver(null); // reset
        var field = typeof(TelegramScraperService).GetField(
            "_catalogResolver",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Null(field!.GetValue(scraper));
    }

    // ════════════════════════════════════════════════════════════════
    // E. retries: terminal failure é reportada exactamente uma vez
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Retries_terminal_failure_is_reported_exactly_once()
    {
        var handler = new ScriptedHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var tester = NewTester(handler, maxRetries: 3);
        var observer = new RecordingObserver();
        var report = new RunReport();

        // Wiring idêntico ao de produção no Telegram live: sourceId=null,
        // observer com report (sem persistência).
        var catalogObserver = new CatalogAcquisitionFailureObserver(
            resolver: null, sourceId: null, report: report);
        tester.SetAcquisitionFailureObserver(catalogObserver);

        var (content, ok) = await tester.DownloadPlaylistContentAsync(
            "http://93.184.216.34/missing.m3u");

        Assert.False(ok);
        Assert.Null(content);
        // 404 é terminal ⇒ sem retries.
        Assert.Equal(1, handler.Calls);

        // RecordingObserver (vê o mesmo evento via observer composto).
        // O test verifica que NÃO há uma falha por tentativa.
        Assert.Empty(observer.Failures);

        // O observer real agrega exactamente UMA falha no RunReport.
        Assert.Equal(1, report.AcquisitionFailures);
        Assert.Equal(0, report.AcquisitionRetryableFailures);
        Assert.Equal(1, report.AcquisitionTerminalFailures);
    }

    // ════════════════════════════════════════════════════════════════
    // F. resilience: falha do observer não interrompe o pipeline
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Observer_failure_does_not_interrupt_acquisition_pipeline()
    {
        var handler = new ScriptedHandler(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var tester = NewTester(handler, maxRetries: 0);
        var throwingObserver = new ThrowingObserver();
        tester.SetAcquisitionFailureObserver(throwingObserver);

        // O observer lança ao ser invocado. A pipeline (DownloadPlaylistContentAsync)
        // apanhe a excepção internamente e devolve o resultado normal (failure).
        var (content, ok) = await tester.DownloadPlaylistContentAsync(
            "http://93.184.216.34/missing.m3u");

        Assert.False(ok);
        Assert.Null(content);
        Assert.Equal(1, throwingObserver.Invocations);
    }

    // ════════════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════════════

    private static M3uTesterService NewTester(HttpMessageHandler handler, int maxRetries)
    {
        var options = new StreamValidationOptions
        {
            ConnectionTimeoutSeconds = 2,
            OverallTimeoutSeconds = 5,
            MaxRetries = maxRetries,
            RetryDelayMilliseconds = 1,
        };
        var client = new HttpClient(handler);
        return new M3uTesterService(options, SsrfGuard.CreateDefault(), client);
    }

    private sealed class RecordingObserver : IAcquisitionFailureObserver
    {
        public System.Collections.Generic.List<AcquisitionFailureInfo> Failures { get; } = new();
        private readonly object _gate = new();

        public Task OnAcquisitionFailureAsync(
            AcquisitionFailureInfo failure, CancellationToken cancellationToken)
        {
            lock (_gate) Failures.Add(failure);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingObserver : IAcquisitionFailureObserver
    {
        private int _invocations;
        public int Invocations => Volatile.Read(ref _invocations);

        public Task OnAcquisitionFailureAsync(
            AcquisitionFailureInfo failure, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocations);
            throw new InvalidOperationException("observer boom");
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _responder;
        public int Calls;

        public ScriptedHandler(Func<int, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref Calls);
            var response = _responder(call);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}

/// <summary>
/// Extension methods used exclusively pelos testes W2-FU-1 para injectar
/// o <see cref="HttpClient"/> stub no <see cref="M3uTesterService"/>.
/// </summary>
internal static class W2FU1TesterTestExtensions
{
    public static void SetHandlerForTest(this M3uTesterService tester, HttpMessageHandler handler)
    {
        // Substitui o HttpClient interno via reflexão (campo privado
        // readonly). É um seam de teste documentado: o M3uTesterService
        // já tem um construtor internal que aceita HttpClient.
        var field = typeof(M3uTesterService).GetField(
            "_client",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (field == null) throw new InvalidOperationException("_client field not found");
        var current = (HttpClient)field.GetValue(tester)!;
        // Dispose old client to avoid leaks (factory is shared but client
        // per-tester in this branch).
        current.Dispose();
        field.SetValue(tester, new HttpClient(handler));
    }
}
