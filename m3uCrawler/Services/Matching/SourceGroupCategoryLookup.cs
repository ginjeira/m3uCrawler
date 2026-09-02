using System.Collections.Generic;

namespace m3uCrawler.Services.Matching
{
    /// <summary>
    /// Resolves the editorial <see cref="Category"/> implied by a
    /// verbatim SourceGroup (provider's group-title).
    ///
    /// <para>
    /// Input contract: caller is responsible for normalizing the
    /// SourceGroup via <see cref="GroupNormalizer.Normalize"/> BEFORE
    /// invoking this lookup. This method does NOT normalize internally.
    /// </para>
    ///
    /// <para>
    /// Returns <c>null</c> when:
    /// </para>
    /// <list type="bullet">
    ///   <item>input is null/empty/whitespace;</item>
    ///   <item>SourceGroup is not in the curated map (decorative,
    ///         technical, foreign, codec, quality, content-type,
    ///         unknown);</item>
    ///   <item>SourceGroup carries no editorial category information.</item>
    /// </list>
    ///
    /// <para>
    /// Returns a <see cref="Category"/> when the SourceGroup is one of
    /// the editorial categories (Live, Entretenimento, Desporto,
    /// Infantil, Documentarios).
    /// </para>
    ///
    /// <para>
    /// Architectural boundary: this class NEVER decides ContentType,
    /// Country, OutputGroup, or Quality/TechnicalAttributes. The
    /// nullable return reflects "absence of editorial information";
    /// the future <c>ResolutionPolicy</c> combines the result with
    /// <see cref="ChannelCategoryLookup"/>, <see cref="ContentTypeDetector"/>,
    /// and <c>CountryChannelValidator</c>.
    /// </para>
    ///
    /// <para>
    /// See `.kilo/plans/1788214551330-source-group-category-lookup-tdd.md`
    /// for the rationale.
    /// </para>
    /// </summary>
    public static class SourceGroupCategoryLookup
    {
        // Curated exact-match dictionary. Case-sensitive.
        // Only editorial categories that the SourceGroup can
        // explicitly communicate by name. Technical/decorative/
        // foreign/codec/quality/content-type SourceGroups are
        // intentionally absent so they return null.
        private static readonly Dictionary<string, Category> CategoryBySourceGroup =
            new(System.StringComparer.Ordinal)
        {
            // ========================= Live =========================
            ["eu | pt | general"] = Category.Live,
            ["portuguese"] = Category.Live,
            ["portugal"] = Category.Live,

            // ========================= Entretenimento =========================
            ["eu | pt | entretenimento"] = Category.Entretenimento,
            // Filmes e series: decisao documentada (fundido em
            // Entretenimento pela especificacao).
            ["eu | pt | filmes e series"] = Category.Entretenimento,

            // ========================= Desporto =========================
            ["eu | pt | esportes"] = Category.Desporto,
            ["sports networks"] = Category.Desporto,

            // ========================= Infantil =========================
            ["eu | pt | infantil"] = Category.Infantil,

            // ========================= Documentarios =========================
            ["eu | pt | documentarios"] = Category.Documentarios,
        };

        /// <summary>
        /// Returns the editorial <see cref="Category"/> implied by the
        /// SourceGroup, or <c>null</c> if the SourceGroup is not a
        /// recognized editorial category (decorative, technical,
        /// foreign, codec, quality, content-type, or unknown).
        ///
        /// <para>
        /// <b>Input contract</b>: the value must already be normalized
        /// via <see cref="GroupNormalizer.Normalize"/>. If the caller
        /// passes raw input (uppercase, whitespace, NBSP, etc.), the
        /// result is <c>null</c>.
        /// </para>
        /// </summary>
        public static Category? Lookup(string? sourceGroup)
        {
            if (string.IsNullOrWhiteSpace(sourceGroup))
            {
                return null;
            }
            return CategoryBySourceGroup.TryGetValue(sourceGroup, out var cat)
                ? cat
                : (Category?)null;
        }
    }
}
