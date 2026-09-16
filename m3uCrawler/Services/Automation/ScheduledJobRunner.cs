using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace m3uCrawler.Services.Automation;

/// <summary>
/// Acção executada por um <see cref="ScheduledJobEntity"/> quando
/// o tick chega. A implementação concreta decide o que fazer
/// (e.g. correr o TelegramDiscoveryService, ou o DispatcharrSyncService).
/// </summary>
public interface IScheduledAction
{
    string Name { get; }
    Task<string> ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// PHASE 12 — Runner que observa os <see cref="ScheduledJobEntity"/>
/// persistentes, calcula o próximo tick e dispara a acção registada.
/// Não invoca as acções directamente: usa o container DI para
/// resolver <see cref="IScheduledAction"/> por nome. Os resultados
/// são persistidos via <c>MarkScheduledJobRanAsync</c>.
/// </summary>
public sealed class ScheduledJobRunner : IDisposable
{
    public const string BlockedResult = "blocked:not-configured";

    private readonly IDbContextFactory<ChannelCatalogDbContext> _dbFactory;
    private readonly IServiceProvider _services;
    private readonly TimeSpan _pollInterval;
    private readonly IConfigurationGate? _gate;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _blockedLogged;

    public ScheduledJobRunner(
        IDbContextFactory<ChannelCatalogDbContext> dbFactory,
        IServiceProvider services,
        TimeSpan? pollInterval = null,
        IConfigurationGate? gate = null)
    {
        _dbFactory = dbFactory;
        _services = services;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(30);
        _gate = gate;
    }

    public void Start()
    {
        if (_loop != null) return;
        _loop = Task.Run(LoopAsync);
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _loop = null;
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await TickOnceAsync(_cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ ScheduledJobRunner tick: {ex.Message}");
            }

            try { await Task.Delay(_pollInterval, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Verifica todos os jobs activos e dispara aqueles cujo
    /// <c>NextRunAtUtc</c> é menor que <c>DateTime.UtcNow</c>.
    /// Útil também em testes para avançar o estado sem esperar pelo loop.
    /// </summary>
    public async Task<int> TickOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var due = await context.ScheduledJobs
            .Where(j => j.IsEnabled && j.NextRunAtUtc != null && j.NextRunAtUtc <= now)
            .ToListAsync(cancellationToken);

        // PHASE 9C.1 — Gate de configuração: em NOT_CONFIGURED/CONFIGURING
        // nenhum job automático executa. A decisão é centralizada no
        // IConfigurationGate (um único mecanismo, não espalhado).
        if (_gate != null && !await _gate.IsReadyAsync(cancellationToken))
        {
            await RecordBlockedAsync(due, now, cancellationToken);
            if (!_blockedLogged)
            {
                Console.WriteLine(
                    $"⛔ scheduler blocked: not configured (state={_gate.State.ToWireName()})");
                _blockedLogged = true;
            }
            return 0;
        }
        _blockedLogged = false;

        var ran = 0;
        foreach (var job in due)
        {
            var action = ResolveAction(job.ActionName);
            if (action == null)
            {
                await using var c2 = await _dbFactory.CreateDbContextAsync(cancellationToken);
                var job2 = await c2.ScheduledJobs.FirstAsync(j => j.Id == job.Id, cancellationToken);
                job2.LastResult = $"unknown-action:{job.ActionName}";
                job2.LastRunAtUtc = now;
                job2.NextRunAtUtc = CronExpression.Parse(job.CronExpression).NextOccurrence(now);
                job2.UpdatedAtUtc = now;
                await c2.SaveChangesAsync(cancellationToken);
                continue;
            }
            var started = DateTime.UtcNow;
            string result;
            try
            {
                result = await action.ExecuteAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                result = $"error:{ex.GetType().Name}:{ex.Message}";
            }
            var finished = DateTime.UtcNow;
            await using var c3 = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var job3 = await c3.ScheduledJobs.FirstAsync(j => j.Id == job.Id, cancellationToken);
            job3.LastRunAtUtc = started;
            job3.LastResult = Truncate(result, 120);
            job3.UpdatedAtUtc = finished;
            job3.NextRunAtUtc = CronExpression.Parse(job3.CronExpression).NextOccurrence(finished);
            await c3.SaveChangesAsync(cancellationToken);
            ran++;
        }
        return ran;
    }

    /// <summary>
    /// Regista nos jobs vencidos que a execução foi bloqueada pela
    /// configuração, sem a tratar como sucesso. Não avança
    /// <c>NextRunAtUtc</c> nem <c>LastRunAtUtc</c>: o job continua vencido
    /// para correr assim que a instalação fique <c>READY</c>.
    /// </summary>
    private async Task RecordBlockedAsync(
        List<ScheduledJobEntity> due,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (due.Count == 0) return;

        await using var context = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var changed = false;
        foreach (var job in due)
        {
            var entity = await context.ScheduledJobs
                .FirstOrDefaultAsync(j => j.Id == job.Id, cancellationToken);
            if (entity == null) continue;
            if (string.Equals(entity.LastResult, BlockedResult, StringComparison.Ordinal)) continue;
            entity.LastResult = BlockedResult;
            entity.UpdatedAtUtc = now;
            changed = true;
        }
        if (changed)
        {
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private IScheduledAction? ResolveAction(string name)
    {
        // Procura o serviço que implementa IScheduledAction e tem Name == nome.
        // Como DI não suporta key lookup nativo, varremos os serviços conhecidos.
        using var scope = _services.CreateScope();
        foreach (var svc in _services.GetServices<IScheduledAction>())
        {
            if (string.Equals(svc.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return svc;
            }
        }
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
