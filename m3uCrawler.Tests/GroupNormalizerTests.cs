using m3uCrawler.Services.Matching;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// TDD tests for <see cref="GroupNormalizer"/>.
///
/// Contract: produce a normalized textual form of a <c>SourceGroup</c>
/// suitable only for **comparison / classification**. The original
/// <c>SourceGroup</c> is preserved verbatim by the caller.
///
/// See `.kilo/plans/1788214551330-group-taxonomy-specification.md`
/// section 6 for the formal contract.
/// </summary>
public class GroupNormalizerTests
{
    // -------- 1. Trim --------

    [Theory]
    [InlineData(" Portugal ", "portugal")]
    [InlineData("  Portugal  ", "portugal")]
    public void Normalize_trims_whitespace(string input, string expected)
    {
        Assert.Equal(expected, GroupNormalizer.Normalize(input));
    }

    // -------- 2. Lowercase (Unicode-InvariantCulture) --------

    [Theory]
    [InlineData("PORTUGAL", "portugal")]
    [InlineData("Portugal", "portugal")]
    [InlineData("PoRtUgAl", "portugal")]
    public void Normalize_lowercases_input(string input, string expected)
    {
        Assert.Equal(expected, GroupNormalizer.Normalize(input));
    }

    // -------- 3. NBSP (U+00A0) -> space --------

    [Fact]
    public void Normalize_converts_NBSP_to_space()
    {
        const string nbsp = "EU | PT | GENERAL\u00A0";
        const string space = "EU | PT | GENERAL";
        Assert.Equal(
            GroupNormalizer.Normalize(space),
            GroupNormalizer.Normalize(nbsp));
        Assert.Equal("eu | pt | general", GroupNormalizer.Normalize(nbsp));
    }

    [Fact]
    public void Normalize_converts_NBSP_between_pipes_to_space()
    {
        // Observed in playlist: "EU | PT | ESPORTES" with NBSP.
        var input = "EU\u00A0|\u00A0PT\u00A0|\u00A0ESPORTES";
        Assert.Equal("eu | pt | esportes", GroupNormalizer.Normalize(input));
    }

    // -------- 4. Collapse whitespace --------

    [Theory]
    [InlineData("EU   |   PT   |   GENERAL", "eu | pt | general")]
    [InlineData("  Portugal  HEVC  ", "portugal hevc")]
    public void Normalize_collapses_whitespace(string input, string expected)
    {
        Assert.Equal(expected, GroupNormalizer.Normalize(input));
    }

    // -------- 5. Null / empty / whitespace-only --------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void Normalize_returns_empty_for_null_or_whitespace(string? input)
    {
        Assert.Equal(string.Empty, GroupNormalizer.Normalize(input));
    }

    // -------- 6. Idempotence --------

    [Theory]
    [InlineData("Portugal")]
    [InlineData("EU | PT | GENERAL")]
    [InlineData("─ ✧･ﾟ|| PORTUGAL VIP")]
    [InlineData("Portugal - Canais 24-7")]
    [InlineData("VIP | 4K ULTRA HD")]
    public void Normalize_is_idempotent(string input)
    {
        var once = GroupNormalizer.Normalize(input);
        var twice = GroupNormalizer.Normalize(once);
        Assert.Equal(once, twice);
    }

    // -------- 7. Non-equivalence preservation --------
    //
    // These are documented **non-Goals** of the current TDD: the
    // normalizer MUST preserve the differences so downstream
    // classification (GroupTaxonomy, SourceGroupCategoryLookup)
    // can decide.

    [Fact]
    public void Normalize_preserves_Portugal_Canais_24_7_hyphen_and_digits()
    {
        // Critical: do NOT collapse the hyphen. "24-7" must survive.
        Assert.Equal(
            "portugal - canais 24-7",
            GroupNormalizer.Normalize("Portugal - Canais 24-7"));
    }

    [Fact]
    public void Normalize_preserves_HEVC_token()
    {
        // HEVC is a Quality/TechnicalAttribute, not decoration. Keep it.
        Assert.Equal("portugal hevc", GroupNormalizer.Normalize("Portugal HEVC"));
    }

    [Fact]
    public void Normalize_preserves_VIP_token_in_VIP_4K_ULTRA_HD()
    {
        // VIP and 4K are attributes; they must not be stripped.
        Assert.Equal(
            "vip | 4k ultra hd",
            GroupNormalizer.Normalize("VIP | 4K ULTRA HD"));
    }

    [Fact]
    public void Normalize_preserves_VIP_token_in_decorated_group()
    {
        Assert.Equal(
            "─ ✧･ﾟ|| portugal vip",
            GroupNormalizer.Normalize("─ ✧･ﾟ|| PORTUGAL VIP"));
    }

    [Theory]
    [InlineData("─ ✧･ﾟ|| PORTUGAL", "─ ✧･ﾟ|| portugal")]
    [InlineData("─ ✧･ﾟ|| PORTUGAL VIP", "─ ✧･ﾟ|| portugal vip")]
    [InlineData("─ ✧･ﾟ|| PORTUGAL SPORTS", "─ ✧･ﾟ|| portugal sports")]
    [InlineData("─ ✧･ﾟ|| PORTUGAL SPORT VIP", "─ ✧･ﾟ|| portugal sport vip")]
    public void Normalize_preserves_unicode_decoration_lowercased(string input, string expected)
    {
        Assert.Equal(expected, GroupNormalizer.Normalize(input));
    }
}
