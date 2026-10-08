using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.Sync;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// DC-5d/DC-5e — Cobertura HTTP dos endpoints consumidos pela nova UI:
/// <list type="bullet">
///   <item><c>GET /api/classification-summary</c> — estado "sem plano"
///     (<c>{error}</c> com 200) e o contrato positivo (contagens por
///     <c>ChannelKind</c>, <c>excludedCount</c>, amostra e metadados).</item>
///   <item><c>GET /api/playlist_temp/preview</c> — 404 quando o ficheiro não
///     existe e conteúdo sanitizado quando existe.</item>
/// </list>
/// Os endpoints backend não são alterados; a cobertura de 405 por verbo já
/// existe em <see cref="WaveW3HttpSemanticsTests"/>.
/// </summary>
public sealed class WaveDC5d5eHttpTests : IAsyncLifetime
{
    private const string RawUrl =
        "http://user:sup3rsecret@dash-temp.example.test/live/USER/PASS/1";

    private string _root = null!;
    private string _dbPath = null!;
    private string _outputDir = null!;
    private DashboardBootstrapEndpointTests.DashboardHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"dc5de-http-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _outputDir = Path.Combine(_root, "output");
        Directory.CreateDirectory(_outputDir);

        var factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();

        var resolver = new CatalogResolver(factory, _dbPath);
        var composer = new PlaylistComposerService(factory);
        var history = new ImportHistoryService(_outputDir);

        _harness = DashboardBootstrapEndpointTests.DashboardHarness.Start(
            _outputDir, resolver, composer, history,
            lifecycle: null, auth: null, bootstrap: null, webToken: null,
            standalone: true);
    }

    public async Task DisposeAsync()
    {
        await _harness.DisposeAsync();
        WebDashboardService.SetAuth(null, null);
        WebDashboardService.SetConfigurationLifecycle(null);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private HttpClient Client => _harness.Client;

    private static CancellationToken ShortToken()
        => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    // ===================== DC-5d — classification-summary =====================

    [Fact]
    public async Task Classification_summary_without_plan_is_a_200_no_data_state()
    {
        var response = await Client.GetAsync("/api/classification-summary", ShortToken());

        // "Sem plano" é um estado, não um erro fatal: 200 com { error }.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("error", out var error));
        Assert.Equal("Sem plano de classificação disponível.", error.GetString());
    }

    [Fact]
    public async Task Classification_summary_with_plan_returns_counts_sample_and_metadata()
    {
        var plan = new MatchPlan
        {
            GeneratedAtUtc = "2026-10-08T12:00:00Z",
            SourcePlaylistPath = "output/playlist.m3u",
            Counts = new SyncReportCounts
            {
                Classification = new Dictionary<string, int>
                {
                    ["Channel"] = 3,
                    ["Vod"] = 1,
                },
            },
            ClassifiedExclusions = new[]
            {
                new ClassifiedExclusion
                {
                    Title = "Filme X",
                    Group = "VOD",
                    Kind = ChannelKind.Vod,
                    Reason = "not-live",
                },
            },
        };
        await MatchPlanSerializer.WriteAsync(
            plan, Path.Combine(_outputDir, "dispatcharr_plan_20261008_120000.json"));

        var response = await Client.GetAsync("/api/classification-summary", ShortToken());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal(3, root.GetProperty("classification").GetProperty("Channel").GetInt32());
        Assert.Equal(1, root.GetProperty("classification").GetProperty("Vod").GetInt32());
        Assert.Equal(1, root.GetProperty("excludedCount").GetInt32());
        Assert.Equal("2026-10-08T12:00:00Z", root.GetProperty("planGeneratedAtUtc").GetString());
        Assert.Equal("output/playlist.m3u", root.GetProperty("planSourcePlaylistPath").GetString());

        var sample = root.GetProperty("sample").EnumerateArray();
        var entry = Assert.Single(sample);
        Assert.Equal("Filme X", entry.GetProperty("title").GetString());
        Assert.Equal("VOD", entry.GetProperty("group").GetString());
        Assert.Equal("Vod", entry.GetProperty("kind").GetString());
        Assert.Equal("not-live", entry.GetProperty("reason").GetString());
    }

    // ===================== DC-5e — playlist_temp/preview =====================

    [Fact]
    public async Task Playlist_temp_preview_missing_file_returns_404_with_message()
    {
        var response = await Client.GetAsync("/api/playlist_temp/preview", ShortToken());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Playlist temporária não encontrada", body);
    }

    [Fact]
    public async Task Playlist_temp_preview_sanitizes_credentials()
    {
        var content = "#EXTM3U\n#EXTINF:-1 tvg-id=\"x\",Canal\n" + RawUrl + "\n";
        File.WriteAllText(
            Path.Combine(_outputDir, "playlist_temp.m3u"), content, new UTF8Encoding(false));

        var response = await Client.GetAsync("/api/playlist_temp/preview", ShortToken());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/x-mpegurl", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sup3rsecret", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("***", body);
        Assert.Equal(CredentialSanitizer.SanitizeM3uContent(content), body);
    }
}
