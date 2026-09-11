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
/// Para cada <see cref="M3uStream"/> recebido:
/// </para>
/// <list type="number">
///   <item>Normaliza o título via <see cref="ChannelNormalizer.Normalize"/>;</item>
///   <item>Resolve a identidade via
///         <see cref="CatalogResolver.ResolveAsync"/>
///         (mecanismo de matching existente — não é introduzido um
///         segundo algoritmo);</item>
///   <item>Se Canonical → usa o canal existente.</item>
///   <item>Se Unknown → cria um CanonicalChannel via
///         <see cref="CatalogResolver.EnsureCanonicalChannelAsync"/>
///         com <see cref="PublicationPolicy.CreateEligible"/>;
///         o canal permanece disponível para revisão posterior
///         via Dashboard (não é eliminado);</item>
///   <item>Cria/atualiza <see cref="ChannelSourceEntity"/>
///         via <see cref="CatalogResolver.RecordChannelSourceAsync"/>
///         (já idempotente por (channelId, sourceId, streamUrl));</item>
///   <item>Regista <see cref="MatchingAuditEntity"/>
///         via <see cref="CatalogResolver.RecordMatchingAuditAsync"/>.</item>
/// </list>
///
/// <para>
/// Proveniência preservada via:
/// </para>
/// <list type="bullet">
///   <item><c>SourceEntity.Origin</c> = chave da source + origem
///         legível (sanitizada);</item>
///   <item><c>ChannelSourceEntity.MatchMethod</c> =
///         "canonical-alias", "canonical-key", "auto-create" ou
///         "auto-create-existing-alias";</item>
///   <item><c>ChannelSourceEntity.MatchConfidence</c> = 1.0 (canonical
///         existente), 0.5 (auto-criado), ou conforme o caso.</item>
/// </list>
///
/// <para>
/// Idempotência: <see cref="CatalogResolver.EnsureSourceAsync"/>
/// (por Key) e <see cref="CatalogResolver.RecordChannelSourceAsync"/>
/// (por (channelId, sourceId, streamUrl)) garantem que uma segunda
/// passagem sobre os mesmos streams não cria duplicados.
/// </para>
/// </summary>
public sealed class PipelineIngestionService
{
    private readonly CatalogResolver _catalog;

    public PipelineIngestionService(CatalogResolver catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>
    /// Resultado agregado de uma ingestão.
    /// </summary>
    public sealed record IngestionResult(
        int ReceivedCount,
        int IngestedCount,
        int MatchedCount,
        int AutoCreatedCount,
        IReadOnlyList<IngestionEntry> Entries);

    /// <summary>
    /// Entrada individual do resultado, útil para diagnóstico e teste.
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

        var kind = ParseKind(sourceKindName);
        var origin = BuildOrigin(sourceKey, sourceKindName, countryCode);

        var source = await _catalog.EnsureSourceAsync(
            key: sourceKey,
            name: sourceKey,
            kind: kind,
            origin: origin,
            priority: 0,
            cancellationToken: cancellationToken);
        await _catalog.MarkSourceDiscoveryAsync(source.Id, DateTime.UtcNow, cancellationToken);

        var entries = new List<IngestionEntry>(streams.Count);
        int matched = 0;
        int autoCreated = 0;

        foreach (var stream in streams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stream == null || string.IsNullOrWhiteSpace(stream.Url))
            {
                continue;
            }

            var normalized = ChannelNormalizer.Normalize(stream.Title);
            var resolution = await _catalog.ResolveAsync(normalized, cancellationToken);

            long canonicalId;
            string matchMethod;
            double confidence;
            bool autoCreatedThis = false;

            if (resolution.Kind == CatalogResolutionKind.Canonical
                && resolution.CanonicalChannelId.HasValue
                && resolution.CanonicalChannelId.Value > 0)
            {
                canonicalId = resolution.CanonicalChannelId.Value;
                matchMethod = "canonical-alias";
                confidence = 1.0;
                matched++;
            }
            else if (resolution.Kind == CatalogResolutionKind.Rule
                && resolution.RuleDisposition == RuleDisposition.Excluded)
            {
                // Excluded by IdentityRule — não ingere.
                continue;
            }
            else
            {
                // Unknown: cria canonical com CreateEligible. O canal
                // permanece disponível para revisão futura via
                // Dashboard.
                var slug = ToSlug(stream.Title);
                var displayName = string.IsNullOrWhiteSpace(stream.Title)
                    ? stream.Url
                    : stream.Title.Trim();

                var (ch, created) = await _catalog.EnsureCanonicalChannelAsync(
                    key: $"{countryCode}-{slug}",
                    displayName: displayName,
                    editorialCategory: EditorialCategory.Live,
                    editorialGroup: GroupFor(countryCode),
                    publicationPolicy: PublicationPolicy.CreateEligible,
                    isEnabled: true,
                    normalizedAlias: string.IsNullOrEmpty(normalized) ? null : normalized,
                    cancellationToken: cancellationToken);

                canonicalId = ch.Id;
                matchMethod = created ? "auto-create" : "auto-create-existing-alias";
                confidence = created ? 0.5 : 0.7;
                if (created) autoCreated++;
                autoCreatedThis = created;
            }

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
                normalizedIdentity: string.IsNullOrEmpty(normalized) ? stream.Url : normalized,
                originalTitle: stream.Title,
                sourceGroup: stream.Group,
                kind: autoCreatedThis
                    ? CatalogResolutionKind.Unknown
                    : (resolution.Kind == CatalogResolutionKind.Canonical
                        ? CatalogResolutionKind.Canonical
                        : resolution.Kind == CatalogResolutionKind.Rule
                            ? CatalogResolutionKind.Rule
                            : CatalogResolutionKind.Unknown),
                canonicalChannelId: canonicalId,
                confidence: confidence,
                reasonSignature: autoCreatedThis ? "auto-created-via-pipeline" : "matched-via-pipeline",
                cancellationToken: cancellationToken);

            entries.Add(new IngestionEntry(
                NormalizedIdentity: normalized,
                CanonicalChannelId: canonicalId,
                MatchMethod: matchMethod,
                MatchConfidence: confidence,
                AutoCreated: autoCreatedThis));
        }

        return new IngestionResult(
            ReceivedCount: streams.Count,
            IngestedCount: entries.Count,
            MatchedCount: matched,
            AutoCreatedCount: autoCreated,
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

    private static CanonicalEditorialGroup GroupFor(string countryCode) =>
        countryCode?.Trim().ToLowerInvariant() switch
        {
            "pt" => CanonicalEditorialGroup.PortugalLive,
            "es" => CanonicalEditorialGroup.Foreign,
            "br" => CanonicalEditorialGroup.Foreign,
            _ => CanonicalEditorialGroup.Other,
        };

    private static string ToSlug(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "unknown";
        var arr = s.Trim().ToLowerInvariant().ToCharArray();
        var sb = new System.Text.StringBuilder(arr.Length);
        foreach (var c in arr)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c == ' ' || c == '-' || c == '_') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(slug) ? "unknown" : slug;
    }
}
