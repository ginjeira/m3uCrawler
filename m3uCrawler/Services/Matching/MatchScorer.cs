using System.Globalization;

namespace m3uCrawler.Services.Matching
{
    public sealed class MatchScorer
    {
        public const int Exact = 95;
        public const int MinSafe = 80;

        public MatchBand Classify(int score, IReadOnlyList<int> otherScores, int threshold)
        {
            if (score >= threshold)
            {
                int margin = 5;
                bool ambiguous = otherScores.Any(o => o >= threshold && Math.Abs(o - score) <= margin);
                if (ambiguous) return MatchBand.Ambiguous;
                return score >= Exact ? MatchBand.Exact : MatchBand.Matched;
            }
            return MatchBand.None;
        }
    }

    public enum MatchBand
    {
        None,
        Matched,
        Exact,
        Ambiguous,
    }
}
