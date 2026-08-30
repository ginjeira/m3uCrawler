using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Testes de regressão diagnosticados em 2026-08-30 a partir do caso real
/// "pesquisa portugal → playlist.m3u contém ABC 10 (KAKE) WICHITA".
///
/// Estes testes NÃO exercem a pipeline `SearchAndTestM3UInTelegramAsync`
/// (que requer Telegram + rede). Em vez disso, fixam o comportamento da
/// API pública do <see cref="CountryChannelValidator"/> que já deveria
/// fornecer a filtragem per-canal.
///
/// Causa raiz documentada:
/// - <see cref="CountryChannelValidator.ValidateStreams"/> (Services/CountryChannelValidator.cs:134)
///   já existe e funciona.
/// - <see cref="CountryChannelValidator.AnalyzePlaylist"/> é per-playlist
///   e aceita playlists inteiras com apenas 3 aliases PT.
/// - O pipeline (Services/TelegramScraperService.cs:186) usa AnalyzePlaylist
///   como aprovação única, ignorando ValidateStreams.
///
/// Estes testes ficam verdes com a API actual; a correcção do pipeline
/// (introduzir ValidateStreams entre parser.Parse e TestStreamsAsync)
/// virá adicionada a esta classe.
/// </summary>
public class CountryChannelPerStreamTests
{
    private static CountryChannelValidator CreateValidator()
    {
        // Diretório vazio -> usa o fallback de canais para "pt" (determinístico e isolado).
        // Semeamos também um channel-indicators.json no directório para o validator
        // carregar os indicadores suplementares (cnn portugal, porto canal, etc.).
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        File.WriteAllText(Path.Combine(tempDir, "channel-indicators.json"), """
        {
          "indicators": [
            "cnn portugal",
            "porto canal",
            "euronews portugal",
            "rtp memoria",
            "rtp madeira"
          ]
        }
        """);

        return new CountryChannelValidator(tempDir);
    }

    private static List<m3uCrawler.Models.M3uStream> StreamsFromContent(string content)
    {
        var parser = new M3uParserService();
        return parser.Parse(content);
    }

