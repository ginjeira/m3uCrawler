using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services.Configuration;

namespace m3uCrawler.Services.LiveRun;

/// <summary>
/// Resultado da discovery Telegram entregue ao executor: streams
/// funcionais, streams adquiridos (playlists funcionais para o país) e o
/// <see cref="RunReport"/> autoritativo.
/// </summary>
public sealed record TelegramDiscoveryResult(
    List<M3uStream> Streams,
    RunReport Report,
    List<M3uStream>? AcquiredStreams = null);

/// <summary>
/// Executa a discovery Telegram. Na produção é o
/// <c>TelegramScraperService.SearchAndTestM3UInTelegramAsync</c> (com o
/// <c>PipelineIngestionService</c> associado); em testes é um fake.
/// </summary>
public delegate Task<TelegramDiscoveryResult> TelegramDiscoveryDelegate(
    DiscoverySettings discovery,
    ILiveRunProgress? progress,
    CancellationToken cancellationToken);

/// <summary>
/// Executa o ciclo de manutenção (merge/retest) já com a publicação
/// delegada em <see cref="IRunPublicationService"/>.
/// </summary>
public delegate Task TelegramMaintenanceDelegate(
    DiscoverySettings discovery,
    ILiveRunProgress? progress,
    CancellationToken cancellationToken);

/// <summary>
/// Executor único de uma execução Telegram (Live Run) para o modo
/// <see cref="LiveRunMode.Telegram"/>. Faz discovery e delega a cauda
/// completa — country gate, selecção, publicação, relatório, histórico e
/// Dispatcharr — no <see cref="IRunPublicationService"/> partilhado.
///
/// <para>
/// É este executor que a CLI (quando existe <c>--web</c>), o
/// <c>POST /api/run/start</c> do dashboard e o
/// <c>ScheduledTelegramRunAction</c> usam; não existe um segundo
/// pipeline. O modo <see cref="LiveRunMode.TelegramMaintain"/> continua a
/// passar pelo ciclo de manutenção, que também publica pelo mesmo
/// serviço.
/// </para>
///
/// <para>
/// As settings de discovery são relidas por execução via
/// <see cref="DiscoverySettingsProvider"/> (Wave C): uma edição no
/// dashboard aplica-se à execução seguinte sem reiniciar o processo.
/// Quando não há provider (testes), usam-se defaults com os overrides
/// do pedido.
/// </para>
/// </summary>
public sealed class TelegramLiveRunExecutor : IRunPipeline, ILiveRunProgressAware
{
    private readonly DiscoverySettingsProvider? _discoverySettings;
    private readonly TelegramDiscoveryDelegate? _discover;
    private readonly TelegramMaintenanceDelegate? _maintain;
    private readonly IRunPublicationService _publication;
    private readonly string _countryCode;
    private readonly string? _domainFilter;

    public TelegramLiveRunExecutor(
        TelegramDiscoveryDelegate? discover,
        TelegramMaintenanceDelegate? maintain,
        IRunPublicationService publication,
        DiscoverySettingsProvider? discoverySettings = null,
        string countryCode = "pt",
        string? domainFilter = null)
    {
        _discover = discover;
        _maintain = maintain;
        _publication = publication ?? throw new ArgumentNullException(nameof(publication));
        _discoverySettings = discoverySettings;
        _countryCode = countryCode;
        _domainFilter = domainFilter;
    }

    public ILiveRunProgress? Progress { get; set; }

    public Task ExecuteAsync(LiveRunRequest request, CancellationToken cancellationToken)
        => ExecuteAsync(request, Progress, cancellationToken);

    public async Task ExecuteAsync(
        LiveRunRequest request,
        ILiveRunProgress? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var discovery = ResolveDiscovery(request);

        if (request.Mode == LiveRunMode.TelegramMaintain)
        {
            var maintain = _maintain
                ?? throw new LiveRunPipelineNotConfiguredException();
            await maintain(discovery, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        var discover = _discover
            ?? throw new LiveRunPipelineNotConfiguredException();
        var result = await discover(discovery, progress, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A discovery Telegram não devolveu resultado.");

        await _publication.PublishAsync(
            new RunPublicationRequest
            {
                Streams = result.Streams,
                AcquiredStreams = result.AcquiredStreams,
                Report = result.Report,
                Keyword = discovery.Keyword,
                HistoryHours = discovery.HistoryHours,
                MaxStreams = discovery.MaxStreams,
                DomainFilter = _domainFilter,
                CountryCode = _countryCode,
                HistoryMode = "TelegramSearch",
            },
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private DiscoverySettings ResolveDiscovery(LiveRunRequest request)
    {
        if (_discoverySettings is not null)
        {
            return _discoverySettings.Resolve(
                request.Keyword, request.HistoryHours, request.MaxStreams, request.MinHistoryHours);
        }

        return new DiscoverySettings().WithOverrides(
            request.Keyword, request.HistoryHours, request.MaxStreams, request.MinHistoryHours);
    }
}
