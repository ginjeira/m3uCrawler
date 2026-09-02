using System.Globalization;
using System.Text.RegularExpressions;

namespace m3uCrawler.Services.Matching
{
    /// <summary>
    /// Normalizes a <c>SourceGroup</c> (verbatim string from the M3U
    /// provider's <c>group-title</c>) into a form suitable **only for
    /// comparison / classification** by future components
    /// (<c>GroupTaxonomy</c>, <c>SourceGroupCategoryLookup</c>,
    /// <c>GroupResolver</c>).
    ///
    /// Contract — explicit non-goals (see specification section 6):
    /// - Does NOT collapse <c>|</c>, <c>||</c>, <c>:</c>, <c>-</c> into
    ///   each other (would lose semantic info like
    ///   "Portugal - Canais 24-7" → "24-7").
    /// - Does NOT strip Unicode decoration ("─ ✧･ﾟ||"). It is a
    ///   provider-specific marker; classification must decide if it
    ///   matters.
    /// - Does NOT strip quality/codec tokens ("HEVC", "4K", "VIP",
    ///   "FHD", "HD", "Mobile", "Backup"). They are
    ///   <c>Quality/TechnicalAttributes</c>, preserved at the
    ///   <c>DiscoveredStream</c> level; downstream classification
    ///   decides whether to surface them.
    /// - Does NOT classify ("Portugal → Portugal/Live"). That
    ///   belongs to <c>GroupTaxonomy</c>.
    ///
    /// The caller is responsible for preserving the original
    /// <c>SourceGroup</c>; this method never mutates its input.
    /// </summary>
    public static class GroupNormalizer
    {
        private static readonly Regex NonBreakingSpace =
            new("\u00A0", RegexOptions.Compiled);

        private static readonly Regex Whitespace =
            new(@"\s+", RegexOptions.Compiled);

        /// <summary>
        /// Returns a normalized form of <paramref name="sourceGroup"/>
        /// for comparison only. Returns the empty string for
        /// <c>null</c>, <see cref="string.Empty"/>, or whitespace-only
        /// input. Output is lowercase (InvariantCulture) with
        /// non-breaking spaces converted to regular spaces and runs
        /// of whitespace collapsed to a single space. The original
        /// string is never modified.
        /// </summary>
        public static string Normalize(string? sourceGroup)
        {
            if (string.IsNullOrWhiteSpace(sourceGroup)) return string.Empty;
            var s = NonBreakingSpace.Replace(sourceGroup, " ");
            s = Whitespace.Replace(s, " ").Trim();
            return s.ToLower(CultureInfo.InvariantCulture);
        }
    }
}