    [Fact]
    public void American_channel_ABC_10_KAKE_WICHITA_is_NOT_matched_for_country_pt()
    {
        var validator = CreateValidator();

        var streams = StreamsFromContent(
            "#EXTM3U\n" +
            "#EXTINF:-1,ABC 10 (KAKE) WICHITA\nhttp://x/kake\n");

        var matches = validator.ValidateStreams(streams, "pt");

        Assert.DoesNotContain(matches, m => m.Stream.Title.Contains("KAKE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(matches, m => m.Stream.Title.Contains("WICHITA", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Mixed_playlist_only_returns_PT_streams_as_matches()
    {
        var validator = CreateValidator();

        var content =
            "#EXTM3U\n" +
            "#EXTINF:-1,RTP1\nhttp://x/rtp1\n" +
            "#EXTINF:-1,ABC 10 (KAKE) WICHITA\nhttp://x/kake\n" +
            "#EXTINF:-1,SIC\nhttp://x/sic\n" +
            "#EXTINF:-1,ABC 10 (KXTV) SACRAMENTO\nhttp://x/kxtv\n" +
            "#EXTINF:-1,TVI\nhttp://x/tvi\n" +
            "#EXTINF:-1,CBS 11 (KHOU) HOUSTON\nhttp://x/khou\n" +
            "#EXTINF:-1,PT || JimJam\nhttp://x/jimjam\n";

        var streams = StreamsFromContent(content);
        var matches = validator.ValidateStreams(streams, "pt");

        var titles = matches.Select(m => m.Stream.Title).ToList();

        Assert.Contains(titles, t => t.Contains("RTP1", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(titles, t => t.Contains("SIC", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(titles, t => t.Contains("TVI", StringComparison.OrdinalIgnoreCase));
        // "PT || JimJam" não bate em nenhum alias por tokenização (o token "||" permanece
        // preso). Esta entrada fica fora do filtro per-canal — é uma lacuna conhecida do
        // validator (e é exactamente por isso que o gate per-canal é necessário e não
        // suficiente; também há que enriquecer aliases).

        Assert.DoesNotContain(titles, t => t.Contains("KAKE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, t => t.Contains("KXTV", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(titles, t => t.Contains("KHOU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pure_PT_playlist_matches_all_PT_streams()
    {
        var validator = CreateValidator();

        var content =
            "#EXTM3U\n" +
            "#EXTINF:-1,RTP1\nhttp://x/rtp1\n" +
            "#EXTINF:-1,SIC\nhttp://x/sic\n" +
            "#EXTINF:-1,TVI\nhttp://x/tvi\n";

        var streams = StreamsFromContent(content);
        var matches = validator.ValidateStreams(streams, "pt");

        Assert.Equal(3, matches.Count);
        Assert.Contains(matches, m => m.Stream.Title == "RTP1");
        Assert.Contains(matches, m => m.Stream.Title == "SIC");
        Assert.Contains(matches, m => m.Stream.Title == "TVI");
    }

    [Theory]
    [InlineData("La 1")]
    [InlineData("Antena 3")]
    [InlineData("Telecinco")]
    [InlineData("Globo")]
    [InlineData("CNN")]
    [InlineData("FOX 5 (WNYW) NEW YORK")]
    public void Non_PT_titles_are_NOT_matched_for_country_pt(string title)
    {
        var validator = CreateValidator();

        var streams = StreamsFromContent(
            $"#EXTM3U\n#EXTINF:-1,{title}\nhttp://x/foo\n");

        var matches = validator.ValidateStreams(streams, "pt");

        Assert.Empty(matches);
    }

    [Fact]
    public void Large_mixed_playlist_strictly_filters_to_PT_only()
    {
        var validator = CreateValidator();

        var lines = new List<string> { "#EXTM3U" };
        var usTitles = new[]
        {
            "ABC 10 (KAKE) WICHITA",
            "ABC 10 (KXTV) SACRAMENTO",
            "ABC 13 (KTRK) HOUSTON",
            "CBS 11 (KHOU) HOUSTON",
            "CBS 12 (WKRC) CINCINNATI",
            "FOX 5 (WNYW) NEW YORK",
            "NBC 4 (WNBC) NEW YORK",
            "PBS 12 (WNET) NEW YORK"
        };
        foreach (var t in usTitles)
            lines.Add($"#EXTINF:-1,{t}\nhttp://x/{Guid.NewGuid():N}");

        lines.Add("#EXTINF:-1,RTP1\nhttp://x/pt1");
        lines.Add("#EXTINF:-1,SIC\nhttp://x/pt2");
        lines.Add("#EXTINF:-1,TVI\nhttp://x/pt3");

        var content = string.Join("\n", lines);
        var streams = StreamsFromContent(content);
        var matches = validator.ValidateStreams(streams, "pt");

        Assert.Equal(3, matches.Count);

        foreach (var m in matches)
        {
            var t = m.Stream.Title;
            Assert.False(t.Contains("KAKE"), $"US title '{t}' should not be matched");
            Assert.False(t.Contains("KXTV"), $"US title '{t}' should not be matched");
            Assert.False(t.Contains("KTRK"), $"US title '{t}' should not be matched");
            Assert.False(t.Contains("KHOU"), $"US title '{t}' should not be matched");
            Assert.False(t.Contains("WKRC"), $"US title '{t}' should not be matched");
            Assert.False(t.Contains("WNYW"), $"US title '{t}' should not be matched");
            Assert.False(t.Contains("WNBC"), $"US title '{t}' should not be matched");
            Assert.False(t.Contains("WNET"), $"US title '{t}' should not be matched");
        }
    }

    [Fact]
    public void ValidateStreams_uses_token_containment_so_basics_does_not_match_SIC()
    {
        var validator = CreateValidator();

        var streams = StreamsFromContent(
            "#EXTM3U\n" +
            "#EXTINF:-1,basics\nhttp://x/basics\n" +
            "#EXTINF:-1,atvinew\nhttp://x/atvi\n" +
            "#EXTINF:-1,privati\nhttp://x/priv\n");

        var matches = validator.ValidateStreams(streams, "pt");

        // Semântica unificada com AnalyzePlaylist: token containment.
        // "basics" não pode casar com "sic" só porque contém a substring.
        Assert.Empty(matches);
    }

    [Fact]
    public void ValidateStreams_substring_match_is_NOT_used_even_when_group_or_url_exists()
    {
        var validator = CreateValidator();

        // Mesmo com texto combinado (title + group + url) contendo a substring, a
        // matching agora exige tokens inteiros. Aqui SIC aparece como substring dentro
        // de "basics"; tem de ser rejeitado.
        var streams = StreamsFromContent(
            "#EXTM3U\n" +
            "#EXTINF:-1 tvg-name=\"basics\",basics\nhttp://x/basics\n");

        var matches = validator.ValidateStreams(streams, "pt");

        Assert.Empty(matches);
    }

    [Fact]
    public void Channel_with_group_title_PT_is_matched_when_title_also_belongs_to_aliases()
    {
        var validator = CreateValidator();

        var streams = StreamsFromContent(
            "#EXTM3U\n" +
            "#EXTINF:-1 tvg-name=\"RTP1\" group-title=\"PT\",RTP1\nhttp://x/rtp1\n");

        var matches = validator.ValidateStreams(streams, "pt");

        var rtp1 = Assert.Single(matches);
        Assert.Equal("PT", rtp1.Stream.Group);
        Assert.Contains("RTP1", rtp1.MatchedAliases, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnalyzePlaylist_threshold_3_lets_mixed_playlist_pass_even_with_thousands_of_US_channels()
    {
        var validator = CreateValidator();

        var lines = new List<string> { "#EXTM3U" };
        for (int i = 0; i < 200; i++)
            lines.Add($"#EXTINF:-1,FOX {i} (WNYW) NEW YORK\nhttp://x/us{i}");
        lines.Add("#EXTINF:-1,RTP1\nhttp://x/pt1");
        lines.Add("#EXTINF:-1,SIC\nhttp://x/pt2");
        lines.Add("#EXTINF:-1,TVI\nhttp://x/pt3");

        var content = string.Join("\n", lines);

        var result = validator.AnalyzePlaylist(content, "pt", threshold: 3);

        Assert.True(result.IsTargetCountry,
            "Estado documentado: AnalyzePlaylist aceita a playlist inteira porque RTP1/SIC/TVI ultrapassam o threshold.");
        Assert.Equal(3, result.RecognizedChannelCount);
    }

    // ==============================================================================
    // Casos A–F do plano de testes (2026-08-30).
    // ==============================================================================

    [Fact]
    public void CaseA_Portuguese_channel_known_alias_is_accepted()
    {
        var validator = CreateValidator();
        var streams = StreamsFromContent("#EXTM3U\n#EXTINF:-1,RTP1\nhttp://x/rtp1\n");
        var matches = validator.ValidateStreams(streams, "pt");
        Assert.Single(matches);
        Assert.Equal("RTP1", matches[0].Stream.Title);
        Assert.False(matches[0].MatchedViaGroup);
    }

    [Fact]
    public void CaseB_american_channel_ABC_10_KAKE_WICHITA_is_rejected()
    {
        var validator = CreateValidator();
        var streams = StreamsFromContent("#EXTM3U\n#EXTINF:-1,ABC 10 (KAKE) WICHITA\nhttp://x/kake\n");
        var matches = validator.ValidateStreams(streams, "pt");
        Assert.Empty(matches);
    }

    [Fact]
    public void CaseC_mixed_playlist_keeps_PT_drops_ABC()
    {
        var validator = CreateValidator();

        var content =
            "#EXTM3U\n" +
            "#EXTINF:-1,RTP1\nhttp://x/rtp1\n" +
            "#EXTINF:-1,SIC\nhttp://x/sic\n" +
            "#EXTINF:-1,TVI\nhttp://x/tvi\n" +
            "#EXTINF:-1,ABC 10 (KAKE) WICHITA\nhttp://x/kake\n";

        var streams = StreamsFromContent(content);
        var matches = validator.ValidateStreams(streams, "pt");

        var titles = matches.Select(m => m.Stream.Title).ToList();
        Assert.Equal(3, matches.Count);
        Assert.Contains(titles, t => t.Contains("RTP1"));
        Assert.Contains(titles, t => t.Contains("SIC"));
        Assert.Contains(titles, t => t.Contains("TVI"));
        Assert.DoesNotContain(titles, t => t.Contains("KAKE"));

        // Em paralelo: AnalyzePlaylist aceita a playlist inteira por causa do threshold,
        // mas ValidateStreams rejeita os canais não-PT. Mantém-se a distinção dos dois
        // gates para futura integração controlada na pipeline.
        var analyze = validator.AnalyzePlaylist(content, "pt", threshold: 3);
        Assert.True(analyze.IsTargetCountry);
    }

    [Fact]
    public void CaseD_PT_JimJam_with_group_PT_is_matched_via_group_title_fallback()
    {
        var validator = CreateValidator();
        var streams = StreamsFromContent(
            "#EXTM3U\n" +
            "#EXTINF:-1 tvg-name=\"JimJam\" group-title=\"PT\",PT || JimJam\nhttp://x/jimjam\n");

        var matches = validator.ValidateStreams(streams, "pt");

        var jimjam = Assert.Single(matches);
        Assert.Equal("PT || JimJam", jimjam.Stream.Title);
        Assert.True(jimjam.MatchedViaGroup,
            "Sem alias em pt.json que case por token, a correspondência tem de vir do fallback group-title.");
        Assert.Equal("PT", jimjam.Stream.Group);
    }

    [Fact]
    public void CaseD_PT_JimJam_with_group_Portugal_is_also_matched_via_group_title()
    {
        var validator = CreateValidator();
        var streams = StreamsFromContent(
            "#EXTM3U\n" +
            "#EXTINF:-1 tvg-name=\"JimJam\" group-title=\"Portugal\",PT || JimJam\nhttp://x/jimjam\n");

        var matches = validator.ValidateStreams(streams, "pt");
        var jimjam = Assert.Single(matches);
        Assert.True(jimjam.MatchedViaGroup);
    }

    [Fact]
    public void CaseD_group_title_UNRELATED_to_PT_does_NOT_match_JimJam()
    {
        var validator = CreateValidator();
        // Título sem token PT E group-title irrelevante -> rejeitado (nenhum sinal PT).
        var streams = StreamsFromContent(
            "#EXTM3U\n" +
            "#EXTINF:-1 tvg-name=\"JimJam\" group-title=\"UK || Kids\",JimJam\nhttp://x/jimjam\n");

        var matches = validator.ValidateStreams(streams, "pt");

        Assert.Empty(matches);
    }

    [Fact]
    public void CaseD_group_title_misspelled_country_does_NOT_match()
    {
        var validator = CreateValidator();
        var streams = StreamsFromContent(
            "#EXTM3U\n" +
            "#EXTINF:-1 tvg-name=\"Patati Patata\" group-title=\"PortuGUAL Regional\",Patati Patata\nhttp://x/pp\n");

        var matches = validator.ValidateStreams(streams, "pt");

        // "PortuGUAL" não é o token "portugal": não é uma categoria explícita do país.
        // A semântica do fallback é conservadora: tokens do group têm de casar EXACTAMENTE
        // (case-insensitive) com um dos tokens conhecidos do país.
        Assert.Empty(matches);
    }

    [Fact]
    public void CaseD_group_title_exact_country_token_LOWER_matches_when_title_also_has_PT_token()
    {
        var validator = CreateValidator();
        // O fallback por group-title="portugal" só é activado se o TÍTULO também contiver
        // pelo menos um token da categoria PT (portugal, pt, 🇵🇹). Aqui o título tem "PT"
        // explícito, pelo que o fallback funciona.
        var streams = StreamsFromContent(
            "#EXTM3U\n" +
            "#EXTINF:-1 tvg-name=\"Patati Patata\" group-title=\"portugal regional\",PT | Patati Patata\nhttp://x/pp\n");

        var matches = validator.ValidateStreams(streams, "pt");
        var m = Assert.Single(matches);
        Assert.True(m.MatchedViaGroup);
    }

    [Fact]
    public void CaseD_group_title_PT_alone_is_NOT_enough_when_title_has_no_PT_token()
    {
        // Segurança contra canais estrangeiros mal categorizados: um fornecedor pode
        // colocar group-title="Portugal" num canal cujo nome não tem qualquer sinal PT.
        // Esta é a nova regra: o título tem de conter pelo menos um token PT.
        var validator = CreateValidator();
        var streams = StreamsFromContent(
            "#EXTM3U\n" +
            "#EXTINF:-1 tvg-name=\"Sky TG24\" group-title=\"Portugal\",Sky TG24\nhttp://x/sky\n");

        var matches = validator.ValidateStreams(streams, "pt");
        Assert.Empty(matches);
    }

    [Fact]
    public void CaseD_group_title_UNRELATED_to_PT_does_NOT_match_when_title_also_has_no_PT_token()
    {
        // Título sem token PT E group-title irrelevante -> rejeitado.
        var validator = CreateValidator();
        var streams = StreamsFromContent(
            "#EXTM3U\n" +
            "#EXTINF:-1 tvg-name=\"JimJam\" group-title=\"UK || Kids\",JimJam\nhttp://x/jimjam\n");

        var matches = validator.ValidateStreams(streams, "pt");
        Assert.Empty(matches);
    }

    [Fact]
    public void CaseD_group_title_PT_does_not_match_when_title_has_no_alias_token_either()
    {
        var validator = CreateValidator();
        var streams = StreamsFromContent(
            "#EXTM3U\n" +
            "#EXTINF:-1 tvg-name=\"RandomChannel\" group-title=\"Hollywood Movies\",RandomChannel\nhttp://x/rc\n");

        var matches = validator.ValidateStreams(streams, "pt");
        Assert.Empty(matches);
    }

    [Fact]
    public void CaseD_group_title_RTP1_can_match_when_fallback_used()
    {
        // title "RTP1" bate pelos tokens (canal em pt.json).
        var validator = CreateValidator();
        var streams = StreamsFromContent(
            "#EXTM3U\n" +
            "#EXTINF:-1 tvg-name=\"RTP1\" group-title=\"Portugal\",RTP1\nhttp://x/rtp1\n");

        var matches = validator.ValidateStreams(streams, "pt");
        var m = Assert.Single(matches);

        // Como o título já bate por alias, MatchedViaGroup deve ser false (title ganhou).
        Assert.False(m.MatchedViaGroup);
    }

    [Fact]
    public void CaseE_substring_match_does_not_inflate_recognition()
    {
        var validator = CreateValidator();
        var streams = StreamsFromContent(
            "#EXTM3U\n" +
            "#EXTINF:-1,basic SIC info\nhttp://x/b\n" +
            "#EXTINF:-1,TV not VI\nhttp://x/v\n");

        var matches = validator.ValidateStreams(streams, "pt");

        // "basic SIC info": tokenizado dá "basic", "sic", "info". SIC bate por token.
        // "TV not VI": tokenizado dá "tv", "not", "vi". Nenhum alias combina.
        Assert.Single(matches);
        Assert.Equal("basic SIC info", matches[0].Stream.Title);
    }

    [Fact]
    public void CaseE_substring_match_basics_atvinew_privati_do_not_match()
    {
        var validator = CreateValidator();
        var streams = StreamsFromContent(
            "#EXTM3U\n" +
            "#EXTINF:-1,basics\nhttp://x/b\n" +
            "#EXTINF:-1,atvinew\nhttp://x/v\n" +
            "#EXTINF:-1,privati\nhttp://x/p\n" +
            "#EXTINF:-1,rtpinternational\nhttp://x/rt\n");

        var matches = validator.ValidateStreams(streams, "pt");

        // rtpinternational tokenizado em ["rtpinternational"] não bate em alias algum.
        Assert.Empty(matches);
    }

    [Theory]
    [InlineData("rtp1")]
    [InlineData("RTP1")]
    [InlineData("RTP 1")]
    [InlineData("RTP-1")]
    [InlineData("RTP_1")]
    [InlineData("RTP.1")]
    [InlineData("  RTP  1  ")]
    public void CaseF_normalization_recognizes_RTP1_variants_in_ValidateStreams(string title)
    {
        var validator = CreateValidator();
        var streams = StreamsFromContent($"#EXTM3U\n#EXTINF:-1,{title}\nhttp://x/rtp1\n");
        var matches = validator.ValidateStreams(streams, "pt");
        Assert.NotEmpty(matches);
    }

    [Fact]
    public void CaseF_case_and_punctuation_does_not_break_match()
    {
        var validator = CreateValidator();
        var streams = StreamsFromContent(
            "#EXTM3U\n" +
            "#EXTINF:-1,SiC\nhttp://x/sic\n" +
            "#EXTINF:-1,sic_hd\nhttp://x/sic3\n");

        var matches = validator.ValidateStreams(streams, "pt");

        // Tokenização: "SiC" -> ["sic"]; "sic_hd" -> ["sic", "hd"]. Os dois casam em SIC.
        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void CaseF_intra_word_dots_break_match_strict_tokenization()
    {
        var validator = CreateValidator();
        var streams = StreamsFromContent(
            "#EXTM3U\n" +
            "#EXTINF:-1,S.I.C.\nhttp://x/sic\n");

        // "S.I.C." após NormalizeText vira "S I C" -> ["s", "i", "c"].
        // Estes tokens não compõem nenhum alias PT completo. Decisão documentada:
        // a matching é estrita por tokens. Para tais títulos, o group-title pode ser o
        // sinal (e é verificado em fallback).
        var matches = validator.ValidateStreams(streams, "pt");
        Assert.Empty(matches);
    }

    [Fact]
    public void CaseF_intra_word_dots_match_via_group_title_fallback_when_title_also_has_PT_token()
    {
        var validator = CreateValidator();
        // O título contém "PT" explícito (além de "S.I.C.") — o fallback por group-title é
        // activado porque o título tem pelo menos um token PT. Esta é a nova regra segura:
        // o group-title sozinho nunca basta.
        var streams = StreamsFromContent(
            "#EXTM3U\n" +
            "#EXTINF:-1 tvg-name=\"S.I.C.\" group-title=\"Portugal\",PT | S.I.C.\nhttp://x/sic\n");

        var matches = validator.ValidateStreams(streams, "pt");
        var m = Assert.Single(matches);
        Assert.True(m.MatchedViaGroup);
    }

    [Fact]
    public void CaseF_intra_word_dots_with_only_group_PT_is_NOT_accepted_anymore()
    {
        // Comportamento seguro: um canal cujo título não tem qualquer sinal PT (S.I.C.
        // tokeniza para ["s", "i", "c"]) NÃO é aceite só porque o group-title diz "Portugal".
        var validator = CreateValidator();
        var streams = StreamsFromContent(
            "#EXTM3U\n" +
            "#EXTINF:-1 tvg-name=\"S.I.C.\" group-title=\"Portugal\",S.I.C.\nhttp://x/sic\n");

        var matches = validator.ValidateStreams(streams, "pt");
        Assert.Empty(matches);
    }

    [Fact]
    public void Channel_in_aliases_but_group_title_prevents_match_title_is_still_used()
    {
        var validator = CreateValidator();
        var streams = StreamsFromContent(
            "#EXTM3U\n" +
            "#EXTINF:-1 tvg-name=\"RTP1\" group-title=\"Movies\",RTP1\nhttp://x/rtp1\n");

        var matches = validator.ValidateStreams(streams, "pt");

        // Title is the primary signal — group-title is NOT consulted when the title
        // already produced a match. RTP1 is in pt.json, so title alone matches.
        var m = Assert.Single(matches);
        Assert.False(m.MatchedViaGroup);
    }

    [Fact]
    public void Supplementary_channel_indicators_cnn_portugal_is_matched()
    {
        // channel-indicators.json is loaded as supplementary aliases for "pt".
        var validator = CreateValidator();
        var streams = StreamsFromContent("#EXTM3U\n#EXTINF:-1,CNN PORTUGAL\nhttp://x/cnn\n");
        var matches = validator.ValidateStreams(streams, "pt");
        Assert.NotEmpty(matches);
    }

    [Fact]
    public void Supplementary_channel_indicators_PORTO_CANAL_is_matched()
    {
        var validator = CreateValidator();
        var streams = StreamsFromContent("#EXTM3U\n#EXTINF:-1,PORTO CANAL\nhttp://x/pc\n");
        var matches = validator.ValidateStreams(streams, "pt");
        Assert.NotEmpty(matches);
    }

    [Fact]
    public void Supplementary_indicators_do_not_create_false_positives()
    {
        var validator = CreateValidator();
        var streams = StreamsFromContent("#EXTM3U\n#EXTINF:-1,Globo News\nhttp://x/gn\n");
        var matches = validator.ValidateStreams(streams, "pt");
        Assert.Empty(matches);
    }

    [Fact]
    public void Supplementary_indicators_only_loaded_for_country_pt()
    {
        // "CNN PORTUGAL" não deve ser aceite para "es" (Espanha).
        var validator = CreateValidator();
        var streams = StreamsFromContent("#EXTM3U\n#EXTINF:-1,CNN PORTUGAL\nhttp://x/cnn\n");
        var matches = validator.ValidateStreams(streams, "es");
        Assert.Empty(matches);
    }
}
