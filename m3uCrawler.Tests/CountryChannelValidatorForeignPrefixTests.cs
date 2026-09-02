using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Tests for the Opção C implementation in
/// <see cref="CountryChannelValidator.ValidateStreams"/>:
/// negative evidence from foreign ISO prefixes in the first token of
/// the title.
///
/// See `.kilo/plans/1788214551330-country-validator-investigation.md`
/// (Grupo B — falsos positivos por alias curto partilhado).
///
/// Opção C scope: ONLY Group B (prefix-based foreign entries).
/// Opção B (PT suffix), / D (alias weighting), / E (score model) are
/// out of scope for this iteration.
/// </summary>
public class CountryChannelValidatorForeignPrefixTests
{
    /// <summary>
    /// Indicadores enriquecidos carregados no validator. Cobre os aliases
    /// suplementares (Porto Canal, RTP Memória, RTP África, etc.) que
    /// estão em <c>channel-indicators.json</c> em produção.
    /// </summary>
    private const string PtIndicatorsJson = """
    {
      "indicators": [
        "cnn portugal", "porto canal", "porto canal hd", "euronews portugal",
        "rtp memoria", "rtp memória", "rtp madeira", "rtp acores", "rtp açores", "rtp africa",
        "rtp áfrica",
        "rtp internacional", "rtp noticias", "rtp play", "rtp play hd",
        "rtp n", "rtp 3", "rtp3",
        "tvi 24", "tvi internacional", "tvi ficcao", "tvi reality",
        "tvi reality camera 1", "tvi reality camera 2", "tvi reality camera 3", "tvi reality camera 4",
        "v+ tvi",
        "sport tv 1", "sport tv 2", "sport tv 3", "sport tv 4",
        "sport tv 5", "sport tv 6", "sport tv 7",
        "sport tv nba", "sport tv news", "sport tv +", "sport tv plus",
        "sporttv 1", "sporttv 2", "sporttv 3", "sporttv 4", "sporttv 5",
        "dazn 1", "dazn 2", "dazn 3", "dazn 4", "dazn 5", "dazn 6", "dazns 2",
        "eleven sport 1", "eleven sport 2", "eleven sport 3",
        "eleven sport 4", "eleven sport 5", "eleven sport 6",
        "star channel", "star comedy", "star crime", "star life", "star movies",
        "btv 1", "btv 2", "btv 3", "benfica tv", "benfica tv 1", "benfica tv hd",
        "caca e pesca", "caca vision", "canal 11", "canal nos", "canal q",
        "canal 180", "cancao nova", "casa e cozinha",
        "cmtv", "cnn portugal",
        "combate", "kombat sport", "kuriakos kids", "kuriakos tv",
        "localvisao",
        "record tv", "record tv 1", "record news", "record news hd",
        "tca", "tpa international", "tv mana 1", "tv mana 2",
        "fatimatv", "zap viva", "alma lusa",
        "disney channel", "cartoon network", "nick jr", "nickelodeon",
        "tv cine action", "tv cine edition", "tv cine emotion",
        "tv cine top", "tv cine +"
      ]
    }
    """;

