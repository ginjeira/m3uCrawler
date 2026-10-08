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
/// DC-11b (DC-D3/DC-D4) — o sync do Dispatcharr segue a Ordering List para
/// <b>membros</b> e <b>ordem</b> (não para agrupamento).
///
/// <list type="bullet">
///   <item>com exactamente uma Ordering List activa, o plano é construído a
///         partir da composição e os canais novos recebem
///         <c>channel_number</c> = posição (0-based) na lista;</item>
///   <item>itens desactivados e canais sem fonte elegível são omitidos;</item>
///   <item>canais <c>CrawlerManaged</c> existentes fora da lista NÃO são
///         removidos nem alterados (decisão A);</item>
///   <item>com zero ou várias listas activas, cai no caminho legado
///         (<c>output/playlist.m3u</c>) e regista o motivo (decisões C/D);</item>
///   <item>sem catálogo o comportamento é intacto
///         (<c>CatalogUnavailable</c> / legado).</item>
/// </list>
///
/// SQLite isolado por teste; nenhuma chamada de rede real (transporte HTTP
/// falso). A posição usada é a do índice em <c>composition.Entries</c>
/// (<c>Position</c> 0-based, consistente com o plano).
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
    // 1. Lista única activa → plano da composição + channel_number = posição
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Single_active_ordering_list_builds_plan_and_numbers_channels_by_position()
    {
        var alphaId = await CreateCanonicalAsync("dc11b-alpha", "DC11b Alpha");
        var betaId = await CreateCanonicalAsync("dc11b-beta", "DC11b Beta");
        await AddSourceAsync(alphaId, "http://dc11b.example/alpha.ts");
        await AddSourceAsync(betaId, "http://dc11b.example/beta.ts");

        // Ordem invertida face à criação: Beta é a posição 0, Alpha a 1.
        var list = await _resolver.CreateOrderingListAsync("dc11b-pt", "DC11b PT", "pt", null);
        await _resolver.AddOrderingItemAsync(list.Id, betaId, position: 0);
        await _resolver.AddOrderingItemAsync(list.Id, alphaId, position: 1);

        // Playlist crua COM um canal diferente: se fosse lida, apareceria
        // um canal "PT: DC11b Legacy" (não deve).
        await File.WriteAllTextAsync(
            Path.Combine(_outputDir, "playlist.m3u"),
            "#EXTM3U\n#EXTINF:-1 group-title=\"News\",PT: DC11b Legacy\nhttp://dc11b.example/legacy.ts\n");

        var handler = new SyncHandler();
        var outcome = await NewCoordinator(handler).RunAsync(
            PlaylistPath(), _outputDir, _resolver, selection: null, liveRunProgress: null);

        Assert.Equal(DispatcharrSyncStatus.Succeeded, outcome.Status);
        Assert.NotNull(outcome.Report);

        var created = ExtractCreatedChannels(handler.PostedChannelBodies);
        Assert.Equal(2, created.Count);

        // Ordem da lista: Beta=0, Alpha=1 (posição = índice 0-based).
        Assert.Equal(0d, created["DC11b Beta"]!.Value);
        Assert.Equal(1d, created["DC11b Alpha"]!.Value);
        Assert.DoesNotContain("PT: DC11b Legacy", created.Keys);

        // DC-11b — o próprio plano reflecte a numeração da Ordering List
        // (não só o POST de criação): proposedChannelNumber = posição.
        var beta = outcome.Report!.Plan.Channels.Single(c => c.CanonicalName == "DC11b Beta");
        var alpha = outcome.Report!.Plan.Channels.Single(c => c.CanonicalName == "DC11b Alpha");
        Assert.Equal(0d, beta.ProposedChannelNumber);
        Assert.Equal(1d, alpha.ProposedChannelNumber);

        // E o JSON do plano gravado em disco também expõe a numeração.
        Assert.NotNull(outcome.Report!.PlanPath);
        using var planDoc = JsonDocument.Parse(await File.ReadAllTextAsync(outcome.Report!.PlanPath!));
        var planChannels = planDoc.RootElement.GetProperty("channels").EnumerateArray()
            .ToDictionary(
                c => c.GetProperty("canonicalName").GetString()!,
                c => c.GetProperty("proposedChannelNumber").GetDouble());
        Assert.Equal(0d, planChannels["DC11b Beta"]);
        Assert.Equal(1d, planChannels["DC11b Alpha"]);
    }

    // ────────────────────────────────────────────────────────────────────
    // 2. Itens desactivados e canais sem fonte são omitidos (membros)
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disabled_items_and_channels_without_source_are_omitted()
    {
        var alphaId = await CreateCanonicalAsync("dc11b-keep", "DC11b Keep");
        var noSourceId = await CreateCanonicalAsync("dc11b-nosource", "DC11b NoSource");
        var disabledId = await CreateCanonicalAsync("dc11b-disabled", "DC11b Disabled");
        await AddSourceAsync(alphaId, "http://dc11b.example/keep.ts");
        // NoSource: sem channel source → omitido.
        await AddSourceAsync(disabledId, "http://dc11b.example/disabled.ts");

        var list = await _resolver.CreateOrderingListAsync("dc11b-members", "DC11b Members", null, null);
        await _resolver.AddOrderingItemAsync(list.Id, alphaId, position: 0);
        await _resolver.AddOrderingItemAsync(list.Id, noSourceId, position: 1);
        // Item desactivado com fonte elegível → ainda assim omitido.
        await _resolver.AddOrderingItemAsync(list.Id, disabledId, position: 2, isEnabled: false);

        var handler = new SyncHandler();
        var outcome = await NewCoordinator(handler).RunAsync(
            PlaylistPath(), _outputDir, _resolver, selection: null, liveRunProgress: null);

        Assert.Equal(DispatcharrSyncStatus.Succeeded, outcome.Status);
        var created = ExtractCreatedChannels(handler.PostedChannelBodies);
        Assert.Single(created);
        Assert.True(created.ContainsKey("DC11b Keep"));
    }

    // ────────────────────────────────────────────────────────────────────
    // 3. Canais CrawlerManaged existentes fora da lista não são tocados
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Existing_channels_outside_the_list_are_not_removed_or_modified()
    {
        var alphaId = await CreateCanonicalAsync("dc11b-alpha", "DC11b Alpha");
        await AddSourceAsync(alphaId, "http://dc11b.example/alpha.ts");

        var list = await _resolver.CreateOrderingListAsync("dc11b-only-alpha", "DC11b Only Alpha", null, null);
        await _resolver.AddOrderingItemAsync(list.Id, alphaId, position: 0);

        // Alpha já existe no Dispatcharr com a MESMA stream (idempotente).
        // "Unrelated" existe mas não consta da lista.
        var handler = new SyncHandler
        {
            Channels = new List<DispatcharrChannel>
            {
                new(1, "DC11b Alpha", null, 5, null, new long[] { 7 }),
                new(2, "DC11b Unrelated", null, 9, null, new long[] { 8 }),
            },
            Streams = new List<DispatcharrStream>
            {
                new(7, "DC11b Alpha", "http://dc11b.example/alpha.ts",
                    null, null, "external", false, true, null),
                new(8, "DC11b Unrelated", "http://dc11b.example/unrelated.ts",
                    null, null, "external", false, true, null),
            },
        };

        var outcome = await NewCoordinator(handler).RunAsync(
            PlaylistPath(), _outputDir, _resolver, selection: null, liveRunProgress: null);

        Assert.Equal(DispatcharrSyncStatus.Succeeded, outcome.Status);
        Assert.NotNull(outcome.Report);

        // O plano só tem o canal da lista; o "Unrelated" nunca entra.
        Assert.DoesNotContain(outcome.Report!.Plan.Channels,
            c => c.ExistingChannelId == 2);

        // Nenhum canal novo e nenhuma escrita sobre o canal fora da lista.
        Assert.Empty(handler.PostedChannelBodies);
        Assert.DoesNotContain(handler.Writes,
            w => w.Contains("/api/channels/channels/2/", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Writes,
            w => w.Contains("/api/channels/streams/8/", StringComparison.Ordinal));
    }

    // ────────────────────────────────────────────────────────────────────
    // 4. Zero / várias listas activas → fallback legado + motivo
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Zero_active_ordering_lists_falls_back_to_legacy_playlist()
    {
        // Canal canónico que casa com o nome da playlist legada, para que o
        // caminho legado (com catálogo activo) produza um canal. Se a
        // composição fosse (incorrectamente) usada, o plano ficaria vazio.
        await CreateCanonicalAsync("dc11b-legacy", "PT: DC11b Legacy");
        await WriteLegacyPlaylistAsync();

        var handler = new SyncHandler();
        var outcome = await NewCoordinator(handler).RunAsync(
            PlaylistPath(), _outputDir, _resolver, selection: null, liveRunProgress: null);

        Assert.Equal(DispatcharrSyncStatus.Succeeded, outcome.Status);
        var created = ExtractCreatedChannels(handler.PostedChannelBodies);
        Assert.True(created.ContainsKey("PT: DC11b Legacy"));
        // Sem composição, não há número de canal (comportamento legado).
        Assert.False(created.TryGetValue("PT: DC11b Legacy", out var number) && number.HasValue);
    }

    [Fact]
    public async Task Multiple_active_ordering_lists_fall_back_to_legacy_and_record_reason()
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
        Assert.Contains(progress.Activities,
            a => a.Contains("Ordering Lists activas", StringComparison.Ordinal));
    }

    // ────────────────────────────────────────────────────────────────────
    // 5. Sem catálogo → CatalogUnavailable intacto (DC-11b não altera)
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
    // 6. Regressão: composição nula mantém a leitura do ficheiro
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Null_composition_still_reads_playlist_file()
    {
        await CreateCanonicalAsync("dc11b-legacy", "PT: DC11b Legacy");
        await WriteLegacyPlaylistAsync();

        var handler = new SyncHandler();
        var outcome = await NewCoordinator(handler).RunAsync(
            PlaylistPath(), _outputDir, _resolver, selection: null, liveRunProgress: null);

        Assert.Equal(DispatcharrSyncStatus.Succeeded, outcome.Status);
        // Com o catálogo sem listas, a composição é nula → leitura do
        // ficheiro (o canal legado é criado).
        Assert.Single(ExtractCreatedChannels(handler.PostedChannelBodies));
    }

    // ────────────────────────────────────────────────────────────────────
    // 7. DC-11b (fix High) — composição ignora a selecção de fontes
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Regressão do caso que hoje falhava: composição (Ordering List) **plus**
    /// selecção de fontes que escolhe uma fonte diferente. Sem o fix, a
    /// selecção filtraria as streams da composição, esvaziando o canal
    /// (PATCH streams=[]) e apagando streams CrawlerManaged (DELETE), e um
    /// canal novo sem streams efectivas nem seria criado.
    /// </summary>
    [Fact]
    public async Task Composition_ignores_selection_and_does_not_empty_or_delete_streams()
    {
        var alphaId = await CreateCanonicalAsync("dc11b-alpha", "DC11b Alpha");
        var betaId = await CreateCanonicalAsync("dc11b-beta", "DC11b Beta");
        await AddSourceAsync(alphaId, "http://dc11b.example/alpha.ts");
        await AddSourceAsync(betaId, "http://dc11b.example/beta.ts");

        var list = await _resolver.CreateOrderingListAsync("dc11b-pt", "DC11b PT", "pt", null);
        await _resolver.AddOrderingItemAsync(list.Id, alphaId, position: 0);
        await _resolver.AddOrderingItemAsync(list.Id, betaId, position: 1);

        // Alpha já existe no Dispatcharr com a stream da composição
        // (id 7), com ownership CrawlerManaged (logo, apagável).
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
        await _resolver.EnsureStreamOwnershipAsync(7, 1, StreamOwnership.CrawlerManaged, null);

        // Selecção adversarial: para ambos os canais, "seleccionadas" = [].
        // Sob o comportamento antigo isto excluía a stream 7 (CrawlerManaged)
        // e a stream nova de Beta, produzindo PATCH streams=[]/DELETE.
        var selection = new DispatcharrSourceSelection
        {
            Applied = true,
            Channels = new List<ChannelSourceSelection>
            {
                new() { CanonicalChannelKey = "dc11b-alpha", CanonicalChannelId = alphaId },
                new() { CanonicalChannelKey = "dc11b-beta", CanonicalChannelId = betaId },
            },
        };

        var outcome = await NewCoordinator(handler).RunAsync(
            PlaylistPath(), _outputDir, _resolver, selection, liveRunProgress: null);

        Assert.Equal(DispatcharrSyncStatus.Succeeded, outcome.Status);

        // (a) nenhum DELETE de stream (a stream CrawlerManaged é preservada).
        Assert.DoesNotContain(handler.Writes,
            w => w.StartsWith("DELETE", StringComparison.Ordinal));
        // (b) nenhum PATCH streams=[] (o canal existente não é esvaziado).
        Assert.DoesNotContain(handler.Writes,
            w => w.StartsWith("PATCH", StringComparison.Ordinal)
              && w.Contains("\"streams\":[]", StringComparison.Ordinal));
        // (c) a stream da composição é associada: Beta (canal novo) é criado.
        Assert.True(ExtractCreatedChannels(handler.PostedChannelBodies)
            .ContainsKey("DC11b Beta"));
    }

    // ────────────────────────────────────────────────────────────────────
    // Helpers de cenário
    // ────────────────────────────────────────────────────────────────────

    private string PlaylistPath() => Path.Combine(_outputDir, "playlist.m3u");

    private async Task WriteLegacyPlaylistAsync() =>
        await File.WriteAllTextAsync(
            PlaylistPath(),
            "#EXTM3U\n#EXTINF:-1 group-title=\"News\",PT: DC11b Legacy\nhttp://dc11b.example/legacy.ts\n");

    private async Task<long> CreateCanonicalAsync(string key, string displayName)
    {
        var channel = await _resolver.CreateCanonicalChannelAsync(
            key, displayName, EditorialCategory.Live,
            CanonicalGroupKeys.PortugalGeneralistas, PublicationPolicy.CreateEligible,
            isEnabled: true, normalizedAliases: new List<string>());
        return channel.Id;
    }

    private async Task AddSourceAsync(long channelId, string url)
    {
        var source = await _resolver.EnsureSourceAsync(
            $"dc11b-src-{Guid.NewGuid():N}", "dc11b-src", SourceKind.Telegram,
            "telegram://dc11b", 0);
        await _resolver.RecordChannelSourceAsync(
            channelId, source.Id, url,
            availability: AvailabilityState.Discovered, matchMethod: "test");
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
    /// <c>HasValue=false</c> quando o payload não traz <c>channel_number</c>.
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
    /// os corpos de criação de canal e todas as escritas (método ≠ GET).
    /// </summary>
    private sealed class SyncHandler : HttpMessageHandler
    {
        public List<DispatcharrChannel> Channels { get; set; } = new();
        public List<DispatcharrStream> Streams { get; set; } = new();
        public List<DispatcharrChannelGroup> Groups { get; set; } = new();
        public List<string> Writes { get; } = new();
        public List<string> PostedChannelBodies { get; } = new();

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
                return Json(new { id = _nextStreamId++, name = "s", url = "u", is_custom = true });
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
