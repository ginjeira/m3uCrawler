using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services.Sync;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W-REVIEW-04 / D6-A + D8-A — Garantias de que o
/// <see cref="DispatcharrSyncCoordinator"/> é o único owner do gate
/// <c>dispatcharr-disabled</c> e do transporte HTTP testável.
///
/// <para>
/// Estes testes complementam os testes do scheduler
/// (<c>ScheduledDispatcharrSyncActionTests</c>) — provam que o
/// Coordinator, chamado directamente, devolve o outcome correcto
/// para cada estado (disabled, enabled sem playlist, enabled com
/// playlist + transport fake).
/// </para>
/// </summary>
public class DispatcharrSyncCoordinatorTests
{
    private const string PlaylistBody =
        "#EXTM3U\n#EXTINF:-1 group-title=\"SPORT TV CHANNELS\",PT: SPORT TV NBA\n" +
        "https://crawler.example/sporttvnba\n";

    [Fact]
    public async Task Disabled_config_returns_disabled_outcome_and_does_not_touch_http()
    {
        var handler = new RecordingHandler();
        var coordinator = new DispatcharrSyncCoordinator(
            configLoader: () => DispatcharrConfig.Disabled(),
            transport: handler);

        var outcome = await coordinator.RunAsync(
            playlistPath: "/tmp/playlist.m3u",
            outputDir: "/tmp",
            catalog: null,
            selection: null,
            liveRunProgress: null,
            cancellationToken: default);

        Assert.Equal(DispatcharrSyncStatus.Disabled, outcome.Status);
        Assert.Null(outcome.ErrorType);
        Assert.Null(outcome.Report);
        Assert.Empty(handler.Traces);
    }

    [Fact]
    public async Task Enabled_without_catalog_and_without_factory_returns_catalog_unavailable_when_legacy_not_allowed()
    {
        var handler = new RecordingHandler();
        var coordinator = new DispatcharrSyncCoordinator(
            configLoader: EnabledConfig,
            transport: handler);

        var outcome = await coordinator.RunAsync(
            playlistPath: "/tmp/playlist.m3u",
            outputDir: "/tmp",
            catalog: null,
            selection: null,
            liveRunProgress: null,
            cancellationToken: default);

        // Comportamento default do Coordinator (Program.cs,
        // RunPublicationService): sem catalog e sem factory => abort
        // antes de qualquer escrita HTTP. É o contract existente.
        Assert.Equal(DispatcharrSyncStatus.CatalogUnavailable, outcome.Status);
        Assert.Empty(handler.Traces);
    }

    [Fact]
    public async Task Enabled_with_allow_legacy_runs_pipeline_when_catalog_is_null()
    {
        // W-REVIEW-04: o scheduler em modo legacy passa
        // allowLegacyWithoutCatalog=true. Sem catalog o Coordinator NÃO
        // aborta: continua em modo legacy (sem ownership).
        var dir = NewOutputDir();
        var playlistPath = Path.Combine(dir, "playlist.m3u");
        await File.WriteAllTextAsync(playlistPath, PlaylistBody);

        var handler = new RecordingHandler { NextStreamId = 9000 };
        var coordinator = new DispatcharrSyncCoordinator(
            configLoader: EnabledConfig,
            transport: handler);

        var outcome = await coordinator.RunAsync(
            playlistPath: playlistPath,
            outputDir: dir,
            catalog: null,
            selection: null,
            liveRunProgress: null,
            cancellationToken: default,
            allowLegacyWithoutCatalog: true);

        Assert.Equal(DispatcharrSyncStatus.Succeeded, outcome.Status);
        Assert.NotNull(outcome.Report);
        Assert.NotEmpty(handler.Traces);
    }

    [Fact]
    public async Task Disabled_outcome_is_not_unstable_when_called_repeatedly()
    {
        // Cada chamada deve reavaliar config.Enabled (releitura).
        var callCount = 0;
        var coordinator = new DispatcharrSyncCoordinator(
            configLoader: () =>
            {
                callCount++;
                return DispatcharrConfig.Disabled();
            });

        for (int i = 0; i < 3; i++)
        {
            var outcome = await coordinator.RunAsync(
                playlistPath: "/tmp/playlist.m3u",
                outputDir: "/tmp",
                catalog: null,
                selection: null,
                liveRunProgress: null,
                cancellationToken: default);
            Assert.Equal(DispatcharrSyncStatus.Disabled, outcome.Status);
        }
        Assert.Equal(3, callCount);
    }

    [Fact]
    public void Outcome_record_carries_report_only_on_success()
    {
        // O record DispatcharrSyncOutcome é imutável; só com Succeeded
        // é que Report faz sentido. Garantimos a forma.
        var succeeded = new DispatcharrSyncOutcome(
            DispatcharrSyncStatus.Succeeded,
            ErrorType: null,
            Report: new DispatcharrSyncResult());
        Assert.Equal(DispatcharrSyncStatus.Succeeded, succeeded.Status);
        Assert.NotNull(succeeded.Report);

        var disabled = new DispatcharrSyncOutcome(DispatcharrSyncStatus.Disabled);
        Assert.Null(disabled.Report);

        var failed = new DispatcharrSyncOutcome(DispatcharrSyncStatus.Failed, "InvalidOperationException");
        Assert.Null(failed.Report);
        Assert.Equal("InvalidOperationException", failed.ErrorType);

        var catalog = new DispatcharrSyncOutcome(DispatcharrSyncStatus.CatalogUnavailable, "Exception");
        Assert.Null(catalog.Report);
        Assert.Equal("Exception", catalog.ErrorType);
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

    private static string NewOutputDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"coordinator-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Handler mínimo que regista todas as chamadas sem tocar a rede.
    /// Devolve listas vazias para os endpoints esperados pelo
    /// DispatcharrSyncService.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public System.Collections.Generic.List<string> Traces { get; } = new();
        public long NextStreamId { get; set; } = 9000;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            var method = req.Method.ToString().ToUpperInvariant();
            Traces.Add($"{method} {path}");

            if (method == "GET" && path.EndsWith("/api/core/version/"))
            {
                return Task.FromResult(Json(new { version = "0.30.0" }));
            }
            if (method == "GET" && path.EndsWith("/api/channels/channels/"))
            {
                return Task.FromResult(Json(new { count = 0, results = Array.Empty<object>() }));
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
                            name = "Sport TV NBA",
                            url = "https://crawler.example/sporttvnba",
                            tvg_id = (string?)null,
                            channel_group = (long?)null,
                            m3u_account = (long?)null,
                            m3u_account_name = "external",
                            is_custom = false,
                        },
                    },
                }));
            }
            if (method == "GET" && path.EndsWith("/api/channels/groups/"))
            {
                return Task.FromResult(Json(new { count = 0, results = Array.Empty<object>() }));
            }
            if (method == "GET" && Regex.IsMatch(path, @"/api/channels/channels/\d+/streams/?$"))
            {
                return Task.FromResult(Json(Array.Empty<object>()));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(object payload) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
    }
}
