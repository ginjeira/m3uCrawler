using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 9C.1 — O dashboard permanece acessível em <c>NOT_CONFIGURED</c>
/// e reporta o estado de configuração de forma inequívoca, sem contornar
/// a autenticação nem expor operações destrutivas.
/// </summary>
public class ConfigurationLifecycleEndpointTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _outputDir;
    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;
    private ConfigurationLifecycleService _lifecycle = null!;

    public ConfigurationLifecycleEndpointTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cfg-endpoint-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _outputDir = Path.Combine(_root, "output");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_outputDir);
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        _resolver = new CatalogResolver(_factory, _dbPath);
        _lifecycle = new ConfigurationLifecycleService(
            new ConfigurationLifecycleStore(Path.Combine(_root, ConfigurationLifecycleStore.FileName)),
            _factory,
            _outputDir);
        await _lifecycle.EnsureInitializedAsync();
    }

    public Task DisposeAsync()
    {
        WebDashboardService.SetConfigurationLifecycle(null);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Lifecycle_payload_reports_ready_and_not_configured_states()
    {
        var notConfigured = await WebDashboardService.BuildLifecyclePayloadAsync(_lifecycle);
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(notConfigured)))
        {
            Assert.Equal(
                ConfigurationLifecycleStateNames.NotConfigured,
                doc.RootElement.GetProperty("state").GetString());
            Assert.True(doc.RootElement.GetProperty("available").GetBoolean());
            Assert.False(doc.RootElement.GetProperty("isReady").GetBoolean());
        }

        _lifecycle.SetState(ConfigurationLifecycleState.Ready, "test");

        var ready = await WebDashboardService.BuildLifecyclePayloadAsync(_lifecycle);
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(ready)))
        {
            Assert.Equal(
                ConfigurationLifecycleStateNames.Ready,
                doc.RootElement.GetProperty("state").GetString());
            Assert.True(doc.RootElement.GetProperty("isReady").GetBoolean());
        }
    }

    [Fact]
    public async Task Lifecycle_payload_without_wiring_is_fail_safe()
    {
        var payload = await WebDashboardService.BuildLifecyclePayloadAsync(null);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));

        Assert.False(doc.RootElement.GetProperty("available").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("isReady").GetBoolean());
        Assert.Equal(
            ConfigurationLifecycleStateNames.NotConfigured,
            doc.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Dashboard_serves_lifecycle_and_version_over_loopback_in_not_configured()
    {
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        WebDashboardService.SetConfigurationLifecycle(_lifecycle);

        var cts = new CancellationTokenSource();
        var composer = new PlaylistComposerService(_factory);
        var history = new ImportHistoryService(_outputDir);

        var loop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (Exception) { break; }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await WebDashboardService.HandleRequestOnTestAsync(
                            ctx, _outputDir, _resolver, composer, history);
                    }
                    catch
                    {
                        try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { /* closed */ }
                    }
                });
            }
        });

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            var version = await http.GetAsync("/api/version");
            Assert.Equal(HttpStatusCode.OK, version.StatusCode);

            var response = await http.GetAsync("/api/configuration/lifecycle");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(
                ConfigurationLifecycleStateNames.NotConfigured,
                doc.RootElement.GetProperty("state").GetString());
            Assert.False(doc.RootElement.GetProperty("isReady").GetBoolean());
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            listener.Close();
            try { await loop; } catch (Exception) { /* best effort */ }
            cts.Dispose();
        }
    }

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
