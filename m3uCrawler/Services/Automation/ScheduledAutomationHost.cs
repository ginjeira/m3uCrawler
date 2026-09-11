using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Matching;
using Microsoft.Extensions.DependencyInjection;

namespace m3uCrawler.Services.Automation;

/// <summary>
/// PHASE 12 — Compõe um <see cref="IServiceProvider"/> mínimo que
/// regista todas as <see cref="IScheduledAction"/> concretas e
/// devolve um <see cref="ScheduledJobRunner"/> pronto a arrancar.
///
/// <para>
/// O container é propositadamente leve: só expõe o que o runner
/// precisa. A aplicação principal continua a instanciar os seus
/// serviços manualmente — não estamos a converter o programa
/// numa aplicação com hosting genérico.
/// </para>
/// </summary>
public sealed class ScheduledAutomationHost : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly ScheduledJobRunner _runner;

    public ScheduledActionOptions Options { get; }
    public IServiceProvider Services => _services;
    public ScheduledJobRunner Runner => _runner;
    public IReadOnlyList<IScheduledAction> RegisteredActions { get; }

    private ScheduledAutomationHost(
        ServiceProvider services,
        ScheduledJobRunner runner,
        ScheduledActionOptions options,
        IReadOnlyList<IScheduledAction> actions)
    {
        _services = services;
        _runner = runner;
        Options = options;
        RegisteredActions = actions;
    }

    public static ScheduledAutomationHost Build(
        CatalogResolver catalog,
        string outputDir,
        DispatcharrConfig dispatcharrConfig,
        TimeSpan? pollInterval = null,
        Action<ScheduledActionOptions>? configureOptions = null)
    {
        var options = new ScheduledActionOptions { OutputDir = outputDir };
        configureOptions?.Invoke(options);

        var sc = new ServiceCollection();
        sc.AddSingleton(options);
        sc.AddSingleton(catalog);
        sc.AddSingleton(dispatcharrConfig);

        // Serviços existentes reutilizados (sem nova pipeline):
        sc.AddSingleton(new PlaylistManagerService());
        sc.AddSingleton(new M3uCrawlerService());
        sc.AddSingleton(new M3uTesterService());
        sc.AddSingleton(new PlaylistComposerService(catalog.GetFactory()));
        sc.AddSingleton<AliasResolver>(_ => AliasResolver.FromFile(dispatcharrConfig.AliasFile));

        sc.AddSingleton<IScheduledAction, ScheduledM3uDiscoveryAction>();
        sc.AddSingleton<IScheduledAction, ScheduledValidationAction>();
        sc.AddSingleton<IScheduledAction, ScheduledPlaylistGenerationAction>();
        sc.AddSingleton<IScheduledAction, ScheduledDispatcharrSyncAction>();

        var provider = sc.BuildServiceProvider();

        // Eagerly resolve to lock the action list at startup. Se
        // uma action falhar a construir, queremos saber já no boot
        // e não à primeira execução agendada.
        var actions = new List<IScheduledAction>();
        foreach (var a in provider.GetServices<IScheduledAction>())
        {
            actions.Add(a);
        }

        var runner = new ScheduledJobRunner(
            catalog.GetFactory(),
            provider,
            pollInterval);

        return new ScheduledAutomationHost(provider, runner, options, actions);
    }

    /// <summary>
    /// Arranca o <see cref="ScheduledJobRunner"/> em background
    /// respeitando o <see cref="CancellationToken"/> fornecido.
    /// Não bloqueia a thread chamadora.
    /// </summary>
    public void Start()
    {
        _runner.Start();
    }

    public async Task StopAsync()
    {
        await _runner.StopAsync();
    }

    public void Dispose()
    {
        _runner.Dispose();
        _services.Dispose();
    }
}
