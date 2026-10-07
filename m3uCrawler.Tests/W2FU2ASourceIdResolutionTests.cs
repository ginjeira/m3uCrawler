using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Validation;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W2-FU-2A (2026-09-23) — Read-only SourceId resolution for the
/// Telegram acquisition-failure observer.
///
/// Cobre:
/// <list type="bullet">
///   <item>Source existente + acquisition failure: o observer com o
///         <c>sourceId</c> resolvido persiste
///         <c>Source.LastAcquisitionFailure*</c> E incrementa
///         <c>RunReport.AcquisitionFailures</c>.</item>
///   <item>Source inexistente + acquisition failure: o lookup devolve
///         <c>null</c>; o observer recebe <c>sourceId=null</c> (caminho
///         W2-FU-1) e nenhuma Source é criada.</item>
///   <item>Primeiro sucesso: regression test do <c>EnsureSourceAsync</c>
///         existente.</item>
///   <item>Restart safety: chamadas sucessivas ao lookup devolvem o
///         mesmo Id (determinístico, sem estado estático).</item>
///   <item>Observer null-safe: com <c>sourceId=null</c>, comportamento
///         idêntico a W2-FU-1.</item>
/// </list>
///
/// NÃO cria identidade peer/chat, NÃO altera cardinalidade de
/// <c>Source</c>, NÃO introduz <c>TelegramPeerEntity</c>, NÃO
/// introduz <c>Source.Key</c> novo.
/// </summary>
public class W2FU2ASourceIdResolutionTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;
    private ChannelCatalogBootstrapper _bootstrapper = null!;
    private CatalogResolver _resolver = null!;

    public W2FU2ASourceIdResolutionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"w2-fu-2a-{Guid.NewGuid():N}.db");
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
    // 1. Source existe + acquisition failure → persiste + RunReport++
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExistingSource_acquisitionFailure_persists_and_increments_run_report()
    {
        // Arrange: Source "telegram-foo" já existe.
        var seeded = await _resolver.EnsureSourceAsync(
            key: "telegram-foo",
            name: "telegram-foo",
            kind: SourceKind.Http,
            origin: "http://93.184.216.34/list.m3u",
            priority: 0);

        // Lookup read-only devolve o Id da Source já existente.
        var resolvedId = await _resolver.GetSourceIdByKeyAsync("telegram-foo");
        Assert.NotNull(resolvedId);
        Assert.Equal(seeded.Id, resolvedId!.Value);

        // O observer é construído com o Id resolvido (como o TelegramScraperService
        // faz em produção neste W2-FU-2A).
        var report = new RunReport();
        var observer = new CatalogAcquisitionFailureObserver(
            _resolver, sourceId: resolvedId.Value, report: report);

        // Act: falha terminal HTTP 404.
        await observer.OnAcquisitionFailureAsync(
            new AcquisitionFailureInfo(
                Url: "http://93.184.216.34/list.m3u",
                FailureKind: AcquisitionFailureKind.Http4xx,
                HttpStatus: 404,
                Detail: "not found",
                RetryExhausted: false,
                Attempts: 1),
            default);

        // Assert: Source persistida com LastAcquisitionFailure* preenchidos.
        var stored = await _resolver.GetSourceAsync(seeded.Id);
        Assert.NotNull(stored);
        Assert.Equal("Http4xx", stored!.LastAcquisitionFailureKind);
        Assert.Equal(404, stored.LastAcquisitionHttpStatus);
        Assert.NotNull(stored.LastAcquisitionFailureAtUtc);

        // Assert: RunReport incrementado (W2-FU-1 contrato preservado).
        Assert.Equal(1, report.AcquisitionFailures);
        Assert.Equal(1, report.AcquisitionTerminalFailures);
        Assert.Equal(0, report.AcquisitionRetryableFailures);
    }

    // ════════════════════════════════════════════════════════════════
    // 2. Source inexistente + acquisition failure → NO Source criada
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task NonExistingSource_lookupReturnsNull_observerFallbacksToRunReportOnly()
    {
        // Arrange: nenhuma Source pré-existente com esta chave.
        var resolvedId = await _resolver.GetSourceIdByKeyAsync("telegram-missing");
        Assert.Null(resolvedId);

        // O TelegramScraperService (W2-FU-2A) propaga sourceId=null ao
        // observer — caminho W2-FU-1: RunReport-only, sem persistência.
        var report = new RunReport();
        var observer = new CatalogAcquisitionFailureObserver(
            _resolver, sourceId: null, report: report);

        // Act: várias falhas para garantir que nenhuma tentativa cria Source.
        for (var i = 0; i < 3; i++)
        {
            await observer.OnAcquisitionFailureAsync(
                new AcquisitionFailureInfo(
                    Url: "http://93.184.216.34/x.m3u",
                    FailureKind: AcquisitionFailureKind.Http5xx,
                    HttpStatus: 503,
                    Detail: "upstream unavailable",
                    RetryExhausted: true,
                    Attempts: 3),
                default);
        }

        // Assert: RunReport incrementado.
        Assert.Equal(3, report.AcquisitionFailures);
        Assert.Equal(3, report.AcquisitionRetryableFailures);
        Assert.Equal(0, report.AcquisitionTerminalFailures);

        // Assert: NENHUMA Source criada (mesmo após 3 falhas).
        await using var ctx = _factory.CreateDbContext();
        Assert.Empty(await ctx.Sources.ToListAsync());
    }

    // ════════════════════════════════════════════════════════════════
    // 3. Primeiro sucesso → EnsureSourceAsync cria Source com Key certa
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FirstSuccess_EnsureSourceAsync_creates_source_with_expected_key()
    {
        // Regression test do caminho de criação (mimics IngestAsync flow):
        // EnsureSourceAsync é a única forma de criar uma Source, e usa
        // exactamente o Key que o caller fornece.
        var created = await _resolver.EnsureSourceAsync(
            key: "telegram-foo",
            name: "telegram-foo",
            kind: SourceKind.Http,
            origin: "http://93.184.216.34/list.m3u",
            priority: 0);

        Assert.Equal("telegram-foo", created.Key);
        Assert.Equal(SourceKind.Http, created.Kind);
        Assert.True(created.IsEnabled);

        // E o lookup read-only consegue encontrá-la imediatamente.
        var resolvedId = await _resolver.GetSourceIdByKeyAsync("telegram-foo");
        Assert.Equal(created.Id, resolvedId);

        // Re-EnsureSourceAsync é idempotente (mesmo Id, sem duplicação).
        var again = await _resolver.EnsureSourceAsync(
            key: "telegram-foo",
            name: "telegram-foo-renamed",
            kind: SourceKind.Http,
            origin: "http://93.184.216.34/list.m3u",
            priority: 0,
            updatePriority: false);
        Assert.Equal(created.Id, again.Id);

        await using var ctx = _factory.CreateDbContext();
        Assert.Single(await ctx.Sources.ToListAsync());
    }

    // ════════════════════════════════════════════════════════════════
    // 4. Execução posterior → lookup devolve o Source.Id
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SubsequentRun_lookupReturnsExistingSourceId()
    {
        // Arrange: primeira execução criou a Source.
        var first = await _resolver.EnsureSourceAsync(
            key: "telegram-foo",
            name: "telegram-foo",
            kind: SourceKind.Http,
            origin: "http://93.184.216.34/list.m3u",
            priority: 0);
        Assert.True(first.Id > 0);

        // Act: segunda execução — só o lookup (read-only), sem tocar.
        var resolvedId = await _resolver.GetSourceIdByKeyAsync("telegram-foo");

        // Assert: mesmo Id.
        Assert.NotNull(resolvedId);
        Assert.Equal(first.Id, resolvedId!.Value);

        // E o nome continua o mesmo (sem updatePriority não renomeia).
        var stored = await _resolver.GetSourceAsync(first.Id);
        Assert.NotNull(stored);
        Assert.Equal("telegram-foo", stored!.Name);
    }

    // ════════════════════════════════════════════════════════════════
    // 5. Restart safety — chamada dupla devolve mesmo Id (determinístico)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RestartSafety_consecutiveCallsReturnSameId_noStaticState()
    {
        await _resolver.EnsureSourceAsync(
            key: "telegram-foo",
            name: "telegram-foo",
            kind: SourceKind.Http,
            origin: "http://93.184.216.34/list.m3u",
            priority: 0);

        // Duas chamadas consecutivas com a mesma chave devolvem o mesmo Id.
        var idA = await _resolver.GetSourceIdByKeyAsync("telegram-foo");
        var idB = await _resolver.GetSourceIdByKeyAsync("telegram-foo");
        Assert.NotNull(idA);
        Assert.Equal(idA, idB);

        // Lookup com chave inexistente devolve null sem efeitos secundários.
        var idMissingA = await _resolver.GetSourceIdByKeyAsync("telegram-nope");
        var idMissingB = await _resolver.GetSourceIdByKeyAsync("telegram-nope");
        Assert.Null(idMissingA);
        Assert.Null(idMissingB);

        // E o lookup NÃO cria a Source inexistente (read-only).
        await using var ctx = _factory.CreateDbContext();
        Assert.Single(await ctx.Sources.ToListAsync());

        // Edge cases: null/whitespace devolvem null sem query.
        Assert.Null(await _resolver.GetSourceIdByKeyAsync(null!));
        Assert.Null(await _resolver.GetSourceIdByKeyAsync(""));
        Assert.Null(await _resolver.GetSourceIdByKeyAsync("   "));
    }

    // ════════════════════════════════════════════════════════════════
    // 6. Observer null-safe — sourceId=null = comportamento W2-FU-1
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Observer_null_safe_when_sourceId_null_matches_w2_fu_1_behavior()
    {
        // Arrange: existe uma Source pré-existente, MAS o caller (path
        // legacy ou Source.Key inexistente) decide passar sourceId=null.
        // O observer NÃO deve tocá-la — comportamento W2-FU-1.
        var preExisting = await _resolver.EnsureSourceAsync(
            key: "telegram-foo",
            name: "telegram-foo",
            kind: SourceKind.Http,
            origin: "http://93.184.216.34/list.m3u",
            priority: 0);

        var report = new RunReport();
        var observer = new CatalogAcquisitionFailureObserver(
            _resolver, sourceId: null, report: report);

        // Act: 5 falhas de aquisição.
        for (var i = 0; i < 5; i++)
        {
            await observer.OnAcquisitionFailureAsync(
                new AcquisitionFailureInfo(
                    Url: "http://93.184.216.34/x.m3u",
                    FailureKind: AcquisitionFailureKind.Http4xx,
                    HttpStatus: 404,
                    Detail: "nf",
                    RetryExhausted: false,
                    Attempts: 1),
                default);
        }

        // Assert: RunReport.incrementado.
        Assert.Equal(5, report.AcquisitionFailures);
        Assert.Equal(5, report.AcquisitionTerminalFailures);

        // Assert: Source pré-existente NÃO foi tocada (sem persistência
        // porque sourceId=null).
        var stored = await _resolver.GetSourceAsync(preExisting.Id);
        Assert.NotNull(stored);
        Assert.Null(stored!.LastAcquisitionFailureKind);
        Assert.Null(stored.LastAcquisitionFailureAtUtc);
        Assert.Null(stored.LastAcquisitionHttpStatus);
    }
}