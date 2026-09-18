using System;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.LiveRun;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;

namespace m3uCrawler.Services.Sync;

/// <summary>
/// Resultado de uma tentativa de sincronização Dispatcharr.
/// <see cref="Status"/> é o contrato estável; <see cref="ErrorType"/> é
/// apenas o tipo da excepção (nunca a mensagem, que pode conter segredos).
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
    string? ErrorType = null);

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

    public DispatcharrSyncCoordinator(
        Func<DispatcharrConfig>? configLoader = null,
        Func<CancellationToken, Task<CatalogResolver>>? catalogFactory = null)
    {
        _configLoader = configLoader ?? DispatcharrConfigLoader.Load;
        _catalogFactory = catalogFactory;
    }

    public async Task<DispatcharrSyncOutcome> RunAsync(
        string playlistPath,
        string outputDir,
        CatalogResolver? catalog,
        DispatcharrSourceSelection? selection,
        ILiveRunProgress? liveRunProgress,
        CancellationToken cancellationToken = default)
    {
        var cfg = _configLoader();
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
            var sync = new DispatcharrSyncService(
                cfg, outputDir,
                aliases: aliases,
                ordering: ordering,
                matcher: matcher,
                catalog: effectiveCatalog);
            await sync.RunAsync(playlistPath, selection, cancellationToken).ConfigureAwait(false);

            if (liveRunProgress is not null)
            {
                liveRunProgress.ReportCounts(counts => counts.DispatcharrSyncCompleted++);
                liveRunProgress.ReportActivity(
                    LiveRunActivityCategory.Dispatcharr,
                    LiveRunActivityLevel.Info,
                    "dispatcharr sync completed");
            }
            return new DispatcharrSyncOutcome(DispatcharrSyncStatus.Succeeded);
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
