using System.Collections.Generic;
using System.Linq;

namespace m3uCrawler.Services.Catalog;

/// <summary>
/// Seed versionado, legível e testável do catálogo. Não há valores
/// escondidos no código: a tabela abaixo é a fonte de verdade do
/// que é inserido na primeira migration.
///
/// <para>
/// Conteúdo organizado em três blocos:
/// </para>
/// <list type="bullet">
///   <item><b>Benfica TV</b>: identidade canónica com aliases
///         explícitos (incluindo BTV HEVC PT, BENFICATV, etc.) e
///         política CreateEligible.</item>
///   <item><b>Sport TV NBA</b>: canal autónomo
///         (<c>sport-tv-nba</c>, <c>CreateEligible</c>), distinto
///         de Sport TV 1..7. Os aliases cobrem as variantes
///         <c>SPORT TV NBA</c>, <c>PT: SPORT TV NBA</c>,
///         <c>PT SPORT TV NBA</c> e <c>SPORT TV NBA HEVC PT</c> na
///         forma canónica (lowercase, espaços). Nunca faz fuzzy
///         para Sport TV 1..7 (token-set ratio 67 &lt; threshold
///         80).</item>
///   <item><b>Aliases canónicos legados</b>: SIC, RTP, CMTV, TVI
///         e restantes identidades que já existiam no
///         <c>ChannelCategoryLookup</c> curado, com a mesma
///         categoria editorial mas com a política
///         <see cref="PublicationPolicy.CreateEligible"/> (são
///         canais publicáveis). Não há promoção implícita de
///         novas entradas; o matcher lê a BD, não o dicionário
///         antigo, para decidir <c>NewChannel</c>.</item>
/// </list>
///
/// <para>
/// Todos os alias são fornecidos já na forma canónica que o
/// <c>ChannelNormalizer</c> produz (lowercase, espaços em vez de
/// hífens, tokens como "PT"/"VIP"/"HEVC"/"FHD"/"HD" removidos).
/// </para>
/// </summary>
public static class CatalogSeed
{
    public static readonly IReadOnlyList<CanonicalChannelSeed> Channels = new[]
    {
        // ========================= Benfica TV =========================
        new CanonicalChannelSeed(
            Key: "benfica-tv",
            DisplayName: "Benfica TV",
            Category: EditorialCategory.Desporto,
            Group: CanonicalEditorialGroup.PortugalDesporto,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[]
            {
                "btv",
                "btv hevc pt",
                "benficatv",
                "benfica tv",
                "pt benfica tv",
                "pt  benfica tv",
            }),

        // ========================= Sport TV NBA =========================
        // Canal autónomo, distinto de Sport TV 1..7. Os aliases estão
        // na forma canónica (lowercase, espaços) que o
        // ChannelNormalizer produz a partir dos títulos raw.
        new CanonicalChannelSeed(
            Key: "sport-tv-nba",
            DisplayName: "Sport TV NBA",
            Category: EditorialCategory.Desporto,
            Group: CanonicalEditorialGroup.PortugalDesporto,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[]
            {
                "sport tv nba",
                "pt sport tv nba",
                "sport tv nba hevc pt",
            }),

        // ========================= Sport TV 1..7 =========================
        new CanonicalChannelSeed(
            Key: "sport-tv-1",
            DisplayName: "Sport TV 1",
            Category: EditorialCategory.Desporto,
            Group: CanonicalEditorialGroup.PortugalDesporto,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[]
            {
                "sport tv 1",
                "sport tv 1 fhd",
            }),
        new CanonicalChannelSeed(
            Key: "sport-tv-2",
            DisplayName: "Sport TV 2",
            Category: EditorialCategory.Desporto,
            Group: CanonicalEditorialGroup.PortugalDesporto,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[]
            {
                "sport tv 2",
            }),
        new CanonicalChannelSeed(
            Key: "sport-tv-3",
            DisplayName: "Sport TV 3",
            Category: EditorialCategory.Desporto,
            Group: CanonicalEditorialGroup.PortugalDesporto,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[]
            {
                "sport tv 3",
            }),
        new CanonicalChannelSeed(
            Key: "sport-tv-4",
            DisplayName: "Sport TV 4",
            Category: EditorialCategory.Desporto,
            Group: CanonicalEditorialGroup.PortugalDesporto,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[]
            {
                "sport tv 4",
            }),
        new CanonicalChannelSeed(
            Key: "sport-tv-5",
            DisplayName: "Sport TV 5",
            Category: EditorialCategory.Desporto,
            Group: CanonicalEditorialGroup.PortugalDesporto,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[]
            {
                "sport tv 5",
            }),
        new CanonicalChannelSeed(
            Key: "sport-tv-6",
            DisplayName: "Sport TV 6",
            Category: EditorialCategory.Desporto,
            Group: CanonicalEditorialGroup.PortugalDesporto,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[]
            {
                "sport tv 6",
                "sporttv 6",
            }),
        new CanonicalChannelSeed(
            Key: "sport-tv-news",
            DisplayName: "Sport TV News",
            Category: EditorialCategory.Desporto,
            Group: CanonicalEditorialGroup.PortugalDesporto,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[]
            {
                "sport tv news",
            }),

        // ========================= Live (generalistas) =========================
        new CanonicalChannelSeed(
            Key: "rtp-1",
            DisplayName: "RTP 1",
            Category: EditorialCategory.Live,
            Group: CanonicalEditorialGroup.PortugalLive,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "rtp 1" }),
        new CanonicalChannelSeed(
            Key: "rtp-2",
            DisplayName: "RTP 2",
            Category: EditorialCategory.Live,
            Group: CanonicalEditorialGroup.PortugalLive,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "rtp 2" }),
        new CanonicalChannelSeed(
            Key: "rtp-3",
            DisplayName: "RTP 3",
            Category: EditorialCategory.Live,
            Group: CanonicalEditorialGroup.PortugalLive,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "rtp 3" }),
        new CanonicalChannelSeed(
            Key: "rtp-noticias",
            DisplayName: "RTP Notícias",
            Category: EditorialCategory.Live,
            Group: CanonicalEditorialGroup.PortugalLive,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "rtp noticias" }),
        new CanonicalChannelSeed(
            Key: "sic",
            DisplayName: "SIC",
            Category: EditorialCategory.Live,
            Group: CanonicalEditorialGroup.PortugalLive,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "sic" }),
        new CanonicalChannelSeed(
            Key: "tvi",
            DisplayName: "TVI",
            Category: EditorialCategory.Live,
            Group: CanonicalEditorialGroup.PortugalLive,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "tvi" }),
        new CanonicalChannelSeed(
            Key: "tvi-24",
            DisplayName: "TVI 24",
            Category: EditorialCategory.Live,
            Group: CanonicalEditorialGroup.PortugalLive,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "tvi 24" }),
        new CanonicalChannelSeed(
            Key: "cmtv",
            DisplayName: "CMTV",
            Category: EditorialCategory.Live,
            Group: CanonicalEditorialGroup.PortugalLive,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "cmtv", "cm tv" }),
        new CanonicalChannelSeed(
            Key: "cnn",
            DisplayName: "CNN",
            Category: EditorialCategory.Live,
            Group: CanonicalEditorialGroup.PortugalLive,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "cnn", "cnn portugal" }),
        new CanonicalChannelSeed(
            Key: "euronews",
            DisplayName: "Euronews",
            Category: EditorialCategory.Live,
            Group: CanonicalEditorialGroup.PortugalLive,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "euronews", "euronews portugal" }),

        // ========================= Entretenimento =========================
        new CanonicalChannelSeed(
            Key: "axn",
            DisplayName: "AXN",
            Category: EditorialCategory.Entretenimento,
            Group: CanonicalEditorialGroup.PortugalEntretenimento,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "axn" }),
        new CanonicalChannelSeed(
            Key: "axn-white",
            DisplayName: "AXN White",
            Category: EditorialCategory.Entretenimento,
            Group: CanonicalEditorialGroup.PortugalEntretenimento,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "axn white" }),
        new CanonicalChannelSeed(
            Key: "amc",
            DisplayName: "AMC",
            Category: EditorialCategory.Entretenimento,
            Group: CanonicalEditorialGroup.PortugalEntretenimento,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "amc" }),
        new CanonicalChannelSeed(
            Key: "fox",
            DisplayName: "FOX",
            Category: EditorialCategory.Entretenimento,
            Group: CanonicalEditorialGroup.PortugalEntretenimento,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "fox" }),
        new CanonicalChannelSeed(
            Key: "tv-cine",
            DisplayName: "TV Cine",
            Category: EditorialCategory.Entretenimento,
            Group: CanonicalEditorialGroup.PortugalEntretenimento,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[]
            {
                "tv cine",
                "tvcine action",
                "tvcine edition",
                "tvcine emotion",
                "tvcine top",
                "tvcine +",
                "tv cine action",
                "tv cine edition",
                "tv cine emotion",
                "tv cine top",
                "tv cine +",
            }),
        new CanonicalChannelSeed(
            Key: "travel-channel",
            DisplayName: "Travel Channel",
            Category: EditorialCategory.Entretenimento,
            Group: CanonicalEditorialGroup.PortugalEntretenimento,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "travel channel" }),
        new CanonicalChannelSeed(
            Key: "tvi-internacional",
            DisplayName: "TVI Internacional",
            Category: EditorialCategory.Entretenimento,
            Group: CanonicalEditorialGroup.PortugalEntretenimento,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "tvi internacional" }),
        new CanonicalChannelSeed(
            Key: "sic-mulher",
            DisplayName: "SIC Mulher",
            Category: EditorialCategory.Entretenimento,
            Group: CanonicalEditorialGroup.PortugalEntretenimento,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "sic mulher" }),
        new CanonicalChannelSeed(
            Key: "sic-radical",
            DisplayName: "SIC Radical",
            Category: EditorialCategory.Entretenimento,
            Group: CanonicalEditorialGroup.PortugalEntretenimento,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "sic radical" }),

        // ========================= Infantil =========================
        new CanonicalChannelSeed(
            Key: "baby-tv",
            DisplayName: "Baby TV",
            Category: EditorialCategory.Infantil,
            Group: CanonicalEditorialGroup.PortugalInfantil,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "baby tv" }),
        new CanonicalChannelSeed(
            Key: "cartoon-network",
            DisplayName: "Cartoon Network",
            Category: EditorialCategory.Infantil,
            Group: CanonicalEditorialGroup.PortugalInfantil,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "cartoon network" }),
        new CanonicalChannelSeed(
            Key: "disney-channel",
            DisplayName: "Disney Channel",
            Category: EditorialCategory.Infantil,
            Group: CanonicalEditorialGroup.PortugalInfantil,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "disney channel" }),
        new CanonicalChannelSeed(
            Key: "canal-panda",
            DisplayName: "Canal Panda",
            Category: EditorialCategory.Infantil,
            Group: CanonicalEditorialGroup.PortugalInfantil,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "canal panda", "panda kids" }),

        // ========================= Documentários =========================
        new CanonicalChannelSeed(
            Key: "discovery",
            DisplayName: "Discovery",
            Category: EditorialCategory.Documentarios,
            Group: CanonicalEditorialGroup.PortugalDocumentarios,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "discovery", "discovery channel" }),
        new CanonicalChannelSeed(
            Key: "nat-geo",
            DisplayName: "National Geographic",
            Category: EditorialCategory.Documentarios,
            Group: CanonicalEditorialGroup.PortugalDocumentarios,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "nat geo", "nat geo wild" }),
        new CanonicalChannelSeed(
            Key: "odisseia",
            DisplayName: "Odisseia",
            Category: EditorialCategory.Documentarios,
            Group: CanonicalEditorialGroup.PortugalDocumentarios,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "odisseia" }),

        // ========================= PPV / Eventos =========================
        new CanonicalChannelSeed(
            Key: "canal-11",
            DisplayName: "Canal 11",
            Category: EditorialCategory.Desporto,
            Group: CanonicalEditorialGroup.PortugalDesporto,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "canal 11" }),
    };

    public static readonly IReadOnlyList<IdentityRuleSeed> IdentityRules = Array.Empty<IdentityRuleSeed>();

    /// <summary>
    /// Garante que cada alias só aparece num canal canónico (sem
    /// sobreposições). Devolve exception se encontrar sobreposição
    /// — protege contra typos durante a edição deste seed.
    /// </summary>
    public static void ValidateSeedConsistency()
    {
        var byAlias = new Dictionary<string, CanonicalChannelSeed>(System.StringComparer.Ordinal);
        foreach (var ch in Channels)
        {
            foreach (var alias in ch.Aliases)
            {
                if (byAlias.TryGetValue(alias, out var prev))
                {
                    throw new System.InvalidOperationException(
                        $"Seed inconsistency: alias '{alias}' appears in both " +
                        $"'{prev.Key}' and '{ch.Key}'.");
                }
                byAlias[alias] = ch;
            }
        }
        foreach (var rule in IdentityRules)
        {
            // IdentityRules devem ser identidades que NÃO estão
            // mapeadas para um canal publicável.
            if (byAlias.ContainsKey(rule.NormalizedIdentity))
            {
                throw new System.InvalidOperationException(
                    $"Seed inconsistency: identity rule '{rule.NormalizedIdentity}' " +
                    $"is also a known alias of a canonical channel.");
            }
        }
    }
}

public sealed record CanonicalChannelSeed(
    string Key,
    string DisplayName,
    EditorialCategory Category,
    CanonicalEditorialGroup Group,
    PublicationPolicy Policy,
    IReadOnlyList<string> Aliases);

public sealed record IdentityRuleSeed(
    string NormalizedIdentity,
    RuleDisposition Disposition,
    string Reason);
