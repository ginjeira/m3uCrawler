using System;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services.LiveRun;

namespace m3uCrawler.Services.Automation;

/// <summary>
/// PHASE 9C.4 (subwave 5) — Acção agendada que arranca uma execução
/// operacional do Telegram através do <see cref="RunCoordinator"/>
/// único.
///
/// <para>
/// <b>Não cria um segundo pipeline nem um segundo scheduler.</b> O
/// runner existente (<see cref="ScheduledJobRunner"/>) resolve esta
/// acção pelo nome e invoca-a; a execução converge para o mesmo
/// <see cref="RunCoordinator.StartAsync"/> usado pela CLI, pelo que o
/// lock de execução é partilhado e uma corrida
/// scheduler/manual resulta numa única execução.
/// </para>
///
/// <para>
/// <b>Nomes de action (estáveis):</b>
/// <list type="bullet">
///   <item><c>telegramRun</c> → <see cref="LiveRunMode.Telegram"/></item>
///   <item><c>telegramMaintainRun</c> → <see cref="LiveRunMode.TelegramMaintain"/></item>
/// </list>
/// A UI calcula a <c>CronExpression</c>; não existe <c>StartAtUtc</c>.
/// </para>
///
/// <para>
/// <b>Configuração inválida é rejeitada de forma segura:</b> se o
/// coordinator ainda não estiver configurado (i.e. o processo corre
/// apenas <c>--web</c> sem <c>--telegram</c>) a acção devolve
/// <see cref="NotConfiguredResult"/> sem lançar. Se já houver um run
/// activo devolve <see cref="AlreadyRunningResult"/>.
/// </para>
/// </summary>
public sealed class ScheduledTelegramRunAction : IScheduledAction
{
    /// <summary>Nome estável para o ciclo Telegram normal.</summary>
    public const string TelegramActionName = "telegramRun";

    /// <summary>Nome estável para o ciclo Telegram com manutenção.</summary>
    public const string TelegramMaintainActionName = "telegramMaintainRun";

    /// <summary>Resultado quando o coordinator não está disponível.</summary>
    public const string NotConfiguredResult = "blocked:live-run-not-configured";

    /// <summary>Resultado quando já existe uma execução activa.</summary>
    public const string AlreadyRunningResult = "blocked:already-running";

    private readonly LiveRunHost? _host;
    private readonly LiveRunMode _mode;

    public ScheduledTelegramRunAction(LiveRunHost? host, LiveRunMode mode)
    {
        _host = host;
        _mode = mode;
    }

    public string Name => _mode == LiveRunMode.TelegramMaintain
        ? TelegramMaintainActionName
        : TelegramActionName;

    public async Task<string> ExecuteAsync(CancellationToken cancellationToken)
    {
        var coordinator = _host?.Coordinator;
        if (coordinator is null)
        {
            return NotConfiguredResult;
        }

        var request = new LiveRunRequest
        {
            Mode = _mode,
            // A origem identifica inequivocamente o scheduler. Este é o
            // único ponto que produz Source=Scheduler para uma LiveRun.
            Source = LiveRunSource.Scheduler,
        };

        LiveRunOutcome outcome;
        try
        {
            // StartAsync (e não KickStartAsync): o job agendado quer o
            // resultado terminal para o registar em LastResult, e o
            // runner é sequencial — não há benefício em desacoplar.
            outcome = await coordinator.StartAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (RunAlreadyInProgressException)
        {
            // Corrida scheduler/manual (ou scheduler/scheduler): o lock
            // único do coordinator garante que só uma execução corre.
            return AlreadyRunningResult;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Nunca propagar excepções (o runner registaria "error:..." mas
            // perderia o contexto de scheduler). Mensagem sem detalhes
            // sensíveis: apenas o tipo.
            return $"error:{ex.GetType().Name}";
        }

        var modeWire = _mode.ToWireName();
        return outcome.Succeeded
            ? $"live-run:{modeWire}:ok"
            : $"live-run:{modeWire}:failed";
    }
}
