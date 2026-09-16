using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Services.Catalog;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9C.4 — subwave 1: modelo persistente de <see cref="LiveRunEntity"/>
/// e <see cref="LiveRunStepEntity"/>. Cobre mapeamento EF, migration
/// <c>AddLiveRuns</c>, chaves, índices e constraints. Não cobre
/// instrumentação da pipeline (subwave 3) nem endpoints (subwave 4).
/// </summary>
public class Phase94LiveRunModelTests
{
    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), $"phase94-liverun-{Guid.NewGuid():N}.db");

    private static DbContextOptions<ChannelCatalogDbContext> OptionsFor(string dbPath) =>
        new DbContextOptionsBuilder<ChannelCatalogDbContext>()
            .UseSqlite($"Data Source={dbPath};Cache=Private")
            .Options;

    private static async Task<IDbContextFactory<ChannelCatalogDbContext>> InitializeCatalogAsync(string dbPath)
    {
        var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        return new TestDbContextFactory(dbPath);
    }

    // ===================== Persistência básica =====================

    [Fact]
    public async Task LiveRun_is_persisted_with_default_terminal_status_unknown()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());

        await using var ctx = await factory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        ctx.LiveRuns.Add(new LiveRunEntity
        {
            RunId = "11111111-1111-1111-1111-111111111111",
            Mode = "telegram",
            Source = "cli",
            StartedAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });
        await ctx.SaveChangesAsync();

        await using var read = await factory.CreateDbContextAsync();
        var loaded = await read.LiveRuns.SingleAsync(r => r.RunId == "11111111-1111-1111-1111-111111111111");
        Assert.Equal(LiveRunTerminalStatus.Unknown, loaded.TerminalStatus);
        Assert.Null(loaded.FinishedAtUtc);
        Assert.Null(loaded.LastMessage);
        Assert.Equal("{}", loaded.CountsJson);
        Assert.Equal("telegram", loaded.Mode);
        Assert.Equal("cli", loaded.Source);
    }

    [Fact]
    public async Task LiveRun_can_be_marked_completed_with_finished_timestamp()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());

        var started = DateTime.UtcNow;
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.LiveRuns.Add(new LiveRunEntity
            {
                RunId = "22222222-2222-2222-2222-222222222222",
                Mode = "telegram",
                Source = "manual",
                StartedAtUtc = started,
                CreatedAtUtc = started,
                UpdatedAtUtc = started,
            });
            await ctx.SaveChangesAsync();
        }

        var finished = started.AddSeconds(42);
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            var run = await ctx.LiveRuns.SingleAsync(r => r.RunId == "22222222-2222-2222-2222-222222222222");
            run.TerminalStatus = LiveRunTerminalStatus.Completed;
            run.FinishedAtUtc = finished;
            run.LastMessage = "merged playlist 142 entries";
            run.UpdatedAtUtc = finished;
            await ctx.SaveChangesAsync();
        }

        await using var read = await factory.CreateDbContextAsync();
        var loaded = await read.LiveRuns.SingleAsync(r => r.RunId == "22222222-2222-2222-2222-222222222222");
        Assert.Equal(LiveRunTerminalStatus.Completed, loaded.TerminalStatus);
        Assert.Equal(finished, loaded.FinishedAtUtc);
        Assert.Equal("merged playlist 142 entries", loaded.LastMessage);
    }

    [Fact]
    public async Task RunId_is_unique_across_live_runs()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());
        var now = DateTime.UtcNow;
        var shared = Guid.NewGuid().ToString();

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.LiveRuns.Add(new LiveRunEntity
            {
                RunId = shared,
                Mode = "telegram",
                Source = "cli",
                StartedAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.LiveRuns.Add(new LiveRunEntity
            {
                RunId = shared,
                Mode = "telegram-maintain",
                Source = "manual",
                StartedAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task CountsJson_persists_arbitrary_payload()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());
        var now = DateTime.UtcNow;

        const string payload = "{\"messagesAnalyzed\":412,\"streamsWorking\":27}";

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.LiveRuns.Add(new LiveRunEntity
            {
                RunId = Guid.NewGuid().ToString(),
                Mode = "telegram",
                Source = "cli",
                StartedAtUtc = now,
                CountsJson = payload,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
            await ctx.SaveChangesAsync();
        }

        await using var read = await factory.CreateDbContextAsync();
        var loaded = await read.LiveRuns.OrderByDescending(r => r.Id).FirstAsync();
        Assert.Equal(payload, loaded.CountsJson);
    }

    // ===================== Steps: relação, FK cascade, índices =====================

    [Fact]
    public async Task LiveRunStep_is_persisted_with_enum_phase_and_timestamps()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());
        var now = DateTime.UtcNow;
        var runId = Guid.NewGuid().ToString();

        long stepId;
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            var run = new LiveRunEntity
            {
                RunId = runId,
                Mode = "telegram",
                Source = "cli",
                StartedAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            ctx.LiveRuns.Add(run);
            await ctx.SaveChangesAsync();

            ctx.LiveRunSteps.Add(new LiveRunStepEntity
            {
                LiveRunId = run.Id,
                Phase = LiveRunPhase.Discovering,
                PhaseIndex = 2,
                PhaseStartedAtUtc = now.AddSeconds(5),
                PhaseFinishedAtUtc = now.AddSeconds(10),
                Message = "found 27 candidates",
                Result = "ok",
            });
            await ctx.SaveChangesAsync();
            stepId = ctx.Entry(ctx.LiveRunSteps.Local.Last()).Entity.Id;
        }

        await using var read = await factory.CreateDbContextAsync();
        var loaded = await read.LiveRunSteps
            .Include(s => s.LiveRun)
            .SingleAsync(s => s.Id == stepId);

        Assert.Equal(LiveRunPhase.Discovering, loaded.Phase);
        Assert.Equal(2, loaded.PhaseIndex);
        Assert.Equal("found 27 candidates", loaded.Message);
        Assert.Equal("ok", loaded.Result);
        Assert.NotNull(loaded.LiveRun);
        Assert.Equal(runId, loaded.LiveRun!.RunId);
    }

    [Fact]
    public async Task LiveRunStep_unique_index_rejects_duplicate_phase_index()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());
        var now = DateTime.UtcNow;
        var runId = Guid.NewGuid().ToString();

        long runPk;
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            var run = new LiveRunEntity
            {
                RunId = runId,
                Mode = "telegram",
                Source = "cli",
                StartedAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            ctx.LiveRuns.Add(run);
            await ctx.SaveChangesAsync();
            runPk = run.Id;

            ctx.LiveRunSteps.Add(new LiveRunStepEntity
            {
                LiveRunId = runPk,
                Phase = LiveRunPhase.ReadingTelegram,
                PhaseIndex = 1,
                PhaseStartedAtUtc = now,
                Result = "ok",
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            ctx.LiveRunSteps.Add(new LiveRunStepEntity
            {
                LiveRunId = runPk,
                Phase = LiveRunPhase.Discovering,
                PhaseIndex = 1, // duplicado
                PhaseStartedAtUtc = now,
                Result = "ok",
            });
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Deleting_a_LiveRun_cascades_to_its_steps()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());
        var now = DateTime.UtcNow;
        var runId = Guid.NewGuid().ToString();

        long runPk;
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            var run = new LiveRunEntity
            {
                RunId = runId,
                Mode = "telegram",
                Source = "cli",
                StartedAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            ctx.LiveRuns.Add(run);
            await ctx.SaveChangesAsync();
            runPk = run.Id;

            ctx.LiveRunSteps.Add(new LiveRunStepEntity
            {
                LiveRunId = runPk, Phase = LiveRunPhase.Analyzing,
                PhaseIndex = 4, PhaseStartedAtUtc = now, Result = "ok",
            });
            ctx.LiveRunSteps.Add(new LiveRunStepEntity
            {
                LiveRunId = runPk, Phase = LiveRunPhase.Validating,
                PhaseIndex = 5, PhaseStartedAtUtc = now, Result = "ok",
            });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = await factory.CreateDbContextAsync())
        {
            var run = await ctx.LiveRuns.SingleAsync(r => r.Id == runPk);
            ctx.LiveRuns.Remove(run);
            await ctx.SaveChangesAsync();
        }

        await using var read = await factory.CreateDbContextAsync();
        Assert.Empty(await read.LiveRunSteps.Where(s => s.LiveRunId == runPk).ToListAsync());
        Assert.Empty(await read.LiveRuns.Where(r => r.Id == runPk).ToListAsync());
    }

    [Fact]
    public async Task Listing_steps_by_run_orders_by_PhaseIndex_ascending()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());
        var now = DateTime.UtcNow;
        var runId = Guid.NewGuid().ToString();

        long runPk;
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            var run = new LiveRunEntity
            {
                RunId = runId,
                Mode = "telegram-maintain",
                Source = "scheduler",
                StartedAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            ctx.LiveRuns.Add(run);
            await ctx.SaveChangesAsync();
            runPk = run.Id;

            // Inserção fora de ordem para validar a query por PhaseIndex.
            var phases = new[]
            {
                (LiveRunPhase.Composing, 6),
                (LiveRunPhase.Discovering, 2),
                (LiveRunPhase.ReadingTelegram, 1),
                (LiveRunPhase.Validating, 5),
            };
            foreach (var (phase, idx) in phases)
            {
                ctx.LiveRunSteps.Add(new LiveRunStepEntity
                {
                    LiveRunId = runPk, Phase = phase, PhaseIndex = idx,
                    PhaseStartedAtUtc = now, Result = "ok",
                });
            }
            await ctx.SaveChangesAsync();
        }

        await using var read = await factory.CreateDbContextAsync();
        var steps = await read.LiveRunSteps
            .Where(s => s.LiveRunId == runPk)
            .OrderBy(s => s.PhaseIndex)
            .ToListAsync();

        Assert.Equal(4, steps.Count);
        Assert.Equal(new[] { 1, 2, 5, 6 }, steps.Select(s => s.PhaseIndex).ToArray());
        Assert.Equal(
            new[] { LiveRunPhase.ReadingTelegram, LiveRunPhase.Discovering, LiveRunPhase.Validating, LiveRunPhase.Composing },
            steps.Select(s => s.Phase).ToArray());
    }

    // ===================== Migração: presença do schema =====================

    [Fact]
    public async Task Migration_AddLiveRuns_creates_both_tables_with_expected_columns()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());

        await using var ctx = await factory.CreateDbContextAsync();

        var runsCols = await GetTableColumnsAsync(ctx, "live_run_runs");
        var stepsCols = await GetTableColumnsAsync(ctx, "live_run_steps");

        // live_run_runs — sanity das colunas obrigatórias.
        foreach (var required in new[] { "Id", "RunId", "Mode", "Source", "StartedAtUtc",
                                          "TerminalStatus", "CountsJson", "CreatedAtUtc", "UpdatedAtUtc" })
        {
            Assert.Contains(required, runsCols);
        }
        // FinishedAtUtc / LastMessage são opcionais.
        Assert.Contains("FinishedAtUtc", runsCols);
        Assert.Contains("LastMessage", runsCols);

        // live_run_steps — sanity.
        foreach (var required in new[] { "Id", "LiveRunId", "Phase", "PhaseIndex",
                                          "PhaseStartedAtUtc", "Result" })
        {
            Assert.Contains(required, stepsCols);
        }
        Assert.Contains("PhaseFinishedAtUtc", stepsCols);
        Assert.Contains("Message", stepsCols);
    }

    [Fact]
    public async Task Indices_unique_on_RunId_and_on_LiveRunId_PhaseIndex_are_present()
    {
        var factory = await InitializeCatalogAsync(NewDbPath());

        await using var ctx = await factory.CreateDbContextAsync();
        var indices = await ctx.Database.SqlQueryRaw<string>(
                "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name IN ('live_run_runs','live_run_steps')")
            .ToListAsync();

        Assert.Contains(indices, n => n!.Contains("IX_live_run_runs_RunId", StringComparison.Ordinal));
        Assert.Contains(indices, n => n!.Contains("IX_live_run_steps_LiveRunId_PhaseIndex", StringComparison.Ordinal));
        Assert.Contains(indices, n => n!.Contains("IX_live_run_steps_LiveRunId_Phase", StringComparison.Ordinal));
    }

    private static async Task<HashSet<string>> GetTableColumnsAsync(ChannelCatalogDbContext ctx, string table)
    {
        // table é um literal controlado por esta classe de teste; não vem
        // de input externo. Suprime o aviso EF1002 (SqlQueryRaw com
        // string interpolada) — a protecção contra SQL injection é
        // responsabilidade do caller, e o uso aqui é seguro por construção.
        var rows = await ctx.Database
#pragma warning disable EF1002
            .SqlQueryRaw<string>($"SELECT name FROM pragma_table_info('{table}')")
#pragma warning restore EF1002
            .ToListAsync();
        return new HashSet<string>(rows!);
    }
}
