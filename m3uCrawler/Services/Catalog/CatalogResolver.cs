using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Services.Audit;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.Recognition;
using m3uCrawler.Services.Validation;

namespace m3uCrawler.Services.Catalog;

/// <summary>
/// Resolve o catálogo persistente para o matcher. Substitui
/// <c>ChannelCategoryLookup.Contains()</c> como fonte de
/// autorização para criar canais. O <c>ChannelCategoryLookup</c>
/// continua a existir apenas para fins de compatibilidade de
/// categoria editorial (não decide criação de canais).
///
/// <para>
/// Thread-safety: todas as queries são async via EF Core
/// (sem cache partilhado). A BD SQLite tem WAL activado pelo
/// EF Core por defeito; leituras concorrentes são seguras.
/// </para>
/// </summary>
public sealed class CatalogResolver
{
    private readonly IDbContextFactory<ChannelCatalogDbContext> _factory;
    private readonly string _dbPath;
    private readonly IAuditService? _audit;

    /// <summary>
    /// Construtor de compatibilidade. <paramref name="audit"/> é opcional
    /// (W5.4): quando fornecido, as operações de lifecycle de Review registam
    /// auditoria; sem ele, o comportamento mantém-se o anterior.
    /// </summary>
    public CatalogResolver(
        IDbContextFactory<ChannelCatalogDbContext> factory,
        string dbPath,
        IAuditService? audit = null)
    {
        _factory = factory;
        _dbPath = dbPath;
        _audit = audit;
    }

    /// <summary>
    /// Exposição explícita da factory para serviços companheiros
    /// (e.g. <see cref="PlaylistComposerService"/>). Não usar para
    /// contornar a resolução do catálogo.
    /// </summary>
    public IDbContextFactory<ChannelCatalogDbContext> GetFactory() => _factory;

    /// <summary>
    /// Resolve uma identidade normalizada para uma decisão
    /// completa: <c>(CanonicalKey, DisplayName, EditorialCategory,
    /// EditorialGroup, PublicationPolicy, CanonicalChannelId)</c>.
    ///
    /// <para>
    /// Ordem de precedência:
    /// </para>
    /// <list type="number">
    ///   <item><b>IdentityRule</b> (ReviewOnly / Excluded) tem
    ///         prioridade absoluta: a identidade é <c>ReviewOnly</c>
    ///         ou <c>Excluded</c>, sem canal canónico.</item>
    ///   <item><b>ExternalIdentity</b> exacta (tvg-id/provider) —
    ///         passo 1/2 da ordem normativa. Ambígua → Review.</item>
    ///   <item><b>AffinityMember</b> (Kind = Channel) resolve para o
    ///         <c>CanonicalChannel</c> via <c>CanonicalChannelKey</c>.</item>
    ///   <item><b>ChannelAlias</b> (nome normalizado / alias conhecido)
    ///         resolve para o <c>CanonicalChannel</c> correspondente.</item>
    ///   <item>Caso contrário, <c>Unknown</c> (sem canal canónico).</item>
    /// </list>
    /// </summary>
    public Task<CatalogResolution> ResolveAsync(
        string normalizedIdentity, CancellationToken cancellationToken = default)
        => ResolveAsync(normalizedIdentity, originalTvgId: null, cancellationToken);

    /// <summary>
    /// Overload com evidência externa (tvg-id). O valor é
    /// canonicalizado por <see cref="ExternalIdentityNormalizer"/>
    /// antes da consulta; nulo/branco não produz passo externo. A
    /// correspondência externa exacta (passo 1/2 da ordem normativa,
    /// <c>05-CATALOGUE.md</c>) precede nome/alias e nenhum passo
    /// posterior a contradiz. Ambiguidade
    /// (mesmo valor → canais distintos) devolve
    /// <see cref="CatalogResolutionKind.Ambiguous"/>, nunca "escolher
    /// o primeiro".
    /// </summary>
    public Task<CatalogResolution> ResolveAsync(
        string normalizedIdentity,
        string? originalTvgId,
        CancellationToken cancellationToken = default)
        => ResolveAsync(normalizedIdentity, originalTvgId, policy: null, cancellationToken);

    /// <summary>
    /// Núcleo determinístico de Recognition (W5.2). Aplica a ordem
    /// normativa de <c>05-CATALOGUE.md §4/§4.1</c> e
    /// <c>34-PIPELINE-CONTRACTS.md</c> P6:
    /// </summary>
    /// <list type="number">
    ///   <item><b>IdentityRule</b> (ReviewOnly/Excluded) — prioridade
    ///         absoluta; regra explícita, nunca cria identidade.</item>
    ///   <item><b>Identidade externa exacta</b> (tvg-id/provider) —
    ///         passo 1/2. Mesmo valor em canais distintos (incluindo
    ///         namespaces diferentes) → <c>Ambiguous</c>, nunca
    ///         "escolher o primeiro".</item>
    ///   <item><b>CanonicalExact</b> — Key normalizada de canais
    ///         activos igual à identidade (passo 2).</item>
    ///   <item><b>NormalizedName</b> — <c>DisplayName</c> normalizado
    ///         (passo 3), distinto de alias.</item>
    ///   <item><b>KnownAlias</b> — <c>ChannelAlias</c> (passo 4).</item>
    ///   <item><b>ExplicitHeuristic</b> — <c>AffinityMember</c>
    ///         (Kind=Channel) → <c>AffinityGroup.CanonicalChannelKey</c>
    ///         (passo 5). Key é autoritativa; uma Key presente mas
    ///         irresolúvel não cai para o FK obsoleto.</item>
    ///   <item><b>Fuzzy</b> — apenas opt-in via
    ///         <see cref="RecognitionPolicy.FuzzyEnabled"/>. Não é
    ///         executado nesta wave.</item>
    /// </list>
    /// <para>
    /// Ties em qualquer passo produzem <c>Ambiguous</c> (nunca por ordem
    /// de BD/Id/inserção). <see cref="CatalogResolution.PolicyVersion"/>
    /// é preenchido em todos os resultados. Este caminho nunca consulta
    /// as tabelas <c>recognition_policies</c>/<c>recognition_policy_snapshots</c>.
    /// </para>
    public async Task<CatalogResolution> ResolveAsync(
        string normalizedIdentity,
        string? originalTvgId,
        RecognitionPolicy? policy,
        CancellationToken cancellationToken = default)
    {
        CatalogResolution Stamp(CatalogResolution resolution)
            => resolution with { PolicyVersion = policy?.Version };

        var hasNameIdentity = !string.IsNullOrWhiteSpace(normalizedIdentity);
        var externalValue = ExternalIdentityNormalizer.Normalize(originalTvgId);
        if (!hasNameIdentity && externalValue.Length == 0)
        {
            return Stamp(CatalogResolution.Unknown());
        }

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);

