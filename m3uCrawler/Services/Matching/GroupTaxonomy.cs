using System.Collections.Generic;

namespace m3uCrawler.Services.Matching
{
    /// <summary>
    /// Editorial OutputGroup kind (final classification after the
    /// pipeline converges).
    ///
    /// 9 values total, mapped 1-1 from the investigation report.
    /// </summary>
    public enum OutputGroupKind
    {
        PortugalLive,
        PortugalVOD,
        PortugalFilmes24_7,
        PortugalEntretenimento,
        PortugalDesporto,
        PortugalInfantil,
        PortugalDocumentarios,
        PortugalPPV,
        Foreign,
    }

    /// <summary>
    /// Strength of the editorial evidence carried by the SourceGroup.
    ///
    /// <para>
    /// <b>High</b>: SourceGroup is a canonical editorial category name
    /// (e.g. "eu | pt | documentarios"), or carries an explicit country
    /// or content-type embedded in its name (e.g. "vod | portugal").
    /// </para>
    /// <para>
    /// <b>Medium</b>: SourceGroup has structural ambiguity (decoration,
    /// codec, quality, partial-name). Classification is by inference.
    /// </para>
    /// <para>
    /// <b>Low</b>: SourceGroup is a generic catch-all (e.g. "Portugal").
    /// Classification is by default; downstream ResolutionPolicy should
    /// prefer ChannelCategoryLookup when available.
    /// </para>
    /// </summary>
    public enum Confidence
    {
        High,
        Medium,
        Low,
    }

    /// <summary>
    /// Resolves the editorial <see cref="OutputGroupKind"/> implied by
    /// a verbatim SourceGroup (provider's group-title), together with
    /// a <see cref="Confidence"/> rating the strength of the evidence.
    ///
    /// <para>
    /// <b>Input contract</b>: caller is responsible for normalizing
    /// the SourceGroup via <see cref="GroupNormalizer.Normalize"/>
    /// BEFORE invoking this lookup. This method does NOT normalize
    /// internally.
    /// </para>
    ///
    /// <para>
    /// Returns <c>(null, High)</c> when:
    /// </para>
    /// <list type="bullet">
    ///   <item>input is null/empty/whitespace;</item>
    ///   <item>SourceGroup is not in the curated map.</item>
    /// </list>
    ///
    /// <para>
    /// Architectural boundary: this class NEVER decides ContentType,
    /// Country, OutputGroup (the future ResolutionPolicy does), or
    /// Quality/TechnicalAttributes. The nullable tuple reflects
    /// "absence of editorial information".
    /// </para>
    ///
    /// <para>
    /// See `.kilo/plans/1788214551330-group-taxonomy-tdd.md` for the
    /// rationale behind each of the 27 SourceGroup mappings.
    /// </para>
    /// </summary>
    public static class GroupTaxonomy
    {
        // Curated exact-match dictionary. Case-sensitive.
        // Each value is the (OutputGroupKind, Confidence) tuple that
        // the canonical SourceGroup implies. No heuristic matching:
        // any SourceGroup not in this map returns (null, High).
        private static readonly Dictionary<string, (OutputGroupKind OutputGroup, Confidence Confidence)> Taxonomy =
            new(System.StringComparer.Ordinal)
        {
            // ========================= Editoriais canónicos (High) =========================
            ["eu | pt | general"]        = (OutputGroupKind.PortugalLive,            Confidence.High),
            ["eu | pt | entretenimento"] = (OutputGroupKind.PortugalEntretenimento,  Confidence.High),
            ["eu | pt | filmes e series"]= (OutputGroupKind.PortugalEntretenimento,  Confidence.High),
            ["eu | pt | documentarios"]  = (OutputGroupKind.PortugalDocumentarios,   Confidence.High),
            ["eu | pt | infantil"]       = (OutputGroupKind.PortugalInfantil,         Confidence.High),
            ["eu | pt | esportes"]       = (OutputGroupKind.PortugalDesporto,         Confidence.High),

            // ========================= Genéricos / catch-all =========================
            ["portuguese"]   = (OutputGroupKind.PortugalLive,     Confidence.Low),
            ["portugal"]     = (OutputGroupKind.PortugalLive,     Confidence.Low),
            ["sports networks"] = (OutputGroupKind.PortugalDesporto, Confidence.Medium),

            // ========================= Content Type embedded (High) =========================
            // Map names only; semantic detection stays in ContentTypeDetector.
            ["vod | portugal"]              = (OutputGroupKind.PortugalVOD,        Confidence.High),
            ["portugal - canais 24-7"]      = (OutputGroupKind.PortugalFilmes24_7, Confidence.High),
            ["vip | liga portugal betclic"] = (OutputGroupKind.PortugalPPV,        Confidence.High),

            // ========================= Decorativos / codecs / qualidade (Medium) =========================
            ["─ ✧･ﾟ|| portugal"]          = (OutputGroupKind.PortugalLive,     Confidence.Medium),
            ["─ ✧･ﾟ|| portugal vip"]      = (OutputGroupKind.PortugalLive,     Confidence.Medium),
            ["─ ✧･ﾟ|| portugal sports"]   = (OutputGroupKind.PortugalDesporto, Confidence.Medium),
            ["─ ✧･ﾟ|| portugal sport vip"]= (OutputGroupKind.PortugalDesporto, Confidence.Medium),
            ["portugal hevc"]               = (OutputGroupKind.PortugalLive,     Confidence.Medium),
            ["vip | 4k ultra hd"]           = (OutputGroupKind.PortugalLive,     Confidence.Medium),

            // ========================= Foreign (High) =========================
            ["eu | belgium"]           = (OutputGroupKind.Foreign, Confidence.High),
            ["eu | bulgaria"]          = (OutputGroupKind.Foreign, Confidence.High),
            ["am | latino"]            = (OutputGroupKind.Foreign, Confidence.High),
            ["eu | france sports"]     = (OutputGroupKind.Foreign, Confidence.High),
            ["eu | france cinema"]     = (OutputGroupKind.Foreign, Confidence.High),
            ["eu | lithuania"]         = (OutputGroupKind.Foreign, Confidence.High),
            ["eu | se | sport tv ppv"] = (OutputGroupKind.Foreign, Confidence.High),
            ["eu | exyu | slovenija"]  = (OutputGroupKind.Foreign, Confidence.High),
            ["as | cambodia"]          = (OutputGroupKind.Foreign, Confidence.High),
        };

        /// <summary>
        /// Returns the (OutputGroupKind?, Confidence) tuple for the
        /// canonical SourceGroup, or (null, High) when the SourceGroup
        /// is not recognized or input is null/empty/whitespace.
        ///
        /// <para>
        /// <b>Input contract</b>: the value must already be normalized
        /// via <see cref="GroupNormalizer.Normalize"/>. If the caller
        /// passes raw input (uppercase, whitespace, NBSP, etc.), the
        /// result is (null, High).
        /// </para>
        /// </summary>
        public static (OutputGroupKind? OutputGroup, Confidence Confidence)
            Lookup(string? sourceGroup)
        {
            if (string.IsNullOrWhiteSpace(sourceGroup))
            {
                return (null, Confidence.High);
            }
            if (Taxonomy.TryGetValue(sourceGroup, out var entry))
            {
                return entry;
            }
            return (null, Confidence.High);
        }
    }
}
