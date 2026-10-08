using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Automation;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using m3uCrawler.Services.LiveRun;
using m3uCrawler.Services.Sync;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Wave W2 — Prova de paridade do pipeline publicado entre CLI
/// single-cycle, dashboard (<c>POST /api/run/start</c>) e scheduler
/// (<c>ScheduledTelegramRunAction</c>), e prova das correcções de
/// scheduler (ordering-list por id, requisitos por capacidade).
/// </summary>
public class WaveW2PublicationTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly string _root;
    private readonly DateTime _fixedLocal = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Local);
    private readonly DateTime _fixedUtc = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;

    public WaveW2PublicationTests()
    {
        _root = TestTempDb.SuitePath($"source-selection-stage-w2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "channel-catalog.db");
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        await using (var ctx = await bootstrapper.InitializeAsync())
        {
        }
        _resolver = new CatalogResolver(_factory, _dbPath);
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        TestTempDb.CleanupDirectory(_root);
        return Task.CompletedTask;
    }

    private static List<M3uStream> NewStreams() => new()
    {
        new M3uStream
        {
            Url = "https://fixture.local/live/a.ts",
            Title = "Canal A",
            Group = "Geral",
            IsWorking = true,
            ResponseTime = 42,
        },
        new M3uStream
        {
            Url = "https://fixture.local/live/b.ts",
            Title = "Canal B",
            Group = "Geral",
            IsWorking = true,
            ResponseTime = 7,
        },
    };

    private RunReport NewReport() => new()
    {
        StartedAt = _fixedUtc,
        FinishedAt = _fixedUtc.AddSeconds(5),
        DurationMs = 5000,
        Status = "completed",
        MessagesAnalyzed = 11,
        CandidatesFound = 3,
        PlaylistsDownloaded = 2,
        CountryMatches = 2,
        StreamsExtracted = 2,
        StreamsTested = 2,
        StreamsWorking = 2,
    };

    private RunPublicationService NewService(string outputDir) => new(
        outputDir,
        new PlaylistManagerService(),
        new ImportHistoryService(outputDir),
        countryValidator: null,
        catalog: null,
        dispatcharr: new DispatcharrSyncCoordinator(() => DispatcharrConfig.Disabled()),
        clock: () => _fixedLocal,
        utcClock: () => _fixedUtc);

    // ===================== Paridade LiveRun ↔ CLI =====================

    [Fact]
    public async Task LiveRun_scheduler_telegram_publication_matches_cli_single_cycle()
    {
        var schedulerDir = Path.Combine(_root, "run-scheduler");
        var cliDir = Path.Combine(_root, "run-cli");
        Directory.CreateDirectory(schedulerDir);
        Directory.CreateDirectory(cliDir);

        var schedulerService = NewService(schedulerDir);
        var counting = new CountingPublicationService(schedulerService);

        // Simula POST /api/run/start / ScheduledTelegramRunAction: a
        // discovery é substituída por um fake; a publicação é a mesma
        // implementação partilhada usada pela CLI.
        var executor = new TelegramLiveRunExecutor(
            discover: (_, _, _) => Task.FromResult(new TelegramDiscoveryResult(NewStreams(), NewReport())),
            maintain: null,
            publication: counting,
            discoverySettings: null,
            countryCode: "pt");

        var host = new LiveRunHost(_factory);
        host.ConfigureExecutor(_ => executor);
        var coordinator = host.Coordinator!;

        var outcome = await coordinator.StartAsync(
            new LiveRunRequest
            {
                Mode = LiveRunMode.Telegram,
                Source = LiveRunSource.Scheduler,
                Keyword = "fixture",
                HistoryHours = 24,
                MaxStreams = 50,
            },
            CancellationToken.None);

        Assert.True(outcome.Succeeded, "A execução agendada devia ter terminado com sucesso.");
        Assert.Equal(1, counting.Calls);

        // Caminho CLI single-cycle: mesma fixture, mesmo relógio, output
        // distinto, chamando directamente o serviço partilhado.
        var cliService = NewService(cliDir);
        await cliService.PublishAsync(new RunPublicationRequest
        {
            Streams = NewStreams(),
            Report = NewReport(),
            Keyword = "fixture",
            HistoryHours = 24,
            MaxStreams = 50,
            CountryCode = "pt",
            HistoryMode = "TelegramSearch",
        });

        // Os artefactos têm de ser os mesmos (nomes e conteúdo).
        var expectedPlaylist = "telegram_playlist_20260919_120000.m3u";
        var expectedReport = "telegram_report_20260919_120000.json";

        Assert.True(File.Exists(Path.Combine(schedulerDir, expectedPlaylist)));
        Assert.True(File.Exists(Path.Combine(cliDir, expectedPlaylist)));

        Assert.Equal(
            await File.ReadAllTextAsync(Path.Combine(cliDir, expectedPlaylist)),
            await File.ReadAllTextAsync(Path.Combine(schedulerDir, expectedPlaylist)));

        Assert.Equal(
            await File.ReadAllTextAsync(Path.Combine(cliDir, expectedReport)),
            await File.ReadAllTextAsync(Path.Combine(schedulerDir, expectedReport)));

        Assert.Equal(
            await File.ReadAllTextAsync(Path.Combine(cliDir, "telegram_run_report.json")),
            await File.ReadAllTextAsync(Path.Combine(schedulerDir, "telegram_run_report.json")));

        Assert.Equal(
            await File.ReadAllTextAsync(Path.Combine(cliDir, "import_history.json")),
            await File.ReadAllTextAsync(Path.Combine(schedulerDir, "import_history.json")));

        // A ingestão/observação do run fica registada na BD (Source=scheduler).
        await using var ctx = _factory.CreateDbContext();
        var run = await ctx.LiveRuns.SingleAsync();
        Assert.Equal("scheduler", run.Source);
        Assert.Equal("telegram", run.Mode);
    }

    // ===================== generatePlaylist: id do job =====================

    [Fact]
    public async Task GeneratePlaylist_honors_selected_ordering_list_id()
    {
        var first = await _resolver.CreateOrderingListAsync("aaa", "AAA list", "pt", null);
        var second = await _resolver.CreateOrderingListAsync("zzz", "ZZZ list", null, null);

        var outputDir = Path.Combine(_root, "generate");
        Directory.CreateDirectory(outputDir);

        var action = new ScheduledPlaylistGenerationAction(
            _resolver,
            new PlaylistComposerService(_resolver.GetFactory()),
            new PlaylistManagerService(),
            new ScheduledActionOptions { OutputDir = outputDir });

        var result = await action.ExecuteAsync(
            new ScheduledJobContext(0, $"{ScheduledPlaylistGenerationAction.ActionName}:{second.Id}", ScheduledPlaylistGenerationAction.ActionName),
            CancellationToken.None);

        Assert.StartsWith("composed:", result);
        var content = await File.ReadAllTextAsync(Path.Combine(outputDir, "playlist.m3u"));
        Assert.Contains($"#ORDERING-LIST:{second.Id}=ZZZ list", content);
        Assert.DoesNotContain($"#ORDERING-LIST:{first.Id}", content);
    }

    [Fact]
    public async Task GeneratePlaylist_without_id_falls_back_deterministically_to_first_list()
    {
        var first = await _resolver.CreateOrderingListAsync("aaa", "AAA list", "pt", null);
        await _resolver.CreateOrderingListAsync("zzz", "ZZZ list", null, null);

        var outputDir = Path.Combine(_root, "generate-fallback");
        Directory.CreateDirectory(outputDir);

        var action = new ScheduledPlaylistGenerationAction(
            _resolver,
            new PlaylistComposerService(_resolver.GetFactory()),
            new PlaylistManagerService(),
            new ScheduledActionOptions { OutputDir = outputDir });

        var result = await action.ExecuteAsync(CancellationToken.None);

        Assert.StartsWith("composed:", result);
        var content = await File.ReadAllTextAsync(Path.Combine(outputDir, "playlist.m3u"));
        Assert.Contains($"#ORDERING-LIST:{first.Id}=AAA list", content);
    }

    [Fact]
    public async Task GeneratePlaylist_with_unknown_id_records_fallback()
    {
        await _resolver.CreateOrderingListAsync("aaa", "AAA list", "pt", null);

        var outputDir = Path.Combine(_root, "generate-unknown");
        Directory.CreateDirectory(outputDir);

        var action = new ScheduledPlaylistGenerationAction(
            _resolver,
            new PlaylistComposerService(_resolver.GetFactory()),
            new PlaylistManagerService(),
            new ScheduledActionOptions { OutputDir = outputDir });

        var result = await action.ExecuteAsync(
            new ScheduledJobContext(0, $"{ScheduledPlaylistGenerationAction.ActionName}:99999", ScheduledPlaylistGenerationAction.ActionName),
            CancellationToken.None);

        Assert.StartsWith("fallback:ordering-list-99999-not-found", result);
    }

    // ===================== Gate por capacidade =====================

    [Fact]
    public async Task Action_capability_gate_allows_non_telegram_and_blocks_telegram()
    {
        var snapshot = Snapshot(telegramAuthenticated: false, catalogOk: true, outputOk: true);
        var gate = new ActionCapabilityGate(_ => Task.FromResult(snapshot));

        Assert.True(await gate.IsReadyForAsync(ScheduledActionCapabilities.Output));
        Assert.True(await gate.IsReadyForAsync(
            ScheduledActionCapabilities.Catalog | ScheduledActionCapabilities.Output));
        Assert.True(await gate.IsReadyForAsync(ScheduledActionCapabilities.Dispatcharr));
        Assert.False(await gate.IsReadyForAsync(ScheduledActionCapabilities.Telegram));
        Assert.True(await gate.IsReadyForAsync(ScheduledActionCapabilities.None));
    }

    [Fact]
    public async Task Non_telegram_scheduled_job_runs_while_telegram_job_is_blocked()
    {
        var outputDir = Path.Combine(_root, "sched-capability");
        Directory.CreateDirectory(outputDir);
        await _resolver.CreateOrderingListAsync("list", "Main list", "pt", null);

        var snapshot = Snapshot(telegramAuthenticated: false, catalogOk: true, outputOk: true);
        using var host = ScheduledAutomationHost.Build(
            _resolver,
            outputDir,
            DispatcharrConfig.Disabled(),
            gate: new AlwaysReadyGate(),
            capabilityGate: new ActionCapabilityGate(_ => Task.FromResult(snapshot)));

        var generateJob = await _resolver.UpsertScheduledJobAsync(
            "play-list", "* * * * *", ScheduledPlaylistGenerationAction.ActionName, isEnabled: true);
        var telegramJob = await _resolver.UpsertScheduledJobAsync(
            "telegram", "* * * * *", ScheduledTelegramRunAction.TelegramActionName, isEnabled: true);
        await ForceDueAsync(generateJob.Id, telegramJob.Id);

        var ran = await host.Runner.TickOnceAsync();

        // Apenas a acção não-Telegram executa; a Telegram é bloqueada por capacidade.
        Assert.Equal(1, ran);

        await using var ctx = _factory.CreateDbContext();
        var jobs = ctx.ScheduledJobs.AsNoTracking().ToDictionary(j => j.Name);
        Assert.StartsWith("composed:", jobs["play-list"].LastResult);
        Assert.StartsWith(ScheduledJobRunner.BlockedCapabilityPrefix, jobs["telegram"].LastResult);
    }

    // ===================== Helpers =====================

    private async Task ForceDueAsync(params long[] jobIds)
    {
        await using var ctx = _factory.CreateDbContext();
        foreach (var id in jobIds)
        {
            var job = ctx.ScheduledJobs.First(j => j.Id == id);
            job.NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1);
        }
        await ctx.SaveChangesAsync();
    }

    private static OperationalReadinessSnapshot Snapshot(
        bool telegramAuthenticated, bool catalogOk, bool outputOk)
    {
        return new OperationalReadinessSnapshot(
            BootstrapReady: true,
            HasAdmin: true,
            TelegramAuthenticated: telegramAuthenticated,
            DispatcharrEnabled: false,
            DispatcharrValid: true,
            CatalogOk: catalogOk,
            CountryDataOk: true,
            OutputOk: outputOk,
            SourcesCount: 1,
            SetupComplete: false,
            OperationalReady: false,
            AdoptedFromLegacy: false,
            Items: Array.Empty<OperationalReadinessItem>(),
            MissingRequired: telegramAuthenticated ? Array.Empty<string>() : new[] { "telegram" });
    }

    private sealed class CountingPublicationService : IRunPublicationService
    {
        private readonly IRunPublicationService _inner;
        public int Calls { get; private set; }

        public CountingPublicationService(IRunPublicationService inner) => _inner = inner;

        public Task<RunPublicationResult> PublishAsync(
            RunPublicationRequest request,
            ILiveRunProgress? progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return _inner.PublishAsync(request, progress, cancellationToken);
        }
    }

    private sealed class AlwaysReadyGate : IConfigurationGate
    {
        public ConfigurationLifecycleState State => ConfigurationLifecycleState.Ready;

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }
}
