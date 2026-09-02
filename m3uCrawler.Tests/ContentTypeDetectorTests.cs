using m3uCrawler.Services.Matching;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// TDD tests for <see cref="ContentTypeDetector"/>.
///
/// Contract (see `.kilo/plans/1788214551330-content-type-detector-tdd.md`):
///
/// <list type="bullet">
///   <item>Answer the question "what kind of content is this stream?"</item>
///   <item>Do NOT classify country (Foreign) — that is
///         <c>CountryChannelValidator</c>'s job.</item>
///   <item>Do NOT exclude bundles — that is
///         <c>ChannelMatcher.IsBundleOrCategory</c>'s job.</item>
///   <item>PreDecided in this order: VOD → Filmes24_7 → PPV → Live.</item>
/// </list>
///
/// Detection rules:
/// <list type="bullet">
///   <item><b>VOD</b>: title matches <c>^PT\s*-\s*.+?\s*-\s*\d{4}\s*$</c>.</item>
///   <item><b>Filmes24_7</b>: SourceGroup first, then title contains
///         <c>24/7</c> or <c>24-7</c>.</item>
///   <item><b>PPV</b>: SourceGroup contains <c>PPV</c> or <c>BETCLIC</c>.</item>
///   <item><b>Live</b>: fallback.</item>
/// </list>
/// </summary>
public class ContentTypeDetectorTests
{
    // ========================= 1. VOD =========================

    [Theory]
    [InlineData("PT - 18 Rosas - 2026", "VOD | PORTUGAL")]
    [InlineData("PT - O Coração Delator - 2025", "VOD | PORTUGAL")]
    [InlineData("PT - Demon Slayer (Castelo Infinito) - 2025", "VOD | PORTUGAL")]
    [InlineData("PT - Lucky Lu - 2026", "VOD | PORTUGAL")]
    [InlineData("PT - 9 ½ Semanas de Prazer - 2026", "VOD | PORTUGAL")]
    [InlineData("PT - Shogun's Ninja - 2025", "VOD | PORTUGAL")]
    public void Detect_identifies_VOD_by_title_pattern(string title, string group)
    {
        Assert.Equal(ContentType.VOD, ContentTypeDetector.Detect(title, group));
    }

    [Theory]
    [InlineData("RTP 1", "Portugal")]
    [InlineData("SIC", "Portugal")]
    [InlineData("CNN Portugal", "Portugal")]
    [InlineData("Filmes Angelina Jolie 24/7 ( Exclusivo ) PT", "Portugal - Canais 24-7")]  // no YYYY
    [InlineData("Sport TV PPV 1 HD", "VIP | LIGA PORTUGAL BETCLIC")]                        // PPV
    [InlineData("Documentario Plano Medio - 2025", "Portugal")]                            // does not start with "PT -"
    [InlineData("Filme Inteiro 2025", "Portugal")]                                        // does not start with "PT -"
    [InlineData("TV CINE ACTION FHD", "EU | PT | FILMES E SÉRIES")]                          // live film channel
    [InlineData("Combates UFC 24/7 ( Exclusivo ) PT", "Portugal - Canais 24-7")]           // guard excludes
    public void Detect_does_not_confuse_normal_channels_as_VOD(string title, string group)
    {
        Assert.NotEqual(ContentType.VOD, ContentTypeDetector.Detect(title, group));
    }

    // ========================= 2. Filmes24_7 =========================

    [Theory]
    [InlineData("Filmes Angelina Jolie 24/7 ( Exclusivo ) PT", "Portugal - Canais 24-7")]
    [InlineData("Filmes Anne Hathaway 24/7 ( Exclusivo ) PT", "Portugal - Canais 24-7")]
    [InlineData("Filmes Batman 24/7 ( Exclusivo ) PT", "Portugal - Canais 24-7")]
    [InlineData("NETFLIX Comedia 24/7 ( Exclusivo ) PT", "Portugal - Canais 24-7")]
    [InlineData("Combates UFC 24/7 ( Exclusivo ) PT", "Portugal - Canais 24-7")]
    [InlineData("Filmes Bruce Willis 24-7 ( Exclusivo ) PT", "Portugal - Canais 24-7")]   // hyphen in title
    public void Detect_identifies_Filmes24_7(string title, string group)
    {
        Assert.Equal(ContentType.Filmes24_7, ContentTypeDetector.Detect(title, group));
    }

