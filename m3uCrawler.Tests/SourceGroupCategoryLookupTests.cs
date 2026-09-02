using m3uCrawler.Services.Matching;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// TDD tests for <see cref="SourceGroupCategoryLookup"/>.
///
/// See `.kilo/plans/1788214551330-source-group-category-lookup-tdd.md`.
/// </summary>
public class SourceGroupCategoryLookupTests
{
    [Theory]
    [InlineData("eu | pt | general", Category.Live)]
    [InlineData("eu | pt | entretenimento", Category.Entretenimento)]
    [InlineData("eu | pt | filmes e series", Category.Entretenimento)]
    [InlineData("eu | pt | documentarios", Category.Documentarios)]
    [InlineData("eu | pt | infantil", Category.Infantil)]
    [InlineData("eu | pt | esportes", Category.Desporto)]
    [InlineData("portuguese", Category.Live)]
    [InlineData("portugal", Category.Live)]
    [InlineData("sports networks", Category.Desporto)]
    public void Lookup_returns_Category_for_editorial_sourcegroups(string group, Category expected)
    {
        Assert.Equal(expected, SourceGroupCategoryLookup.Lookup(group));
    }

    // Decorativos — não categoria.
    [Theory]
    [InlineData("─ ✧･ﾟ|| portugal vip")]
    [InlineData("─ ✧･ﾟ|| portugal")]
    [InlineData("─ ✧･ﾟ|| portugal sports")]
    [InlineData("─ ✧･ﾟ|| portugal sport vip")]
    public void Lookup_returns_null_for_decorative_sourcegroups(string group)
    {
        Assert.Null(SourceGroupCategoryLookup.Lookup(group));
    }

    // Codec / qualidade — não categoria.
    [Theory]
    [InlineData("portugal hevc")]
    [InlineData("vip | 4k ultra hd")]
    public void Lookup_returns_null_for_codec_quality_sourcegroups(string group)
    {
        Assert.Null(SourceGroupCategoryLookup.Lookup(group));
    }

    // ContentType — não categoria.
    [Theory]
    [InlineData("vod | portugal")]
    [InlineData("portugal - canais 24-7")]
    [InlineData("vip | liga portugal betclic")]
    public void Lookup_returns_null_for_content_type_sourcegroups(string group)
    {
        Assert.Null(SourceGroupCategoryLookup.Lookup(group));
    }

    // Estrangeiros — não categoria.
    [Theory]
    [InlineData("eu | belgium")]
    [InlineData("eu | bulgaria")]
    [InlineData("am | latino")]
    [InlineData("eu | france sports")]
    [InlineData("eu | france cinema")]
    [InlineData("eu | lithuania")]
    [InlineData("eu | se | sport tv ppv")]
    [InlineData("eu | exyu | slovenija")]
    [InlineData("as | cambodia")]
    public void Lookup_returns_null_for_foreign_sourcegroups(string group)
    {
        Assert.Null(SourceGroupCategoryLookup.Lookup(group));
    }

