using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Validation;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W2 (2026-09-19) — persistência aditiva da falha de aquisição na
/// <c>Source</c> e agregação no <see cref="RunReport"/>
/// (<c>docs/Reestructure/19-FAILURE-MODEL.md §6</c>,
/// <c>16-PERSISTENCE.md §5</c>).
/// </summary>
public class WaveW2AcquisitionPersistenceTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;
    private ChannelCatalogBootstrapper _bootstrapper = null!;
    private CatalogResolver _resolver = null!;

    public WaveW2AcquisitionPersistenceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"wave-w2-{Guid.NewGuid():N}.db");
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
    // Source + Run representam a MESMA falha
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Retryable_failure_is_persisted_on_source_and_aggregated_on_run()
    {
        var source = await NewSourceAsync("w2-src-a");
        var when = new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
        var report = new RunReport();
        var observer = new CatalogAcquisitionFailureObserver(_resolver, source.Id, report, () => when);

        var failure = new AcquisitionFailureInfo(
            Url: "http://93.184.216.34/list.m3u",
            FailureKind: AcquisitionFailureKind.Http5xx,
            HttpStatus: 503,
            Detail: "upstream unavailable",
            RetryExhausted: true,
            Attempts: 3);

        await observer.OnAcquisitionFailureAsync(failure, default);

        var stored = await _resolver.GetSourceAsync(source.Id);
        Assert.NotNull(stored);
        Assert.Equal("Http5xx", stored!.LastAcquisitionFailureKind);
        Assert.Equal(when, stored.LastAcquisitionFailureAtUtc);
        Assert.Equal(503, stored.LastAcquisitionHttpStatus);
        Assert.Contains("upstream unavailable", stored.LastAcquisitionFailureDetail);

        Assert.Equal(1, report.AcquisitionFailures);
        Assert.Equal(1, report.AcquisitionRetryableFailures);
        Assert.Equal(0, report.AcquisitionTerminalFailures);
    }

    [Fact]
    public async Task Terminal_failure_is_persisted_and_aggregated_as_terminal()
    {
        var source = await NewSourceAsync("w2-src-b");
        var report = new RunReport();
        var observer = new CatalogAcquisitionFailureObserver(_resolver, source.Id, report);

        await observer.OnAcquisitionFailureAsync(
            new AcquisitionFailureInfo(
                Url: "http://93.184.216.34/empty.m3u",
                FailureKind: AcquisitionFailureKind.Empty,
                HttpStatus: 200,
                Detail: "empty-body",
                RetryExhausted: false,
                Attempts: 1),
            default);

        var stored = await _resolver.GetSourceAsync(source.Id);
        Assert.Equal("Empty", stored!.LastAcquisitionFailureKind);
        Assert.Equal(200, stored.LastAcquisitionHttpStatus);

        Assert.Equal(1, report.AcquisitionFailures);
        Assert.Equal(0, report.AcquisitionRetryableFailures);
        Assert.Equal(1, report.AcquisitionTerminalFailures);
    }

    [Fact]
    public async Task Run_aggregate_and_source_failure_use_the_same_kind()
    {
        var source = await NewSourceAsync("w2-src-c");
        var report = new RunReport();
        var observer = new CatalogAcquisitionFailureObserver(_resolver, source.Id, report);

        await observer.OnAcquisitionFailureAsync(
            new AcquisitionFailureInfo(
                "http://93.184.216.34/x.m3u",
                AcquisitionFailureKind.Dns,
                null, "dns", RetryExhausted: true, Attempts: 2),
            default);

        var stored = await _resolver.GetSourceAsync(source.Id);
        Assert.Equal("Dns", stored!.LastAcquisitionFailureKind);
        Assert.Equal(1, report.AcquisitionRetryableFailures);
    }

    // ════════════════════════════════════════════════════════════════
    // Sanitização do detalhe persistido
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Persisted_detail_never_contains_credentials_or_tokens()
    {
        var source = await NewSourceAsync("w2-src-secret");
        var report = new RunReport();
        var observer = new CatalogAcquisitionFailureObserver(_resolver, source.Id, report);

        var rawUrl = "http://alice:supersecret@93.184.216.34/get.php?username=alice&password=topsecret123&token=tok_abc";
        await observer.OnAcquisitionFailureAsync(
            new AcquisitionFailureInfo(
                rawUrl,
                AcquisitionFailureKind.Http4xx,
                403,
                $"Authorization: Bearer tok_abc password=topsecret123 url={rawUrl}",
                RetryExhausted: false,
                Attempts: 1),
            default);

        var stored = await _resolver.GetSourceAsync(source.Id);
        var detail = stored!.LastAcquisitionFailureDetail ?? string.Empty;

        Assert.DoesNotContain("supersecret", detail);
        Assert.DoesNotContain("topsecret123", detail);
        Assert.DoesNotContain("tok_abc", detail);
        Assert.Contains("***", detail);
    }

    [Fact]
    public async Task MarkSourceAcquisitionFailure_returns_false_for_unknown_source()
    {
        var result = await _resolver.MarkSourceAcquisitionFailureAsync(
            999_999, "Network", DateTime.UtcNow, null, "d");
        Assert.False(result);
    }

    // ════════════════════════════════════════════════════════════════
    // Migração aditiva
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Migration_adds_nullable_columns_and_preserves_existing_rows()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"wave-w2-mig-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<ChannelCatalogDbContext>()
                .UseSqlite($"Data Source={dbPath};Cache=Private")
                .Options;

            // Aplicar até à migration imediatamente anterior à W2.
            await using (var ctx = new ChannelCatalogDbContext(options))
            {
                var migrator = ctx.GetService<IMigrator>();
                await migrator.MigrateAsync("20260919114108_AddProviderAccountAndDiscoveryCandidate");
            }

            // Dados "legacy" no schema anterior.
            using (var conn = new SqliteConnection($"Data Source={dbPath};Cache=Private"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO sources (Name, Key, Kind, Origin, IsEnabled, Priority, " +
                    "CreatedAtUtc, UpdatedAtUtc) VALUES " +
                    "('Legacy W2','legacy-w2',3,'http://93.184.216.34/legacy.m3u',1,0," +
                    "'2026-01-01 00:00:00','2026-01-01 00:00:00')";
                cmd.ExecuteNonQuery();
            }

            await using (var ctx = new ChannelCatalogDbContext(options))
            {
                await ctx.Database.MigrateAsync();
            }

            await using (var ctx = new ChannelCatalogDbContext(options))
            {
                Assert.True(await ctx.Sources.AnyAsync(s => s.Key == "legacy-w2"));
                var legacy = await ctx.Sources.SingleAsync(s => s.Key == "legacy-w2");
                Assert.Null(legacy.LastAcquisitionFailureKind);
                Assert.Null(legacy.LastAcquisitionFailureAtUtc);
                Assert.Null(legacy.LastAcquisitionHttpStatus);
                Assert.Null(legacy.LastAcquisitionFailureDetail);
            }

            Assert.True(ColumnExists(dbPath, "sources", "LastAcquisitionFailureKind"));
            Assert.True(ColumnExists(dbPath, "sources", "LastAcquisitionFailureAtUtc"));
            Assert.True(ColumnExists(dbPath, "sources", "LastAcquisitionHttpStatus"));
            Assert.True(ColumnExists(dbPath, "sources", "LastAcquisitionFailureDetail"));
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    // ════════════════════════════════════════════════════════════════
    // LiveRunCounts espelha os contadores da Run
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void LiveRunCounts_mirrors_acquisition_counters()
    {
        var report = new RunReport
        {
            AcquisitionFailures = 5,
            AcquisitionRetryableFailures = 3,
            AcquisitionTerminalFailures = 2,
        };
        var counts = new m3uCrawler.Services.LiveRun.LiveRunCounts();
        counts.MirrorFrom(report);

        Assert.Equal(5, counts.AcquisitionFailures);
        Assert.Equal(3, counts.AcquisitionRetryableFailures);
        Assert.Equal(2, counts.AcquisitionTerminalFailures);
    }

    // ════════════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════════════

    private Task<SourceEntity> NewSourceAsync(string key)
        => _resolver.EnsureSourceAsync(
            key, key, SourceKind.Http, "http://93.184.216.34/list.m3u", 0);

    private static bool ColumnExists(string dbPath, string table, string column)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Cache=Private");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
