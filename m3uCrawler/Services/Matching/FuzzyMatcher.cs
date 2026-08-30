using System.Globalization;

namespace m3uCrawler.Services.Matching
{
    public sealed class FuzzyMatcher
    {
        public const int ExactScore = 100;
        public const int NumericSiblingPenalty = 50;

        public MatchScore Score(string query, string candidate)
        {
            var q = ChannelNormalizer.Normalize(query);
            var c = ChannelNormalizer.Normalize(candidate);

            if (string.IsNullOrWhiteSpace(q) || string.IsNullOrWhiteSpace(c))
                return new MatchScore(0, "empty");

            if (string.Equals(q, c, StringComparison.Ordinal))
                return new MatchScore(ExactScore, "exact");

            if (q.Length >= 3 && c.Length >= 3
                && (q.Contains(c, StringComparison.Ordinal) || c.Contains(q, StringComparison.Ordinal)))
            {
                int minLen = Math.Min(q.Length, c.Length);
                int maxLen = Math.Max(q.Length, c.Length);
                if (maxLen > 0 && (double)minLen / maxLen >= 0.4)
                {
                    if (HasConflictingDigitToken(q, c))
                        return new MatchScore(Math.Min(NumericSiblingPenalty, 60), "numeric-sibling-guard");
                    return new MatchScore(95, "substring");
                }
            }

            var qTokens = ChannelNormalizer.Tokens(q).OrderBy(t => t, StringComparer.Ordinal).ToArray();
            var cTokens = ChannelNormalizer.Tokens(c).OrderBy(t => t, StringComparer.Ordinal).ToArray();

            int qTokenSetScore = TokenSetScore(qTokens, cTokens);
            int lev = Levenshtein(string.Join(' ', qTokens), string.Join(' ', cTokens));
            int ml = Math.Max(string.Join(' ', qTokens).Length, string.Join(' ', cTokens).Length);
            if (ml == 0)
                return new MatchScore(0, "empty");

            int levScore = ml == 0 ? 0 : (int)Math.Round(100.0 * (1.0 - (double)lev / ml), MidpointRounding.AwayFromZero);
            int ratio = Math.Max(qTokenSetScore, Math.Min(99, levScore));

            var digitTokensQ = qTokens.Where(t => t.Any(char.IsDigit)).ToHashSet();
            var digitTokensC = cTokens.Where(t => t.Any(char.IsDigit)).ToHashSet();

            if (digitTokensQ.Count > 0 && digitTokensC.Count > 0
                && !digitTokensQ.Overlaps(digitTokensC)
                && digitTokensQ.Count != digitTokensC.Count)
            {
                ratio = Math.Min(ratio, NumericSiblingPenalty);
                return new MatchScore((int)ratio, "numeric-sibling-guard");
            }

            if (HasConflictingDigitToken(q, c))
                return new MatchScore((int)Math.Min(ratio, NumericSiblingPenalty), "numeric-sibling-guard");

            return new MatchScore(Math.Clamp(ratio, 0, 99), "fuzzy");
        }

        private static int TokenSetScore(string[] qTokens, string[] cTokens)
        {
            if (qTokens.Length == 0 || cTokens.Length == 0) return 0;
            var qSet = new HashSet<string>(qTokens, StringComparer.OrdinalIgnoreCase);
            var cSet = new HashSet<string>(cTokens, StringComparer.OrdinalIgnoreCase);
            int intersect = qSet.Intersect(cSet, StringComparer.OrdinalIgnoreCase).Count();
            int total = qSet.Count + cSet.Count;
            if (total == 0) return 0;
            int pct = (int)Math.Round(100.0 * (2.0 * intersect) / total, MidpointRounding.AwayFromZero);
            return Math.Clamp(pct, 0, 99);
        }

        private static bool HasConflictingDigitToken(string q, string c)
        {
            var qTokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cTokens = c.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var qDigits = qTokens.Where(t => t.Any(char.IsDigit)).ToHashSet(StringComparer.Ordinal);
            var cDigits = cTokens.Where(t => t.Any(char.IsDigit)).ToHashSet(StringComparer.Ordinal);
            if (qDigits.Count == 0 || cDigits.Count == 0) return false;
            if (qDigits.SetEquals(cDigits)) return false;
            return qDigits.Any(qd => cDigits.Any(cd => SingleDigitDifference(qd, cd)));
        }

        private static bool SingleDigitDifference(string a, string b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) diff++;
            return diff == 1 && a.Any(char.IsDigit) && b.Any(char.IsDigit);
        }

        private static int Levenshtein(string a, string b)
        {
            int n = a.Length;
            int m = b.Length;
            if (n == 0) return m;
            if (m == 0) return n;
            var prev = new int[m + 1];
            var curr = new int[m + 1];
            for (int j = 0; j <= m; j++) prev[j] = j;
            for (int i = 1; i <= n; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= m; j++)
                {
                    int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                (prev, curr) = (curr, prev);
            }
            return prev[m];
        }
    }

    public readonly record struct MatchScore(int Score, string Reason)
    {
        public static readonly MatchScore None = new(0, "none");

        public override string ToString() => $"{Score}:{Reason}";
    }
}
