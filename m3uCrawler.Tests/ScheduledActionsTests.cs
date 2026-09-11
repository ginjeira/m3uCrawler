using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Automation;
using m3uCrawler.Services.Catalog;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 12 — Testes das <see cref="IScheduledAction"/> concretas e do
/// <see cref="ScheduledAutomationHost"/>. Não dependem da Internet
/// real: cada action é exercitada isoladamente, com mocks/fakes
/// mínimos ou com <see cref="M3uTesterService"/> real mas a testar
/// apenas em streams sintéticos (HTTP local nunca é tocado).
/// </summary>
public class ScheduledActionsTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;
    private ChannelCatalogBootstrapper _bootstrapper = null!;
    private CatalogResolver _resolver = null!;

    public ScheduledActionsTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"scheduled-actions-{Guid.NewGuid():N}.db");
    }

    private static ScheduledActionOptions NewOptions(string outputDir) =>
        new() { OutputDir = outputDir };

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        _bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await _bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        _resolver = new CatalogResolver(_factory, _dbPath);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------- IScheduledAction resolution via DI ----------

    [Fact]
    public void ScheduledAutomationHost_resolves_all_four_actions_via_DI()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sched-out-{Guid.NewGuid():N}");
        using var host = ScheduledAutomationHost.Build(
            _resolver, tempDir, DispatcharrConfig.Disabled());
        var names = host.RegisteredActions.Select(a => a.Name).ToArray();

        Assert.Contains(ScheduledM3uDiscoveryAction.ActionName, names);
        Assert.Contains(ScheduledValidationAction.ActionName, names);
        Assert.Contains(ScheduledPlaylistGenerationAction.ActionName, names);
        Assert.Contains(ScheduledDispatcharrSyncAction.ActionName, names);
        Assert.Equal(4, names.Length);
    }

    [Fact]
    public async Task ScheduledAutomationHost_runner_is_idempotent_on_repeated_Start()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sched-out-{Guid.NewGuid():N}");
        using var host = ScheduledAutomationHost.Build(
            _resolver, tempDir, DispatcharrConfig.Disabled());
        host.Start();
        host.Start(); // deve ser no-op; sem excepção
        await host.StopAsync();
    }

    // ---------- Action names estáveis ----------

    [Fact]
    public void Action_names_are_stable_and_distinct()
    {
        Assert.Equal("discoverM3u", ScheduledM3uDiscoveryAction.ActionName);
        Assert.Equal("validatePlaylist", ScheduledValidationAction.ActionName);
        Assert.Equal("generatePlaylist", ScheduledPlaylistGenerationAction.ActionName);
        Assert.Equal("syncDispatcharr", ScheduledDispatcharrSyncAction.ActionName);
        Assert.Equal(
            4,
            new[] {
                ScheduledM3uDiscoveryAction.ActionName,
                ScheduledValidationAction.ActionName,
                ScheduledPlaylistGenerationAction.ActionName,
                ScheduledDispatcharrSyncAction.ActionName,
            }.Distinct(StringComparer.Ordinal).Count());
    }

    // ---------- Dispatcharr action sem credenciais / enabled=false ----------

    [Fact]
    public async Task SyncDispatcharr_action_is_noop_when_disabled()
    {
        var action = new ScheduledDispatcharrSyncAction(
            DispatcharrConfig.Disabled(),
            NewOptions(Path.Combine(Path.GetTempPath(), $"sched-out-{Guid.NewGuid():N}")));
        var result = await action.ExecuteAsync(CancellationToken.None);
        Assert.Equal("dispatcharr-disabled", result);
    }

    [Fact]
    public async Task SyncDispatcharr_action_is_noop_when_playlist_missing()
    {
        var cfg = new DispatcharrConfig
        {
            Enabled = true,
            BaseUrl = "http://test.local",
            AliasFile = null,
        };
        var action = new ScheduledDispatcharrSyncAction(
            cfg, NewOptions(Path.Combine(Path.GetTempPath(), $"sched-out-{Guid.NewGuid():N}")));
        var result = await action.ExecuteAsync(CancellationToken.None);
        Assert.Equal("no-playlist", result);
    }

    // ---------- generatePlaylist action ----------

    [Fact]
    public async Task GeneratePlaylist_action_throws_when_no_ordering_list()
    {
        var composer = new PlaylistComposerService(_factory);
        var writer = new PlaylistManagerService();
        var action = new ScheduledPlaylistGenerationAction(
            _resolver, composer, writer,
            NewOptions(Path.Combine(Path.GetTempPath(), $"sched-out-{Guid.NewGuid():N}")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            action.ExecuteAsync(CancellationToken.None));
    }

    // ---------- validatePlaylist action ----------

    [Fact]
    public async Task ValidatePlaylist_action_returns_no_playlist_when_file_missing()
    {
        var tester = new M3uTesterService();
        var reader = new PlaylistManagerService();
        try
        {
            var action = new ScheduledValidationAction(
                tester, reader,
                NewOptions(Path.Combine(Path.GetTempPath(), $"sched-out-{Guid.NewGuid():N}")));
            var result = await action.ExecuteAsync(CancellationToken.None);
            Assert.Equal("no-playlist", result);
        }
        finally
        {
            tester.Dispose();
        }
    }

    // ---------- Cancellation ----------

    [Fact]
    public async Task ValidatePlaylist_action_propagates_cancellation()
    {
        // ValidatePlaylist itera sobre streams existentes e chama
        // TestM3u8Stream — bom sítio para testar propagação de cancellation.
        // Como TestM3u8Stream tenta abrir socket real, usamos uma
        // CancellationTokenSource já cancelada: a primeira iteração
        // lança antes de tocar em rede.
        var tempDir = Path.Combine(Path.GetTempPath(), $"sched-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        // Criar playlist sintética com 1 stream (a cancellation vai
        // ocorrer antes do primeiro teste real).
        await File.WriteAllTextAsync(
            Path.Combine(tempDir, "playlist.m3u"),
            "#EXTM3U\n#EXTINF:-1,Test\nhttp://example.invalid/s\n");

        var tester = new M3uTesterService();
        var reader = new PlaylistManagerService();
        try
        {
            var action = new ScheduledValidationAction(
                tester, reader,
                NewOptions(tempDir));
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                action.ExecuteAsync(cts.Token));
        }
        finally
        {
            tester.Dispose();
        }
    }

    // ---------- Runner tick: job disabled não corre ----------

    [Fact]
    public async Task Runner_tick_skips_disabled_jobs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sched-out-{Guid.NewGuid():N}");
        using var host = ScheduledAutomationHost.Build(
            _resolver, tempDir, DispatcharrConfig.Disabled());

        // Job com NextRunAtUtc no passado mas IsEnabled=false não deve correr.
        await _resolver.UpsertScheduledJobAsync(
            "disabled-job", "* * * * *",
            ScheduledDispatcharrSyncAction.ActionName, isEnabled: false);

        var ran = await host.Runner.TickOnceAsync();
        Assert.Equal(0, ran);
    }

    // ---------- Runner tick: action desconhecida grava erro e continua ----------

    [Fact]
    public async Task Runner_tick_marks_unknown_action_with_error_and_advances_next()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sched-out-{Guid.NewGuid():N}");
        using var host = ScheduledAutomationHost.Build(
            _resolver, tempDir, DispatcharrConfig.Disabled());

        // Criar job já vencido com action inexistente.
        await _resolver.UpsertScheduledJobAsync(
            "unknown-action-job", "* * * * *",
            "this-action-does-not-exist", isEnabled: true);
        var jobId = (await _resolver.ListScheduledJobsAsync()).First().Id;

        // Forçar NextRunAtUtc no passado. Usamos o resolver directamente
        // (que cria/descarta o seu próprio contexto) em vez de um contexto
        // partilhado, para evitar caching a nível de DbContext.
        await _resolver.MarkScheduledJobRanAsync(jobId, DateTime.UtcNow.AddMinutes(-5), "warmup");

        var ran = await host.Runner.TickOnceAsync();
        // O runner não conta unknown-action como "ran" (faz continue sem
        // incrementar). A prova de execução é o LastResult persistido.
        var reloaded = (await _resolver.ListScheduledJobsAsync()).First();
        Assert.StartsWith("unknown-action:", reloaded.LastResult);
        Assert.NotNull(reloaded.LastRunAtUtc);
        Assert.True(reloaded.NextRunAtUtc > DateTime.UtcNow);
        _ = ran; // suprimir warning
    }

    // ---------- Runner tick: action existente executa e persiste resultado ----------

    [Fact]
    public async Task Runner_tick_executes_concrete_action_and_persists_result()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sched-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        using var host = ScheduledAutomationHost.Build(
            _resolver, tempDir, DispatcharrConfig.Disabled());

        await _resolver.UpsertScheduledJobAsync(
            "every-minute-sync", "* * * * *",
            ScheduledDispatcharrSyncAction.ActionName, isEnabled: true);
        var jobId = (await _resolver.ListScheduledJobsAsync()).First().Id;

        // Forçar NextRunAtUtc para o passado via MarkScheduledJobRanAsync
        // (que já existe e escreve no DB).
        await _resolver.MarkScheduledJobRanAsync(jobId, DateTime.UtcNow.AddMinutes(-1), "warmup");

        var ran = await host.Runner.TickOnceAsync();
        Assert.Equal(1, ran);

        var reloaded = (await _resolver.ListScheduledJobsAsync()).First();
        Assert.Equal("dispatcharr-disabled", reloaded.LastResult);
        Assert.NotNull(reloaded.LastRunAtUtc);
        Assert.True(reloaded.NextRunAtUtc > DateTime.UtcNow);
    }

    // ---------- Runner tick: job não vencido não corre ----------

    [Fact]
    public async Task Runner_tick_skips_job_not_due_yet()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sched-out-{Guid.NewGuid():N}");
        using var host = ScheduledAutomationHost.Build(
            _resolver, tempDir, DispatcharrConfig.Disabled());

        await _resolver.UpsertScheduledJobAsync(
            "future-job", "0 12 * * *",
            ScheduledDispatcharrSyncAction.ActionName, isEnabled: true);

        var ran = await host.Runner.TickOnceAsync();
        Assert.Equal(0, ran);
    }
}
