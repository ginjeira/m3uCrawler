using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace m3uCrawler.Services.Matching
{
    public static class ChannelNormalizer
    {
        private static readonly Regex BracketTags = new(@"\[[^\]]*\]", RegexOptions.Compiled);
        private static readonly Regex Parenthesized = new(@"\(([^)]*)\)", RegexOptions.Compiled);
        private static readonly Regex GeoPrefix = new(@"^(?:[A-Za-z]{2,3})[:\|]\s*", RegexOptions.Compiled);
        private static readonly Regex QualityTag = new(@"\b(?:4K|UHD|FHD|HD|SD|HDR|HEVC)\b", RegexOptions.Compiled);
        private static readonly Regex RegionTag = new(@"\b(?:East|West|North|South|Pacific|Mountain|Central|Leste|Oeste|Norte|Sul)\b", RegexOptions.Compiled);
        private static readonly Regex CountryToken = new(@"\b(?:Portugal|España|Spain|France|Italia|Brasil|Brazil|US|UK|DE|FR|IT|ES|PT|BR)\b", RegexOptions.Compiled);
        private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex DiacriticsFormD = new(@"\p{Mn}", RegexOptions.Compiled);
        private static readonly Regex LetterDigitSplit = new(@"([A-Za-zÀ-ÿ])(\d)", RegexOptions.Compiled);
        private static readonly Regex DigitLetterSplit = new(@"(\d)([A-Za-zÀ-ÿ])", RegexOptions.Compiled);

        public static string Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            var s = raw.Normalize(NormalizationForm.FormD);
            s = DiacriticsFormD.Replace(s, string.Empty);

            s = s.Replace(":", " ")
                  .Replace("|", " ")
                  .Replace("/", " ")
                  .Replace("\\", " ")
                  .Replace("-", " ")
                  .Replace("_", " ")
                  .Replace(".", " ")
                  .Replace(",", " ")
                  .Replace(";", " ");

            s = LetterDigitSplit.Replace(s, "$1 $2");
            s = DigitLetterSplit.Replace(s, "$1 $2");

            s = GeoPrefix.Replace(s, string.Empty);
            s = BracketTags.Replace(s, " ");
            var parenSb = new StringBuilder();
            int depth = 0;
            foreach (var ch in s)
            {
                if (ch == '(') { depth++; continue; }
                if (ch == ')') { if (depth > 0) depth--; continue; }
                if (depth == 0) parenSb.Append(ch);
            }
            s = parenSb.ToString();

            s = QualityTag.Replace(s, " ");
            s = RegionTag.Replace(s, " ");
            s = CountryToken.Replace(s, " ");

            s = Parenthesized.Replace(s, " ");

            s = MultiSpace.Replace(s, " ").Trim();

            s = s.Replace("|", " ");

            s = s.Normalize(NormalizationForm.FormC);

            return s.ToLower(CultureInfo.InvariantCulture);
        }

        public static IReadOnlyList<string> Tokens(string normalized)
        {
            if (string.IsNullOrWhiteSpace(normalized)) return Array.Empty<string>();
            return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }
}
