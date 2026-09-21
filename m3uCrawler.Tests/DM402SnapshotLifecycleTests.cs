using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.LiveRun;
using m3uCrawler.Services.Recognition;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// D-M4-02 — RecognitionPolicySnapshot lifecycle: criação no arranque do
/// Run, idempotência, race-safety e propagação da policy efectiva para a
/// ingestion do catálogo.
///
/// Decisão ratificada:
///   <c>recognition_policy_snapshots.RunId</c> é UNIQUE; o snapshot é
///   criado por <see cref="RunCoordinator"/> antes de iniciar a
///   pipeline, é imutável e nunca é regravado. Resolução da policy
///   efectiva no ingestion consome o snapshot (não a policy mutável).
///   Sem Run operacional (= sem <see cref="ILiveRunProgress"/>) ⇒ sem
///   snapshot, sem policy (B1).
/// </summary>
public class DM402SnapshotLifecycleTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;
    private ChannelCatalogBootstrapper _bootstrapper = null!;
    private CatalogResolver _catalog = null!;
    private RecognitionPolicyResolver _policyResolver = null!;

    public DM402SnapshotLifecycleTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(), $"dm402-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        _bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await _bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        _catalog = new CatalogResolver(_factory, _dbPath);
        _policyResolver = new RecognitionPolicyResolver(_catalog);
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        return Task.CompletedTask;
    }

    // ──────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────

    private sealed class CapturingPipeline : IRunPipeline
    {
        public int ExecuteCallCount { get; private set; }
        public Func<LiveRunRequest, Task>? OnExecute { get; set; }

        public Task ExecuteAsync(LiveRunRequest request, CancellationToken cancellationToken)
        {
            ExecuteCallCount++;
            return OnExecute?.Invoke(request) ?? Task.CompletedTask;
        }
    }

    private sealed class ThrowingPipeline : IRunPipeline
    {
        public Task ExecuteAsync(LiveRunRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("pipeline should not be invoked");
    }

    private async Task<List<RecognitionPolicySnapshotEntity>> SnapshotsAsync()
    {
        var factory = (IDbContextFactory<ChannelCatalogDbContext>)_factory;
        await using var ctx = await factory.CreateDbContextAsync();
        return await ctx.RecognitionPolicySnapshots.AsNoTracking().ToListAsync();
    }

    private RunCoordinator NewCoordinator(IRunPipeline pipeline, RecognitionPolicyResolver? resolver = null)
        => new RunCoordinator(_factory, _ => pipeline, logger: null, recognitionPolicyResolver: resolver);

    // ──────────────────────────────────────────────────────────────────
    // 1 — Run start creates snapshot before pipeline runs
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_start_creates_snapshot_before_pipeline_runs()
    {
        string? observedCoordinatorRunId = null;
        var pipeline = new CapturingPipeline
        {
            OnExecute = async req =>
            {
                // Assert: snapshot exists (1 row), ResolverVersion
                // corresponde a `rp1` (DL-110). O RunId do snapshot é
                // o do coordinator (validado abaixo após StartAsync).
                var snaps = await SnapshotsAsync();
                Assert.Single(snaps);
                Assert.Equal("rp1", snaps[0].ResolverVersion);
            },
        };
        var coordinator = NewCoordinator(pipeline, _policyResolver);

        // Pre-snapshot observability hook: snapshot doesn't exist yet.
        Assert.Empty(await SnapshotsAsync());

        var outcome = await coordinator.StartAsync(
            new LiveRunRequest
            {
                Mode = LiveRunMode.Telegram,
                Source = LiveRunSource.Manual,
                Keyword = "dm402-1",
            },
            CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.NotNull(outcome.Snapshot);
        observedCoordinatorRunId = outcome.Snapshot!.RunId;

        Assert.Equal(1, pipeline.ExecuteCallCount);
        Assert.Equal(LiveRunTerminalStatus.Completed, outcome.Snapshot.TerminalStatus);
        var snaps = await SnapshotsAsync();
        Assert.Single(snaps);
        Assert.Equal(observedCoordinatorRunId, snaps[0].RunId);
    }

    // ──────────────────────────────────────────────────────────────────
    // 2 — Snapshot creation failure marks run Failed and blocks pipeline
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Snapshot_creation_failure_marks_run_failed_and_blocks_pipeline()
    {
        var pipeline = new ThrowingPipeline();
        // Resolver que rebenta sempre que tenta criar snapshot. Usa um
        // CatalogResolver cuja factory aponta para um path inválido, de
        // modo a forçar DbUpdateException / IOException no
        // SaveRecognitionPolicySnapshotAsync.
        var brokenFactory = new BrokenDbContextFactory();
        var brokenCatalog = new CatalogResolver(brokenFactory, _dbPath);
        var brokenResolver = new RecognitionPolicyResolver(brokenCatalog);
        var coordinator = NewCoordinator(pipeline, brokenResolver);

        var outcome = await coordinator.StartAsync(
            new LiveRunRequest
            {
                Mode = LiveRunMode.Telegram,
                Source = LiveRunSource.Manual,
                Keyword = "dm402-2",
            },
            CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.NotNull(outcome.Snapshot);
        Assert.Equal(LiveRunTerminalStatus.Failed, outcome.Snapshot!.TerminalStatus);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Snapshot.LastMessage));
        Assert.Contains("snapshot", outcome.Snapshot.LastMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("snapshot creation exception", outcome.ErrorMessage);
        // Pipeline NÃO foi invocada — provamos pelo tipo ThrowingPipeline
        // (qualquer chamada teria lançado).
    }

    private sealed class BrokenDbContextFactory : IDbContextFactory<ChannelCatalogDbContext>
    {
        public ChannelCatalogDbContext CreateDbContext()
            => throw new InvalidOperationException("broken factory for test");

        public Task<ChannelCatalogDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("broken factory for test");
    }

    // ──────────────────────────────────────────────────────────────────
    // 3 — Sequential calls return same snapshot (idempotência)
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSnapshotAsync_sequential_calls_return_same_snapshot()
    {
        var first = await _policyResolver.CreateSnapshotAsync("run-seq");
        var again = await _policyResolver.CreateSnapshotAsync("run-seq");
        var third = await _policyResolver.CreateSnapshotAsync("run-seq");

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.Id, third.Id);
        Assert.Equal(first.PoliciesJson, again.PoliciesJson);
        Assert.Equal(first.PoliciesJson, third.PoliciesJson);
        Assert.Single(await SnapshotsAsync());
    }

    // ──────────────────────────────────────────────────────────────────
    // 4 — Concurrent calls never throw duplicate key
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSnapshotAsync_concurrent_calls_never_throw_duplicate_key()
    {
        var runId = "run-concurrent";
        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => _policyResolver.CreateSnapshotAsync(runId)))
            .ToArray();

        // Não rebenta com DbUpdateException em nenhum path.
        var snapshots = await Task.WhenAll(tasks);

        // Todos devolvem a mesma entidade (mesmo Id, mesmo PoliciesJson).
        Assert.All(snapshots, s =>
        {
            Assert.Equal(snapshots[0].Id, s.Id);
            Assert.Equal(snapshots[0].PoliciesJson, s.PoliciesJson);
            Assert.Equal(runId, s.RunId);
        });

        var rows = await SnapshotsAsync();
        Assert.Single(rows);
    }

    // ──────────────────────────────────────────────────────────────────
    // 5 — Snapshot policy is frozen for the run
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Snapshot_policy_is_frozen_for_run()
    {
        // Snapshot com a policy inicial (sem overrides).
        var firstSnapshot = await _policyResolver.CreateSnapshotAsync("run-frozen");

        // Mudar a policy em mutável (global). O snapshot NÃO deve mudar.
        await _catalog.UpsertGlobalRecognitionPolicyAsync(
            enabled: true, fuzzyEnabled: true, fuzzyThreshold: 88, fuzzyAmbiguityMargin: 9);

        // Obter via snapshot policy API: ainda é a frozen.
        var fromSnapshot = await _policyResolver.GetSnapshotPolicyAsync("run-frozen", null, null);
        Assert.NotNull(fromSnapshot);
        // A policy inicial tinha fuzzy off (Default); após mudança, a policy
        // global está fuzzy on. O snapshot NÃO reflecte a mudança.
        Assert.False(fromSnapshot!.FuzzyEnabled);
        Assert.Null(fromSnapshot.FuzzyThreshold);

        // Persistência directa: o PoliciesJson persistido é igual ao do
        // primeiro snapshot.
        var fromDb = await _catalog.GetRecognitionPolicySnapshotAsync("run-frozen");
        Assert.NotNull(fromDb);
        Assert.Equal(firstSnapshot.PoliciesJson, fromDb!.PoliciesJson);
    }

    // ──────────────────────────────────────────────────────────────────
    // 6 — Snapshot RunId is coordinator RunId, not trace.RunId
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Recognitional_run_id_never_uses_PipelineTrace_RunId()
    {
        // Mesmo com PipelineTrace activo, o snapshot usa o RunId do
        // coordinator (não o do trace).
        var pipeline = new CapturingPipeline();
        var coordinator = NewCoordinator(pipeline, _policyResolver);

        var outcome = await coordinator.StartAsync(
            new LiveRunRequest
            {
                Mode = LiveRunMode.Telegram,
                Source = LiveRunSource.Manual,
                Keyword = "dm402-6",
            },
            CancellationToken.None);

        Assert.True(outcome.Succeeded);
        var coordinatorRunId = outcome.Snapshot!.RunId;

        var snaps = await SnapshotsAsync();
        Assert.Single(snaps);
        Assert.Equal(coordinatorRunId, snaps[0].RunId);
        // Em produção, o PipelineTrace.RunId difere do RunId do
        // coordinator (ver `D-M4-02a`). Esta asserção documenta que o
        // snapshot.RunId nunca é um trace.RunId hipotético.
        Assert.False(string.IsNullOrWhiteSpace(snaps[0].RunId));
    }
}
