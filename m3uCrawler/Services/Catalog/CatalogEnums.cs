namespace m3uCrawler.Services.Catalog;

/// <summary>
/// Editorial category of a television channel. Independent of
/// country, content type and quality. Mirrors the legacy
/// <c>ChannelCategoryLookup.Category</c> enum so the catalogue
/// and the legacy lookup can be cross-validated during
/// transition.
/// </summary>
public enum EditorialCategory
{
    Live = 0,
    Entretenimento,
    Desporto,
    Infantil,
    Documentarios,
}

/// <summary>
/// Editorial group final de publicação do canal. Por exemplo,
/// "PORTUGAL", "EU | PT | GENERAL", "Sport TV Channels". Não confundir
/// com <c>OutputGroupKind</c> (que é derivado em runtime a partir do
/// source group pelo ResolutionPolicy).
/// </summary>
public enum CanonicalEditorialGroup
{
    PortugalLive = 0,
    PortugalFilmes24_7,
    PortugalEntretenimento,
    PortugalDesporto,
    PortugalInfantil,
    PortugalDocumentarios,
    PortugalPPV,
    Foreign,
    Other,
}

/// <summary>
/// Política de publicação do canal canónico. Determina se o matcher
/// pode criar o canal automaticamente no Dispatcharr, se só pode
/// anexar streams em modo merge-only, se deve ficar bloqueado para
/// revisão, ou se deve ser excluído por completo.
/// </summary>
public enum PublicationPolicy
{
    /// <summary>
    /// Identidade curada. O matcher pode criar um canal novo
    /// automaticamente no Dispatcharr e anexar streams a ele
    /// (incluindo criar streams novas). Esta é a única política
    /// que permite NewChannel.
    /// </summary>
    CreateEligible = 0,

    /// <summary>
    /// O canal só pode receber streams novas em modo merge-only.
    /// Nunca é criado pelo crawler, mas se já existir no
    /// Dispatcharr o crawler pode anexar streams novas a ele.
    /// </summary>
    MergeOnly,

    /// <summary>
    /// Streams cuja identidade canónica corresponde a este canal
    /// devem ser marcadas para revisão humana e não podem ser
    /// anexadas nem criar canais.
    /// </summary>
    ReviewOnly,

    /// <summary>
    /// Streams cuja identidade canónica corresponde a este canal
    /// devem ser completamente excluídas da pipeline.
    /// </summary>
    Excluded,
}
