using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Dispatcharr;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.Sync;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Testes de integração para o filtro redundante de ownership em
/// <see cref="DispatcharrSyncService.ApplyAsync"/>.
///
/// <para>
/// O filtro primário vive no <c>ChannelMatcher.BuildExistingDecision</c>
/// (camada pura). Esta camada em <c>ApplyAsync</c> é uma salvaguarda
/// adicional: mesmo que um plano inválido contenha
/// <c>SyncOutcome.Removed</c> para uma stream com ownership
/// <c>External</c> ou <c>Unknown</c>, NUNCA deve emitir DELETE.
/// Apenas streams comprovadamente <c>CrawlerManaged</c> podem ser
/// removidas.
/// </para>
///
/// <para>
/// Estes testes usam um cliente Dispatcharr falso
/// (<see cref="OwnershipGuardRecordingHandler"/>) que regista todas as
/// chamadas HTTP e nunca toca na rede.
/// </para>
/// </summary>
public class DispatcharrSyncServiceOwnershipGuardTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;

    public DispatcharrSyncServiceOwnershipGuardTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"channel-catalog-ownership-guard-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task External_stream_marked_Removed_in_plan_is_never_deleted_by_ApplyAsync()
    {
        // Stream id=7001 ownership External. Plano (artificial) marca
        // como Removed. ApplyAsync NÃO deve chamar DELETE.
        await using (var ctx = _factory.CreateDbContext())
        {
            ctx.DispatcharrStreamOwnerships.Add(new DispatcharrStreamOwnershipEntity
            {
                DispatcharrStreamId = 7001,
                DispatcharrChannelId = 700,
                Ownership = StreamOwnership.External,
                CreatedBySyncRunId = null,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var resolver = new CatalogResolver(_factory);
        var (svc, handler, state) = BuildSvc(resolver);

        var decision = new ChannelDecision
        {
            Identity = "Sport TV NBA",
            CanonicalName = "Sport TV NBA",
            Outcome = SyncOutcome.ExistingReassigned,
            ExistingChannelId = 700,
            ChannelGroupName = "SPORT TV CHANNELS",
            OutputGroup = null,
            MatchReason = "exact",
            MatchScore = 100,
            Streams = new[]
            {
                new StreamMatchDecision
                {
                    Provider = "external",
                    StreamUrl = "https://external.test/nba",
                    StreamName = "Sport TV NBA",
                    Outcome = SyncOutcome.Removed,
                    ExistingStreamId = 7001,
                    IsWorking = true,
                    GroupName = "SPORT TV CHANNELS",
                },
            },
        };
        var plan = new MatchPlan
        {
            GeneratedAtUtc = "2026-09-04T00:00:00Z",
            SourcePlaylistPath = "x.m3u",
            DispatcharrBaseUrl = "http://dispatcharr.local",
            DryRun = false,
            MatchThreshold = 80,
            Counts = new SyncReportCounts(),
            Channels = new[] { decision },
            AmbiguousGroups = Array.Empty<AmbiguousGroupEntry>(),
            ClassifiedExclusions = Array.Empty<ClassifiedExclusion>(),
            UnknownReviewRequired = Array.Empty<ClassifiedExclusion>(),
        };

        await svc.ApplyAsync(plan, state, new List<FailedReportEntry>(), CancellationToken.None);

        // Defesa redundante: NENHUM DELETE para a stream 7001.
        Assert.DoesNotContain(7001L, handler.DeleteStreamIds);
    }

    [Fact]
    public async Task Unknown_stream_marked_Removed_in_plan_is_never_deleted_by_ApplyAsync()
    {
        // Stream id=8002 sem registo de ownership (Unknown por
        // bootstrap default). Plano marca como Removed. Sem DELETE.
        var resolver = new CatalogResolver(_factory);
        var (svc, handler, state) = BuildSvc(resolver);

        var decision = new ChannelDecision
        {
            Identity = "Sport TV NBA",
            CanonicalName = "Sport TV NBA",
            Outcome = SyncOutcome.ExistingReassigned,
            ExistingChannelId = 800,
            ChannelGroupName = "SPORT TV CHANNELS",
            OutputGroup = null,
            MatchReason = "exact",
            MatchScore = 100,
            Streams = new[]
            {
                new StreamMatchDecision
                {
                    Provider = "(unknown)",
                    StreamUrl = "https://unknown.test/nba",
                    StreamName = "Sport TV NBA",
                    Outcome = SyncOutcome.Removed,
                    ExistingStreamId = 8002,
                    IsWorking = true,
                    GroupName = "SPORT TV CHANNELS",
                },
            },
        };
        var plan = new MatchPlan
        {
            GeneratedAtUtc = "2026-09-04T00:00:00Z",
            SourcePlaylistPath = "x.m3u",
            DispatcharrBaseUrl = "http://dispatcharr.local",
            DryRun = false,
            MatchThreshold = 80,
            Counts = new SyncReportCounts(),
            Channels = new[] { decision },
            AmbiguousGroups = Array.Empty<AmbiguousGroupEntry>(),
            ClassifiedExclusions = Array.Empty<ClassifiedExclusion>(),
            UnknownReviewRequired = Array.Empty<ClassifiedExclusion>(),
        };

        await svc.ApplyAsync(plan, state, new List<FailedReportEntry>(), CancellationToken.None);

        Assert.DoesNotContain(8002L, handler.DeleteStreamIds);
    }

    [Fact]
    public async Task CrawlerManaged_stream_marked_Removed_in_plan_is_deleted_by_ApplyAsync()
    {
        // Stream id=9001 ownership CrawlerManaged. Plano marca como
        // Removed (legítimo). ApplyAsync DEVE chamar DELETE.
        await using (var ctx = _factory.CreateDbContext())
        {
            ctx.DispatcharrStreamOwnerships.Add(new DispatcharrStreamOwnershipEntity
            {
                DispatcharrStreamId = 9001,
                DispatcharrChannelId = 900,
                Ownership = StreamOwnership.CrawlerManaged,
                CreatedBySyncRunId = null,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var resolver = new CatalogResolver(_factory);
        var (svc, handler, state) = BuildSvc(resolver);

        var decision = new ChannelDecision
        {
            Identity = "Benfica TV",
            CanonicalName = "Benfica TV",
            Outcome = SyncOutcome.ExistingReassigned,
            ExistingChannelId = 900,
            ChannelGroupName = "DESPORTO",
            OutputGroup = null,
            MatchReason = "exact",
            MatchScore = 100,
            Streams = new[]
            {
                new StreamMatchDecision
                {
                    Provider = "crawler",
                    StreamUrl = "https://crawler.test/benfica",
                    StreamName = "Benfica TV",
                    Outcome = SyncOutcome.Removed,
                    ExistingStreamId = 9001,
                    IsWorking = false,
                    GroupName = "DESPORTO",
                },
            },
            StreamsEmptied = true,
        };
        var plan = new MatchPlan
        {
            GeneratedAtUtc = "2026-09-04T00:00:00Z",
            SourcePlaylistPath = "x.m3u",
            DispatcharrBaseUrl = "http://dispatcharr.local",
            DryRun = false,
            MatchThreshold = 80,
            Counts = new SyncReportCounts(),
            Channels = new[] { decision },
            AmbiguousGroups = Array.Empty<AmbiguousGroupEntry>(),
            ClassifiedExclusions = Array.Empty<ClassifiedExclusion>(),
            UnknownReviewRequired = Array.Empty<ClassifiedExclusion>(),
        };

        await svc.ApplyAsync(plan, state, new List<FailedReportEntry>(), CancellationToken.None);

        Assert.Contains(9001L, handler.DeleteStreamIds);
    }

    private (DispatcharrSyncService svc, OwnershipGuardRecordingHandler handler, DispatcharrState state)
        BuildSvc(CatalogResolver resolver)
    {
        var cfg = new DispatcharrConfig
        {
            Enabled = true,
            BaseUrl = "http://dispatcharr.local",
            ApiKey = "PLACEHOLDER-API-KEY",
            DryRun = false,
            MatchThreshold = 80,
        };

        var handler = new OwnershipGuardRecordingHandler();
        var auth = new DispatcharrAuthState();
        auth.Set("PLACEHOLDER-API-KEY", null);
        var login = new DispatcharrLoginApi(new HttpClient()) { ApiKey = "PLACEHOLDER-API-KEY" };
        var authHandler = new DispatcharrAuthHandler(auth, login) { InnerHandler = handler };
        var client = new HttpClient(authHandler) { BaseAddress = new Uri("http://dispatcharr.local/api/") };

        var svc = new DispatcharrSyncService(
            cfg, Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid():N}"),
            aliases: new AliasResolver(),
            ordering: new m3uCrawler.Services.SourceOrdering.StreamOrderingPolicy(),
            channels: new DispatcharrChannelClient(client),
            streams: new DispatcharrStreamClient(client),
            m3u: new DispatcharrM3UClient(client),
            http: client,
            auth: auth,
            login: login,
            catalog: resolver);

        var state = new DispatcharrState(
            Channels: Array.Empty<DispatcharrChannel>(),
            Streams: Array.Empty<DispatcharrStream>(),
            Groups: Array.Empty<DispatcharrChannelGroup>(),
            Version: "0.30.0");

        return (svc, handler, state);
    }

    private sealed class OwnershipGuardRecordingHandler : HttpMessageHandler
    {
        public List<string> Traces { get; } = new();
        public List<long> DeleteStreamIds { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            var method = req.Method.ToString().ToUpperInvariant();
            Traces.Add($"{method} {path}");

            if (method == "GET" && path.EndsWith("/api/channels/channels/"))
                return Task.FromResult(JsonResp(new { count = 0, results = Array.Empty<object>() }));
            if (method == "GET" && path.EndsWith("/api/channels/streams/"))
                return Task.FromResult(JsonResp(new { count = 0, results = Array.Empty<object>() }));
            if (method == "GET" && path.EndsWith("/api/channels/groups/"))
                return Task.FromResult(JsonResp(new { count = 0, results = Array.Empty<object>() }));
            if (method == "GET" && path.EndsWith("/api/core/version/"))
                return Task.FromResult(JsonResp(new { version = "0.30.0" }));
            if (method == "GET" && path.Contains("/streams/") && path.Contains("/channels/"))
                return Task.FromResult(JsonResp(Array.Empty<object>()));

            if (method == "POST" && path.EndsWith("/api/channels/streams/"))
                return Task.FromResult(JsonResp(new { id = new Random().Next(1_000_000, 10_000_000), name = "n", url = "x", is_custom = true }));
            if (method == "POST" && path.EndsWith("/api/channels/groups/"))
                return Task.FromResult(JsonResp(new { id = 5L, name = "DESPORTO" }));

            if (method == "PATCH" && path.Contains("/api/channels/channels/"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            if (method == "DELETE" && path.Contains("/api/channels/streams/") && !path.EndsWith("/streams/"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(path, @"/streams/(\d+)/");
                if (match.Success && long.TryParse(match.Groups[1].Value, out var sid))
                    DeleteStreamIds.Add(sid);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResp(object payload) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
    }
}
