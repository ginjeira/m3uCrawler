using m3uCrawler.Services.Validation;

namespace m3uCrawler.Services.Validation;

/// <summary>
/// Estado partilhado da PHASE 9A dentro de um processo.
/// Concentra <see cref="StreamValidationOptions"/>, <see cref="StreamValidationCache"/>
/// e <see cref="HostFailureTracker"/> para que múltiplos call sites de produção
/// (TelegramScraperService, Program.cs, TelegramBotService,
/// ScheduledAutomationHost) usem a mesma infraestrutura.
///
/// Não é uma nova persistence layer:
/// - Cache e HostTracker são apenas in-memory.
/// - A policy persiste via <see cref="StreamValidationPolicyStore"/> que já existia.
///   Esta classe apenas carrega-a em memória uma vez e reusa-a.
///
/// Lifetime: processo. Não serializável, não persistido.
/// </summary>
public sealed class StreamValidationState
{
    /// <summary>
    /// Opções efectivas lidas do <see cref="StreamValidationPolicyStore"/> quando
    /// disponível, ou <see cref="StreamValidationOptions.DefaultClone"/> caso
    /// contrário. Imutável do ponto de vista do caller; <see cref="ReloadPolicy"/>
    /// substitui o objecto por um novo.
    /// </summary>
    public StreamValidationOptions Options { get; private set; }

    /// <summary>
    /// Cache partilhado entre todos os testers criados a partir deste state.
    /// </summary>
    public StreamValidationCache Cache { get; }

    /// <summary>
    /// Host-failure tracker partilhado entre todos os testers.
    /// </summary>
    public HostFailureTracker HostTracker { get; } = new();

    private readonly StreamValidationPolicyStore? _policyStore;

    /// <summary>
    /// Cria um state isolado (sem policy persistida). Útil para testes e
    /// para cenários onde a policy não tem de ser recarregada.
    /// </summary>
    public StreamValidationState(StreamValidationOptions? options = null)
    {
        Options = (options ?? StreamValidationOptions.DefaultClone()).Sanitized();
        Cache = new StreamValidationCache(Options);
    }

    /// <summary>
    /// Cria um state ligado a um <see cref="StreamValidationPolicyStore"/>.
    /// A policy é carregada imediatamente e pode ser recarregada via
    /// <see cref="ReloadPolicy"/>.
    /// </summary>
    public StreamValidationState(StreamValidationPolicyStore store)
    {
        _policyStore = store ?? throw new ArgumentNullException(nameof(store));
        Options = store.Load().Sanitized();
        Cache = new StreamValidationCache(Options);
    }

    /// <summary>
    /// Recarrega a policy do store (se existir). Substitui <see cref="Options"/>
    /// por uma nova instância e propaga-a ao Cache.
    /// </summary>
    public void ReloadPolicy()
    {
        if (_policyStore == null) return;
        Options = _policyStore.Load().Sanitized();
        // O Cache referencia as Options pelo TTL; recriar aqui mantém
        // simetria com o construtor de M3uTesterService.
        var fresh = new StreamValidationCache(Options);
        // NÃO copiamos entradas: as novas options podem ter TTLs diferentes.
        // Cache hit/miss no próximo run usa a cache fresh.
        // Para preservar continuidade, expomos o método ApplyNewOptions.
        Cache.ApplyNewOptions(Options);
    }
}
