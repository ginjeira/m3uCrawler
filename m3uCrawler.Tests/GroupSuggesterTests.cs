using m3uCrawler.Services.Catalog;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Wave D3 — testes de <see cref="GroupSuggester"/>. A sugestão é apenas
/// pré-selecção (taxonomia curada primeiro, heurísticas de tokens depois).
/// </summary>
public class GroupSuggesterTests
{
    // ========================= Taxonomia curada =========================

    [Theory]
    [InlineData("eu | pt | general", CanonicalGroupKeys.PortugalGeneralistas)]
    [InlineData("eu | pt | entretenimento", CanonicalGroupKeys.PortugalEntretenimento)]
    [InlineData("vod | portugal", CanonicalGroupKeys.PortugalFilmesSeries)]
    [InlineData("portugal - canais 24-7", CanonicalGroupKeys.PortugalFilmesSeries)]
    [InlineData("eu | pt | esportes", CanonicalGroupKeys.PortugalDesporto)]
    [InlineData("eu | pt | infantil", CanonicalGroupKeys.PortugalInfantil)]
    [InlineData("eu | pt | documentarios", CanonicalGroupKeys.PortugalDocumentarios)]
    [InlineData("vip | liga portugal betclic", CanonicalGroupKeys.PortugalPPV)]
    public void Taxonomy_hit_maps_to_canonical_group_key(string group, string expected)
    {
        Assert.Equal(expected, GroupSuggester.SuggestGroupKey(group, "Título irrelevante"));
    }

    [Theory]
    [InlineData("eu | france cinema")]
    [InlineData("as | cambodia")]
    [InlineData("eu | belgium")]
    public void Foreign_taxonomy_maps_to_international(string group)
    {
        Assert.Equal(CanonicalGroupKeys.International, GroupSuggester.SuggestGroupKey(group, null));
    }

    // ========================= Fallback por tokens =========================

    [Theory]
    [InlineData("Canal Desporto HD")]
    [InlineData("Sport TV 1")]
    [InlineData("EU Sports")]
    public void Token_desporto_or_sport_maps_to_pt_desporto(string group)
    {
        Assert.Equal(CanonicalGroupKeys.PortugalDesporto, GroupSuggester.SuggestGroupKey(group, null));
    }

    [Theory]
    [InlineData("Canal Infantil")]
    [InlineData("Kids TV")]
    [InlineData("Crianças")]
    public void Token_infantil_kids_criancas_maps_to_pt_infantil(string group)
    {
        Assert.Equal(CanonicalGroupKeys.PortugalInfantil, GroupSuggester.SuggestGroupKey(group, null));
    }

    [Theory]
    [InlineData("Documentários PT")]
    [InlineData("Doc Channel")]
    [InlineData("History HD")]
    public void Token_documentari_doc_history_maps_to_pt_documentarios(string group)
    {
        Assert.Equal(CanonicalGroupKeys.PortugalDocumentarios, GroupSuggester.SuggestGroupKey(group, null));
    }

    [Theory]
    [InlineData("Filmes HD")]
    [InlineData("Séries TV")]
    [InlineData("Series 24/7")]
    [InlineData("VOD Portugal")]
    public void Token_filmes_series_vod_maps_to_pt_filmes_series(string group)
    {
        Assert.Equal(CanonicalGroupKeys.PortugalFilmesSeries, GroupSuggester.SuggestGroupKey(group, null));
    }

    [Theory]
    [InlineData("Notícias 24")]
    [InlineData("News PT")]
    [InlineData("General HD")]
    [InlineData("Generalistas")]
    public void Token_noticias_news_general_maps_to_pt_generalistas(string group)
    {
        Assert.Equal(CanonicalGroupKeys.PortugalGeneralistas, GroupSuggester.SuggestGroupKey(group, null));
    }

    [Fact]
    public void Token_fallback_uses_title_when_group_is_absent()
    {
        Assert.Equal(
            CanonicalGroupKeys.PortugalDocumentarios,
            GroupSuggester.SuggestGroupKey(null, "Canal Documentários PT"));
    }

    // ========================= Sem sugestão =========================

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData(null, "zzz")]
    [InlineData("xyz", null)]
    [InlineData("canal aleatorio", "titulo desconhecido")]
    public void Unknown_input_returns_null(string? group, string? title)
    {
        Assert.Null(GroupSuggester.SuggestGroupKey(group, title));
    }
}
