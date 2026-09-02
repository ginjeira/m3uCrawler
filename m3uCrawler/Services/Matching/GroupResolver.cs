using System;
using System.Collections.Generic;
using System.Linq;
using m3uCrawler.Models;

namespace m3uCrawler.Services.Matching
{
    /// <summary>
    /// Resolves the canonical display title within a bucket of
    /// <see cref="DiscoveredStream"/> variants of the same channel.
    ///
    /// <para>
    /// <b>Heuristic</b> (deterministic, no detection):
    /// </para>
    /// <list type="number">
    ///   <item>Filter to streams with <c>IsWorking=true</c>; if the
    ///         filtered list is empty, fall back to the entire bucket.</item>
    ///   <item>Order by length of
    ///         <see cref="ChannelNormalizer.Normalize"/>(title)
    ///         ascending — i.e. shortest normalized title first.</item>
    ///   <item>Tie-break by <see cref="DiscoveredStream.Title"/>
    ///         using <see cref="StringComparer.OrdinalIgnoreCase"/>.</item>
    ///   <item>Return the raw <see cref="DiscoveredStream.Title"/> of
    ///         the first element.</item>
    /// </list>
    ///
    /// <para>
    /// The heuristic intentionally preserves the behaviour of the
    /// legacy <c>ChannelMatcher.ChooseCanonicalName</c>. <b>No</b>
    /// majority voting, no frequency analysis, no fuzzy matching, no
    /// substring heuristics.
    /// </para>
    ///
    /// <para>
    /// See `.kilo/plans/1788214551330-group-resolver-tdd.md` for the
    /// rationale behind each rule.
    /// </para>
    /// </summary>
    public static class GroupResolver
    {
        /// <summary>
        /// Returns the canonical display title for a bucket of
        /// <see cref="DiscoveredStream"/> variants of the same channel.
        ///
        /// <exception cref="ArgumentNullException">
        /// When <paramref name="bucket"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// When <paramref name="bucket"/> is empty, or contains only
        /// streams with null/whitespace titles. The caller (typically
        /// <c>ChannelMatcher.BuildPlan</c>) must guarantee a non-empty
        /// bucket with at least one valid title before invoking this
        /// method.
        /// </exception>
        /// </summary>
        public static string ResolveCanonical(
            IReadOnlyList<DiscoveredStream> bucket)
        {
            if (bucket is null)
            {
                throw new ArgumentNullException(nameof(bucket));
            }
            if (bucket.Count == 0)
            {
                throw new ArgumentException(
                    "Bucket must not be empty.",
                    nameof(bucket));
            }

            // Filter to working streams; fall back to entire bucket.
            var working = bucket.Where(s => s.IsWorking).ToList();
            var pool = working.Count > 0 ? working : (IList<DiscoveredStream>)bucket;

            // Filter out null/whitespace titles defensively.
            var candidates = pool
                .Where(s => !string.IsNullOrWhiteSpace(s.Title))
                .ToList();

            if (candidates.Count == 0)
            {
                throw new ArgumentException(
                    "Bucket contains no stream with a non-null/non-whitespace title.",
                    nameof(bucket));
            }

            // Order by normalized-title length ascending (shortest first),
            // tie-break by OrdinalIgnoreCase on raw title.
            return candidates
                .OrderBy(s => ChannelNormalizer.Normalize(s.Title).Length)
                .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .First()
                .Title;
        }
    }
}
