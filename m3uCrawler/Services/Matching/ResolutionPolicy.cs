using System.Collections.Generic;

namespace m3uCrawler.Services.Matching
{
    /// <summary>
    /// Combines the five dimensions (ChannelIdentity, SourceGroup,
    /// ContentType, Country, GroupTaxonomy) into a single
    /// <see cref="OutputGroupKind"/> final.
    ///
    /// <para>
    /// <b>Input contract</b>: the caller is responsible for
    /// normalizing the inputs (via <see cref="GroupNormalizer.Normalize"/>
    /// and <see cref="ChannelNormalizer.Normalize"/>) BEFORE invoking
    /// this method. This method does NOT normalize internally.
    /// </para>
    ///
    /// <para>
    /// <b>Architectural boundary</b>: this class never re-implements
    /// detection. It ONLY combines the outputs of the five existing
    /// components. No regex, no substring matching, no heuristics.
    /// </para>
    ///
    /// <para>
    /// See `.kilo/plans/1788214551330-resolution-policy-tdd.md` for
    /// the rationale behind each precedence rule.
    /// </para>
    /// </summary>
    public static class ResolutionPolicy
    {
        /// <summary>
        /// Resolves the final OutputGroupKind for a single stream,
        /// combining the five dimensions according to the documented
        /// precedence.
        ///
        /// <para>
        /// Precedence (deterministic):
        /// </para>
        /// <list type="number">
        ///   <item><paramref name="isForeign"/> = true → Foreign.</item>
        ///   <item>ContentType = VOD → PortugalVOD.</item>
        ///   <item>ContentType = Filmes24_7 → PortugalFilmes24_7.</item>
        ///   <item>ContentType = PPV → PortugalPPV.</item>
        ///   <item>ChannelCategoryLookup != Category.Live →
        ///         map Category to OutputGroupKind.</item>
        ///   <item>SourceGroupCategoryLookup != null →
        ///         map Category to OutputGroupKind.</item>
        ///   <item>GroupTaxonomy.OutputGroupKind != null → it.</item>
        ///   <item>Fallback → PortugalLive.</item>
        /// </list>
        ///
        /// <para>
        /// The caller is responsible for detecting Foreign (typically
        /// via CountryChannelValidator.ValidateStreams in batch) and
        /// passing the result as <paramref name="isForeign"/>.
        /// </para>
        /// </summary>
        /// <param name="channelIdentity">
        ///   Canonical ChannelIdentity produced by
        ///   <see cref="ChannelNormalizer.Normalize"/>. May be null.
        /// </param>
        /// <param name="sourceGroup">
        ///   SourceGroup canonical form produced by
        ///   <see cref="GroupNormalizer.Normalize"/>. May be null.
        /// </param>
        /// <param name="title">
        ///   Raw stream title (not normalized). May be null.
        /// </param>
        /// <param name="isForeign">
        ///   Whether the stream is foreign, decided by the caller
        ///   (typically CountryChannelValidator).
        /// </param>
        public static OutputGroupKind Resolve(
            string? channelIdentity,
            string? sourceGroup,
            string? title,
            bool isForeign)
        {
            // Prioridade 1: Foreign confirmado.
            if (isForeign)
            {
                return OutputGroupKind.Foreign;
            }

            // Prioridades 2-4: ContentType especial sobrepõe-se.
            var contentType = ContentTypeDetector.Detect(title, sourceGroup);
            switch (contentType)
            {
                case ContentType.VOD:
                    return OutputGroupKind.PortugalVOD;
                case ContentType.Filmes24_7:
                    return OutputGroupKind.PortugalFilmes24_7;
                case ContentType.PPV:
                    return OutputGroupKind.PortugalPPV;
                case ContentType.Live:
                    // Continua para Prioridades 5-7.
                    break;
            }

            // Prioridade 5: ChannelCategoryLookup com Category != Live.
            var channelCategory = ChannelCategoryLookup.Lookup(channelIdentity);
            if (channelCategory != Category.Live)
            {
                return MapCategoryToOutputGroupKind(channelCategory);
            }

            // Prioridade 6: SourceGroupCategoryLookup != null.
            var sourceGroupCategory = SourceGroupCategoryLookup.Lookup(sourceGroup);
            if (sourceGroupCategory.HasValue)
            {
                return MapCategoryToOutputGroupKind(sourceGroupCategory.Value);
            }

            // Prioridade 7: GroupTaxonomy com OutputGroupKind != null.
            var (taxonomyKind, _) = GroupTaxonomy.Lookup(sourceGroup);
            if (taxonomyKind.HasValue)
            {
                return taxonomyKind.Value;
            }

            // Prioridade 8: Fallback final.
            return OutputGroupKind.PortugalLive;
        }

        /// <summary>
        /// Maps a <see cref="Category"/> to the corresponding
        /// <see cref="OutputGroupKind"/>.
        ///
        /// Note: this is purely a 1-1 mapping; no detection or
        /// heuristic involved.
        /// </summary>
        private static OutputGroupKind MapCategoryToOutputGroupKind(Category category)
        {
            switch (category)
            {
                case Category.Live:
                    return OutputGroupKind.PortugalLive;
                case Category.Entretenimento:
                    return OutputGroupKind.PortugalEntretenimento;
                case Category.Desporto:
                    return OutputGroupKind.PortugalDesporto;
                case Category.Infantil:
                    return OutputGroupKind.PortugalInfantil;
                case Category.Documentarios:
                    return OutputGroupKind.PortugalDocumentarios;
                default:
                    // Inesperado: enum fora dos 5 valores conhecidos.
                    // Fallback seguro.
                    return OutputGroupKind.PortugalLive;
            }
        }
    }
}
