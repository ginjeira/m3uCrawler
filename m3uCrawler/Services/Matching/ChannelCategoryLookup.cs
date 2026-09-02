using System.Collections.Generic;

namespace m3uCrawler.Services.Matching
{
    /// <summary>
    /// Editorial category of a television channel.
    ///
    /// Independent of Country (PT/Foreign), ContentType
    /// (Live/VOD/24-7/PPV) and Quality/TechnicalAttributes
    /// (VIP/HEVC/4K/HHD/Mobile/Backup).
    /// </summary>
    public enum Category
    {
        /// <summary>Generalista ou temático sem categoria específica. Catch-all seguro.</summary>
        Live = 0,
        /// <summary>Filmes/séries premium, lifestyle, TVI Reality, etc.</summary>
        Entretenimento,
        /// <summary>Desporto: SPORT TV, DAZN, Eleven, Benfica TV, Canal 11 (PT).</summary>
        Desporto,
        /// <summary>Baby TV, Disney Jr, Cartoon Network, etc.</summary>
        Infantil,
        /// <summary>Discovery, NatGeo, Odisseia, Casa e Cozinha, etc.</summary>
        Documentarios,
    }

    /// <summary>
    /// Resolves the editorial <see cref="Category"/> of a canonical
    /// <c>ChannelIdentity</c>.
    ///
    /// <para>
    /// Contract — see
    /// `.kilo/plans/1788214551330-channel-category-lookup-tdd.md`:
    /// </para>
    ///
    /// <list type="bullet">
    ///   <item>The input is a canonical <c>ChannelIdentity</c>, already
    ///         normalized by <see cref="ChannelNormalizer.Normalize"/>.</item>
    ///   <item>This class does <b>NOT</b> normalize internally — caller
    ///         is responsible for normalization.</item>
    ///   <item>Comparison is case-sensitive
    ///         (<see cref="System.StringComparer.Ordinal"/>).</item>
    ///   <item>No substring heuristics, no regex, no fuzzy matching —
    ///         pure exact-match dictionary lookup.</item>
    ///   <item>Unknown / null / empty / whitespace inputs default to
    ///         <see cref="Category.Live"/>.</item>
    ///   <item>This class only decides <see cref="Category"/> — never
    ///         ContentType, Country, OutputGroup or Quality/Technical-
    ///         Attributes.</item>
    ///   <item>SourceGroup does <b>NOT</b> participate in this lookup.</item>
    /// </list>
    ///
    /// <para>
    /// This is a deliberately simple component. The dictionary below
    /// is the canonical source of truth for editorial category of
    /// each known PT channel. New channels default to
    /// <see cref="Category.Live"/> until they are added.
    /// </para>
    /// </summary>
    public static class ChannelCategoryLookup
    {
        // Explicit exact-match dictionary. Case-sensitive.
        private static readonly Dictionary<string, Category> CategoryByIdentity =
            new(System.StringComparer.Ordinal)
        {
            // ========================= Live (generalistas) =========================
            ["rtp 1"] = Category.Live,
            ["rtp 2"] = Category.Live,
            ["rtp 3"] = Category.Live,
            ["rtp noticias"] = Category.Live,
            ["rtp memoria"] = Category.Live,
            ["rtp madeira"] = Category.Live,
            ["rtp acores"] = Category.Live,
            ["rtp africa"] = Category.Live,
            ["rtp internacional"] = Category.Live,
            ["sic"] = Category.Live,
            ["tvi"] = Category.Live,
            ["tvi 24"] = Category.Live,
            ["cmtv"] = Category.Live,
            ["cnn portugal"] = Category.Live,
            ["euronews portugal"] = Category.Live,

            // ========================= Entretenimento =========================
            ["axn"] = Category.Entretenimento,
            ["axn white"] = Category.Entretenimento,
            ["axn movies"] = Category.Entretenimento,
            ["amc"] = Category.Entretenimento,
            ["amc break"] = Category.Entretenimento,
            ["amc crime"] = Category.Entretenimento,
            ["fox"] = Category.Entretenimento,
            ["fox crime"] = Category.Entretenimento,
            ["fox life"] = Category.Entretenimento,
            ["fox movies"] = Category.Entretenimento,
            ["star channel"] = Category.Entretenimento,
            ["star comedy"] = Category.Entretenimento,
            ["star crime"] = Category.Entretenimento,
            ["star life"] = Category.Entretenimento,
            ["star movies"] = Category.Entretenimento,
            ["hollywood"] = Category.Entretenimento,
            ["nos studios"] = Category.Entretenimento,
            ["syfy"] = Category.Entretenimento,
            ["tvcine action"] = Category.Entretenimento,
            ["tvcine edition"] = Category.Entretenimento,
            ["tvcine emotion"] = Category.Entretenimento,
            ["tvcine top"] = Category.Entretenimento,
            ["tvcine +"] = Category.Entretenimento,
            ["travel channel"] = Category.Entretenimento,
            ["24 kitchen"] = Category.Entretenimento,
            ["vh 1"] = Category.Entretenimento,
            ["tvi internacional"] = Category.Entretenimento,
            ["tvi ficcao"] = Category.Entretenimento,
            ["tvi reality"] = Category.Entretenimento,
            ["sic mulher"] = Category.Entretenimento,
            ["sic radical"] = Category.Entretenimento,
            ["sic k"] = Category.Entretenimento,

            // ========================= Desporto =========================
            ["btv"] = Category.Desporto,
            ["benfica tv"] = Category.Desporto,
            ["canal 11"] = Category.Desporto,
            ["sport tv 1"] = Category.Desporto,
            ["sport tv 2"] = Category.Desporto,
            ["sport tv 3"] = Category.Desporto,
            ["sport tv 4"] = Category.Desporto,
            ["sport tv 5"] = Category.Desporto,
            ["sport tv+"] = Category.Desporto,
            ["sport tv nba"] = Category.Desporto,
            ["sport tv news"] = Category.Desporto,
            ["eleven sports 1"] = Category.Desporto,
            ["eleven sports 2"] = Category.Desporto,
            ["eleven sports 3"] = Category.Desporto,
            ["eleven sports 4"] = Category.Desporto,
            ["eleven sports 5"] = Category.Desporto,
            ["eleven sports 6"] = Category.Desporto,
            ["eurosport"] = Category.Desporto,
            ["eurosport 2"] = Category.Desporto,
            ["a bola tv"] = Category.Desporto,
            ["dazn 1"] = Category.Desporto,
            ["dazn 2"] = Category.Desporto,
            ["dazn 3"] = Category.Desporto,
            ["dazn 4"] = Category.Desporto,
            ["dazn 5"] = Category.Desporto,
            ["dazn 6"] = Category.Desporto,
            ["dazns 2"] = Category.Desporto,

            // ========================= Infantil =========================
            ["baby tv"] = Category.Infantil,
            ["cartoon network"] = Category.Infantil,
            ["disney channel"] = Category.Infantil,
            ["disney junior"] = Category.Infantil,
            ["biggs"] = Category.Infantil,
            ["boomerang"] = Category.Infantil,
            ["canal panda"] = Category.Infantil,
            ["panda kids"] = Category.Infantil,
            ["lolly kids"] = Category.Infantil,

            // ========================= Documentarios =========================
            ["discovery"] = Category.Documentarios,
            ["discovery channel"] = Category.Documentarios,
            ["nat geo"] = Category.Documentarios,
            ["nat geo wild"] = Category.Documentarios,
            ["id"] = Category.Documentarios,
            ["investigation discovery"] = Category.Documentarios,
            ["odisseia"] = Category.Documentarios,
            ["casa e cozinha"] = Category.Documentarios,
        };

        /// <summary>
        /// Returns the editorial <see cref="Category"/> of the
        /// canonical <paramref name="channelIdentity"/>.
        ///
        /// <para>
        /// <b>Input contract</b>: the value must be a canonical
        /// <c>ChannelIdentity</c> produced by
        /// <see cref="ChannelNormalizer.Normalize"/>. If the caller
        /// passes anything else (raw title, mixed case, with quality
        /// tokens, etc.), the result is the fallback
        /// <see cref="Category.Live"/>.
        /// </para>
        ///
        /// <para>
        /// <b>Returns</b>: <see cref="Category.Live"/> for null,
        /// empty, whitespace, or unmapped identities.
        /// </para>
        /// </summary>
        public static Category Lookup(string? channelIdentity)
        {
            if (string.IsNullOrWhiteSpace(channelIdentity))
            {
                return Category.Live;
            }
            return CategoryByIdentity.TryGetValue(channelIdentity, out var cat)
                ? cat
                : Category.Live;
        }
    }
}