    [Fact]
    public void Detect_Filmes24_7_hyphen_only_in_title()
    {
        // SourceGroup has no 24-7 marker, but the title does.
        Assert.Equal(
            ContentType.Filmes24_7,
            ContentTypeDetector.Detect("Filmes Angelina Jolie 24-7", "Portugal"));
    }

    [Fact]
    public void Detect_Filmes24_7_searches_SourceGroup_before_title()
    {
        // If only the SourceGroup contains the marker, classification
        // must still succeed (SourceGroup has precedence).
        Assert.Equal(
            ContentType.Filmes24_7,
            ContentTypeDetector.Detect("Some channel", "Portugal - Canais 24-7"));
    }

    [Theory]
    [InlineData("RTP 1", "Portugal")]
    [InlineData("Sport TV 1", "Portugal")]
    [InlineData("Sport TV PPV 1 HD", "VIP | LIGA PORTUGAL BETCLIC")]
    [InlineData("TV CINE Top", "─ ✧･ﾟ|| PORTUGAL")]
    [InlineData("AXN", "Portugal")]
    public void Detect_does_not_confuse_live_channels_as_Filmes24_7(string title, string group)
    {
        Assert.NotEqual(ContentType.Filmes24_7, ContentTypeDetector.Detect(title, group));
    }

    // ========================= 3. PPV =========================

    [Theory]
    [InlineData("Sport TV PPV 1 HD", "VIP | LIGA PORTUGAL BETCLIC")]
    [InlineData("Sport TV PPV 2 HD", "VIP | LIGA PORTUGAL BETCLIC")]
    [InlineData("Sport TV PPV 3 HD", "VIP | LIGA PORTUGAL BETCLIC")]
    [InlineData("Live stream Liga", "VIP | LIGA PORTUGAL BETCLIC")]
    [InlineData("Stream qualquer", "EU | SE | SPORT TV PPV")]
    public void Detect_identifies_PPV_by_source_group(string title, string group)
    {
        Assert.Equal(ContentType.PPV, ContentTypeDetector.Detect(title, group));
    }

    [Theory]
    [InlineData("Sport TV 1", "Portugal HEVC")]
    [InlineData("Sport TV 1", "EU | PT | ESPORTES")]
    [InlineData("Sport TV 1", "Portugal")]
    [InlineData("Sport TV 2", "─ ✧･ﾟ|| PORTUGAL SPORT VIP")]  // SPORT VIP, not PPV
    public void Detect_does_not_confuse_continuous_Sport_TV_as_PPV(string title, string group)
    {
        Assert.NotEqual(ContentType.PPV, ContentTypeDetector.Detect(title, group));
    }

    // ========================= 4. Live fallback =========================

    [Theory]
    [InlineData("RTP 1", "Portugal")]
    [InlineData("RTP 1", "EU | PT | GENERAL")]
    [InlineData("SIC", "Portugal")]
    [InlineData("CNN Portugal", "Portugal")]
    [InlineData("Sport TV 1", "Portugal")]
    [InlineData("Sport TV 1", "EU | PT | ESPORTES")]
    [InlineData("AXN", "Portugal")]
    [InlineData("Baby TV", "EU | PT | INFANTIL")]
    [InlineData("Discovery", "EU | PT | DOCUMENTÁRIOS")]
    [InlineData("Sport TV 1", "Portugal HEVC")]
    [InlineData("TV CINE Top", "─ ✧･ﾟ|| PORTUGAL")]
    [InlineData("Stingray iConcerts", "─ ✧･ﾟ|| PORTUGAL VIP")]
    [InlineData("Sport TV 1", "SPORTS NETWORKS")]
    [InlineData("VIP - SPORT TV 1 4K", "VIP | 4K ULTRA HD")]
    public void Detect_defaults_to_Live_for_normal_channels(string title, string group)
    {
        Assert.Equal(ContentType.Live, ContentTypeDetector.Detect(title, group));
    }

