using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Testes do filtro per-stream integrado no pipeline de descoberta em 2026-08-30.
/// Estes testes exercem o helper interno `FilterStreamsByCountry` para confirmar
/// que apenas streams pertencentes ao país-alvo passam para `TestStreamsAsync`,
/// sem tocar em rede.
///
/// Caso de regressão obrigatório (do enunciado original):
///   query = "portugal"
///   playlist contém RTP1, SIC, TVI, ABC 10 (KAKE) WICHITA, e outros canais US
///   ⇒ RTP1/SIC/TVI prosseguem; ABC 10 (KAKE) WICHITA NÃO chega a TestStreamsAsync.
/// </summary>
public class TelegramPipelineCountryFilterTests
{
    private static CountryChannelValidator CreateValidator(string seedIndicatorsJson = "{}")
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "channel-indicators.json"), seedIndicatorsJson);
        return new CountryChannelValidator(tempDir);
    }

    private const string BalancedIndicators = """
    {
      "indicators": ["cnn portugal", "porto canal", "euronews portugal"]
    }
    """;

    private static List<M3uStream> StreamsFromContent(string content)
    {
        var parser = new M3uParserService();
        return parser.Parse(content);
    }

    [Fact]
    public void CaseA_Regression_ABC_10_KAKE_WICHITA_is_excluded_while_RTP1_SIC_TVI_are_kept()
    {
        // Caso de regressão exacto do enunciado de 2026-08-30.
        var validator = CreateValidator(BalancedIndicators);

        var content =
            "#EXTM3U\n" +
            "#EXTINF:-1,RTP1\nhttp://x/rtp1\n" +
            "#EXTINF:-1,SIC\nhttp://x/sic\n" +
            "#EXTINF:-1,TVI\nhttp://x/tvi\n" +
            "#EXTINF:-1,ABC 10 (KAKE) WICHITA\nhttp://x/kake\n" +
            "#EXTINF:-1,ABC 10 (KXTV) SACRAMENTO\nhttp://x/kxtv\n" +
            "#EXTINF:-1,CBS 11 (KHOU) HOUSTON\nhttp://x/khou\n" +
            "#EXTINF:-1,FOX 5 (WNYW) NEW YORK\nhttp://x/wnyw\n" +
            "#EXTINF:-1,NBC 4 (WNBC) NEW YORK\nhttp://x/wnbc\n";

        var streams = StreamsFromContent(content);
        var (accepted, rejected) = TelegramScraperService.FilterStreamsByCountry(
            validator, streams, countryCode: "pt");

        var acceptedTitles = accepted.Select(s => s.Title).ToList();

        Assert.Equal(3, accepted.Count);
        Assert.Contains(acceptedTitles, t => t == "RTP1");
        Assert.Contains(acceptedTitles, t => t == "SIC");
        Assert.Contains(acceptedTitles, t => t == "TVI");

        Assert.DoesNotContain(acceptedTitles, t => t.Contains("KAKE"));
        Assert.DoesNotContain(acceptedTitles, t => t.Contains("KXTV"));
        Assert.DoesNotContain(acceptedTitles, t => t.Contains("KHOU"));
        Assert.DoesNotContain(acceptedTitles, t => t.Contains("WNYW"));
        Assert.DoesNotContain(acceptedTitles, t => t.Contains("WNBC"));

        Assert.Equal(5, rejected);
    }

    [Fact]
    public void CaseInverse_pure_PT_playlist_returns_all_PT_streams()
    {
        var validator = CreateValidator(BalancedIndicators);

        var content =
            "#EXTM3U\n" +
            "#EXTINF:-1,RTP1\nhttp://x/rtp1\n" +
            "#EXTINF:-1,SIC\nhttp://x/sic\n" +
            "#EXTINF:-1,TVI\nhttp://x/tvi\n";

        var streams = StreamsFromContent(content);
        var (accepted, rejected) = TelegramScraperService.FilterStreamsByCountry(
            validator, streams, "pt");

        Assert.Equal(3, accepted.Count);
        Assert.Equal(0, rejected);
    }

    [Fact]
    public void Non_PT_playlist_returns_empty_and_zero_accepted()
    {
        var validator = CreateValidator(BalancedIndicators);

        var content =
            "#EXTM3U\n" +
            "#EXTINF:-1,La 1\nhttp://x/la1\n" +
            "#EXTINF:-1,Antena 3\nhttp://x/a3\n";

        var streams = StreamsFromContent(content);
        var (accepted, rejected) = TelegramScraperService.FilterStreamsByCountry(
            validator, streams, "pt");

        Assert.Empty(accepted);
        Assert.Equal(2, rejected);
    }

    [Fact]
    public void Rejected_counter_equals_total_minus_accepted()
    {
        var validator = CreateValidator(BalancedIndicators);

        var content =
            "#EXTM3U\n" +
            "#EXTINF:-1,RTP1\nhttp://x/rtp1\n" +
            "#EXTINF:-1,SIC\nhttp://x/sic\n" +
            "#EXTINF:-1,TVI\nhttp://x/tvi\n" +
            "#EXTINF:-1,ABC 10 (KAKE) WICHITA\nhttp://x/kake\n" +
            "#EXTINF:-1,NBC 4 (WNBC) NEW YORK\nhttp://x/wnbc\n";

        var streams = StreamsFromContent(content);
        var (accepted, rejected) = TelegramScraperService.FilterStreamsByCountry(
            validator, streams, "pt");

        Assert.Equal(streams.Count, accepted.Count + rejected);
    }

    [Fact]
    public void Filter_preserves_stream_identity_no_copies()
    {
        // Demonstrar que as referências dos streams originais são preservadas: o
        // pipeline não deve clonar instâncias, pois isso quebraria OriginalExtInf, etc.
        var validator = CreateValidator(BalancedIndicators);

        var content =
            "#EXTM3U\n" +
            "#EXTINF:-1,RTP1\nhttp://x/rtp1\n";

        var streams = StreamsFromContent(content);
        var originalRtp1 = streams[0];

        var (accepted, _) = TelegramScraperService.FilterStreamsByCountry(
            validator, streams, "pt");

        Assert.Same(originalRtp1, accepted[0]);
    }

    [Fact]
    public void Filter_preserves_metadata_OriginalExtInf_Title_Group_Logo_Url()
    {
        var validator = CreateValidator(BalancedIndicators);

        var content =
            "#EXTM3U\n" +
            "#EXTINF:-1 tvg-name=\"RTP1\" tvg-logo=\"http://l/rtp.png\" group-title=\"Portugal\",RTP1\n" +
            "http://x/rtp1\n";

        var streams = StreamsFromContent(content);
        var (accepted, _) = TelegramScraperService.FilterStreamsByCountry(
            validator, streams, "pt");

        var s = Assert.Single(accepted);
        Assert.Equal("RTP1", s.Title);
        Assert.Equal("Portugal", s.Group);
        Assert.Equal("http://l/rtp.png", s.Logo);
        Assert.Equal("http://x/rtp1", s.Url);
        Assert.Contains("RTP1", s.OriginalExtInf);
    }

    [Fact]
    public void Group_title_fallback_accepts_PT_JimJam_under_PT_category()
    {
        // O filtro aceita o canal cujo título é "PT || JimJam" (não bate em nenhum
        // alias conhecido) apenas porque o group-title declara a categoria "PT".
        var validator = CreateValidator(BalancedIndicators);

        var content =
            "#EXTM3U\n" +
            "#EXTINF:-1 tvg-name=\"JimJam\" group-title=\"PT\",PT || JimJam\nhttp://x/jimjam\n";

        var streams = StreamsFromContent(content);
        var (accepted, _) = TelegramScraperService.FilterStreamsByCountry(
            validator, streams, "pt");

        var s = Assert.Single(accepted);
        Assert.Equal("PT || JimJam", s.Title);
        Assert.Equal("PT", s.Group);
    }

    [Fact]
    public void Large_mixed_playlist_filters_out_every_US_stream()
    {
        var validator = CreateValidator(BalancedIndicators);

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

        var streams = StreamsFromContent(string.Join("\n", lines));
        var (accepted, rejected) = TelegramScraperService.FilterStreamsByCountry(
            validator, streams, "pt");

        Assert.Equal(3, accepted.Count);
        Assert.Equal(8, rejected);
        Assert.DoesNotContain(accepted.Select(a => a.Title), t => t.Contains("ABC"));
        Assert.DoesNotContain(accepted.Select(a => a.Title), t => t.Contains("CBS"));
        Assert.DoesNotContain(accepted.Select(a => a.Title), t => t.Contains("FOX"));
        Assert.DoesNotContain(accepted.Select(a => a.Title), t => t.Contains("NBC"));
        Assert.DoesNotContain(accepted.Select(a => a.Title), t => t.Contains("PBS"));
    }

    [Fact]
    public void Substring_overlap_does_NOT_promote_non_PT_into_filter()
    {
        // "basics" não pode entrar como SIC; "atvinew" não pode entrar como TVI;
        // "privati" não pode entrar como RTP1 — mesmo quando a fonte é uma playlist
        // enorme onde esses tokens aparecem acidentalmente.
        var validator = CreateValidator(BalancedIndicators);

        var content =
            "#EXTM3U\n" +
            "#EXTINF:-1,basics\nhttp://x/b\n" +
            "#EXTINF:-1,atvinew\nhttp://x/a\n" +
            "#EXTINF:-1,privati\nhttp://x/p\n";

        var streams = StreamsFromContent(content);
        var (accepted, _) = TelegramScraperService.FilterStreamsByCountry(
            validator, streams, "pt");

        Assert.Empty(accepted);
    }

    [Fact]
    public void Filter_for_country_es_only_returns_es_streams()
    {
        // O filtro é isolado por país; "pt" e "es" têm listas distintas.
        var validator = CreateValidator(BalancedIndicators);

        var content =
            "#EXTM3U\n" +
            "#EXTINF:-1,La 1\nhttp://x/la1\n" +
            "#EXTINF:-1,Antena 3\nhttp://x/a3\n" +
            "#EXTINF:-1,RTP1\nhttp://x/rtp1\n";

        var streams = StreamsFromContent(content);
        var (accepted, _) = TelegramScraperService.FilterStreamsByCountry(
            validator, streams, "es");

        var titles = accepted.Select(s => s.Title).ToList();
        Assert.Equal(2, accepted.Count);
        Assert.Contains(titles, t => t == "La 1");
        Assert.Contains(titles, t => t == "Antena 3");
        Assert.DoesNotContain(titles, t => t == "RTP1");
    }

    [Fact]
    public void Filter_with_empty_input_returns_empty_accepted_and_zero_rejected()
    {
        var validator = CreateValidator(BalancedIndicators);
        var (accepted, rejected) = TelegramScraperService.FilterStreamsByCountry(
            validator, new List<M3uStream>(), "pt");
        Assert.Empty(accepted);
        Assert.Equal(0, rejected);
    }

    [Fact]
    public void RunReport_new_fields_default_to_zero_for_backward_compatibility()
    {
        // Os campos aditivos StreamsAfterCountryFilter e StreamsRejectedByCountry
        // devem existir e default a 0 para retrocompatibilidade do telegram_run_report.json.
        var rep = new RunReport();

        Assert.Equal(0, rep.StreamsAfterCountryFilter);
        Assert.Equal(0, rep.StreamsRejectedByCountry);
    }

    [Fact]
    public void DiscoveredPlaylist_StreamsAfterCountryFilter_defaults_to_zero()
    {
        var dp = new DiscoveredPlaylist();

        Assert.Equal(0, dp.StreamsAfterCountryFilter);
    }
}
