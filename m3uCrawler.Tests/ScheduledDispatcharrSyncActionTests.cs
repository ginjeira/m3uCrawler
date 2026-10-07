using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Models;
using m3uCrawler.Services.Automation;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Dispatcharr;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Wave 10-0 — Testes do caminho <b>agendado</b> de sincronização com
/// Dispatcharr. Provam que <see cref="ScheduledDispatcharrSyncAction"/>
/// transporta o contexto canónico (<see cref="CatalogResolver"/> +
/// ownership registry) para o <c>ChannelMatcher</c> e para o
/// <c>DispatcharrSyncService</c>, e que a protecção de streams
/// <c>External</c>/<c>Unknown</c>/sem registo está activa nesse caminho.
///
/// <para>
/// Sem rede real: o transporte HTTP é injectado pelo seam interno
/// (<see cref="RecordingDispatcharrHandler"/>), que regista DELETE e
/// nunca toca a Internet.
/// </para>
/// </summary>
public class ScheduledDispatcharrSyncActionTests : IAsyncLifetime
{
    private const string ExternalUrl = "https://external.example/sporttvnba";
    private const string CrawlerUrl = "https://crawler.example/sporttvnba";

    // A stream externa tem um nome que NÃO normaliza para o título
    // descoberto, para que seja considerada stale (candidata a Removed)
    // e o guard de ownership seja efectivamente exercitado.
    private const string ExternalStreamName = "Feed Antigo";

    private const string PlaylistBody =
        "#EXTM3U\n#EXTINF:-1 group-title=\"SPORT TV CHANNELS\",PT: SPORT TV NBA\n" + CrawlerUrl + "\n";

    private readonly string _dbPath;
    private TestDbContextFactory _factory = null!;

