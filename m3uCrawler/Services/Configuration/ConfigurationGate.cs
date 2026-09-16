namespace m3uCrawler.Services.Configuration;

/// <summary>
/// PHASE 9C.1 — Mecanismo único de decisão sobre se uma execução
/// automática (discovery ou scheduler) é permitida pelo estado de
/// configuração actual. Substitui a dispersão de <c>if (!configured)</c>
/// por múltiplos locais.
/// </summary>
public interface IConfigurationGate
{
    /// <summary>
    /// Último estado conhecido. Pode estar desactualizado; para decisões
    /// usar sempre <see cref="IsReadyAsync"/>.
    /// </summary>
    ConfigurationLifecycleState State { get; }

    /// <summary>
    /// <c>true</c> apenas quando a instalação está <c>READY</c>. Qualquer
    /// estado desconhecido ou ilegível é tratado como não-pronto
    /// (fail-safe).
    /// </summary>
    Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementação do gate apoiada em <see cref="ConfigurationLifecycleService"/>.
/// </summary>
public sealed class ConfigurationGate : IConfigurationGate
{
    private readonly ConfigurationLifecycleService _lifecycle;

    public ConfigurationGate(ConfigurationLifecycleService lifecycle)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    public ConfigurationLifecycleState State =>
        _lifecycle.Current?.State ?? ConfigurationLifecycleState.NotConfigured;

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _lifecycle.GetStateAsync(cancellationToken);
        return snapshot.IsReady;
    }
}
