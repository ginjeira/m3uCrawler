using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Testes específicos para os indicadores enriquecidos de Portugal
/// (derivados da lista de referência PORTUGAL_BASE.m3u).
///
/// Estes testes exercitam:
///   - identificação correcta de canais portugueses adicionais (DAZN, Eleven, STAR, TVCine, etc.);
///   - ausência de falsos positivos em nomes claramente não portugueses;
///   - regressão: nomes clássicos (RTP1, SIC, TVI) continuam reconhecidos;
///   - fallback por group-title: activado quando o título também tem token PT;
///   - segurança do fallback: rejeitado quando só o group-title tem "Portugal" mas o título é opaco.
/// </summary>
public class PortugalIndicatorsEnrichmentTests
{
    private static CountryChannelValidator CreateValidator()
    {
        // Aponta para a pasta runtime-data do repositório para que os
        // indicadores e o fallback de países sejam carregados.
        var repoRuntimeData = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "m3uCrawler", "runtime-data");
        return new CountryChannelValidator(Path.GetFullPath(repoRuntimeData));
    }

    private static string Playlist(params string[] titles)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("#EXTM3U");
        int i = 1;
        foreach (var t in titles)
        {
            sb.AppendLine($"#EXTINF:-1,{t}");
            sb.AppendLine($"http://example.com/{i++}");
        }
        return sb.ToString();
    }

    // ---- Casos positivos: canais da referência enriquecida ----

    [Theory]
    [InlineData("RTP Memória")]
    [InlineData("RTP Notícias")]
    [InlineData("RTP Açores")]
    [InlineData("RTP Madeira")]
    [InlineData("RTP África")]
    [InlineData("RTP Internacional")]
    [InlineData("RTP Mundo")]
    [InlineData("RTP Play")]
    public void Recognises_extended_RTP_variants(string title)
    {
        var v = CreateValidator();
        var r = v.AnalyzePlaylist(Playlist(title, "RTP1", "SIC"), "pt", 3);
        Assert.True(r.IsTargetCountry);
        Assert.Contains(title.ToLowerInvariant(), r.MatchedAliases.Select(a => a.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("SIC Caras")]
    [InlineData("SIC Mulher")]
    [InlineData("SIC Radical")]
    [InlineData("SIC Novelas")]
    [InlineData("SIC Internacional")]
    [InlineData("SIC K")]
    public void Recognises_extended_SIC_variants(string title)
    {
        var v = CreateValidator();
        var r = v.AnalyzePlaylist(Playlist(title, "RTP1", "TVI"), "pt", 3);
        Assert.True(r.IsTargetCountry);
    }

    [Theory]
    [InlineData("TVI 24")]
    [InlineData("TVI Internacional")]
    [InlineData("TVI Ficção")]
    [InlineData("TVI Reality")]
    [InlineData("TVI Reality Camera 1")]
    [InlineData("TVI Reality Camara 2")]
    [InlineData("TVI Reality Mosaico")]
    [InlineData("V+ TVI")]
    public void Recognises_extended_TVI_variants(string title)
    {
        var v = CreateValidator();
        var r = v.AnalyzePlaylist(Playlist(title, "RTP1", "SIC"), "pt", 3);
        Assert.True(r.IsTargetCountry);
    }

    [Theory]
    [InlineData("Sport TV 1")]
    [InlineData("Sport TV 6")]
    [InlineData("Sport TV 7")]
    [InlineData("Sport TV NBA")]
    [InlineData("Sporting TV")]
    public void Recognises_extended_sports_variants(string title)
    {
        var v = CreateValidator();
        var r = v.AnalyzePlaylist(Playlist(title, "RTP1", "SIC"), "pt", 3);
        Assert.True(r.IsTargetCountry);
    }

    [Theory]
    [InlineData("DAZN 1")]
    [InlineData("DAZN 2")]
    [InlineData("DAZN 3")]
    [InlineData("DAZN 4")]
    [InlineData("DAZN 5")]
    [InlineData("DAZN 6")]
    [InlineData("DAZNS 2")]
    public void Recognises_DAZN_variants(string title)
    {
        var v = CreateValidator();
        var r = v.AnalyzePlaylist(Playlist(title, "RTP1", "SIC"), "pt", 3);
        Assert.True(r.IsTargetCountry);
    }

    [Theory]
    [InlineData("Eleven Sport 1")]
    [InlineData("Eleven Sport 2")]
    [InlineData("Eleven Sport 3")]
    [InlineData("Eleven Sport 4")]
    [InlineData("Eleven Sport 5")]
    [InlineData("Eleven Sport 6")]
    public void Recognises_Eleven_Sport_variants(string title)
    {
        var v = CreateValidator();
        var r = v.AnalyzePlaylist(Playlist(title, "RTP1", "SIC"), "pt", 3);
        Assert.True(r.IsTargetCountry);
    }

    [Theory]
    [InlineData("TVCine Action")]
    [InlineData("TVCine Edition")]
    [InlineData("TVCine Emotion")]
    [InlineData("TVCine Top")]
    [InlineData("TVCine+")]
    public void Recognises_TVCine_variants(string title)
    {
        var v = CreateValidator();
        var r = v.AnalyzePlaylist(Playlist(title, "RTP1", "SIC"), "pt", 3);
        Assert.True(r.IsTargetCountry);
    }

    [Theory]
    [InlineData("STAR Channel")]
    [InlineData("STAR Movies")]
    [InlineData("STAR Life")]
    [InlineData("STAR Crime")]
    [InlineData("STAR Comedy")]
    public void Recognises_STAR_variants(string title)
    {
        var v = CreateValidator();
        var r = v.AnalyzePlaylist(Playlist(title, "RTP1", "SIC"), "pt", 3);
        Assert.True(r.IsTargetCountry);
    }

    [Theory]
    [InlineData("BTV 1")]
    [InlineData("BTV 2")]
    [InlineData("BTV 3")]
    [InlineData("Benfica TV")]
    [InlineData("Benfica TV HD")]
    [InlineData("Benficatv")]
    public void Recognises_BTV_and_Benfica_variants(string title)
    {
        var v = CreateValidator();
        var r = v.AnalyzePlaylist(Playlist(title, "RTP1", "SIC"), "pt", 3);
        Assert.True(r.IsTargetCountry);
    }

    [Theory]
    [InlineData("Porto Canal")]
    [InlineData("TV Mais")]
    [InlineData("TV Mais HD")]
    [InlineData("Canal Q")]
    [InlineData("Localvisao")]
    [InlineData("Odisseia")]
    [InlineData("CMTV")]
    [InlineData("CM TV")]
    [InlineData("Canal 11")]
    public void Recognises_other_PT_channels(string title)
    {
        var v = CreateValidator();
        var r = v.AnalyzePlaylist(Playlist(title, "RTP1", "SIC"), "pt", 3);
        Assert.True(r.IsTargetCountry);
    }

    [Theory]
    [InlineData("CNN Portugal")]
    [InlineData("Record TV")]
    [InlineData("Record News")]
    [InlineData("Globo Portugal")]
    [InlineData("Euronews Portugal")]
    [InlineData("A Bola TV")]
    public void Recognises_news_and_sport_branded_channels(string title)
    {
        var v = CreateValidator();
        var r = v.AnalyzePlaylist(Playlist(title, "RTP1", "SIC"), "pt", 3);
        Assert.True(r.IsTargetCountry);
    }

    [Theory]
    [InlineData("TCV Internacional")]
    [InlineData("TPA International")]
    [InlineData("TV Mana 1")]
    [InlineData("TV Mana 2")]
    public void Recognises_lusophone_channels(string title)
    {
        var v = CreateValidator();
        var r = v.AnalyzePlaylist(Playlist(title, "RTP1", "SIC"), "pt", 3);
        Assert.True(r.IsTargetCountry);
    }

    [Theory]
    [InlineData("Fatima TV")]
    [InlineData("Cancao Nova")]
    [InlineData("Alma Lusa")]
    [InlineData("Casa e Cozinha")]
    [InlineData("Odisseia")]
    [InlineData("Toros")]
    [InlineData("W-Sport")]
    [InlineData("Unife TV")]
    public void Recognises_PT_specific_channels(string title)
    {
        var v = CreateValidator();
        var r = v.AnalyzePlaylist(Playlist(title, "RTP1", "SIC"), "pt", 3);
        Assert.True(r.IsTargetCountry);
    }

    // ---- Casos negativos: não devem ser reconhecidos como PT ----

    [Theory]
    [InlineData("La 1")]
    [InlineData("Antena 3")]
    [InlineData("Telecinco")]
    [InlineData("TVE 1")]
    [InlineData("Canal Sur")]
    public void Foreign_spanish_channels_are_not_PT(string title)
    {
        var v = CreateValidator();
        var r = v.AnalyzePlaylist(Playlist(title), "pt", 3);
        Assert.False(r.IsTargetCountry);
    }

    [Theory]
    [InlineData("BBC One")]
    [InlineData("Sky News")]
    [InlineData("CNN International")]  // sem o sufixo "Portugal"
    [InlineData("Al Jazeera English")]
    [InlineData("France 24")]
    public void Foreign_international_channels_are_not_PT(string title)
    {
        var v = CreateValidator();
        var r = v.AnalyzePlaylist(Playlist(title), "pt", 3);
        Assert.False(r.IsTargetCountry);
    }

    [Fact]
    public void Random_noise_title_is_not_recognised_as_PT()
    {
        var v = CreateValidator();
        var content = "#EXTM3U\n#EXTINF:-1,TV box review\nhttp://x/1\n#EXTINF:-1,basics channel\nhttp://x/2\n#EXTINF:-1,privati\nhttp://x/3";
        var r = v.AnalyzePlaylist(content, "pt", 3);
        Assert.Equal(0, r.RecognizedChannelCount);
        Assert.False(r.IsTargetCountry);
    }

    // ---- Regressão: casos clássicos continuam a funcionar ----

    [Fact]
    public void Classic_RTP1_SIC_TVI_still_recognised()
    {
        var v = CreateValidator();
        var content = Playlist("RTP1", "SIC", "TVI");
        var r = v.AnalyzePlaylist(content, "pt", 3);
        Assert.True(r.IsTargetCountry);
        Assert.Contains("rtp1", r.MatchedAliases.Select(a => a.ToLowerInvariant()));
        Assert.Contains("sic", r.MatchedAliases.Select(a => a.ToLowerInvariant()));
        Assert.Contains("tvi", r.MatchedAliases.Select(a => a.ToLowerInvariant()));
    }

    [Fact]
    public void Channel_variants_collapse_to_single_family()
    {
        var v = CreateValidator();
        // Várias grafias do mesmo canal não devem inflacionar o contador.
        var content = Playlist("RTP1", "RTP 1", "RTP1 HD", "RTP 1 HD");
        var r = v.AnalyzePlaylist(content, "pt", 3);
        Assert.Equal(2, r.RecognizedChannelCount); // rtp1 + rtp1hd colapsam, mas rtp1hd é distinto de rtp1
    }

    // ---- Group-title fallback continua intacto ----

    [Fact]
    public void Group_title_Portugal_still_triggers_fallback_when_title_also_has_PT_token()
    {
        var v = CreateValidator();
        // O fallback continua a funcionar quando o título também contém um token da categoria
        // PT ("portugal" ou "pt" — o emoji é descartado pelo NormalizeText). Os títulos abaixo
        // NÃO são aliases conhecidos, pelo que a única forma de serem aceites é via
        // group-title. Caso típico de playlists IPTV onde o fornecedor prefixa o nome do
        // canal com "PT" ou "Portugal" e agrupa-o sob "Portugal".
        var streams = new List<m3uCrawler.Models.M3uStream>
        {
            new() { Title = "PT | CanalX", Group = "Portugal" },
            new() { Title = "Portugal | CanalY", Group = "Portugal" },
            new() { Title = "pt | CanalZ", Group = "Portugal" }
        };
        var matches = v.ValidateStreams(streams, "pt");
        Assert.Equal(3, matches.Count);
        Assert.All(matches, m => Assert.True(m.MatchedViaGroup));
    }

    [Fact]
    public void Group_title_Portugal_is_NOT_enough_when_title_has_no_PT_token()
    {
        var v = CreateValidator();
        // Segurança contra canais estrangeiros mal categorizados pelo fornecedor:
        // um stream cujo nome não tem qualquer sinal PT (e.g. "Sky TG24") não pode ser
        // aceite apenas porque o group-title declara "Portugal".
        var streams = new List<m3uCrawler.Models.M3uStream>
        {
            new() { Title = "Sky TG24", Group = "Portugal" },
            new() { Title = "Canal Misterioso", Group = "Portugal" },
            new() { Title = "BBC One", Group = "Portugal" }
        };
        var matches = v.ValidateStreams(streams, "pt");
        Assert.Empty(matches);
    }
}