    public ScheduledDispatcharrSyncActionTests()
    {
        _dbPath = Path.Combine(
            Path.GetTempPath(), $"scheduled-dispatcharr-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------- Ownership protection no caminho agendado ----------

    [Fact]
    public async Task Scheduled_sync_with_external_ownership_never_deletes_stream()
    {
        await SeedStreamOwnershipAsync(StreamOwnership.External);

        var (handler, _) = await RunScheduledSyncAsync(useCatalog: true);

        Assert.DoesNotContain(1L, handler.DeleteStreamIds);
    }

    [Fact]
    public async Task Scheduled_sync_without_ownership_record_treats_stream_as_unknown_and_never_deletes()
    {
        // Sem registo na BD e COM catalog → Unknown (default seguro).
        // Este teste é a prova de que o catalog está efectivamente ligado:
        // sem ele, a stream seria tratada como CrawlerManaged e removida
        // (ver Scheduled_sync_without_catalog_falls_back_to_legacy_delete).
        var (handler, _) = await RunScheduledSyncAsync(useCatalog: true);

        Assert.DoesNotContain(1L, handler.DeleteStreamIds);
    }

    [Fact]
    public async Task Scheduled_sync_with_crawler_managed_ownership_deletes_stream()
    {
        await SeedStreamOwnershipAsync(StreamOwnership.CrawlerManaged);

        var (handler, _) = await RunScheduledSyncAsync(useCatalog: true);

        Assert.Contains(1L, handler.DeleteStreamIds);
    }

    [Fact]
    public async Task Scheduled_sync_without_catalog_falls_back_to_legacy_delete()
    {
        // Caracterização do fallback legacy (sem catalog): serve de
        // contraste para provar que o cenário de teste produz realmente
        // um candidato a Removed e que é o catalog que o protege.
        var (handler, _) = await RunScheduledSyncAsync(useCatalog: false);

        Assert.Contains(1L, handler.DeleteStreamIds);
    }

    // ---------- Comportamento preservado ----------

    [Fact]
    public async Task Scheduled_sync_is_noop_when_disabled_and_never_calls_http()
    {
        var outputDir = NewOutputDir();
        var handler = new RecordingDispatcharrHandler();
        var action = new ScheduledDispatcharrSyncAction(
            DispatcharrConfig.Disabled(),
            new ScheduledActionOptions { OutputDir = outputDir },
            catalog: new CatalogResolver(_factory, _dbPath),
            transport: handler);

        var result = await action.ExecuteAsync(CancellationToken.None);

        Assert.Equal("dispatcharr-disabled", result);
        Assert.Empty(handler.Traces);
    }

    [Fact]
    public async Task Scheduled_sync_returns_no_playlist_when_file_missing()
    {
        var outputDir = NewOutputDir();
        var handler = new RecordingDispatcharrHandler();
        var action = new ScheduledDispatcharrSyncAction(
            EnabledConfig(),
            new ScheduledActionOptions { OutputDir = outputDir },
            catalog: new CatalogResolver(_factory, _dbPath),
            transport: handler);

        var result = await action.ExecuteAsync(CancellationToken.None);

        Assert.Equal("no-playlist", result);
        Assert.Empty(handler.Traces);
    }

    // ---------- helpers ----------

    private async Task SeedStreamOwnershipAsync(StreamOwnership ownership)
    {
        await using var ctx = _factory.CreateDbContext();
        ctx.DispatcharrStreamOwnerships.Add(new DispatcharrStreamOwnershipEntity
        {
            DispatcharrStreamId = 1,
            DispatcharrChannelId = 100,
            Ownership = ownership,
            CreatedBySyncRunId = null,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<(RecordingDispatcharrHandler Handler, string Result)> RunScheduledSyncAsync(bool useCatalog)
    {
        var outputDir = NewOutputDir();
        await File.WriteAllTextAsync(Path.Combine(outputDir, "playlist.m3u"), PlaylistBody);

        var handler = new RecordingDispatcharrHandler();
        var action = new ScheduledDispatcharrSyncAction(
            EnabledConfig(),
            new ScheduledActionOptions { OutputDir = outputDir },
            catalog: useCatalog ? new CatalogResolver(_factory, _dbPath) : null,
            transport: handler);

        var result = await action.ExecuteAsync(CancellationToken.None);
        return (handler, result);
    }

    private static string NewOutputDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sched-dispatcharr-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
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

    private static HttpResponseMessage Json(object payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

    /// <summary>
    /// Dispatcharr falso: devolve um canal <c>Sport TV NBA</c> (id 100)
    /// com uma stream externa (id 1, URL diferente da playlist) e
    /// regista todos os DELETE emitidos. Nunca toca a rede.
    /// </summary>
    private sealed class RecordingDispatcharrHandler : HttpMessageHandler
    {
        public List<string> Traces { get; } = new();
        public List<long> DeleteStreamIds { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            var method = req.Method.ToString().ToUpperInvariant();
            Traces.Add($"{method} {path}");

            var channelStreams = Regex.Match(path, @"/api/channels/channels/(\d+)/streams/?$");
            if (method == "GET" && channelStreams.Success)
            {
                return Task.FromResult(Json(new[]
                {
                    new { id = 1L, name = ExternalStreamName, url = ExternalUrl },
                }));
            }

            if (method == "GET" && path.EndsWith("/api/channels/channels/"))
            {
                return Task.FromResult(Json(new
                {
                    count = 1,
                    results = new[]
                    {
                        new { id = 100L, name = "Sport TV NBA", channel_number = 1.0, tvg_id = (string?)null, streams = new long[] { 1 } },
                    },
                }));
            }

            if (method == "GET" && path.EndsWith("/api/channels/streams/"))
            {
                return Task.FromResult(Json(new
                {
                    count = 1,
                    results = new[]
                    {
                        new
                        {
                            id = 1L,
                            name = ExternalStreamName,
                            url = ExternalUrl,
                            tvg_id = (string?)null,
                            channel_group = (long?)5,
                            m3u_account = (long?)null,
                            m3u_account_name = "external",
                            is_custom = false,
                        },
                    },
                }));
            }

            if (method == "GET" && path.EndsWith("/api/channels/groups/"))
            {
                return Task.FromResult(Json(new
                {
                    count = 1,
                    results = new[] { new { id = 5L, name = "SPORT TV CHANNELS" } },
                }));
            }

            if (method == "GET" && path.EndsWith("/api/core/version/"))
            {
                return Task.FromResult(Json(new { version = "0.30.0" }));
            }

            if (method == "POST" && path.EndsWith("/api/channels/streams/"))
            {
                return Task.FromResult(Json(new { id = 2001L, name = "Sport TV NBA", url = CrawlerUrl, is_custom = true }));
            }

            if (method == "POST" && path.EndsWith("/api/channels/groups/"))
            {
                return Task.FromResult(Json(new { id = 5L, name = "SPORT TV CHANNELS" }));
            }

            if (method == "POST" && path.EndsWith("/api/channels/channels/"))
            {
                return Task.FromResult(Json(new { id = 500L, name = "Sport TV NBA", channel_number = 1.0, streams = new long[] { 2001 } }));
            }

            if (method == "PATCH" && path.Contains("/api/channels/channels/"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            if (method == "DELETE" && Regex.IsMatch(path, @"/api/channels/streams/\d+/?$"))
            {
                var m = Regex.Match(path, @"/streams/(\d+)");
                if (m.Success && long.TryParse(m.Groups[1].Value, out var sid))
                {
                    DeleteStreamIds.Add(sid);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
