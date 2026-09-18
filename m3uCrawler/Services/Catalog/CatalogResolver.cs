using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

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

    public CatalogResolver(IDbContextFactory<ChannelCatalogDbContext> factory, string dbPath)
    {
        _factory = factory;
        _dbPath = dbPath;
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
    ///   <item><b>ChannelAlias</b> resolve para o <c>CanonicalChannel</c>
    ///         correspondente, se existir.</item>
    ///   <item>Caso contrário, retorna <c>null</c> (Unknown sem
    ///         canal canónico).</item>
    /// </list>
    /// </summary>
    public async Task<CatalogResolution> ResolveAsync(
        string normalizedIdentity, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedIdentity))
        {
            return CatalogResolution.Unknown();
        }

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);

        // 1. IdentityRule (priority over everything).
        var rule = await context.IdentityRules
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.NormalizedIdentity == normalizedIdentity, cancellationToken);
        if (rule != null)
        {
            return CatalogResolution.FromRule(rule);
        }

        // 2. AffinityMember (Kind = Channel) -> AffinityGroup ->
        //    CanonicalChannel. Membros Country não resolvem canal.
        //    Wave 9C.6: CanonicalChannelKey é a identidade de runtime
        //    autoritativa; CanonicalChannelId/nav é apenas fallback
        //    legado quando a Key está ausente.
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
                    return CatalogResolution.FromCanonical(canonicalByKey);
                }

                // Key presente mas sem resolução (inexistente/desactivada):
                // a Key é autoritativa, logo não cair no CanonicalChannelId
                // obsoleto. Prossegue para o passo de alias.
            }
            else if (member.AffinityGroup.CanonicalChannel != null
                && member.AffinityGroup.CanonicalChannel.IsEnabled)
            {
                return CatalogResolution.FromCanonical(member.AffinityGroup.CanonicalChannel);
            }
        }

        // 3. ChannelAlias -> CanonicalChannel.
        var alias = await context.ChannelAliases
            .AsNoTracking()
            .Include(a => a.CanonicalChannel)
            .FirstOrDefaultAsync(a => a.NormalizedAlias == normalizedIdentity, cancellationToken);
        if (alias?.CanonicalChannel != null && alias.CanonicalChannel.IsEnabled)
        {
            return CatalogResolution.FromCanonical(alias.CanonicalChannel);
        }

        // 3. Unknown (no canonical channel).
        return CatalogResolution.Unknown();
    }

    /// <summary>
    /// Regista (ou actualiza) um item de revisão. Idempotente: se
    /// já existir um item com o mesmo fingerprint em estado Open,
    /// não cria duplicado. Devolve a entrada persistida.
    /// </summary>
    public async Task<ReviewItemEntity> UpsertReviewItemAsync(
        string normalizedIdentity,
        string sourceGroup,
        string reasonSignature,
        string reasonText,
        CancellationToken cancellationToken = default)
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
            if (existing.State == ReviewItemState.Open)
            {
                return existing;
            }
            // Aprovado/excluído: reabrir como Open para nova evidência.
            existing.State = ReviewItemState.Open;
            existing.UpdatedAtUtc = DateTime.UtcNow;
            existing.ResolvedAtUtc = null;
            existing.Note = string.Empty;
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

        var members = CleanMembers(normalizedMembers);
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

    private static List<string> CleanMembers(IReadOnlyList<string>? normalizedMembers)
    {
        if (normalizedMembers == null) return new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in normalizedMembers)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var value = raw.Trim();
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

        var members = CleanMembers(normalizedMembers);
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

    /// <summary>
    /// Approva um item de revisão (ReviewItemState.Approved) e
    /// opcionalmente regista o canal canónico aprovado.
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

        item.State = ReviewItemState.Approved;
        item.ResolvedAtUtc = DateTime.UtcNow;
        item.UpdatedAtUtc = DateTime.UtcNow;
        item.ApprovedCanonicalChannelId = approvedCanonicalChannelId;
        await context.SaveChangesAsync(cancellationToken);
        return item;
    }

    /// <summary>
    /// Exclui um item de revisão (ReviewItemState.Excluded).
    /// </summary>
    public async Task<ReviewItemEntity?> ExcludeReviewAsync(
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var item = await context.ReviewItems
            .FirstOrDefaultAsync(r => r.Fingerprint == fingerprint, cancellationToken);
        if (item == null) return null;

        item.State = ReviewItemState.Excluded;
        item.ResolvedAtUtc = DateTime.UtcNow;
        item.UpdatedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return item;
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
    /// Aprova um canal pendente: cria IdentityRule com CreateEligible
    /// e marca o pending como Approved. Opcionalmente adiciona o membro
    /// ao grupo de afinidade do país (criando o grupo se não existir).
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

        var normalizedAliasSet = new HashSet<string>(
            normalizedAliases.Select(a => a.Trim()), StringComparer.Ordinal);
        if (normalizedAliasSet.Count != normalizedAliases.Count)
        {
            throw new ChannelAdministrationException(
                ChannelAdministrationError.InvalidInput,
                "Aliases duplicados no payload (cada alias deve aparecer uma única vez).");
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
    /// <paramref name="normalizedAlias"/> como alias se for não-vazio
    /// e não colidir com nenhum alias já existente noutro canal.
    /// Usado pela pipeline de ingestão (Telegram/M3U/M3U8-search) para
    /// criar canais desconhecidos sem bloquear em duplicados.
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
            var alias = normalizedAlias.Trim();
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
        var alias = normalizedAlias.Trim();

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

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var alias = await context.ChannelAliases
            .FirstOrDefaultAsync(a => a.CanonicalChannelId == channelId && a.NormalizedAlias == normalizedAlias, cancellationToken);
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
            ReviewItemsApproved = await context.ReviewItems.AsNoTracking().CountAsync(r => r.State == ReviewItemState.Approved, cancellationToken),
            ReviewItemsExcluded = await context.ReviewItems.AsNoTracking().CountAsync(r => r.State == ReviewItemState.Excluded, cancellationToken),
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
    /// Cria ou actualiza uma <see cref="SourceEntity"/> pela chave
    /// (slug). A origem é sanitizada antes de ser persistida para
    /// não guardar credenciais em claro.
    /// </summary>
    public async Task<SourceEntity> EnsureSourceAsync(
        string key, string name, SourceKind kind, string origin, int priority,
        bool isEnabled = true, CancellationToken cancellationToken = default)
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
            existing.Priority = priority;
            existing.IsEnabled = isEnabled;
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
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.Sources.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return entity;
    }

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
    /// Regista (ou actualiza) um <see cref="ChannelSourceEntity"/> —
    /// a associação entre um canal canónico e uma stream concreta de
    /// uma source. A URL é sanitizada antes de persistir.
    /// </summary>
    public async Task<ChannelSourceEntity> RecordChannelSourceAsync(
        long canonicalChannelId,
        long sourceId,
        string streamUrl,
        StreamQuality quality = StreamQuality.Unknown,
        EpgState epg = EpgState.Unknown,
        AvailabilityState availability = AvailabilityState.Discovered,
        double matchConfidence = 0,
        string matchMethod = "unknown",
        string? externalStreamId = null,
        bool isEnabled = true,
        CancellationToken cancellationToken = default)
    {
        if (canonicalChannelId <= 0) throw new ArgumentException("CanonicalChannelId inválido.", nameof(canonicalChannelId));
        if (sourceId <= 0) throw new ArgumentException("SourceId inválido.", nameof(sourceId));
        if (string.IsNullOrWhiteSpace(streamUrl)) throw new ArgumentException("StreamUrl é obrigatória.", nameof(streamUrl));
        if (matchMethod.Length > 80) throw new ArgumentException("MatchMethod excede 80 caracteres.", nameof(matchMethod));

        var sanitizedUrl = CredentialSanitizer.SanitizeUrl(streamUrl);
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);

        var existing = await context.ChannelSources
            .FirstOrDefaultAsync(cs => cs.CanonicalChannelId == canonicalChannelId
                                    && cs.SourceId == sourceId
                                    && cs.StreamUrl == sanitizedUrl,
                cancellationToken);

        var now = DateTime.UtcNow;
        if (existing != null)
        {
            existing.Quality = quality;
            existing.Epg = epg;
            existing.Availability = availability;
            existing.MatchConfidence = matchConfidence;
            existing.MatchMethod = matchMethod;
            existing.ExternalStreamId = externalStreamId;
            existing.IsEnabled = isEnabled;
            existing.LastSeenAtUtc = now;
            existing.LastTestedAtUtc = now;
            existing.UpdatedAtUtc = now;
            await context.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var entity = new ChannelSourceEntity
        {
            CanonicalChannelId = canonicalChannelId,
            SourceId = sourceId,
            StreamUrl = sanitizedUrl,
            ExternalStreamId = externalStreamId,
            Quality = quality,
            Epg = epg,
            Availability = availability,
            MatchConfidence = matchConfidence,
            MatchMethod = matchMethod,
            FirstSeenAtUtc = now,
            LastSeenAtUtc = now,
            LastTestedAtUtc = now,
            LastResponseTimeMs = 0,
            IsEnabled = isEnabled,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        context.ChannelSources.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return entity;
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
    public int ReviewItemsApproved { get; set; }
    public int ReviewItemsExcluded { get; set; }
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
    public static CatalogResolution Unknown() => new(
        CatalogResolutionKind.Unknown,
        null, null, null, null, null,
        PublicationPolicy.Excluded, null, null);

    public static CatalogResolution FromCanonical(CanonicalChannelEntity ch) => new(
        CatalogResolutionKind.Canonical,
        ch.Id, ch.Key, ch.DisplayName,
        ch.EditorialCategory, ch.EditorialGroup,
        ch.PublicationPolicy, null, null);

    public static CatalogResolution FromRule(IdentityRuleEntity rule) => new(
        CatalogResolutionKind.Rule,
        null, null, null, null, null,
        rule.Disposition == global::m3uCrawler.Services.Catalog.RuleDisposition.Excluded
            ? PublicationPolicy.Excluded
            : PublicationPolicy.ReviewOnly,
        rule.Disposition, rule.Reason);

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
