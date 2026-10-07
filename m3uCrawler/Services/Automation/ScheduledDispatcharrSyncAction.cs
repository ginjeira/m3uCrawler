using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using m3uCrawler.Services.SourceSelection;
using m3uCrawler.Services.Sync;

namespace m3uCrawler.Services.Automation;

/// <summary>
/// PHASE 12 — Acção agendada que sincroniza a playlist funcional
/// (<c>output/playlist.m3u</c>) com Dispatcharr, reutilizando o
/// <see cref="DispatcharrSyncCoordinator"/> canónico como owner único do
/// fluxo Dispatcharr.
///
/// <para>
/// Nome de action (estável): <c>syncDispatcharr</c>.
/// </para>
///
/// <para>
/// W-REVIEW-04 / D6-A + D8-A: o gate <c>dispatcharr-disabled</c> e toda a
/// execução Dispatcharr vivem agora exclusivamente no
/// <see cref="DispatcharrSyncCoordinator"/>. Esta acção apenas:
/// (1) verifica a presença da playlist funcional (<c>no-playlist</c>);
/// (2) constrói a <see cref="DispatcharrSourceSelection"/> a partir do
/// catálogo (paridade com o caminho manual);
/// (3) delega no Coordinator;
/// (4) converte o <see cref="DispatcharrSyncOutcome"/> no contract string
/// preservado (<c>"dispatcharr-disabled"</c>, <c>"no-playlist"</c>,
/// <c>"error:..."</c>) usado em <c>LastResult</c>.
/// </para>
///
/// <para>
/// Wave 10-0 — o caminho agendado continua a injectar o
/// <see cref="CatalogResolver"/> no Coordinator para que
/// <see cref="ChannelMatcher"/> e o sync usem o catálogo canónico +
/// ownership registry. Sem catalog o matcher e a fase de apply cairiam no
/// <i>modo legacy</i> (tudo tratado como <c>CrawlerManaged</c>),
/// permitindo DELETE/rename de streams <c>External</c>/<c>Unknown</c>.
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
    private readonly Func<DispatcharrConfig>? _configLoader;
    private readonly string _outputDir;
    private readonly CatalogResolver? _catalog;
    private readonly HttpMessageHandler? _transport;
    private readonly DispatcharrSyncCoordinator _coordinator;

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
        : this(config, options, catalog, transport: null, configLoader: null, coordinator: null)
    {
    }

    /// <summary>
    /// Seam interno de teste: permite injectar o transporte HTTP e/ou
    /// um Coordinator pré-configurado para exercitar o pipeline real
    /// (matcher + ownership + apply) sem tocar a rede. Não exposto em
    /// DI. <paramref name="configLoader"/> permite injectar a releitura
    /// de config sem tocar em <c>wtelegram.config</c>.
    /// </summary>
    internal ScheduledDispatcharrSyncAction(
        DispatcharrConfig config,
        ScheduledActionOptions options,
        CatalogResolver? catalog,
        HttpMessageHandler? transport,
        Func<DispatcharrConfig>? configLoader = null,
        DispatcharrSyncCoordinator? coordinator = null)
    {
        _config = config;
        _outputDir = options.OutputDir;
        _catalog = catalog;
        _transport = transport;
        _configLoader = configLoader;
        // Quando não há configLoader explícito, fornecemos um loader que
        // devolve o snapshot injectado em vez do loader default do
        // Coordinator (que lê wtelegram.config do disco). Isto preserva
        // o comportamento de testes e de runners que constroem a acção
        // directamente sem DI.
        Func<DispatcharrConfig> effectiveConfigLoader = configLoader ?? ((Func<DispatcharrConfig>)(() => config));
        _coordinator = coordinator ?? new DispatcharrSyncCoordinator(
            configLoader: effectiveConfigLoader,
            catalogFactory: null,
            transport: transport);
    }

    public string Name => ActionName;

    public string Description =>
        "Sincroniza <output-dir>/playlist.m3u com o Dispatcharr via DispatcharrSyncCoordinator. Respeita dispatcharr_enabled e dispatcharr_dry_run; decisões ambíguas nunca são aplicadas automaticamente. Só chama a API Dispatcharr se activo e fora de dry-run.";

    /// <summary>
    /// Sincronização requer Dispatcharr activo e válido (quando activado).
    /// Não depende do Telegram.
    /// </summary>
    public ScheduledActionCapabilities RequiredCapabilities =>
        ScheduledActionCapabilities.Dispatcharr;

    public async Task<string> ExecuteAsync(CancellationToken cancellationToken)
    {
        // W-REVIEW-04 / D6-A + D8-A — Sequência de decisões:
        //
        // 1. Releitura de config (paridade com caminho CLI). Sem loader
        //    explícito mantém-se o snapshot injectado.
        // 2. Gate `dispatcharr-disabled`: tem de ser avaliado ANTES do
        //    `no-playlist` para preservar o contract observável (a ordem
        //    disabled → no-playlist é parte de LastResult). Este check é
        //    apenas uma leitura de `cfg.Enabled` — não reimplementa o gate,
        //    apenas preserva a ordem. A execução real (e a sua terminação
        //    precoce) acontece no Coordinator.
        // 3. Check `no-playlist` (file exists).
        // 4. Construção da DispatcharrSourceSelection (paridade manual).
        // 5. Delegação no Coordinator (único owner do gate de execução,
        //    dry-run, RunId, error propagation, etc.).
        var playlistPath = Path.Combine(_outputDir, FunctionalPlaylistFileName);
        var config = _configLoader?.Invoke() ?? _config;
        if (!config.Enabled)
        {
            return "dispatcharr-disabled";
        }

        if (!File.Exists(playlistPath))
        {
            return "no-playlist";
        }

        // Construir a selecção de fontes (paridade com o caminho manual).
        // O Coordinator recebe-a como parâmetro — a "responsabilidade
        // correcta" desta construção é do scheduler (que tem acesso ao
        // catalog + playlist); não duplicamos aqui a lógica nem no
        // Coordinator.
        var selection = await BuildDispatcharrSelectionAsync(playlistPath, cancellationToken);
        if (selection is null && _catalog is not null)
        {
            Console.WriteLine(
                "ℹ️ Dispatcharr sync agendado: selecção de fontes não aplicada " +
                "(sem catálogo/política aplicável); apply em modo legacy.");
        }

        // Delegar no Coordinator. Aqui começa a execução Dispatcharr
        // propriamente dita — gate de execução, dry-run, RunId, error
        // propagation vivem todos no Coordinator (único owner). Esta
        // delegação cumpre D6-A (scheduler não constrói
        // DispatcharrSyncService) e D8-A (gate de execução vive no
        // Coordinator).
        //
        // Quando o scheduler corre sem catalog (modo legacy), pede
        // `allowLegacyWithoutCatalog=true` para preservar o comportamento
        // documentado em 03G/03E (sem catalog ⇒ modo legacy, sem abort).
        var outcome = await _coordinator.RunAsync(
            playlistPath,
            _outputDir,
            _catalog,
            selection,
            liveRunProgress: null,
            cancellationToken,
            allowLegacyWithoutCatalog: _catalog is null).ConfigureAwait(false);

        return MapOutcomeToString(outcome, selection);
    }

    /// <summary>
    /// Converte <see cref="DispatcharrSyncOutcome"/> no contract string
    /// preservado pelo scheduler (<c>LastResult</c>).
    /// </summary>
    private static string MapOutcomeToString(
        DispatcharrSyncOutcome outcome,
        DispatcharrSourceSelection? selection)
    {
        switch (outcome.Status)
        {
            case DispatcharrSyncStatus.Disabled:
                return "dispatcharr-disabled";
            case DispatcharrSyncStatus.CatalogUnavailable:
                return $"error:catalog-unavailable:{(outcome.ErrorType ?? "unknown")}";
            case DispatcharrSyncStatus.Failed:
                return $"error:{outcome.ErrorType ?? "unknown"}";
            case DispatcharrSyncStatus.Succeeded:
                var counts = outcome.Report?.Report?.Counts;
                var selectionToken = selection is null ? "none" : "applied";
                return $"newChannels={counts?.NewChannels ?? 0} newStreams={counts?.NewStreams ?? 0} matched={counts?.Matched ?? 0} ambiguous={counts?.Ambiguous ?? 0} dryRun={outcome.Report?.Report is not null} selection={selectionToken}";
            default:
                return $"error:unknown-status:{outcome.Status}";
        }
    }

    /// <summary>
    /// Constrói o artefacto <see cref="DispatcharrSourceSelection"/> a partir
    /// da playlist funcional, usando a política persistida e o selector único
    /// (<see cref="SourceSelectionStage"/>). Devolve <c>null</c> quando não há
    /// selecção aplicável (sem catálogo, leitura falhada ou stage no-op), o
    /// que corresponde exactamente ao contrato do caminho manual.
    /// </summary>
    private async Task<DispatcharrSourceSelection?> BuildDispatcharrSelectionAsync(
        string playlistPath,
        CancellationToken cancellationToken)
    {
        if (_catalog is null)
        {
            return null;
        }

        IReadOnlyList<M3uStream> streams;
        try
        {
            var discovered = await PlaylistReader
                .ReadAsync(playlistPath, defaultProvider: null, ct: cancellationToken)
                .ConfigureAwait(false);
            streams = discovered.Select(d => d.Original).ToList();
        }
        catch (Exception)
        {
            return null;
        }

        try
        {
            var policies = await new SourceSelectionPolicyResolver(_catalog)
                .LoadEffectivePoliciesAsync(cancellationToken)
                .ConfigureAwait(false);
            var stage = await new SourceSelectionStage(_catalog)
                .ApplyAsync(streams, policies, cancellationToken)
                .ConfigureAwait(false);
            if (!stage.Applied)
            {
                return null;
            }

            return DispatcharrSourceSelectionFactory.FromStageResult(stage, policies, DateTime.UtcNow);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
