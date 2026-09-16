using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Dispatcharr;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using m3uCrawler.Services.Sync;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9C.3 — naming canónico no Dispatcharr: criação usa o
/// DisplayName actual do canal canónico; rename só para canais
/// CrawlerManaged; External/Unknown nunca são renomeados.
/// </summary>
public class Phase93DispatcharrNamingTests
{
    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), $"phase93-dispatcharr-{Guid.NewGuid():N}.db");

    private static async Task<(string dbPath, CatalogResolver resolver)> NewCatalogAsync()
    {
        var dbPath = NewDbPath();
        var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        return (dbPath, new CatalogResolver(new TestDbContextFactory(dbPath), dbPath));
    }

    private static DispatcharrSyncService BuildSvc(CapturingHandler handler, CatalogResolver? catalog)
    {
        var cfg = new DispatcharrConfig
        {
            Enabled = true,
            BaseUrl = "http://dispatcharr.local",
            ApiKey = "PLACEHOLDER-API-KEY",
            DryRun = false,
            MatchThreshold = 80,
        };

        var auth = new DispatcharrAuthState();
        auth.Set("PLACEHOLDER-API-KEY", null);
        var login = new DispatcharrLoginApi(new HttpClient()) { ApiKey = "PLACEHOLDER-API-KEY" };
        var authHandler = new DispatcharrAuthHandler(auth, login) { InnerHandler = handler };
        var client = new HttpClient(authHandler) { BaseAddress = new Uri("http://dispatcharr.local/api/") };

        return new DispatcharrSyncService(
            cfg,
            Path.Combine(Path.GetTempPath(), $"phase93-out-{Guid.NewGuid():N}"),
            aliases: new AliasResolver(),
            ordering: new StreamOrderingPolicy(),
            channels: new DispatcharrChannelClient(client),
            streams: new DispatcharrStreamClient(client),
            m3u: new DispatcharrM3UClient(client),
            http: client,
            auth: auth,
            login: login,
            catalog: catalog);
    }

    private static ChannelDecision NewChannelDecision(string canonicalName, string key, long? canonicalId) => new()
    {
        Identity = key,
        CanonicalName = canonicalName,
        CanonicalChannelKey = key,
        CanonicalChannelId = canonicalId,
        Outcome = SyncOutcome.NewChannel,
        ExistingChannelId = null,
        MatchReason = "no-match",
        MatchScore = 0,
        Streams = new[]
        {
            new StreamMatchDecision
            {
                Provider = "p",
                StreamUrl = "https://provider.example/x",
                StreamName = "X",
                Outcome = SyncOutcome.NewStream,
                ExistingStreamId = null,
                ProposedOrder = 0,
                OrderReason = "new-channel-initial",
                IsWorking = true,
            },
        },
        AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
    };

    [Fact]
    public async Task NewChannel_is_created_with_catalog_DisplayName_and_recorded_as_CrawlerManaged()
    {
        var (dbPath, resolver) = await NewCatalogAsync();
        var channel = await resolver.CreateCanonicalChannelAsync(
            "x", "Nome Canónico", EditorialCategory.Live, CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy.CreateEligible, true, new List<string>(), country: "pt");

        var handler = new CapturingHandler();
        var svc = BuildSvc(handler, resolver);
        var state = new DispatcharrState(
            Array.Empty<DispatcharrChannel>(), Array.Empty<DispatcharrStream>(),
            Array.Empty<DispatcharrChannelGroup>(), "0.30.0");

        await svc.BeginChannelApplyAsync(
            NewChannelDecision("Nome Canónico", "x", channel.Id),
            state,
            new Dictionary<string, long>(),
            CancellationToken.None);

        Assert.Equal("Nome Canónico", handler.CreatedChannelName);

        await using var ctx = new TestDbContextFactory(dbPath).CreateDbContext();
        var ownership = await ctx.DispatcharrChannelOwnerships.SingleAsync(o => o.DispatcharrChannelId == 777);
        Assert.Equal(ChannelOwnership.CrawlerManaged, ownership.Ownership);
    }

    [Fact]
    public async Task CrawlerManaged_channel_is_renamed_to_current_canonical_name()
    {
        var (dbPath, resolver) = await NewCatalogAsync();
        var channel = await resolver.CreateCanonicalChannelAsync(
            "x", "Nome Canónico", EditorialCategory.Live, CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy.CreateEligible, true, new List<string>(), country: "pt");
        await resolver.EnsureChannelOwnershipAsync(
            dispatcharrChannelId: 100,
            evidence: "created-by-crawler",
            canonicalChannelId: channel.Id,
            cancellationToken: CancellationToken.None,
            ownership: ChannelOwnership.CrawlerManaged);

        var handler = new CapturingHandler();
        var svc = BuildSvc(handler, resolver);
        var state = new DispatcharrState(
            new[] { new DispatcharrChannel(100, "Nome Antigo", "G", 1, null, new long[] { 5 }) },
            Array.Empty<DispatcharrStream>(),
            Array.Empty<DispatcharrChannelGroup>(), "0.30.0");

        var decision = new ChannelDecision
        {
            Identity = "x",
            CanonicalName = "Nome Canónico",
            CanonicalChannelKey = "x",
            CanonicalChannelId = channel.Id,
            Outcome = SyncOutcome.ExistingReassigned,
            ExistingChannelId = 100,
            MatchReason = "exact",
            MatchScore = 100,
            Streams = new[]
            {
                new StreamMatchDecision
                {
                    Provider = "p", StreamUrl = "https://provider.example/x", StreamName = "X",
                    Outcome = SyncOutcome.ExistingUnchanged, ExistingStreamId = 5,
                    ProposedOrder = 0, OrderReason = "keep", IsWorking = true,
                },
            },
            AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
        };

        var ownershipMap = new Dictionary<long, ChannelOwnership> { [100] = ChannelOwnership.CrawlerManaged };
        await svc.BeginChannelApplyAsync(decision, state, new Dictionary<string, long>(), CancellationToken.None, ownershipMap);

        Assert.Contains("Nome Canónico", handler.RenamedChannelNames);
        Assert.DoesNotContain("Nome Antigo", handler.RenamedChannelNames);
    }

    [Fact]
    public async Task Unknown_ownership_channel_is_not_renamed()
    {
        var handler = new CapturingHandler();
        var svc = BuildSvc(handler, catalog: null);
        var state = new DispatcharrState(
            new[] { new DispatcharrChannel(100, "Nome Antigo", "G", 1, null, new long[] { 5 }) },
            Array.Empty<DispatcharrStream>(),
            Array.Empty<DispatcharrChannelGroup>(), "0.30.0");

        var decision = new ChannelDecision
        {
            Identity = "x",
            CanonicalName = "Nome Canónico",
            CanonicalChannelKey = "x",
            CanonicalChannelId = null,
            Outcome = SyncOutcome.ExistingReassigned,
            ExistingChannelId = 100,
            MatchReason = "exact",
            MatchScore = 100,
            Streams = new[]
            {
                new StreamMatchDecision
                {
                    Provider = "p", StreamUrl = "https://provider.example/x", StreamName = "X",
                    Outcome = SyncOutcome.ExistingUnchanged, ExistingStreamId = 5,
                    ProposedOrder = 0, OrderReason = "keep", IsWorking = true,
                },
            },
            AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
        };

        var ownershipMap = new Dictionary<long, ChannelOwnership> { [100] = ChannelOwnership.Unknown };
        await svc.BeginChannelApplyAsync(decision, state, new Dictionary<string, long>(), CancellationToken.None, ownershipMap);

        Assert.Empty(handler.RenamedChannelNames);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? CreatedChannelName { get; private set; }
        public List<string> RenamedChannelNames { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Post && path.EndsWith("/api/channels/streams/"))
            {
                return Json(new { id = 11L, name = "X", url = "https://provider.example/x", is_custom = true });
            }
            if (req.Method == HttpMethod.Post && path.EndsWith("/api/channels/channels/"))
            {
                var body = req.Content == null ? "" : await req.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                CreatedChannelName = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
                return Json(new { id = 777L, name = CreatedChannelName, streams = new long[] { 11 } });
            }
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/channels/channels/100/streams/"))
            {
                return Json(new object[] { new { id = 5L, name = "X", url = "https://provider.example/x" } });
            }
            if (req.Method == HttpMethod.Patch && path.EndsWith("/api/channels/channels/100/"))
            {
                var body = req.Content == null ? "" : await req.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                {
                    RenamedChannelNames.Add(n.GetString()!);
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
    }
}
