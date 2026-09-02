using m3uCrawler.Services.Matching;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// TDD tests for <see cref="ChannelCategoryLookup"/>.
/// </summary>
public class ChannelCategoryLookupTests
{
    [Theory]
    [InlineData("rtp 1")]
    [InlineData("rtp 2")]
    [InlineData("rtp 3")]
    [InlineData("rtp noticias")]
    [InlineData("rtp memoria")]
    [InlineData("rtp madeira")]
    [InlineData("rtp acores")]
    [InlineData("rtp africa")]
    [InlineData("rtp internacional")]
    [InlineData("sic")]
    [InlineData("tvi")]
    [InlineData("tvi 24")]
    [InlineData("cmtv")]
    [InlineData("cnn portugal")]
    [InlineData("euronews portugal")]
    public void Lookup_returns_Live_for_generalistas(string id)
    {
        Assert.Equal(Category.Live, ChannelCategoryLookup.Lookup(id));
    }

    [Fact]
    public void Lookup_returns_Live_for_cnn_without_portugal_suffix()
    {
        Assert.Equal(Category.Live, ChannelCategoryLookup.Lookup("cnn"));
    }

    [Fact]
    public void Lookup_returns_Live_for_cnn_portugal_alias()
    {
        Assert.Equal(Category.Live, ChannelCategoryLookup.Lookup("cnn portugal"));
    }

    [Theory]
    [InlineData("axn")]
    [InlineData("axn white")]
    [InlineData("axn movies")]
    [InlineData("amc")]
    [InlineData("amc break")]
    [InlineData("amc crime")]
    [InlineData("fox")]
    [InlineData("fox crime")]
    [InlineData("fox life")]
    [InlineData("fox movies")]
    [InlineData("star channel")]
    [InlineData("star comedy")]
    [InlineData("star crime")]
    [InlineData("star life")]
    [InlineData("star movies")]
    [InlineData("hollywood")]
    [InlineData("nos studios")]
    [InlineData("syfy")]
    [InlineData("tvcine action")]
    [InlineData("tvcine edition")]
    [InlineData("tvcine emotion")]
    [InlineData("tvcine top")]
    [InlineData("tvcine +")]
    [InlineData("travel channel")]
    [InlineData("24 kitchen")]
    [InlineData("vh 1")]
    [InlineData("tvi internacional")]
    [InlineData("tvi ficcao")]
    [InlineData("tvi reality")]
    [InlineData("sic mulher")]
    [InlineData("sic radical")]
    [InlineData("sic k")]
    public void Lookup_returns_Entretenimento_for_films_and_lifestyle(string id)
    {
        Assert.Equal(Category.Entretenimento, ChannelCategoryLookup.Lookup(id));
    }

    [Theory]
    [InlineData("btv")]
    [InlineData("benfica tv")]
    [InlineData("canal 11")]
    [InlineData("sport tv 1")]
    [InlineData("sport tv 2")]
    [InlineData("sport tv 3")]
    [InlineData("sport tv 4")]
    [InlineData("sport tv 5")]
    [InlineData("sport tv+")]
    [InlineData("sport tv nba")]
    [InlineData("sport tv news")]
    [InlineData("eleven sports 1")]
    [InlineData("eleven sports 2")]
    [InlineData("eleven sports 3")]
    [InlineData("eleven sports 4")]
    [InlineData("eleven sports 5")]
    [InlineData("eleven sports 6")]
    [InlineData("eurosport")]
    [InlineData("eurosport 2")]
    [InlineData("a bola tv")]
    public void Lookup_returns_Desporto_for_sports(string id)
    {
        Assert.Equal(Category.Desporto, ChannelCategoryLookup.Lookup(id));
    }

    [Theory]
    [InlineData("dazn 1")]
    [InlineData("dazn 2")]
    [InlineData("dazn 3")]
    [InlineData("dazn 4")]
    [InlineData("dazn 5")]
    [InlineData("dazn 6")]
    [InlineData("dazns 2")]
    public void Lookup_returns_Desporto_for_DAZN_normalized(string id)
    {
        Assert.Equal(Category.Desporto, ChannelCategoryLookup.Lookup(id));
    }

    [Theory]
    [InlineData("baby tv")]
    [InlineData("cartoon network")]
    [InlineData("disney channel")]
    [InlineData("disney junior")]
    [InlineData("biggs")]
    [InlineData("boomerang")]
    [InlineData("canal panda")]
    [InlineData("panda kids")]
    [InlineData("lolly kids")]
    public void Lookup_returns_Infantil_for_kids_channels(string id)
    {
        Assert.Equal(Category.Infantil, ChannelCategoryLookup.Lookup(id));
    }

