using System;
using System.Threading;
using System.Threading.Tasks;

namespace m3uCrawler.Services.LiveRun;

/// <summary>
/// PHASE 9C.4 — Implementação concreta do
/// <see cref="IRunPipeline"/> que delega numa função de
/// invocação do pipeline Telegram fornecida pelo caller.
///
/// <para>
/// A função é responsável por invocar
/// <c>TelegramScraperService.SearchAndTestM3UInTelegramAsync</c>
/// (modo <see cref="LiveRunMode.Telegram"/>) ou
/// <c>RunTelegramMaintenanceCycle</c> (modo
/// <see cref="LiveRunMode.TelegramMaintain"/>). O coordinator
/// permanece agnóstico a esse detalhe: tudo o que precisa é
/// de uma função que realize o trabalho e devolva.
/// </para>
///
/// <para>
/// O pipeline é <see cref="ILiveRunProgressAware"/>: o
/// <see cref="RunCoordinator"/> injecta o monitor da execução antes
/// de invocar <see cref="ExecuteAsync"/>, e a função delegada
/// recebe-o para reportar fases, contadores e actividades nos pontos
/// reais da pipeline. Sem monitor, a função recebe <c>null</c> e o
/// comportamento é exactamente o actual.
/// </para>
/// </summary>
public sealed class TelegramRunPipeline : IRunPipeline, ILiveRunProgressAware
{
    private readonly Func<LiveRunRequest, ILiveRunProgress?, CancellationToken, Task> _invoker;

    public TelegramRunPipeline(
        Func<LiveRunRequest, ILiveRunProgress?, CancellationToken, Task> invoker)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
    }

    /// <summary>
    /// Monitor injectado pelo <see cref="RunCoordinator"/>. <c>null</c>
    /// significa "sem instrumentação".
    /// </summary>
    public ILiveRunProgress? Progress { get; set; }

    public Task ExecuteAsync(LiveRunRequest request, CancellationToken cancellationToken) =>
        _invoker(request, Progress, cancellationToken);
}
