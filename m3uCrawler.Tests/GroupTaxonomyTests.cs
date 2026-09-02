using m3uCrawler.Services.Matching;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// TDD tests for <see cref="GroupTaxonomy"/>.
///
/// See `.kilo/plans/1788214551330-group-taxonomy-tdd.md`.
/// </summary>
public class GroupTaxonomyTests
{
    // ========================= Editoriais canónicos =========================

    [Theory]
    [InlineData("eu | pt | general", OutputGroupKind.PortugalLive, Confidence.High)]
    [InlineData("eu | pt | entretenimento", OutputGroupKind.PortugalEntretenimento, Confidence.High)]
    [InlineData("eu | pt | filmes e series", OutputGroupKind.PortugalEntretenimento, Confidence.High)]
    [InlineData("eu | pt | documentarios", OutputGroupKind.PortugalDocumentarios, Confidence.High)]
    [InlineData("eu | pt | infantil", OutputGroupKind.PortugalInfantil, Confidence.High)]
    [InlineData("eu | pt | esportes", OutputGroupKind.PortugalDesporto, Confidence.High)]
    public void Lookup_returns_High_for_canonical_editorial_sourcegroups(
        string group, OutputGroupKind expectedKind, Confidence expectedConfidence)
    {
        var (kind, confidence) = GroupTaxonomy.Lookup(group);
        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedConfidence, confidence);
    }

    // ========================= Genéricos / catch-all =========================

    [Theory]
    [InlineData("portuguese", OutputGroupKind.PortugalLive, Confidence.Low)]
    [InlineData("portugal", OutputGroupKind.PortugalLive, Confidence.Low)]
    [InlineData("sports networks", OutputGroupKind.PortugalDesporto, Confidence.Medium)]
    public void Lookup_returns_correct_confidence_for_generic_sourcegroups(
        string group, OutputGroupKind expectedKind, Confidence expectedConfidence)
    {
        var (kind, confidence) = GroupTaxonomy.Lookup(group);
        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedConfidence, confidence);
    }

    // ========================= Content Type embedded =========================

    [Theory]
    [InlineData("vod | portugal", OutputGroupKind.PortugalVOD, Confidence.High)]
    [InlineData("portugal - canais 24-7", OutputGroupKind.PortugalFilmes24_7, Confidence.High)]
    [InlineData("vip | liga portugal betclic", OutputGroupKind.PortugalPPV, Confidence.High)]
    public void Lookup_returns_High_for_content_type_embedded_sourcegroups(
        string group, OutputGroupKind expectedKind, Confidence expectedConfidence)
    {
        var (kind, confidence) = GroupTaxonomy.Lookup(group);
        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedConfidence, confidence);
    }

    // ========================= Decorativos / codecs / qualidade =========================

    [Theory]
    [InlineData("─ ✧･ﾟ|| portugal", OutputGroupKind.PortugalLive, Confidence.Medium)]
    [InlineData("─ ✧･ﾟ|| portugal vip", OutputGroupKind.PortugalLive, Confidence.Medium)]
    [InlineData("─ ✧･ﾟ|| portugal sports", OutputGroupKind.PortugalDesporto, Confidence.Medium)]
    [InlineData("─ ✧･ﾟ|| portugal sport vip", OutputGroupKind.PortugalDesporto, Confidence.Medium)]
    [InlineData("portugal hevc", OutputGroupKind.PortugalLive, Confidence.Medium)]
    [InlineData("vip | 4k ultra hd", OutputGroupKind.PortugalLive, Confidence.Medium)]
    public void Lookup_returns_Medium_for_decorative_codec_quality_sourcegroups(
        string group, OutputGroupKind expectedKind, Confidence expectedConfidence)
    {
        var (kind, confidence) = GroupTaxonomy.Lookup(group);
        Assert.Equal(expectedKind, kind);
        Assert.Equal(expectedConfidence, confidence);
    }

    // ========================= Foreign =========================

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
    public void Lookup_returns_Foreign_High_for_foreign_sourcegroups(string group)
    {
        var (kind, confidence) = GroupTaxonomy.Lookup(group);
        Assert.Equal(OutputGroupKind.Foreign, kind);
        Assert.Equal(Confidence.High, confidence);
    }

    // ========================= Null / vazio / whitespace =========================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void Lookup_returns_null_High_for_null_or_whitespace(string? group)
    {
        var (kind, confidence) = GroupTaxonomy.Lookup(group);
        Assert.Null(kind);
        Assert.Equal(Confidence.High, confidence);
    }

    // ========================= SourceGroup desconhecido =========================

    [Theory]
    [InlineData("xyz desconhecido")]
    [InlineData("canal novo 2026")]
    [InlineData("foo bar baz")]
    public void Lookup_returns_null_High_for_unknown_sourcegroups(string group)
    {
        var (kind, confidence) = GroupTaxonomy.Lookup(group);
        Assert.Null(kind);
        Assert.Equal(Confidence.High, confidence);
    }

    // ========================= Exact matching (sem substring) =========================

    [Theory]
    [InlineData("eu")]                              // prefix of "eu | pt | general"
    [InlineData("general")]                        // suffix
    [InlineData("eu | pt")]                        // middle
    [InlineData("general vip")]                    // partial
    [InlineData("PORTUGAL")]                       // case (caller nao normalizou)
    [InlineData("portugal vip")]                   // missing decoration
    [InlineData("portugal vip vip")]               // suffix
    [InlineData("eu | pt | general extra")]        // suffix
    [InlineData("vod")]                            // prefix of vod | portugal
    public void Lookup_does_not_use_substring_matching(string group)
    {
        var (kind, _) = GroupTaxonomy.Lookup(group);
        Assert.Null(kind);
    }

    // ========================= Case sensitivity / normalização =========================

    [Theory]
    [InlineData("EU | PT | GENERAL")]
    [InlineData("Eu | pt | general")]
    [InlineData("PORTUGAL")]
    [InlineData("Portuguese")]
    public void Lookup_is_case_sensitive(string group)
    {
        var (kind, confidence) = GroupTaxonomy.Lookup(group);
        Assert.Null(kind);
        Assert.Equal(Confidence.High, confidence);
    }

    [Theory]
    [InlineData("Portugal")]
    [InlineData("PORTUGAL")]
    [InlineData("  portugal  ")]
    [InlineData("eu   |   pt   |   general")]
    [InlineData("Eu|Pt|General")]
    public void Lookup_does_not_normalize_input(string rawGroup)
    {
        var (kind, _) = GroupTaxonomy.Lookup(rawGroup);
        Assert.Null(kind);
    }

    [Fact]
    public void Lookup_accepts_normalized_input_from_GroupNormalizer()
    {
        var group = GroupNormalizer.Normalize("Portugal");
        Assert.Equal("portugal", group);
        var (kind, confidence) = GroupTaxonomy.Lookup(group);
        Assert.Equal(OutputGroupKind.PortugalLive, kind);
        Assert.Equal(Confidence.Low, confidence);
    }

    [Fact]
    public void Lookup_NBSP_sourcegroup_requires_normalization()
    {
        var rawGroup = "EU\u00A0|\u00A0PT\u00A0|\u00A0ESPORTES";
        var (kind, _) = GroupTaxonomy.Lookup(rawGroup);
        Assert.Null(kind);

        var normalized = GroupNormalizer.Normalize(rawGroup);
        Assert.Equal("eu | pt | esportes", normalized);
        var (kindNorm, confNorm) = GroupTaxonomy.Lookup(normalized);
        Assert.Equal(OutputGroupKind.PortugalDesporto, kindNorm);
        Assert.Equal(Confidence.High, confNorm);
    }

    // ========================= Distinção entre GroupTaxonomy e ContentType =========================

    [Fact]
    public void Lookup_vod_portugal_maps_to_PortugalVOD()
    {
        var (kind, _) = GroupTaxonomy.Lookup("vod | portugal");
        Assert.Equal(OutputGroupKind.PortugalVOD, kind);
    }

    [Fact]
    public void Lookup_portugal_canais_24_7_maps_to_PortugalFilmes24_7()
    {
        var (kind, _) = GroupTaxonomy.Lookup("portugal - canais 24-7");
        Assert.Equal(OutputGroupKind.PortugalFilmes24_7, kind);
    }

    [Fact]
    public void Lookup_vip_liga_portugal_betclic_maps_to_PortugalPPV()
    {
        var (kind, _) = GroupTaxonomy.Lookup("vip | liga portugal betclic");
        Assert.Equal(OutputGroupKind.PortugalPPV, kind);
    }

    // ========================= Não-interferência =========================

    [Fact]
    public void GroupTaxonomy_does_not_break_other_components()
    {
        Assert.Equal("portugal", GroupNormalizer.Normalize("Portugal"));
        Assert.Equal(ContentType.Live, ContentTypeDetector.Detect("RTP 1", "Portugal"));
        Assert.Equal(ContentType.VOD, ContentTypeDetector.Detect("PT - O Teste - 2026", "VOD | PORTUGAL"));
        Assert.Equal(Category.Live, ChannelCategoryLookup.Lookup("rtp 1"));
        Assert.Equal(Category.Live, SourceGroupCategoryLookup.Lookup("portugal"));
        Assert.Null(SourceGroupCategoryLookup.Lookup("vod | portugal"));
    }

    // ========================= Idempotência =========================

    [Theory]
    [InlineData("portugal")]
    [InlineData("eu | pt | general")]
    [InlineData("vod | portugal")]
    [InlineData("─ ✧･ﾟ|| portugal vip")]
    [InlineData(null)]
    [InlineData("")]
    public void Lookup_is_pure_and_idempotent(string? group)
    {
        var first = GroupTaxonomy.Lookup(group);
        var second = GroupTaxonomy.Lookup(group);
        var third = GroupTaxonomy.Lookup(group);
        Assert.Equal(first.OutputGroup, second.OutputGroup);
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(second.OutputGroup, third.OutputGroup);
        Assert.Equal(second.Confidence, third.Confidence);
    }

    // ========================= Enum coverage =========================

    [Fact]
    public void OutputGroupKind_enum_has_exactly_nine_values()
    {
        var values = Enum.GetValues<OutputGroupKind>();
        Assert.Equal(9, values.Length);
        Assert.Contains(OutputGroupKind.PortugalLive, values);
        Assert.Contains(OutputGroupKind.PortugalVOD, values);
        Assert.Contains(OutputGroupKind.PortugalFilmes24_7, values);
        Assert.Contains(OutputGroupKind.PortugalEntretenimento, values);
        Assert.Contains(OutputGroupKind.PortugalDesporto, values);
        Assert.Contains(OutputGroupKind.PortugalInfantil, values);
        Assert.Contains(OutputGroupKind.PortugalDocumentarios, values);
        Assert.Contains(OutputGroupKind.PortugalPPV, values);
        Assert.Contains(OutputGroupKind.Foreign, values);
    }

    [Fact]
    public void Confidence_enum_has_exactly_three_values()
    {
        var values = Enum.GetValues<Confidence>();
        Assert.Equal(3, values.Length);
        Assert.Contains(Confidence.High, values);
        Assert.Contains(Confidence.Medium, values);
        Assert.Contains(Confidence.Low, values);
    }
}
