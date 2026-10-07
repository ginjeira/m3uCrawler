using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Recognition;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W5.1 — RecognitionPolicy: scopes/precedência, defaults (fuzzy opt-in),
/// persistência/versionamento/auditoria e snapshot imutável por Run.
/// Não cobre fuzzy matching, threshold behaviour, Review lifecycle nem
/// recognition order (W5.2–W5.6).
/// </summary>
public class WaveW51RecognitionPolicyTests : IAsyncLifetime
{
    private const string PreviousMigration = "20260919152739_AddChannelSourceStreamFingerprint";

    private readonly string _dbPath;
    private CatalogResolver _catalog = null!;
    private RecognitionPolicyResolver _resolver = null!;

    public WaveW51RecognitionPolicyTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"wave-w5.1-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        _catalog = new CatalogResolver(new TestDbContextFactory(_dbPath), _dbPath);
        _resolver = new RecognitionPolicyResolver(_catalog);
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        return Task.CompletedTask;
    }

    // 1 — default/system
    [Fact]
    public async Task Default_system_policy_is_enabled_with_fuzzy_off()
    {
        var set = await _resolver.LoadEffectivePoliciesAsync();
        var policy = set.ResolveWithScope(null, null, out var scope);

        Assert.Equal(RecognitionPolicyScope.System, scope);
        Assert.True(policy.Enabled);
        Assert.False(policy.FuzzyEnabled);
        Assert.Null(policy.FuzzyThreshold);
        Assert.Null(policy.FuzzyAmbiguityMargin);
        Assert.Null(policy.FuzzyWeightsJson);
    }

    // 2 — global override
    [Fact]
    public async Task Global_override_applies_when_no_specific_scope_exists()
    {
        await _catalog.UpsertGlobalRecognitionPolicyAsync(
            enabled: true, fuzzyEnabled: true, fuzzyThreshold: 90, fuzzyAmbiguityMargin: 7);

        var policy = await _resolver.ResolveEffectiveAsync(null, null);
        Assert.True(policy.FuzzyEnabled);
        Assert.Equal(90, policy.FuzzyThreshold);
        Assert.Equal(7, policy.FuzzyAmbiguityMargin);
    }

    // 3 — group over global
    [Fact]
    public async Task Group_override_wins_over_global()
    {
        await _catalog.UpsertGlobalRecognitionPolicyAsync(true, false, fuzzyThreshold: 80);
        await _catalog.UpsertGroupRecognitionPolicyAsync("sports", true, true, fuzzyThreshold: 85);

        var set = await _resolver.LoadEffectivePoliciesAsync();
        var policy = set.ResolveWithScope(null, "sports", out var scope);

        Assert.Equal(RecognitionPolicyScope.Group, scope);
        Assert.Equal(85, policy.FuzzyThreshold);
    }

    // 4 — channel over group
    [Fact]
    public async Task Channel_override_wins_over_group_and_global()
    {
        await _catalog.UpsertGlobalRecognitionPolicyAsync(true, false, fuzzyThreshold: 80);
        await _catalog.UpsertGroupRecognitionPolicyAsync("sports", true, true, fuzzyThreshold: 85);
        await _catalog.UpsertChannelRecognitionPolicyAsync("rtp1", true, true, fuzzyThreshold: 95);

        var set = await _resolver.LoadEffectivePoliciesAsync();
        var policy = set.ResolveWithScope("rtp1", "sports", out var scope);

        Assert.Equal(RecognitionPolicyScope.Channel, scope);
        Assert.Equal(95, policy.FuzzyThreshold);
    }

    // 5 — fallback por ausência de scope específico
    [Fact]
    public async Task Missing_specific_scope_falls_back_deterministically()
    {
        await _catalog.UpsertGlobalRecognitionPolicyAsync(true, false, fuzzyThreshold: 80);
        await _catalog.UpsertGroupRecognitionPolicyAsync("news", true, true, fuzzyThreshold: 88);

        var set = await _resolver.LoadEffectivePoliciesAsync();

        // canal desconhecido + grupo conhecido → grupo
        Assert.Equal(88, set.ResolveWithScope("ch-x", "news", out var s1).FuzzyThreshold);
        Assert.Equal(RecognitionPolicyScope.Group, s1);

        // canal desconhecido + grupo desconhecido → global
        Assert.Equal(80, set.ResolveWithScope("ch-x", "other", out var s2).FuzzyThreshold);
        Assert.Equal(RecognitionPolicyScope.Global, s2);
    }

    // 6 — precedência determinística
    [Fact]
    public async Task Precedence_is_deterministic_across_repeated_resolutions()
    {
        await _catalog.UpsertGlobalRecognitionPolicyAsync(true, false, fuzzyThreshold: 80);
        await _catalog.UpsertGroupRecognitionPolicyAsync("g", true, true, fuzzyThreshold: 85);
        await _catalog.UpsertChannelRecognitionPolicyAsync("c", true, true, fuzzyThreshold: 95);

        var set = await _resolver.LoadEffectivePoliciesAsync();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(95, set.Resolve("c", "g").FuzzyThreshold);
            Assert.Equal(85, set.Resolve("nope", "g").FuzzyThreshold);
            Assert.Equal(80, set.Resolve("nope", "nope").FuzzyThreshold);
        }
    }

    // 7 — fuzzy off por defeito
    [Fact]
    public async Task Persisted_policy_defaults_fuzzy_off()
    {
        var entity = await _catalog.UpsertGlobalRecognitionPolicyAsync(true, fuzzyEnabled: false);
        Assert.False(entity.FuzzyEnabled);

        await using var ctx = await _catalog.GetFactory().CreateDbContextAsync();
        var stored = await ctx.RecognitionPolicies.AsNoTracking().SingleAsync(p => p.ScopeKey == "global");
        Assert.False(stored.FuzzyEnabled);
    }

    // 8 — valores de fuzzy preservados
    [Fact]
    public async Task Fuzzy_parameters_round_trip_through_persistence()
    {
        const string weights = "{\"name\":2,\"tvgId\":3}";
        await _catalog.UpsertChannelRecognitionPolicyAsync(
            "c1", enabled: true, fuzzyEnabled: true,
            fuzzyThreshold: 91, fuzzyAmbiguityMargin: 6, fuzzyWeightsJson: weights);

        var policy = await _resolver.ResolveEffectiveAsync("c1", null);
        Assert.True(policy.FuzzyEnabled);
        Assert.Equal(91, policy.FuzzyThreshold);
        Assert.Equal(6, policy.FuzzyAmbiguityMargin);
        Assert.Equal(weights, policy.FuzzyWeightsJson);
    }

    // 9 — snapshot estável depois de alteração da policy
    [Fact]
    public async Task Snapshot_is_stable_after_policy_change()
    {
        var first = await _resolver.CreateSnapshotAsync("run-1");
        Assert.Contains("\"fuzzyEnabled\":false", first.PoliciesJson);

        await _catalog.UpsertGlobalRecognitionPolicyAsync(true, true, fuzzyThreshold: 99);

        var again = await _resolver.CreateSnapshotAsync("run-1");
        Assert.Equal(first.PoliciesJson, again.PoliciesJson);

        var secondRun = await _resolver.CreateSnapshotAsync("run-2");
        Assert.Contains("99", secondRun.PoliciesJson);
        Assert.NotEqual(first.PoliciesJson, secondRun.PoliciesJson);
    }

    // 10 — versões distintas coexistem
    [Fact]
    public async Task Different_policy_versions_can_coexist()
    {
        var globalV1 = await _catalog.UpsertGlobalRecognitionPolicyAsync(true, false);
        var channelV1 = await _catalog.UpsertChannelRecognitionPolicyAsync("c1", true, false);
        Assert.Equal(1, globalV1.Version);
        Assert.Equal(1, channelV1.Version);

        var globalV2 = await _catalog.UpsertGlobalRecognitionPolicyAsync(true, true, fuzzyThreshold: 70);
        Assert.Equal(2, globalV2.Version);

        await using var ctx = await _catalog.GetFactory().CreateDbContextAsync();
        var channelStored = await ctx.RecognitionPolicies.AsNoTracking()
            .SingleAsync(p => p.ScopeKey == "channel:c1");
        Assert.Equal(1, channelStored.Version);

        var resolved = await _resolver.ResolveEffectiveAsync("c1", null);
        Assert.False(resolved.FuzzyEnabled); // o override de canal (v1) vence a global (v2)
    }

    // 11 — alteração auditável
    [Fact]
    public async Task Policy_change_is_auditable()
    {
        await _catalog.UpsertGlobalRecognitionPolicyAsync(true, true, fuzzyThreshold: 80, actor: "admin");

        await using var ctx = await _catalog.GetFactory().CreateDbContextAsync();
        var audit = await ctx.AuditRecords.AsNoTracking()
            .Where(a => a.ObjectType == "recognition-policy")
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync();

        Assert.NotNull(audit);
        Assert.Equal("catalog.recognition-policy.upsert", audit!.Operation);
        Assert.Equal("global", audit.ObjectId);
        Assert.Equal("user", audit.ActorType);
        Assert.Contains("fuzzyThreshold", audit.AfterJson ?? string.Empty);
    }

    // 12 — migration Up/Down
    [Fact]
    public async Task Migration_is_additive_reversible_and_creates_expected_tables()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"wave-w51-mig-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<ChannelCatalogDbContext>()
                .UseSqlite($"Data Source={dbPath};Cache=Private")
                .Options;

            await using (var ctx = new ChannelCatalogDbContext(options))
            {
                await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            }

            Assert.False(TableExists(dbPath, "recognition_policies"));
            Assert.False(TableExists(dbPath, "recognition_policy_snapshots"));

            await using (var ctx = new ChannelCatalogDbContext(options))
            {
                await ctx.Database.MigrateAsync();
            }

            Assert.True(TableExists(dbPath, "recognition_policies"));
            Assert.True(TableExists(dbPath, "recognition_policy_snapshots"));

            await using (var ctx = new ChannelCatalogDbContext(options))
            {
                await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            }

            Assert.False(TableExists(dbPath, "recognition_policies"));
            Assert.False(TableExists(dbPath, "recognition_policy_snapshots"));

            await using (var ctx = new ChannelCatalogDbContext(options))
            {
                await ctx.Database.MigrateAsync();
            }
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    // 13 — dados existentes continuam utilizáveis
    [Fact]
    public async Task Migration_preserves_existing_rows()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"wave-w51-pres-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<ChannelCatalogDbContext>()
                .UseSqlite($"Data Source={dbPath};Cache=Private")
                .Options;

            await using (var ctx = new ChannelCatalogDbContext(options))
            {
                await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            }

            using (var conn = new SqliteConnection($"Data Source={dbPath};Cache=Private"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO canonical_channels (Key, DisplayName, EditorialCategory, EditorialGroup, PublicationPolicy, IsEnabled, CreatedAtUtc, UpdatedAtUtc) " +
                    "VALUES ('w51-ch','W5.1',0,0,0,1,'2026-01-01 00:00:00','2026-01-01 00:00:00');";
                cmd.ExecuteNonQuery();
            }

            await using (var ctx = new ChannelCatalogDbContext(options))
            {
                await ctx.Database.MigrateAsync();
            }

            await using (var ctx = new ChannelCatalogDbContext(options))
            {
                Assert.Equal(1, await ctx.CanonicalChannels.CountAsync());
                Assert.Equal("w51-ch", (await ctx.CanonicalChannels.SingleAsync()).Key);
            }
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    // Isolamento — channel não afecta outro channel
    [Fact]
    public async Task Channel_policy_does_not_affect_another_channel()
    {
        await _catalog.UpsertGlobalRecognitionPolicyAsync(true, false, fuzzyThreshold: 80);
        await _catalog.UpsertChannelRecognitionPolicyAsync("a", true, true, fuzzyThreshold: 95);

        var set = await _resolver.LoadEffectivePoliciesAsync();
        Assert.Equal(95, set.Resolve("a", null).FuzzyThreshold);
        Assert.Equal(80, set.Resolve("b", null).FuzzyThreshold);
    }

    // Isolamento — group não afecta outro group
    [Fact]
    public async Task Group_policy_does_not_affect_another_group()
    {
        await _catalog.UpsertGlobalRecognitionPolicyAsync(true, false, fuzzyThreshold: 80);
        await _catalog.UpsertGroupRecognitionPolicyAsync("g1", true, true, fuzzyThreshold: 88);

        var set = await _resolver.LoadEffectivePoliciesAsync();
        Assert.Equal(88, set.Resolve(null, "g1").FuzzyThreshold);
        Assert.Equal(80, set.Resolve(null, "g2").FuzzyThreshold);
    }

    // Isolamento — global não ultrapassa channel
    [Fact]
    public async Task Global_policy_never_overrides_channel_policy()
    {
        await _catalog.UpsertGlobalRecognitionPolicyAsync(true, true, fuzzyThreshold: 60);
        await _catalog.UpsertChannelRecognitionPolicyAsync("c", true, false);

        var resolved = await _resolver.ResolveEffectiveAsync("c", null);
        Assert.False(resolved.FuzzyEnabled);
    }

    // Snapshot posterior não é alterado por nova policy
    [Fact]
    public async Task Later_policy_change_does_not_mutate_existing_snapshot()
    {
        await _catalog.UpsertGlobalRecognitionPolicyAsync(true, true, fuzzyThreshold: 50);
        var snap = await _resolver.CreateSnapshotAsync("run-x");
        var payload = snap.PoliciesJson;

        await _catalog.UpsertGlobalRecognitionPolicyAsync(true, true, fuzzyThreshold: 100);
        var stored = await _resolver.GetSnapshotAsync("run-x");
        Assert.Equal(payload, stored!.PoliciesJson);
    }

    private static bool TableExists(string dbPath, string table)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Cache=Private");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$n";
        cmd.Parameters.AddWithValue("$n", table);
        return cmd.ExecuteScalar() is not null;
    }
}
