using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Audit;
using m3uCrawler.Services.Auth;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W6a — Auditoria administrativa. Verifica que mutações representativas
/// produzem registos com actor/operação/objecto/resultado correctos, que
/// falhas são registadas, que segredos são redigidos e que
/// <c>GET /api/audit</c> respeita autenticação, filtros e método.
/// </summary>
[Collection("DashboardStaticState")]
public class AuditRecordEndpointTests : IAsyncLifetime
{
    private const string ValidPassword = "a-very-strong-password";
    private static readonly DateTime FixedNow = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _outputDir;
    private readonly string _storePath;

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;
    private PlaylistComposerService _composer = null!;
    private ImportHistoryService _history = null!;
    private ConfigurationLifecycleService _lifecycle = null!;
    private AuthService _auth = null!;
    private BootstrapService _bootstrap = null!;
    private AuditService _audit = null!;
    private DashboardBootstrapEndpointTests.DashboardHarness? _harness;

    public AuditRecordEndpointTests()
    {
        _root = TestTempDb.SuitePath($"audit-record-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _outputDir = Path.Combine(_root, "output");
        _storePath = Path.Combine(_root, ConfigurationLifecycleStore.FileName);
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
        _lifecycle = new ConfigurationLifecycleService(
            new ConfigurationLifecycleStore(_storePath), _factory, _outputDir);

        var users = new AdminUserStore(_factory);
        _auth = new AuthService(users, new SessionStore(_factory));
        _bootstrap = new BootstrapService(
            _lifecycle, users,
            new BootstrapConfigurationValidator(_factory, _outputDir));

        _audit = new AuditService(_factory, () => FixedNow);
    }

    public async Task DisposeAsync()
    {
        if (_harness != null)
        {
            await _harness.DisposeAsync();
        }
        WebDashboardService.SetAuth(null, null);
        WebDashboardService.SetConfigurationLifecycle(null);
        WebDashboardService.SetAuditService(null);
        TestTempDb.CleanupDirectory(_root);
    }

    private DashboardBootstrapEndpointTests.DashboardHarness StartHarness()
    {
        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, _resolver, _composer, _history,
            _lifecycle, _auth, _bootstrap, webToken: null,
            auditService: _audit);
        return _harness;
    }

