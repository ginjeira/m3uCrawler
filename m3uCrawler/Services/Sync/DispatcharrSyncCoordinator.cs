using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Dispatcharr;
using m3uCrawler.Services.LiveRun;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;

namespace m3uCrawler.Services.Sync;

/// <summary>
/// Resultado de uma tentativa de sincronização Dispatcharr.
/// <see cref="Status"/> é o contrato estável; <see cref="ErrorType"/> é
/// apenas o tipo da excepção (nunca a mensagem, que pode conter segredos).
/// <see cref="Report"/> é o resultado detalhado (apenas quando
/// <see cref="Status"/> é <see cref="DispatcharrSyncStatus.Succeeded"/>);
/// pode ser <c>null</c> em <c>Disabled</c> ou <c>CatalogUnavailable</c>.
/// </summary>
public enum DispatcharrSyncStatus
{
    Disabled = 0,
    CatalogUnavailable = 1,
    Succeeded = 2,
    Failed = 3,
}

public sealed record DispatcharrSyncOutcome(
    DispatcharrSyncStatus Status,
    string? ErrorType = null,
    DispatcharrSyncResult? Report = null);

/// <summary>
/// Application service único que aplica a playlist publicada ao
/// Dispatcharr (P15). Substitui a lógica que vivia em
/// <c>Program.TrySyncToDispatcharrAsync</c>, para que CLI, ciclo de
/// manutenção, dashboard e scheduler usem exactamente o mesmo caminho.
///
/// <para>
/// A decisão de <c>dry_run</c> pertence ao <see cref="DispatcharrSyncService"/>
/// (config injectada); este coordenador nunca força nem ignora esse
/// flag. Sem integração activa devolve <see cref="DispatcharrSyncStatus.Disabled"/>
/// e não toca a rede.
/// </para>
///
/// <para>
/// O catálogo é obrigatório para o modo canónico (ownership). Quando
/// não é conhecido, pode ser resolvido tardiamente através de
/// <c>catalogFactory</c>; se continuar indisponível, a sincronização é
/// abortada <b>antes</b> de qualquer escrita HTTP e reportada como
/// <see cref="DispatcharrSyncStatus.CatalogUnavailable"/> — a playlist
/// publicada permanece válida.
/// </para>
/// </summary>
public sealed class DispatcharrSyncCoordinator
{
    private readonly Func<DispatcharrConfig> _configLoader;
    private readonly Func<CancellationToken, Task<CatalogResolver>>? _catalogFactory;
    private readonly HttpMessageHandler? _transport;

    public DispatcharrSyncCoordinator(
        Func<DispatcharrConfig>? configLoader = null,
        Func<CancellationToken, Task<CatalogResolver>>? catalogFactory = null,
        HttpMessageHandler? transport = null)
    {
        _configLoader = configLoader ?? DispatcharrConfigLoader.Load;
        _catalogFactory = catalogFactory;
        _transport = transport;
    }

    public Task<DispatcharrSyncOutcome> RunAsync(
        string playlistPath,
        string outputDir,
        CatalogResolver? catalog,
        DispatcharrSourceSelection? selection,
        ILiveRunProgress? liveRunProgress,
        CancellationToken cancellationToken = default,
        bool allowLegacyWithoutCatalog = false)
        => RunAsync(playlistPath, outputDir, catalog, selection, liveRunProgress,
            forceDryRun: null, cancellationToken, allowLegacyWithoutCatalog);

    /// <summary>
    /// W-API-DISPATCHARR-HTTP-IMPLEMENTATION (DL-128): overload que aceita
    /// um override explícito de <c>dry_run</c>. Usado pelos endpoints HTTP
    /// <c>/api/dispatcharr/dry-run</c> e <c>/api/dispatcharr/sync</c>:
    /// <list type="bullet">
    ///   <item><c>forceDryRun=true</c> → endpoint <c>/dry-run</c></item>
    ///   <item><c>forceDryRun=false</c> → endpoint <c>/sync</c> (override
    ///         da flag <c>dispatcharr_dry_run</c> da configuração
    ///         persistida para esta chamada concreta).</item>
    ///   <item><c>forceDryRun=null</c> → comportamento legado (usa
    ///         <c>cfg.DryRun</c>); preserva o caminho do scheduler,
    ///         Program.cs e RunPublicationService.</item>
    /// </list>
    /// </summary>
    public Task<DispatcharrSyncOutcome> RunAsync(
        string playlistPath,
        string outputDir,
        CatalogResolver? catalog,
        DispatcharrSourceSelection? selection,
        ILiveRunProgress? liveRunProgress,
        bool? forceDryRun,
        CancellationToken cancellationToken = default,
        bool allowLegacyWithoutCatalog = false)
    {
        // O override é aplicado a nível do gate antes da pipeline. Quando
        // forceDryRun tem valor, carregamos uma config clonada com o
        // DryRun substituído — sem mutar o estado global.
        if (forceDryRun.HasValue)
        {
            return RunWithDryRunOverrideAsync(
                playlistPath, outputDir, catalog, selection, liveRunProgress,
                forceDryRun.Value, cancellationToken, allowLegacyWithoutCatalog);
        }

        return RunCoreAsync(
            playlistPath, outputDir, catalog, selection, liveRunProgress,
            cfgOverride: null, cancellationToken, allowLegacyWithoutCatalog);
    }

