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

    public CatalogResolver(IDbContextFactory<ChannelCatalogDbContext> factory)
    {
        _factory = factory;
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

        // 1. IdentityRule (priority over ChannelAlias).
        var rule = await context.IdentityRules
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.NormalizedIdentity == normalizedIdentity, cancellationToken);
        if (rule != null)
        {
            return CatalogResolution.FromRule(rule);
        }

        // 2. ChannelAlias -> CanonicalChannel.
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
