using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Negative evidence genérica de prefixo de região/país estrangeiro no
/// título, no formato <c>&lt;TOKEN&gt;:</c> (ex.: <c>CAR:</c>,
/// <c>EX-YU:</c>, <c>XXX:</c>).
///
/// Motivação (produção): os aliases bare <c>"CANAL 11"</c> e
/// <c>"SPORT TV 1"</c> de <c>runtime-data/countries/pt.json</c> casavam por
/// tokens com títulos estrangeiros <c>"CAR: CANAL 11"</c> (grupo
/// <c>LAME | CARIBBEAN</c>) e <c>"EX-YU: SPORT TV 1"</c> (grupo
/// <c>EURO | EX-YU</c>). O gate tem de rejeitar o prefixo estrangeiro sem
/// remover os aliases legítimos (canais PT).
/// </summary>
public class CountryChannelValidatorForeignRegionPrefixTests
{
    private static CountryChannelValidator CreateValidator()
    {
        // Diretório vazio: força BuildFallbackCountryAliases (inclui
        // "CANAL 11", "SPORT TV 1", "RTP 1", "SIC", "TVI").
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        return new CountryChannelValidator(tempDir);
    }

    private static M3uStream StreamWith(string title, string group = "Portugal")
        => new() { Title = title, Url = "http://x/stream", Group = group, IsWorking = true };

    // ---------------- Prefixo estrangeiro: REJEITADO ----------------

    [Theory]
    [InlineData("CAR: CANAL 11")]
    [InlineData("EX-YU: SPORT TV 1")]
    [InlineData("XXX: RTP 1")]
    [InlineData("car: canal 11")]
    [InlineData("CAR : CANAL 11")]
    [InlineData("ex-yu: sport tv 1")]
    [InlineData("  EX-YU :  SPORT TV 1")]
    public void Foreign_region_prefix_is_rejected_for_PT(string title)
    {
        var v = CreateValidator();
        var matches = v.ValidateStreams(new List<M3uStream> { StreamWith(title) }, "pt");
        Assert.Empty(matches);
    }

    [Fact]
    public void Foreign_group_plus_foreign_prefix_is_rejected()
    {
        var v = CreateValidator();
        var matches = v.ValidateStreams(
            new List<M3uStream> { StreamWith("CAR: CANAL 11", "LAME | CARIBBEAN") }, "pt");
        Assert.Empty(matches);
    }

    [Fact]
    public void Foreign_prefix_beats_bare_alias_match()
    {
        // O alias "CANAL 11" casa por tokens, mas o prefixo estrangeiro
        // precede o match e short-circuita.
        var v = CreateValidator();
        var matches = v.ValidateStreams(
            new List<M3uStream> { StreamWith("EX-YU: SPORT TV 1", "EURO | EX-YU") }, "pt");
        Assert.Empty(matches);
    }

    // ---------------- Sem prefixo / prefixo do país: ACEITE ----------------

    [Theory]
    [InlineData("CANAL 11")]
    [InlineData("SPORT TV 1")]
    [InlineData("RTP 1")]
    [InlineData("PT: RTP 1")]
    [InlineData("pt: rtp 1")]
    [InlineData("PORTUGAL: RTP 1")]
    [InlineData("Portugal : RTP 1")]
    [InlineData("🇵🇹: RTP 1")]
    public void Bare_or_country_prefixed_channel_is_accepted_for_PT(string title)
    {
        var v = CreateValidator();
        var matches = v.ValidateStreams(new List<M3uStream> { StreamWith(title) }, "pt");
        Assert.NotEmpty(matches);
    }

    [Fact]
    public void Multi_word_prefix_that_is_a_country_channel_is_accepted()
    {
        // ":" a meio de um título legítimo (alias "SPORT TV 1") não é
        // tratado como prefixo de região.
        var v = CreateValidator();
        var matches = v.ValidateStreams(
            new List<M3uStream> { StreamWith("Sport TV 1: FHD") }, "pt");
        Assert.NotEmpty(matches);
    }

    [Fact]
    public void Long_word_prefix_is_not_treated_as_region_code()
    {
        var v = CreateValidator();
        var matches = v.ValidateStreams(
            new List<M3uStream> { StreamWith("Hollywood: RTP 1") }, "pt");
        Assert.NotEmpty(matches);
    }

    // ---------------- Predicado estático (W-FEED canonical fallback) ----------------

    [Theory]
    [InlineData("CAR: CANAL 11", "pt", true)]
    [InlineData("EX-YU: SPORT TV 1", "pt", true)]
    [InlineData("XXX: RTP 1", "pt", true)]
    [InlineData("PT: RTP 1", "pt", false)]
    [InlineData("PORTUGAL: RTP 1", "pt", false)]
    [InlineData("CANAL 11", "pt", false)]
    [InlineData("Sport TV 1: FHD", "pt", false)]
    [InlineData("Hollywood: RTP 1", "pt", false)]
    // Comportamento Opção C pré-existente (ISO, separador "-") preservado:
    [InlineData("BE - WFeed Demo 42", "pt", true)]
    [InlineData("BE - WFeed Demo 42", "es", false)]
    public void HasForeignCountryPrefix_predicate(string title, string country, bool expected)
    {
        Assert.Equal(expected, CountryChannelValidator.HasForeignCountryPrefix(title, country));
    }
}