    private static CountryChannelValidator CreateValidator()
    {
        // Seed both the fallback directory and channel-indicators.json
        // so that the regression tests can rely on the same set of
        // indicators used in production.
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "channel-indicators.json"), PtIndicatorsJson);
        return new CountryChannelValidator(tempDir);
    }

    private static M3uStream StreamWithTitleGroup(string title, string group)
        => new() { Title = title, Url = "http://x/stream", Group = group, IsWorking = true };

    private static List<M3uStream> Streams(params M3uStream[] s) => s.ToList();

    // ============== Grupo B — devem ser REJEITADOS para PT ==============

    [Fact]
    public void BE_RTL_TVI_HEVC_with_group_BELGIUM_is_rejected_for_PT()
    {
        var v = CreateValidator();
        var matches = v.ValidateStreams(
            Streams(StreamWithTitleGroup("BE - RTL TVI HEVC", "EU | BELGIUM")), "pt");
        Assert.Empty(matches);
    }

    [Fact]
    public void BE_RTL_TVI_HD_with_group_BELGIUM_is_rejected_for_PT()
    {
        var v = CreateValidator();
        var matches = v.ValidateStreams(
            Streams(StreamWithTitleGroup("BE - RTL TVI HD ◉", "EU | BELGIUM")), "pt");
        Assert.Empty(matches);
    }

    [Fact]
    public void BG_BTV_HD_with_group_BULGARIA_is_rejected_for_PT()
    {
        var v = CreateValidator();
        var matches = v.ValidateStreams(
            Streams(StreamWithTitleGroup("BG - BTV HD", "EU | BULGARIA")), "pt");
        Assert.Empty(matches);
    }

    [Fact]
    public void BG_BTV_ACTION_HD_is_rejected_for_PT()
    {
        var v = CreateValidator();
        var matches = v.ValidateStreams(
            Streams(StreamWithTitleGroup("BG - BTV ACTION HD", "EU | BULGARIA")), "pt");
        Assert.Empty(matches);
    }

    [Fact]
    public void BG_BTV_CINEMA_HD_is_rejected_for_PT()
    {
        var v = CreateValidator();
        var matches = v.ValidateStreams(
            Streams(StreamWithTitleGroup("BG - BTV CINEMA HD", "EU | BULGARIA")), "pt");
        Assert.Empty(matches);
    }

    [Fact]
    public void BG_BTV_COMEDY_HD_is_rejected_for_PT()
    {
        var v = CreateValidator();
        var matches = v.ValidateStreams(
            Streams(StreamWithTitleGroup("BG - BTV COMEDY HD", "EU | BULGARIA")), "pt");
        Assert.Empty(matches);
    }

    [Fact]
    public void BG_BTV_STORY_is_rejected_for_PT()
    {
        var v = CreateValidator();
        var matches = v.ValidateStreams(
            Streams(StreamWithTitleGroup("BG - BTV STORY", "EU | BULGARIA")), "pt");
        Assert.Empty(matches);
    }

    [Fact]
    public void GT_CANAL_11_with_group_LATINO_is_rejected_for_PT()
    {
        var v = CreateValidator();
        var matches = v.ValidateStreams(
            Streams(StreamWithTitleGroup("GT - CANAL 11", "AM | LATINO")), "pt");
        Assert.Empty(matches);
    }

    [Fact]
    public void KH_BTV_NEWS_is_rejected_for_PT()
    {
        var v = CreateValidator();
        var matches = v.ValidateStreams(
            Streams(StreamWithTitleGroup("KH - BTV NEWS", "AS | CAMBODIA")), "pt");
        Assert.Empty(matches);
    }

    [Fact]
    public void SI_SPORT_TV_1_HD_with_group_SLOVENIJA_is_rejected_for_PT()
    {
        // Adicionado após a auditoria de 2026-09-01: o prefixo ISO `si`
        // (Eslovénia) não estava no ForeignCountryPrefixes e deixava
        // escapar esta entrada como PT via alias `SPORT TV` / `SPORT TV 1`.
        var v = CreateValidator();
        var matches = v.ValidateStreams(
            Streams(StreamWithTitleGroup(
                "SI - SPORT TV  1 HD", "EU | EXYU | SLOVENIJA")), "pt");
        Assert.Empty(matches);
    }

    [Fact]
    public void LT_BTV_HD_is_rejected_for_PT()
    {
        var v = CreateValidator();
        var matches = v.ValidateStreams(
            Streams(StreamWithTitleGroup("LT - BTV HD", "EU | LITHUANIA")), "pt");
        Assert.Empty(matches);
    }

    [Fact]
    public void SW_SVENSKBIL_SPORT_TV_PPV_is_rejected_for_PT()
    {
        var v = CreateValidator();
        var matches = v.ValidateStreams(
            Streams(StreamWithTitleGroup(
                "SW - SVENSKBIL SPORT TV PPV 1 :", "EU | SE | SPORT TV PPV")), "pt");
        Assert.Empty(matches);
    }

    [Fact]
    public void UY_CANAL_11_LAS_PIEDRAS_is_rejected_for_PT()
    {
        var v = CreateValidator();
        var matches = v.ValidateStreams(
            Streams(StreamWithTitleGroup(
                "UY - CANAL 11 LAS PIEDRAS", "AM | LATINO")), "pt");
        Assert.Empty(matches);
    }

    [Fact]
    public void UY_CANAL_11_TREINTA_Y_TRES_is_rejected_for_PT()
    {
        var v = CreateValidator();
        var matches = v.ValidateStreams(
            Streams(StreamWithTitleGroup(
                "UY - CANAL 11 TREINTA Y TRES", "AM | LATINO")), "pt");
        Assert.Empty(matches);
    }

    // ============== Regressões PT — devem continuar a ser ACEITES ==============

    [Theory]
    [InlineData("RTP 1")]
    [InlineData("RTP1")]
    [InlineData("SIC")]
    [InlineData("TVI")]
    [InlineData("SPORT TV 1")]
    [InlineData("CNN Portugal")]
    [InlineData("RTP Memória")]
    [InlineData("RTP África")]
    [InlineData("Porto Canal")]
    public void Legitimate_PT_channels_still_accepted_after_Opcao_C(string title)
    {
        var v = CreateValidator();
        var matches = v.ValidateStreams(
            Streams(StreamWithTitleGroup(title, "Portugal")), "pt");
        Assert.NotEmpty(matches);
    }

    // ============== Documentação do Grupo A — NÃO resolvido nesta iteração ==============

    [Fact]
    public void France_24_PT_with_group_Portugal_is_still_accepted_by_Opcao_C()
    {
        // Documenta explicitamente o limite da Opção C. Este caso não
        // é resolvido nesta iteração (pertence ao Grupo A — Opção B).
        // A correcção virá numa iteração separada.
        var v = CreateValidator();
        var matches = v.ValidateStreams(
            Streams(StreamWithTitleGroup("France 24 PT", "Portugal")), "pt");
        Assert.NotEmpty(matches);
        Assert.True(matches[0].MatchedViaGroup,
            "France 24 PT é aceite via fallback group-title (Grupo A — não corrigido nesta iteração).");
    }
}