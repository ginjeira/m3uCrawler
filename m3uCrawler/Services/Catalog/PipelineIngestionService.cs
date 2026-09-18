using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Matching;

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
///         "canonical-alias" (canal canónico existente);</item>
///   <item><c>ChannelSourceEntity.MatchConfidence</c> = 1.0 (canonical
///         existente).</item>
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
        double MatchConfidence,
        bool AutoCreated);

    public async Task<IngestionResult> IngestAsync(
        IReadOnlyList<M3uStream> streams,
        string sourceKey,
        string sourceKindName,
        string countryCode,
        CancellationToken cancellationToken = default)
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
            cancellationToken: cancellationToken);
        await _catalog.MarkSourceDiscoveryAsync(source.Id, DateTime.UtcNow, cancellationToken);

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
            var resolution = await _catalog.ResolveAsync(normalized, cancellationToken);
            var auditIdentity = string.IsNullOrEmpty(normalized) ? stream.Url : normalized;
            var originalTitle = stream.Title ?? string.Empty;

            if (resolution.Kind == CatalogResolutionKind.Canonical
                && resolution.CanonicalChannelId.HasValue
                && resolution.CanonicalChannelId.Value > 0)
            {
                var canonicalId = resolution.CanonicalChannelId.Value;
                const string matchMethod = "canonical-alias";
                const double confidence = 1.0;
                matched++;

                var availability = stream.IsWorking
                    ? AvailabilityState.Reachable
                    : AvailabilityState.Dead;

                await _catalog.RecordChannelSourceAsync(
                    canonicalChannelId: canonicalId,
                    sourceId: source.Id,
                    streamUrl: stream.Url,
                    quality: StreamQuality.Unknown,
                    epg: EpgState.Unknown,
                    availability: availability,
                    matchConfidence: confidence,
                    matchMethod: matchMethod,
                    isEnabled: stream.IsWorking,
                    cancellationToken: cancellationToken);

                await _catalog.RecordMatchingAuditAsync(
                    normalizedIdentity: auditIdentity,
                    originalTitle: originalTitle,
                    sourceGroup: stream.Group,
                    kind: CatalogResolutionKind.Canonical,
                    canonicalChannelId: canonicalId,
                    confidence: confidence,
                    reasonSignature: "matched-via-pipeline",
                    cancellationToken: cancellationToken);

                entries.Add(new IngestionEntry(
                    NormalizedIdentity: normalized,
                    CanonicalChannelId: canonicalId,
                    MatchMethod: matchMethod,
                    MatchConfidence: confidence,
                    AutoCreated: false));
                continue;
            }

            // Não-canónico. A identidade de canal é apenas
            // Key + ChannelAlias: NUNCA criar um CanonicalChannel a
            // partir de um título desconhecido, e NUNCA fabricar um
            // ChannelSource sob uma identidade inventada. O stream é
            // registado apenas como sinal de revisão/observabilidade.
            var (auditKind, reasonSignature) = resolution.Kind == CatalogResolutionKind.Rule
                ? (CatalogResolutionKind.Rule,
                   resolution.RuleDisposition == RuleDisposition.Excluded
                       ? "excluded-via-pipeline"
                       : "review-only-via-pipeline")
                : (CatalogResolutionKind.Unknown, "unknown-via-pipeline");

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
}