    private Task<DispatcharrSyncOutcome> RunWithDryRunOverrideAsync(
        string playlistPath,
        string outputDir,
        CatalogResolver? catalog,
        DispatcharrSourceSelection? selection,
        ILiveRunProgress? liveRunProgress,
        bool forceDryRun,
        CancellationToken cancellationToken,
        bool allowLegacyWithoutCatalog)
    {
        var cfg = _configLoader();
        var overridden = new DispatcharrConfig
        {
            Enabled = cfg.Enabled,
            BaseUrl = cfg.BaseUrl,
            ApiKey = cfg.ApiKey,
            Username = cfg.Username,
            Password = cfg.Password,
            DryRun = forceDryRun,
            MatchThreshold = cfg.MatchThreshold,
            AliasFile = cfg.AliasFile,
            ProviderPriority = cfg.ProviderPriority,
            AutoCreateGroups = cfg.AutoCreateGroups,
            TargetGroupName = cfg.TargetGroupName,
        };
        return RunCoreAsync(
            playlistPath, outputDir, catalog, selection, liveRunProgress,
            cfgOverride: overridden, cancellationToken, allowLegacyWithoutCatalog);
    }

    private async Task<DispatcharrSyncOutcome> RunCoreAsync(
        string playlistPath,
        string outputDir,
        CatalogResolver? catalog,
        DispatcharrSourceSelection? selection,
        ILiveRunProgress? liveRunProgress,
        DispatcharrConfig? cfgOverride,
        CancellationToken cancellationToken,
        bool allowLegacyWithoutCatalog)
    {
        var cfg = cfgOverride ?? _configLoader();
        if (!cfg.Enabled)
        {
            if (liveRunProgress is not null)
            {
                liveRunProgress.ReportCounts(counts => counts.DispatcharrSyncSkipped++);
                liveRunProgress.ReportActivity(
                    LiveRunActivityCategory.Dispatcharr,
                    LiveRunActivityLevel.Info,
                    "dispatcharr sync skipped (disabled)");
            }
            return new DispatcharrSyncOutcome(DispatcharrSyncStatus.Disabled);
        }

        if (liveRunProgress is not null)
        {
            await liveRunProgress.EnterPhaseAsync(
                LiveRunPhase.SyncingDispatcharr, "syncing dispatcharr", cancellationToken)
                .ConfigureAwait(false);
            liveRunProgress.ReportCounts(counts => counts.DispatcharrSyncAttempted++);
        }

        var effectiveCatalog = catalog;
        if (effectiveCatalog is null && _catalogFactory is not null)
        {
            try
            {
                effectiveCatalog = await _catalogFactory(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                liveRunProgress?.ReportCounts(counts => counts.DispatcharrSyncFailed++);
                liveRunProgress?.ReportActivity(
                    LiveRunActivityCategory.Dispatcharr,
                    LiveRunActivityLevel.Error,
                    "dispatcharr sync failed: catalog unavailable");
                Console.WriteLine(
                    $"❌ Catálogo indisponível para sincronização Dispatcharr. " +
                    $"Sincronização abortada antes de qualquer escrita HTTP. " +
                    $"Erro: {ex.GetType().Name}");
                return new DispatcharrSyncOutcome(
                    DispatcharrSyncStatus.CatalogUnavailable, ex.GetType().Name);
            }
        }

        if (effectiveCatalog is null)
        {
            // W-REVIEW-04 — compatibilidade com o caminho scheduler legacy.
            // Quando o caller (scheduler, em modo legacy) pede
            // explicitamente allowLegacyWithoutCatalog=true e não há
            // catalog, NÃO abortamos: continuamos em modo legacy (sem
            // ownership), exactamente como o scheduler fazia antes do
            // refactor D6-A. Para os outros caminhos (Program.cs,
            // RunPublicationService), o default false preserva o
            // comportamento existente (CatalogUnavailable).
            if (allowLegacyWithoutCatalog)
            {
                try
                {
                    var aliases = AliasResolver.FromFile(cfg.AliasFile);
                    var ordering = new StreamOrderingPolicy(cfg.ProviderPriority);
                    var matcher = new ChannelMatcher(aliases, null, effectiveCatalog);
                    DispatcharrSyncService sync;
                    if (_transport != null)
                    {
                        var built = DispatcharrClientFactory.BuildWithTransport(
                            cfg.BaseUrl, cfg.ApiKey, cfg.Username, cfg.Password, _transport);
                        sync = new DispatcharrSyncService(
                            cfg, outputDir,
                            aliases: aliases,
                            ordering: ordering,
                            matcher: matcher,
                            http: built.Http,
                            auth: built.Auth,
                            login: built.Login,
                            channels: built.Channels,
                            streams: built.Streams,
                            m3u: built.M3U,
                            catalog: effectiveCatalog);
                    }
                    else
                    {
                        sync = new DispatcharrSyncService(
                            cfg, outputDir,
                            aliases: aliases,
                            ordering: ordering,
                            matcher: matcher,
                            catalog: effectiveCatalog);
                    }
                    var syncResult = await sync.RunAsync(playlistPath, selection, cancellationToken).ConfigureAwait(false);

                    if (liveRunProgress is not null)
                    {
                        liveRunProgress.ReportCounts(counts => counts.DispatcharrSyncCompleted++);
                        liveRunProgress.ReportActivity(
                            LiveRunActivityCategory.Dispatcharr,
                            LiveRunActivityLevel.Info,
                            "dispatcharr sync completed (legacy)");
                    }
                    return new DispatcharrSyncOutcome(
                        DispatcharrSyncStatus.Succeeded,
                        ErrorType: null,
                        Report: syncResult);
                }
                catch (Exception ex)
                {
                    if (liveRunProgress is not null)
                    {
                        liveRunProgress.ReportCounts(counts => counts.DispatcharrSyncFailed++);
                        liveRunProgress.ReportActivity(
                            LiveRunActivityCategory.Dispatcharr,
                            LiveRunActivityLevel.Error,
                            "dispatcharr sync failed (legacy)");
                    }
                    Console.WriteLine($"⚠️ Falha na sincronização Dispatcharr (legacy): {ex.Message}");
                    return new DispatcharrSyncOutcome(DispatcharrSyncStatus.Failed, ex.GetType().Name);
                }
            }

            liveRunProgress?.ReportCounts(counts => counts.DispatcharrSyncFailed++);
            liveRunProgress?.ReportActivity(
                LiveRunActivityCategory.Dispatcharr,
                LiveRunActivityLevel.Error,
                "dispatcharr sync failed: catalog unavailable");
            Console.WriteLine(
                "❌ Catálogo canónico indisponível. Sincronização Dispatcharr abortada " +
                "antes de qualquer escrita HTTP.");
            return new DispatcharrSyncOutcome(DispatcharrSyncStatus.CatalogUnavailable);
        }

        try
        {
            var aliases = AliasResolver.FromFile(cfg.AliasFile);
            var ordering = new StreamOrderingPolicy(cfg.ProviderPriority);
            var matcher = new ChannelMatcher(aliases, null, effectiveCatalog);
            DispatcharrSyncService sync;
            if (_transport != null)
            {
                var built = DispatcharrClientFactory.BuildWithTransport(
                    cfg.BaseUrl, cfg.ApiKey, cfg.Username, cfg.Password, _transport);
                sync = new DispatcharrSyncService(
                    cfg, outputDir,
                    aliases: aliases,
                    ordering: ordering,
                    matcher: matcher,
                    http: built.Http,
                    auth: built.Auth,
                    login: built.Login,
                    channels: built.Channels,
                    streams: built.Streams,
                    m3u: built.M3U,
                    catalog: effectiveCatalog);
            }
            else
            {
                sync = new DispatcharrSyncService(
                    cfg, outputDir,
                    aliases: aliases,
                    ordering: ordering,
                    matcher: matcher,
                    catalog: effectiveCatalog);
            }
            var syncResult = await sync.RunAsync(playlistPath, selection, cancellationToken).ConfigureAwait(false);

            if (liveRunProgress is not null)
            {
                liveRunProgress.ReportCounts(counts => counts.DispatcharrSyncCompleted++);
                liveRunProgress.ReportActivity(
                    LiveRunActivityCategory.Dispatcharr,
                    LiveRunActivityLevel.Info,
                    "dispatcharr sync completed");
            }
            return new DispatcharrSyncOutcome(
                DispatcharrSyncStatus.Succeeded,
                ErrorType: null,
                Report: syncResult);
        }
        catch (Exception ex)
        {
            if (liveRunProgress is not null)
            {
                liveRunProgress.ReportCounts(counts => counts.DispatcharrSyncFailed++);
                liveRunProgress.ReportActivity(
                    LiveRunActivityCategory.Dispatcharr,
                    LiveRunActivityLevel.Error,
                    "dispatcharr sync failed");
            }
            Console.WriteLine($"⚠️ Falha na sincronização Dispatcharr: {ex.Message}");
            return new DispatcharrSyncOutcome(DispatcharrSyncStatus.Failed, ex.GetType().Name);
        }
    }
}
