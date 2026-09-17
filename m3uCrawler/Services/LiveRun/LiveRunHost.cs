using System;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace m3uCrawler.Services.LiveRun;

/// <summary>
/// PHASE 9C.4 — Host process-wide que faz a ponte entre o
/// <see cref="RunCoordinator"/> (único, partilhado por CLI, scheduler e
/// futura API) e os entry points que o invocam.
///
/// <para>
/// A factory de pipelines não é conhecida no momento da construção do
/// coordinator (e.g. o <c>scraper</c> só existe quando <c>--telegram</c>
/// é passado). O host resolve isto com <see cref="ConfigureExecutor"/>:
/// a função é registada tardiamente e o coordinator passa a poder
/// construir pipelines. Se o executor ainda não foi registado quando a
/// pipeline for pedida, devolve <see cref="LiveRunPipelineNotConfiguredException"/>
/// (a dashboard mapeia em 503).
/// </para>
///
/// <para>
/// Existe <b>um</b> <see cref="LiveRunHost"/> por processo. Em testes,
/// o isolamento é feito pelo <c>StaticLiveRunHostScope</c> do
/// <c>WebDashboardService</c>.
/// </para>
/// </summary>
public sealed class LiveRunHost
{
    private readonly IDbContextFactory<Catalog.ChannelCatalogDbContext> _dbFactory;
    private readonly ILogger<LiveRunHost> _logger;

    private readonly object _gate = new();
    private RunCoordinator? _coordinator;
    private Func<LiveRunRequest, IRunPipeline>? _executor;

    public LiveRunHost(
        IDbContextFactory<Catalog.ChannelCatalogDbContext> dbFactory,
        ILogger<LiveRunHost>? logger = null)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _logger = logger ?? NullLogger<LiveRunHost>.Instance;
    }

    /// <summary>Coordinator único, ou <c>null</c> se ainda não configurado.</summary>
    public RunCoordinator? Coordinator
    {
        get { lock (_gate) return _coordinator; }
    }

    /// <summary>
    /// Verdadeiro se houver um executor de pipeline registado
    /// (i.e. o <c>scraper</c> foi construído, normalmente via <c>--telegram</c>).
    /// </summary>
    public bool PipelineConfigured
    {
        get { lock (_gate) return _executor != null; }
    }

    /// <summary>
    /// Regista o executor de pipeline. Idempotente: a segunda chamada
    /// substitui a referência (útil em testes). Cria o
    /// <see cref="RunCoordinator"/> na primeira invocação; as
    /// seguintes reutilizam o mesmo coordinator.
    /// </summary>
    public RunCoordinator ConfigureExecutor(Func<LiveRunRequest, IRunPipeline> executor)
    {
        ArgumentNullException.ThrowIfNull(executor);
        lock (_gate)
        {
            _executor = executor;
            if (_coordinator is null)
            {
                _coordinator = new RunCoordinator(_dbFactory, BuildPipeline, null);
            }
            return _coordinator;
        }
    }

    /// <summary>
    /// Fábrica usada pelo <see cref="RunCoordinator"/>: lê o executor
    /// actual sob lock para evitar um snapshot stale quando o executor
    /// é (re)registado entre runs. Se não houver executor, devolve
    /// <see cref="LiveRunPipelineNotConfiguredException"/>.
    /// </summary>
    private IRunPipeline BuildPipeline(LiveRunRequest request)
    {
        Func<LiveRunRequest, IRunPipeline>? executor;
        lock (_gate)
        {
            executor = _executor;
        }
        if (executor is null)
        {
            throw new LiveRunPipelineNotConfiguredException();
        }
        return executor(request);
    }
}
