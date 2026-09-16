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
/// O objectivo desta subwave é existir um único caminho de
/// invocação. O wiring concreto com o pipeline Telegram é
/// completado na subwave 3 (instrumentação). Aqui o coordinator
/// já consegue iniciar/parar/recovery sem alterar
/// <c>TelegramScraperService</c>.
/// </para>
/// </summary>
public sealed class TelegramRunPipeline : IRunPipeline
{
    private readonly Func<LiveRunRequest, CancellationToken, Task> _invoker;

    public TelegramRunPipeline(Func<LiveRunRequest, CancellationToken, Task> invoker)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
    }

    public Task ExecuteAsync(LiveRunRequest request, CancellationToken cancellationToken) =>
        _invoker(request, cancellationToken);
}
