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
///
/// <para>
/// Wave 4 (PHASE 9C): o gate pode ser composto com um
/// <see cref="IOperationalReadinessGate"/>. Quando presente,
/// <see cref="IsReadyAsync"/> exige não só o lifecycle <c>READY</c> como
/// também <see cref="IOperationalReadinessGate.IsSetupCompleteAsync"/>.
/// Quando ausente, o comportamento é exactamente o anterior
/// (retrocompatibilidade — o caminho CLI <c>--telegram</c>
/// manual/automático mantém apenas o gate de lifecycle).
/// </para>
/// </summary>
public sealed class ConfigurationGate : IConfigurationGate
{
    private readonly ConfigurationLifecycleService _lifecycle;
    private readonly IOperationalReadinessGate? _readiness;

    public ConfigurationGate(
        ConfigurationLifecycleService lifecycle,
        IOperationalReadinessGate? readiness = null)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _readiness = readiness;
    }

    public ConfigurationLifecycleState State =>
        _lifecycle.Current?.State ?? ConfigurationLifecycleState.NotConfigured;

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _lifecycle.GetStateAsync(cancellationToken);
        if (!snapshot.IsReady)
        {
            return false;
        }

        if (_readiness is null)
        {
            return true;
        }

        return await _readiness.IsSetupCompleteAsync(cancellationToken);
    }

    /// <summary>
    /// Prontidão operacional (todos os componentes obrigatórios). Sem
    /// readiness gate devolve <c>false</c> (fail-safe): não há forma de
    /// confirmar a prontidão.
    /// </summary>
    public async Task<bool> IsOperationalReadyAsync(CancellationToken cancellationToken = default)
    {
        if (_readiness is null)
        {
            return false;
        }

        return await _readiness.IsSetupCompleteAsync(cancellationToken);
    }

    /// <summary>
    /// Componentes obrigatórios em falta, para logs/diagnóstico. Devolve
    /// uma lista vazia quando não há readiness gate. Só o
    /// <see cref="OperationalReadinessService"/> concreto consegue
    /// materializar a lista; qualquer outra implementação do gate é
    /// tratada como opaca.
    /// </summary>
    public async Task<IReadOnlyList<string>> MissingOperationalAsync(
        CancellationToken cancellationToken = default)
    {
        if (_readiness is not OperationalReadinessService service)
        {
            return Array.Empty<string>();
        }

        var snapshot = await service.EvaluateAsync(cancellationToken);
        return snapshot.MissingRequired;
    }
}
