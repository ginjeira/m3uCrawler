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
using m3uCrawler.Services.Validation;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W1 (2026-09-19) — provas de SEMÂNTICA do modelo
/// Provider / ProviderAccount / DiscoveryCandidate e da identidade
/// funcional canónica (<c>AccountKey</c>), conforme
/// <c>docs/Reestructure/03-DISCOVERY.md §3</c>,
/// <c>32-DOMAIN-SCHEMA.md</c> e <c>16-PERSISTENCE.md §3</c>.
///
/// Os testes exercitam a camada de persistência real (SQLite + EF
/// Core + migration), não a mera existência de classes.
/// </summary>
public class W1AccountIdentityModelTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;
    private ChannelCatalogBootstrapper _bootstrapper = null!;
    private CatalogResolver _resolver = null!;

    public W1AccountIdentityModelTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"wave-w1-{Guid.NewGuid():N}.db");
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

    private static (string AccountKey, string ExternalIdentity) XtreamIdentity(string url)
    {
        Assert.True(
            AccountIdentity.TryComputeXtreamExternalIdentity(url, out var external),
            $"URL Xtream sem identidade funcional: {url}");
        Assert.NotNull(external);
        var key = AccountKey.Compose(ProviderNamespaces.Xtream, external);
        Assert.NotNull(key);
        return (key!, external!);
    }

    private async Task<ProviderEntity> XtreamProviderAsync()
        => await _resolver.EnsureProviderAsync(ProviderNamespaces.Xtream, "Xtream Codes", ProviderType.Xtream);

    // ════════════════════════════════════════════════════════════════
    // T1 — mesma conta funcional no mesmo Run → um único processamento
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task T1_Same_functional_account_in_one_run_yields_single_candidate_and_account()
    {
        var provider = await XtreamProviderAsync();
        var (accountKey, external) = XtreamIdentity(
            "http://host.example:8080/get.php?username=u1&password=p1");

        var account = await _resolver.EnsureProviderAccountAsync(provider.Id, accountKey, "u1");

        var first = await _resolver.RecordDiscoveryCandidateAsync(
            runId: "run-T1", providerId: provider.Id, providerAccountId: account.Id,
            externalIdentity: external, normalizedIdentity: external,
            evidence: "telegram:c1", status: DiscoveryCandidateStatus.Normalized);
        var second = await _resolver.RecordDiscoveryCandidateAsync(
            runId: "run-T1", providerId: provider.Id, providerAccountId: account.Id,
            externalIdentity: external, normalizedIdentity: external,
            evidence: "telegram:c2", status: DiscoveryCandidateStatus.Normalized);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Candidate.Id, second.Candidate.Id);

        // Uma única conta funcional e uma única ocorrência no Run.
        Assert.Single(await _resolver.ListProviderAccountsAsync(provider.Id));
        var candidates = await _resolver.ListDiscoveryCandidatesAsync("run-T1");
        Assert.Single(candidates);
        Assert.Equal(DiscoveryCandidateStatus.Deduplicated, candidates[0].Status);
    }

    // ════════════════════════════════════════════════════════════════
    // T2 — contas distintas não são deduplicadas
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task T2_Distinct_functional_accounts_are_not_deduped()
    {
        var provider = await XtreamProviderAsync();
        var (keyA, extA) = XtreamIdentity("http://host.example:8080/get.php?username=alice&password=p");
        var (keyB, extB) = XtreamIdentity("http://host.example:8080/get.php?username=bob&password=p");

        Assert.NotEqual(keyA, keyB);

        var accountA = await _resolver.EnsureProviderAccountAsync(provider.Id, keyA, "alice");
        var accountB = await _resolver.EnsureProviderAccountAsync(provider.Id, keyB, "bob");
        Assert.NotEqual(accountA.Id, accountB.Id);

        var first = await _resolver.RecordDiscoveryCandidateAsync(
            "run-T2", provider.Id, accountA.Id, extA, extA, "e-a");
        var second = await _resolver.RecordDiscoveryCandidateAsync(
            "run-T2", provider.Id, accountB.Id, extB, extB, "e-b");

        Assert.True(first.Created);
        Assert.True(second.Created);
        Assert.Equal(2, (await _resolver.ListDiscoveryCandidatesAsync("run-T2")).Count);
        Assert.Equal(2, (await _resolver.ListProviderAccountsAsync(provider.Id)).Count);
    }

    // ════════════════════════════════════════════════════════════════
    // T3 — evidência insuficiente não é deduplicada silenciosamente
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task T3_Insufficient_evidence_is_preserved_as_distinct_occurrence_without_dedup()
    {
        // URL genérica sem username: NÃO há identidade funcional estável.
        Assert.False(AccountIdentity.TryComputeXtreamExternalIdentity(
            "http://example.com/list.m3u8", out var external));
        Assert.Null(external);
        Assert.Null(AccountIdentity.ComputeXtreamAccountKey("http://example.com/list.m3u8"));
        Assert.Null(AccountIdentity.ComputeXtreamAccountKey(""));

        var first = await _resolver.RecordDiscoveryCandidateAsync(
            "run-T3", providerId: null, providerAccountId: null,
            externalIdentity: null, normalizedIdentity: null, evidence: "attachment-1");
        var second = await _resolver.RecordDiscoveryCandidateAsync(
            "run-T3", providerId: null, providerAccountId: null,
            externalIdentity: null, normalizedIdentity: null, evidence: "attachment-2");

        Assert.True(first.Created);
        Assert.True(second.Created);
        Assert.NotEqual(first.Candidate.Id, second.Candidate.Id);
        Assert.Null(first.Candidate.ProviderAccountId);
        Assert.Equal(0, first.Candidate.NormalizedIdentity.Length);
        Assert.Equal(2, (await _resolver.ListDiscoveryCandidatesAsync("run-T3")).Count);
    }

    // ════════════════════════════════════════════════════════════════
    // T4 — evidência não-funcional não cria outra conta
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task T4_NonFunctional_evidence_changes_do_not_create_another_account()
    {
        // Mesmo endpoint + username; muda o formato de playlist
        // (type/output) — routing, não identidade.
        var a = XtreamIdentity("http://host.example:8080/get.php?username=u1&password=p&type=m3u_plus");
        var b = XtreamIdentity("http://host.example:8080/get.php?username=u1&password=p&output=ts");
        Assert.Equal(a.AccountKey, b.AccountKey);

        // Normalização não-colapsante: NFKC aplica-se, casefold NÃO.
        Assert.Equal("ABC", AccountKey.NormalizeExternalIdentity("  \uFF21\uFF22\uFF23  "));
        Assert.NotEqual(
            AccountKey.NormalizeExternalIdentity("User"),
            AccountKey.NormalizeExternalIdentity("user"));

        var provider = await XtreamProviderAsync();
        var account1 = await _resolver.EnsureProviderAccountAsync(provider.Id, a.AccountKey, "u1");
        var account2 = await _resolver.EnsureProviderAccountAsync(provider.Id, b.AccountKey, "u1 display");
        Assert.Equal(account1.Id, account2.Id);
        Assert.Single(await _resolver.ListProviderAccountsAsync(provider.Id));
    }

    // ════════════════════════════════════════════════════════════════
    // T5 — password/credencial não participa da identidade
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void T5_Password_does_not_participate_in_functional_identity()
    {
        var p1 = XtreamIdentity("http://host.example:8080/get.php?username=u1&password=SECRET-A");
        var p2 = XtreamIdentity("http://host.example:8080/get.php?username=u1&password=SECRET-B");
        Assert.Equal(p1.AccountKey, p2.AccountKey);
        Assert.Equal(p1.ExternalIdentity, p2.ExternalIdentity);

        // Não deve conter a password em claro.
        Assert.DoesNotContain("SECRET", p1.ExternalIdentity);

        // Host diferente (infra-estrutura diferente) → conta diferente.
        var otherHost = XtreamIdentity("http://other.example:8080/get.php?username=u1&password=SECRET-A");
        Assert.NotEqual(p1.AccountKey, otherHost.AccountKey);
    }

    // ════════════════════════════════════════════════════════════════
    // T6 — mesma AccountKey em Runs diferentes → ocorrências distintas
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task T6_Same_account_key_in_different_runs_yields_distinct_candidates_per_run()
    {
        var provider = await XtreamProviderAsync();
        var (accountKey, external) = XtreamIdentity("http://host.example:8080/get.php?username=u1&password=p");
        var account = await _resolver.EnsureProviderAccountAsync(provider.Id, accountKey, "u1");

        var runA = await _resolver.RecordDiscoveryCandidateAsync(
            "run-A", provider.Id, account.Id, external, external, "e-a");
        var runB = await _resolver.RecordDiscoveryCandidateAsync(
            "run-B", provider.Id, account.Id, external, external, "e-b");

        Assert.True(runA.Created);
        Assert.True(runB.Created);
        Assert.NotEqual(runA.Candidate.Id, runB.Candidate.Id);
        Assert.Equal("run-A", runA.Candidate.RunId);
        Assert.Equal("run-B", runB.Candidate.RunId);
        Assert.Single(await _resolver.ListDiscoveryCandidatesAsync("run-A"));
        Assert.Single(await _resolver.ListDiscoveryCandidatesAsync("run-B"));
    }

    // ════════════════════════════════════════════════════════════════
    // T7 — ProviderAccount reutilizada para a mesma conta funcional
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task T7_ProviderAccount_is_reused_for_the_same_functional_account()
    {
        var provider = await XtreamProviderAsync();
        var (accountKey, external) = XtreamIdentity("http://host.example:8080/get.php?username=u1&password=p");

        var first = await _resolver.EnsureProviderAccountAsync(provider.Id, accountKey, "u1");
        var second = await _resolver.EnsureProviderAccountAsync(provider.Id, accountKey, "u1 renamed");
        Assert.Equal(first.Id, second.Id);
        Assert.Single(await _resolver.ListProviderAccountsAsync(provider.Id));

        // Duas Sources distintas podem apontar para a mesma conta.
        var src1 = await _resolver.EnsureSourceAsync(
            "xtream-u1-a", "Xtream u1 A", SourceKind.Xtream, "xtream://u1?country=pt", 0,
            providerAccountId: first.Id);
        var src2 = await _resolver.EnsureSourceAsync(
            "xtream-u1-b", "Xtream u1 B", SourceKind.Xtream, "xtream://u1?country=pt", 0,
            providerAccountId: first.Id);
        Assert.Equal(first.Id, src1.ProviderAccountId);
        Assert.Equal(first.Id, src2.ProviderAccountId);
        Assert.NotEqual(src1.Id, src2.Id);

        // Candidato associado de forma rastreável a conta, run e Source.
        var record = await _resolver.RecordDiscoveryCandidateAsync(
            "run-T7", provider.Id, first.Id, external, external, "e", sourceId: src1.Id);
        Assert.Equal(first.Id, record.Candidate.ProviderAccountId);
        Assert.Equal(src1.Id, record.Candidate.SourceId);
        Assert.Equal("run-T7", record.Candidate.RunId);
    }

    // ════════════════════════════════════════════════════════════════
    // T8 — migration cria constraints/índices sem destruir dados
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task T8_Migration_creates_expected_constraints_without_deleting_data()
    {
        // Dados válidos pré-existentes (antes de qualquer uso das novas tabelas).
        var channelCountBefore = (await _resolver.ListCanonicalChannelsAsync()).Count;
        var seedSource = await _resolver.EnsureSourceAsync(
            "migration-keep", "Migration Keep", SourceKind.M3U, "http://x/keep.m3u", 3);
        var sourceCountBefore = (await _resolver.ListSourcesAsync()).Count;

        var indexes = QueryNames("sqlite_master", "type='index' AND name LIKE 'IX_%'");
        Assert.Contains("IX_providers_Key", indexes);
        Assert.Contains("IX_provider_accounts_ProviderId_AccountKey", indexes);
        Assert.Contains("IX_discovery_candidates_RunId_ProviderAccountId", indexes);
        Assert.Contains("IX_sources_ProviderAccountId", indexes);

        var tables = QueryNames("sqlite_master", "type='table' AND name NOT LIKE 'sqlite_%'");
        Assert.Contains("providers", tables);
        Assert.Contains("provider_accounts", tables);
        Assert.Contains("discovery_candidates", tables);

        Assert.True(ColumnExists("sources", "ProviderAccountId"));

        // A coluna nova é nullable e não destrói rows existentes.
        Assert.Equal(sourceCountBefore, (await _resolver.ListSourcesAsync()).Count);
        Assert.Contains((await _resolver.ListSourcesAsync()), s => s.Id == seedSource.Id);

        // A constraint única (RunId, ProviderAccountId) é real: rejeita
        // dois candidatos do mesmo Run para a mesma conta, mesmo quando
        // inseridos directamente sem passar pela dedup da aplicação.
        var provider = await XtreamProviderAsync();
        var (accountKey, external) = XtreamIdentity("http://host.example:8080/get.php?username=u1&password=p");
        var account = await _resolver.EnsureProviderAccountAsync(provider.Id, accountKey, "u1");
        await _resolver.RecordDiscoveryCandidateAsync(
            "run-T8", provider.Id, account.Id, external, external, "e1");

        await using var ctx = _factory.CreateDbContext();
        ctx.DiscoveryCandidates.Add(new DiscoveryCandidateEntity
        {
            RunId = "run-T8",
            ProviderId = provider.Id,
            ProviderAccountId = account.Id,
            ExternalIdentity = external,
            NormalizedIdentity = external,
            Evidence = "e2",
            Status = DiscoveryCandidateStatus.Discovered,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(
            () => ctx.SaveChangesAsync());

        Assert.Equal(channelCountBefore, (await _resolver.ListCanonicalChannelsAsync()).Count);
    }

    // ════════════════════════════════════════════════════════════════
    // T8b — migration é aditiva desde o schema anterior e preserva rows
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task T8b_Migration_is_additive_from_previous_schema_and_preserves_rows()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"wave-w1-mig-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<ChannelCatalogDbContext>()
                .UseSqlite($"Data Source={dbPath};Cache=Private")
                .Options;

            // Aplicar apenas até à migration imediatamente anterior à W1.
            await using (var ctx = new ChannelCatalogDbContext(options))
            {
                var migrator = ctx.GetService<IMigrator>();
                await migrator.MigrateAsync("20260919075139_AddAuditRecords");
            }

            // Inserir dados "legacy" no schema anterior (sem as colunas W1).
            using (var conn = new SqliteConnection($"Data Source={dbPath};Cache=Private"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO sources (Name, Key, Kind, Origin, IsEnabled, Priority, " +
                    "CreatedAtUtc, UpdatedAtUtc) VALUES " +
                    "('Legacy Source','legacy-source',1,'http://x/legacy.m3u',1,0," +
                    "'2026-01-01 00:00:00','2026-01-01 00:00:00')";
                cmd.ExecuteNonQuery();
            }

            // Aplicar a migration W1 (aditiva).
            await using (var ctx = new ChannelCatalogDbContext(options))
            {
                await ctx.Database.MigrateAsync();
            }

            await using (var ctx = new ChannelCatalogDbContext(options))
            {
                Assert.True(await ctx.Sources.AnyAsync(s => s.Key == "legacy-source"));
                Assert.True(await ctx.Sources.AnyAsync(s => s.ProviderAccountId == null));
            }
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    private List<string> QueryNames(string table, string where)
    {
        var result = new List<string>();
        using var conn = new SqliteConnection($"Data Source={_dbPath};Cache=Private");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name FROM {table} WHERE {where}";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private bool ColumnExists(string table, string column)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Cache=Private");
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