    private static async Task ReachReadyAsync(DashboardBootstrapEndpointTests.DashboardHarness harness)
    {
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.PostAsync("/api/bootstrap/start", EmptyJson())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.PostAsync(
            "/api/bootstrap/admin",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await harness.Client.PostAsync("/api/bootstrap/complete", EmptyJson())).StatusCode);
    }

    private static async Task<string> LoginAsync(DashboardBootstrapEndpointTests.DashboardHarness harness)
    {
        var login = await harness.Client.PostAsync(
            "/api/session",
            new StringContent(
                JsonSerializer.Serialize(new { username = "admin", password = ValidPassword }),
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var csrf = doc.RootElement.GetProperty("csrfToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(csrf));
        return csrf!;
    }

    private static StringContent EmptyJson()
        => new("{}", Encoding.UTF8, "application/json");

    private static HttpRequestMessage WithCsrf(HttpMethod method, string path, string body, string csrf)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CSRF-Token", csrf);
        return request;
    }

    private async Task<List<AuditRecordEntity>> ReadAuditRowsAsync()
    {
        await using var context = _factory.CreateDbContext();
        return await context.AuditRecords.AsNoTracking()
            .OrderBy(r => r.Id)
            .ToListAsync();
    }

    [Fact]
    public async Task Channel_create_records_row_with_user_actor_and_operation()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var key = "audit-create-" + Guid.NewGuid().ToString("N")[..8];
        var body = JsonSerializer.Serialize(new
        {
            key,
            displayName = "Audit Create",
            country = "PT",
            editorialCategory = "Live",
            groupKey = "pt-generalistas",
            publicationPolicy = "CreateEligible",
            isEnabled = true,
            aliases = new[] { "audit create hd" },
        });

        var created = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, "/api/catalog/channels", body, csrf));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var rows = await ReadAuditRowsAsync();
        var row = rows.Single(r => r.Operation == "catalog.channel.create" && r.ObjectId == key);
        Assert.Equal(AuditActorType.User, row.ActorType);
        Assert.Equal("admin", row.ActorName);
        Assert.Equal("canonical-channel", row.ObjectType);
        Assert.Equal(AuditResult.Success, row.Result);
        Assert.Equal(FixedNow, row.OccurredAtUtc);
        Assert.Null(row.BeforeJson);
        Assert.Contains(key, row.AfterJson);
    }

    [Fact]
    public async Task Failed_delete_records_failure_result()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var missingId = 987654321;
        await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Delete, $"/api/catalog/channels/{missingId}", "{}", csrf));

        var rows = await ReadAuditRowsAsync();
        var row = rows.Single(r => r.Operation == "catalog.channel.delete" && r.ObjectId == missingId.ToString());
        Assert.Equal(AuditResult.Failure, row.Result);
        Assert.Equal("not-found", row.Detail);
    }

    [Fact]
    public async Task Audit_endpoint_filters_and_never_returns_secrets()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var key = "audit-secret-" + Guid.NewGuid().ToString("N")[..8];
        var body = JsonSerializer.Serialize(new
        {
            key,
            displayName = "Audit Secret",
            editorialCategory = "Live",
            groupKey = "pt-generalistas",
            publicationPolicy = "CreateEligible",
            isEnabled = true,
        });
        Assert.Equal(
            HttpStatusCode.Created,
            (await harness.Client.SendAsync(WithCsrf(HttpMethod.Post, "/api/catalog/channels", body, csrf))).StatusCode);

        // Registo com valores que "parecem credenciais": devem ser redigidos.
        const string secret = "super-secret-password-value";
        await _audit.RecordAsync(new AuditRecord
        {
            Actor = AuditActor.User(1, "admin"),
            Operation = "test.secret",
            ObjectType = "test-object",
            ObjectId = "secret-1",
            Before = new { password = secret, apiKey = "api-key-value" },
            After = new { note = $"password={secret}" },
            Result = AuditResult.Success,
            Detail = $"token={secret}",
        });

        var response = await harness.Client.GetAsync($"/api/audit?objectType=canonical-channel&objectId={key}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, payload);

        using var doc = JsonDocument.Parse(payload);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Single(doc.RootElement.EnumerateArray());
        var item = doc.RootElement.EnumerateArray().Single();
        Assert.Equal("catalog.channel.create", item.GetProperty("operation").GetString());

        // O registo com segredo não aparece sob o filtro e, sem filtro,
        // nunca contém o valor em claro.
        var all = await harness.Client.GetAsync("/api/audit?limit=500");
        var allPayload = await all.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, allPayload);
        Assert.Contains("test.secret", allPayload);
    }

    [Fact]
    public async Task Audit_endpoint_requires_authentication()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);

        var response = await harness.Client.GetAsync("/api/audit");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Audit_endpoint_wrong_method_returns_405()
    {
        var harness = StartHarness();
        await ReachReadyAsync(harness);
        var csrf = await LoginAsync(harness);

        var response = await harness.Client.SendAsync(
            WithCsrf(HttpMethod.Post, "/api/audit", "{}", csrf));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}

/// <summary>
/// W6a — Sanitização centralizada usada pelos registos de auditoria.
/// </summary>
public class AuditRedactionTests
{
    [Fact]
    public void SanitizeJson_redacts_sensitive_keys_and_keeps_the_rest()
    {
        var json = "{\"displayName\":\"Canal\",\"password\":\"abc\",\"apiKey\":\"k\",\"nested\":{\"token\":\"t\"}}";
        var sanitized = CredentialSanitizer.SanitizeJson(json);

        Assert.DoesNotContain("abc", sanitized);
        Assert.DoesNotContain("\"k\"", sanitized);
        Assert.DoesNotContain("\"t\"", sanitized);
        Assert.Contains("Canal", sanitized);
        Assert.Contains("***", sanitized);
    }

