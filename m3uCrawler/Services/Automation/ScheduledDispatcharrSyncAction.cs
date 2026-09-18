using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Dispatcharr;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using m3uCrawler.Services.Sync;

namespace m3uCrawler.Services.Automation;

/// <summary>
/// PHASE 12 — Acção agendada que sincroniza a playlist funcional
/// (<c>output/playlist.m3u</c>) com Dispatcharr, reutilizando o
/// <see cref="DispatcharrSyncService"/> existente.
///
/// <para>
/// Nome de action (estável): <c>syncDispatcharr</c>.
/// </para>
///
/// <para>
/// Quando <c>dispatcharr_enabled=false</c> em <c>wtelegram.config</c>,
/// a acção termina precocemente com <c>dispatcharr-disabled</c> — não
/// escreve nada em HTTP nem em ficheiros extra. Esta decisão é
/// tomada aqui deliberadamente (em vez de delegar no sync service
/// para devolver DryRun) para que o resultado registado no
/// <c>LastResult</c> reflicta fielmente porque é que nada aconteceu.
/// </para>
///
/// <para>
/// Wave 10-0 — o caminho agendado constrói o <b>mesmo pipeline
/// canónico</b> do caminho principal (<c>Program.cs</c>):
/// <see cref="CatalogResolver"/> (catálogo canónico + ownership) é
/// injectado no <see cref="ChannelMatcher"/> e no
/// <see cref="DispatcharrSyncService"/>. Sem catalog o matcher e a
/// fase de apply cairiam no <i>modo legacy</i> (tudo tratado como
/// <c>CrawlerManaged</c>), permitindo DELETE/rename de streams
/// <c>External</c>/<c>Unknown</c>. Isso deixa de ser possível no
/// caminho agendado.
/// </para>
///
/// <para>
/// Contrato da playlist: existe <b>uma única</b> playlist funcional,
/// <c>output/playlist.m3u</c> (ver <see cref="FunctionalPlaylistFileName"/>),
/// partilhada pelas várias fases do crawler (discovery, validação,
/// geração composta, pipeline Telegram). O sync consome exactamente
/// esse ficheiro — não é criada uma segunda playlist.
/// </para>
/// </summary>
public sealed class ScheduledDispatcharrSyncAction : IScheduledAction
{
    public const string ActionName = "syncDispatcharr";

    /// <summary>
    /// Nome do ficheiro da playlist funcional única consumida pelo sync
    /// (relativo a <see cref="ScheduledActionOptions.OutputDir"/>).
    /// </summary>
    internal const string FunctionalPlaylistFileName = "playlist.m3u";

    private readonly DispatcharrConfig _config;
    private readonly string _outputDir;
    private readonly CatalogResolver? _catalog;
    private readonly HttpMessageHandler? _transport;

    /// <summary>
    /// Construtor de produção. O <see cref="CatalogResolver"/> é
    /// injectado pelo DI (registado como singleton em
    /// <see cref="ScheduledAutomationHost.Build"/>); quando é
    /// <c>null</c> (contextos de teste/legado explícitos) o pipeline
    /// corre em modo legacy, tal como antes da Wave 10-0.
    /// </summary>
    public ScheduledDispatcharrSyncAction(
        DispatcharrConfig config,
        ScheduledActionOptions options,
        CatalogResolver? catalog = null)
        : this(config, options, catalog, transport: null)
    {
    }

    /// <summary>
    /// Seam interno de teste: permite injectar o transporte HTTP para
    /// exercitar o pipeline real (matcher + ownership + apply) sem
    /// tocar a rede. Não exposto em DI.
    /// </summary>
    internal ScheduledDispatcharrSyncAction(
        DispatcharrConfig config,
        ScheduledActionOptions options,
        CatalogResolver? catalog,
        HttpMessageHandler? transport)
    {
        _config = config;
        _outputDir = options.OutputDir;
        _catalog = catalog;
        _transport = transport;
    }

    public string Name => ActionName;

    public async Task<string> ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!_config.Enabled)
        {
            return "dispatcharr-disabled";
        }

        var playlistPath = Path.Combine(_outputDir, FunctionalPlaylistFileName);
        if (!File.Exists(playlistPath))
        {
            return "no-playlist";
        }

        var aliases = AliasResolver.FromFile(_config.AliasFile);
        var ordering = new StreamOrderingPolicy(_config.ProviderPriority);
        var matcher = new ChannelMatcher(aliases, null, _catalog);

        DispatcharrSyncService sync;
        if (_transport != null)
        {
            var built = DispatcharrClientFactory.BuildWithTransport(
                _config.BaseUrl, _config.ApiKey, _config.Username, _config.Password, _transport);
            sync = new DispatcharrSyncService(
                _config, _outputDir,
                aliases: aliases,
                ordering: ordering,
                matcher: matcher,
                http: built.Http,
                auth: built.Auth,
                login: built.Login,
                channels: built.Channels,
                streams: built.Streams,
                m3u: built.M3U,
                catalog: _catalog);
        }
        else
        {
            sync = new DispatcharrSyncService(
                _config, _outputDir,
                aliases: aliases,
                ordering: ordering,
                matcher: matcher,
                catalog: _catalog);
        }

        // Legacy scheduled path: sem artefacto de selecção, selection null
        // (sem correlação heurística com a playlist).
        var result = await sync.RunAsync(playlistPath, cancellationToken);
        var counts = result.Report?.Counts;
        return $"newChannels={counts?.NewChannels ?? 0} newStreams={counts?.NewStreams ?? 0} matched={counts?.Matched ?? 0} ambiguous={counts?.Ambiguous ?? 0} dryRun={result.DryRun}";
    }
}
