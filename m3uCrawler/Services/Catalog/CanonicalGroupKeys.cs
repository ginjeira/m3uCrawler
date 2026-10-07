namespace m3uCrawler.Services.Catalog;

/// <summary>
/// Chaves estáveis dos grupos canónicos por omissão (<see cref="CanonicalGroupEntity.Key"/>).
///
/// <para>
/// Substituem o enum legado <c>CanonicalEditorialGroup</c> (Wave D2): o grupo
/// canónico é agora identificado apenas pela <c>Key</c> do
/// <see cref="CanonicalGroupEntity"/> e pela FK <c>GroupId</c> em
/// <see cref="CanonicalChannelEntity"/>. Os valores coincidem com as chaves
/// semeadas pela migration <c>AddCanonicalChannelGroupFk</c>.
/// </para>
/// </summary>
public static class CanonicalGroupKeys
{
    public const string PortugalGeneralistas = "pt-generalistas";
    public const string PortugalFilmesSeries = "pt-filmes-series";
    public const string PortugalEntretenimento = "pt-entretenimento";
    public const string PortugalDesporto = "pt-desporto";
    public const string PortugalInfantil = "pt-infantil";
    public const string PortugalDocumentarios = "pt-documentarios";
    public const string PortugalPPV = "pt-ppv";
    public const string International = "international";
    public const string Other = "other";
}