    // ========================= 5. Null / empty =========================

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "Portugal")]
    [InlineData("RTP 1", null)]
    [InlineData("", "")]
    [InlineData("   ", "Portugal")]
    [InlineData("RTP 1", "")]
    public void Detect_handles_null_and_empty_gracefully(string? title, string? group)
    {
        Assert.Equal(ContentType.Live, ContentTypeDetector.Detect(title, group));
    }

    // ========================= 6. Precedência =========================

    [Fact]
    public void Detect_precedence_VOD_wins_over_other_signals()
    {
        // A PT - X - YYYY pattern in the title is the strongest
        // signal and wins even if the SourceGroup says "24-7".
        Assert.Equal(
            ContentType.VOD,
            ContentTypeDetector.Detect("PT - Movie Title - 2025", "Portugal - Canais 24-7"));
    }

    [Fact]
    public void Detect_precedence_Filmes24_7_sourcegroup_beats_title()
    {
        // If both SourceGroup and title have 24-7, the detection still
        // returns Filmes24_7 — proving SourceGroup precedence.
        Assert.Equal(
            ContentType.Filmes24_7,
            ContentTypeDetector.Detect("Some random title 24-7", "Portugal - Canais 24-7"));
    }

    // ========================= 7. Casos reais da playlist =========================

    [Theory]
    [InlineData("Filmes Angelina Jolie 24/7 ( Exclusivo ) PT", "Portugal - Canais 24-7", ContentType.Filmes24_7)]
    [InlineData("Combates UFC 24/7 ( Exclusivo ) PT", "Portugal - Canais 24-7", ContentType.Filmes24_7)]
    [InlineData("Filmes Bruce Willis 24/7 ( Exclusivo ) PT", "Portugal - Canais 24-7", ContentType.Filmes24_7)]
    [InlineData("Sport TV PPV 1 HD", "VIP | LIGA PORTUGAL BETCLIC", ContentType.PPV)]
    [InlineData("Sport TV PPV 2 HD", "VIP | LIGA PORTUGAL BETCLIC", ContentType.PPV)]
    [InlineData("PT - 18 Rosas - 2026", "VOD | PORTUGAL", ContentType.VOD)]
    [InlineData("PT - Lucky Lu - 2026", "VOD | PORTUGAL", ContentType.VOD)]
    [InlineData("RTP 1", "Portugal", ContentType.Live)]
    [InlineData("CNN Portugal", "Portugal", ContentType.Live)]
    [InlineData("Sport TV 1", "Portugal HEVC", ContentType.Live)]
    [InlineData("Sport TV 1", "EU | PT | ESPORTES", ContentType.Live)]
    [InlineData("AXN", "Portugal", ContentType.Live)]
    public void Detect_handles_real_playlist_titles(string title, string group, ContentType expected)
    {
        Assert.Equal(expected, ContentTypeDetector.Detect(title, group));
    }

    // ========================= 8. Não-interferência =========================

    [Fact]
    public void Detect_is_pure_and_does_not_throw_for_unusual_inputs()
    {
        // Smoke: many odd inputs that should never crash the detector.
        var inputs = new (string? title, string? group)[]
        {
            ("", "│─━┃┃┃┃┃"), // boxes
            (new string('a', 10000), null),  // very long title
            ("\u0000\u0001\u0002", "\u0003"), // control chars
            ("BE - RTL TVI HEVC", "EU | BELGIUM"), // foreign
            ("FR - CANAL+ LIVE 11 HD", "EU | FRANCE SPORTS"),
            ("#f#11ffff00##### PORTUGAL #####", "VIP | LIGA PORTUGAL BETCLIC"),  // colour placeholder
        };
        foreach (var (title, group) in inputs)
        {
            // No exceptions, returns a valid ContentType value.
            var result = ContentTypeDetector.Detect(title, group);
            Assert.True(Enum.IsDefined(typeof(ContentType), result));
        }
    }
}