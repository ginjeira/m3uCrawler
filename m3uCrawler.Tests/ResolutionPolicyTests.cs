using m3uCrawler.Services.Matching;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// TDD tests for <see cref="ResolutionPolicy"/>.
///
/// See `.kilo/plans/1788214551330-resolution-policy-tdd.md`.
/// </summary>
public class ResolutionPolicyTests
{
    // ========================= Foreign confirmado (Prioridade 1) =========================

    [Theory]
    [InlineData("rtp 1", "eu | pt | general", "RTP 1", true, OutputGroupKind.Foreign)]
    [InlineData("sport tv 1", "eu | pt | esportes", "Sport TV 1", true, OutputGroupKind.Foreign)]
    [InlineData("sic", "eu | belgium", "SIC", true, OutputGroupKind.Foreign)]
    [InlineData("cnn portugal", "vod | portugal", "CNN Portugal", true, OutputGroupKind.Foreign)]
    [InlineData(null, "portugal", null, true, OutputGroupKind.Foreign)]
    [InlineData("", "", "", true, OutputGroupKind.Foreign)]
    public void Resolve_returns_Foreign_when_isForeign_true(
        string? identity, string? sourceGroup, string? title,
        bool isForeign, OutputGroupKind expected)
    {
        Assert.Equal(expected, ResolutionPolicy.Resolve(
            identity, sourceGroup, title, isForeign));
    }

    // ========================= ContentType especial override =========================

    [Theory]
    [InlineData("sic", "vod | portugal", "SIC", false, OutputGroupKind.PortugalVOD)]
    [InlineData("sic", "portugal - canais 24-7", "SIC", false, OutputGroupKind.PortugalFilmes24_7)]
    [InlineData("sic", "vip | liga portugal betclic", "SIC", false, OutputGroupKind.PortugalPPV)]
    [InlineData("canal_totalmente_desconhecido", "vod | portugal", "x", false, OutputGroupKind.PortugalVOD)]
    [InlineData("canal_totalmente_desconhecido", "vip | liga portugal betclic", "x", false, OutputGroupKind.PortugalPPV)]
    public void Resolve_ContentType_special_overrides_Category(
        string? identity, string? sourceGroup, string? title,
        bool isForeign, OutputGroupKind expected)
    {
        Assert.Equal(expected, ResolutionPolicy.Resolve(
            identity, sourceGroup, title, isForeign));
    }

    // ========================= Category conhecida (não-Live) tem prioridade =========================

    [Theory]
    [InlineData("24 kitchen", "eu | pt | general", "24 Kitchen", false, OutputGroupKind.PortugalEntretenimento)]
    [InlineData("sport tv 1", "portugal", "Sport TV 1", false, OutputGroupKind.PortugalDesporto)]
    [InlineData("disney channel", "vod | portugal", "Disney Channel", false, OutputGroupKind.PortugalInfantil)]
    [InlineData("discovery", "portugal", "Discovery", false, OutputGroupKind.PortugalDocumentarios)]
    public void Resolve_Category_known_overrides_GroupTaxonomy(
        string? identity, string? sourceGroup, string? title,
        bool isForeign, OutputGroupKind expected)
    {
        Assert.Equal(expected, ResolutionPolicy.Resolve(
            identity, sourceGroup, title, isForeign));
    }

    // ========================= SourceGroupCategory como evidência secundária =========================

    [Fact]
    public void Resolve_uses_SourceGroupCategory_when_ChannelCategory_is_Live_fallback()
    {
        // Canal desconhecido (ChannelCategory devolve Live por fallback)
        // + SourceGroupCategory devolve Documentarios
        // → PortugalDocumentarios.
        Assert.Equal(
            OutputGroupKind.PortugalDocumentarios,
            ResolutionPolicy.Resolve(
                channelIdentity: "canal_totalmente_desconhecido_xyz",
                sourceGroup: "eu | pt | documentarios",
                title: "algum titulo",
                isForeign: false));
    }

    // ========================= GroupTaxonomy como classificação do SourceGroup =========================

    [Fact]
    public void Resolve_uses_GroupTaxonomy_when_ChannelCategory_and_SourceGroupCategory_unknown()
    {
        Assert.Equal(
            OutputGroupKind.PortugalVOD,
            ResolutionPolicy.Resolve(
                channelIdentity: "canal_desconhecido",
                sourceGroup: "vod | portugal",
                title: "algum titulo",
                isForeign: false));
    }

