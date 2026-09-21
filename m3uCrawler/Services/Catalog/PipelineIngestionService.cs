using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.Recognition;
using m3uCrawler.Services.Validation;

namespace m3uCrawler.Services.Catalog;

/// <summary>
/// Bridge PHASE-Bridge — Liga o pipeline real de discovery
/// (Telegram, M3U, M3U8-search) ao catálogo persistente
/// (SourceEntity, ChannelSourceEntity, CanonicalChannelEntity).
///
/// <para>
/// <b>Invariante de identidade (Wave B).</b> <see cref="CanonicalChannelEntity"/>
/// (Key) + <see cref="ChannelAliasEntity"/> é a ÚNICA fonte de
/// identidade de canal. A ingestão NUNCA cria canais canónicos nem
/// inventa identidade a partir de títulos desconhecidos: se o
/// <see cref="CatalogResolver.ResolveAsync"/> não devolver um canal
/// canónico real, o stream não é ligado ao catálogo.
/// </para>
///
/// <para>
/// Para cada <see cref="M3uStream"/> recebido:
/// </para>
/// <list type="number">
///   <item>Normaliza o título via <see cref="ChannelNormalizer.Normalize"/>;</item>
///   <item>Resolve a identidade via
///         <see cref="CatalogResolver.ResolveAsync"/>
///         (IdentityRule → Affinity(Channel) → ChannelAlias — o
///         mecanismo de matching existente; não é introduzido um
///         segundo algoritmo);</item>
///   <item>Se Canonical → liga a stream ao canal existente,
///         criando/atualizando <see cref="ChannelSourceEntity"/>
///         via <see cref="CatalogResolver.RecordChannelSourceAsync"/>
///         (já idempotente por (channelId, sourceId, streamUrl));</item>
///   <item>Se Unknown/ReviewOnly/Excluded → NÃO cria canal canónico,
///         NÃO cria <see cref="ChannelSourceEntity"/>. Regista
///         observabilidade (<see cref="MatchingAuditEntity"/> via
///         <see cref="CatalogResolver.RecordMatchingAuditAsync"/>) e,
///         para Unknown/ReviewOnly, um item de revisão idempotente via
///         <see cref="CatalogResolver.UpsertReviewItemAsync"/>. O
///         stream fica visível sem se tornar identidade;</item>
/// </list>
///
/// <para>
/// <b>Country gate (R1).</b> O constructor exige um
/// <see cref="CountryChannelValidator"/>. O
/// <see cref="IngestAsync"/> chama <see cref="CountryChannelValidator.ValidateStreams"/>
/// internamente. Streams REJECTED pelo country policy
/// (estrangeiros, sem token PT, etc.) são silenciosamente
/// descartados antes de qualquer persistência.
/// </para>
///
/// <para>
/// <b>REJECT ≠ UNKNOWN.</b> O country gate é executado antes do
/// matching de canal. Um stream que viola a política de país
/// nunca é convertido em identidade canónica — e um stream que passa
/// o gate mas não resolve continua a não criar identidade.
/// </para>
///
/// <para>
/// <b>Constructor sem validator.</b> Não é permitido. Lança
/// <see cref="InvalidOperationException"/> na primeira chamada a
/// <see cref="IngestAsync"/>. O caller tem de fornecer um
/// <see cref="CountryChannelValidator"/> explícito. Não existe
/// fallback silencioso para "pt" por defeito — ausência de
/// contexto deve ser tratada como erro, não como "ingerir tudo".
/// </para>
///
/// <para>
/// Proveniência preservada via:
/// </para>
/// <list type="bullet">
///   <item><c>SourceEntity.Origin</c> = chave da source + origem
///         legível (sanitizada);</item>
    ///   <item><c>ChannelSourceEntity.MatchMethod</c> =
    ///         método efectivo de <c>CatalogResolution.MatchMethod</c>
    ///         (ex.: <c>NormalizedName</c>, <c>KnownAlias</c>); fallback
    ///         historico <c>"canonical-alias"</c> quando ausente;</item>
    ///   <item><c>ChannelSourceEntity.MatchConfidence</c> = valor transportado
    ///         por <c>CatalogResolution.MatchConfidence</c> (W5.6; nunca
    ///         recalculado nem fixado pelo pipeline); e
    ///         <c>ChannelSourceEntity.MatchSemanticsVersion</c> =
    ///         <c>"msm1"</c>.</item>
