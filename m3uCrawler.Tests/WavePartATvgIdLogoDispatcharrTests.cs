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
/// Parte A — emissão de <c>tvg_id</c>/<c>logo_url</c> no sync do
/// Dispatcharr. Cobre:
/// <list type="bullet">
///   <item>transporte de tvg-id/logo no plano (<see cref="StreamMatchDecision"/>)</item>
///   <item>resolução do <see cref="ChannelDecision.EpgTvgId"/> a partir do
///         tvg-id curado do canal canónico, com fallback para o
///         <c>OriginalTvgId</c> da fonte;</item>
///   <item>criação de canal/stream com os metadados;</item>
///   <item>PATCH de canais/streams <c>CrawlerManaged</c> existentes e
///         não-interferência em <c>External</c>;</item>
///   <item>idempotência e serialização aditiva.</item>
/// </list>
/// </summary>
public class WavePartATvgIdLogoDispatcharrTests : IDisposable
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"wave-partA-{Guid.NewGuid():N}.db");
        _dbPaths.Add(dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        return new CatalogResolver(new TestDbContextFactory(dbPath), dbPath);
    }

    private static DiscoveredStream Stream(
        string title, string group, string tvgId = "", string logo = "", bool isWorking = true)
    {
        var m3u = new M3uStream
        {
            Title = title,
            Url = $"https://provider.example/{title.Replace(' ', '_')}",
            Group = group,
            OriginalTvgId = tvgId,
            Logo = logo,
            IsWorking = isWorking,
            ResponseTime = 100,
        };
        return new DiscoveredStream(m3u, "P", "src");
    }

    private static MatchPlan BuildPlan(ChannelMatcher matcher, params DiscoveredStream[] streams)
        => matcher.BuildPlan(
            streams.ToList(),
            new DispatcharrState(
                Array.Empty<DispatcharrChannel>(),
                Array.Empty<DispatcharrStream>(),
                Array.Empty<DispatcharrChannelGroup>(),
                null),
            MatchingOptions.Default,
            new StreamOrderingPolicy(),
            "test.m3u",
            "http://x",
            dryRun: true);

    // =====================================================================
    // 1. Plano — transporte de tvgId/logoUrl e EpgTvgId
    // =====================================================================

    [Fact]
    public async Task Plan_transports_stream_tvg_and_logo_and_falls_back_to_source_tvg_for_channel()
    {
        var resolver = await NewResolverAsync();
        await resolver.CreateCanonicalChannelAsync(
            "parta-unique-tvg", "PartA Unique TV", EditorialCategory.Live,
            CanonicalGroupKeys.PortugalGeneralistas, PublicationPolicy.CreateEligible,
            isEnabled: true, normalizedAliases: Array.Empty<string>(), country: "pt");

        var matcher = new ChannelMatcher(new AliasResolver(), resolutionPolicy: null, catalog: resolver);
        var plan = BuildPlan(matcher,
            Stream("PartA Unique TV", "Portugal", tvgId: "src.tvg", logo: "https://logo.example/parta.png"));

        var channel = Assert.Single(plan.Channels);
        Assert.Equal(SyncOutcome.NewChannel, channel.Outcome);
        // Sem identidade curada, o EpgTvgId cai para o OriginalTvgId da fonte.
        Assert.Equal("src.tvg", channel.EpgTvgId);

        var stream = Assert.Single(channel.Streams);
        Assert.Equal("src.tvg", stream.TvgId);
        Assert.Equal("https://logo.example/parta.png", stream.LogoUrl);
    }

    [Fact]
    public async Task Plan_resolves_channel_epg_tvg_from_curated_canonical_identity()
    {
        var resolver = await NewResolverAsync();
        var channel = await resolver.CreateCanonicalChannelAsync(
            "parta-unique-tvg", "PartA Unique TV", EditorialCategory.Live,
            CanonicalGroupKeys.PortugalGeneralistas, PublicationPolicy.CreateEligible,
            isEnabled: true, normalizedAliases: Array.Empty<string>(), country: "pt");
        await resolver.RecordExternalIdentityAsync(
            channel.Id, providerId: null,
            @namespace: ExternalIdentityNamespaces.TvgId,
            rawValue: "parta.epg.unique", origin: "operator", confidence: 1.0);

        var matcher = new ChannelMatcher(new AliasResolver(), resolutionPolicy: null, catalog: resolver);
        var plan = BuildPlan(matcher,
            Stream("PartA Unique TV", "Portugal", tvgId: "src.tvg", logo: "https://logo.example/parta.png"));

        var decision = Assert.Single(plan.Channels);
        // O tvg-id curado (normalizado) vence o OriginalTvgId da fonte.
        Assert.Equal("parta.epg.unique", decision.EpgTvgId);
        var stream = Assert.Single(decision.Streams);
        Assert.Equal("src.tvg", stream.TvgId);
    }

    [Fact]
    public void Plan_without_catalog_falls_back_to_source_tvg_for_channel()
    {
        var matcher = new ChannelMatcher(new AliasResolver());
        var plan = BuildPlan(matcher,
            Stream("SIC", "Portugal", tvgId: "src.tvg", logo: "https://logo.example/parta.png"));

        var decision = Assert.Single(plan.Channels);
        // Sem catálogo, não há identidade curada: fallback para o OriginalTvgId.
        Assert.Equal("src.tvg", decision.EpgTvgId);
        var stream = Assert.Single(decision.Streams);
        Assert.Equal("src.tvg", stream.TvgId);
        Assert.Equal("https://logo.example/parta.png", stream.LogoUrl);
    }

    // =====================================================================
    // 2. Sync — criação com tvg_id / tvg_id+logo_url
    // =====================================================================

    [Fact]
    public async Task Create_channel_and_stream_send_tvg_id_and_logo_url()
    {
        var resolver = await NewResolverAsync();
        var handler = new RecordingHandler();
        var svc = BuildSvc(handler, resolver);

        var decision = new ChannelDecision
        {
            Identity = "rtp1",
            CanonicalName = "RTP1",
            CanonicalChannelKey = "rtp1",
            Outcome = SyncOutcome.NewChannel,
            ExistingChannelId = null,
            EpgTvgId = "epg.rtp1",
            MatchReason = "no-match",
            Streams = new[]
            {
                new StreamMatchDecision
                {
                    Provider = "p",
                    StreamUrl = "https://provider.example/rtp1",
                    StreamName = "RTP1",
                    Outcome = SyncOutcome.NewStream,
                    ProposedOrder = 0,
                    OrderReason = "new-channel-initial",
                    IsWorking = true,
                    TvgId = "src.tvg",
                    LogoUrl = "https://logo.example/rtp1.png",
                },
            },
        };

        var state = new DispatcharrState(
            Array.Empty<DispatcharrChannel>(), Array.Empty<DispatcharrStream>(),
            Array.Empty<DispatcharrChannelGroup>(), "0.30.0");

        await svc.BeginChannelApplyAsync(decision, state, new Dictionary<string, long>(), CancellationToken.None);

        Assert.NotNull(handler.CreatedChannelBody);
        Assert.Equal("epg.rtp1", handler.CreatedChannelBody!.Value.GetProperty("tvg_id").GetString());

        Assert.NotNull(handler.CreatedStreamBody);
        Assert.Equal("src.tvg", handler.CreatedStreamBody!.Value.GetProperty("tvg_id").GetString());
        Assert.Equal("https://logo.example/rtp1.png",
            handler.CreatedStreamBody.Value.GetProperty("logo_url").GetString());
    }

    // =====================================================================
    // 3. Sync — PATCH de canais/streams CrawlerManaged existentes
    // =====================================================================

    [Fact]
    public async Task Existing_crawler_managed_channel_and_stream_are_patched_when_metadata_differs()
    {
        var handler = new RecordingHandler();
        var svc = BuildSvc(handler, catalog: null);

        var decision = ExistingDecision(
            epgTvgId: "epg.new", streamTvgId: "new.tvg", streamLogo: "https://logo.example/new.png");
        var state = new DispatcharrState(
            new[] { new DispatcharrChannel(100, "RTP1", null, 1, "old.tvg", new long[] { 5 }) },
            new[]
            {
                new DispatcharrStream(5, "RTP1", "https://provider.example/rtp1", "old.tvg", null, null, true, true, null)
                {
                    LogoUrl = "https://logo.example/old.png",
                },
            },
            Array.Empty<DispatcharrChannelGroup>(), "0.30.0");

        var channelOwnership = new Dictionary<long, ChannelOwnership> { [100] = ChannelOwnership.CrawlerManaged };
        var streamOwnership = new Dictionary<long, StreamOwnership> { [5] = StreamOwnership.CrawlerManaged };

        await svc.BeginChannelApplyAsync(
            decision, state, new Dictionary<string, long>(), CancellationToken.None,
            channelOwnershipById: channelOwnership,
            streamOwnershipById: streamOwnership);

        var channelPatch = Assert.Single(handler.ChannelPatchBodies);
        Assert.Equal("epg.new", channelPatch.GetProperty("tvg_id").GetString());

        Assert.Equal(2, handler.StreamPatchBodies.Count);
        var tvgPatch = handler.StreamPatchBodies.Single(b => b.TryGetProperty("tvg_id", out _));
        Assert.Equal("new.tvg", tvgPatch.GetProperty("tvg_id").GetString());
        var logoPatch = handler.StreamPatchBodies.Single(b => b.TryGetProperty("logo_url", out _));
        Assert.Equal("https://logo.example/new.png", logoPatch.GetProperty("logo_url").GetString());
    }

    [Fact]
    public async Task External_channel_and_stream_are_never_patched()
    {
        var handler = new RecordingHandler();
        // Catalog activo: a ownership das streams é consultada no mapa;
        // External nunca é tocado.
        var svc = BuildSvc(handler, await NewResolverAsync());

        var decision = ExistingDecision(
            epgTvgId: "epg.new", streamTvgId: "new.tvg", streamLogo: "https://logo.example/new.png");
        var state = new DispatcharrState(
            new[] { new DispatcharrChannel(100, "RTP1", null, 1, "old.tvg", new long[] { 5 }) },
            new[]
            {
                new DispatcharrStream(5, "RTP1", "https://provider.example/rtp1", "old.tvg", null, null, false, true, null)
                {
                    LogoUrl = "https://logo.example/old.png",
                },
            },
            Array.Empty<DispatcharrChannelGroup>(), "0.30.0");

        var channelOwnership = new Dictionary<long, ChannelOwnership> { [100] = ChannelOwnership.External };
        var streamOwnership = new Dictionary<long, StreamOwnership> { [5] = StreamOwnership.External };

        await svc.BeginChannelApplyAsync(
            decision, state, new Dictionary<string, long>(), CancellationToken.None,
            channelOwnershipById: channelOwnership,
            streamOwnershipById: streamOwnership);

        Assert.Empty(handler.ChannelPatchBodies);
        Assert.Empty(handler.StreamPatchBodies);
    }

    [Fact]
    public async Task Existing_channel_and_stream_patch_is_idempotent_when_metadata_equal()
    {
        var handler = new RecordingHandler();
        var svc = BuildSvc(handler, catalog: null);

        var decision = ExistingDecision(
            epgTvgId: "epg.same", streamTvgId: "same.tvg", streamLogo: "https://logo.example/same.png");
        var state = new DispatcharrState(
            new[] { new DispatcharrChannel(100, "RTP1", null, 1, "epg.same", new long[] { 5 }) },
            new[]
            {
                new DispatcharrStream(5, "RTP1", "https://provider.example/rtp1", "same.tvg", null, null, true, true, null)
                {
                    LogoUrl = "https://logo.example/same.png",
                },
            },
            Array.Empty<DispatcharrChannelGroup>(), "0.30.0");

        var channelOwnership = new Dictionary<long, ChannelOwnership> { [100] = ChannelOwnership.CrawlerManaged };
        var streamOwnership = new Dictionary<long, StreamOwnership> { [5] = StreamOwnership.CrawlerManaged };

        await svc.BeginChannelApplyAsync(
            decision, state, new Dictionary<string, long>(), CancellationToken.None,
            channelOwnershipById: channelOwnership,
            streamOwnershipById: streamOwnership);

        Assert.Empty(handler.ChannelPatchBodies);
        Assert.Empty(handler.StreamPatchBodies);
    }

    // =====================================================================
    // 4. Serialização aditiva (MatchPlanSerializer)
    // =====================================================================

    [Fact]
    public void Serialized_plan_roundtrips_tvg_and_logo_fields()
    {
        var plan = new MatchPlan
        {
            GeneratedAtUtc = "2026-10-10T00:00:00Z",
            SourcePlaylistPath = "x.m3u",
            DispatcharrBaseUrl = "http://dispatcharr.local",
            Channels = new[]
            {
                new ChannelDecision
                {
                    Identity = "rtp1",
                    CanonicalName = "RTP1",
                    Outcome = SyncOutcome.NewChannel,
                    EpgTvgId = "epg.rtp1",
                    Streams = new[]
                    {
                        new StreamMatchDecision
                        {
                            StreamUrl = "https://provider.example/rtp1",
                            StreamName = "RTP1",
                            Outcome = SyncOutcome.NewStream,
                            IsWorking = true,
                            TvgId = "src.tvg",
                            LogoUrl = "https://logo.example/rtp1.png",
                        },
                    },
                },
            },
        };

        var json = MatchPlanSerializer.Serialize(plan);
        var roundtrip = MatchPlanSerializer.Deserialize(json);

        Assert.NotNull(roundtrip);
        var channel = Assert.Single(roundtrip!.Channels);
        Assert.Equal("epg.rtp1", channel.EpgTvgId);
        var stream = Assert.Single(channel.Streams);
        Assert.Equal("src.tvg", stream.TvgId);
        Assert.Equal("https://logo.example/rtp1.png", stream.LogoUrl);
    }

    [Fact]
    public void Serialized_plan_with_null_metadata_is_valid_and_additive()
    {
        var plan = new MatchPlan
        {
            GeneratedAtUtc = "2026-10-10T00:00:00Z",
            SourcePlaylistPath = "x.m3u",
            DispatcharrBaseUrl = "http://dispatcharr.local",
            Channels = new[]
            {
                new ChannelDecision
                {
                    Identity = "rtp1",
                    CanonicalName = "RTP1",
                    Outcome = SyncOutcome.NewChannel,
                    Streams = new[]
                    {
                        new StreamMatchDecision
                        {
                            StreamUrl = "https://provider.example/rtp1",
                            StreamName = "RTP1",
                            Outcome = SyncOutcome.NewStream,
                            IsWorking = true,
                        },
                    },
                },
            },
        };

        var json = MatchPlanSerializer.Serialize(plan);
        // Campos aditivos existem no JSON mas como null (não quebram consumidores).
        Assert.Contains("\"epgTvgId\"", json);
        Assert.Contains("\"tvgId\"", json);
        Assert.Contains("\"logoUrl\"", json);
        var roundtrip = MatchPlanSerializer.Deserialize(json);
        Assert.NotNull(roundtrip);
        Assert.Null(Assert.Single(roundtrip!.Channels).EpgTvgId);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static ChannelDecision ExistingDecision(string? epgTvgId, string? streamTvgId, string? streamLogo) => new()
    {
        Identity = "rtp1",
        CanonicalName = "RTP1",
        CanonicalChannelKey = "rtp1",
        Outcome = SyncOutcome.ExistingReassigned,
        ExistingChannelId = 100,
        ChannelGroupName = null,
        EpgTvgId = epgTvgId,
        MatchReason = "exact",
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
                TvgId = streamTvgId,
                LogoUrl = streamLogo,
            },
        },
    };

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
            Path.Combine(Path.GetTempPath(), $"wave-partA-out-{Guid.NewGuid():N}"),
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
        public JsonElement? CreatedChannelBody { get; private set; }
        public JsonElement? CreatedStreamBody { get; private set; }
        public List<JsonElement> ChannelPatchBodies { get; } = new();
        public List<JsonElement> StreamPatchBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Post && path.EndsWith("/api/channels/streams/"))
            {
                CreatedStreamBody = await ReadBodyAsync(req, ct);
                return Json(new { id = 900L, name = "RTP1", url = "https://provider.example/rtp1", is_custom = true });
            }
            if (req.Method == HttpMethod.Post && path.EndsWith("/api/channels/channels/"))
            {
                CreatedChannelBody = await ReadBodyAsync(req, ct);
                return Json(new { id = 777L, name = "RTP1", streams = new long[] { 900 } });
            }
            if (req.Method == HttpMethod.Patch && path.Contains("/api/channels/streams/"))
            {
                StreamPatchBodies.Add(await ReadBodyAsync(req, ct));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (req.Method == HttpMethod.Patch && path.Contains("/api/channels/channels/"))
            {
                ChannelPatchBodies.Add(await ReadBodyAsync(req, ct));
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            if (req.Method == HttpMethod.Get && path.Contains("/api/channels/channels/") && path.EndsWith("/streams/"))
            {
                return Json(new object[] { new { id = 5L, name = "RTP1", url = "https://provider.example/rtp1" } });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static async Task<JsonElement> ReadBodyAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content == null ? "{}" : await req.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }

        private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
    }
}
