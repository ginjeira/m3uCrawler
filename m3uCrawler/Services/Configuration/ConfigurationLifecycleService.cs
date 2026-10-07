using m3uCrawler.Services.Catalog;
using Microsoft.EntityFrameworkCore;

namespace m3uCrawler.Services.Configuration;

/// <summary>
/// Requisito advisory derivado dos itens da PHASE 9C §5 do roadmap.
/// Nesta wave é apenas informativo: nunca bloqueia <c>READY</c>.
/// </summary>
/// <param name="Key">Identificador estável do requisito.</param>
/// <param name="Satisfied">Se o requisito está satisfeito.</param>
/// <param name="Detail">Descrição curta, sem dados sensíveis.</param>
public sealed record AdvisoryRequirement(string Key, bool Satisfied, string Detail);

/// <summary>
/// PHASE 9C.1 — Serviço central do ciclo de vida de configuração.
///
/// <para>
/// Autoridade: se existir estado persistido válido, esse estado é usado
/// sem qualquer inferência. Só quando não existe estado persistido é que
/// se corre o bootstrap:
/// </para>
/// <list type="number">
///   <item>procurar evidência objectiva de instalação já operacional;</item>
///   <item>se existir, adoptar <c>READY</c> (legacy adoption) e registar;</item>
///   <item>caso contrário, iniciar em <c>NOT_CONFIGURED</c>.</item>
/// </list>
///
/// <para>
/// O critério de <c>READY</c> nesta wave é exactamente "estado persistido
/// READY" (por transição explícita ou adopção legacy). Os requisitos
/// completos da PHASE 9C §5 são apenas advisory (<see cref="EvaluateAdvisoryAsync"/>)
/// e não são gate.
/// </para>
/// </summary>
public sealed class ConfigurationLifecycleService
{
    private readonly ConfigurationLifecycleStore _store;
    private readonly LegacyConfigurationEvidenceEvaluator _evidence;
    private readonly IDbContextFactory<ChannelCatalogDbContext>? _factory;
    private readonly string? _outputDir;

    public ConfigurationLifecycleService(
        ConfigurationLifecycleStore store,
        IDbContextFactory<ChannelCatalogDbContext>? factory,
        string? outputDir)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _factory = factory;
        _outputDir = outputDir;
        _evidence = new LegacyConfigurationEvidenceEvaluator(factory, outputDir);
    }

    /// <summary>Última fotografia conhecida em memória (pode ser <c>null</c> antes do bootstrap).</summary>
    public ConfigurationLifecycleSnapshot? Current { get; private set; }

    public string FilePath => _store.FilePath;

    /// <summary>
    /// Garante que existe um estado persistido. Idempotente: se já existir
    /// estado, devolve-o sem reavaliar evidência.
    /// </summary>
    public async Task<ConfigurationLifecycleSnapshot> EnsureInitializedAsync(
        CancellationToken cancellationToken = default)
    {
        var persisted = _store.Load();
        if (persisted != null)
        {
            Current = persisted;
            return persisted;
        }

        var evidence = await _evidence.EvaluateAsync(cancellationToken);
        var now = DateTime.UtcNow;

        var snapshot = evidence.HasEvidence
            ? new ConfigurationLifecycleSnapshot(
                ConfigurationLifecycleState.Ready,
                AdoptedFromLegacy: true,
                AdoptedAtUtc: now,
                LastReason: "legacy-adoption:" + string.Join(",", evidence.Reasons),
                UpdatedAtUtc: now)
            : new ConfigurationLifecycleSnapshot(
                ConfigurationLifecycleState.NotConfigured,
                AdoptedFromLegacy: false,
                AdoptedAtUtc: null,
                LastReason: "first-run:no-persisted-state",
                UpdatedAtUtc: now);

        _store.Save(snapshot);
        Current = snapshot;
        return snapshot;
    }

    /// <summary>
    /// Lê o estado persistido. Se ainda não existir, corre o bootstrap
    /// (equivalente a <see cref="EnsureInitializedAsync"/>).
    /// </summary>
    public async Task<ConfigurationLifecycleSnapshot> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        var persisted = _store.Load();
        if (persisted != null)
        {
            Current = persisted;
            return persisted;
        }

        if (Current != null)
        {
            return Current;
        }

        return await EnsureInitializedAsync(cancellationToken);
    }

    /// <summary>
    /// Persiste uma transição explícita de estado. Preserva a marca de
    /// adopção legacy quando já existia.
    /// </summary>
    public ConfigurationLifecycleSnapshot SetState(
        ConfigurationLifecycleState state,
        string reason)
    {
        var previous = _store.Load() ?? Current;
        var now = DateTime.UtcNow;

        var snapshot = new ConfigurationLifecycleSnapshot(
            state,
            AdoptedFromLegacy: previous?.AdoptedFromLegacy ?? false,
            AdoptedAtUtc: previous?.AdoptedAtUtc,
            LastReason: reason ?? string.Empty,
            UpdatedAtUtc: now);

        _store.Save(snapshot);
        Current = snapshot;
        return snapshot;
    }

    /// <summary>
    /// Avaliação advisory dos requisitos da PHASE 9C §5, quando
    /// determináveis de forma segura a partir do estado existente. Nunca
    /// é usada como gate nesta wave.
    /// </summary>
    public async Task<IReadOnlyList<AdvisoryRequirement>> EvaluateAdvisoryAsync(
        CancellationToken cancellationToken = default)
    {
        var requirements = new List<AdvisoryRequirement>();

        if (_factory == null)
        {
            requirements.Add(new AdvisoryRequirement(
                "catalog-database", false, "catálogo indisponível para avaliação."));
            return requirements;
        }

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);

        requirements.Add(new AdvisoryRequirement(
            "canonical-catalog",
            await context.CanonicalChannels.AnyAsync(cancellationToken),
            "catálogo canónico tem canais."));
        requirements.Add(new AdvisoryRequirement(
            "ordering-list",
            await context.OrderingLists.AnyAsync(cancellationToken),
            "existe pelo menos uma ordering list."));
        requirements.Add(new AdvisoryRequirement(
            "import-policies",
            await context.ImportPolicies.AnyAsync(cancellationToken),
            "existem import policies definidas."));
        requirements.Add(new AdvisoryRequirement(
            "canonical-groups",
            await context.CanonicalGroups.AnyAsync(cancellationToken),
            "existem grupos canónicos definidos."));
        requirements.Add(new AdvisoryRequirement(
            "active-sources",
            await context.Sources.AnyAsync(s => s.IsEnabled, cancellationToken),
            "existem fontes activas."));
        requirements.Add(new AdvisoryRequirement(
            "source-priority",
            await context.SourcePriorityPolicies.AnyAsync(cancellationToken),
            "existe política de prioridade de fontes."));
        requirements.Add(new AdvisoryRequirement(
            "scheduler-jobs",
            await context.ScheduledJobs.AnyAsync(cancellationToken),
            "existem jobs agendados."));

        var outputUsable = !string.IsNullOrWhiteSpace(_outputDir) && Directory.Exists(_outputDir);
        requirements.Add(new AdvisoryRequirement(
            "output-dir", outputUsable, "pasta de output existe."));

        return requirements;
    }
}