    [Fact]
    public void SanitizeJson_redacts_url_credentials_in_string_values()
    {
        var json = "{\"origin\":\"http://user:pass@host/live/USER/PASS/1.ts\"}";
        var sanitized = CredentialSanitizer.SanitizeJson(json);

        Assert.DoesNotContain("user:pass@", sanitized);
        Assert.DoesNotContain("/live/USER/PASS", sanitized);
        Assert.Contains("***", sanitized);
    }

    [Fact]
    public void SanitizeSensitiveText_redacts_key_value_pairs()
    {
        var sanitized = CredentialSanitizer.SanitizeSensitiveText("password=hunter2 api_hash=deadbeef token=tok");

        Assert.DoesNotContain("hunter2", sanitized);
        Assert.DoesNotContain("deadbeef", sanitized);
        Assert.Contains("password=***", sanitized);
        Assert.Contains("api_hash=***", sanitized);
        Assert.Contains("token=***", sanitized);
    }

    [Fact]
    public async Task AuditService_is_best_effort_and_never_throws_on_failure()
    {
        // Fábrica apontando para um directório inexistente: a escrita falha.
        var badPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "x.db");
        var service = new AuditService(new TestDbContextFactory(badPath));

        await service.RecordAsync(new AuditRecord
        {
            Operation = "test.failure",
            ObjectType = "test-object",
            Result = AuditResult.Success,
        });
    }
}

/// <summary>
/// W6a — A migration aditiva de auditoria aplica-se a uma BD nova e a uma
/// BD existente, preservando os dados já presentes.
/// </summary>
public class AuditMigrationTests
{
    private const string PreviousMigration = "20260919063912_AddExternalIdentity";

    private static string NewDbPath() => TestTempDb.SuitePath($"audit-migration-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Migration_applies_on_fresh_database()
    {
        var dbPath = NewDbPath();
        try
        {
            var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
            await using var context = await bootstrapper.InitializeAsync();

            var applied = await context.Database.GetAppliedMigrationsAsync();
            Assert.Contains("AddAuditRecords", applied.Select(m => m.Split('_').Last()));

            // O schema existe e aceita escrita/leitura.
            context.AuditRecords.Add(new AuditRecordEntity
            {
                OccurredAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ActorType = "system",
                Operation = "migration.fresh",
                ObjectType = "test",
                Result = "success",
            });
            await context.SaveChangesAsync();
            Assert.Equal(1, await context.AuditRecords.CountAsync());
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task Migration_applies_on_existing_database_preserving_data()
    {
        var dbPath = NewDbPath();
        try
        {
            var options = new DbContextOptionsBuilder<ChannelCatalogDbContext>()
                .UseSqlite($"Data Source={dbPath};Cache=Private")
                .Options;

            const string sentinelKey = "audit-sentinel-channel";
            await using (var context = new ChannelCatalogDbContext(options))
            {
                // Migra apenas até à migration imediatamente anterior.
                var migrator = context.GetService<IMigrator>();
                migrator.Migrate(PreviousMigration);

                var applied = (await context.Database.GetAppliedMigrationsAsync()).ToList();
                Assert.DoesNotContain("AddAuditRecords", applied.Select(m => m.Split('_').Last()));

                // Dados pré-existentes (instalação antiga) que a migration
                // aditiva não pode perder. Inserção por SQL bruto: o modelo
                // EF actual inclui a coluna GroupId (Wave A), que ainda não
                // existe neste ponto do schema; o objectivo aqui é apenas
                // ter uma linha pré-existente sobrevivente.
                var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO canonical_channels (Key, DisplayName, EditorialCategory, EditorialGroup, PublicationPolicy, IsEnabled, CreatedAtUtc, UpdatedAtUtc) VALUES ({sentinelKey}, 'Audit Sentinel', 0, 0, 0, 1, {now}, {now});");
            }

            var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
            await using (var context = await bootstrapper.InitializeAsync())
            {
                var applied = (await context.Database.GetAppliedMigrationsAsync()).ToList();
                Assert.Contains("AddAuditRecords", applied.Select(m => m.Split('_').Last()));

                Assert.True(
                    await context.CanonicalChannels.AnyAsync(c => c.Key == sentinelKey),
                    "O canal pré-existente deve sobreviver à migration aditiva.");
            }
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }
}
