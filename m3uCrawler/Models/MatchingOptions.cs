namespace m3uCrawler.Models
{
    public sealed class MatchingOptions
    {
        public int MatchThreshold { get; init; } = 80;
        public int ExactMatchScore { get; init; } = 95;
        public int AmbiguityMargin { get; init; } = 5;
        public IReadOnlyDictionary<string, string> Aliases { get; init; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static MatchingOptions Default => new();
    }
}
