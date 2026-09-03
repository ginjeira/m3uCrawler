using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace m3uCrawler.Services.Matching
{
    /// <summary>
    /// Editorial / structural kind of a <see cref="Models.DiscoveredStream"/>.
    ///
    /// <para>
    /// This enum is the explicit classification boundary between
    /// <i>stream discovery</i> and <i>channel matching</i>. Only entries
    /// with <see cref="ChannelKind.Channel"/> are eligible to produce a
    /// <see cref="Models.ChannelDecision"/> (NewChannel / Matched /
    /// ExistingReassigned / etc.). All other kinds are excluded from the
    /// matching pipeline and recorded as <see cref="Models.ClassifiedExclusion"/>.
    /// </para>
    ///
    /// <para>
    /// Taxonomia alinhada com <c>ResolutionPolicy</c> e
    /// <c>ContentTypeDetector</c>: não há duplicação. Bundles e VOD
    /// mantêm-se como kinds próprios porque têm semântica estrutural
    /// distinta da editorial (Live/VOD/PPV/Foreign) — bundles são
    /// packages de streams lineares; VOD são ficheiros individuais.
    /// </para>
    /// </summary>
    public enum ChannelKind
    {
        /// <summary>Continuous live television channel (eligible for matching).</summary>
        Channel = 0,

        /// <summary>Source group name appearing as a title (no stream content).</summary>
        Group,

        /// <summary>24/7 themed loop (e.g. "Filmes Batman 24/7", "SPORT TV PACK").</summary>
        Bundle,

        /// <summary>Single video on demand (filme / série / episódio individual).</summary>
        Vod,

        /// <summary>Continuous camera stream (e.g. "LiveCam Nazaré").</summary>
        LiveCam,

        /// <summary>Category label without stream (e.g. "Filmes", "Series").</summary>
        Category,

        /// <summary>Non-Portuguese content (tracked for reporting; never promoted to a PT channel).</summary>
        Foreign,

        /// <summary>Colour placeholder (#f#...), structural artefact, not a channel.</summary>
        Placeholder,

        /// <summary>No evidence of being a channel; entry must NOT produce a NewChannel.</summary>
        Unknown,
    }

    /// <summary>
    /// Result of classifying a single stream entry against
    /// <see cref="ContentClassifier"/>. Pure value type — no I/O,
    /// deterministic, safe to serialise.
    /// </summary>
    public readonly record struct ChannelClassification(
        ChannelKind Kind,
        string Reason)
    {
        public static readonly ChannelClassification Channel = new(ChannelKind.Channel, "channel-evidence");
        public static readonly ChannelClassification Group = new(ChannelKind.Group, "group-taxonomy");
        public static readonly ChannelClassification Unknown = new(ChannelKind.Unknown, "no-channel-evidence");

        public static ChannelClassification Of(ChannelKind kind, string reason) => new(kind, reason);
    }

    /// <summary>
    /// Pure deterministic classifier that runs BEFORE the
    /// channel-matching pipeline.
    ///
    /// <para>
    /// <b>Precedence</b> (deterministic, evaluated in order, first
    /// match wins):
    /// </para>
    /// <list type="number">
    ///   <item>Empty title → <see cref="ChannelKind.Unknown"/>.</item>
    ///   <item>Colour-placeholder pattern (matches what the legacy
    ///         bundle-guard called "placeholders de cor") →
    ///         <see cref="ChannelKind.Placeholder"/>.</item>
    ///   <item><see cref="ContentTypeDetector"/> returns <see cref="ContentType.VOD"/>
    ///         or <see cref="ContentType.PPV"/> → <see cref="ChannelKind.Vod"/>.</item>
    ///   <item>Source group is a 24/7 loop group (e.g.
    ///         <c>portugal - canais 24-7</c>) OR title contains
    ///         <c>24/7</c>/<c>24-7</c> →
    ///         <see cref="ChannelKind.Bundle"/>.</item>
    ///   <item>Title contains <c>LiveCam</c> as a word →
    ///         <see cref="ChannelKind.LiveCam"/>.</item>
    ///   <item>Title contains <c>PACK</c> or <c>BUNDLE</c> as a word →
    ///         <see cref="ChannelKind.Bundle"/>.</item>
    ///   <item><see cref="GroupTaxonomy"/> maps source group to
    ///         <see cref="OutputGroupKind.Foreign"/> →
    ///         <see cref="ChannelKind.Foreign"/>.</item>
    ///   <item>Normalized title resolves through
    ///         <see cref="ChannelCategoryLookup"/> →
    ///         <see cref="ChannelKind.Channel"/>.</item>
    ///   <item><see cref="SourceGroupCategoryLookup"/> recognises the
    ///         source group as an editorial category AND the normalized
    ///         title is in <see cref="ChannelCategoryLookup"/> OR matches
    ///         a known PT-channel pattern → <see cref="ChannelKind.Channel"/>.</item>
    ///   <item>Otherwise → <see cref="ChannelKind.Unknown"/>.</item>
    /// </list>
    ///
    /// <para>
    /// <b>Architectural boundary</b>: this class NEVER decides
    /// <c>ContentType</c>, <c>Category</c>, <c>Country</c>,
    /// <c>OutputGroup</c> or quality attributes. It composes the
    /// outputs of the existing detection components into a single
    /// structural kind for the matching pipeline.
    /// </para>
    ///
    /// <para>
    /// The detector is intentionally conservative: a stream that does
    /// not produce positive evidence of being a live TV channel is
    /// classified as <see cref="ChannelKind.Unknown"/> and therefore
    /// excluded from matching. <c>Unknown</c> is NEVER promoted to
    /// <see cref="ChannelKind.Channel"/>.
    /// </para>
    /// </summary>
    public static class ContentClassifier
    {
        // Colour placeholder: "#f#..." ou "#00ff00ff####" produzido pelo
        // Dispatcharr para separar secções do playlist. Não é stream.
        private static readonly Regex ColourPlaceholderPattern = new(
            @"^#f#|^#[0-9a-fA-F]{4,}#",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 24/7 themed loop: cobrimos ambas as grafias observadas no
        // fixture real (com barra, com hífen).
        private static readonly Regex Loop247Pattern = new(
            @"\b(24\s*/\s*7|24\s*-\s*7)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // LiveCam como palavra isolada (whitespace antes/depois).
        // Evita matches parciais como "LiveCam_xxx".
        private static readonly Regex LiveCamWordPattern = new(
            @"\bLiveCam\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // PACK / BUNDLE como palavra isolada.
        private static readonly Regex BundleWordPattern = new(
            @"\b(PACK|BUNDLE)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Source group "24-7 loop" (case-insensitive, normalized-space).
        // Exemplos reais: "portugal - canais 24-7".
        private static readonly Regex LoopGroupPattern = new(
            @"\bcanais\s*24\s*[-/]\s*7\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Source group "VOD | ..." (vinha do VodGroupPattern legacy).
        private static readonly Regex VodGroupPrefixPattern = new(
            @"^VOD\s*\|",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Source group começa por "VIP | LIGA PORTUGAL BETCLIC" — fica
        // acima do matching de canais porque o conteúdo é evento
        // desportivo PPV/BETCLIC, não um canal linear.
        private static readonly Regex PpvBetclicGroupPattern = new(
            @"\b(PPV|BETCLIC|LIGA PORTUGAL)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Classifies the entry given by its title and source group.
        /// Never throws; always returns a defined value.
        /// </summary>
        public static ChannelClassification Classify(string? title, string? sourceGroup)
        {
            var normalizedTitle = ChannelNormalizer.Normalize(title);
            var trimmedTitle = title?.Trim() ?? string.Empty;
            var trimmedGroup = sourceGroup?.Trim() ?? string.Empty;

            // 1. Empty title → Unknown.
            if (string.IsNullOrWhiteSpace(trimmedTitle))
            {
                return ChannelClassification.Of(ChannelKind.Unknown, "empty-title");
            }

            // 2. Colour placeholder.
            if (ColourPlaceholderPattern.IsMatch(trimmedTitle))
            {
                return ChannelClassification.Of(ChannelKind.Placeholder, "colour-placeholder");
            }

            // 3. Channel explícito: identidade conhecida no ChannelCategoryLookup.
            //    Esta é a via mais forte. Sobrepõe-se a VOD/Bundle/PPV
            //    detectados pelo source group porque a identidade do
            //    canal é a fonte primária de verdade (ex.: "SIC" num
            //    grupo "portugal - canais 24-7" continua a ser um canal).
            //
            //    NOTA: Foreign NÃO é um ChannelKind — é um OutputGroupKind.
            //    Um canal com identidade conhecida num grupo estrangeiro
            //    continua a ser Channel; o ResolutionPolicy atribui-lhe
            //    depois OutputGroupKind.Foreign.
            if (!string.IsNullOrEmpty(normalizedTitle) && ChannelCategoryLookup.Contains(normalizedTitle))
            {
                return ChannelClassification.Of(ChannelKind.Channel, "channel-category-lookup");
            }

            // 4. VOD/Bundle/PPV via título (ContentTypeDetector).
            //    Aplicado apenas a títulos NÃO reconhecidos como
            //    identidade de canal — os canais legítimos não são
            //    afectados por estas heurísticas.
            var contentType = ContentTypeDetector.Detect(trimmedTitle, trimmedGroup);
            if (contentType == ContentType.VOD)
            {
                return ChannelClassification.Of(ChannelKind.Vod, "vod-title-pattern");
            }
            if (contentType == ContentType.PPV)
            {
                return ChannelClassification.Of(ChannelKind.Vod, "ppv-group-pattern");
            }

            // 5. Bundle por source group 24-7 ou por title 24/7/24-7.
            if (LoopGroupPattern.IsMatch(trimmedGroup))
            {
                return ChannelClassification.Of(ChannelKind.Bundle, "loop-group-pattern");
            }
            if (Loop247Pattern.IsMatch(trimmedTitle))
            {
                return ChannelClassification.Of(ChannelKind.Bundle, "loop-title-pattern");
            }

            // 6. Bundle por PACK / BUNDLE como palavra isolada.
            if (BundleWordPattern.IsMatch(trimmedTitle))
            {
                return ChannelClassification.Of(ChannelKind.Bundle, "bundle-title-pattern");
            }

            // 7. VOD por prefixo do source group ("VOD | ...").
            if (VodGroupPrefixPattern.IsMatch(trimmedGroup))
            {
                return ChannelClassification.Of(ChannelKind.Vod, "vod-group-prefix");
            }

            // 8. VOD/PPV por BETCLIC/LIGA PORTUGAL (cobre "PT - NO EVENT"
            //    em grupos PPV/BETCLIC).
            if (PpvBetclicGroupPattern.IsMatch(trimmedGroup))
            {
                return ChannelClassification.Of(ChannelKind.Vod, "ppv-betclic-group");
            }

            // 9. LiveCam como palavra isolada.
            if (LiveCamWordPattern.IsMatch(trimmedTitle))
            {
                return ChannelClassification.Of(ChannelKind.LiveCam, "livecam-title-pattern");
            }

            // 10. Foreign: SourceGroup mapeado para Foreign em
            //     GroupTaxonomy (cobre o caso de canais cujo source
            //     group é estrangeiro; títulos não-canais estrangeiros
            //     ficam em Unknown, não em Foreign — não temos como
            //     saber se são canais sem identidade explícita).
            var normalizedGroupLate = GroupNormalizer.Normalize(trimmedGroup);
            if (!string.IsNullOrEmpty(normalizedGroupLate))
            {
                var (taxonomyKind, _) = GroupTaxonomy.Lookup(normalizedGroupLate);
                if (taxonomyKind == OutputGroupKind.Foreign)
                {
                    return ChannelClassification.Of(ChannelKind.Foreign, "group-taxonomy-foreign");
                }
            }

            // 11. Sem evidência suficiente.
            return ChannelClassification.Unknown;
        }
    }
}
