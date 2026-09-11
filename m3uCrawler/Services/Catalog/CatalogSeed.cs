using System.Collections.Generic;
using System.Linq;

namespace m3uCrawler.Services.Catalog;

/// <summary>
/// Seed versionado, legível e testável do catálogo. Não há valores
/// escondidos no código: a tabela abaixo é a fonte de verdade do
/// que é inserido na primeira migration.
/// <para>
/// <b>R2 (PHASE-Bridge / Catalog Consistency — 2026-09-11):</b>
/// Este seed foi saneado para não duplicar a baseline canónica
/// definida em <c>docs/catalog/m3ucrawler_pt_canonical_catalog.json</c>.
/// Regra arquitectural: UM canal lógico = UM CanonicalChannel.
/// A baseline JSON é a fonte primária; este seed apenas adiciona
/// canais que <b>NÃO</b> estão na baseline e que precisam de estar
/// disponíveis antes de o JSON ser carregado (fallback) ou que
/// precisam de aliases mais ricos do que a baseline.
/// </para>
/// <para>
/// Os canais removidos deste seed são os que têm a sua identidade
/// totalmente coberta pela baseline JSON (RTP 1-3, SIC, TVI, SIC
/// Notícias, CNN Portugal, CMTV, News Now, Canal 11, V+ TVI, RTP
/// Memória, Porto Canal, Sport TV 1, Eurosport 1, BTV, SIC K,
/// Panda, Cartoon Network, Hollywood, Cinemundo, AXN, Discovery,
/// NatGeo, Globo).
/// </para>
/// <para>
/// Canais que permanecem (não cobertos pela baseline):
/// <list type="bullet">
///   <item>Sport TV 2-7 (apenas Sport TV 1 está na baseline)</item>
///   <item>Sport TV NBA (autónomo, distinto de Sport TV 1-7)</item>
///   <item>TVI 24, TVI Internacional, SIC Mulher, SIC Radical</item>
///   <item>Baby TV, Odisseia (Canal Panda mantém-se com a key
///         <c>canal-panda</c> porque a baseline usa <c>panda</c>
///         para um canal diferente no agrupamento infantil)</item>
/// </list>
/// </para>
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
        // ========================= Benfica TV (BTV) =========================
        // Mantido como fallback com aliases históricos. A baseline JSON
        // também cria "btv" via `pt.btv`, mas em BDs legadas o seed é
        // a única fonte; mantemos para resiliência.
        new CanonicalChannelSeed(
            Key: "btv",
            DisplayName: "BTV",
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
        // Canal autónomo, distinto de Sport TV 1..7.
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

        // ========================= Sport TV 2-7 =========================
        // Apenas Sport TV 1 está na baseline. Os restantes ficam aqui
        // com aliases ricas.
        new CanonicalChannelSeed(
            Key: "sport-tv-2",
            DisplayName: "Sport TV 2",
            Category: EditorialCategory.Desporto,
            Group: CanonicalEditorialGroup.PortugalDesporto,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[]
            {
                "sport tv 2",
                "sport tv 2 hd",
                "sport tv 2 fhd",
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
                "sport tv 3 hd",
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
                "sport tv 4 hd",
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
                "sport tv 5 hd",
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
                "sport tv 6 hd",
            }),
        new CanonicalChannelSeed(
            Key: "sport-tv-7",
            DisplayName: "Sport TV 7",
            Category: EditorialCategory.Desporto,
            Group: CanonicalEditorialGroup.PortugalDesporto,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[]
            {
                "sport tv 7",
                "sport tv 7 hd",
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

        // ========================= Live adicional =========================
        // TVI 24, Euronews Portugal, CNN (caso a baseline não tenha sido
        // carregada).
        new CanonicalChannelSeed(
            Key: "tvi-24",
            DisplayName: "TVI 24",
            Category: EditorialCategory.Live,
            Group: CanonicalEditorialGroup.PortugalLive,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[]
            {
                "tvi 24",
                "tvi24",
            }),
        new CanonicalChannelSeed(
            Key: "euronews",
            DisplayName: "Euronews",
            Category: EditorialCategory.Live,
            Group: CanonicalEditorialGroup.PortugalLive,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[]
            {
                "euronews",
                "euronews portugal",
            }),

        // ========================= Entretenimento =========================
        new CanonicalChannelSeed(
            Key: "axn-white",
            DisplayName: "AXN White",
            Category: EditorialCategory.Entretenimento,
            Group: CanonicalEditorialGroup.PortugalEntretenimento,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "axn white", "axn white hd" }),
        new CanonicalChannelSeed(
            Key: "amc",
            DisplayName: "AMC",
            Category: EditorialCategory.Entretenimento,
            Group: CanonicalEditorialGroup.PortugalEntretenimento,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "amc", "amc hd" }),
        new CanonicalChannelSeed(
            Key: "fox",
            DisplayName: "FOX",
            Category: EditorialCategory.Entretenimento,
            Group: CanonicalEditorialGroup.PortugalEntretenimento,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "fox", "fox hd", "fox life", "fox crime" }),
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
            Aliases: new[] { "travel channel", "travel channel hd" }),
        new CanonicalChannelSeed(
            Key: "tvi-internacional",
            DisplayName: "TVI Internacional",
            Category: EditorialCategory.Entretenimento,
            Group: CanonicalEditorialGroup.PortugalEntretenimento,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[]
            {
                "tvi internacional",
                "tvi internacional hd",
            }),
        new CanonicalChannelSeed(
            Key: "sic-mulher",
            DisplayName: "SIC Mulher",
            Category: EditorialCategory.Entretenimento,
            Group: CanonicalEditorialGroup.PortugalEntretenimento,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "sic mulher", "sic mulher hd" }),
        new CanonicalChannelSeed(
            Key: "sic-radical",
            DisplayName: "SIC Radical",
            Category: EditorialCategory.Entretenimento,
            Group: CanonicalEditorialGroup.PortugalEntretenimento,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "sic radical", "sic radical hd" }),

        // ========================= Infantil =========================
        // Canal Panda permanece como "canal-panda" (key legada) porque a
        // baseline usa "panda" para um canal diferente. Em BDs legadas
        // esta key está estabelecida.
        new CanonicalChannelSeed(
            Key: "canal-panda",
            DisplayName: "Canal Panda",
            Category: EditorialCategory.Infantil,
            Group: CanonicalEditorialGroup.PortugalInfantil,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[]
            {
                "canal panda",
                "panda kids",
                "panda",
            }),
        new CanonicalChannelSeed(
            Key: "baby-tv",
            DisplayName: "Baby TV",
            Category: EditorialCategory.Infantil,
            Group: CanonicalEditorialGroup.PortugalInfantil,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "baby tv", "babytv" }),

        // ========================= Documentários =========================
        new CanonicalChannelSeed(
            Key: "odisseia",
            DisplayName: "Odisseia",
            Category: EditorialCategory.Documentarios,
            Group: CanonicalEditorialGroup.PortugalDocumentarios,
            Policy: PublicationPolicy.CreateEligible,
            Aliases: new[] { "odisseia", "canal odisseia" }),
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
