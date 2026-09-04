using System;
using System.Collections.Generic;

namespace m3uCrawler.Services.Catalog;

/// <summary>
/// Catálogo persistente de canais canónicos. Substitui o uso
/// editorial de <c>ChannelCategoryLookup</c> como autorização de
/// criação. Ver
/// <c>docs/architecture/channel-catalog-and-ownership.md</c>.
/// </summary>
public sealed class CanonicalChannelEntity
{
    public long Id { get; set; }

    /// <summary>
    /// Identificador estável único, slug-friendly, imutável após a
    /// criação. Exemplo: "benfica-tv", "sport-tv-1". Usado como
    /// chave estável em logs, ficheiros de seed e URLs internas.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Nome editorial a apresentar (em PT-PT). Exemplo:
    /// "Benfica TV", "Sport TV 1".
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Categoria editorial (Live, Desporto, etc.).
    /// </summary>
    public EditorialCategory EditorialCategory { get; set; }

    /// <summary>
    /// Grupo final de publicação. Mantido como enum estável
    /// (CanonicalEditorialGroup) — ver <c>CatalogEnums.cs</c>.
    /// </summary>
    public CanonicalEditorialGroup EditorialGroup { get; set; }

    /// <summary>
    /// Política de publicação. Só <see cref="PublicationPolicy.CreateEligible"/>
    /// permite criar canais. As outras três políticas apenas
    /// protegem canais/streams existentes.
    /// </summary>
    public PublicationPolicy PublicationPolicy { get; set; }

    /// <summary>
    /// Quando <c>false</c> o matcher ignora completamente este
    /// canal (mesmo que o alias bata certo). Usado para
    /// descontinuações temporárias sem apagar o histórico.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public List<ChannelAliasEntity> Aliases { get; set; } = new();
}

/// <summary>
/// Alias normalizado de uma identidade para um canal canónico. A
/// <c>NormalizedAlias</c> deve estar na mesma forma canónica que o
/// matcher produz (lower-case, espaços em vez de hífens, tokens
/// como "PT"/"VIP" removidos via
/// <c>ChannelNormalizer.Normalize</c>). Ver
/// <see cref="m3uCrawler.Services.Matching.ChannelNormalizer"/>.
/// </summary>
public sealed class ChannelAliasEntity
{
    public long Id { get; set; }

    /// <summary>
    /// Forma canónica da identidade (e.g. "btv hevc pt", "benficatv",
    /// "benfica tv"). Único. Comparação case-sensitive ordinal.
    /// </summary>
    public string NormalizedAlias { get; set; } = string.Empty;