    // Desconhecidos — não categoria.
    [Theory]
    [InlineData("xyz desconhecido")]
    [InlineData("canal novo 2026")]
    [InlineData("foo bar baz")]
    public void Lookup_returns_null_for_unknown_sourcegroups(string group)
    {
        Assert.Null(SourceGroupCategoryLookup.Lookup(group));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void Lookup_returns_null_for_null_or_whitespace(string? group)
    {
        Assert.Null(SourceGroupCategoryLookup.Lookup(group));
    }

    [Theory]
    [InlineData("eu")]
    [InlineData("general")]
    [InlineData("eu | pt")]
    [InlineData("general vip")]
    [InlineData("PORTUGAL")]
    [InlineData("portugal vip")]
    [InlineData("portugal vip vip")]
    [InlineData("eu | pt | general extra")]
    public void Lookup_does_not_use_substring_matching(string group)
    {
        Assert.Null(SourceGroupCategoryLookup.Lookup(group));
    }

    [Theory]
    [InlineData("EU | PT | GENERAL")]
    [InlineData("Eu | pt | general")]
    [InlineData("PORTUGAL")]
    [InlineData("Portuguese")]
    public void Lookup_is_case_sensitive(string group)
    {
        Assert.Null(SourceGroupCategoryLookup.Lookup(group));
    }

    [Theory]
    [InlineData("Portugal")]
    [InlineData("PORTUGAL")]
    [InlineData("  portugal  ")]
    [InlineData("eu   |   pt   |   general")]
    [InlineData("Eu|Pt|General")]
    public void Lookup_does_not_normalize_input(string rawGroup)
    {
        // Caller DEVE chamar GroupNormalizer antes; o lookup não normaliza.
        Assert.Null(SourceGroupCategoryLookup.Lookup(rawGroup));
    }

    [Fact]
    public void Lookup_accepts_normalized_input_from_GroupNormalizer()
    {
        var group = GroupNormalizer.Normalize("Portugal");
        Assert.Equal("portugal", group);
        Assert.Equal(Category.Live, SourceGroupCategoryLookup.Lookup(group));
    }

    [Fact]
    public void Lookup_NBSP_sourcegroup_requires_normalization()
    {
        // Caller não normalizou: NBSP permanece.
        var rawGroup = "EU\u00A0|\u00A0PT\u00A0|\u00A0ESPORTES";
        Assert.Null(SourceGroupCategoryLookup.Lookup(rawGroup));

        // Caller normalizou: lookup devolve Desporto.
        var normalized = GroupNormalizer.Normalize(rawGroup);
        Assert.Equal("eu | pt | esportes", normalized);
        Assert.Equal(Category.Desporto, SourceGroupCategoryLookup.Lookup(normalized));
    }

    [Fact]
    public void Lookup_does_not_decide_ContentType_VOD()
    {
        Assert.Null(SourceGroupCategoryLookup.Lookup("vod | portugal"));
    }

    [Fact]
    public void Lookup_does_not_decide_ContentType_Filmes24_7()
    {
        Assert.Null(SourceGroupCategoryLookup.Lookup("portugal - canais 24-7"));
    }

    [Fact]
    public void Lookup_does_not_decide_ContentType_PPV()
    {
        Assert.Null(SourceGroupCategoryLookup.Lookup("vip | liga portugal betclic"));
    }

    [Theory]
    [InlineData("eu | belgium")]
    [InlineData("eu | bulgaria")]
    [InlineData("eu | se | sport tv ppv")]
    public void Lookup_does_not_decide_Country(string group)
    {
        Assert.Null(SourceGroupCategoryLookup.Lookup(group));
    }

    [Theory]
    [InlineData("eu | pt | general")]
    [InlineData("vod | portugal")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown xyz")]
    public void Lookup_is_pure_and_idempotent(string? group)
    {
        var first = SourceGroupCategoryLookup.Lookup(group);
        var second = SourceGroupCategoryLookup.Lookup(group);
        var third = SourceGroupCategoryLookup.Lookup(group);
        Assert.Equal(first, second);
        Assert.Equal(second, third);
    }

    [Fact]
    public void SourceGroupCategoryLookup_does_not_break_other_components()
    {
        // ChannelCategoryLookup continua a funcionar.
        Assert.Equal(Category.Live, ChannelCategoryLookup.Lookup("rtp 1"));
        Assert.Equal(Category.Desporto, ChannelCategoryLookup.Lookup("sport tv 1"));

        // ChannelNormalizer continua a funcionar.
        Assert.Equal("rtp 1", ChannelNormalizer.Normalize("RTP 1"));

        // ContentTypeDetector continua a funcionar.
        Assert.Equal(ContentType.Live, ContentTypeDetector.Detect("RTP 1", "Portugal"));
        Assert.Equal(ContentType.VOD, ContentTypeDetector.Detect("PT - O Teste - 2026", "VOD | PORTUGAL"));

        // GroupNormalizer continua a funcionar.
        Assert.Equal("portugal", GroupNormalizer.Normalize("Portugal"));
    }
}
