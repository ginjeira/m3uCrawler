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
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Dispatcharr;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using m3uCrawler.Services.Sync;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Wave B — L7: guard de ownership no update de streams do apply.
/// <see cref="DispatcharrSyncService.BeginChannelApplyAsync"/> só pode
/// alterar a associação de streams (<c>UpdateStreamsAsync</c>) de canais
/// comprovadamente <see cref="ChannelOwnership.CrawlerManaged"/>, tal
/// como já acontece com o rename e o <c>tvg_id</c>. Canais do
/// utilizador (<see cref="ChannelOwnership.External"/>) nunca são
/// tocados.
/// </summary>
public class WaveBOwnershipApplyGuardTests : IDisposable
{
    private readonly List<string> _dbPaths = new();

    public void Dispose()
    {
        foreach (var path in _dbPaths)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
        }
    }

    private async Task<CatalogResolver> NewResolverAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"waveb-guard-{Guid.NewGuid():N}.db");
        _dbPaths.Add(dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
        await using (var ctx = await bootstrapper.InitializeAsync())
        {
        }
        return new CatalogResolver(new TestDbContextFactory(dbPath), dbPath);
    }

    [Theory]
    [InlineData(ChannelOwnership.External)]
    [InlineData(ChannelOwnership.Unknown)]
    public async Task Non_crawler_managed_channel_stream_association_is_not_updated(
        ChannelOwnership ownership)
    {
        var handler = new RecordingHandler();
        var svc = BuildSvc(handler, await NewResolverAsync());

        await svc.BeginChannelApplyAsync(
            ExistingDecision(), State(), new Dictionary<string, long>(), CancellationToken.None,
            channelOwnershipById: new Dictionary<long, ChannelOwnership>
            {
                [100] = ownership,
            });

        Assert.Empty(handler.StreamAssociationPatches);
    }

    [Fact]
    public async Task Channel_without_ownership_row_keeps_legacy_behaviour()
    {
        var handler = new RecordingHandler();
        var svc = BuildSvc(handler, await NewResolverAsync());

        // Com catálogo activo, um canal sem row de ownership (legacy: criado
        // antes da ownership persistida) mantém o comportamento histórico —
        // é actualizado. A guarda só bloqueia ownership EXPLÍCITO não-
        // CrawlerManaged (External/Unknown).
        await svc.BeginChannelApplyAsync(
            ExistingDecision(), State(), new Dictionary<string, long>(), CancellationToken.None,
            channelOwnershipById: new Dictionary<long, ChannelOwnership>());

        var patch = Assert.Single(handler.StreamAssociationPatches);
        var ids = patch.GetProperty("streams").EnumerateArray().Select(e => e.GetInt64()).ToList();
        Assert.Equal(new long[] { 5 }, ids);
    }

    [Fact]
    public async Task CrawlerManaged_channel_stream_association_is_updated()
    {
        var handler = new RecordingHandler();
        var svc = BuildSvc(handler, await NewResolverAsync());

        await svc.BeginChannelApplyAsync(
            ExistingDecision(), State(), new Dictionary<string, long>(), CancellationToken.None,
            channelOwnershipById: new Dictionary<long, ChannelOwnership>
            {
                [100] = ChannelOwnership.CrawlerManaged,
            });

        var patch = Assert.Single(handler.StreamAssociationPatches);
        var ids = patch.GetProperty("streams").EnumerateArray().Select(e => e.GetInt64()).ToList();
        Assert.Equal(new long[] { 5 }, ids);
    }

    // ──────────────────────────────── helpers ────────────────────────────────

    private static ChannelDecision ExistingDecision() => new()
    {
        Identity = "rtp1",
        CanonicalName = "RTP1",
        Outcome = SyncOutcome.ExistingReassigned,
        ExistingChannelId = 100,
        ChannelGroupName = null,
        MatchReason = "ownership",
        MatchScore = 100,
        Streams = new[]
        {
            new StreamMatchDecision
            {
                Provider = "p",
                StreamUrl = "https://provider.example/rtp1",
                StreamName = "RTP1",
                Outcome = SyncOutcome.ExistingUnchanged,
                ExistingStreamId = 5,
                ProposedOrder = 0,
                OrderReason = "keep",
                IsWorking = true,
            },
        },
    };

    private static DispatcharrState State() => new(
        new[] { new DispatcharrChannel(100, "RTP1", null, 1, null, new long[] { 7, 8 }) },
        new[]
        {
            new DispatcharrStream(5, "RTP1", "https://provider.example/rtp1", null, null, null, true, true, null),
        },
        Array.Empty<DispatcharrChannelGroup>(),
        "0.30.0");

    private static DispatcharrSyncService BuildSvc(RecordingHandler handler, CatalogResolver? catalog)
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
            Path.Combine(Path.GetTempPath(), $"waveb-guard-out-{Guid.NewGuid():N}"),
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

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<JsonElement> StreamAssociationPatches { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;

            if (req.Method == HttpMethod.Get
                && path.Contains("/api/channels/channels/")
                && path.EndsWith("/streams/"))
            {
                return Task.FromResult(Json(new object[]
                {
                    new { id = 7L, name = "RTP1", url = "https://provider.example/rtp1" },
                    new { id = 8L, name = "RTP1", url = "https://provider.example/rtp1" },
                }));
            }
            if (req.Method == HttpMethod.Patch && path.Contains("/api/channels/channels/"))
            {
                using var doc = JsonDocument.Parse(req.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult());
                var root = doc.RootElement.Clone();
                if (root.TryGetProperty("streams", out _))
                {
                    StreamAssociationPatches.Add(root);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
    }
}
