using System;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services.Automation;

namespace m3uCrawler.Services.Configuration;

/// <summary>
/// Gate de prontidão por capacidade. Complementa o gate global de
/// bootstrap (<see cref="IConfigurationGate"/>, que decide se o lifecycle
/// está <c>READY</c>) com a verificação dos componentes concretos que
/// cada acção agendada exige.
///
/// <para>
/// Motivação: o <see cref="ConfigurationGate"/> composto com
/// <see cref="OperationalReadinessService"/> bloqueia tudo quando a
/// sessão Telegram não está autenticada. Isso impede acções que não
/// dependem do Telegram (discovery HTTP, sync, validação, geração de
/// playlist) de correr. Este gate remove esse bloqueio global e
/// aplica-o apenas às acções que declaram
/// <see cref="ScheduledActionCapabilities.Telegram"/>.
/// </para>
/// </summary>
public interface IActionCapabilityGate
{
    Task<bool> IsReadyForAsync(
        ScheduledActionCapabilities required,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementação apoiada no snapshot de prontidão operacional. É
/// fail-safe (excepção ⇒ não pronto). Uma instalação grandfathered
/// (<c>AdoptedFromLegacy</c>) é considerada pronta para todas as
/// capacidades, consistente com <see cref="OperationalReadinessService"/>.
/// </summary>
public sealed class ActionCapabilityGate : IActionCapabilityGate
{
    private readonly Func<CancellationToken, Task<OperationalReadinessSnapshot>> _evaluate;

    public ActionCapabilityGate(
        Func<CancellationToken, Task<OperationalReadinessSnapshot>> evaluate)
    {
        _evaluate = evaluate ?? throw new ArgumentNullException(nameof(evaluate));
    }

    public ActionCapabilityGate(OperationalReadinessService readiness)
        : this((readiness ?? throw new ArgumentNullException(nameof(readiness))).EvaluateAsync)
    {
    }

    public async Task<bool> IsReadyForAsync(
        ScheduledActionCapabilities required,
        CancellationToken cancellationToken = default)
    {
        if (required == ScheduledActionCapabilities.None)
        {
            return true;
        }

        OperationalReadinessSnapshot snapshot;
        try
        {
            snapshot = await _evaluate(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }

        if (snapshot is null)
        {
            return false;
        }

        if (snapshot.AdoptedFromLegacy)
        {
            return true;
        }

        if (required.HasFlag(ScheduledActionCapabilities.Telegram)
            && !snapshot.TelegramAuthenticated)
        {
            return false;
        }

        if (required.HasFlag(ScheduledActionCapabilities.Dispatcharr)
            && snapshot.DispatcharrEnabled
            && !snapshot.DispatcharrValid)
        {
            return false;
        }

        if (required.HasFlag(ScheduledActionCapabilities.Catalog) && !snapshot.CatalogOk)
        {
            return false;
        }

        if (required.HasFlag(ScheduledActionCapabilities.Output) && !snapshot.OutputOk)
        {
            return false;
        }

        return true;
    }
}