    public long CanonicalChannelId { get; set; }
    public CanonicalChannelEntity? CanonicalChannel { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// Regra explícita de identidade que NÃO resolve para um canal
/// publicável (e.g. "Sport TV NBA" → review, não criar). Usado
/// para títulos de bundle/canal de PPV/evento que não devem gerar
/// NewChannel.
/// </summary>
public sealed class IdentityRuleEntity
{
    public long Id { get; set; }

    /// <summary>
    /// Identidade canónica (mesma forma que o matcher produz).
    /// </summary>
    public string NormalizedIdentity { get; set; } = string.Empty;

    public RuleDisposition Disposition { get; set; }

    /// <summary>
    /// Razão textual, sanitizada (sem URLs nem credenciais).
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum RuleDisposition
{
    ReviewOnly = 0,
    Excluded = 1,
}

/// <summary>
/// Ownership de um canal do Dispatcharr: se o canal foi criado
/// pelo crawler, se é externo, ou se é desconhecido (bootstrap).
/// </summary>
public enum ChannelOwnership
{
    Unknown = 0,
    CrawlerManaged = 1,
    External = 2,
}

/// <summary>
/// Ownership de uma stream individual dentro de um canal. Aplicado
/// a cada stream do Dispatcharr para impedir que streams
/// externas/desconhecidas sejam removidas por ausência na playlist
/// do crawler.
/// </summary>
public enum StreamOwnership
{
    Unknown = 0,
    CrawlerManaged = 1,
    External = 2,
}

public sealed class DispatcharrChannelOwnershipEntity
{
    public long Id { get; set; }

    /// <summary>ID do canal no Dispatcharr. Único.</summary>
    public long DispatcharrChannelId { get; set; }

    public ChannelOwnership Ownership { get; set; } = ChannelOwnership.Unknown;

    public long? CanonicalChannelId { get; set; }
    public CanonicalChannelEntity? CanonicalChannel { get; set; }

    public DateTime FirstObservedAtUtc { get; set; }
    public DateTime LastObservedAtUtc { get; set; }

    /// <summary>
    /// Texto curto, sanitizado. Pode incluir o nome ou o
    /// source group, mas nunca URLs nem credenciais.
    /// </summary>
    public string Evidence { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class DispatcharrStreamOwnershipEntity
{
    public long Id { get; set; }

    public long DispatcharrStreamId { get; set; }
    public long DispatcharrChannelId { get; set; }

    public StreamOwnership Ownership { get; set; } = StreamOwnership.Unknown;

    /// <summary>
    /// ID do SyncRun que criou a stream (quando aplicável).
    /// Null para streams externas/desconhecidas observadas em
    /// bootstrap.
    /// </summary>
    public long? CreatedBySyncRunId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// Item para revisão humana. Gerado quando o matcher não
/// consegue decidir automaticamente (ex.: "PT: SPORT TV NBA"). Estados:
/// <c>Open</c> (a aguardar decisão), <c>Approved</c> (aprovado por
/// humano), <c>Excluded</c> (excluído por humano). O fingerprint
/// é determinístico e baseado em
/// <c>(normalizedIdentity, sourceGroup, reasonSignature)</c> para
/// evitar duplicados.
/// </summary>
public sealed class ReviewItemEntity
{
    public long Id { get; set; }

    /// <summary>
    /// SHA-256 hex de
    /// <c>"{normalizedIdentity}|{sourceGroup}|{reasonSignature}"</c>.
    /// Único. 64 chars.
    /// </summary>
    public string Fingerprint { get; set; } = string.Empty;

    public string NormalizedIdentity { get; set; } = string.Empty;
    public string SourceGroup { get; set; } = string.Empty;
    public string ReasonSignature { get; set; } = string.Empty;

    public ReviewItemState State { get; set; } = ReviewItemState.Open;

    /// <summary>
    /// Após decisão humana, referência opcional ao canal canónico
    /// aprovado.
    /// </summary>
    public long? ApprovedCanonicalChannelId { get; set; }
    public CanonicalChannelEntity? ApprovedCanonicalChannel { get; set; }

    /// <summary>
    /// Texto opcional, sanitizado (sem URLs nem credenciais).
    /// </summary>
    public string Note { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
}

public enum ReviewItemState
{
    Open = 0,
    Approved = 1,
    Excluded = 2,
}

/// <summary>
/// Registo de uma execução de sincronização Telegram. Sem URLs,
/// credenciais ou tokens — apenas contadores agregados.
/// </summary>
public sealed class SyncRunEntity
{
    public long Id { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }

    /// <summary>Versão da aplicação (commit SHA curto).</summary>
    public string AppVersion { get; set; } = string.Empty;

    // Contadores separados (brief ponto 6 do contract).
    public int CountCreatedCrawlerManaged { get; set; }
    public int CountMergedIntoExternal { get; set; }
    public int CountProtectedExternalStreams { get; set; }
    public int CountRemovedCrawlerManagedStreams { get; set; }
    public int CountReviewRequired { get; set; }
    public int CountExcluded { get; set; }

    /// <summary>
    /// "ok", "cancelled", "error: …". Texto sanitizado.
    /// </summary>
    public string Result { get; set; } = string.Empty;
}
