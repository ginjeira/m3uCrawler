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
using m3uCrawler.Services.Automation;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.SourceSelection;
using m3uCrawler.Services.Sync;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W6c — O caminho agendado/standalone (<c>syncDispatcharr</c>) aplica a
/// <b>mesma</b> selecção de fontes que o caminho manual (W2/W13): política
/// persistida + selector único (<see cref="SourceSelectionStage"/>), em vez de
/// <c>selection = null</c>. Sem catálogo/política aplicável a ausência é
/// registada explicitamente (<c>selection=none</c>), nunca silenciosa.
/// </summary>
public class WaveW6cScheduledDispatcharrSelectionTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;

    public WaveW6cScheduledDispatcharrSelectionTests()
    {
        _dbPath = TestTempDb.SuitePath($"w6c-scheduled-selection-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        _resolver = new CatalogResolver(_factory, _dbPath);
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Scheduled_sync_applies_same_source_selection_as_manual_path()
    {
        var channel = await CreateCanonicalAsync("zulucluster", "zulucluster", new[] { "zulucluster" });
        var urls = await RecordChannelSourcesAsync(channel, count: 12, host: "one.example", priorityStart: 1000);
        await _resolver.UpsertGlobalSourceSelectionPolicyAsync(10, true, null, true);

        var outputDir = NewOutputDir();
        var playlistPath = Path.Combine(outputDir, "playlist.m3u");
        await File.WriteAllTextAsync(playlistPath, BuildPlaylist(urls, "zulucluster"));

        // Selecção "manual": exactamente a computada pelo caminho de publicação
        // (RunPublicationService) — política persistida + SourceSelectionStage +
        // DispatcharrSourceSelectionFactory.
        var discovered = await PlaylistReader.ReadAsync(playlistPath);
        var streams = discovered.Select(d => d.Original).ToList();
        var policies = await new SourceSelectionPolicyResolver(_resolver).LoadEffectivePoliciesAsync();
        var stage = await new SourceSelectionStage(_resolver).ApplyAsync(streams, policies);
        var manualSelection = DispatcharrSourceSelectionFactory.FromStageResult(stage, policies, DateTime.UtcNow);
        var expected = manualSelection.Channels
            .Single()
            .Selected
            .Select(s => s.StreamUrl)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(10, expected.Count);

        var handler = new SelectionRecordingHandler { NextStreamId = 9000 };
        var action = new ScheduledDispatcharrSyncAction(
            EnabledConfig(),
            new ScheduledActionOptions { OutputDir = outputDir },
            catalog: _resolver,
            transport: handler);

        var result = await action.ExecuteAsync(CancellationToken.None);

        Assert.Contains("selection=applied", result);
        var posted = handler.StreamPostBodies
            .Select(ParseUrl)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected, posted);
        Assert.Equal(10, handler.StreamPostBodies.Count);
        // Nenhuma stream não seleccionada é enviada.
        Assert.All(urls.Except(expected), u => Assert.DoesNotContain(u, posted));
    }

    [Fact]
    public async Task Scheduled_sync_without_applicable_selection_records_none_and_posts_all()
    {
        // Catálogo presente mas sem ChannelSource avaliável: o stage é no-op
        // (Applied=false). O agendado deve registar `selection=none` e manter o
        // comportamento legacy (todas as streams publicadas) — nunca converter
        // "não avaliado" em "seleccionar zero".
        await CreateCanonicalAsync("zulucluster", "zulucluster", new[] { "zulucluster" });

        var outputDir = NewOutputDir();
        var urls = new[]
        {
            "http://one.example/a.ts",
            "http://one.example/b.ts",
            "http://one.example/c.ts",
        };
        await File.WriteAllTextAsync(
            Path.Combine(outputDir, "playlist.m3u"), BuildPlaylist(urls, "zulucluster"));

        var handler = new SelectionRecordingHandler { NextStreamId = 9500 };
        var action = new ScheduledDispatcharrSyncAction(
            EnabledConfig(),
            new ScheduledActionOptions { OutputDir = outputDir },
            catalog: _resolver,
            transport: handler);

        var result = await action.ExecuteAsync(CancellationToken.None);

        Assert.Contains("selection=none", result);
        Assert.Equal(3, handler.StreamPostBodies.Count);
    }

    // ---------------- helpers ----------------

    private static DispatcharrConfig EnabledConfig() => new()
    {
        Enabled = true,
        BaseUrl = "http://dispatcharr.local",
        ApiKey = "PLACEHOLDER-API-KEY",
        DryRun = false,
        MatchThreshold = 80,
        AliasFile = null,
    };

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
                $"w6c-{channel.Key}-{i:000}",
                $"w6c-{channel.Key}-{i:000}",
                SourceKind.Telegram,
                $"telegram://{channel.Key}-{i}",
                priorityStart - i);
            await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, url, matchMethod: "test");
            urls.Add(url);
        }
        return urls;
    }

    private static string NewOutputDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"w6c-sched-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string BuildPlaylist(IEnumerable<string> urls, string title)
        => "#EXTM3U\n" +
           string.Join("\n", urls.Select(u => $"#EXTINF:-1 group-title=\"News\",{title}\n{u}")) +
           "\n";

    private static string ParseUrl(string postBody)
        => JsonDocument.Parse(postBody).RootElement.GetProperty("url").GetString()!;

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

    private sealed class SelectionRecordingHandler : HttpMessageHandler
    {
        public List<string> StreamPostBodies { get; } = new();
        public List<string> Traces { get; } = new();
        public long NextStreamId { get; set; } = 9000;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            var method = req.Method.Method.ToUpperInvariant();
            Traces.Add($"{method} {path}");

            if (method == "GET" && path.EndsWith("/api/core/version/"))
                return Task.FromResult(JsonResponse(new { version = "0.30.0" }));
            if (method == "GET" && path.EndsWith("/api/channels/channels/"))
                return Task.FromResult(JsonResponse(new { count = 0, results = Array.Empty<object>() }));
            if (method == "GET" && path.EndsWith("/api/channels/streams/"))
                return Task.FromResult(JsonResponse(new { count = 0, results = Array.Empty<object>() }));
            if (method == "GET" && path.EndsWith("/api/channels/groups/"))
                return Task.FromResult(JsonResponse(new { count = 0, results = Array.Empty<object>() }));

            if (method == "POST" && path.EndsWith("/api/channels/streams/"))
            {
                var body = req.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
                var url = JsonDocument.Parse(body).RootElement.GetProperty("url").GetString()!;
                var id = NextStreamId++;
                StreamPostBodies.Add(body);
                return Task.FromResult(JsonResponse(new { id, name = "n", url, is_custom = true }));
            }
            if (method == "POST" && path.EndsWith("/api/channels/groups/"))
                return Task.FromResult(JsonResponse(new { id = 5L, name = "News" }));
            if (method == "POST" && path.EndsWith("/api/channels/channels/"))
                return Task.FromResult(JsonResponse(new
                {
                    id = 500L,
                    name = "zulucluster",
                    channel_number = 1.0,
                    streams = new long[] { NextStreamId - 1 },
                }));
            if (method == "PATCH" && path.Contains("/api/channels/channels/"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            if (method == "DELETE")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