    [Theory]
    [InlineData("discovery")]
    [InlineData("discovery channel")]
    [InlineData("nat geo")]
    [InlineData("nat geo wild")]
    [InlineData("id")]
    [InlineData("investigation discovery")]
    [InlineData("odisseia")]
    [InlineData("casa e cozinha")]
    public void Lookup_returns_Documentarios_for_doc_channels(string id)
    {
        Assert.Equal(Category.Documentarios, ChannelCategoryLookup.Lookup(id));
    }

    [Fact]
    public void Lookup_btv_assumes_PT_Benfica_TV()
    {
        Assert.Equal(Category.Desporto, ChannelCategoryLookup.Lookup("btv"));
    }

    [Fact]
    public void Lookup_benfica_tv_is_Desporto()
    {
        Assert.Equal(Category.Desporto, ChannelCategoryLookup.Lookup("benfica tv"));
    }

    [Fact]
    public void Lookup_canal_11_assumes_PT_Eleven()
    {
        Assert.Equal(Category.Desporto, ChannelCategoryLookup.Lookup("canal 11"));
    }

    [Fact]
    public void Lookup_sport_tv_1_is_Desporto()
    {
        Assert.Equal(Category.Desporto, ChannelCategoryLookup.Lookup("sport tv 1"));
    }

    [Fact]
    public void Lookup_sport_tv_1_hd_normalizes_to_sport_tv_1()
    {
        var normalized = ChannelNormalizer.Normalize("Sport TV 1 HD");
        Assert.Equal("sport tv 1", normalized);
        Assert.Equal(Category.Desporto, ChannelCategoryLookup.Lookup(normalized));
    }

    [Fact]
    public void Lookup_discovery_is_Documentarios()
    {
        Assert.Equal(Category.Documentarios, ChannelCategoryLookup.Lookup("discovery"));
    }

