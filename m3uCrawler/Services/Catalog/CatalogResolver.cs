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

        // 2. AffinityMember -> AffinityGroup -> CanonicalChannel.
        var member = await context.AffinityMembers
            .AsNoTracking()
            .Include(m => m.AffinityGroup)
                .ThenInclude(g => g!.CanonicalChannel)
            .FirstOrDefaultAsync(m => m.NormalizedMember == normalizedIdentity, cancellationToken);
        if (member?.AffinityGroup?.CanonicalChannel != null && member.AffinityGroup.CanonicalChannel.IsEnabled)
        {
            return CatalogResolution.FromCanonical(member.AffinityGroup.CanonicalChannel);
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
        CancellationToken cancellationToken = default)
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
                Ownership = ChannelOwnership.Unknown,
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
        long? canonicalChannelId,
        string? countryCode,
        IReadOnlyList<string> normalizedMembers,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name required", nameof(name));
        if (normalizedMembers == null || normalizedMembers.Count == 0)
            throw new ArgumentException("at least one member required", nameof(normalizedMembers));

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);

        if (canonicalChannelId.HasValue)
        {
            var canonical = await context.CanonicalChannels
                .FirstOrDefaultAsync(c => c.Id == canonicalChannelId.Value, cancellationToken);
            if (canonical == null)
                throw new InvalidOperationException($"CanonicalChannel {canonicalChannelId} not found.");
        }

        var now = DateTime.UtcNow;
        var group = new AffinityGroupEntity
        {
            Name = name,
            CountryCode = countryCode,
            CanonicalChannelId = canonicalChannelId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Members = normalizedMembers
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => new AffinityMemberEntity
                {
                    NormalizedMember = m.Trim(),
                    CreatedAtUtc = now,
                }).ToList(),
        };

        context.AffinityGroups.Add(group);
        await context.SaveChangesAsync(cancellationToken);
        return group;
    }

    public async Task<AffinityGroupEntity?> UpdateAffinityGroupAsync(
        long groupId,
        string name,
        long? canonicalChannelId,
        string? countryCode,
        IReadOnlyList<string> normalizedMembers,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name required", nameof(name));

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);

        var group = await context.AffinityGroups
            .Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        if (group == null) return null;

        if (canonicalChannelId.HasValue)
        {
            var canonical = await context.CanonicalChannels
                .FirstOrDefaultAsync(c => c.Id == canonicalChannelId.Value, cancellationToken);
            if (canonical == null)
                throw new InvalidOperationException($"CanonicalChannel {canonicalChannelId} not found.");
        }

        group.Name = name;
        group.CanonicalChannelId = canonicalChannelId;
        group.CountryCode = countryCode;
        group.UpdatedAtUtc = DateTime.UtcNow;

        context.AffinityMembers.RemoveRange(group.Members);
        group.Members.Clear();

        var now = DateTime.UtcNow;
        foreach (var m in normalizedMembers.Where(m => !string.IsNullOrWhiteSpace(m)))
        {
            group.Members.Add(new AffinityMemberEntity
            {
                NormalizedMember = m.Trim(),
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
            DbPath = _dbPath,
            GeneratedAtUtc = now,
        };
    }
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
