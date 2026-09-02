using System.Text.RegularExpressions;

namespace m3uCrawler.Services.Matching
{
    /// <summary>
    /// Editorial structure of the content carried by a single stream.
    /// </summary>
    public enum ContentType
    {
        /// <summary>
        /// Continuous live television channel.
        /// </summary>
        Live = 0,

        /// <summary>
        /// Single video on demand (filme / série / episódio
        /// individual). Detected by title pattern
        /// <c>^PT\s*-\s*.+?\s*-\s*\d{4}\s*$</c>.
        /// </summary>
        VOD,

        /// <summary>
        /// Channel that loops a stream 24 hours a day, often
        /// themed (movies of an actor, fights, etc.). Detected by
        /// <c>24/7</c> or <c>24-7</c> in the SourceGroup (first) or
        /// in the title (fallback).
        /// </summary>
        Filmes24_7,

        /// <summary>
        /// Pay-per-view / one-off event stream (e.g. Liga Portugal
        /// match). Detected by <c>PPV</c> or <c>BETCLIC</c> in the
        /// SourceGroup.
        /// </summary>
        PPV,
    }

    /// <summary>
    /// Detects the <see cref="ContentType"/> of a stream from its
    /// verbatim <paramref name="title"/> and <paramref name="sourceGroup"/>.
    ///
    /// <para>
    /// This detector answers ONE question only: <i>what kind of
    /// content does this stream represent?</i> It does NOT decide
    /// whether the stream should be applied to Dispatcharr — that is
    /// the responsibility of <c>ChannelMatcher.IsBundleOrCategory</c>
    /// and the future <c>ResolutionPolicy</c>.
    /// </para>
    ///
    /// <para>
    /// Foreign detection is NOT part of this detector — see
    /// <c>CountryChannelValidator</c>.
    /// </para>
    ///
    /// Precedence (deterministic):
    /// <list type="number">
    ///   <item><b>VOD</b> — title matches <c>^PT\s*-\s*.+?\s*-\s*\d{4}\s*$</c>.</item>
    ///   <item><b>Filmes24_7</b> — SourceGroup first, then title contains
    ///         <c>24/7</c> or <c>24-7</c>.</item>
    ///   <item><b>PPV</b> — SourceGroup contains <c>PPV</c> or <c>BETCLIC</c>.</item>
    ///   <item><b>Live</b> — fallback.</item>
    /// </list>
    /// </summary>
    public static class ContentTypeDetector
    {
        // VOD: "PT - <título> - <ano de 4 dígitos>" no fim do título.
        private static readonly Regex VodTitlePattern = new(
            @"^PT\s*-\s*.+?\s*-\s*\d{4}\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Filmes 24/7 ou 24-7: cobre ambos os formatos observados
        // ("24/7" com barra, "24-7" com hífen).
        private static readonly Regex Movies247Pattern = new(
            @"\b(24\s*/\s*7|24\s*-\s*7)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // PPV: SourceGroup contém "PPV" ou "BETCLIC".
        private static readonly Regex PpvGroupPattern = new(
            @"\b(PPV|BETCLIC)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Returns the <see cref="ContentType"/> of the stream
        /// described by <paramref name="title"/> and
        /// <paramref name="sourceGroup"/>. Either parameter may be
        /// <c>null</c>, empty or whitespace; the function never
        /// throws and always returns a defined enum value.
        /// </summary>
        public static ContentType Detect(string? title, string? sourceGroup)
        {
            // 1. VOD (most specific signal — title format).
            if (!string.IsNullOrWhiteSpace(title)
                && VodTitlePattern.IsMatch(title.Trim()))
            {
                return ContentType.VOD;
            }

            // 2. Filmes 24/7 (SourceGroup first, then title).
            if (!string.IsNullOrWhiteSpace(sourceGroup)
                && Movies247Pattern.IsMatch(sourceGroup))
            {
                return ContentType.Filmes24_7;
            }
            if (!string.IsNullOrWhiteSpace(title)
                && Movies247Pattern.IsMatch(title))
            {
                return ContentType.Filmes24_7;
            }

            // 3. PPV (SourceGroup).
            if (!string.IsNullOrWhiteSpace(sourceGroup)
                && PpvGroupPattern.IsMatch(sourceGroup))
            {
                return ContentType.PPV;
            }

            // 4. Live (fallback).
            return ContentType.Live;
        }
    }
}