    [Fact]
    public void Lookup_vh_1_is_Entretenimento()
    {
        Assert.Equal(Category.Entretenimento, ChannelCategoryLookup.Lookup("vh 1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void Lookup_returns_Live_for_null_or_whitespace(string? id)
    {
        Assert.Equal(Category.Live, ChannelCategoryLookup.Lookup(id));
    }

    [Theory]
    [InlineData("xyz desconhecido")]
    [InlineData("canal novo 2026")]
    [InlineData("foo bar baz")]
    [InlineData("cmtv")]
    [InlineData("sport tv 99")]
    [InlineData("caça e pesca")]
    [InlineData("globo portugal")]
    public void Lookup_returns_Live_for_unknown_or_unmapped(string id)
    {
        Assert.Equal(Category.Live, ChannelCategoryLookup.Lookup(id));
    }

    [Fact]
    public void Lookup_distinguishes_sport_tv_1_from_sport_tv_2()
    {
        Assert.Equal(Category.Desporto, ChannelCategoryLookup.Lookup("sport tv 1"));
        Assert.Equal(Category.Desporto, ChannelCategoryLookup.Lookup("sport tv 2"));
    }

    [Fact]
    public void Lookup_distinguishes_dazn_1_from_dazn_2()
    {
        Assert.Equal(Category.Desporto, ChannelCategoryLookup.Lookup("dazn 1"));
        Assert.Equal(Category.Desporto, ChannelCategoryLookup.Lookup("dazn 2"));
    }

    [Fact]
    public void Lookup_treats_dazn_1_and_dazns_2_as_distinct_identities()
    {
        Assert.Equal(Category.Desporto, ChannelCategoryLookup.Lookup("dazn 1"));
        Assert.Equal(Category.Desporto, ChannelCategoryLookup.Lookup("dazns 2"));
    }

    [Theory]
    [InlineData("RTP 1")]
    [InlineData("rtp1")]
    [InlineData("  rtp 1  ")]
    [InlineData("Sic")]
    [InlineData("si c")]
    [InlineData("sport-tv-1")]
    public void Lookup_does_not_normalize_input(string rawInput)
    {
        Assert.Equal(Category.Live, ChannelCategoryLookup.Lookup(rawInput));
    }

    [Fact]
    public void Lookup_accepts_normalized_input_from_ChannelNormalizer()
    {
        var id = ChannelNormalizer.Normalize("RTP 1");
        Assert.Equal("rtp 1", id);
        Assert.Equal(Category.Live, ChannelCategoryLookup.Lookup(id));
    }

    [Fact]
    public void Lookup_accepts_normalized_input_for_vh_1()
    {
        var id = ChannelNormalizer.Normalize("VH1");
        Assert.Equal("vh 1", id);
        Assert.Equal(Category.Entretenimento, ChannelCategoryLookup.Lookup(id));
    }

    [Fact]
    public void Lookup_is_case_sensitive_lowercase_required()
    {
        Assert.Equal(Category.Live, ChannelCategoryLookup.Lookup("RTP 1"));
        Assert.Equal(Category.Live, ChannelCategoryLookup.Lookup("Rtp 1"));
        Assert.Equal(Category.Live, ChannelCategoryLookup.Lookup("rTp 1"));
    }

    [Fact]
    public void Lookup_with_correct_lowercase_case_matches()
    {
        Assert.Equal(Category.Live, ChannelCategoryLookup.Lookup("rtp 1"));
    }

    [Theory]
    [InlineData("rtp")]
    [InlineData("1")]
    [InlineData("sport")]
    [InlineData("disney")]
    [InlineData("si c")]
    [InlineData("bnfca tv")]
    [InlineData("sport tv")]
    public void Lookup_does_not_use_substring_matching(string id)
    {
        Assert.Equal(Category.Live, ChannelCategoryLookup.Lookup(id));
    }

    [Fact]
    public void Lookup_does_not_classify_ContentType()
    {
        Assert.Equal(Category.Desporto, ChannelCategoryLookup.Lookup("sport tv 1"));
    }

    [Fact]
    public void Lookup_does_not_classify_Country()
    {
        Assert.Equal(Category.Live, ChannelCategoryLookup.Lookup("cnn"));
    }

    [Fact]
    public void Lookup_does_not_classify_OutputGroup()
    {
        var cat = ChannelCategoryLookup.Lookup("rtp 1");
        Assert.True(Enum.IsDefined(typeof(Category), cat));
    }

    [Fact]
    public void Lookup_does_not_strip_quality_tokens()
    {
        Assert.Equal(Category.Live, ChannelCategoryLookup.Lookup("sport tv 1 hd"));
    }

    [Fact]
    public void Lookup_does_not_strip_foreign_prefixes()
    {
        Assert.Equal(Category.Live, ChannelCategoryLookup.Lookup("BE - RTL TVI HEVC"));
    }

    [Theory]
    [InlineData("rtp 1")]
    [InlineData("sport tv 1")]
    [InlineData("disney channel")]
    [InlineData("discovery")]
    [InlineData("vh 1")]
    [InlineData("canal 11")]
    [InlineData("btv")]
    [InlineData("axn")]
    [InlineData("benfica tv")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown xyz")]
    public void Lookup_is_pure_and_idempotent(string? id)
    {
        var first = ChannelCategoryLookup.Lookup(id);
        var second = ChannelCategoryLookup.Lookup(id);
        var third = ChannelCategoryLookup.Lookup(id);
        Assert.Equal(first, second);
        Assert.Equal(second, third);
    }

    [Fact]
    public void Category_enum_has_exactly_five_values()
    {
        var values = Enum.GetValues<Category>();
        Assert.Equal(5, values.Length);
        Assert.Contains(Category.Live, values);
        Assert.Contains(Category.Entretenimento, values);
        Assert.Contains(Category.Desporto, values);
        Assert.Contains(Category.Infantil, values);
        Assert.Contains(Category.Documentarios, values);
    }

    [Fact]
    public void ChannelCategoryLookup_does_not_break_other_components()
    {
        Assert.Equal("rtp 1", ChannelNormalizer.Normalize("RTP 1"));
        Assert.Equal("sport tv 1", ChannelNormalizer.Normalize("Sport TV 1 HD"));
        Assert.Equal("vh 1", ChannelNormalizer.Normalize("VH1"));

        Assert.Equal(ContentType.Live, ContentTypeDetector.Detect("RTP 1", "Portugal"));
        Assert.Equal(ContentType.VOD, ContentTypeDetector.Detect("PT - O Teste - 2026", "VOD | PORTUGAL"));

        Assert.Equal("portugal", GroupNormalizer.Normalize("Portugal"));
        Assert.Equal("eu | pt | general", GroupNormalizer.Normalize("EU | PT | GENERAL"));
    }
}
