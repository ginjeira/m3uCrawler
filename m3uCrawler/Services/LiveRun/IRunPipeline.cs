using System;
using System.Threading;
using System.Threading.Tasks;

namespace m3uCrawler.Services.LiveRun;

/// <summary>
/// PHASE 9C.4 — Abstração sobre o trabalho de uma execução
/// operacional do Telegram. Permite ao
/// <see cref="RunCoordinator"/> ser testado sem invocar a
/// pipeline real (HTTP, Telegram client, etc.).
///
/// <para>
/// O <see cref="RunCoordinator"/> é o único caller desta
/// interface — não há outros pontos de entrada a executar a
/// pipeline Telegram. As implementações concretas delegam em
/// <c>TelegramScraperService.SearchAndTestM3UInTelegramAsync</c>
/// / <c>RunTelegramMaintenanceCycle</c>, sem duplicar lógica.
/// </para>
/// </summary>
public interface IRunPipeline
{
    /// <summary>
    /// Executa um ciclo Telegram (com ou sem manutenção).
    /// Implementações NÃO devem alterar <see cref="LiveRunSnapshot"/>;
    /// apenas realizam o trabalho.
    /// </summary>
    Task ExecuteAsync(LiveRunRequest request, CancellationToken cancellationToken);
}
