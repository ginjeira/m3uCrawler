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
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.LiveRun;
using m3uCrawler.Services.Sync;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Wave W2 — pipeline canónico de playlists:
/// <c>RUN → normalização/deduplicação → playlist_temp.m3u →
/// filtros/validação/matching/rejeição/publicação → playlist.m3u →
/// Dispatcharr</c>.
///
/// <para>
/// Prova que (A) a deduplicação por URL é materializada no intermédio;
/// (B/C) o canónico final é exactamente a saída da selecção e deixa cair
/// os streams rejeitados que ainda constam no intermédio; (D) o
/// Dispatcharr consome <c>playlist.m3u</c> (nunca o timestamped); e (E) o
/// timestamped continua a ser produzido no caminho normal como histórico,
/// enquanto o modo manutenção escreve apenas o canónico.
/// </para>
///
/// <para>
/// Determinístico: relógio fixo, directório de output temporário, catálogo
/// SQLite temporário e transporte Dispatcharr falso. Sem rede, sem
/// Telegram e sem Dispatcharr real.
/// </para>
/// </summary>
public class WaveW2CanonicalPipelineTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly DateTime _fixedLocal = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Local);
    private readonly DateTime _fixedUtc = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;

    public WaveW2CanonicalPipelineTests()
    {
        _root = TestTempDb.SuitePath($"w2-canonical-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "channel-catalog.db");
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        await using (var ctx = await bootstrapper.InitializeAsync())
        {
        }
        _resolver = new CatalogResolver(_factory, _dbPath);
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        TestTempDb.CleanupDirectory(_root);
        return Task.CompletedTask;
    }

    // ===================== A — dedup no intermédio =====================

    [Fact]
    public async Task A_intermediate_hold_deduplicated_discovery_set()
    {
        var outputDir = NewDir("w2a");
        var streams = new List<M3uStream>
        {
            Work("http://dup.example/a.ts", "A"),
            Work("http://dup.example/b.ts", "B"),
            Work("http://dup.example/a.ts", "A-duplicado"),
        };

        var result = await NewService(outputDir).PublishAsync(new RunPublicationRequest
        {
            Streams = streams,
            Report = new RunReport(),
            RunCountryGate = false,
            Verbose = false,
        });

        var tempPath = Path.Combine(outputDir, "playlist_temp.m3u");
        Assert.True(File.Exists(tempPath));
        Assert.Equal(tempPath, result.IntermediatePlaylistPath);

        var tempUrls = ReadUrls(tempPath);
        Assert.Equal(2, tempUrls.Count);
        Assert.Single(tempUrls, u => u.Equals("http://dup.example/a.ts", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("http://dup.example/b.ts", tempUrls);

        // Dedup mantém a PRIMEIRA ocorrência (título "A", não "A-duplicado").
        var content = await File.ReadAllTextAsync(tempPath);
        Assert.Contains(",A", content);
        Assert.DoesNotContain("A-duplicado", content);

        // Sem selecção aplicável (catálogo sem ChannelSources) o intermédio
        // e o final coincidem em conteúdo.
        Assert.Equal(tempUrls.OrderBy(x => x), ReadUrls(result.PlaylistPath).OrderBy(x => x));
    }

    // ============ B/C — final é a selecção; temp preserva rejeitados ============

    [Fact]
    public async Task B_C_final_playlist_is_selection_output_while_temp_keeps_rejected()
    {
        var channel = await CreateCanonicalAsync("zulucluster", "zulucluster", new[] { "zulucluster" });
        var urls = await RecordChannelSourcesAsync(channel, count: 12, host: "one.example", priorityStart: 1000);
        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(10, true, null, true);

        var outputDir = NewDir("w2bc");
        var result = await NewService(outputDir, _resolver).PublishAsync(new RunPublicationRequest
        {
            Streams = urls.Select(u => Work(u, "zulucluster")).ToList(),
            Report = new RunReport(),
            RunCountryGate = false,
            Verbose = false,
        });

        // A selecção correu e rejeitou 2 das 12 fontes (max 10 por canal).
        Assert.True(result.Selection.Applied);
        Assert.Equal(2, result.Selection.Rejected.Count);
        Assert.Equal(10, result.Selection.Selected.Count);

        var tempUrls = ReadUrls(result.IntermediatePlaylistPath).ToHashSet(StringComparer.Ordinal);
        var finalUrls = ReadUrls(result.PlaylistPath).ToHashSet(StringComparer.Ordinal);

        // B — o final é exactamente a saída da selecção.
        var expectedFinal = result.Selection.Published.Select(s => s.Url).ToHashSet(StringComparer.Ordinal);
        Assert.True(expectedFinal.SetEquals(finalUrls));
        Assert.Equal(10, finalUrls.Count);

        // C — separação: o intermédio mantém o conjunto descoberto (12); cada
        // rejeitado está presente no intermédio e ausente do final.
        Assert.Equal(12, tempUrls.Count);
        foreach (var rejected in result.Selection.Rejected)
        {
            Assert.Contains(rejected.Candidate.StreamUrl, tempUrls);
            Assert.DoesNotContain(rejected.Candidate.StreamUrl, finalUrls);
        }
        Assert.NotEqual(tempUrls, finalUrls);
    }

    // ============ D — Dispatcharr consome playlist.m3u ============

    [Fact]
    public async Task D_dispatcharr_consumes_canonical_playlist_not_timestamped()
    {
        var channel = await CreateCanonicalAsync("zulucluster", "zulucluster", new[] { "zulucluster" });
        var urls = await RecordChannelSourcesAsync(channel, count: 3, host: "one.example", priorityStart: 100);
        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(10, true, null, true);

        var outputDir = NewDir("w2d");
        var handler = new EmptyDispatcharrHandler();
        var dispatcharr = new DispatcharrSyncCoordinator(
            () => EnabledDryRunConfig(), transport: handler);

        var result = await NewService(outputDir, _resolver, dispatcharr).PublishAsync(
            new RunPublicationRequest
            {
                Streams = urls.Select(u => Work(u, "zulucluster")).ToList(),
                Report = new RunReport(),
                RunCountryGate = false,
                Verbose = false,
            });

        Assert.Equal(DispatcharrSyncStatus.Succeeded, result.Dispatcharr.Status);
        Assert.NotNull(result.Dispatcharr.Report);
        Assert.True(result.Dispatcharr.Report!.DryRun, "O sync de teste deve correr em dry-run.");

        var sourcePath = result.Dispatcharr.Report!.Report.SourcePlaylistPath;
        Assert.EndsWith("playlist.m3u", sourcePath);
        Assert.DoesNotContain("telegram_playlist_", sourcePath);
        Assert.True(File.Exists(Path.Combine(outputDir, "playlist.m3u")));
    }

    // ============ E — timestamped histórico + manutenção só canónico ============

    [Fact]
    public async Task E_timestamped_is_historical_and_maintenance_writes_only_canonical()
    {
        // Caminho normal: timestamped (histórico) + canónico, com conteúdo igual.
        var normalDir = NewDir("w2e-normal");
        var result = await NewService(normalDir).PublishAsync(new RunPublicationRequest
        {
            Streams = new List<M3uStream> { Work("http://e.example/x.ts", "X") },
            Report = new RunReport(),
            RunCountryGate = false,
            Verbose = false,
        });

        var timestamped = Path.Combine(normalDir, "telegram_playlist_20260921_120000.m3u");
        Assert.True(File.Exists(timestamped));
        Assert.True(File.Exists(Path.Combine(normalDir, "playlist.m3u")));
        Assert.EndsWith("playlist.m3u", result.PlaylistPath);
        Assert.EndsWith("telegram_playlist_20260921_120000.m3u", result.HistoricalPlaylistPath);
        Assert.Equal(
            await File.ReadAllTextAsync(timestamped),
            await File.ReadAllTextAsync(result.PlaylistPath));

        // Modo manutenção: PlaylistFileName="playlist.m3u" coincide com o
        // canónico, pelo que nenhum timestamped é criado.
        var maintDir = NewDir("w2e-maint");
        var maintResult = await NewService(maintDir).PublishAsync(new RunPublicationRequest
        {
            Streams = new List<M3uStream> { Work("http://e.example/y.ts", "Y") },
            Report = new RunReport(),
            RunCountryGate = false,
            Verbose = false,
            PlaylistFileName = "playlist.m3u",
        });

        Assert.True(File.Exists(Path.Combine(maintDir, "playlist.m3u")));
        Assert.Empty(Directory.GetFiles(maintDir, "telegram_playlist_*.m3u"));
        Assert.Equal(maintResult.PlaylistPath, maintResult.HistoricalPlaylistPath);
    }

    // ===================== helpers =====================

    private string NewDir(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private RunPublicationService NewService(
        string outputDir,
        CatalogResolver? catalog = null,
        DispatcharrSyncCoordinator? dispatcharr = null)
        => new(
            outputDir,
            new PlaylistManagerService(),
            new ImportHistoryService(outputDir),
            countryValidator: null,
            catalog: catalog,
            dispatcharr: dispatcharr ?? new DispatcharrSyncCoordinator(() => DispatcharrConfig.Disabled()),
            clock: () => _fixedLocal,
            utcClock: () => _fixedUtc);

    private static M3uStream Work(string url, string title) => new()
    {
        Url = url,
        Title = title,
        Group = "Geral",
        IsWorking = true,
        ResponseTime = 10,
    };

    private static List<string> ReadUrls(string path)
        => File.ReadAllLines(path)
            .Where(l => l.StartsWith("http://", StringComparison.Ordinal)
                     || l.StartsWith("https://", StringComparison.Ordinal))
            .ToList();

    private async Task<CanonicalChannelEntity> CreateCanonicalAsync(
        string key, string displayName, string[] aliases)
        => await _resolver.CreateCanonicalChannelAsync(
            key,
            displayName,
            EditorialCategory.Live,
            CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy.CreateEligible,
            isEnabled: true,
            normalizedAliases: aliases);

    private async Task<List<string>> RecordChannelSourcesAsync(
        CanonicalChannelEntity channel, int count, string host, int priorityStart)
    {
        var urls = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var url = $"http://{host}/s{i:000}.ts";
            var source = await _resolver.EnsureSourceAsync(
                $"w2-{channel.Key}-{i:000}",
                $"w2-{channel.Key}-{i:000}",
                SourceKind.Telegram,
                $"telegram://{channel.Key}-{i}",
                priorityStart - i);
            await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, url, matchMethod: "test");
            urls.Add(url);
        }
        return urls;
    }

    private static DispatcharrConfig EnabledDryRunConfig() => new()
    {
        Enabled = true,
        BaseUrl = "http://dispatcharr.local",
        ApiKey = "PLACEHOLDER-API-KEY",
        DryRun = true,
        MatchThreshold = 80,
        AliasFile = null,
    };

    private static HttpResponseMessage Json(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

    /// <summary>
    /// Dispatcharr falso, estado vazio. Só serve o GET de leitura que o
    /// dry-run executa; nunca toca a rede nem escreve.
    /// </summary>
    private sealed class EmptyDispatcharrHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/core/version/"))
                return Task.FromResult(Json(new { version = "0.30.0" }));
            if (path.EndsWith("/api/channels/channels/"))
                return Task.FromResult(Json(new { count = 0, results = Array.Empty<object>() }));
            if (path.EndsWith("/api/channels/streams/"))
                return Task.FromResult(Json(new { count = 0, results = Array.Empty<object>() }));
            if (path.EndsWith("/api/channels/groups/"))
                return Task.FromResult(Json(new { count = 0, results = Array.Empty<object>() }));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