/// </list>
///
/// <para>
/// Idempotência: <see cref="CatalogResolver.EnsureSourceAsync"/>
/// (por Key, sem reescrever a prioridade existente),
/// <see cref="CatalogResolver.RecordChannelSourceAsync"/>
/// (por (channelId, sourceId, streamUrl)) e
/// <see cref="CatalogResolver.UpsertReviewItemAsync"/>
/// (por fingerprint) garantem que uma segunda passagem sobre os
/// mesmos streams não cria duplicados.
/// </para>
/// </summary>
public sealed class PipelineIngestionService
{
    private readonly CatalogResolver _catalog;
    private readonly CountryChannelValidator? _countryValidator;

    /// <summary>
    /// Constructor preferido. O <paramref name="countryValidator"/>
    /// aplica <see cref="CountryChannelValidator.ValidateStreams"/>
    /// antes de qualquer persistência. Sem ele, a ingestion não
    /// é segura (nenhum filtro de país) e lança
    /// <see cref="InvalidOperationException"/> quando chamada.
    /// </summary>
    public PipelineIngestionService(
        CatalogResolver catalog,
        CountryChannelValidator countryValidator)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _countryValidator = countryValidator ?? throw new ArgumentNullException(
            nameof(countryValidator),
            "PipelineIngestionService requer um CountryChannelValidator. " +
            "Sem country gate, streams estrangeiros/rejeitados seriam persistidos " +
            "no catálogo. Não há fallback silencioso para 'pt' por defeito.");
    }

    /// <summary>
    /// Resultado agregado de uma ingestão. <c>AutoCreatedCount</c> é
    /// sempre 0 (a ingestão não cria canais); mantido no contrato por
    /// compatibilidade.
    /// </summary>
    public sealed record IngestionResult(
        int ReceivedCount,
        int RejectedByCountryCount,
        int IngestedCount,
        int MatchedCount,
        int AutoCreatedCount,
        IReadOnlyList<IngestionEntry> Entries);

    /// <summary>
    /// Entrada individual do resultado (apenas streams ligados a um
    /// canal canónico resolvido), útil para diagnóstico e teste.
    /// </summary>
    public sealed record IngestionEntry(
        string NormalizedIdentity,
        long CanonicalChannelId,
        string MatchMethod,
        double? MatchConfidence,
        bool AutoCreated);

    public async Task<IngestionResult> IngestAsync(
        IReadOnlyList<M3uStream> streams,
        string sourceKey,
        string sourceKindName,
        string countryCode,
        CancellationToken cancellationToken = default,
        string? runId = null,
        RecognitionPolicy? policy = null)
    {
        if (streams == null) throw new ArgumentNullException(nameof(streams));
        if (string.IsNullOrWhiteSpace(sourceKey))
            throw new ArgumentException("sourceKey obrigatório.", nameof(sourceKey));
        if (string.IsNullOrWhiteSpace(sourceKindName))
            throw new ArgumentException("sourceKindName obrigatório.", nameof(sourceKindName));
        if (string.IsNullOrWhiteSpace(countryCode))
            throw new ArgumentException(
                "countryCode obrigatório. Streams devem ter contexto de país antes da ingestion.",
                nameof(countryCode));

        // R1 — country gate. O validator é obrigatório pelo construtor;
        // esta verificação é defensiva para o caso de alguém injectar
        // null via reflection ou subclasses acidentais.
        if (_countryValidator == null)
        {
            throw new InvalidOperationException(
                "PipelineIngestionService foi construído sem CountryChannelValidator. " +
                "Isto indica um bug — o construtor devia ter rejeitado o null. " +
                "Reconstruir com um validator (ver construtor).");
        }

        // Aplicar o country gate: só streams que passam ValidateStreams
        // entram no pipeline de ingestion. Streams REJECTED (estrangeiros,
        // sem token PT, etc.) são silenciosamente descartados antes de
        // qualquer persistência — REJECT nunca é convertido em UNKNOWN.
        var countryMatches = _countryValidator.ValidateStreams(
            streams.ToList(), countryCode);

        // Map por referência M3uStream → CountryStreamMatch para
        // preservar o stream original no loop.
        var passedStreams = new HashSet<M3uStream>(
            countryMatches.Select(m => m.Stream));

        var kind = ParseKind(sourceKindName);
        var origin = BuildOrigin(sourceKey, sourceKindName, countryCode);

        // W1 (2026-09-19) — identidade funcional das contas presentes no
        // lote. A identidade é derivada da evidência funcional estável já
        // usada pelo pipeline Xtream (endpoint + username; password nunca
        // participa). Sem identidade estável NÃO se inventa conta: o
        // candidato é preservado como ocorrência distinta.
        var accounts = await ResolveFunctionalAccountsAsync(streams, cancellationToken);
        long? singleAccountId = accounts.Count == 1 ? accounts.Values.First().AccountId : null;

        // A prioridade de uma Source existente é estado do operador
        // (Dashboard/ordenação) e NÃO deve ser reescrita pela ingestion.
        // A prioridade 0 só se aplica quando a Source é criada agora.
        var source = await _catalog.EnsureSourceAsync(
            key: sourceKey,
            name: sourceKey,
            kind: kind,
            origin: origin,
            priority: 0,
            updatePriority: false,
            cancellationToken: cancellationToken,
            providerAccountId: singleAccountId);
        await _catalog.MarkSourceDiscoveryAsync(source.Id, DateTime.UtcNow, cancellationToken);

        // W1 — persistir as ocorrências de descoberta associadas ao Run.
        // Com conta funcional, a dedup por (RunId, ProviderAccountId)
        // garante uma única ocorrência por conta no mesmo Run. Sem conta,
        // a ocorrência é sempre preservada (nunca dedup silenciosa).
        var evidence = BuildDiscoveryEvidence(sourceKey, sourceKindName, countryCode);
        if (accounts.Count > 0)
        {
            var xtreamProvider = await _catalog.EnsureProviderAsync(
                ProviderNamespaces.Xtream,
                "Xtream Codes",
                ProviderType.Xtream,
                cancellationToken: cancellationToken);

            foreach (var account in accounts.Values)
            {
                await _catalog.RecordDiscoveryCandidateAsync(
                    runId: runId,
                    providerId: xtreamProvider.Id,
                    providerAccountId: account.AccountId,
                    externalIdentity: account.ExternalIdentity,
                    normalizedIdentity: account.ExternalIdentity,
                    evidence: evidence,
                    status: DiscoveryCandidateStatus.Normalized,
                    sourceId: source.Id,
                    cancellationToken: cancellationToken);
            }
        }
        else
        {
            await _catalog.RecordDiscoveryCandidateAsync(
                runId: runId,
                providerId: null,
                providerAccountId: null,
                externalIdentity: null,
                normalizedIdentity: null,
                evidence: evidence,
                status: DiscoveryCandidateStatus.Discovered,
                sourceId: source.Id,
                cancellationToken: cancellationToken);
        }

        var entries = new List<IngestionEntry>(streams.Count);
        int matched = 0;

        foreach (var stream in streams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stream == null || string.IsNullOrWhiteSpace(stream.Url))
            {
                continue;
            }

            // R1 — REJECT ≠ UNKNOWN. Se o stream foi rejeitado pelo
            // country gate, salta sem persistir nada.
            if (!passedStreams.Contains(stream))
            {
                continue;
            }

            var normalized = ChannelNormalizer.Normalize(stream.Title);
            // D-M4-02 — quando o caller passou uma `RecognitionPolicy`
            // derivada do snapshot do Run, propaga-a ao núcleo de
            // `ResolveAsync`. Quando `policy == null`, a forma do
            // overload legado (3-arg com `policy: null`) é equivalente
            // ao comportamento anterior (B1: nunca fallback para policy
            // viva; sem policy ⇒ sem policy).
            var resolution = await _catalog.ResolveAsync(
                normalized, stream.OriginalTvgId, policy, cancellationToken);
            var auditIdentity = string.IsNullOrEmpty(normalized) ? stream.Url : normalized;
            var originalTitle = stream.Title ?? string.Empty;

            if (resolution.Kind == CatalogResolutionKind.Canonical
                && resolution.CanonicalChannelId.HasValue
                && resolution.CanonicalChannelId.Value > 0)
            {
                var canonicalId = resolution.CanonicalChannelId.Value;
                var matchMethod = resolution.MatchMethod ?? "canonical-alias";
                // W5.6 — o pipeline apenas transporta a confiança decidida pela
                // Recognition; nunca a calcula, altera ou infere. Não existe
                // fallback para 0 quando o valor é null (o pipeline não pode
                // fabricar confiança).
                var matchConfidence = resolution.MatchConfidence;
                // Confiança de evidência legacy (audit/identidade externa),
                // conceito distinto de MatchConfidence; comportamento inalterado.
                const double legacyEvidenceConfidence = 1.0;
                matched++;

                var availability = stream.IsWorking
                    ? AvailabilityState.Reachable
                    : AvailabilityState.Dead;

                var channelSource = await _catalog.RecordChannelSourceAsync(
                    canonicalChannelId: canonicalId,
                    sourceId: source.Id,
                    streamUrl: stream.Url,
                    quality: StreamQuality.Unknown,
                    epg: EpgState.Unknown,
                    availability: availability,
                    matchConfidence: matchConfidence,
                    matchMethod: matchMethod,
                    isEnabled: stream.IsWorking,
                    cancellationToken: cancellationToken);

                // Wave W6b-2 — observação histórica apenas para streams
                // efectivamente validados (LastTested != default). Nunca se
                // fabrica uma observação para um stream por testar. A
                // re-ingerir o mesmo evento de validação (mesmo LastTested)
                // não cria duplicados: a dedupe é por
                // (ChannelSourceId, ObservedAtUtc).
                if (stream.LastTested != default)
                {
                    var observedAtUtc = stream.LastTested.Kind == DateTimeKind.Utc
                        ? stream.LastTested
                        : stream.LastTested.ToUniversalTime();
                    var responseTimeMs = stream.ResponseTime > 0
                        ? (long)Math.Round(stream.ResponseTime)
                        : 0L;
                    await _catalog.RecordChannelSourceObservationIfAbsentAsync(
                        channelSourceId: channelSource.Id,
                        quality: StreamQuality.Unknown,
                        epg: EpgState.Unknown,
                        availability: availability,
                        responseTimeMs: responseTimeMs,
                        observedAtUtc: observedAtUtc,
                        cancellationToken: cancellationToken);
                }

                await _catalog.RecordMatchingAuditAsync(
                    normalizedIdentity: auditIdentity,
                    originalTitle: originalTitle,
                    sourceGroup: stream.Group,
                    kind: CatalogResolutionKind.Canonical,
                    canonicalChannelId: canonicalId,
                    confidence: legacyEvidenceConfidence,
                    reasonSignature: "matched-via-pipeline",
                    cancellationToken: cancellationToken);

                // Evidência externa (tvg-id) do stream, associada ao
                // canal canónico JÁ resolvido. Idempotente por
                // (Namespace, Value); nunca sobrepõe uma associação
                // existente a outro canal. Não cria identidade nova
                // (DL-002) — apenas registra evidência.
                await _catalog.RecordExternalIdentityAsync(
                    canonicalChannelId: canonicalId,
                    providerId: sourceKey,
                    @namespace: ExternalIdentityNamespaces.TvgId,
                    rawValue: stream.OriginalTvgId,
                    origin: "ingestion",
                    confidence: legacyEvidenceConfidence,
                    cancellationToken: cancellationToken);

                entries.Add(new IngestionEntry(
                    NormalizedIdentity: normalized,
                    CanonicalChannelId: canonicalId,
                    MatchMethod: matchMethod,
                    MatchConfidence: matchConfidence,
                    AutoCreated: false));
                continue;
            }

            // Não-canónico. A identidade de canal é apenas
            // Key + ChannelAlias: NUNCA criar um CanonicalChannel a
            // partir de um título desconhecido, e NUNCA fabricar um
            // ChannelSource sob uma identidade inventada. O stream é
            // registado apenas como sinal de revisão/observabilidade.
            var (auditKind, reasonSignature) = resolution.Kind switch
            {
                CatalogResolutionKind.Rule => (CatalogResolutionKind.Rule,
                    resolution.RuleDisposition == RuleDisposition.Excluded
                        ? "excluded-via-pipeline"
                        : "review-only-via-pipeline"),
                CatalogResolutionKind.Ambiguous =>
                    (CatalogResolutionKind.Unknown, AmbiguousReasonSignature(resolution)),
                _ => (CatalogResolutionKind.Unknown, "unknown-via-pipeline"),
            };

            await _catalog.RecordMatchingAuditAsync(
                normalizedIdentity: auditIdentity,
                originalTitle: originalTitle,
                sourceGroup: stream.Group,
                kind: auditKind,
                canonicalChannelId: null,
                confidence: 0.0,
                reasonSignature: reasonSignature,
                cancellationToken: cancellationToken);

            // Excluded é uma decisão explícita de identidade: não a
            // transformamos em pedido de revisão. Unknown/ReviewOnly
            // ficam visíveis para decisão humana.
            if (resolution.Kind != CatalogResolutionKind.Rule
                || resolution.RuleDisposition != RuleDisposition.Excluded)
            {
                if (!string.IsNullOrEmpty(normalized))
                {
                    await _catalog.UpsertReviewItemAsync(
                        normalizedIdentity: normalized,
                        sourceGroup: stream.Group ?? string.Empty,
                        reasonSignature: reasonSignature,
                        reasonText: "Ingestion não encontrou canal canónico; stream não foi ligado ao catálogo.",
                        cancellationToken: cancellationToken);
                }
            }
        }

        return new IngestionResult(
            ReceivedCount: streams.Count,
            RejectedByCountryCount: streams.Count - passedStreams.Count,
            IngestedCount: entries.Count,
            MatchedCount: matched,
            // A ingestão já não cria canais: mantido no contrato por
            // compatibilidade e sempre 0.
            AutoCreatedCount: 0,
            Entries: entries);
    }

    /// <summary>
    /// W5.4 — Uma ambiguidade de Recognition que traz diagnóstico fuzzy de
    /// W5.3 preserva o motivo técnico (<c>fuzzy-ambiguous</c> /
    /// <c>fuzzy-below-threshold</c>) como <c>reasonSignature</c> do
    /// <see cref="ReviewItemEntity"/>; as restantes ambiguidades mantêm o
    /// motivo histórico. Não altera o motor fuzzy nem cria identidade.
    /// </summary>
    internal static string AmbiguousReasonSignature(CatalogResolution resolution)
    {
        var fuzzyReason = resolution.FuzzyDiagnostic?.DecisionReason;
        return string.IsNullOrWhiteSpace(fuzzyReason)
            ? "ambiguous-external-identity"
            : fuzzyReason!;
    }

    private static SourceKind ParseKind(string name) => name?.Trim().ToLowerInvariant() switch
    {
        "telegram" => SourceKind.Telegram,
        "m3u" => SourceKind.M3U,
        "xtream" => SourceKind.Xtream,
        "http" => SourceKind.Http,
        "file" => SourceKind.File,
        "manual" => SourceKind.Manual,
        _ => SourceKind.M3U,
    };

    private static string BuildOrigin(string sourceKey, string sourceKindName, string countryCode) =>
        $"{sourceKindName}://{sourceKey}?country={countryCode}";

    /// <summary>
    /// W1 — resolve as contas funcionais distintas presentes num lote de
    /// streams. Reutiliza a identidade funcional Xtream existente
    /// (<see cref="AccountIdentity.TryComputeXtreamExternalIdentity"/>):
    /// endpoint + username, password excluída. Streams sem identidade
    /// funcional estável não contribuem para nenhuma conta.
    /// </summary>
    private sealed record FunctionalAccount(string ExternalIdentity, long AccountId);

    private async Task<Dictionary<string, FunctionalAccount>> ResolveFunctionalAccountsAsync(
        IReadOnlyList<M3uStream> streams, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, FunctionalAccount>(StringComparer.Ordinal);
        ProviderEntity? xtreamProvider = null;
        foreach (var stream in streams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stream == null || string.IsNullOrWhiteSpace(stream.Url)) continue;
            if (!AccountIdentity.TryComputeXtreamExternalIdentity(stream.Url, out var external)
                || string.IsNullOrEmpty(external))
            {
                continue;
            }

            var accountKey = AccountKey.Compose(ProviderNamespaces.Xtream, external);
            if (accountKey is null || result.ContainsKey(accountKey)) continue;

            xtreamProvider ??= await _catalog.EnsureProviderAsync(
                ProviderNamespaces.Xtream,
                "Xtream Codes",
                ProviderType.Xtream,
                cancellationToken: cancellationToken);
            var account = await _catalog.EnsureProviderAccountAsync(
                xtreamProvider.Id,
                accountKey,
                accountKey,
                ProviderAccountStatus.Discovered,
                cancellationToken: cancellationToken);
            result[accountKey] = new FunctionalAccount(external, account.Id);
        }
        return result;
    }

    /// <summary>
    /// Evidência sanitizada da descoberta. Nunca contém credenciais.
    /// </summary>
    private static string BuildDiscoveryEvidence(string sourceKey, string sourceKindName, string countryCode)
        => CredentialSanitizer.SanitizeText($"{sourceKindName}:{sourceKey}:country={countryCode}");
}