    [Fact]
    public void Resolve_uses_GroupTaxonomy_for_decorative_sourcegroups()
    {
        Assert.Equal(
            OutputGroupKind.PortugalLive,
            ResolutionPolicy.Resolve(
                channelIdentity: "canal_x",
                sourceGroup: "─ ✧･ﾟ|| portugal",
                title: null,
                isForeign: false));
    }

    // ========================= Fallback final =========================

    [Fact]
    public void Resolve_returns_PortugalLive_when_all_dimensions_unknown()
    {
        Assert.Equal(
            OutputGroupKind.PortugalLive,
            ResolutionPolicy.Resolve(
                channelIdentity: null,
                sourceGroup: null,
                title: null,
                isForeign: false));
    }

    [Fact]
    public void Resolve_returns_PortugalLive_when_only_ChannelCategory_is_Live_fallback()
    {
        Assert.Equal(
            OutputGroupKind.PortugalLive,
            ResolutionPolicy.Resolve(
                channelIdentity: "canal_xyz",
                sourceGroup: "xyz desconhecido",
                title: null,
                isForeign: false));
    }

    // ========================= Null e vazio =========================

    [Theory]
    [InlineData(null, null, null, false, OutputGroupKind.PortugalLive)]
    [InlineData("", "", "", false, OutputGroupKind.PortugalLive)]
    [InlineData("   ", "   ", "   ", false, OutputGroupKind.PortugalLive)]
    [InlineData(null, "eu | pt | general", null, false, OutputGroupKind.PortugalLive)]
    [InlineData("rtp 1", null, null, false, OutputGroupKind.PortugalLive)]
    [InlineData("rtp 1", null, "RTP 1", false, OutputGroupKind.PortugalLive)]
    public void Resolve_handles_null_and_whitespace(
        string? identity, string? sourceGroup, string? title,
        bool isForeign, OutputGroupKind expected)
    {
        Assert.Equal(expected, ResolutionPolicy.Resolve(
            identity, sourceGroup, title, isForeign));
    }

    // ========================= Idempotência =========================

    [Theory]
    [InlineData("sport tv 1", "vod | portugal", "Sport TV 1", false)]
    [InlineData("sic", "eu | pt | general", "SIC", false)]
    [InlineData(null, null, null, true)]
    public void Resolve_is_pure_and_idempotent(
        string? identity, string? sourceGroup, string? title, bool isForeign)
    {
        var first = ResolutionPolicy.Resolve(identity, sourceGroup, title, isForeign);
        var second = ResolutionPolicy.Resolve(identity, sourceGroup, title, isForeign);
        var third = ResolutionPolicy.Resolve(identity, sourceGroup, title, isForeign);
        Assert.Equal(first, second);
        Assert.Equal(second, third);
    }

    // ========================= Não-interferência =========================

    [Fact]
    public void ResolutionPolicy_does_not_break_other_components()
    {
        Assert.Equal("portugal", GroupNormalizer.Normalize("Portugal"));
        Assert.Equal(ContentType.Live, ContentTypeDetector.Detect("RTP 1", "Portugal"));
        Assert.Equal(Category.Live, ChannelCategoryLookup.Lookup("rtp 1"));
        Assert.Equal(Category.Live, SourceGroupCategoryLookup.Lookup("portugal"));
        var taxonomy = GroupTaxonomy.Lookup("eu | pt | general");
        Assert.Equal(OutputGroupKind.PortugalLive, taxonomy.OutputGroup);
        Assert.Equal(Confidence.High, taxonomy.Confidence);
    }

    // ========================= Não-decisão duplicada =========================

    [Fact]
    public void ResolutionPolicy_does_not_reimplement_detection()
    {
        // SourceGroups estrangeiros que NÃO estão em GroupTaxonomy
        // (e.g. hipotético) nunca devem produzir Foreign via
        // ResolutionPolicy — Foreign só é decidido por isForeign=true.
        // O caller é responsável por detectar Foreign.
        // Aqui usamos um source group hipotético não-mapeado.
        var result = ResolutionPolicy.Resolve(
            channelIdentity: "canal",
            sourceGroup: "zzz source group hipotetico nunca visto",
            title: "titulo",
            isForeign: false);
        Assert.NotEqual(OutputGroupKind.Foreign, result);
    }
}
