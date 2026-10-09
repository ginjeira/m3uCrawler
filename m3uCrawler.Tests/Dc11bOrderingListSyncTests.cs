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
using m3uCrawler.Services.LiveRun;
using m3uCrawler.Services.Sync;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Opção A (revisão do DC-11b / DC-D3/DC-D4, 2026-10-09) — o sync do
/// Dispatcharr lê <c>playlist.m3u</c> como fonte de <b>membros e streams</b>
/// (URLs reais + política de selecção). A Ordering List serve <b>apenas para
/// ordem</b>: os canais <b>novos</b> listados recebem
/// <c>channel_number</c> = rank 1-based (por <c>Position</c>, normalizado);
/// canais na playlist fora da lista são numerados a seguir (N+1, N+2, ...);
/// canais existentes nunca são renumerados.
///
/// <list type="bullet">
///   <item>com exactamente uma Ordering List activa, aplica-se a numeração;</item>
///   <item>só os itens activos definem ordem; itens desactivados não definem
///         ordem e os seus canais são numerados no fim, com os não-listados;</item>
///   <item>com zero ou várias listas activas, não há numeração (ambíguo);</item>
///   <item>a lista NÃO define membros: canais listados mas ausentes da
///         playlist não são criados;</item>
///   <item>canais <c>CrawlerManaged</c> fora da playlist não são removidos;</item>
///   <item>sem catálogo o comportamento é intacto
///         (<c>CatalogUnavailable</c> / legado).</item>
/// </list>
///
/// SQLite isolado por teste; nenhuma chamada de rede real (transporte HTTP
/// falso).
/// </summary>
public class Dc11bOrderingListSyncTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _outputDir;

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;

    public Dc11bOrderingListSyncTests()
    {
        _root = TestTempDb.SuitePath($"channel-catalog-tests-dc11b-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _outputDir = Path.Combine(_root, "output");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_outputDir);
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        await using (var ctx = await bootstrapper.InitializeAsync())
        {
        }
        _resolver = new CatalogResolver(_factory, _dbPath);
    }

    public Task DisposeAsync()
    {
        TestTempDb.CleanupDirectory(_root);
        return Task.CompletedTask;
    }

    // ────────────────────────────────────────────────────────────────────
    // 1. O sync lê playlist.m3u: várias streams por canal (URLs reais)
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_reads_playlist_and_creates_channel_with_all_streams()
    {
        await CreateCanonicalAsync("dc11b-multi", "DC11b Multi");
        await WritePlaylistAsync(
            ("News", "DC11b Multi", "http://dc11b.example/multi-1.ts"),
            ("News", "DC11b Multi", "http://dc11b.example/multi-2.ts"),
            ("News", "DC11b Multi", "http://dc11b.example/multi-3.ts"));

        var handler = new SyncHandler();
        var outcome = await NewCoordinator(handler).RunAsync(
            PlaylistPath(), _outputDir, _resolver, selection: null, liveRunProgress: null);

        Assert.Equal(DispatcharrSyncStatus.Succeeded, outcome.Status);

        // Um único canal criado (membros vêm da playlist), com 3 streams.
        var body = Assert.Single(handler.PostedChannelBodies);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("DC11b Multi", root.GetProperty("name").GetString());
        Assert.Equal(3, root.GetProperty("streams").GetArrayLength());

        // As URLs publicadas são as REAIS da playlist (nunca sanitizadas).
        var postedUrls = ExtractPostedStreamUrls(handler.PostedStreamBodies);
        Assert.Equal(3, postedUrls.Count);
        Assert.Contains("http://dc11b.example/multi-1.ts", postedUrls);
        Assert.Contains("http://dc11b.example/multi-2.ts", postedUrls);
        Assert.Contains("http://dc11b.example/multi-3.ts", postedUrls);
    }

    [Fact]
    public async Task Selection_artifact_limits_streams_per_channel()
    {
        var multiId = await CreateCanonicalAsync("dc11b-multi", "DC11b Multi");
        await WritePlaylistAsync(
            ("News", "DC11b Multi", "http://dc11b.example/multi-1.ts"),
            ("News", "DC11b Multi", "http://dc11b.example/multi-2.ts"),
            ("News", "DC11b Multi", "http://dc11b.example/multi-3.ts"));

        // Política/selecção escolhe apenas 2 das 3 fontes.
        var selection = new DispatcharrSourceSelection
        {
            Applied = true,
            Channels = new List<ChannelSourceSelection>
            {
                new()
                {
                    CanonicalChannelKey = "dc11b-multi",
                    CanonicalChannelId = multiId,
                    Selected = new List<SelectedStreamSelection>
                    {
                        new() { StreamUrl = "http://dc11b.example/multi-1.ts", Rank = 1 },
                        new() { StreamUrl = "http://dc11b.example/multi-2.ts", Rank = 2 },
                    },
                },
            },
        };

        var handler = new SyncHandler();
        var outcome = await NewCoordinator(handler).RunAsync(
            PlaylistPath(), _outputDir, _resolver, selection, liveRunProgress: null);

        Assert.Equal(DispatcharrSyncStatus.Succeeded, outcome.Status);

        var body = Assert.Single(handler.PostedChannelBodies);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(2, doc.RootElement.GetProperty("streams").GetArrayLength());

        var postedUrls = ExtractPostedStreamUrls(handler.PostedStreamBodies);
        Assert.Equal(2, postedUrls.Count);
        Assert.DoesNotContain("http://dc11b.example/multi-3.ts", postedUrls);
    }

    // ────────────────────────────────────────────────────────────────────
    // 2. Lista única activa → canal_number 1..N + fora da lista N+1..
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Single_active_ordering_list_numbers_listed_channels_and_appends_unlisted()
    {
        var alphaId = await CreateCanonicalAsync("dc11b-alpha", "DC11b Alpha");
        var betaId = await CreateCanonicalAsync("dc11b-beta", "DC11b Beta");
        await CreateCanonicalAsync("dc11b-extra-a", "DC11b Extra A");
        await CreateCanonicalAsync("dc11b-extra-b", "DC11b Extra B");

        // Ordem invertida face à playlist, com buracos nas posições
        // (beta=3, alpha=8) → ranks normalizados beta=1, alpha=2.
        var list = await _resolver.CreateOrderingListAsync("dc11b-pt", "DC11b PT", "pt", null);
        await _resolver.AddOrderingItemAsync(list.Id, betaId, position: 3);
        await _resolver.AddOrderingItemAsync(list.Id, alphaId, position: 8);

        // Playlist: os 4 canais, incluindo dois fora da lista (Extra A/B).
        await WritePlaylistAsync(
            ("News", "DC11b Alpha", "http://dc11b.example/alpha.ts"),
            ("News", "DC11b Beta", "http://dc11b.example/beta.ts"),
            ("News", "DC11b Extra A", "http://dc11b.example/extra-a.ts"),
            ("News", "DC11b Extra B", "http://dc11b.example/extra-b.ts"));

        var handler = new SyncHandler();
        var outcome = await NewCoordinator(handler).RunAsync(
            PlaylistPath(), _outputDir, _resolver, selection: null, liveRunProgress: null);

        Assert.Equal(DispatcharrSyncStatus.Succeeded, outcome.Status);
        Assert.NotNull(outcome.Report);

        var created = ExtractCreatedChannels(handler.PostedChannelBodies);
        Assert.Equal(4, created.Count);

        // Listados: ranks 1..N (1-based) pela ordem da lista.
        Assert.Equal(1d, created["DC11b Beta"]);
        Assert.Equal(2d, created["DC11b Alpha"]);
        // Fora da lista: N+1, N+2 pela ordem determinística do plano.
        Assert.Equal(3d, created["DC11b Extra A"]);
        Assert.Equal(4d, created["DC11b Extra B"]);

        // O próprio plano reflecte a numeração.
        var plan = outcome.Report!.Plan.Channels.ToDictionary(c => c.CanonicalName, c => c.ProposedChannelNumber);
        Assert.Equal(2d, plan["DC11b Alpha"]);
        Assert.Equal(1d, plan["DC11b Beta"]);
        Assert.Equal(3d, plan["DC11b Extra A"]);
        Assert.Equal(4d, plan["DC11b Extra B"]);

        // E o JSON do plano gravado em disco também expõe a numeração.
        Assert.NotNull(outcome.Report!.PlanPath);
        using var planDoc = JsonDocument.Parse(await File.ReadAllTextAsync(outcome.Report!.PlanPath!));
        var planChannels = planDoc.RootElement.GetProperty("channels").EnumerateArray()
            .ToDictionary(
                c => c.GetProperty("canonicalName").GetString()!,
                c => c.GetProperty("proposedChannelNumber").GetDouble());
        Assert.Equal(1d, planChannels["DC11b Beta"]);
        Assert.Equal(3d, planChannels["DC11b Extra A"]);
    }

    [Fact]
    public async Task Single_active_ordering_list_does_not_renumber_existing_channels()
    {
        var alphaId = await CreateCanonicalAsync("dc11b-alpha", "DC11b Alpha");
        var betaId = await CreateCanonicalAsync("dc11b-beta", "DC11b Beta");

        var list = await _resolver.CreateOrderingListAsync("dc11b-pt", "DC11b PT", "pt", null);
        await _resolver.AddOrderingItemAsync(list.Id, alphaId, position: 0);
        await _resolver.AddOrderingItemAsync(list.Id, betaId, position: 1);

        // Alpha já existe no Dispatcharr (channel_number legado 5) com a
        // MESMA stream. Beta é novo.
        var handler = new SyncHandler
        {
            Channels = new List<DispatcharrChannel>
            {
                new(1, "DC11b Alpha", null, 5, null, new long[] { 7 }),
            },
            Streams = new List<DispatcharrStream>
            {
                new(7, "DC11b Alpha", "http://dc11b.example/alpha.ts",
                    null, null, "external", true, true, null),
            },
        };

        await WritePlaylistAsync(
            ("News", "DC11b Alpha", "http://dc11b.example/alpha.ts"),
            ("News", "DC11b Beta", "http://dc11b.example/beta.ts"));

        var outcome = await NewCoordinator(handler).RunAsync(
            PlaylistPath(), _outputDir, _resolver, selection: null, liveRunProgress: null);

        Assert.Equal(DispatcharrSyncStatus.Succeeded, outcome.Status);

        // Apenas Beta é criado; Alpha (existente) não é renumerado nem
        // recriado. O rank de Beta é 2 (posição na lista, 1-based).
        var created = ExtractCreatedChannels(handler.PostedChannelBodies);
        Assert.Single(created);
        Assert.Equal(2d, created["DC11b Beta"]);

        // Nenhum PATCH renumera o canal existente.
        Assert.DoesNotContain(handler.Writes,
            w => w.StartsWith("PATCH", StringComparison.Ordinal)
              && w.Contains("channel_number", StringComparison.Ordinal));
    }

    // ────────────────────────────────────────────────────────────────────
    // 3. Zero / várias listas activas → sem numeração
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Zero_active_ordering_lists_leaves_channels_unnumbered()
    {
        await CreateCanonicalAsync("dc11b-legacy", "PT: DC11b Legacy");
        await WriteLegacyPlaylistAsync();

        var handler = new SyncHandler();
        var outcome = await NewCoordinator(handler).RunAsync(
            PlaylistPath(), _outputDir, _resolver, selection: null, liveRunProgress: null);

        Assert.Equal(DispatcharrSyncStatus.Succeeded, outcome.Status);
        var created = ExtractCreatedChannels(handler.PostedChannelBodies);
        Assert.True(created.ContainsKey("PT: DC11b Legacy"));
        Assert.Null(created["PT: DC11b Legacy"]);
    }

    [Fact]
    public async Task Multiple_active_ordering_lists_leave_channels_unnumbered_and_record_reason()
    {
        await CreateCanonicalAsync("dc11b-legacy", "PT: DC11b Legacy");
        // Country nulo → duas listas coexistem (a unicidade por país da
        // DC-D4 só se aplica com Country não nulo).
        await _resolver.CreateOrderingListAsync("dc11b-a", "DC11b A", null, null);
        await _resolver.CreateOrderingListAsync("dc11b-b", "DC11b B", null, null);
        await WriteLegacyPlaylistAsync();

        var handler = new SyncHandler();
        var progress = new RecordingProgress();
        var outcome = await NewCoordinator(handler).RunAsync(
            PlaylistPath(), _outputDir, _resolver, selection: null, liveRunProgress: progress);

        Assert.Equal(DispatcharrSyncStatus.Succeeded, outcome.Status);
        var created = ExtractCreatedChannels(handler.PostedChannelBodies);
        Assert.True(created.ContainsKey("PT: DC11b Legacy"));
        Assert.Null(created["PT: DC11b Legacy"]);
        Assert.Contains(progress.Activities,
            a => a.Contains("Ordering Lists activas", StringComparison.Ordinal));
    }

    // ────────────────────────────────────────────────────────────────────
    // 3b. Item desactivado não define ordem — numerado no fim (com os não-listados)
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disabled_ordering_item_does_not_define_order_and_is_numbered_last()
    {
        var alphaId = await CreateCanonicalAsync("dc11b-alpha", "DC11b Alpha");
        var betaId = await CreateCanonicalAsync("dc11b-beta", "DC11b Beta");
        var gammaId = await CreateCanonicalAsync("dc11b-gamma", "DC11b Gamma");
        await CreateCanonicalAsync("dc11b-extra", "DC11b Extra");

        var list = await _resolver.CreateOrderingListAsync("dc11b-pt", "DC11b PT", "pt", null);
        await _resolver.AddOrderingItemAsync(list.Id, alphaId, position: 0);
        // Item desactivado: em opção A a pertença vem da playlist, logo o canal
        // continua a ser criado — mas o item NÃO define ordem (não ocupa rank).
        await _resolver.AddOrderingItemAsync(list.Id, betaId, position: 1, isEnabled: false);
        await _resolver.AddOrderingItemAsync(list.Id, gammaId, position: 2);

        await WritePlaylistAsync(
            ("News", "DC11b Alpha", "http://dc11b.example/alpha.ts"),
            ("News", "DC11b Beta", "http://dc11b.example/beta.ts"),
            ("News", "DC11b Gamma", "http://dc11b.example/gamma.ts"),
            ("News", "DC11b Extra", "http://dc11b.example/extra.ts"));

        var handler = new SyncHandler();
        var outcome = await NewCoordinator(handler).RunAsync(
            PlaylistPath(), _outputDir, _resolver, selection: null, liveRunProgress: null);

        Assert.Equal(DispatcharrSyncStatus.Succeeded, outcome.Status);
        var created = ExtractCreatedChannels(handler.PostedChannelBodies);
        Assert.Equal(4, created.Count);

        // Activos ordenam: Alpha=1, Gamma=2 (o desactivado foi ignorado).
        Assert.Equal(1d, created["DC11b Alpha"]);
        Assert.Equal(2d, created["DC11b Gamma"]);
        // O canal do item desactivado é criado (membros vêm da playlist), mas
        // é numerado no FIM, junto com o não-listado (N+1, N+2, ...).
        Assert.Equal(3d, created["DC11b Beta"]);
        Assert.Equal(4d, created["DC11b Extra"]);
    }

    // ────────────────────────────────────────────────────────────────────
    // 4. Regressão — a lista NÃO define membros (sem composição)
    // ────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Channel_listed_but_absent_from_playlist_is_not_created()
    {
        var listedId = await CreateCanonicalAsync("dc11b-listed", "DC11b Listed");
        await CreateCanonicalAsync("dc11b-legacy", "PT: DC11b Legacy");

        var list = await _resolver.CreateOrderingListAsync("dc11b-pt", "DC11b PT", "pt", null);
        await _resolver.AddOrderingItemAsync(list.Id, listedId, position: 0);

        // Playlist só contém o canal legado; o canal listado não consta.
        await WriteLegacyPlaylistAsync();

        var handler = new SyncHandler();
        var outcome = await NewCoordinator(handler).RunAsync(
            PlaylistPath(), _outputDir, _resolver, selection: null, liveRunProgress: null);

        Assert.Equal(DispatcharrSyncStatus.Succeeded, outcome.Status);
        var created = ExtractCreatedChannels(handler.PostedChannelBodies);
        Assert.Single(created);
        Assert.True(created.ContainsKey("PT: DC11b Legacy"));
        Assert.DoesNotContain("DC11b Listed", created.Keys);
    }

    [Fact]
    public async Task Existing_crawler_managed_channel_outside_playlist_is_not_removed()
    {
        await CreateCanonicalAsync("dc11b-legacy", "PT: DC11b Legacy");

        // Canal CrawlerManaged existente que não consta da playlist.
        var handler = new SyncHandler
        {
            Channels = new List<DispatcharrChannel>
            {
                new(2, "DC11b Unrelated", null, 9, null, new long[] { 8 }),
            },
            Streams = new List<DispatcharrStream>
            {
                new(8, "DC11b Unrelated", "http://dc11b.example/unrelated.ts",
                    null, null, "external", true, true, null),
            },
        };
        await _resolver.EnsureStreamOwnershipAsync(8, 2, StreamOwnership.CrawlerManaged, null);

        await WriteLegacyPlaylistAsync();

        var outcome = await NewCoordinator(handler).RunAsync(
            PlaylistPath(), _outputDir, _resolver, selection: null, liveRunProgress: null);

        Assert.Equal(DispatcharrSyncStatus.Succeeded, outcome.Status);

        // Nenhum DELETE e nenhuma escrita sobre o canal/stream fora da playlist.
        Assert.DoesNotContain(handler.Writes,
            w => w.StartsWith("DELETE", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Writes,
            w => w.Contains("/api/channels/channels/2/", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Writes,
            w => w.Contains("/api/channels/streams/8/", StringComparison.Ordinal));
    }

    // ────────────────────────────────────────────────────────────────────
    // 5. Sem catálogo → CatalogUnavailable intacto
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Without_catalog_and_without_factory_remains_catalog_unavailable()
    {
        var handler = new SyncHandler();
        var coordinator = new DispatcharrSyncCoordinator(
            configLoader: EnabledConfig,
            catalogFactory: null,
            transport: handler);

        var outcome = await coordinator.RunAsync(
            PlaylistPath(), _outputDir, catalog: null, selection: null, liveRunProgress: null);

        Assert.Equal(DispatcharrSyncStatus.CatalogUnavailable, outcome.Status);
        Assert.DoesNotContain(handler.Writes, w => !w.StartsWith("GET", StringComparison.Ordinal));
    }

    // ────────────────────────────────────────────────────────────────────
    // Helpers de cenário
    // ────────────────────────────────────────────────────────────────────

    private string PlaylistPath() => Path.Combine(_outputDir, "playlist.m3u");

    private async Task WriteLegacyPlaylistAsync() =>
        await WritePlaylistAsync(("News", "PT: DC11b Legacy", "http://dc11b.example/legacy.ts"));

    private async Task WritePlaylistAsync(params (string Group, string Title, string Url)[] entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        foreach (var entry in entries)
        {
            sb.AppendLine($"#EXTINF:-1 group-title=\"{entry.Group}\",{entry.Title}");
            sb.AppendLine(entry.Url);
        }
        await File.WriteAllTextAsync(PlaylistPath(), sb.ToString());
    }

    private async Task<long> CreateCanonicalAsync(string key, string displayName)
    {
        var channel = await _resolver.CreateCanonicalChannelAsync(
            key, displayName, EditorialCategory.Live,
            CanonicalGroupKeys.PortugalGeneralistas, PublicationPolicy.CreateEligible,
            isEnabled: true, normalizedAliases: new List<string>());
        return channel.Id;
    }

    private static DispatcharrConfig EnabledConfig() => new()
    {
        Enabled = true,
        BaseUrl = "http://dispatcharr.local",
        ApiKey = "PLACEHOLDER-API-KEY",
        DryRun = false,
        MatchThreshold = 80,
        AliasFile = null,
    };

    private DispatcharrSyncCoordinator NewCoordinator(HttpMessageHandler transport) =>
        new(configLoader: EnabledConfig, catalogFactory: null, transport: transport);

    /// <summary>
    /// Extrai (name → channel_number) dos corpos POST de criação de canal.
    /// <c>null</c> quando o payload não traz <c>channel_number</c>.
    /// </summary>
    private static Dictionary<string, double?> ExtractCreatedChannels(IEnumerable<string> bodies)
    {
        var result = new Dictionary<string, double?>(StringComparer.Ordinal);
        foreach (var body in bodies)
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var name = root.GetProperty("name").GetString()!;
            double? number = root.TryGetProperty("channel_number", out var n)
                && n.ValueKind == JsonValueKind.Number
                ? n.GetDouble()
                : null;
            result[name] = number;
        }
        return result;
    }

    private static List<string> ExtractPostedStreamUrls(IEnumerable<string> bodies)
    {
        var result = new List<string>();
        foreach (var body in bodies)
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
            {
                result.Add(url.GetString()!);
            }
        }
        return result;
    }

    private sealed class RecordingProgress : ILiveRunProgress
    {
        public List<string> Activities { get; } = new();
        public string RunId => "dc11b-test";
        public Task EnterPhaseAsync(LiveRunPhase phase, string? message = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void ReportCounts(Action<LiveRunCounts> mutate) { }
        public void ReportCounts(RunReport report) { }
        public void ReportMessage(string message) { }
        public void ReportActivity(
            LiveRunActivityCategory category, LiveRunActivityLevel level,
            string message, IReadOnlyDictionary<string, string>? metadata = null)
            => Activities.Add(message);
    }

    /// <summary>
    /// Transporte HTTP falso: universo configurável, sem rede real. Captura
    /// os corpos de criação de canal/stream e todas as escritas (método ≠ GET).
    /// </summary>
    private sealed class SyncHandler : HttpMessageHandler
    {
        public List<DispatcharrChannel> Channels { get; set; } = new();
        public List<DispatcharrStream> Streams { get; set; } = new();
        public List<DispatcharrChannelGroup> Groups { get; set; } = new();
        public List<string> Writes { get; } = new();
        public List<string> PostedChannelBodies { get; } = new();
        public List<string> PostedStreamBodies { get; } = new();

        private long _nextChannelId = 1000;
        private long _nextStreamId = 5000;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            var method = req.Method.Method.ToUpperInvariant();

            if (method == "GET")
            {
                if (path.EndsWith("/api/core/version/", StringComparison.Ordinal))
                    return Json(new { version = "0.30.0" });

                if (path.EndsWith("/api/channels/channels/", StringComparison.Ordinal))
                    return Json(new { count = Channels.Count, results = Channels.Select(ChannelJson).ToArray() });

                if (path.EndsWith("/api/channels/streams/", StringComparison.Ordinal))
                    return Json(new { count = Streams.Count, results = Streams.Select(StreamJson).ToArray() });

                if (path.EndsWith("/api/channels/groups/", StringComparison.Ordinal))
                    return Json(new { count = Groups.Count, results = Groups.Select(g => new { id = g.Id, name = g.Name }).ToArray() });

                if (path.Contains("/api/channels/channels/", StringComparison.Ordinal)
                    && path.EndsWith("/streams/", StringComparison.Ordinal))
                {
                    var id = ExtractId(path);
                    var channel = Channels.FirstOrDefault(c => c.Id == id);
                    var ids = channel?.StreamIds ?? (IReadOnlyList<long>)Array.Empty<long>();
                    return Json(ids.Select(s => new { id = s }).ToArray());
                }
            }

            var body = req.Content is null ? string.Empty : await req.Content.ReadAsStringAsync(ct);
            Writes.Add($"{method} {path} {body}".TrimEnd());

            if (method == "POST" && path.EndsWith("/api/channels/channels/", StringComparison.Ordinal))
            {
                PostedChannelBodies.Add(body);
                var id = _nextChannelId++;
                return Json(new { id, name = "c", channel_number = (double?)null, streams = Array.Empty<long>() });
            }
            if (method == "POST" && path.EndsWith("/api/channels/streams/", StringComparison.Ordinal))
            {
                PostedStreamBodies.Add(body);
                return Json(new { id = _nextStreamId++, name = "s", url = "u", is_custom = true });
            }
            if (method == "POST" && path.EndsWith("/api/channels/groups/", StringComparison.Ordinal))
                return Json(new { id = 700L, name = "g" });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }

        private static object ChannelJson(DispatcharrChannel c) => new
        {
            id = c.Id,
            name = c.Name,
            channel_group_id = (long?)null,
            channel_number = c.ChannelNumber,
            tvg_id = c.TvgId,
            streams = c.StreamIds.ToArray(),
        };

        private static object StreamJson(DispatcharrStream s) => new
        {
            id = s.Id,
            name = s.Name,
            url = s.Url,
            tvg_id = s.TvgId,
            channel_group = (long?)null,
            m3u_account = (long?)null,
            m3u_account_name = s.M3uAccountName,
            is_custom = s.IsCustom,
        };

        private static long ExtractId(string path)
        {
            const string marker = "/api/channels/channels/";
            var start = path.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            var rest = path[start..];
            var segment = rest.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "0";
            return long.TryParse(segment, out var id) ? id : 0;
        }

        private static HttpResponseMessage Json(object payload) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
    }
}