        // 0. IdentityRule (excepção explícita, prioridade absoluta).
        if (hasNameIdentity)
        {
            var rule = await context.IdentityRules
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.NormalizedIdentity == normalizedIdentity, cancellationToken);
            if (rule != null)
            {
                return Stamp(CatalogResolution.FromRule(rule));
            }
        }

        // 1. Identidade externa exacta (tvg-id/canonical/provider identity).
        if (externalValue.Length > 0)
        {
            var externalMatches = await context.ExternalIdentities
                .AsNoTracking()
                .Where(x => x.Value == externalValue)
                .Select(x => new { x.CanonicalChannelId, x.Namespace })
                .ToListAsync(cancellationToken);

            var distinctChannelIds = externalMatches
                .Select(x => x.CanonicalChannelId)
                .Distinct()
                .ToList();

            if (distinctChannelIds.Count > 1)
            {
                // Mesmo valor → canais distintos (mesmo namespace ou
                // namespaces diferentes): ambíguo. Nunca "escolher o
                // primeiro" (05-CATALOGUE.md §5).
                return Stamp(CatalogResolution.Ambiguous("external-identity-ambiguous"));
            }

            if (distinctChannelIds.Count == 1)
            {
                var matchedNamespace = externalMatches
                    .First(x => x.CanonicalChannelId == distinctChannelIds[0])
                    .Namespace;
                var canonical = await context.CanonicalChannels
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == distinctChannelIds[0], cancellationToken);
                if (canonical != null && canonical.IsEnabled)
                {
                    var method = string.Equals(
                        matchedNamespace, ExternalIdentityNamespaces.TvgId, StringComparison.Ordinal)
                        ? RecognitionMatchMethods.TvgIdExact
                        : RecognitionMatchMethods.ExternalIdentityExact;
                    return Stamp(CatalogResolution.FromCanonical(canonical, method));
                }

                // Exacta mas sem canal activo: não contradizer a
                // correspondência exacta caindo para nome/alias.
                return Stamp(CatalogResolution.Unknown());
            }
        }

        if (!hasNameIdentity)
        {
            return Stamp(CatalogResolution.Unknown());
        }

        // Candidatos para os passos 2/3: apenas canais activos. A
        // projecção evita carregar entidades completas; o canal
        // escolhido é re-obtido individualmente.
        var enabledChannels = await context.CanonicalChannels
            .AsNoTracking()
            .Where(c => c.IsEnabled)
            .Select(c => new { c.Id, c.Key, c.DisplayName })
            .ToListAsync(cancellationToken);

        // 2. CanonicalExact — Key normalizada.
        var canonicalMatches = enabledChannels
            .Where(c => ChannelNormalizer.Normalize(c.Key) == normalizedIdentity)
            .Select(c => c.Id)
            .Distinct()
            .ToList();
        if (canonicalMatches.Count > 1)
        {
            return Stamp(CatalogResolution.Ambiguous("canonical-identity-ambiguous"));
        }

        if (canonicalMatches.Count == 1)
        {
            var canonical = await context.CanonicalChannels
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == canonicalMatches[0], cancellationToken);
            if (canonical != null)
            {
                return Stamp(CatalogResolution.FromCanonical(
                    canonical, RecognitionMatchMethods.CanonicalExact));
            }
        }

        // 3. NormalizedName — DisplayName normalizado (passo próprio,
        //    distinto do alias conhecido do passo 4).
        var nameMatches = enabledChannels
            .Where(c => ChannelNormalizer.Normalize(c.DisplayName) == normalizedIdentity)
            .Select(c => c.Id)
            .Distinct()
            .ToList();
        if (nameMatches.Count > 1)
        {
            return Stamp(CatalogResolution.Ambiguous("normalized-name-ambiguous"));
        }

        if (nameMatches.Count == 1)
        {
            var canonical = await context.CanonicalChannels
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == nameMatches[0], cancellationToken);
            if (canonical != null)
            {
                return Stamp(CatalogResolution.FromCanonical(
                    canonical, RecognitionMatchMethods.NormalizedName));
            }
        }

        // 4. ChannelAlias -> CanonicalChannel (alias conhecido).
        var alias = await context.ChannelAliases
            .AsNoTracking()
            .Include(a => a.CanonicalChannel)
            .FirstOrDefaultAsync(a => a.NormalizedAlias == normalizedIdentity, cancellationToken);
        if (alias?.CanonicalChannel != null && alias.CanonicalChannel.IsEnabled)
        {
            return Stamp(CatalogResolution.FromCanonical(
                alias.CanonicalChannel, RecognitionMatchMethods.KnownAlias));
        }

        // 5. ExplicitHeuristic — AffinityMember (Kind = Channel) ->
        //    AffinityGroup -> CanonicalChannel. Membros Country não
        //    resolvem canal. Wave 9C.6: CanonicalChannelKey é a
        //    identidade de runtime autoritativa; CanonicalChannelId/nav
        //    é apenas fallback legado quando a Key está ausente.
        var member = await context.AffinityMembers
            .AsNoTracking()
            .Include(m => m.AffinityGroup)
                .ThenInclude(g => g!.CanonicalChannel)
            .FirstOrDefaultAsync(
                m => m.NormalizedMember == normalizedIdentity && m.Kind == AffinityKind.Channel,
                cancellationToken);
        if (member?.AffinityGroup != null)
        {
            var affinityKey = member.AffinityGroup.CanonicalChannelKey;
            if (!string.IsNullOrWhiteSpace(affinityKey))
            {
                var canonicalByKey = await context.CanonicalChannels
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Key == affinityKey, cancellationToken);
                if (canonicalByKey != null && canonicalByKey.IsEnabled)
                {
                    return Stamp(CatalogResolution.FromCanonical(
                        canonicalByKey, RecognitionMatchMethods.ExplicitHeuristic));
                }

                // Key presente mas sem resolução (inexistente/desactivada):
                // a Key é autoritativa, logo não cair no CanonicalChannelId
                // obsoleto.
            }
            else if (member.AffinityGroup.CanonicalChannel != null
                && member.AffinityGroup.CanonicalChannel.IsEnabled)
            {
                return Stamp(CatalogResolution.FromCanonical(
                    member.AffinityGroup.CanonicalChannel, RecognitionMatchMethods.ExplicitHeuristic));
            }
        }

        // 6. Fuzzy — opt-in (W5.3; contrato 48 F1–F14). Só corre quando a
        //    policy tem FuzzyEnabled=true e um threshold válido (0..100);
        //    caso contrário é fail-closed e cai para Unknown com diagnóstico
        //    de configuração. Universo: todos os canais activos + aliases
        //    (F11). Nunca cria identidade nem ReviewItem (F14).
        if (policy?.FuzzyEnabled == true)
        {
            // Aliases normalizados dos canais activos (F11). Carregados
            // apenas quando o passo fuzzy corre, para não onerar os passos
            // exactos quando o fuzzy está desligado.
            var aliasRows = await context.ChannelAliases
                .AsNoTracking()
                .Where(a => a.CanonicalChannel!.IsEnabled)
                .Select(a => new { a.CanonicalChannelId, a.NormalizedAlias })
                .ToListAsync(cancellationToken);

            var aliasesByChannel = aliasRows
                .GroupBy(a => a.CanonicalChannelId)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<string>)g.Select(a => a.NormalizedAlias).ToList());

            var fuzzyCandidates = enabledChannels
                .Select(c => new FuzzyCandidate(
                    c.Id,
                    c.Key,
                    c.DisplayName,
                    aliasesByChannel.TryGetValue(c.Id, out var aliases)
                        ? aliases
                        : Array.Empty<string>()))
                .ToList();

            var fuzzy = FuzzyRecognitionEvaluator.Evaluate(normalizedIdentity, fuzzyCandidates, policy);

            switch (fuzzy.Kind)
            {
                case FuzzyDecisionKind.Canonical when fuzzy.CanonicalChannelId is long fuzzyId:
                {
                    var fuzzyChannel = await context.CanonicalChannels
                        .AsNoTracking()
                        .FirstOrDefaultAsync(c => c.Id == fuzzyId, cancellationToken);
                    if (fuzzyChannel != null)
                    {
                        return Stamp(CatalogResolution.FromCanonical(
                            fuzzyChannel, RecognitionMatchMethods.Fuzzy) with
                        {
                            FuzzyScore = fuzzy.Score,
                            FuzzyDiagnostic = fuzzy.Diagnostic,
                        });
                    }

                    break;
                }

                case FuzzyDecisionKind.Ambiguous:
                    return Stamp(CatalogResolution.Ambiguous(
                        fuzzy.Diagnostic?.DecisionReason ?? FuzzyDecisionReasons.Ambiguous) with
                    {
                        FuzzyDiagnostic = fuzzy.Diagnostic,
                    });

                default:
                    // Unknown (sem candidato) ou NotExecuted (fail-closed):
                    // comportamento observável idêntico a fuzzy off, mas com
                    // diagnóstico técnico quando aplicável.
                    return Stamp(CatalogResolution.Unknown() with
                    {
                        FuzzyDiagnostic = fuzzy.Diagnostic,
                    });
            }
        }

        // 7. Unknown (sem canal canónico).
        return Stamp(CatalogResolution.Unknown());
    }

    /// <summary>
    /// Regista uma <see cref="ExternalIdentityEntity"/> de forma
    /// idempotente: o par canónico (Namespace, Value) é único.
    ///
    /// <list type="bullet">
    ///   <item>valor nulo/branco ou sem forma canónica → nada é
    ///         escrito (DL-002: sem evidência não há identidade);</item>
    ///   <item>par já existente a apontar para o mesmo canal →
    ///         <see cref="RecordExternalIdentityOutcome.Unchanged"/>;</item>
    ///   <item>par existente a apontar para outro canal → NÃO
    ///         sobrepõe (edição do operador/runtime preservada) e
    ///         devolve <see cref="RecordExternalIdentityOutcome.Conflict"/>.</item>
    /// </list>
    /// </summary>
    public async Task<RecordExternalIdentityOutcome> RecordExternalIdentityAsync(
        long canonicalChannelId,
        string? providerId,
        string @namespace,
        string? rawValue,
        string origin,
        double confidence,
        CancellationToken cancellationToken = default)
    {
        if (canonicalChannelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(canonicalChannelId));
        }

        if (string.IsNullOrWhiteSpace(@namespace))
        {
            throw new ArgumentException("namespace required", nameof(@namespace));
        }

        var value = ExternalIdentityNormalizer.Normalize(rawValue);
        if (value.Length == 0)
        {
            return RecordExternalIdentityOutcome.Ignored;
        }

        var ns = @namespace.Trim();
        var now = DateTime.UtcNow;

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var existing = await context.ExternalIdentities
            .FirstOrDefaultAsync(
                e => e.Namespace == ns && e.Value == value,
                cancellationToken);

        if (existing != null)
        {
            if (existing.CanonicalChannelId == canonicalChannelId)
            {
                return RecordExternalIdentityOutcome.Unchanged;
            }

            // Nunca sobrepõe uma associação existente (preserva
            // edições do operador/runtime). O conflito é reportado.
            return RecordExternalIdentityOutcome.Conflict;
        }

        context.ExternalIdentities.Add(new ExternalIdentityEntity
        {
            CanonicalChannelId = canonicalChannelId,
            ProviderId = string.IsNullOrWhiteSpace(providerId) ? null : providerId.Trim(),
            Namespace = ns,
            Value = value,
            Origin = string.IsNullOrWhiteSpace(origin) ? "ingestion" : origin.Trim(),
            Confidence = Math.Clamp(confidence, 0.0, 1.0),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return RecordExternalIdentityOutcome.Created;
        }
        catch (DbUpdateException)
        {
            // Corrida: outro writer inseriu o mesmo (Namespace, Value).
            // O invariante de unicidade manda; não duplicar.
            return RecordExternalIdentityOutcome.Unchanged;
        }
    }

    /// <summary>
    /// Regista (ou actualiza) um item de revisão. Idempotente: se
    /// já existir um item com o mesmo fingerprint, não cria duplicado.
    ///
    /// <para>
    /// <b>Decisão humana não é revertida.</b> Se o item já estiver
    /// <see cref="ReviewItemState.Resolved"/> ou
    /// <see cref="ReviewItemState.Ignored"/>, uma nova observação
    /// apenas actualiza <c>UpdatedAtUtc</c> (nova evidência); o
    /// estado decidido, o <c>ResolvedAtUtc</c>, a nota e o canal
    /// resolvido permanecem intactos. Um item <c>Open</c> ou
    /// <c>InReview</c> é devolvido tal como está.
    /// </para>
    /// </summary>
    public async Task<ReviewItemEntity> UpsertReviewItemAsync(
        string normalizedIdentity,
        string sourceGroup,
        string reasonSignature,
        string reasonText,
        CancellationToken cancellationToken = default,
        // W-REVIEW-01 — evidência persistente da ocorrência que originou
        // a Review. Todos opcionais para retro-compatibilidade: callers
        // sem Source/RunId (e.g. ChannelMatcher.ClassifyStreams) passam
        // null e a row fica com evidência parcial — exactamente como
        // reviews legadas.
        string? streamUrl = null,
        long? sourceId = null,
        string? streamFingerprint = null,
        string? streamFingerprintVersion = null,
        string? runId = null)
    {
        if (string.IsNullOrWhiteSpace(normalizedIdentity))
        {
            throw new ArgumentException("normalizedIdentity required", nameof(normalizedIdentity));
        }

        var fingerprint = ReviewFingerprint.Of(normalizedIdentity, sourceGroup, reasonSignature);

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var existing = await context.ReviewItems
            .FirstOrDefaultAsync(r => r.Fingerprint == fingerprint, cancellationToken);
        if (existing != null)
        {
            if (existing.State is ReviewItemState.Open or ReviewItemState.InReview)
            {
                // W-REVIEW-01 — refresca a evidência para reflectir a
                // ocorrência mais recente (a Review ainda não foi
                // decidida). Apenas campos fornecidos pelo caller são
                // tocados: nunca limpamos evidência já persistida.
                if (streamUrl != null) existing.StreamUrl = streamUrl;
                if (sourceId != null) existing.SourceId = sourceId;
                if (streamFingerprint != null) existing.StreamFingerprint = streamFingerprint;
                if (streamFingerprintVersion != null) existing.StreamFingerprintVersion = streamFingerprintVersion;
                if (runId != null) existing.RunId = runId;
                existing.UpdatedAtUtc = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);
                return existing;
            }
            // Resolved/Ignored: uma decisão humana NÃO é revertida
            // silenciosamente por uma nova observação. Registamos a
            // nova evidência actualizando o timestamp, sem resetar o
            // estado nem apagar a nota/decisão.
            existing.UpdatedAtUtc = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var now = DateTime.UtcNow;
        var entry = new ReviewItemEntity
        {
            Fingerprint = fingerprint,
            NormalizedIdentity = normalizedIdentity,
            SourceGroup = sourceGroup ?? string.Empty,
            ReasonSignature = reasonSignature ?? "no-exact-or-alias-match",
            State = ReviewItemState.Open,
            Note = reasonText ?? string.Empty,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            // W-REVIEW-01
            StreamUrl = streamUrl,
            SourceId = sourceId,
            StreamFingerprint = streamFingerprint,
            StreamFingerprintVersion = streamFingerprintVersion,
            RunId = runId,
        };
        context.ReviewItems.Add(entry);
        await context.SaveChangesAsync(cancellationToken);
        return entry;
    }

    /// <summary>
    /// Regista (ou actualiza) o ownership de um canal do Dispatcharr.
    /// Bootstrap: se o canal não existir na BD, é registado como
    /// <see cref="ChannelOwnership.Unknown"/>. Nunca classifica
    /// automaticamente como CrawlerManaged ou External.
    /// </summary>
    public async Task<DispatcharrChannelOwnershipEntity> EnsureChannelOwnershipAsync(
        long dispatcharrChannelId,
        string evidence,
        long? canonicalChannelId,
        CancellationToken cancellationToken = default,
        ChannelOwnership ownership = ChannelOwnership.Unknown)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var existing = await context.DispatcharrChannelOwnerships
            .FirstOrDefaultAsync(o => o.DispatcharrChannelId == dispatcharrChannelId, cancellationToken);
        var now = DateTime.UtcNow;
        if (existing == null)
        {
            existing = new DispatcharrChannelOwnershipEntity
            {
                DispatcharrChannelId = dispatcharrChannelId,
                Ownership = ownership,
                CanonicalChannelId = canonicalChannelId,
                FirstObservedAtUtc = now,
                LastObservedAtUtc = now,
                Evidence = evidence ?? string.Empty,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            context.DispatcharrChannelOwnerships.Add(existing);
        }
        else
        {
            existing.LastObservedAtUtc = now;
            existing.UpdatedAtUtc = now;
            // Ownership NUNCA é promovido automaticamente. Se já é
            // Unknown, fica Unknown. Se já é CrawlerManaged, fica
            // CrawlerManaged. Se já é External, fica External.
        }
        await context.SaveChangesAsync(cancellationToken);
        return existing;
    }

    /// <summary>
    /// Regista (ou actualiza) o ownership de uma stream do Dispatcharr.
    /// </summary>
    public async Task<DispatcharrStreamOwnershipEntity> EnsureStreamOwnershipAsync(
        long dispatcharrStreamId,
        long dispatcharrChannelId,
        StreamOwnership ownership,
        long? createdBySyncRunId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var existing = await context.DispatcharrStreamOwnerships
            .FirstOrDefaultAsync(o => o.DispatcharrStreamId == dispatcharrStreamId, cancellationToken);
        var now = DateTime.UtcNow;
        if (existing == null)
        {
            existing = new DispatcharrStreamOwnershipEntity
            {
                DispatcharrStreamId = dispatcharrStreamId,
                DispatcharrChannelId = dispatcharrChannelId,
                Ownership = ownership,
                CreatedBySyncRunId = createdBySyncRunId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            context.DispatcharrStreamOwnerships.Add(existing);
        }
        else
        {
            // Ownership já é uma verdade persistida. Não regredir.
            existing.UpdatedAtUtc = now;
        }
        await context.SaveChangesAsync(cancellationToken);
        return existing;
    }

    /// <summary>
    /// Regista um SyncRun e devolve o ID. Persiste apenas contadores
    /// (sem URLs nem credenciais).
    /// </summary>
    public async Task<long> RecordSyncRunAsync(SyncRunEntity run, CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        context.SyncRuns.Add(run);
        await context.SaveChangesAsync(cancellationToken);
        return run.Id;
    }

    /// <summary>
    /// PHASE W6b-2 — Fecha um <see cref="SyncRunEntity"/> persistindo o
    /// instante final, o resultado (texto sanitizado) e os contadores
    /// agregados. Um run terminado nunca regressa a <c>running</c>:
    /// actualizações repetidas são idempotentes (o último resultado
    /// prevalece) e não criam novas rows.
    /// </summary>
    public async Task<bool> FinishSyncRunAsync(
        long id,
        DateTime finishedAtUtc,
        string result,
        int countCreatedCrawlerManaged = 0,
        int countMergedIntoExternal = 0,
        int countProtectedExternalStreams = 0,
        int countRemovedCrawlerManagedStreams = 0,
        int countReviewRequired = 0,
        int countExcluded = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(result))
            throw new ArgumentException("Result é obrigatório.", nameof(result));
        var normalizedResult = result.Length > 200 ? result[..200] : result;

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var run = await context.SyncRuns.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (run == null) return false;

        run.FinishedAtUtc = finishedAtUtc;
        run.Result = normalizedResult;
        run.CountCreatedCrawlerManaged = countCreatedCrawlerManaged;
        run.CountMergedIntoExternal = countMergedIntoExternal;
        run.CountProtectedExternalStreams = countProtectedExternalStreams;
        run.CountRemovedCrawlerManagedStreams = countRemovedCrawlerManagedStreams;
        run.CountReviewRequired = countReviewRequired;
        run.CountExcluded = countExcluded;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Lista items de revisão em estado <c>Open</c>, ordenados por
    /// data de criação (mais antigos primeiro).
    /// </summary>
    public async Task<IReadOnlyList<ReviewItemEntity>> ListOpenReviewItemsAsync(
        int limit, CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.ReviewItems
            .AsNoTracking()
            .Where(r => r.State == ReviewItemState.Open)
            .OrderBy(r => r.CreatedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Lista todos os items de revisão, ordenados por data de
    /// criação (mais recentes primeiro). O dashboard usa este
    /// método para mostrar o histórico completo.
    /// </summary>
    public async Task<IReadOnlyList<ReviewItemEntity>> ListAllReviewItemsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.ReviewItems
            .AsNoTracking()
            .OrderByDescending(r => r.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// W5.5 — Lê um <see cref="ReviewItemEntity"/> pela sua identidade
    /// persistida (<see cref="ReviewItemEntity.Id"/>), usada pela API HTTP
    /// de Review (DL-120). Não é identidade de negócio do catálogo; é a
    /// chave de referência das novas rotas.
    /// </summary>
    public async Task<ReviewItemEntity?> GetReviewItemAsync(
        long id, CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.ReviewItems
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    /// <summary>
    /// Lista todos os canais canónicos (com aliases) ordenados por
    /// DisplayName. Usado pelo dashboard.
    /// </summary>
    public async Task<IReadOnlyList<CanonicalChannelEntity>> ListCanonicalChannelsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.CanonicalChannels
            .AsNoTracking()
            .Include(c => c.Aliases)
            .OrderBy(c => c.DisplayName)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Lista todo o ownership de canais do Dispatcharr.
    /// </summary>
    public async Task<IReadOnlyList<DispatcharrChannelOwnershipEntity>> ListChannelOwnershipsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.DispatcharrChannelOwnerships
            .AsNoTracking()
            .OrderBy(o => o.DispatcharrChannelId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Devolve um mapa de ownership por canal id para os ids
    /// pedidos. Canais sem registo prévio ficam
    /// <see cref="ChannelOwnership.Unknown"/> (bootstrap default).
    /// Suporta a decisão de rename: só canais CrawlerManaged podem
    /// ser renomeados.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, ChannelOwnership>> GetChannelOwnershipMapAsync(
        IReadOnlyCollection<long> dispatcharrChannelIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<long, ChannelOwnership>();
        if (dispatcharrChannelIds == null || dispatcharrChannelIds.Count == 0)
        {
            return result;
        }
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var distinctIds = dispatcharrChannelIds.Distinct().ToList();
        var rows = await context.DispatcharrChannelOwnerships
            .AsNoTracking()
            .Where(o => distinctIds.Contains(o.DispatcharrChannelId))
            .Select(o => new { o.DispatcharrChannelId, o.Ownership })
            .ToListAsync(cancellationToken);
        foreach (var r in rows)
        {
            result[r.DispatcharrChannelId] = r.Ownership;
        }
        return result;
    }

    /// <summary>
    /// Devolve um mapa de ownership por stream id para os ids
    /// pedidos. Streams sem registo prévio ficam
    /// <see cref="StreamOwnership.Unknown"/> (bootstrap default).
    /// </summary>
    public async Task<IReadOnlyDictionary<long, StreamOwnership>> GetStreamOwnershipMapAsync(
        IReadOnlyCollection<long> dispatcharrStreamIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<long, StreamOwnership>();
        if (dispatcharrStreamIds == null || dispatcharrStreamIds.Count == 0)
        {
            return result;
        }
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var distinctIds = dispatcharrStreamIds.Distinct().ToList();
        var rows = await context.DispatcharrStreamOwnerships
            .AsNoTracking()
            .Where(o => distinctIds.Contains(o.DispatcharrStreamId))
            .Select(o => new { o.DispatcharrStreamId, o.Ownership })
            .ToListAsync(cancellationToken);
        foreach (var r in rows)
        {
            result[r.DispatcharrStreamId] = r.Ownership;
        }
        return result;
    }

    /// <summary>
    /// Lista todas as regras de identidade.
    /// </summary>
    public async Task<IReadOnlyList<IdentityRuleEntity>> ListIdentityRulesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.IdentityRules
            .AsNoTracking()
            .OrderBy(r => r.NormalizedIdentity)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Cria uma nova regra de identidade. Falha se já existir
    /// uma regra com a mesma NormalizedIdentity.
    /// </summary>
    public async Task<IdentityRuleEntity> CreateIdentityRuleAsync(
        string normalizedIdentity,
        RuleDisposition disposition,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedIdentity))
        {
            throw new ArgumentException("normalizedIdentity required", nameof(normalizedIdentity));
        }

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var existing = await context.IdentityRules
            .FirstOrDefaultAsync(r => r.NormalizedIdentity == normalizedIdentity, cancellationToken);
        if (existing != null)
        {
            throw new InvalidOperationException($"Rule already exists for '{normalizedIdentity}'");
        }

        var now = DateTime.UtcNow;
        var rule = new IdentityRuleEntity
        {
            NormalizedIdentity = normalizedIdentity,
            Disposition = disposition,
            Reason = reason ?? string.Empty,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.IdentityRules.Add(rule);
        await context.SaveChangesAsync(cancellationToken);
        return rule;
    }

    /// <summary>
    /// Elimina uma regra de identidade pela sua identity normalizada.
    /// </summary>
    public async Task<bool> DeleteIdentityRuleAsync(
        string normalizedIdentity,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedIdentity))
        {
            return false;
        }

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var rule = await context.IdentityRules
            .FirstOrDefaultAsync(r => r.NormalizedIdentity == normalizedIdentity, cancellationToken);
        if (rule == null)
        {
            return false;
        }

        context.IdentityRules.Remove(rule);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<AffinityGroupEntity>> ListAffinityGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.AffinityGroups
            .AsNoTracking()
            .Include(g => g.Members)
            .Include(g => g.CanonicalChannel)
            .OrderBy(g => g.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<AffinityGroupEntity> CreateAffinityGroupAsync(
        string name,
        AffinityKind kind,
        string? canonicalChannelKey,
        string? countryCode,
        IReadOnlyList<string> normalizedMembers,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name required", nameof(name));

        var members = CleanMembers(normalizedMembers, kind);
        if (members.Count == 0)
            throw new ArgumentException("at least one member required", nameof(normalizedMembers));

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);

        long? canonicalChannelId = null;
        string? effectiveKey = null;
        string? effectiveCountry = null;

        if (kind == AffinityKind.Channel)
        {
            effectiveKey = (canonicalChannelKey ?? string.Empty).Trim();
            if (effectiveKey.Length == 0)
                throw new InvalidOperationException("CanonicalChannelKey é obrigatório para afinidades de canal.");

            var canonical = await context.CanonicalChannels
                .FirstOrDefaultAsync(c => c.Key == effectiveKey, cancellationToken);
            if (canonical == null)
                throw new InvalidOperationException($"Canal canónico '{effectiveKey}' não encontrado.");

            var already = await context.AffinityGroups
                .AnyAsync(g => g.Kind == AffinityKind.Channel && g.CanonicalChannelKey == effectiveKey, cancellationToken);
            if (already)
                throw new InvalidOperationException($"O canal '{effectiveKey}' já tem uma afinidade de canal (máximo 1).");

            canonicalChannelId = canonical.Id;
        }
        else
        {
            effectiveCountry = (countryCode ?? string.Empty).Trim();
            if (effectiveCountry.Length == 0)
                throw new InvalidOperationException("CountryCode é obrigatório para afinidades de país.");
            if (effectiveCountry.Length > 10)
                throw new InvalidOperationException("CountryCode excede 10 caracteres.");
        }

        var now = DateTime.UtcNow;
        var group = new AffinityGroupEntity
        {
            Name = name.Trim(),
            Kind = kind,
            CanonicalChannelKey = effectiveKey,
            CountryCode = effectiveCountry,
            CanonicalChannelId = canonicalChannelId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Members = members
                .Select(m => new AffinityMemberEntity
                {
                    NormalizedMember = m,
                    Kind = kind,
                    CreatedAtUtc = now,
                }).ToList(),
        };

        context.AffinityGroups.Add(group);
        await context.SaveChangesAsync(cancellationToken);
        return group;
    }

    /// <summary>
    /// Devolve as Keys dos canais canónicos que já têm uma Channel
    /// affinity. Usado pela UI para excluir esses canais do dropdown
    /// (cardinalidade 0..1).
    /// </summary>
    public async Task<IReadOnlyCollection<string>> ListChannelAffinityKeysAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.AffinityGroups
            .AsNoTracking()
            .Where(g => g.Kind == AffinityKind.Channel && g.CanonicalChannelKey != null)
            .Select(g => g.CanonicalChannelKey!)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// W5 — Garante que <paramref name="normalizedMember"/> pertence à
    /// Channel affinity do canal canónico <paramref name="canonicalChannelKey"/>,
    /// criando o grupo (cardinalidade 0..1 por canal) quando ainda não
    /// existe. Opera sobre o <paramref name="context"/> recebido para
    /// participar na transacção do chamador; <b>não</b> faz SaveChanges.
    ///
    /// <para>
    /// Idempotente: repetir com o mesmo membro não duplica o grupo nem o
    /// membro. O canal tem de existir (caso contrário
    /// <see cref="ChannelAdministrationError.ChannelNotFound"/>).
    /// </para>
    /// </summary>
    private static async Task EnsureChannelAffinityMemberCoreAsync(
        ChannelCatalogDbContext context,
        string canonicalChannelKey,
        string normalizedMember,
        DateTime now,
        CancellationToken ct)
    {
        var member = ChannelNormalizer.Normalize(normalizedMember);
        if (member.Length == 0) return;

        var key = (canonicalChannelKey ?? string.Empty).Trim();
        var channel = await context.CanonicalChannels
            .FirstOrDefaultAsync(c => c.Key == key, ct);
        if (channel == null)
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.ChannelNotFound,
                $"Canal canónico '{key}' não encontrado.");
        }

        var group = await context.AffinityGroups
            .Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.Kind == AffinityKind.Channel && g.CanonicalChannelKey == key, ct);

        if (group == null)
        {
            var name = string.IsNullOrWhiteSpace(channel.DisplayName) ? key : channel.DisplayName;
            group = new AffinityGroupEntity
            {
                Name = name,
                Kind = AffinityKind.Channel,
                CanonicalChannelKey = key,
                CanonicalChannelId = channel.Id,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Members = new List<AffinityMemberEntity>
                {
                    new()
                    {
                        NormalizedMember = member,
                        Kind = AffinityKind.Channel,
                        CreatedAtUtc = now,
                    },
                },
            };
            context.AffinityGroups.Add(group);
            return;
        }

        var exists = group.Members.Any(m =>
            m.Kind == AffinityKind.Channel &&
            string.Equals(m.NormalizedMember, member, StringComparison.Ordinal));
        if (!exists)
        {
            group.Members.Add(new AffinityMemberEntity
            {
                NormalizedMember = member,
                Kind = AffinityKind.Channel,
                CreatedAtUtc = now,
            });
            group.UpdatedAtUtc = now;
        }
    }

    /// <summary>
    /// W5 — Wrapper público de <see cref="EnsureChannelAffinityMemberCoreAsync"/>
    /// que abre o seu próprio contexto e persiste. Usado por testes e por
    /// consumidores fora da transacção de aprovação.
    /// </summary>
    public async Task EnsureChannelAffinityMemberAsync(
        string canonicalChannelKey,
        string normalizedMember,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        await EnsureChannelAffinityMemberCoreAsync(
            context, canonicalChannelKey, normalizedMember, DateTime.UtcNow, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Limpa e deduplica os membros de uma afinidade. Membros de
    /// uma <see cref="AffinityKind.Channel"/> affinity são
    /// normalizados via <see cref="ChannelNormalizer.Normalize"/>
    /// (forma única matchable). Membros <see cref="AffinityKind.Country"/>
    /// são apenas trim'd — são tokens de país, não identidades de canal.
    /// </summary>
    private static List<string> CleanMembers(IReadOnlyList<string>? normalizedMembers, AffinityKind kind)
    {
        if (normalizedMembers == null) return new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in normalizedMembers)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var value = raw.Trim();
            if (kind == AffinityKind.Channel)
            {
                value = ChannelNormalizer.Normalize(value);
            }
            if (value.Length == 0) continue;
            if (seen.Add(value)) result.Add(value);
        }
        return result;
    }

    public async Task<AffinityGroupEntity?> UpdateAffinityGroupAsync(
        long groupId,
        string name,
        AffinityKind kind,
        string? canonicalChannelKey,
        string? countryCode,
        IReadOnlyList<string> normalizedMembers,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name required", nameof(name));

        var members = CleanMembers(normalizedMembers, kind);
        if (members.Count == 0)
            throw new ArgumentException("at least one member required", nameof(normalizedMembers));

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);

        var group = await context.AffinityGroups
            .Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group == null) return null;

        if (kind != group.Kind)
            throw new InvalidOperationException("Não é possível alterar o tipo de uma afinidade existente.");

        if (kind == AffinityKind.Channel)
        {
            // A identidade (canal) é imutável após a criação. A UI
            // não permite alterá-la; qualquer tentativa é rejeitada
            // para não mudar implicitamente a afinidade de canal.
            var incoming = (canonicalChannelKey ?? string.Empty).Trim();
            if (incoming.Length > 0
                && !string.Equals(incoming, group.CanonicalChannelKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("O canal de uma afinidade existente não pode ser alterado.");
            }
        }
        else
        {
            var effectiveCountry = (countryCode ?? string.Empty).Trim();
            if (effectiveCountry.Length == 0)
                throw new InvalidOperationException("CountryCode é obrigatório para afinidades de país.");
            if (effectiveCountry.Length > 10)
                throw new InvalidOperationException("CountryCode excede 10 caracteres.");
            group.CountryCode = effectiveCountry;
        }

        group.Name = name.Trim();
        group.UpdatedAtUtc = DateTime.UtcNow;

        context.AffinityMembers.RemoveRange(group.Members);
        group.Members.Clear();

        var now = DateTime.UtcNow;
        foreach (var m in members)
        {
            group.Members.Add(new AffinityMemberEntity
            {
                NormalizedMember = m,
                Kind = group.Kind,
                AffinityGroupId = group.Id,
                CreatedAtUtc = now,
            });
        }

        await context.SaveChangesAsync(cancellationToken);
        return group;
    }

    public async Task<bool> DeleteAffinityGroupAsync(
        long groupId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var group = await context.AffinityGroups
            .FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group == null) return false;

        context.AffinityGroups.Remove(group);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ────────────────────────────────────────────────────────────────
    // W5.4 — Lifecycle de Review (DL-105/DL-119; 33-STATE-MACHINES.md).
    // Operações administrativas auditadas. O motor puro de transições
    // vive em ReviewLifecycle; aqui aplica-se persistência + auditoria.
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>Open → InReview</c>. Idempotente se já estiver <c>InReview</c>.
    /// </summary>
    public Task<ReviewLifecycleResult?> BeginReviewAsync(
        string fingerprint,
        AuditActor? actor = null,
        CancellationToken cancellationToken = default)
        => ApplyLifecycleTransitionAsync(
            fingerprint,
            target: ReviewItemState.InReview,
            operation: "catalog.review.begin",
            note: null,
            auditDetail: null,
            setApprovedChannel: false,
            approvedCanonicalChannelId: null,
            clearApprovedChannel: false,
            actor: actor,
            cancellationToken: cancellationToken);

    /// <summary>
    /// <c>InReview → Resolved</c>. Requer que o item esteja em tratamento
    /// (<c>InReview</c>); nunca cria identidade nem catálogo.
    /// </summary>
    public Task<ReviewLifecycleResult?> ResolveReviewAsync(
        string fingerprint,
        long? approvedCanonicalChannelId = null,
        string? note = null,
        AuditActor? actor = null,
        CancellationToken cancellationToken = default)
        => ApplyLifecycleTransitionAsync(
            fingerprint,
            target: ReviewItemState.Resolved,
            operation: "catalog.review.resolve",
            note: note,
            auditDetail: note,
            setApprovedChannel: true,
            approvedCanonicalChannelId: approvedCanonicalChannelId,
            clearApprovedChannel: false,
            actor: actor,
            cancellationToken: cancellationToken);

    /// <summary>
    /// <c>Open → Ignored</c> / <c>InReview → Ignored</c>. O motivo é
    /// obrigatório (não vazio). Idempotente se já estiver <c>Ignored</c>.
    /// </summary>
    public Task<ReviewLifecycleResult?> IgnoreReviewAsync(
        string fingerprint,
        string reason,
        AuditActor? actor = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.InvalidInput,
                "Motivo é obrigatório para ignorar um item de revisão.");
        }

        return ApplyLifecycleTransitionAsync(
            fingerprint,
            target: ReviewItemState.Ignored,
            operation: "catalog.review.ignore",
            note: reason.Trim(),
            auditDetail: reason.Trim(),
            setApprovedChannel: false,
            approvedCanonicalChannelId: null,
            clearApprovedChannel: true,
            actor: actor,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// <c>Resolved → Open</c> / <c>Ignored → Open</c> (reabertura manual
    /// auditada). A justificação é obrigatória (não vazia). A deteção
    /// automática de evidência materialmente incompatível está fora desta
    /// wave (DL-119).
    /// </summary>
    public Task<ReviewLifecycleResult?> ReopenReviewAsync(
        string fingerprint,
        string justification,
        AuditActor? actor = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(justification))
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.InvalidInput,
                "Justificação é obrigatória para reabrir um item de revisão.");
        }

        return ApplyLifecycleTransitionAsync(
            fingerprint,
            target: ReviewItemState.Open,
            operation: "catalog.review.reopen",
            note: null,
            auditDetail: justification.Trim(),
            setApprovedChannel: false,
            approvedCanonicalChannelId: null,
            clearApprovedChannel: true,
            actor: actor,
            cancellationToken: cancellationToken);
    }

    private async Task<ReviewLifecycleResult?> ApplyLifecycleTransitionAsync(
        string fingerprint,
        ReviewItemState target,
        string operation,
        string? note,
        string? auditDetail,
        bool setApprovedChannel,
        long? approvedCanonicalChannelId,
        bool clearApprovedChannel,
        AuditActor? actor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.InvalidInput,
                "Fingerprint é obrigatório.");
        }

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var item = await context.ReviewItems
            .FirstOrDefaultAsync(r => r.Fingerprint == fingerprint, cancellationToken);
        if (item == null) return null;

        var prior = item.State;
        if (prior == target)
        {
            // Reexecução idempotente: já está no estado pretendido.
            return new ReviewLifecycleResult(item, prior, Changed: false, operation);
        }

        if (!ReviewLifecycle.CanTransition(prior, target))
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.ReviewConflict,
                $"Transição de Review inválida: {prior} → {target}.");
        }

        var now = DateTime.UtcNow;
        item.State = target;
        item.UpdatedAtUtc = now;

        if (ReviewLifecycle.IsTerminal(target))
        {
            item.ResolvedAtUtc = now;
        }
        else if (target == ReviewItemState.Open)
        {
            item.ResolvedAtUtc = null;
        }

        if (setApprovedChannel)
        {
            item.ApprovedCanonicalChannelId = approvedCanonicalChannelId;
        }
        if (clearApprovedChannel)
        {
            item.ApprovedCanonicalChannelId = null;
        }
        if (note != null)
        {
            item.Note = TruncateReviewNote(note);
        }

        await context.SaveChangesAsync(cancellationToken);
        await RecordReviewAuditAsync(operation, fingerprint, prior, target, auditDetail, actor, cancellationToken);

        return new ReviewLifecycleResult(item, prior, Changed: true, operation);
    }

    private async Task RecordReviewAuditAsync(
        string operation,
        string fingerprint,
        ReviewItemState? before,
        ReviewItemState? after,
        string? detail,
        AuditActor? actor,
        CancellationToken cancellationToken)
    {
        if (_audit is null) return;

        await _audit.RecordAsync(
            new AuditRecord
            {
                // Actor de sistema com nome estável quando o chamador não
                // fornece um actor humano (W5.5 injectará o actor da sessão).
                Actor = actor ?? AuditActor.System("catalog-review"),
                Operation = operation,
                ObjectType = "review-item",
                ObjectId = fingerprint,
                Before = before is null ? null : new { state = before.Value.ToString() },
                After = after is null ? null : new { state = after.Value.ToString() },
                Detail = detail,
            },
            cancellationToken);
    }

    private static string TruncateReviewNote(string note)
        => note.Length > 500 ? note.Substring(0, 500) : note;

    /// <summary>
    /// Compatibilidade (W5.4): o antigo <c>Approve</c> passa a terminar em
    /// <see cref="ReviewItemState.Resolved"/>. Um item <c>Open</c> percorre
    /// <c>Open → InReview → Resolved</c> (nunca um salto directo), preservando
    /// o resultado observável da API existente. A auditoria desta via é feita
    /// pelo chamador HTTP (W5.5).
    /// </summary>
    public async Task<ReviewItemEntity?> ApproveReviewAsync(
        string fingerprint,
        long? approvedCanonicalChannelId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var item = await context.ReviewItems
            .FirstOrDefaultAsync(r => r.Fingerprint == fingerprint, cancellationToken);
        if (item == null) return null;

        if (item.State == ReviewItemState.Resolved)
        {
            if (item.ApprovedCanonicalChannelId == approvedCanonicalChannelId)
            {
                return item; // idempotente
            }
            throw new ChannelAdministrationException(
                ChannelAdministrationError.ReviewConflict,
                "O item de revisão já foi resolvido com outro canal canónico.");
        }
        if (item.State == ReviewItemState.Ignored)
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.ReviewConflict,
                "O item de revisão já foi ignorado; uma decisão fechada não é reaberta.");
        }

        ApplyLegacyResolveTransition(item, approvedCanonicalChannelId, DateTime.UtcNow);
        await context.SaveChangesAsync(cancellationToken);
        return item;
    }

    /// <summary>
    /// Compatibilidade (W5.4): o antigo <c>Exclude</c> passa a terminar em
    /// <see cref="ReviewItemState.Ignored"/>. Usa o motivo administrativo
    /// por defeito (não vazio). Auditoria pelo chamador HTTP (W5.5).
    /// </summary>
    public async Task<ReviewItemEntity?> ExcludeReviewAsync(
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var item = await context.ReviewItems
            .FirstOrDefaultAsync(r => r.Fingerprint == fingerprint, cancellationToken);
        if (item == null) return null;

        if (item.State == ReviewItemState.Ignored)
        {
            return item; // idempotente
        }
        if (item.State == ReviewItemState.Resolved)
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.ReviewConflict,
                "O item de revisão já foi resolvido; uma decisão fechada não é reaberta.");
        }

        ApplyIgnoreTransition(item, DefaultExclusionReason, DateTime.UtcNow);
        await context.SaveChangesAsync(cancellationToken);
        return item;
    }

    /// <summary>
    /// Aplica o caminho de aprovação legacy composto por transições válidas:
    /// <c>Open → InReview → Resolved</c> ou <c>InReview → Resolved</c>.
    /// </summary>
    private static void ApplyLegacyResolveTransition(
        ReviewItemEntity item, long? approvedCanonicalChannelId, DateTime now)
    {
        if (item.State == ReviewItemState.Resolved)
        {
            // Reexecução idempotente da aprovação.
            item.UpdatedAtUtc = now;
            item.ApprovedCanonicalChannelId = approvedCanonicalChannelId;
            return;
        }
        if (item.State == ReviewItemState.Open)
        {
            ReviewLifecycle.EnsureTransition(ReviewItemState.Open, ReviewItemState.InReview);
            item.State = ReviewItemState.InReview;
        }
        ReviewLifecycle.EnsureTransition(item.State, ReviewItemState.Resolved);
        item.State = ReviewItemState.Resolved;
        item.ResolvedAtUtc = now;
        item.UpdatedAtUtc = now;
        item.ApprovedCanonicalChannelId = approvedCanonicalChannelId;
    }

    private static void ApplyIgnoreTransition(ReviewItemEntity item, string reason, DateTime now)
    {
        ReviewLifecycle.EnsureTransition(item.State, ReviewItemState.Ignored);
        item.State = ReviewItemState.Ignored;
        item.ResolvedAtUtc = now;
        item.UpdatedAtUtc = now;
        item.ApprovedCanonicalChannelId = null;
        item.Note = TruncateReviewNote(reason);
    }

    private const string DefaultExclusionReason = "excluído por decisão administrativa";

    /// <summary>
    /// W6b-1 — Aplica uma aprovação/exclusão de Review com mudança de
    /// catálogo EXPLÍCITA e auditável (<c>05-CATALOGUE.md §9</c>).
    /// Devolve <c>null</c> se o fingerprint não existir.
    ///
    /// <list type="bullet">
    ///   <item><see cref="ReviewApprovalAction.AddAlias"/> — exige
    ///         <c>CanonicalChannelKey</c>; adiciona o valor observado
    ///         (ou <c>Alias</c> explícito) como alias normalizado do
    ///         canal existente;</item>
    ///   <item><see cref="ReviewApprovalAction.CreateChannel"/> — exige
    ///         <c>Channel.Key</c> e <c>Channel.Name</c>; cria o canal
    ///         canónico declarado pelo administrador e adiciona o
    ///         alias — é o único caminho de criação a partir de uma
    ///         Review (nunca implícito, DL-002);</item>
    ///   <item><see cref="ReviewApprovalAction.Exclude"/> — marca a
    ///         Review como excluída com uma razão; não altera o
    ///         catálogo.</item>
    /// </list>
    ///
    /// <para>
    /// Idempotência/conflictos: reaplicar a MESMA mudança declarada
    /// não duplica alias/canais nem reabre a decisão; uma mudança
    /// declarada diferente sobre uma Review já resolvida levanta
    /// <see cref="ChannelAdministrationError.ReviewConflict"/>.
    /// </para>
    /// </summary>
    public async Task<ReviewApprovalResult?> ApplyReviewApprovalAsync(
        string fingerprint,
        ReviewApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.InvalidInput,
                "Fingerprint é obrigatório.");
        }
        if (decision is null)
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.InvalidInput,
                "Decisão de aprovação é obrigatória.");
        }

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var item = await context.ReviewItems
            .FirstOrDefaultAsync(r => r.Fingerprint == fingerprint, cancellationToken);
        if (item == null) return null;

        return decision.Action switch
        {
            ReviewApprovalAction.AddAlias =>
                await ApplyAddAliasAsync(context, item, decision, cancellationToken),
            ReviewApprovalAction.CreateChannel =>
                await ApplyCreateChannelAsync(context, item, decision, cancellationToken),
            ReviewApprovalAction.Exclude =>
                await ApplyExcludeAsync(context, item, decision, cancellationToken),
            _ => throw new ChannelAdministrationException(
                ChannelAdministrationError.InvalidInput,
                "Acção de aprovação desconhecida."),
        };
    }

    private async Task<ReviewApprovalResult> ApplyAddAliasAsync(
        ChannelCatalogDbContext context,
        ReviewItemEntity item,
        ReviewApprovalDecision decision,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(decision.CanonicalChannelKey))
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.InvalidInput,
                "canonicalChannelKey é obrigatório para action=add-alias.");
        }

        var key = decision.CanonicalChannelKey!.Trim();
        var alias = NormalizeReviewAlias(decision.Alias, item.NormalizedIdentity);
        var priorState = item.State;
        var priorApprovedId = item.ApprovedCanonicalChannelId;

        var channel = await context.CanonicalChannels
            .FirstOrDefaultAsync(c => c.Key == key, cancellationToken);
        if (channel == null)
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.ChannelNotFound,
                $"Canal canónico '{key}' não encontrado.");
        }

        var existing = await context.ChannelAliases
            .FirstOrDefaultAsync(a => a.NormalizedAlias == alias, cancellationToken);
        if (existing != null && existing.CanonicalChannelId != channel.Id)
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.AliasConflict,
                $"Alias '{alias}' já pertence ao canal #{existing.CanonicalChannelId}.");
        }

        EnsureOpenOrSameApproval(item, channel.Id);

        var now = DateTime.UtcNow;
        ChannelAliasEntity aliasEntity;
        var catalogueChanged = false;
        if (existing != null)
        {
            aliasEntity = existing;
        }
        else
        {
            aliasEntity = new ChannelAliasEntity
            {
                NormalizedAlias = alias,
                CanonicalChannelId = channel.Id,
                CreatedAtUtc = now,
            };
            context.ChannelAliases.Add(aliasEntity);
            channel.UpdatedAtUtc = now;
            catalogueChanged = true;
        }

        // W5 — Add Alias alimenta exactamente UMA Channel affinity do
        // canal aprovado, no mesmo contexto/transacção (idempotente).
        await EnsureChannelAffinityMemberCoreAsync(context, channel.Key, alias, now, cancellationToken);

        ApplyLegacyResolveTransition(item, channel.Id, now);

        // W-REVIEW-02 — materializar ChannelSource no mesmo DbContext
        // ANTES do SaveChanges final. Se a materialização for saltada
        // (gate), retorna null sem erro; a aprovação prossegue
        // normalmente (alias + ReviewItem committed).
        var materialized = await MaterializeChannelSourceFromReviewAsync(context, item, cancellationToken);

        // W-REVIEW-02B — channel_sources UNIQUE discrimination. The reload
        // callback detaches the failed Added entity, reloads the existing
        // row by fingerprint (the only identity key), and re-issues SaveChanges
        // so the alias + review-item changes get committed.
        try
        {
            await SaveReviewApprovalAsync(context, alias, cancellationToken);
        }
        catch (ChannelAdministrationException ex) when (
            ex.Error == ChannelAdministrationError.AliasConflict
            && materialized != null
            && ex.Message.Contains("UNIQUE em channel_sources", StringComparison.Ordinal))
        {
            var reloaded = await ReloadChannelSourceOnUniqueAsync(
                context,
                canonicalChannelId: channel.Id,
                sourceId: item.SourceId!.Value,
                sanitizedUrl: materialized.StreamUrl,
                fingerprint: materialized.Fingerprint,
                fingerprintVersion: materialized.FingerprintVersion,
                cancellationToken);
            if (reloaded == null) throw;
        }

        return new ReviewApprovalResult(
            item, priorState, priorApprovedId, channel, aliasEntity,
            Idempotent: !catalogueChanged,
            CatalogueChanged: catalogueChanged,
            Action: "add-alias",
            MaterializedChannelSource: materialized);
    }

    private async Task<ReviewApprovalResult> ApplyCreateChannelAsync(
        ChannelCatalogDbContext context,
        ReviewItemEntity item,
        ReviewApprovalDecision decision,
        CancellationToken cancellationToken)
    {
        var spec = decision.Channel;
        if (spec == null || string.IsNullOrWhiteSpace(spec.Key) || string.IsNullOrWhiteSpace(spec.Name))
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.InvalidInput,
                "channel.key e channel.name são obrigatórios para action=create-channel.");
        }

        ValidateKey(spec.Key);
        ValidateDisplayName(spec.Name);
        var key = spec.Key.Trim();
        var alias = NormalizeReviewAlias(decision.Alias, item.NormalizedIdentity);
        var priorState = item.State;
        var priorApprovedId = item.ApprovedCanonicalChannelId;

        CanonicalChannelEntity channel;
        var channelCreated = false;

        // W-REVIEW-02B — explicit transaction wrapping canonical-create
        // (when applicable) + alias + review-item + ChannelSource. The
        // pre-existing orphan-canonical risk between SaveChanges #1 and #2
        // is now closed: a failure on either rolls back both. The
        // pre-write queries (Resolved/Ignored dispatch, byKey pre-check)
        // stay OUTSIDE the transaction — they don't perform writes that
        // would need rolling back, and throwing ReviewConflict/DuplicateKey
        // before any insert avoids opening a needless transaction.
        await using var tx = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            if (item.State == ReviewItemState.Resolved)
            {
                var approved = item.ApprovedCanonicalChannelId.HasValue
                    ? await context.CanonicalChannels.FirstOrDefaultAsync(
                        c => c.Id == item.ApprovedCanonicalChannelId!.Value, cancellationToken)
                    : null;
                if (approved == null || !string.Equals(approved.Key, key, StringComparison.Ordinal))
                {
                    throw new ChannelAdministrationException(
                        ChannelAdministrationError.ReviewConflict,
                        "O item de revisão já foi resolvido com outra mudança de catálogo.");
                }
                channel = approved;
            }
            else if (item.State == ReviewItemState.Ignored)
            {
                throw new ChannelAdministrationException(
                    ChannelAdministrationError.ReviewConflict,
                    "O item de revisão já foi ignorado; uma decisão fechada não é reaberta.");
            }
            else
            {
                var byKey = await context.CanonicalChannels
                    .FirstOrDefaultAsync(c => c.Key == key, cancellationToken);
                if (byKey != null)
                {
                    throw new ChannelAdministrationException(
                        ChannelAdministrationError.DuplicateKey,
                        $"Já existe um canal canónico com a key '{key}' (id={byKey.Id}).");
                }

                var createdNow = DateTime.UtcNow;
                channel = new CanonicalChannelEntity
                {
                    Key = key,
                    DisplayName = spec.Name.Trim(),
                    Country = NormalizeCountry(spec.Country),
                    EditorialCategory = spec.EditorialCategory ?? EditorialCategory.Live,
                    EditorialGroup = spec.EditorialGroup ?? CanonicalEditorialGroup.Other,
                    PublicationPolicy = spec.PublicationPolicy ?? PublicationPolicy.CreateEligible,
                    IsEnabled = spec.IsEnabled ?? true,
                    CreatedAtUtc = createdNow,
                    UpdatedAtUtc = createdNow,
                };
                context.CanonicalChannels.Add(channel);
                // W-REVIEW-02B — SaveChanges #1 of two, wrapped in tx.
                // On UNIQUE violation of canonical_channels.Key (race between
                // pre-check and save) translate to DuplicateKey domain
                // exception. The outer transaction will roll back.
                try
                {
                    await context.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex) when (IsCanonicalChannelsKeyUniqueViolation(ex))
                {
                    throw new ChannelAdministrationException(
                        ChannelAdministrationError.DuplicateKey,
                        $"Já existe um canal canónico com a key '{key}' (corrida concorrente).");
                }
                channelCreated = true;
            }

            var existing = await context.ChannelAliases
                .FirstOrDefaultAsync(a => a.NormalizedAlias == alias, cancellationToken);
            if (existing != null && existing.CanonicalChannelId != channel.Id)
            {
                throw new ChannelAdministrationException(
                    ChannelAdministrationError.AliasConflict,
                    $"Alias '{alias}' já pertence ao canal #{existing.CanonicalChannelId}.");
            }

            var now = DateTime.UtcNow;
            ChannelAliasEntity aliasEntity;
            var aliasCreated = false;
            if (existing != null)
            {
                aliasEntity = existing;
            }
            else
            {
                aliasEntity = new ChannelAliasEntity
                {
                    NormalizedAlias = alias,
                    // W-REVIEW-02 — FK-id (não navigation property) atribuído
                    // AQUI, depois do SaveChanges intermédio ter popular
                    // `channel.Id`. Esta combinação preserva a estrutura
                    // existente (FK-id directo) sem regressão de
                    // comportamento.
                    CanonicalChannelId = channel.Id,
                    CreatedAtUtc = now,
                };
                context.ChannelAliases.Add(aliasEntity);
                channel.UpdatedAtUtc = now;
                aliasCreated = true;
            }

            // W5 — Create Channel alimenta exactamente UMA Channel affinity
            // do canal criado, no mesmo contexto/transacção (idempotente).
            await EnsureChannelAffinityMemberCoreAsync(context, channel.Key, alias, now, cancellationToken);

            ApplyLegacyResolveTransition(item, channel.Id, now);

            // W-REVIEW-02 — materializar ChannelSource no mesmo DbContext
            // ANTES do SaveChanges final. Atomicidade: alias + review-item
            // + ChannelSource committed num único SaveChanges, dentro do
            // mesmo transaction que cobre o canonical-create (quando
            // aplicável).
            var materialized = await MaterializeChannelSourceFromReviewAsync(context, item, cancellationToken);

            // W-REVIEW-02B — channel_sources UNIQUE discrimination. The
            // reload callback detaches the failed Added entity, reloads
            // the existing row by fingerprint (the only identity key), and
            // re-issues SaveChanges so the alias + review-item changes
            // (and the canonical, when applicable) get committed even when
            // the channel_sources INSERT was rejected by the new filtered
            // UNIQUE index.
            try
            {
                await SaveReviewApprovalAsync(context, alias, cancellationToken);
            }
            catch (ChannelAdministrationException ex) when (
                ex.Error == ChannelAdministrationError.AliasConflict
                && materialized != null
                && ex.Message.Contains("UNIQUE em channel_sources", StringComparison.Ordinal))
            {
                var reloaded = await ReloadChannelSourceOnUniqueAsync(
                    context,
                    canonicalChannelId: channel.Id,
                    sourceId: item.SourceId!.Value,
                    sanitizedUrl: materialized.StreamUrl,
                    fingerprint: materialized.Fingerprint,
                    fingerprintVersion: materialized.FingerprintVersion,
                    cancellationToken);
                if (reloaded == null) throw;
            }

            var changed = channelCreated || aliasCreated;
            var result = new ReviewApprovalResult(
                item, priorState, priorApprovedId, channel, aliasEntity,
                Idempotent: !changed,
                CatalogueChanged: changed,
                Action: "create-channel",
                MaterializedChannelSource: materialized);

            await tx.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<ReviewApprovalResult> ApplyExcludeAsync(
        ChannelCatalogDbContext context,
        ReviewItemEntity item,
        ReviewApprovalDecision decision,
        CancellationToken cancellationToken)
    {
        var reason = string.IsNullOrWhiteSpace(decision.Reason)
            ? DefaultExclusionReason
            : decision.Reason!.Trim();
        var priorState = item.State;
        var priorApprovedId = item.ApprovedCanonicalChannelId;

        if (item.State == ReviewItemState.Resolved)
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.ReviewConflict,
                "O item de revisão já foi resolvido; uma decisão fechada não é reaberta.");
        }

        if (item.State == ReviewItemState.Ignored)
        {
            return new ReviewApprovalResult(
                item, priorState, priorApprovedId, null, null,
                Idempotent: true, CatalogueChanged: false, Action: "exclude");
        }

        ApplyIgnoreTransition(item, reason, DateTime.UtcNow);
        await context.SaveChangesAsync(cancellationToken);

        // W-REVIEW-02 — Exclude nunca materializa ChannelSource (por
        // design). ApplyIgnoreTransition zera ApprovedCanonicalChannelId,
        // fechando o gate de qualquer materialização acidental.
        return new ReviewApprovalResult(
            item, priorState, priorApprovedId, null, null,
            Idempotent: false, CatalogueChanged: false, Action: "exclude");
    }

    /// <summary>
    /// W-REVIEW-02 — Materializa um <see cref="ChannelSourceEntity"/> a partir
    /// da evidência persistida no próprio <see cref="ReviewItemEntity"/>
    /// aprovado. A materialização corre no MESMO <c>DbContext</c> da aprovação,
    /// sendo commitada no mesmo <c>SaveChangesAsync</c> final (atomicidade
    /// transaccional dentro do mesmo implicit EF transaction).
    ///
    /// <para><b>Gate (pré-requisitos):</b></para>
    /// <list type="bullet">
    ///   <item><c>item.ApprovedCanonicalChannelId.HasValue</c> — set pela transição de resolução;</item>
    ///   <item><c>!string.IsNullOrWhiteSpace(item.StreamUrl)</c> — evidência da ocorrência original;</item>
    ///   <item><c>item.SourceId.HasValue &amp;&amp; item.SourceId.Value > 0</c> — Source resolvida pelo ingestion.</item>
    /// </list>
    /// <para>
    /// Se qualquer pré-requisito falhar, devolve <c>false</c> e NÃO
    /// modifica o contexto. Reviews legadas (pré-W-REVIEW-01) têm
    /// <c>StreamUrl</c>/<c>SourceId</c> a <c>null</c> e ficam
    /// correctamente saltadas — a aprovação prossegue sem
    /// materialização e o operador pode reprocessar com nova ingestion
    /// para activar a evidência.
    /// </para>
    /// <para>
    /// Não inventa evidência, não consulta <c>DiscoveryCandidate</c>,
    /// não procura o URL noutro Run.
    /// </para>
    /// </summary>
    private async Task<ChannelSourceEntity?> MaterializeChannelSourceFromReviewAsync(
        ChannelCatalogDbContext context,
        ReviewItemEntity item,
        CancellationToken cancellationToken)
    {
        if (!item.ApprovedCanonicalChannelId.HasValue) return null;
        if (string.IsNullOrWhiteSpace(item.StreamUrl)) return null;
        if (!item.SourceId.HasValue || item.SourceId.Value <= 0) return null;

        // A approval já passou pelo EnsureOpenOrSameApproval / state-guard;
        // o item está tracked em `context` (Open→Resolved, com
        // ApprovedCanonicalChannelId acabado de setar). Materializamos
        // sem qualquer lookup adicional: os 3 campos mínimos estão
        // garantidos pelo gate.
        var materialized = await RecordChannelSourceAsync(
            canonicalChannelId: item.ApprovedCanonicalChannelId.Value,
            sourceId: item.SourceId.Value,
            streamUrl: item.StreamUrl!,
            quality: StreamQuality.Unknown,
            epg: EpgState.Unknown,
            availability: AvailabilityState.Discovered,
            matchConfidence: 1.0,
            matchMethod: RecognitionMatchMethods.ReviewApproval,
            externalStreamId: item.NormalizedIdentity,
            isEnabled: true,
            cancellationToken: cancellationToken,
            context: context);

        return materialized;
    }

    private static void EnsureOpenOrSameApproval(ReviewItemEntity item, long channelId)
    {
        if (item.State == ReviewItemState.Ignored)
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.ReviewConflict,
                "O item de revisão já foi ignorado; uma decisão fechada não é reaberta.");
        }
        if (item.State == ReviewItemState.Resolved
            && item.ApprovedCanonicalChannelId.HasValue
            && item.ApprovedCanonicalChannelId.Value != channelId)
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.ReviewConflict,
                "O item de revisão já foi resolvido com outro canal canónico.");
        }
    }

    private static string NormalizeReviewAlias(string? explicitAlias, string observed)
    {
        var raw = string.IsNullOrWhiteSpace(explicitAlias) ? observed : explicitAlias;
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.InvalidInput,
                "Alias é obrigatório (nem o valor observado nem alias explícito fornecidos).");
        }
        var normalized = ChannelNormalizer.Normalize(raw);
        if (normalized.Length == 0)
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.InvalidInput,
                $"Alias '{raw}' normaliza para vazio e não é matchable.");
        }
        return normalized;
    }

    private static async Task SaveReviewApprovalAsync(
        ChannelCatalogDbContext context,
        string? alias,
        CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsChannelSourcesUniqueViolation(ex))
        {
            // W-REVIEW-02B — channel_sources UNIQUE violation. The caller has
            // its own try/catch (with reload callback) for this branch — when
            // we reach here, no callback was supplied. Translate to a domain
            // exception with a clear message; the caller may catch it before
            // falling through.
            throw new ChannelAdministrationException(
                ChannelAdministrationError.AliasConflict,
                "UNIQUE em channel_sources violada; nenhum reload configurado para esta operação.");
        }
        catch (DbUpdateException)
        {
            // channel_aliases UNIQUE (or any other DbUpdateException) — alias
            // uniqueness manda; não duplicar.
            throw new ChannelAdministrationException(
                ChannelAdministrationError.AliasConflict,
                alias is null
                    ? "Conflito de unicidade ao resolver a Review."
                    : $"Alias '{alias}' já existe (corrida concorrente).");
        }
    }

    /// <summary>
    /// W-REVIEW-02B — Discriminates <c>DbUpdateException</c> by inspecting the
    /// inner <see cref="SqliteException"/>: code 19 (<c>SQLITE_CONSTRAINT</c>)
    /// combined with substring match on the index name in the error message.
    /// SQLite error messages include the index name when a UNIQUE INDEX trips,
    /// which makes the message substring the most portable discriminator
    /// across SQLite versions.
    /// </summary>
    private static bool IsChannelSourcesUniqueViolation(DbUpdateException ex)
        => IsSqliteUniqueViolationOnIndex(ex, "IX_channel_sources_Channel_Source_Fingerprint_Unique");

    private static bool IsChannelAliasesUniqueViolation(DbUpdateException ex)
        => IsSqliteUniqueViolationOnIndex(ex, "IX_channel_aliases_NormalizedAlias");

    private static bool IsCanonicalChannelsKeyUniqueViolation(DbUpdateException ex)
        => IsSqliteUniqueViolationOnIndex(ex, "IX_canonical_channels_Key");

    private static bool IsSqliteUniqueViolationOnIndex(DbUpdateException ex, string indexName)
    {
        if (ex.InnerException is not SqliteException sql) return false;
        // SqliteErrorCode 19 == SQLITE_CONSTRAINT (incl. UNIQUE).
        if (sql.SqliteErrorCode != 19) return false;
        // The error message includes the index name for UNIQUE-index violations.
        return sql.Message.Contains(indexName, StringComparison.Ordinal);
    }

    /// <summary>
    /// W-REVIEW-02B — Reload callback for <c>channel_sources</c> UNIQUE violation.
    /// Detaches the uncommitted <see cref="EntityState.Added"/> entry, reloads
    /// the existing row by fingerprint (the only identity key; no sanitized-URL
    /// fallback), and re-issues SaveChanges so the alias + review-item changes
    /// get committed even when the channel_sources INSERT was rejected.
    /// </summary>
    private static async Task<ChannelSourceEntity?> ReloadChannelSourceOnUniqueAsync(
        ChannelCatalogDbContext context,
        long canonicalChannelId,
        long sourceId,
        string sanitizedUrl,
        string? fingerprint,
        string? fingerprintVersion,
        CancellationToken cancellationToken)
    {
        var added = context.ChangeTracker.Entries<ChannelSourceEntity>()
            .FirstOrDefault(e => e.State == EntityState.Added
                && e.Entity.CanonicalChannelId == canonicalChannelId
                && e.Entity.SourceId == sourceId);
        if (added != null) added.State = EntityState.Detached;

        ChannelSourceEntity? existing = null;
        if (!string.IsNullOrEmpty(fingerprint))
        {
            existing = await context.ChannelSources.FirstOrDefaultAsync(
                cs => cs.CanonicalChannelId == canonicalChannelId
                    && cs.SourceId == sourceId
                    && cs.Fingerprint == fingerprint
                    && cs.FingerprintVersion == fingerprintVersion,
                cancellationToken);
        }
        // (D-F3) Sem fallback por URL sanitizada. A violação do unique index
        // filtrado (`Fingerprint IS NOT NULL`) garante que a row em conflito
        // transporta um fingerprint; a pesquisa por fingerprint é suficiente.
        // O parâmetro `sanitizedUrl` mantém-se na assinatura por estabilidade
        // dos callers, mas não é usado como chave de identidade.

        if (existing != null)
        {
            context.ChangeTracker.DetectChanges();
            await context.SaveChangesAsync(cancellationToken);
        }
        return existing;
    }

    /// <summary>
    /// Lista todos os SyncRun ordenados por StartedAtUtc
    /// (mais recentes primeiro).
    /// </summary>
    public async Task<IReadOnlyList<SyncRunEntity>> ListSyncRunsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.SyncRuns
            .AsNoTracking()
            .OrderByDescending(r => r.StartedAtUtc)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Lista todos os PendingCountryApproval ordenados por data
    /// de criação (mais antigos primeiro, para review FIFO).
    /// </summary>
    public async Task<IReadOnlyList<PendingCountryApprovalEntity>> ListPendingCountryApprovalsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.PendingCountryApprovals
            .AsNoTracking()
            .OrderBy(r => r.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Regista um novo canal pendente de aprovação. Idempotente:
    /// se já existir um registo Open com a mesma (NormalizedIdentity,
    /// CountryCode, ReasonSignature), não cria duplicado.
    /// </summary>
    public async Task<PendingCountryApprovalEntity> UpsertPendingCountryApprovalAsync(
        string normalizedIdentity,
        string originalTitle,
        string countryCode,
        string sanitizedStreamUrl,
        string? sourceGroup,
        string reasonSignature,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedIdentity))
            throw new ArgumentException("normalizedIdentity required", nameof(normalizedIdentity));

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);

        var existing = await context.PendingCountryApprovals
            .FirstOrDefaultAsync(r =>
                r.NormalizedIdentity == normalizedIdentity &&
                r.CountryCode == countryCode &&
                r.ReasonSignature == reasonSignature &&
                r.State == PendingApprovalState.Open,
                cancellationToken);

        if (existing != null)
            return existing;

        var now = DateTime.UtcNow;
        var entry = new PendingCountryApprovalEntity
        {
            NormalizedIdentity = normalizedIdentity,
            OriginalTitle = originalTitle ?? string.Empty,
            CountryCode = countryCode ?? string.Empty,
            StreamUrl = sanitizedStreamUrl ?? string.Empty,
            SourceGroup = sourceGroup,
            ReasonSignature = reasonSignature ?? string.Empty,
            State = PendingApprovalState.Open,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.PendingCountryApprovals.Add(entry);
        await context.SaveChangesAsync(cancellationToken);
        return entry;
    }

    /// <summary>
    /// Aprova um canal pendente: cria <see cref="IdentityRuleEntity"/>
    /// com <see cref="RuleDisposition.ReviewOnly"/> e marca o pending
    /// como Approved. <b>ReviewOnly não autoriza criação automática</b>
    /// de canal (apenas desbloqueia o matching/revisão), pelo que a
    /// criação continua a ser uma decisão humana explícita. Opcionalmente
    /// adiciona o membro ao grupo de afinidade do país (criando o grupo
    /// se não existir).
    /// </summary>
    public async Task<PendingCountryApprovalEntity?> ApprovePendingCountryApprovalAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var item = await context.PendingCountryApprovals
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (item == null) return null;

        var existingRule = await context.IdentityRules
            .FirstOrDefaultAsync(r => r.NormalizedIdentity == item.NormalizedIdentity, cancellationToken);
        if (existingRule == null)
        {
            var now = DateTime.UtcNow;
            context.IdentityRules.Add(new IdentityRuleEntity
            {
                NormalizedIdentity = item.NormalizedIdentity,
                Disposition = RuleDisposition.ReviewOnly,
                Reason = $"Approved from PendingCountryApproval #{id} ({item.ReasonSignature})",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        }

        item.State = PendingApprovalState.Approved;
        item.ResolvedAtUtc = DateTime.UtcNow;
        item.UpdatedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return item;
    }

    /// <summary>
    /// Reprova um canal pendente: cria IdentityRule com Excluded
    /// e marca o pending como Rejected.
    /// </summary>
    public async Task<PendingCountryApprovalEntity?> RejectPendingCountryApprovalAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var item = await context.PendingCountryApprovals
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (item == null) return null;

        var existingRule = await context.IdentityRules
            .FirstOrDefaultAsync(r => r.NormalizedIdentity == item.NormalizedIdentity, cancellationToken);
        if (existingRule == null)
        {
            var now = DateTime.UtcNow;
            context.IdentityRules.Add(new IdentityRuleEntity
            {
                NormalizedIdentity = item.NormalizedIdentity,
                Disposition = RuleDisposition.Excluded,
                Reason = $"Rejected from PendingCountryApproval #{id} ({item.ReasonSignature})",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        }

        item.State = PendingApprovalState.Rejected;
        item.ResolvedAtUtc = DateTime.UtcNow;
        item.UpdatedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return item;
    }

    public async Task<CanonicalChannelEntity?> GetCanonicalChannelAsync(
        long id, CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.CanonicalChannels
            .AsNoTracking()
            .Include(c => c.Aliases)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    public async Task<CanonicalChannelEntity> CreateCanonicalChannelAsync(
        string key,
        string displayName,
        EditorialCategory editorialCategory,
        CanonicalEditorialGroup editorialGroup,
        PublicationPolicy publicationPolicy,
        bool isEnabled,
        IReadOnlyList<string> normalizedAliases,
        string? country = null,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ValidateDisplayName(displayName);
        ValidateAliases(normalizedAliases);

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var normalizedKey = key.Trim();
        var existingByKey = await context.CanonicalChannels
            .FirstOrDefaultAsync(c => c.Key == normalizedKey, cancellationToken);
        if (existingByKey != null)
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.DuplicateKey,
                $"Já existe um canal canónico com a key '{key}' (id={existingByKey.Id}).");
        }

        var normalizedAliasSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alias in normalizedAliases ?? Array.Empty<string>())
        {
            // Forma única matchable: o mesmo normalizador que o
            // matcher usa antes de consultar o catálogo. Aliases que
            // colapsam para a mesma forma (ou para vazio) são
            // deduplicados silenciosamente.
            var canonical = ChannelNormalizer.Normalize(alias);
            if (canonical.Length > 0)
            {
                normalizedAliasSet.Add(canonical);
            }
        }

        var conflictAliases = await context.ChannelAliases
            .Where(a => normalizedAliasSet.Contains(a.NormalizedAlias))
            .Select(a => a.NormalizedAlias)
            .ToListAsync(cancellationToken);
        if (conflictAliases.Count > 0)
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.AliasConflict,
                $"Os seguintes aliases já pertencem a outro canal: {string.Join(", ", conflictAliases)}");
        }

        var now = DateTime.UtcNow;
        var channel = new CanonicalChannelEntity
        {
            Key = normalizedKey,
            DisplayName = displayName.Trim(),
            Country = NormalizeCountry(country),
            EditorialCategory = editorialCategory,
            EditorialGroup = editorialGroup,
            PublicationPolicy = publicationPolicy,
            IsEnabled = isEnabled,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.CanonicalChannels.Add(channel);
        foreach (var alias in normalizedAliasSet)
        {
            context.ChannelAliases.Add(new ChannelAliasEntity
            {
                NormalizedAlias = alias,
                CanonicalChannel = channel,
                CreatedAtUtc = now,
            });
        }
        await context.SaveChangesAsync(cancellationToken);
        return channel;
    }

    public async Task<CanonicalChannelEntity?> UpdateCanonicalChannelAsync(
        long id,
        string displayName,
        EditorialCategory editorialCategory,
        CanonicalEditorialGroup editorialGroup,
        PublicationPolicy publicationPolicy,
        bool isEnabled,
        string? country = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDisplayName(displayName);

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var channel = await context.CanonicalChannels
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (channel == null) return null;

        channel.DisplayName = displayName.Trim();
        channel.Country = NormalizeCountry(country);
        channel.EditorialCategory = editorialCategory;
        channel.EditorialGroup = editorialGroup;
        channel.PublicationPolicy = publicationPolicy;
        channel.IsEnabled = isEnabled;
        channel.UpdatedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return channel;
    }

    public async Task<bool> DeleteCanonicalChannelAsync(
        long id, CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var channel = await context.CanonicalChannels
            .Include(c => c.Aliases)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (channel == null) return false;

        var ownershipCount = await context.DispatcharrChannelOwnerships
            .CountAsync(o => o.CanonicalChannelId == id, cancellationToken);
        if (ownershipCount > 0)
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.HasOwnership,
                $"Não é possível eliminar o canal #{id}: existem {ownershipCount} registos de ownership do Dispatcharr.");
        }

        context.CanonicalChannels.Remove(channel);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Faz upsert idempotente de um <see cref="CanonicalChannelEntity"/>
    /// identificado por <paramref name="key"/>. Se já existir, devolve
    /// o existente (sem mexer em campos). Se não existir, cria um novo
    /// canal com os parâmetros fornecidos, e adiciona
    /// <paramref name="normalizedAlias"/> (normalizado via
    /// <see cref="ChannelNormalizer.Normalize"/>) como alias se for
    /// não-vazio e não colidir com nenhum alias já existente noutro
    /// canal.
    ///
    /// <para>
    /// <b>Não é a pipeline de ingestão.</b> A ingestão
    /// (<see cref="PipelineIngestionService"/>) não cria canais: usa
    /// apenas <see cref="ResolveAsync"/>. Este método permanece para
    /// chamadas administrativas/explícitas que já tenham decidido a
    /// identidade canónica.
    /// </para>
    /// </summary>
    public async Task<(CanonicalChannelEntity Channel, bool Created)> EnsureCanonicalChannelAsync(
        string key,
        string displayName,
        EditorialCategory editorialCategory,
        CanonicalEditorialGroup editorialGroup,
        PublicationPolicy publicationPolicy,
        bool isEnabled,
        string? normalizedAlias,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("DisplayName obrigatório.", nameof(displayName));

        var normalizedKey = key.Trim();

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var existing = await context.CanonicalChannels
            .FirstOrDefaultAsync(c => c.Key == normalizedKey, cancellationToken);
        if (existing != null)
        {
            return (existing, false);
        }

        // Conflicto de alias com outro canal? Ignorar silenciosamente
        // (o alias será mantido no canal existente; este canal
        // desconhecido fica sem o alias, mas é criado).
        var now = DateTime.UtcNow;
        var channel = new CanonicalChannelEntity
        {
            Key = normalizedKey,
            DisplayName = displayName.Trim(),
            EditorialCategory = editorialCategory,
            EditorialGroup = editorialGroup,
            PublicationPolicy = publicationPolicy,
            IsEnabled = isEnabled,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.CanonicalChannels.Add(channel);
        await context.SaveChangesAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(normalizedAlias))
        {
            var alias = ChannelNormalizer.Normalize(normalizedAlias);
            if (alias.Length == 0)
            {
                return (channel, true);
            }
            var aliasConflict = await context.ChannelAliases
                .AnyAsync(a => a.NormalizedAlias == alias, cancellationToken);
            if (!aliasConflict)
            {
                context.ChannelAliases.Add(new ChannelAliasEntity
                {
                    NormalizedAlias = alias,
                    CanonicalChannelId = channel.Id,
                    CreatedAtUtc = now,
                });
                await context.SaveChangesAsync(cancellationToken);
            }
        }
        return (channel, true);
    }

    /// <summary>
    /// Adiciona um alias a um canal canónico. O alias é normalizado
    /// via <see cref="ChannelNormalizer.Normalize"/> antes de ser
    /// persistido, para que fique na mesma forma que o matcher
    /// consulta (forma única matchable). Aliases que normalizam para
    /// vazio são rejeitados.
    /// </summary>
    public async Task<ChannelAliasEntity> AddAliasAsync(
        long channelId,
        string normalizedAlias,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedAlias))
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.InvalidInput,
                "Alias é obrigatório.");
        }
        var alias = ChannelNormalizer.Normalize(normalizedAlias);
        if (alias.Length == 0)
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.InvalidInput,
                $"Alias '{normalizedAlias}' normaliza para vazio e não é matchable.");
        }

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var channel = await context.CanonicalChannels
            .FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel == null)
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.ChannelNotFound,
                $"Canal #{channelId} não encontrado.");
        }

        var alreadyOnChannel = await context.ChannelAliases
            .AnyAsync(a => a.CanonicalChannelId == channelId && a.NormalizedAlias == alias, cancellationToken);
        if (alreadyOnChannel)
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.AlreadyExists,
                $"Alias '{alias}' já existe neste canal.");
        }
        var conflict = await context.ChannelAliases
            .AnyAsync(a => a.NormalizedAlias == alias, cancellationToken);
        if (conflict)
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.AliasConflict,
                $"Alias '{alias}' já pertence a outro canal canónico.");
        }

        var entity = new ChannelAliasEntity
        {
            NormalizedAlias = alias,
            CanonicalChannelId = channelId,
            CreatedAtUtc = DateTime.UtcNow,
        };
        context.ChannelAliases.Add(entity);
        channel.UpdatedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<bool> RemoveAliasAsync(
        long channelId,
        string normalizedAlias,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedAlias)) return false;

        // Aceita a forma exacta (removendo aliases legados ainda não
        // normalizados) ou a forma normalizada matchable.
        var trimmed = normalizedAlias.Trim();
        var normalized = ChannelNormalizer.Normalize(trimmed);
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var alias = await context.ChannelAliases
            .FirstOrDefaultAsync(a => a.CanonicalChannelId == channelId
                                   && (a.NormalizedAlias == trimmed || a.NormalizedAlias == normalized), cancellationToken);
        if (alias == null) return false;

        context.ChannelAliases.Remove(alias);
        var channel = await context.CanonicalChannels
            .FirstOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel != null) channel.UpdatedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.InvalidInput,
                "Key é obrigatória.");
        }
        var trimmed = key.Trim();
        if (trimmed.Length > 120)
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.InvalidInput,
                "Key excede 120 caracteres.");
        }
        foreach (var ch in trimmed)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.'))
            {
                throw new ChannelAdministrationException(
                    ChannelAdministrationError.InvalidInput,
                    "Key contém caracteres inválidos. Use letras, dígitos, '-', '_' ou '.'.");
            }
        }
    }

    /// <summary>
    /// Normaliza o país do canal canónico: trim, vazio → null,
    /// máximo 10 caracteres. Não faz parte da identidade (Key).
    /// </summary>
    private static string? NormalizeCountry(string? country)
    {
        if (string.IsNullOrWhiteSpace(country)) return null;
        var trimmed = country.Trim();
        if (trimmed.Length > 10)
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.InvalidInput,
                "Country excede 10 caracteres.");
        }
        return trimmed;
    }

    private static void ValidateDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.InvalidInput,
                "DisplayName é obrigatório.");
        }
        if (displayName.Trim().Length > 200)
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.InvalidInput,
                "DisplayName excede 200 caracteres.");
        }
    }

    private static void ValidateAliases(IReadOnlyList<string>? aliases)
    {
        if (aliases == null) return;
        foreach (var a in aliases)
        {
            if (a == null)
            {
                throw new ChannelAdministrationException(
                    ChannelAdministrationError.InvalidInput,
                    "Alias contém valor nulo.");
            }
            var trimmed = a.Trim();
            if (trimmed.Length == 0)
            {
                throw new ChannelAdministrationException(
                    ChannelAdministrationError.InvalidInput,
                    "Alias não pode ser vazio.");
            }
            if (trimmed.Length > 200)
            {
                throw new ChannelAdministrationException(
                    ChannelAdministrationError.InvalidInput,
                    "Alias excede 200 caracteres.");
            }
        }
    }

    /// <summary>
    /// Estatísticas agregadas do catálogo: contagens por tabela.
    /// </summary>
    public async Task<CatalogStats> GetStatsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        return new CatalogStats
        {
            CanonicalChannels = await context.CanonicalChannels.AsNoTracking().CountAsync(cancellationToken),
            ChannelAliases = await context.ChannelAliases.AsNoTracking().CountAsync(cancellationToken),
            IdentityRules = await context.IdentityRules.AsNoTracking().CountAsync(cancellationToken),
            AffinityGroups = await context.AffinityGroups.AsNoTracking().CountAsync(cancellationToken),
            AffinityMembers = await context.AffinityMembers.AsNoTracking().CountAsync(cancellationToken),
            DispatcharrChannelOwnerships = await context.DispatcharrChannelOwnerships.AsNoTracking().CountAsync(cancellationToken),
            DispatcharrStreamOwnerships = await context.DispatcharrStreamOwnerships.AsNoTracking().CountAsync(cancellationToken),
            ReviewItemsOpen = await context.ReviewItems.AsNoTracking().CountAsync(r => r.State == ReviewItemState.Open, cancellationToken),
            ReviewItemsInReview = await context.ReviewItems.AsNoTracking().CountAsync(r => r.State == ReviewItemState.InReview, cancellationToken),
            ReviewItemsResolved = await context.ReviewItems.AsNoTracking().CountAsync(r => r.State == ReviewItemState.Resolved, cancellationToken),
            ReviewItemsIgnored = await context.ReviewItems.AsNoTracking().CountAsync(r => r.State == ReviewItemState.Ignored, cancellationToken),
            SyncRuns = await context.SyncRuns.AsNoTracking().CountAsync(cancellationToken),
            PendingCountryApprovals = await context.PendingCountryApprovals.AsNoTracking().CountAsync(cancellationToken),
            PendingCountryApprovalsOpen = await context.PendingCountryApprovals.AsNoTracking().CountAsync(r => r.State == PendingApprovalState.Open, cancellationToken),
            Sources = await context.Sources.AsNoTracking().CountAsync(cancellationToken),
            ChannelSources = await context.ChannelSources.AsNoTracking().CountAsync(cancellationToken),
            OrderingLists = await context.OrderingLists.AsNoTracking().CountAsync(cancellationToken),
            OrderingItems = await context.OrderingItems.AsNoTracking().CountAsync(cancellationToken),
            SourcePriorityPolicies = await context.SourcePriorityPolicies.AsNoTracking().CountAsync(cancellationToken),
            ImportPolicies = await context.ImportPolicies.AsNoTracking().CountAsync(cancellationToken),
            CanonicalGroups = await context.CanonicalGroups.AsNoTracking().CountAsync(cancellationToken),
            GroupMappings = await context.GroupMappings.AsNoTracking().CountAsync(cancellationToken),
            MatchingAudits = await context.MatchingAudits.AsNoTracking().CountAsync(cancellationToken),
            ChannelSourceObservations = await context.ChannelSourceObservations.AsNoTracking().CountAsync(cancellationToken),
            SyncRunSteps = await context.SyncRunSteps.AsNoTracking().CountAsync(cancellationToken),
            ScheduledJobs = await context.ScheduledJobs.AsNoTracking().CountAsync(cancellationToken),
            DbPath = _dbPath,
            GeneratedAtUtc = now,
        };
    }

    // ============================================================================
    // PHASE 4 — Sources & ChannelSource
    // ============================================================================

    public async Task<IReadOnlyList<SourceEntity>> ListSourcesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.Sources
            .AsNoTracking()
            .OrderBy(s => s.Priority).ThenBy(s => s.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<SourceEntity?> GetSourceAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.Sources
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
    }

    /// <summary>
    /// W2-FU-2A (2026-09-23) — read-only lookup por <see cref="SourceEntity.Key"/>.
    /// Devolve <c>null</c> se a Source não existir. NÃO cria nem modifica nenhuma
    /// entidade. Determinístico: a mesma chave devolve sempre o mesmo Id (via PK SQLite).
    /// Thread-safe (cada chamada usa o seu próprio DbContext via factory).
    ///
    /// <para>
    /// Uso: o observer de falhas de aquisição (<c>CatalogAcquisitionFailureObserver</c>)
    /// precisa do <c>Source.Id</c> quando já existe uma Source — para que a falha
    /// possa ser persistida. Quando a Source ainda não existe, o caller recebe
    /// <c>null</c> e cai no caminho W2-FU-1 (apenas RunReport, sem persistência).
    /// </para>
    /// </summary>
    public async Task<long?> GetSourceIdByKeyAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var normalizedKey = key.Trim();

        await using var context =
            await _factory.CreateDbContextAsync(cancellationToken);

        return await context.Sources
            .AsNoTracking()
            .Where(s => s.Key == normalizedKey)
            .Select(s => (long?)s.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Cria ou actualiza uma <see cref="SourceEntity"/> pela chave
    /// (slug). A origem é sanitizada antes de ser persistida para
    /// não guardar credenciais em claro.
    ///
    /// <para>
    /// <paramref name="updatePriority"/> controla se a prioridade de
    /// uma Source já existente é reescrita. A ingestão automática
    /// passa <c>false</c> para não destruir a prioridade definida pelo
    /// operador; a criação de uma Source nova usa sempre
    /// <paramref name="priority"/>.
    /// </para>
    /// </summary>
    public async Task<SourceEntity> EnsureSourceAsync(
        string key, string name, SourceKind kind, string origin, int priority,
        bool isEnabled = true, CancellationToken cancellationToken = default,
        bool updatePriority = true, long? providerAccountId = null)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key é obrigatória.", nameof(key));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name é obrigatório.", nameof(name));
        if (key.Length > 120) throw new ArgumentException("Key excede 120 caracteres.", nameof(key));
        if (name.Length > 200) throw new ArgumentException("Name excede 200 caracteres.", nameof(name));
        if (origin.Length > 1000) throw new ArgumentException("Origin excede 1000 caracteres.", nameof(origin));

        var sanitizedOrigin = CredentialSanitizer.SanitizeUrl(origin);
        var normalizedKey = key.Trim();

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var existing = await context.Sources
            .FirstOrDefaultAsync(s => s.Key == normalizedKey, cancellationToken);

        if (existing != null)
        {
            existing.Name = name.Trim();
            existing.Kind = kind;
            existing.Origin = sanitizedOrigin;
            if (updatePriority)
            {
                existing.Priority = priority;
            }
            existing.IsEnabled = isEnabled;
            // A conta funcional só é (re)associada quando explicitamente
            // fornecida; nunca é apagada implicitamente por uma chamada
            // sem identidade.
            if (providerAccountId.HasValue && providerAccountId.Value > 0)
            {
                existing.ProviderAccountId = providerAccountId.Value;
            }
            existing.UpdatedAtUtc = now;
            await context.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var entity = new SourceEntity
        {
            Key = normalizedKey,
            Name = name.Trim(),
            Kind = kind,
            Origin = sanitizedOrigin,
            Priority = priority,
            IsEnabled = isEnabled,
            ProviderAccountId = providerAccountId.HasValue && providerAccountId.Value > 0
                ? providerAccountId.Value
                : null,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.Sources.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return entity;
    }

    // ============================================================================
    // W1 (2026-09-19) — Provider / ProviderAccount / DiscoveryCandidate
    // ============================================================================

    /// <summary>
    /// Cria ou reutiliza um <see cref="ProviderEntity"/> pela
    /// <see cref="ProviderEntity.Key"/> (namespace). Idempotente: a
    /// mesma key devolve sempre a mesma row; nunca cria duplicados.
    /// </summary>
    public async Task<ProviderEntity> EnsureProviderAsync(
        string key,
        string name,
        ProviderType type,
        string capabilities = "{}",
        bool isEnabled = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Provider key é obrigatória.", nameof(key));
        if (key.Length > 80) throw new ArgumentException("Provider key excede 80 caracteres.", nameof(key));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Provider name é obrigatório.", nameof(name));
        if (name.Length > 200) throw new ArgumentException("Provider name excede 200 caracteres.", nameof(name));

        var normalizedKey = key.Trim();
        var now = DateTime.UtcNow;
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var existing = await context.Providers
            .FirstOrDefaultAsync(p => p.Key == normalizedKey, cancellationToken);
        if (existing != null)
        {
            existing.Name = name.Trim();
            existing.Type = type;
            existing.Capabilities = string.IsNullOrWhiteSpace(capabilities) ? "{}" : capabilities;
            existing.UpdatedAtUtc = now;
            await context.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var entity = new ProviderEntity
        {
            Key = normalizedKey,
            Name = name.Trim(),
            Type = type,
            Capabilities = string.IsNullOrWhiteSpace(capabilities) ? "{}" : capabilities,
            IsEnabled = isEnabled,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.Providers.Add(entity);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Corrida: outro writer criou o mesmo namespace. O
            // invariante de unicidade manda; reutilizar.
            context.ChangeTracker.Clear();
            return await context.Providers.FirstAsync(p => p.Key == normalizedKey, cancellationToken);
        }
        return entity;
    }

    /// <summary>
    /// Cria ou reutiliza um <see cref="ProviderAccountEntity"/> pela
    /// identidade funcional canónica <paramref name="accountKey"/>, que
    /// é única dentro do namespace do provider. Nunca cria uma conta
    /// nova para a mesma identidade funcional (T7).
    /// </summary>
    public async Task<ProviderAccountEntity> EnsureProviderAccountAsync(
        long providerId,
        string accountKey,
        string displayName,
        ProviderAccountStatus status = ProviderAccountStatus.Discovered,
        string? credentialsReference = null,
        CancellationToken cancellationToken = default)
    {
        if (providerId <= 0) throw new ArgumentOutOfRangeException(nameof(providerId));
        if (string.IsNullOrWhiteSpace(accountKey)) throw new ArgumentException("AccountKey é obrigatória.", nameof(accountKey));
        if (accountKey.Length > 200) throw new ArgumentException("AccountKey excede 200 caracteres.", nameof(accountKey));

        var normalizedKey = accountKey.Trim();
        var now = DateTime.UtcNow;
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var existing = await context.ProviderAccounts
            .FirstOrDefaultAsync(a => a.ProviderId == providerId && a.AccountKey == normalizedKey, cancellationToken);
        if (existing != null)
        {
            if (!string.IsNullOrWhiteSpace(displayName)) existing.DisplayName = displayName.Trim();
            existing.Status = status;
            if (!string.IsNullOrWhiteSpace(credentialsReference))
            {
                existing.CredentialsReference = credentialsReference.Trim();
            }
            existing.UpdatedAtUtc = now;
            await context.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var entity = new ProviderAccountEntity
        {
            ProviderId = providerId,
            AccountKey = normalizedKey,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? normalizedKey : displayName.Trim(),
            Status = status,
            CredentialsReference = string.IsNullOrWhiteSpace(credentialsReference) ? null : credentialsReference.Trim(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.ProviderAccounts.Add(entity);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            return await context.ProviderAccounts.FirstAsync(
                a => a.ProviderId == providerId && a.AccountKey == normalizedKey, cancellationToken);
        }
        return entity;
    }

    /// <summary>
    /// Resultado do registo de uma ocorrência de descoberta.
    /// <see cref="Created"/> é <c>false</c> quando a ocorrência foi
    /// deduplicada para uma já existente no mesmo Run.
    /// </summary>
    public sealed record DiscoveryCandidateRecord(
        DiscoveryCandidateEntity Candidate,
        bool Created,
        ProviderAccountEntity? Account);

    /// <summary>
    /// W1 — persiste uma ocorrência de descoberta associada a um Run.
    ///
    /// <para>
    /// <b>Dedup por identidade funcional.</b> Quando existe
    /// <paramref name="providerAccountId"/> e um <paramref name="runId"/>
    /// não vazio, duas ocorrências do mesmo Run para a mesma conta
    /// funcional são a mesma unidade: a segunda devolve a row existente
    /// (<see cref="DiscoveryCandidateRecord.Created"/> = <c>false</c>) e
    /// não cria processamento equivalente.
    /// </para>
    ///
    /// <para>
    /// <b>Sem identidade → sem dedup.</b> Quando não há conta funcional
    /// (<paramref name="providerAccountId"/> nulo) ou não há Run
    /// atribuível, a ocorrência é sempre preservada como distinta. Nunca
    /// se inventa identidade para poder deduplicar.
    /// </para>
    /// </summary>
    public async Task<DiscoveryCandidateRecord> RecordDiscoveryCandidateAsync(
        string? runId,
        long? providerId,
        long? providerAccountId,
        string? externalIdentity,
        string? normalizedIdentity,
        string evidence,
        DiscoveryCandidateStatus status = DiscoveryCandidateStatus.Discovered,
        long? sourceId = null,
        CancellationToken cancellationToken = default)
    {
        if (providerId.HasValue && providerId.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(providerId));
        }

        var external = TrimTo(externalIdentity ?? string.Empty, 400);
        var normalized = TrimTo(
            AccountKey.NormalizeExternalIdentity(normalizedIdentity ?? externalIdentity), 400);
        var sanitizedEvidence = TrimTo(
            CredentialSanitizer.SanitizeText(
                CredentialSanitizer.SanitizeUrl(evidence ?? string.Empty)),
            1000);
        var effectiveRunId = string.IsNullOrWhiteSpace(runId) ? null : runId.Trim();
        if (effectiveRunId != null && effectiveRunId.Length > 64) effectiveRunId = effectiveRunId[..64];

        var now = DateTime.UtcNow;
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);

        ProviderAccountEntity? account = null;
        if (providerAccountId.HasValue && providerAccountId.Value > 0)
        {
            account = await context.ProviderAccounts
                .FirstOrDefaultAsync(a => a.Id == providerAccountId.Value, cancellationToken);
        }

        // Dedup apenas com conta funcional E Run atribuível.
        if (account != null && effectiveRunId != null)
        {
            var existing = await context.DiscoveryCandidates
                .FirstOrDefaultAsync(
                    c => c.RunId == effectiveRunId && c.ProviderAccountId == account.Id,
                    cancellationToken);
            if (existing != null)
            {
                existing.Status = DiscoveryCandidateStatus.Deduplicated;
                existing.UpdatedAtUtc = now;
                if (sourceId.HasValue && sourceId.Value > 0)
                {
                    existing.SourceId = sourceId.Value;
                }
                await context.SaveChangesAsync(cancellationToken);
                return new DiscoveryCandidateRecord(existing, Created: false, account);
            }
        }

        var entity = new DiscoveryCandidateEntity
        {
            ProviderId = providerId,
            ProviderAccountId = account?.Id,
            ExternalIdentity = external,
            NormalizedIdentity = normalized,
            Evidence = sanitizedEvidence,
            Status = status,
            RunId = effectiveRunId,
            SourceId = sourceId.HasValue && sourceId.Value > 0 ? sourceId.Value : null,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.DiscoveryCandidates.Add(entity);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Corrida no índice único (RunId, ProviderAccountId).
            context.ChangeTracker.Clear();
            var existing = await context.DiscoveryCandidates
                .FirstAsync(c => c.RunId == effectiveRunId && c.ProviderAccountId == account!.Id, cancellationToken);
            return new DiscoveryCandidateRecord(existing, Created: false, account);
        }
        return new DiscoveryCandidateRecord(entity, Created: true, account);
    }

    /// <summary>
    /// Associa explicitamente uma <see cref="DiscoveryCandidateEntity"/>
    /// a uma <see cref="SourceEntity"/> (passagem Candidate→Source
    /// rastreável). Idempotente.
    /// </summary>
    public async Task<bool> LinkDiscoveryCandidateToSourceAsync(
        long candidateId, long sourceId, CancellationToken cancellationToken = default)
    {
        if (candidateId <= 0) throw new ArgumentOutOfRangeException(nameof(candidateId));
        if (sourceId <= 0) throw new ArgumentOutOfRangeException(nameof(sourceId));
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var candidate = await context.DiscoveryCandidates
            .FirstOrDefaultAsync(c => c.Id == candidateId, cancellationToken);
        if (candidate == null) return false;
        candidate.SourceId = sourceId;
        candidate.UpdatedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ProviderEntity>> ListProvidersAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.Providers
            .AsNoTracking()
            .OrderBy(p => p.Key)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProviderAccountEntity>> ListProviderAccountsAsync(
        long? providerId = null, CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var query = context.ProviderAccounts.AsNoTracking().AsQueryable();
        if (providerId.HasValue) query = query.Where(a => a.ProviderId == providerId.Value);
        return await query.OrderBy(a => a.AccountKey).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DiscoveryCandidateEntity>> ListDiscoveryCandidatesAsync(
        string? runId = null, CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var query = context.DiscoveryCandidates.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(runId)) query = query.Where(c => c.RunId == runId);
        return await query.OrderBy(c => c.Id).ToListAsync(cancellationToken);
    }

    private static string TrimTo(string value, int max)
        => value.Length <= max ? value : value[..max];

    public async Task<bool> DeleteSourceAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Sources.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (entity == null) return false;
        context.Sources.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> MarkSourceDiscoveryAsync(long id, DateTime whenUtc, CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Sources.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (entity == null) return false;
        entity.LastDiscoveryAtUtc = whenUtc;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> MarkSourceValidationAsync(long id, DateTime whenUtc, CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Sources.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (entity == null) return false;
        entity.LastValidationAtUtc = whenUtc;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// W2 (2026-09-19) — regista a última falha de aquisição persistente de
    /// uma <see cref="SourceEntity"/> (<c>19-FAILURE-MODEL.md §6</c>).
    ///
    /// <para>
    /// O detalhe é sanitizado (<see cref="CredentialSanitizer.SanitizeSensitiveText"/>)
    /// antes de persistir: nunca são guardadas credenciais, tokens,
    /// Authorization nem URLs com credenciais. A operação é aditiva — não
    /// apaga nem altera outros dados da Source.
    /// </para>
    /// </summary>
    public async Task<bool> MarkSourceAcquisitionFailureAsync(
        long id,
        string failureKind,
        DateTime whenUtc,
        int? httpStatus,
        string? detail,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var entity = await context.Sources.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (entity == null) return false;

        entity.LastAcquisitionFailureKind = TrimTo(failureKind ?? string.Empty, 40);
        entity.LastAcquisitionFailureAtUtc = whenUtc;
        entity.LastAcquisitionHttpStatus = httpStatus;
        entity.LastAcquisitionFailureDetail = detail is null
            ? null
            : TrimTo(CredentialSanitizer.SanitizeSensitiveText(detail), 500);
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Regista (ou actualiza) um <see cref="ChannelSourceEntity"/> —
    /// a associação entre um canal canónico e uma stream concreta de
    /// uma source. A URL é sanitizada antes de persistir.
    ///
    /// <para>
    /// <b>W4 — fingerprint e dedup intra-Source.</b> O fingerprint
    /// (<c>sfp1</c>, ver <c>docs/Reestructure/04-PLAYLIST-STREAM.md §4</c>) é
    /// calculado deterministicamente a partir do <paramref name="streamUrl"/>
    /// original. A identidade/dedup é
    /// <c>(CanonicalChannelId, SourceId, Fingerprint, FingerprintVersion)</c>:
    /// mesma Source + mesmo canal + mesmo fingerprint (+versão) consolidam
    /// numa única row (actualização de LastSeen/LastTested/metadados).
    /// <c>ChannelSource.StreamUrl</c> é uma representação sanitizada de
    /// apresentação/persistência e NUNCA é usada como identidade nem como
    /// chave de fallback. Rows cuja URL não é fingerprintável (não-http) ou
    /// rows legacy com <c>Fingerprint IS NULL</c> NÃO são re-correspondidas
    /// (sem fallback por URL sanitizada): é criada uma nova row. Sources
    /// diferentes nunca são consolidadas.
    /// </para>
    /// </summary>
    public async Task<ChannelSourceEntity> RecordChannelSourceAsync(
        long canonicalChannelId,
        long sourceId,
        string streamUrl,
        StreamQuality quality = StreamQuality.Unknown,
        EpgState epg = EpgState.Unknown,
        AvailabilityState availability = AvailabilityState.Discovered,
        double? matchConfidence = 0,
        string matchMethod = "unknown",
        string? externalStreamId = null,
        bool isEnabled = true,
        CancellationToken cancellationToken = default,
        // W-REVIEW-02 — tracked-vs-persisted bifurcation. Quando não-nulo,
        // o caller (ApplyAddAliasAsync / ApplyCreateChannelAsync) já possui
        // o DbContext da aprovação; este método limita-se a fazer lookup +
        // Add/Update tracked, sem SaveChanges, sem dispose. Os dois callers
        // existentes (PipelineIngestionService, HandleChannelSourceUpsertAsync)
        // continuam a passar null por omissão — comportamento idêntico ao
        // pré-wave.
        ChannelCatalogDbContext? context = null)
    {
        if (canonicalChannelId <= 0) throw new ArgumentException("CanonicalChannelId inválido.", nameof(canonicalChannelId));
        if (sourceId <= 0) throw new ArgumentException("SourceId inválido.", nameof(sourceId));
        if (string.IsNullOrWhiteSpace(streamUrl)) throw new ArgumentException("StreamUrl é obrigatória.", nameof(streamUrl));
        if (matchMethod.Length > 80) throw new ArgumentException("MatchMethod excede 80 caracteres.", nameof(matchMethod));

        // W5.6 F8-B (DL-123) — MatchSemanticsVersion = "msm1" identifica apenas
        // pares produzidos sob a semântica W5.6: método normativo + confidence
        // validada + igual à tabela §7. Qualquer outro caminho (legacy
        // "unknown"/0, método arbitrário, confidence nula ou par divergente)
        // fica com versão nula; nunca se cria um valor "legacy".
        string? matchSemanticsVersion = null;
        if (RecognitionMatchMethods.TryGetMatchConfidence(matchMethod, out var expectedConfidence)
            && matchConfidence.HasValue
            && Math.Abs(matchConfidence.Value - expectedConfidence) < 1e-9)
        {
            matchSemanticsVersion = RecognitionMatchMethods.MatchSemanticsVersion;
        }

        var sanitizedUrl = CredentialSanitizer.SanitizeUrl(streamUrl);
        var hasFingerprint = StreamFingerprint.TryCreate(streamUrl, out _, out var fingerprint);
        var fingerprintVersion = hasFingerprint ? StreamFingerprint.Version : null;

        // W-REVIEW-02 — bifurcação tracked vs persisted. Quando o caller
        // passou um context, NÃO criamos outro (mantemos a transacção do
        // caller) e NÃO chamamos SaveChanges (caller controla o save).
        var ownsContext = context is null;
        var activeContext = context ?? await _factory.CreateDbContextAsync(cancellationToken);
        try
        {
            // 1. Dedup canónico intra-Source: mesma Source + canal + fingerprint
            //    + versão consolidam na mesma row.
            ChannelSourceEntity? existing = null;
            if (hasFingerprint)
            {
                existing = await activeContext.ChannelSources
                    .FirstOrDefaultAsync(cs => cs.CanonicalChannelId == canonicalChannelId
                                            && cs.SourceId == sourceId
                                            && cs.Fingerprint == fingerprint
                                            && cs.FingerprintVersion == fingerprintVersion,
                        cancellationToken);
            }

            // 2. A identidade interna é o fingerprint (`sfp1`). `ChannelSource.StreamUrl`
            //    é apenas apresentação/persistência e NUNCA é chave de identidade nem
            //    fallback de dedup. Rows legacy sem fingerprint não são correspondidas
            //    aqui (ver D-F3); é criada uma nova row.

            var now = DateTime.UtcNow;
            if (existing != null)
            {
                existing.Quality = quality;
                existing.Epg = epg;
                existing.Availability = availability;
                existing.MatchConfidence = matchConfidence;
                existing.MatchMethod = matchMethod;
                existing.MatchSemanticsVersion = matchSemanticsVersion;
                existing.ExternalStreamId = externalStreamId;
                existing.IsEnabled = isEnabled;
                existing.LastSeenAtUtc = now;
                existing.LastTestedAtUtc = now;
                existing.UpdatedAtUtc = now;
                if (hasFingerprint && string.IsNullOrEmpty(existing.Fingerprint))
                {
                    existing.Fingerprint = fingerprint;
                    existing.FingerprintVersion = fingerprintVersion;
                }
                if (ownsContext)
                {
                    await activeContext.SaveChangesAsync(cancellationToken);
                }
                return existing;
            }

            var entity = new ChannelSourceEntity
            {
                CanonicalChannelId = canonicalChannelId,
                SourceId = sourceId,
                StreamUrl = sanitizedUrl,
                ExternalStreamId = externalStreamId,
                Fingerprint = hasFingerprint ? fingerprint : null,
                FingerprintVersion = fingerprintVersion,
                Quality = quality,
                Epg = epg,
                Availability = availability,
                MatchConfidence = matchConfidence,
                MatchMethod = matchMethod,
                MatchSemanticsVersion = matchSemanticsVersion,
                FirstSeenAtUtc = now,
                LastSeenAtUtc = now,
                LastTestedAtUtc = now,
                LastResponseTimeMs = 0,
                IsEnabled = isEnabled,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            activeContext.ChannelSources.Add(entity);
            if (ownsContext)
            {
                await activeContext.SaveChangesAsync(cancellationToken);
            }
            return entity;
        }
        finally
        {
            if (ownsContext) await activeContext.DisposeAsync();
        }
    }

    public async Task<IReadOnlyList<ChannelSourceEntity>> ListChannelSourcesAsync(
        long? canonicalChannelId = null,
        long? sourceId = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var query = context.ChannelSources
            .AsNoTracking()
            .Include(cs => cs.CanonicalChannel)
            .AsQueryable();
        if (canonicalChannelId.HasValue) query = query.Where(cs => cs.CanonicalChannelId == canonicalChannelId.Value);
        if (sourceId.HasValue) query = query.Where(cs => cs.SourceId == sourceId.Value);
        return await query
            .OrderBy(cs => cs.CanonicalChannelId).ThenBy(cs => cs.SourceId)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> DeleteChannelSourceAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var entity = await context.ChannelSources.FirstOrDefaultAsync(cs => cs.Id == id, cancellationToken);
        if (entity == null) return false;
        context.ChannelSources.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetChannelSourceEnabledAsync(long id, bool isEnabled, CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var entity = await context.ChannelSources.FirstOrDefaultAsync(cs => cs.Id == id, cancellationToken);
        if (entity == null) return false;
        entity.IsEnabled = isEnabled;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ============================================================================
    // PHASE 5 — Ordering Lists
    // ============================================================================

    public async Task<IReadOnlyList<OrderingListEntity>> ListOrderingListsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.OrderingLists
            .AsNoTracking()
            .OrderBy(l => l.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<OrderingListEntity?> GetOrderingListAsync(long id, bool includeItems = false,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var query = context.OrderingLists.AsNoTracking().AsQueryable();
        if (includeItems)
        {
            query = query.Include(l => l.Items.OrderBy(i => i.Position));
        }
        return await query.FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
    }

    public async Task<OrderingListEntity> CreateOrderingListAsync(
        string key, string name, string? country, string? description,
        bool isEnabled = true, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key é obrigatória.", nameof(key));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name é obrigatório.", nameof(name));
        if (key.Length > 120) throw new ArgumentException("Key excede 120 caracteres.", nameof(key));
        if (name.Length > 200) throw new ArgumentException("Name excede 200 caracteres.", nameof(name));

        var normalizedKey = key.Trim();
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        if (await context.OrderingLists.AnyAsync(l => l.Key == normalizedKey, cancellationToken))
        {
            throw new InvalidOperationException($"Já existe uma OrderingList com a key '{normalizedKey}'.");
        }

        var now = DateTime.UtcNow;
        var entity = new OrderingListEntity
        {
            Key = normalizedKey,
            Name = name.Trim(),
            Country = string.IsNullOrWhiteSpace(country) ? null : country.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            IsEnabled = isEnabled,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.OrderingLists.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return entity;
    }

    /// <summary>
    /// W4 — actualiza os metadados de uma <see cref="OrderingListEntity"/>
    /// existente. A <c>Key</c> é imutável; só <c>Name</c>, <c>Country</c>,
    /// <c>Description</c> e <c>IsEnabled</c> são alteráveis.
    /// </summary>
    public async Task<OrderingListEntity?> UpdateOrderingListAsync(
        long id, string name, string? country, string? description, bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.InvalidInput, "Name é obrigatório.");
        }

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var entity = await context.OrderingLists.FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (entity == null) return null;

        entity.Name = name.Trim();
        entity.Country = string.IsNullOrWhiteSpace(country) ? null : country.Trim();
        entity.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        entity.IsEnabled = isEnabled;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<OrderingListEntity> DuplicateOrderingListAsync(
        long sourceListId, string newKey, string? newName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newKey)) throw new ArgumentException("Key é obrigatória.", nameof(newKey));

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var source = await context.OrderingLists
            .Include(l => l.Items)
            .FirstOrDefaultAsync(l => l.Id == sourceListId, cancellationToken);
        if (source == null) throw new InvalidOperationException($"OrderingList #{sourceListId} não encontrada.");

        if (await context.OrderingLists.AnyAsync(l => l.Key == newKey, cancellationToken))
        {
            throw new InvalidOperationException($"Já existe uma OrderingList com a key '{newKey}'.");
        }

        var now = DateTime.UtcNow;
        var clone = new OrderingListEntity
        {
            Key = newKey.Trim(),
            Name = string.IsNullOrWhiteSpace(newName) ? $"{source.Name} (cópia)" : newName.Trim(),
            Country = source.Country,
            Description = source.Description,
            IsEnabled = source.IsEnabled,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.OrderingLists.Add(clone);
        await context.SaveChangesAsync(cancellationToken);

        foreach (var item in source.Items.OrderBy(i => i.Position))
        {
            context.OrderingItems.Add(new OrderingItemEntity
            {
                OrderingListId = clone.Id,
                CanonicalChannelId = item.CanonicalChannelId,
                Position = item.Position,
                IsEnabled = item.IsEnabled,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        }
        await context.SaveChangesAsync(cancellationToken);
        return clone;
    }

    public async Task<bool> DeleteOrderingListAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var entity = await context.OrderingLists.FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (entity == null) return false;
        context.OrderingLists.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<OrderingItemEntity> AddOrderingItemAsync(
        long orderingListId, long canonicalChannelId,
        int? position = null, bool isEnabled = true,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);

        var list = await context.OrderingLists
            .Include(l => l.Items)
            .FirstOrDefaultAsync(l => l.Id == orderingListId, cancellationToken);
        if (list == null) throw new InvalidOperationException($"OrderingList #{orderingListId} não encontrada.");

        if (!await context.CanonicalChannels.AnyAsync(c => c.Id == canonicalChannelId, cancellationToken))
        {
            throw new InvalidOperationException($"Canal canónico #{canonicalChannelId} não encontrado.");
        }

        if (list.Items.Any(i => i.CanonicalChannelId == canonicalChannelId))
        {
            throw new InvalidOperationException($"Canal #{canonicalChannelId} já está na lista #{orderingListId}.");
        }

        var now = DateTime.UtcNow;
        var insertPos = position ?? (list.Items.Count == 0 ? 0 : list.Items.Max(i => i.Position) + 1);
        // Renumera items >= insertPos para manter a continuidade.
        foreach (var existing in list.Items.Where(i => i.Position >= insertPos).OrderBy(i => i.Position).ToList())
        {
            existing.Position += 1;
            existing.UpdatedAtUtc = now;
        }

        var item = new OrderingItemEntity
        {
            OrderingListId = orderingListId,
            CanonicalChannelId = canonicalChannelId,
            Position = insertPos,
            IsEnabled = isEnabled,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.OrderingItems.Add(item);
        list.UpdatedAtUtc = now;
        await context.SaveChangesAsync(cancellationToken);
        return item;
    }

    public async Task<bool> RemoveOrderingItemAsync(long itemId, CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var item = await context.OrderingItems
            .Include(i => i.OrderingList)
            .FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        if (item == null) return false;

        var now = DateTime.UtcNow;
        context.OrderingItems.Remove(item);
        // Reaperta posições para evitar gaps.
        var remaining = await context.OrderingItems
            .Where(i => i.OrderingListId == item.OrderingListId && i.Position > item.Position)
            .OrderBy(i => i.Position)
            .ToListAsync(cancellationToken);
        foreach (var r in remaining)
        {
            r.Position -= 1;
            r.UpdatedAtUtc = now;
        }
        if (item.OrderingList != null) item.OrderingList.UpdatedAtUtc = now;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> MoveOrderingItemAsync(long itemId, int newPosition,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var item = await context.OrderingItems
            .FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        if (item == null) return false;

        var siblings = await context.OrderingItems
            .Where(i => i.OrderingListId == item.OrderingListId && i.Id != item.Id)
            .OrderBy(i => i.Position)
            .ToListAsync(cancellationToken);

        if (newPosition < 0 || newPosition > siblings.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(newPosition),
                $"newPosition tem de estar entre 0 e {siblings.Count}.");
        }

        // Renumera siblings excluindo a posição actual do item e
        // depois atribui ao item a newPosition. Isto evita conflito
        // temporário com o índice único (OrderingListId, Position).
        var now = DateTime.UtcNow;
        for (var i = 0; i < siblings.Count; i++)
        {
            var newSiblingPos = i >= newPosition ? i + 1 : i;
            if (siblings[i].Position != newSiblingPos)
            {
                siblings[i].Position = newSiblingPos;
                siblings[i].UpdatedAtUtc = now;
            }
        }

        // Move o item para uma posição temporária fora do range
        // para não colidir com índices únicos, depois para newPosition.
        item.Position = siblings.Count + 1;
        item.UpdatedAtUtc = now;
        await context.SaveChangesAsync(cancellationToken);

        item.Position = newPosition;
        item.UpdatedAtUtc = now;
        await context.SaveChangesAsync(cancellationToken);

        // Actualiza UpdatedAtUtc da lista.
        var list = await context.OrderingLists
            .FirstOrDefaultAsync(l => l.Id == item.OrderingListId, cancellationToken);
        if (list != null)
        {
            list.UpdatedAtUtc = now;
            await context.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    public async Task<bool> SetOrderingItemEnabledAsync(long itemId, bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var item = await context.OrderingItems.FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken);
        if (item == null) return false;
        item.IsEnabled = isEnabled;
        item.UpdatedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ============================================================================
    // PHASE 6 — Source Priority
    // ============================================================================

    public async Task<SourcePriorityPolicyEntity> GetOrCreateGlobalPriorityPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var existing = await context.SourcePriorityPolicies
            .FirstOrDefaultAsync(p => p.Scope == "global", cancellationToken);
        if (existing != null) return existing;

        var now = DateTime.UtcNow;
        existing = new SourcePriorityPolicyEntity
        {
            Scope = "global",
            CriteriaJson = "[\"Quality\",\"Reliability\",\"Availability\"]",
            PreferredQuality = "UHD,FHD,HD,SD",
            AllowFallback = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.SourcePriorityPolicies.Add(existing);
        await context.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<SourcePriorityPolicyEntity?> GetChannelPriorityPolicyAsync(
        long canonicalChannelId, CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.SourcePriorityPolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.CanonicalChannelId == canonicalChannelId, cancellationToken);
    }

    public async Task<SourcePriorityPolicyEntity> UpsertPriorityPolicyAsync(
        string scope, long? canonicalChannelId,
        string criteriaJson, string preferredQuality, bool allowFallback,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scope)) throw new ArgumentException("Scope é obrigatório.", nameof(scope));
        if (scope != "global" && canonicalChannelId == null)
        {
            throw new ArgumentException("Policies não-global requerem CanonicalChannelId.", nameof(canonicalChannelId));
        }

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var existing = await context.SourcePriorityPolicies
            .FirstOrDefaultAsync(p => p.Scope == scope
                && (canonicalChannelId == null
                    ? p.CanonicalChannelId == null
                    : p.CanonicalChannelId == canonicalChannelId),
                cancellationToken);

        if (existing != null)
        {
            existing.CriteriaJson = criteriaJson;
            existing.PreferredQuality = preferredQuality ?? string.Empty;
            existing.AllowFallback = allowFallback;
            existing.UpdatedAtUtc = now;
            await context.SaveChangesAsync(cancellationToken);
            return existing;
        }

        existing = new SourcePriorityPolicyEntity
        {
            Scope = scope,
            CanonicalChannelId = canonicalChannelId,
            CriteriaJson = criteriaJson,
            PreferredQuality = preferredQuality ?? string.Empty,
            AllowFallback = allowFallback,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.SourcePriorityPolicies.Add(existing);
        await context.SaveChangesAsync(cancellationToken);
        return existing;
    }

    // ============================================================================
    // PHASE 13 (Wave 13-4) — Source Selection Policy
    // ============================================================================

    public async Task<SourceSelectionPolicyEntity> GetOrCreateGlobalSourceSelectionPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var existing = await context.SourceSelectionPolicies
            .FirstOrDefaultAsync(p => p.ScopeKey == "global", cancellationToken);
        if (existing != null) return existing;

        var now = DateTime.UtcNow;
        existing = new SourceSelectionPolicyEntity
        {
            ScopeKey = "global",
            CanonicalChannelKey = null,
            MaxSourcesPerChannel = 10,
            PreferDistinctProviders = true,
            MaxSourcesPerProvider = null,
            AllowFallbackToSameProvider = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.SourceSelectionPolicies.Add(existing);
        await context.SaveChangesAsync(cancellationToken);
        return existing;
    }

    /// <summary>
    /// PHASE 13 (Wave 13-5) — Devolve a política global de selecção de fontes
    /// <b>sem a criar</b>, ou <c>null</c> se não existir. Estritamente
    /// read-only (<see cref="EntityFrameworkQueryableExtensions.AsNoTracking{TEntity}(IQueryable{TEntity})"/>),
    /// usada pelo preview/dry-run para nunca mutar o catálogo.
    /// </summary>
    public async Task<SourceSelectionPolicyEntity?> GetGlobalSourceSelectionPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.SourceSelectionPolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.ScopeKey == SourceSelectionPolicyScopes.Global, cancellationToken);
    }

    public async Task<SourceSelectionPolicyEntity> UpsertGlobalSourceSelectionPolicyAsync(
        int maxSourcesPerChannel,
        bool preferDistinctProviders,
        int? maxSourcesPerProvider,
        bool allowFallbackToSameProvider,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var existing = await context.SourceSelectionPolicies
            .FirstOrDefaultAsync(p => p.ScopeKey == "global", cancellationToken);

        if (existing != null)
        {
            existing.MaxSourcesPerChannel = maxSourcesPerChannel;
            existing.PreferDistinctProviders = preferDistinctProviders;
            existing.MaxSourcesPerProvider = maxSourcesPerProvider;
            existing.AllowFallbackToSameProvider = allowFallbackToSameProvider;
            existing.UpdatedAtUtc = now;
            await context.SaveChangesAsync(cancellationToken);
            return existing;
        }

        existing = new SourceSelectionPolicyEntity
        {
            ScopeKey = "global",
            CanonicalChannelKey = null,
            MaxSourcesPerChannel = maxSourcesPerChannel,
            PreferDistinctProviders = preferDistinctProviders,
            MaxSourcesPerProvider = maxSourcesPerProvider,
            AllowFallbackToSameProvider = allowFallbackToSameProvider,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.SourceSelectionPolicies.Add(existing);
        await context.SaveChangesAsync(cancellationToken);
        return existing;
    }

    // ----------------------------------------------------------------------------
    // PHASE 13 (Wave 13-4b) — Source Selection Policy (override por canal)
    //
    // O âmbito por canal usa ScopeKey = "channel:{key}". É intencionalmente
    // FK-less: não há validação de existência do canal canónico. Um override
    // órfão (canal inexistente/removido) fica simplesmente inerte — nunca é
    // resolvido porque nenhum ChannelSource aponta para essa chave.
    // ----------------------------------------------------------------------------

    /// <summary>
    /// Devolve o override de política de selecção de fontes para o canal
    /// canónico indicado, ou <c>null</c> se não existir.
    /// </summary>
    public async Task<SourceSelectionPolicyEntity?> GetChannelSourceSelectionPolicyAsync(
        string canonicalChannelKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(canonicalChannelKey))
        {
            throw new ArgumentException("Chave canónica é obrigatória.", nameof(canonicalChannelKey));
        }

        var scopeKey = SourceSelectionPolicyScopes.ForChannel(canonicalChannelKey.Trim());
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.SourceSelectionPolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.ScopeKey == scopeKey, cancellationToken);
    }

    /// <summary>
    /// Cria ou actualiza o override de política de selecção de fontes do
    /// canal canónico indicado. Um override é uma política <b>completa</b>:
    /// substitui a global por inteiro quando presente.
    /// </summary>
    public async Task<SourceSelectionPolicyEntity> UpsertChannelSourceSelectionPolicyAsync(
        string canonicalChannelKey,
        int maxSourcesPerChannel,
        bool preferDistinctProviders,
        int? maxSourcesPerProvider,
        bool allowFallbackToSameProvider,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(canonicalChannelKey))
        {
            throw new ArgumentException("Chave canónica é obrigatória.", nameof(canonicalChannelKey));
        }

        var key = canonicalChannelKey.Trim();
        var scopeKey = SourceSelectionPolicyScopes.ForChannel(key);
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var existing = await context.SourceSelectionPolicies
            .FirstOrDefaultAsync(p => p.ScopeKey == scopeKey, cancellationToken);

        if (existing != null)
        {
            existing.CanonicalChannelKey = key;
            existing.MaxSourcesPerChannel = maxSourcesPerChannel;
            existing.PreferDistinctProviders = preferDistinctProviders;
            existing.MaxSourcesPerProvider = maxSourcesPerProvider;
            existing.AllowFallbackToSameProvider = allowFallbackToSameProvider;
            existing.UpdatedAtUtc = now;
            await context.SaveChangesAsync(cancellationToken);
            return existing;
        }

        existing = new SourceSelectionPolicyEntity
        {
            ScopeKey = scopeKey,
            CanonicalChannelKey = key,
            MaxSourcesPerChannel = maxSourcesPerChannel,
            PreferDistinctProviders = preferDistinctProviders,
            MaxSourcesPerProvider = maxSourcesPerProvider,
            AllowFallbackToSameProvider = allowFallbackToSameProvider,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.SourceSelectionPolicies.Add(existing);
        await context.SaveChangesAsync(cancellationToken);
        return existing;
    }

    /// <summary>
    /// Apaga o override de política de selecção de fontes do canal
    /// canónico indicado. Devolve <c>true</c> se uma linha foi apagada.
    /// </summary>
    public async Task<bool> DeleteChannelSourceSelectionPolicyAsync(
        string canonicalChannelKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(canonicalChannelKey))
        {
            throw new ArgumentException("Chave canónica é obrigatória.", nameof(canonicalChannelKey));
        }

        var scopeKey = SourceSelectionPolicyScopes.ForChannel(canonicalChannelKey.Trim());
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var existing = await context.SourceSelectionPolicies
            .FirstOrDefaultAsync(p => p.ScopeKey == scopeKey, cancellationToken);
        if (existing == null) return false;

        context.SourceSelectionPolicies.Remove(existing);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Lista todos os overrides de política de selecção de fontes por canal,
    /// ordenados por <see cref="SourceSelectionPolicyEntity.ScopeKey"/>.
    /// </summary>
    public async Task<IReadOnlyList<SourceSelectionPolicyEntity>> ListChannelSourceSelectionPoliciesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.SourceSelectionPolicies
            .AsNoTracking()
            .Where(p => p.ScopeKey.StartsWith(SourceSelectionPolicyScopes.ChannelPrefix))
            .OrderBy(p => p.ScopeKey)
            .ToListAsync(cancellationToken);
    }

    // ----------------------------------------------------------------------------
    // W5.1 — RecognitionPolicy (scopes system/global/group/channel) + snapshot por Run
    //
    // Resolução e snapshot vivem no RecognitionPolicyResolver; aqui fica apenas
    // a persistência, versionamento e auditoria. Os métodos não aplicam fuzzy
    // nem alteram o algoritmo de reconhecimento.
    // ----------------------------------------------------------------------------

    /// <summary>Lista todas as linhas de RecognitionPolicy (read-only).</summary>
    public async Task<IReadOnlyList<RecognitionPolicyEntity>> ListRecognitionPoliciesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.RecognitionPolicies
            .AsNoTracking()
            .OrderBy(p => p.ScopeKey)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Devolve a policy do âmbito indicado, ou <c>null</c>.</summary>
    public async Task<RecognitionPolicyEntity?> GetRecognitionPolicyAsync(
        string scopeKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scopeKey))
        {
            throw new ArgumentException("ScopeKey é obrigatório.", nameof(scopeKey));
        }

        var key = scopeKey.Trim();
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.RecognitionPolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.ScopeKey == key, cancellationToken);
    }

    /// <summary>
    /// Cria ou actualiza a policy do âmbito indicado. Incrementa a versão e
    /// escreve um <see cref="AuditRecordEntity"/> na mesma transacção.
    /// Valores <c>null</c> em threshold/margem/pesos permanecem PARAMETER_GAP.
    /// </summary>
    public async Task<RecognitionPolicyEntity> UpsertRecognitionPolicyAsync(
        string scopeKey,
        bool enabled,
        bool fuzzyEnabled,
        int? fuzzyThreshold,
        int? fuzzyAmbiguityMargin,
        string? fuzzyWeightsJson,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scopeKey))
        {
            throw new ArgumentException("ScopeKey é obrigatório.", nameof(scopeKey));
        }

        var key = scopeKey.Trim();
        var now = DateTime.UtcNow;
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var existing = await context.RecognitionPolicies
            .FirstOrDefaultAsync(p => p.ScopeKey == key, cancellationToken);

        string? before = existing is null ? null : RecognitionPolicyAuditJson(existing);
        RecognitionPolicyEntity entity;
        if (existing is not null)
        {
            entity = existing;
            entity.Enabled = enabled;
            entity.FuzzyEnabled = fuzzyEnabled;
            entity.FuzzyThreshold = fuzzyThreshold;
            entity.FuzzyAmbiguityMargin = fuzzyAmbiguityMargin;
            entity.FuzzyWeightsJson = fuzzyWeightsJson;
            entity.Version += 1;
            entity.UpdatedAtUtc = now;
        }
        else
        {
            entity = new RecognitionPolicyEntity
            {
                ScopeKey = key,
                CanonicalChannelKey = Recognition.RecognitionPolicyScopes.ChannelKeyFromScope(key),
                GroupKey = Recognition.RecognitionPolicyScopes.GroupKeyFromScope(key),
                Enabled = enabled,
                FuzzyEnabled = fuzzyEnabled,
                FuzzyThreshold = fuzzyThreshold,
                FuzzyAmbiguityMargin = fuzzyAmbiguityMargin,
                FuzzyWeightsJson = fuzzyWeightsJson,
                Version = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            context.RecognitionPolicies.Add(entity);
        }

        context.AuditRecords.Add(new AuditRecordEntity
        {
            OccurredAtUtc = now,
            ActorType = string.IsNullOrWhiteSpace(actor) ? "system" : "user",
            ActorName = actor,
            Operation = "catalog.recognition-policy.upsert",
            ObjectType = "recognition-policy",
            ObjectId = key,
            BeforeJson = before,
            AfterJson = RecognitionPolicyAuditJson(entity),
            Result = "success",
        });

        await context.SaveChangesAsync(cancellationToken);
        return entity;
    }

    /// <summary>Conveniência: policy global.</summary>
    public Task<RecognitionPolicyEntity> UpsertGlobalRecognitionPolicyAsync(
        bool enabled,
        bool fuzzyEnabled,
        int? fuzzyThreshold = null,
        int? fuzzyAmbiguityMargin = null,
        string? fuzzyWeightsJson = null,
        string? actor = null,
        CancellationToken cancellationToken = default)
        => UpsertRecognitionPolicyAsync(
            Recognition.RecognitionPolicyScopes.Global,
            enabled, fuzzyEnabled, fuzzyThreshold, fuzzyAmbiguityMargin, fuzzyWeightsJson,
            actor, cancellationToken);

    /// <summary>Conveniência: override por grupo.</summary>
    public Task<RecognitionPolicyEntity> UpsertGroupRecognitionPolicyAsync(
        string groupKey,
        bool enabled,
        bool fuzzyEnabled,
        int? fuzzyThreshold = null,
        int? fuzzyAmbiguityMargin = null,
        string? fuzzyWeightsJson = null,
        string? actor = null,
        CancellationToken cancellationToken = default)
        => UpsertRecognitionPolicyAsync(
            Recognition.RecognitionPolicyScopes.ForGroup(groupKey),
            enabled, fuzzyEnabled, fuzzyThreshold, fuzzyAmbiguityMargin, fuzzyWeightsJson,
            actor, cancellationToken);

    /// <summary>Conveniência: override por canal.</summary>
    public Task<RecognitionPolicyEntity> UpsertChannelRecognitionPolicyAsync(
        string canonicalChannelKey,
        bool enabled,
        bool fuzzyEnabled,
        int? fuzzyThreshold = null,
        int? fuzzyAmbiguityMargin = null,
        string? fuzzyWeightsJson = null,
        string? actor = null,
        CancellationToken cancellationToken = default)
        => UpsertRecognitionPolicyAsync(
            Recognition.RecognitionPolicyScopes.ForChannel(canonicalChannelKey),
            enabled, fuzzyEnabled, fuzzyThreshold, fuzzyAmbiguityMargin, fuzzyWeightsJson,
            actor, cancellationToken);

    /// <summary>Apaga a policy do âmbito indicado; audita. Devolve <c>true</c> se apagou.</summary>
    public async Task<bool> DeleteRecognitionPolicyAsync(
        string scopeKey,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scopeKey))
        {
            throw new ArgumentException("ScopeKey é obrigatório.", nameof(scopeKey));
        }

        var key = scopeKey.Trim();
        var now = DateTime.UtcNow;
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var existing = await context.RecognitionPolicies
            .FirstOrDefaultAsync(p => p.ScopeKey == key, cancellationToken);
        if (existing is null) return false;

        context.RecognitionPolicies.Remove(existing);
        context.AuditRecords.Add(new AuditRecordEntity
        {
            OccurredAtUtc = now,
            ActorType = string.IsNullOrWhiteSpace(actor) ? "system" : "user",
            ActorName = actor,
            Operation = "catalog.recognition-policy.delete",
            ObjectType = "recognition-policy",
            ObjectId = key,
            BeforeJson = RecognitionPolicyAuditJson(existing),
            AfterJson = null,
            Result = "success",
        });
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Devolve o snapshot de um Run, ou <c>null</c>.</summary>
    public async Task<RecognitionPolicySnapshotEntity?> GetRecognitionPolicySnapshotAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId)) return null;
        var id = runId.Trim();
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.RecognitionPolicySnapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.RunId == id, cancellationToken);
    }

    /// <summary>
    /// Grava o snapshot de um Run se ainda não existir (imutável). Se já existir,
    /// devolve o existente sem o alterar.
    ///
    /// <para>
    /// <b>D-M4-02 — C1 (race-safe).</b> O snapshot é único por <c>RunId</c>
    /// (UNIQUE constraint em <c>recognition_policy_snapshots.RunId</c>,
    /// <c>ChannelCatalogDbContext.OnModelCreating</c>). O caminho rápido
    /// é a leitura prévia (curto-circuito); quando dois writers
    /// concorrentes vencem a corrida de leitura (ambos vêem
    /// <c>existing == null</c>) o segundo <c>SaveChangesAsync</c>
    /// rebenta com <see cref="DbUpdateException"/>. Em vez de propagar
    /// a falha, recarregamos o snapshot já persistido pelo vencedor e
    /// devolvemo-lo ao caller — exactamente o mesmo idioma de
    /// <see cref="RecordExternalIdentityAsync"/>. Outros erros
    /// (<see cref="DbUpdateException"/> não relacionados com o
    /// invariante de unicidade, falhas de I/O, etc.) NÃO são
    /// engolidos: propagam-se para o coordinator, que marca o run
    /// como <see cref="LiveRunTerminalStatus.Failed"/>.
    /// </para>
    /// </summary>
    public async Task<RecognitionPolicySnapshotEntity> SaveRecognitionPolicySnapshotAsync(
        string runId,
        string policiesJson,
        string resolverVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("RunId é obrigatório.", nameof(runId));
        }

        var id = runId.Trim();
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var existing = await context.RecognitionPolicySnapshots
            .FirstOrDefaultAsync(s => s.RunId == id, cancellationToken);
        if (existing is not null) return existing;

        var entity = new RecognitionPolicySnapshotEntity
        {
            RunId = id,
            ResolverVersion = resolverVersion,
            PoliciesJson = policiesJson,
            ResolvedAtUtc = DateTime.UtcNow,
        };
        context.RecognitionPolicySnapshots.Add(entity);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return entity;
        }
        catch (DbUpdateException)
        {
            // Corrida: outro writer inseriu o snapshot para o mesmo RunId
            // entre a nossa leitura e o SaveChanges. O invariante de
            // unicidade manda; não duplicar — devolver o registo existente.
            // O context já foi disposed pelo using; abrir um novo apenas
            // para a re-leitura é mais barato que re-lançar.
            await context.DisposeAsync();
            await using var reload = await _factory.CreateDbContextAsync(cancellationToken);
            return await reload.RecognitionPolicySnapshots
                .FirstAsync(s => s.RunId == id, cancellationToken);
        }
    }

    private static string RecognitionPolicyAuditJson(RecognitionPolicyEntity e)
        => $"{{\"scopeKey\":\"{e.ScopeKey}\",\"enabled\":{e.Enabled.ToString().ToLowerInvariant()}," +
           $"\"fuzzyEnabled\":{e.FuzzyEnabled.ToString().ToLowerInvariant()}," +
           $"\"fuzzyThreshold\":{(e.FuzzyThreshold?.ToString() ?? "null")}," +
           $"\"fuzzyAmbiguityMargin\":{(e.FuzzyAmbiguityMargin?.ToString() ?? "null")}," +
           $"\"version\":{e.Version}}}";

    // ============================================================================
    // PHASE 8 — TV/Radio/VOD/Groups + Import Policies
    // ============================================================================

    public async Task<IReadOnlyList<ImportPolicyEntity>> ListImportPoliciesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.ImportPolicies
            .AsNoTracking()
            .OrderBy(p => p.MediaKind)
            .ToListAsync(cancellationToken);
    }

    public async Task<ImportPolicyEntity?> GetImportPolicyAsync(MediaKind kind,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.ImportPolicies
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.MediaKind == kind, cancellationToken);
    }

    public async Task<ImportPolicyEntity> UpsertImportPolicyAsync(
        MediaKind kind, VodPolicy vodPolicy,
        string targetGroupsCsv, string excludedGroupsCsv, bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        if (targetGroupsCsv.Length > 2000) throw new ArgumentException("TargetGroupsCsv excede 2000 caracteres.", nameof(targetGroupsCsv));
        if (excludedGroupsCsv.Length > 2000) throw new ArgumentException("ExcludedGroupsCsv excede 2000 caracteres.", nameof(excludedGroupsCsv));

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var existing = await context.ImportPolicies
            .FirstOrDefaultAsync(p => p.MediaKind == kind, cancellationToken);
        if (existing != null)
        {
            existing.VodPolicy = vodPolicy;
            existing.TargetGroupsCsv = targetGroupsCsv ?? string.Empty;
            existing.ExcludedGroupsCsv = excludedGroupsCsv ?? string.Empty;
            existing.IsEnabled = isEnabled;
            existing.UpdatedAtUtc = now;
            await context.SaveChangesAsync(cancellationToken);
            return existing;
        }
        existing = new ImportPolicyEntity
        {
            MediaKind = kind,
            VodPolicy = vodPolicy,
            TargetGroupsCsv = targetGroupsCsv ?? string.Empty,
            ExcludedGroupsCsv = excludedGroupsCsv ?? string.Empty,
            IsEnabled = isEnabled,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.ImportPolicies.Add(existing);
        await context.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<IReadOnlyList<CanonicalGroupEntity>> ListCanonicalGroupsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.CanonicalGroups
            .AsNoTracking()
            .OrderBy(g => g.Order).ThenBy(g => g.DisplayName)
            .ToListAsync(cancellationToken);
    }

    public async Task<CanonicalGroupEntity> UpsertCanonicalGroupAsync(
        string key, string displayName, string? country, int order,
        bool isEnabled, bool isDefault,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key é obrigatória.", nameof(key));
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("DisplayName é obrigatório.", nameof(displayName));
        if (key.Length > 120) throw new ArgumentException("Key excede 120 caracteres.", nameof(key));
        if (displayName.Length > 200) throw new ArgumentException("DisplayName excede 200 caracteres.", nameof(displayName));

        var normalizedKey = key.Trim();
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var existing = await context.CanonicalGroups
            .FirstOrDefaultAsync(g => g.Key == normalizedKey, cancellationToken);
        if (existing != null)
        {
            existing.DisplayName = displayName.Trim();
            existing.Country = string.IsNullOrWhiteSpace(country) ? null : country.Trim();
            existing.Order = order;
            existing.IsEnabled = isEnabled;
            existing.IsDefault = isDefault;
            existing.UpdatedAtUtc = now;
            await context.SaveChangesAsync(cancellationToken);
            return existing;
        }
        existing = new CanonicalGroupEntity
        {
            Key = normalizedKey,
            DisplayName = displayName.Trim(),
            Country = string.IsNullOrWhiteSpace(country) ? null : country.Trim(),
            Order = order,
            IsEnabled = isEnabled,
            IsDefault = isDefault,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.CanonicalGroups.Add(existing);
        await context.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<bool> DeleteCanonicalGroupAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var entity = await context.CanonicalGroups.FirstOrDefaultAsync(g => g.Id == id, cancellationToken);
        if (entity == null) return false;
        context.CanonicalGroups.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<GroupMappingEntity>> ListGroupMappingsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.GroupMappings
            .AsNoTracking()
            .Include(m => m.CanonicalGroup)
            .OrderBy(m => m.SourceKind).ThenBy(m => m.SourceGroupTitle)
            .ToListAsync(cancellationToken);
    }

    public async Task<GroupMappingEntity> UpsertGroupMappingAsync(
        SourceKind sourceKind, string sourceGroupTitle, long canonicalGroupId,
        bool isEnabled = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceGroupTitle)) throw new ArgumentException("SourceGroupTitle é obrigatório.", nameof(sourceGroupTitle));
        if (sourceGroupTitle.Length > 400) throw new ArgumentException("SourceGroupTitle excede 400 caracteres.", nameof(sourceGroupTitle));

        var normalizedTitle = sourceGroupTitle.Trim();
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        if (!await context.CanonicalGroups.AnyAsync(g => g.Id == canonicalGroupId, cancellationToken))
        {
            throw new InvalidOperationException($"CanonicalGroup #{canonicalGroupId} não encontrada.");
        }
        var now = DateTime.UtcNow;
        var existing = await context.GroupMappings
            .FirstOrDefaultAsync(m => m.SourceKind == sourceKind && m.SourceGroupTitle == normalizedTitle,
                cancellationToken);
        if (existing != null)
        {
            existing.CanonicalGroupId = canonicalGroupId;
            existing.IsEnabled = isEnabled;
            existing.UpdatedAtUtc = now;
            await context.SaveChangesAsync(cancellationToken);
            return existing;
        }
        existing = new GroupMappingEntity
        {
            SourceKind = sourceKind,
            SourceGroupTitle = normalizedTitle,
            CanonicalGroupId = canonicalGroupId,
            IsEnabled = isEnabled,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.GroupMappings.Add(existing);
        await context.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<bool> DeleteGroupMappingAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var entity = await context.GroupMappings.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (entity == null) return false;
        context.GroupMappings.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ============================================================================
    // PHASE 3 — Matching observability
    // ============================================================================

    /// <summary>
    /// Regista uma decisão de <see cref="ResolveAsync"/> para
    /// observabilidade. Pode ser chamado pelo próprio pipeline
    /// após cada resolução.
    /// </summary>
    public async Task RecordMatchingAuditAsync(
        string normalizedIdentity,
        string originalTitle,
        string? sourceGroup,
        CatalogResolutionKind kind,
        long? canonicalChannelId,
        double confidence,
        string reasonSignature,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedIdentity)) return;
        if (originalTitle.Length > 500) originalTitle = originalTitle[..500];
        if (reasonSignature.Length > 120) reasonSignature = reasonSignature[..120];

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        context.MatchingAudits.Add(new MatchingAuditEntity
        {
            NormalizedIdentity = normalizedIdentity,
            OriginalTitle = originalTitle,
            SourceGroup = sourceGroup,
            ResolutionKind = kind.ToString(),
            CanonicalChannelId = canonicalChannelId,
            Confidence = confidence,
            ReasonSignature = reasonSignature,
            AtUtc = DateTime.UtcNow,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MatchingAuditEntity>> GetRecentMatchingAuditsAsync(
        int limit = 100,
        long? canonicalChannelId = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var query = context.MatchingAudits.AsNoTracking().AsQueryable();
        if (canonicalChannelId.HasValue)
        {
            query = query.Where(a => a.CanonicalChannelId == canonicalChannelId.Value);
        }
        return await query
            .OrderByDescending(a => a.AtUtc)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToListAsync(cancellationToken);
    }

    public async Task<MatchingAuditStats> GetMatchingAuditStatsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var total = await context.MatchingAudits.AsNoTracking().CountAsync(cancellationToken);
        var canonicalCount = await context.MatchingAudits.AsNoTracking()
            .CountAsync(a => a.ResolutionKind == "Canonical", cancellationToken);
        var aliasCount = await context.MatchingAudits.AsNoTracking()
            .CountAsync(a => a.ResolutionKind == "Alias", cancellationToken);
        var ruleCount = await context.MatchingAudits.AsNoTracking()
            .CountAsync(a => a.ResolutionKind == "Rule", cancellationToken);
        var unknownCount = await context.MatchingAudits.AsNoTracking()
            .CountAsync(a => a.ResolutionKind == "Unknown", cancellationToken);
        var last24h = await context.MatchingAudits.AsNoTracking()
            .CountAsync(a => a.AtUtc >= now.AddHours(-24), cancellationToken);

        return new MatchingAuditStats
        {
            Total = total,
            Canonical = canonicalCount,
            Alias = aliasCount,
            Rule = ruleCount,
            Unknown = unknownCount,
            Last24h = last24h,
        };
    }

    // ============================================================================
    // PHASE 9 b — ChannelSource observation history
    // ============================================================================

    public async Task RecordChannelSourceObservationAsync(
        long channelSourceId,
        StreamQuality quality,
        EpgState epg,
        AvailabilityState availability,
        long responseTimeMs,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        context.ChannelSourceObservations.Add(new ChannelSourceObservationEntity
        {
            ChannelSourceId = channelSourceId,
            Quality = quality,
            Epg = epg,
            Availability = availability,
            ResponseTimeMs = responseTimeMs,
            ObservedAtUtc = DateTime.UtcNow,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// PHASE W6b-2 — Registo idempotente de uma observação associada a um
    /// evento de validação concreto. Deduplica por
    /// <c>(ChannelSourceId, ObservedAtUtc)</c>: a mesma validação
    /// re-ingerida não cria uma segunda row, mas uma nova validação
    /// (timestamp distinto) é registada como nova amostra histórica.
    /// Devolve <c>null</c> quando a observação já existia.
    /// </summary>
    public async Task<ChannelSourceObservationEntity?> RecordChannelSourceObservationIfAbsentAsync(
        long channelSourceId,
        StreamQuality quality,
        EpgState epg,
        AvailabilityState availability,
        long responseTimeMs,
        DateTime observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (channelSourceId <= 0)
            throw new ArgumentException("ChannelSourceId inválido.", nameof(channelSourceId));

        var observedUtc = observedAtUtc.Kind == DateTimeKind.Utc
            ? observedAtUtc
            : observedAtUtc.ToUniversalTime();

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var exists = await context.ChannelSourceObservations
            .AnyAsync(o => o.ChannelSourceId == channelSourceId
                        && o.ObservedAtUtc == observedUtc, cancellationToken);
        if (exists) return null;

        var entity = new ChannelSourceObservationEntity
        {
            ChannelSourceId = channelSourceId,
            Quality = quality,
            Epg = epg,
            Availability = availability,
            ResponseTimeMs = Math.Max(0, responseTimeMs),
            ObservedAtUtc = observedUtc,
        };
        context.ChannelSourceObservations.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<IReadOnlyList<ChannelSourceObservationEntity>> GetChannelSourceObservationsAsync(
        long channelSourceId,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.ChannelSourceObservations
            .AsNoTracking()
            .Where(o => o.ChannelSourceId == channelSourceId)
            .OrderByDescending(o => o.ObservedAtUtc)
            .Take(Math.Clamp(limit, 1, 5000))
            .ToListAsync(cancellationToken);
    }

    // ============================================================================
    // PHASE 11 — Runs (per-step breakdown)
    // ============================================================================

    public async Task<SyncRunStepEntity> RecordSyncRunStepAsync(
        long syncRunId, string step,
        DateTime startedAtUtc, DateTime finishedAtUtc,
        int itemsProcessed, int itemsSucceeded, int itemsFailed,
        string result = "ok",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(step)) throw new ArgumentException("Step é obrigatório.", nameof(step));
        if (step.Length > 80) throw new ArgumentException("Step excede 80 caracteres.", nameof(step));
        if (result.Length > 40) throw new ArgumentException("Result excede 40 caracteres.", nameof(result));

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var stepEntity = new SyncRunStepEntity
        {
            SyncRunId = syncRunId,
            Step = step,
            StartedAtUtc = startedAtUtc,
            FinishedAtUtc = finishedAtUtc,
            DurationMs = Math.Max(0, (long)(finishedAtUtc - startedAtUtc).TotalMilliseconds),
            ItemsProcessed = itemsProcessed,
            ItemsSucceeded = itemsSucceeded,
            ItemsFailed = itemsFailed,
            Result = result,
        };
        context.SyncRunSteps.Add(stepEntity);
        await context.SaveChangesAsync(cancellationToken);
        return stepEntity;
    }

    public async Task<IReadOnlyList<SyncRunStepEntity>> GetSyncRunStepsAsync(
        long syncRunId, CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.SyncRunSteps
            .AsNoTracking()
            .Where(s => s.SyncRunId == syncRunId)
            .OrderBy(s => s.StartedAtUtc)
            .ToListAsync(cancellationToken);
    }

    // ============================================================================
    // PHASE 12 — Scheduled jobs
    // ============================================================================

    public async Task<IReadOnlyList<ScheduledJobEntity>> ListScheduledJobsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        return await context.ScheduledJobs
            .AsNoTracking()
            .OrderBy(j => j.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<ScheduledJobEntity> UpsertScheduledJobAsync(
        string name, string cronExpression, string actionName, bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name é obrigatório.", nameof(name));
        if (name.Length > 120) throw new ArgumentException("Name excede 120 caracteres.", nameof(name));
        if (string.IsNullOrWhiteSpace(cronExpression)) throw new ArgumentException("CronExpression é obrigatória.", nameof(cronExpression));
        if (cronExpression.Length > 80) throw new ArgumentException("CronExpression excede 80 caracteres.", nameof(cronExpression));
        if (string.IsNullOrWhiteSpace(actionName)) throw new ArgumentException("ActionName é obrigatória.", nameof(actionName));
        if (actionName.Length > 80) throw new ArgumentException("ActionName excede 80 caracteres.", nameof(actionName));
        // Validate cron syntax eagerly so the operator gets feedback at upsert time.
        _ = m3uCrawler.Services.Automation.CronExpression.Parse(cronExpression);

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var existing = await context.ScheduledJobs
            .FirstOrDefaultAsync(j => j.Name == name, cancellationToken);
        if (existing != null)
        {
            existing.CronExpression = cronExpression;
            existing.ActionName = actionName;
            existing.IsEnabled = isEnabled;
            existing.UpdatedAtUtc = now;
            existing.NextRunAtUtc = m3uCrawler.Services.Automation.CronExpression.Parse(cronExpression)
                .NextOccurrence(now);
            await context.SaveChangesAsync(cancellationToken);
            return existing;
        }
        var entity = new ScheduledJobEntity
        {
            Name = name,
            CronExpression = cronExpression,
            ActionName = actionName,
            IsEnabled = isEnabled,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            NextRunAtUtc = m3uCrawler.Services.Automation.CronExpression.Parse(cronExpression)
                .NextOccurrence(now),
        };
        context.ScheduledJobs.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<bool> SetScheduledJobEnabledAsync(long id, bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var entity = await context.ScheduledJobs.FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
        if (entity == null) return false;
        entity.IsEnabled = isEnabled;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteScheduledJobAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var entity = await context.ScheduledJobs.FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
        if (entity == null) return false;
        context.ScheduledJobs.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> MarkScheduledJobRanAsync(long id, DateTime ranAtUtc, string result,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var entity = await context.ScheduledJobs.FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
        if (entity == null) return false;
        entity.LastRunAtUtc = ranAtUtc;
        entity.LastResult = result;
        entity.UpdatedAtUtc = ranAtUtc;
        entity.NextRunAtUtc = m3uCrawler.Services.Automation.CronExpression.Parse(entity.CronExpression)
            .NextOccurrence(ranAtUtc);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ============================================================================
    // PHASE 9 — Stream degradation dashboard (visão agregada)
    // ============================================================================

    /// <summary>
    /// Identifica <see cref="ChannelSourceEntity"/> cujo estado mais recente
    /// é terminal (Dead / Unreachable / Timeout) E que anteriormente
    /// estavam em estado positivo (Validated / Reachable). São os
    /// candidatos a "stream degradado" no Dashboard.
    /// </summary>
    public async Task<IReadOnlyList<DegradedStreamSummary>> GetDegradedStreamsAsync(
        int lookbackMinutes = 60 * 24 * 7,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        lookbackMinutes = Math.Clamp(lookbackMinutes, 1, 60 * 24 * 30);
        limit = Math.Clamp(limit, 1, 2000);
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var from = now.AddMinutes(-lookbackMinutes);

        // Carrega todas as observações activas no lookback.
        var observations = await context.ChannelSourceObservations
            .AsNoTracking()
            .Where(o => o.ObservedAtUtc >= from)
            .OrderByDescending(o => o.ObservedAtUtc)
            .ToListAsync(cancellationToken);

        // Agrupa por ChannelSourceId e classifica.
        var byChannelSource = observations
            .GroupBy(o => o.ChannelSourceId)
            .Where(g => g.Any(o => IsTerminalState(o.Availability))
                     && g.Any(o => IsHealthyState(o.Availability)))
            .Select(g => new
            {
                ChannelSourceId = g.Key,
                Latest = g.First(),
                LastHealthy = g.FirstOrDefault(o => IsHealthyState(o.Availability)),
                LatestTerminal = g.FirstOrDefault(o => IsTerminalState(o.Availability)),
                TotalSamples = g.Count(),
                FailedSamples = g.Count(o => IsTerminalState(o.Availability)),
            })
            .Where(x => x.LatestTerminal != null)
            .Take(limit)
            .ToList();

        // Carrega os ChannelSources correspondentes para enriquecer o output.
        var ids = byChannelSource.Select(x => x.ChannelSourceId).ToList();
        var sources = await context.ChannelSources
            .AsNoTracking()
            .Include(cs => cs.Source)
            .Where(cs => ids.Contains(cs.Id))
            .ToListAsync(cancellationToken);

        var byId = sources.ToDictionary(s => s.Id);
        var result = new List<DegradedStreamSummary>();
        foreach (var entry in byChannelSource)
        {
            if (!byId.TryGetValue(entry.ChannelSourceId, out var cs)) continue;
            var failureRate = entry.TotalSamples == 0
                ? 0d
                : (double)entry.FailedSamples / entry.TotalSamples;
            result.Add(new DegradedStreamSummary
            {
                ChannelSourceId = cs.Id,
                CanonicalChannelId = cs.CanonicalChannelId,
                SourceId = cs.SourceId,
                SourceName = cs.Source?.Name ?? "—",
                LatestAvailability = entry.Latest.Availability,
                LatestObservedAtUtc = entry.Latest.ObservedAtUtc,
                LatestResponseMs = entry.Latest.ResponseTimeMs,
                LastHealthyAtUtc = entry.LastHealthy?.ObservedAtUtc,
                TotalSamples = entry.TotalSamples,
                FailedSamples = entry.FailedSamples,
                FailureRate = failureRate,
                Quality = cs.Quality,
                Epg = cs.Epg,
            });
        }

        return result
            .OrderByDescending(r => r.LatestObservedAtUtc)
            .ToList();
    }

    public async Task<DegradationStats> GetDegradationStatsAsync(
        int lookbackMinutes = 60 * 24,
        CancellationToken cancellationToken = default)
    {
        lookbackMinutes = Math.Clamp(lookbackMinutes, 1, 60 * 24 * 30);
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var from = DateTime.UtcNow.AddMinutes(-lookbackMinutes);
        var observations = await context.ChannelSourceObservations
            .AsNoTracking()
            .Where(o => o.ObservedAtUtc >= from)
            .ToListAsync(cancellationToken);

        var dead = observations.Count(o => o.Availability == AvailabilityState.Dead);
        var timeout = observations.Count(o => o.Availability == AvailabilityState.Timeout);
        var unreachable = observations.Count(o => o.Availability == AvailabilityState.Unreachable);
        var reachable = observations.Count(o => o.Availability == AvailabilityState.Reachable);
        var validated = observations.Count(o => o.Availability == AvailabilityState.Validated);

        return new DegradationStats
        {
            LookbackMinutes = lookbackMinutes,
            DeadSamples = dead,
            TimeoutSamples = timeout,
            UnreachableSamples = unreachable,
            ReachableSamples = reachable,
            ValidatedSamples = validated,
            TerminalRate = observations.Count == 0 ? 0 :
                (double)(dead + timeout + unreachable) / observations.Count,
        };
    }

    private static bool IsTerminalState(AvailabilityState s) =>
        s is AvailabilityState.Dead
            or AvailabilityState.Unreachable
            or AvailabilityState.Timeout;

    private static bool IsHealthyState(AvailabilityState s) =>
        s is AvailabilityState.Validated
            or AvailabilityState.Reachable;
}

public sealed class DegradedStreamSummary
{
    public long ChannelSourceId { get; set; }
    public long CanonicalChannelId { get; set; }
    public long SourceId { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public AvailabilityState LatestAvailability { get; set; }
    public DateTime LatestObservedAtUtc { get; set; }
    public long LatestResponseMs { get; set; }
    public DateTime? LastHealthyAtUtc { get; set; }
    public int TotalSamples { get; set; }
    public int FailedSamples { get; set; }
    public double FailureRate { get; set; }
    public StreamQuality Quality { get; set; }
    public EpgState Epg { get; set; }
}

public sealed class DegradationStats
{
    public int LookbackMinutes { get; set; }
    public int DeadSamples { get; set; }
    public int TimeoutSamples { get; set; }
    public int UnreachableSamples { get; set; }
    public int ReachableSamples { get; set; }
    public int ValidatedSamples { get; set; }
    public double TerminalRate { get; set; }
}

public sealed class MatchingAuditStats
{
    public int Total { get; set; }
    public int Canonical { get; set; }
    public int Alias { get; set; }
    public int Rule { get; set; }
    public int Unknown { get; set; }
    public int Last24h { get; set; }
}

public sealed class CatalogStats
{
    public int CanonicalChannels { get; set; }
    public int ChannelAliases { get; set; }
    public int IdentityRules { get; set; }
    public int AffinityGroups { get; set; }
    public int AffinityMembers { get; set; }
    public int DispatcharrChannelOwnerships { get; set; }
    public int DispatcharrStreamOwnerships { get; set; }
    public int ReviewItemsOpen { get; set; }
    public int ReviewItemsInReview { get; set; }
    public int ReviewItemsResolved { get; set; }
    public int ReviewItemsIgnored { get; set; }
    public int SyncRuns { get; set; }
    public int PendingCountryApprovals { get; set; }
    public int PendingCountryApprovalsOpen { get; set; }
    public int Sources { get; set; }
    public int ChannelSources { get; set; }
    public int OrderingLists { get; set; }
    public int OrderingItems { get; set; }
    public int SourcePriorityPolicies { get; set; }
    public int ImportPolicies { get; set; }
    public int CanonicalGroups { get; set; }
    public int GroupMappings { get; set; }
    public int MatchingAudits { get; set; }
    public int ChannelSourceObservations { get; set; }
    public int SyncRunSteps { get; set; }
    public int ScheduledJobs { get; set; }
    public string DbPath { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; }
}

/// <summary>
/// Resultado de <see cref="CatalogResolver.ResolveAsync"/>. O tipo
/// discriminado (struct) garante que a caller sabe sempre o
/// caminho: Canonical, Rule, ou Unknown.
/// </summary>
public readonly record struct CatalogResolution(
    CatalogResolutionKind Kind,
    long? CanonicalChannelId,
    string? CanonicalKey,
    string? DisplayName,
    EditorialCategory? EditorialCategory,
    CanonicalEditorialGroup? EditorialGroup,
    PublicationPolicy PublicationPolicy,
    RuleDisposition? RuleDisposition,
    string? RuleReason)
{
    public string? MatchMethod { get; init; }

    /// <summary>
    /// W5.6 — Confiança do reconhecimento (<c>double 0..1</c>), method-specific,
    /// atribuída pela Recognition segundo a tabela normativa
    /// (<see cref="RecognitionMatchMethods.TryGetMatchConfidence"/>). É
    /// <c>null</c> em <c>Unknown</c>/<c>Ambiguous</c> e quando o método é
    /// nulo/desconhecido. <b>Não</b> é <c>FuzzyScore</c> (domínio <c>0..100</c>)
    /// e nunca é calculada no pipeline.
    /// </summary>
    public double? MatchConfidence { get; init; }

    /// <summary>
    /// Versão da <c>RecognitionPolicy</c> consumida (snapshot) quando o
    /// resultado foi produzido. <c>null</c> quando não foi fornecida
    /// policy explícita. Registo, não autoridade.
    /// </summary>
    public int? PolicyVersion { get; init; }

    /// <summary>
    /// W5.3 — Score técnico do passo fuzzy (<c>0..100</c>), preenchido quando
    /// o método efectivo é <see cref="RecognitionMatchMethods.Fuzzy"/>. Não é
    /// <c>MatchConfidence</c> (domínio <c>0..1</c>, W5.6).
    /// </summary>
    public int? FuzzyScore { get; init; }

    /// <summary>
    /// W5.3 — Diagnóstico técnico do passo fuzzy (candidatos, scores e motivo
    /// de decisão) para consumo por W5.4. É evidência, não autoridade: não
    /// altera a decisão nem cria Review.
    /// </summary>
    public FuzzyRecognitionDiagnostic? FuzzyDiagnostic { get; init; }

    /// <summary>
    /// Resultado P6 de Recognition (W5.2) derivado do caminho:
    /// <c>Canonical</c>, <c>Ambiguous</c>, <c>Excluded</c> (regra
    /// determinística), <c>Review</c> (IdentityRule não-exclusiva) ou
    /// <c>Unknown</c>.
    /// </summary>
    public RecognitionOutcome Outcome => Kind switch
    {
        CatalogResolutionKind.Canonical => RecognitionOutcome.Canonical,
        CatalogResolutionKind.Ambiguous => RecognitionOutcome.Ambiguous,
        CatalogResolutionKind.Rule when RuleDisposition
            == global::m3uCrawler.Services.Catalog.RuleDisposition.Excluded => RecognitionOutcome.Excluded,
        CatalogResolutionKind.Rule => RecognitionOutcome.Review,
        _ => RecognitionOutcome.Unknown,
    };

    public static CatalogResolution Unknown() => new(
        CatalogResolutionKind.Unknown,
        null, null, null, null, null,
        PublicationPolicy.Excluded, null, null);

    public static CatalogResolution FromCanonical(CanonicalChannelEntity ch, string? matchMethod = null) => new(
        CatalogResolutionKind.Canonical,
        ch.Id, ch.Key, ch.DisplayName,
        ch.EditorialCategory, ch.EditorialGroup,
        ch.PublicationPolicy, null, null)
    {
        MatchMethod = matchMethod,
        MatchConfidence = RecognitionMatchMethods.TryGetMatchConfidence(matchMethod, out var confidence)
            ? confidence
            : null,
    };

    public static CatalogResolution FromRule(IdentityRuleEntity rule) => new(
        CatalogResolutionKind.Rule,
        null, null, null, null, null,
        rule.Disposition == global::m3uCrawler.Services.Catalog.RuleDisposition.Excluded
            ? PublicationPolicy.Excluded
            : PublicationPolicy.ReviewOnly,
        rule.Disposition, rule.Reason)
    {
        MatchMethod = RecognitionMatchMethods.ManualReview,
        MatchConfidence = RecognitionMatchMethods.TryGetMatchConfidence(
            RecognitionMatchMethods.ManualReview, out var confidence) ? confidence : null,
    };

    /// <summary>
    /// Resultado ambíguo (ex.: a mesma identidade externa aponta
    /// para canais distintos). Nunca resolve canal e nunca cria
    /// identidade; o caller deve conduzir a Review.
    /// </summary>
    public static CatalogResolution Ambiguous(string reason) => new(
        CatalogResolutionKind.Ambiguous,
        null, null, null, null, null,
        PublicationPolicy.ReviewOnly, null, reason);

    /// <summary>
    /// Verdadeiro se o matcher pode criar um canal novo no
    /// Dispatcharr a partir desta entrada. Só <c>Canonical</c> com
    /// <see cref="PublicationPolicy.CreateEligible"/> permite
    /// criação; <c>Rule</c> pode bloquear (ReviewOnly) ou
    /// marcar excluído.
    /// </summary>
    public bool AllowsNewChannel => Kind == CatalogResolutionKind.Canonical
        && PublicationPolicy == PublicationPolicy.CreateEligible;
}

public enum CatalogResolutionKind
{
    Unknown = 0,
    Canonical = 1,
    Rule = 2,
    /// <summary>Evidência suficiente mas conflituosa (sem desempate) → Review.</summary>
    Ambiguous = 3,
}

/// <summary>
/// Resultado de <see cref="CatalogResolver.RecordExternalIdentityAsync"/>.
/// </summary>
public enum RecordExternalIdentityOutcome
{
    /// <summary>Nada escrito (valor sem forma canónica).</summary>
    Ignored = 0,

    /// <summary>Nova associação criada.</summary>
    Created = 1,

    /// <summary>Já existia a associação idêntica (idempotente).</summary>
    Unchanged = 2,

    /// <summary>Já existia o par (Namespace, Value) para outro canal; não sobreposto.</summary>
    Conflict = 3,
}

/// <summary>
/// Gera fingerprints determinísticos para <see cref="ReviewItemEntity"/>.
/// SHA-256 hex de "{normalizedIdentity}|{sourceGroup}|{reasonSignature}".
/// </summary>
public static class ReviewFingerprint
{
    public static string Of(string normalizedIdentity, string sourceGroup, string reasonSignature)
    {
        var input = $"{normalizedIdentity ?? string.Empty}|{sourceGroup ?? string.Empty}|{reasonSignature ?? string.Empty}";
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
