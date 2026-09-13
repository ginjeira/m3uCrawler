using m3uCrawler.Services.Validation;

namespace m3uCrawler.Services;

/// <summary>
/// Factory para <see cref="M3uTesterService"/> ligado ao
/// <see cref="StreamValidationState"/>. Esta é a forma CANÓNICA de obter
/// testers em produção a partir desta tarefa (9A-PROD-WIRING).
///
/// Antes desta tarefa, cada call site fazia <c>new M3uTesterService()</c>
/// com defaults hardcoded — resultava em:
/// - cache privada por call site (perdida entre runs);
/// - HostFailureTracker privado (reset entre runs);
/// - policy persistida ignorada (defaults hardcoded);
/// - scheduling explosivo (48k Tasks em
///   <c>Program.cs:RunTelegramMaintenanceCycle</c>).
///
/// O factory resolve os três primeiros pontos e mantém o
/// <see cref="M3uTesterService"/> como entidade leve que reutiliza o
/// state partilhado.
/// </summary>
public static class StreamValidationTesterFactory
{
    /// <summary>
    /// Cria um tester ligado ao state partilhado. Use um único state por
    /// processo (via DI singleton ou variável estática) para que cache e
    /// host-tracker sobrevivam entre runs.
    /// </summary>
    public static M3uTesterService CreateTester(StreamValidationState state)
    {
        if (state == null) throw new ArgumentNullException(nameof(state));
        return new M3uTesterService(state);
    }

    /// <summary>
    /// Helper de conveniência: cria um state efémero (sem policy store).
    /// Útil em testes e em call sites onde a policy não é relevante.
    /// Em produção, prefira <see cref="StreamValidationState(StreamValidationPolicyStore)"/>.
    /// </summary>
    public static StreamValidationState CreateIsolatedState() => new();

    /// <summary>
    /// Helper de conveniência: cria um state carregado de um policy store.
    /// </summary>
    public static StreamValidationState CreateStateFromStore(StreamValidationPolicyStore store)
    {
        if (store == null) throw new ArgumentNullException(nameof(store));
        return new StreamValidationState(store);
    }
}
