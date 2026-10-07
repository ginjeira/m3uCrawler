using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;

namespace m3uCrawler.Services.LiveRun;

/// <summary>
/// PHASE 9C.4 — Ponto de observação do pipeline Telegram para o
/// Live Run Monitor.
///
/// <para>
/// A pipeline (<c>TelegramScraperService.SearchAndTestM3UInTelegramAsync</c>
/// e <c>Program.RunTelegramMaintenanceCycle</c>) reporta aqui as
/// transições de fase e os contadores, nos mesmos pontos onde já
/// actualiza o <see cref="RunReport"/> autoritativo. A instrumentação
/// é estritamente aditiva: com <c>null</c> (default em todos os
/// call-sites) o comportamento é exactamente o actual.
/// </para>
///
/// <para>
/// <b>Não duplicar contagens:</b> <see cref="ReportCounts(RunReport)"/>
/// copia os contadores já existentes do relatório activo; não os
/// recalcula nem infere a partir de strings/logs.
/// </para>
/// </summary>
public interface ILiveRunProgress
{
    /// <summary>GUID do run a que este progresso pertence.</summary>
    string RunId { get; }

    /// <summary>
    /// Entra numa fase. Transições repetidas ou retroactivas são
    /// ignoradas (o timeline é monotónico). Persiste o
    /// <c>LiveRunStepEntity</c> da nova fase e finaliza o anterior.
    /// </summary>
    Task EnterPhaseAsync(
        Catalog.LiveRunPhase phase,
        string? message = null,
        CancellationToken cancellationToken = default);

    /// <summary>Actualiza os totalizadores em memória (mutação síncrona).</summary>
    void ReportCounts(Action<LiveRunCounts> mutate);

    /// <summary>Espelha os contadores autoritativos do relatório activo.</summary>
    void ReportCounts(RunReport report);

    /// <summary>Actualiza a última mensagem sanitizada (sem transição de fase).</summary>
    void ReportMessage(string message);

    /// <summary>Publica uma actividade no feed limitado em memória.</summary>
    void ReportActivity(
        LiveRunActivityCategory category,
        LiveRunActivityLevel level,
        string message,
        IReadOnlyDictionary<string, string>? metadata = null);
}

/// <summary>
/// PHASE 9C.4 — Implementação no-op de <see cref="ILiveRunProgress"/>.
/// Usada quando nenhum monitor está associado (testes, caminhos sem
/// catálogo), garantindo zero efeitos colaterais.
/// </summary>
public sealed class NullLiveRunProgress : ILiveRunProgress
{
    public static readonly NullLiveRunProgress Instance = new();

    private NullLiveRunProgress() { }

    public string RunId => string.Empty;

    public Task EnterPhaseAsync(
        Catalog.LiveRunPhase phase,
        string? message = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void ReportCounts(Action<LiveRunCounts> mutate) { }

    public void ReportCounts(RunReport report) { }

    public void ReportMessage(string message) { }

    public void ReportActivity(
        LiveRunActivityCategory category,
        LiveRunActivityLevel level,
        string message,
        IReadOnlyDictionary<string, string>? metadata = null) { }
}

/// <summary>
/// PHASE 9C.4 — Contrato pelo qual o <see cref="RunCoordinator"/>
/// injecta o monitor de execução numa pipeline concreta. Mantém
/// <see cref="IRunPipeline"/> intacto (os pipelines existentes
/// continuam a compilar sem alterações).
/// </summary>
public interface ILiveRunProgressAware
{
    ILiveRunProgress? Progress { get; set; }
}
