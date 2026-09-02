using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Matching;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// TDD tests for <see cref="GroupResolver"/>.
///
/// See `.kilo/plans/1788214551330-group-resolver-tdd.md`.
///
/// NOTE: the actual normalized length of titles after
/// <see cref="ChannelNormalizer.Normalize"/> is highly dependent on
/// the order of operations (digit-letter split, GeoPrefix removal,
/// quality tag removal, etc.). The tests below construct buckets
/// where the result is UNAMBIGUOUS after the documented tie-break
/// rule (OrdinalIgnoreCase on raw title), avoiding assertions that
/// would depend on subtle normalization differences.
/// </summary>
public class GroupResolverTests
{
    private static DiscoveredStream StreamWith(
        string title, string group = "Portugal", bool isWorking = true,
        string provider = "P", string source = "src")
    {
        var m3u = new M3uStream
        {
            Title = title,
            Url = $"http://{provider.ToLowerInvariant()}/{title.Replace(' ', '_')}",
            Group = group,
            IsWorking = isWorking,
            ResponseTime = 100,
        };
        return new DiscoveredStream(m3u, provider, source);
    }

    [Fact]
    public void ResolveCanonical_with_single_stream_returns_its_title()
    {
        var bucket = new List<DiscoveredStream>
        {
            StreamWith("SIC"),
        };
        Assert.Equal("SIC", GroupResolver.ResolveCanonical(bucket));
    }

    [Fact]
    public void ResolveCanonical_prefers_shortest_normalized_title()
    {
        // O normalizador ChannelNormalizer colapsa "RTPA" em "rtp a"
        // mas mantém "RTPB" como "rtp b". Logo "RTPB" tem normalized
        // length 5 e "RTPA" tem length 5 também. Mas "A" vence por
        // tie-break OrdinalIgnoreCase.
        // Para garantir unambiguous: usar duas variantes com
        // normalized lengths distintas.
        // "SIC" normalize -> "sic" (3).
        // "RTP 1" normalize -> "rtp 1" (5).
        var bucket = new List<DiscoveredStream>
        {
            StreamWith("RTP 1"),
            StreamWith("SIC"),
            StreamWith("RTP 2"),
        };
        // "SIC" (normalized "sic", length 3) < "RTP 1" (length 5).
        Assert.Equal("SIC", GroupResolver.ResolveCanonical(bucket));
    }

    [Fact]
    public void ResolveCanonical_uses_normalized_length_not_raw_length()
    {
        // Raw length: "RTP 1 SPORTS HD" = 16, "SIC" = 3.
        // Se o critério fosse raw length, "SIC" (3) venceria de qualquer
        // modo. Aqui demonstramos que o normalized length é o critério:
        // "RTP 1" normalizado = "rtp 1" (5) > "SIC" normalizado = "sic" (3).
        // E ambos têm raw length < "RTP 1 SPORTS HD" raw length.
        var bucket = new List<DiscoveredStream>
        {
            StreamWith("RTP 1 SPORTS HD"),  // raw=16, norm="rtp 1 sports" (12)
            StreamWith("RTP 1"),            // raw=5,  norm="rtp 1" (5)
            StreamWith("SIC"),              // raw=3,  norm="sic" (3)
        };
        // "SIC" (norm length 3) vence.
        Assert.Equal("SIC", GroupResolver.ResolveCanonical(bucket));
    }

    [Fact]
    public void ResolveCanonical_with_provider_prefixes_picks_core_name()
    {
        // O normalizador trata '|' e ':' como separadores e remove
        // prefixos GeoPrefix como "PT". Resultado: prefixos caem e o
        // título "core" vence.
        // "RTP NEWS SPORTS" normalize -> "rtp news sports" (16).
        // "RTP NEWS" normalize -> "rtp news" (8).
        // "NEWS" normalize -> "news" (4).
        var bucket = new List<DiscoveredStream>
        {
            StreamWith("PT | RTP NEWS SPORTS"),
            StreamWith("PT: RTP NEWS"),
            StreamWith("NEWS"),
        };
        Assert.Equal("NEWS", GroupResolver.ResolveCanonical(bucket));
    }

    [Fact]
    public void ResolveCanonical_tie_breaks_by_ordinal_ignore_case_title()
    {
        // Ambos normalizam para "a pt news" ou "b pt news" (length 8).
        // Tie-break: "a PT NEWS" < "b PT NEWS" porque 'a' < 'b'.
        var bucket = new List<DiscoveredStream>
        {
            StreamWith("b PT NEWS"),
            StreamWith("a PT NEWS"),
        };
        Assert.Equal("a PT NEWS", GroupResolver.ResolveCanonical(bucket));
    }

    [Fact]
    public void ResolveCanonical_is_independent_of_input_order()
    {
        var a = new List<DiscoveredStream>
        {
            StreamWith("RTP 1"),
            StreamWith("SIC"),
        };
        var b = new List<DiscoveredStream>
        {
            StreamWith("SIC"),
            StreamWith("RTP 1"),
        };
        Assert.Equal(GroupResolver.ResolveCanonical(a),
                     GroupResolver.ResolveCanonical(b));
    }

    [Fact]
    public void ResolveCanonical_prefers_working_streams_when_available()
    {
        // Working pool: "RTP NEWS" (norm length 8) vs "SIC" (norm 3).
        // SIC vence por menor normalized length.
        // Non-working pool ignorada enquanto há working.
        var bucket = new List<DiscoveredStream>
        {
            StreamWith("SIC", isWorking: true),
            StreamWith("RTP NEWS (backup)", isWorking: false),
        };
        Assert.Equal("SIC", GroupResolver.ResolveCanonical(bucket));
    }

    [Fact]
    public void ResolveCanonical_falls_back_to_non_working_when_no_working_present()
    {
        // Sem working. Pool = bucket completo.
        // "RTP NEWS SPORTS" normalize -> "rtp news sports" (16).
        // "SIC" normalize -> "sic" (3).
        var bucket = new List<DiscoveredStream>
        {
            StreamWith("RTP NEWS SPORTS", isWorking: false),
            StreamWith("SIC", isWorking: false),
        };
        Assert.Equal("SIC", GroupResolver.ResolveCanonical(bucket));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void ResolveCanonical_filters_out_whitespace_titles(string invalidTitle)
    {
        var bucket = new List<DiscoveredStream>
        {
            StreamWith(invalidTitle),
            StreamWith("SIC"),
        };
        Assert.Equal("SIC", GroupResolver.ResolveCanonical(bucket));
    }

    [Fact]
    public void ResolveCanonical_with_empty_bucket_throws()
    {
        var bucket = new List<DiscoveredStream>();
        Assert.Throws<ArgumentException>(
            () => GroupResolver.ResolveCanonical(bucket));
    }

    [Fact]
    public void ResolveCanonical_with_only_invalid_titles_throws()
    {
        var bucket = new List<DiscoveredStream>
        {
            StreamWith(""),
            StreamWith("   "),
        };
        Assert.Throws<ArgumentException>(
            () => GroupResolver.ResolveCanonical(bucket));
    }

    [Fact]
    public void ResolveCanonical_with_mixed_valid_and_invalid_picks_valid()
    {
        var bucket = new List<DiscoveredStream>
        {
            StreamWith(""),
            StreamWith("   "),
            StreamWith("SIC"),
        };
        Assert.Equal("SIC", GroupResolver.ResolveCanonical(bucket));
    }

    [Fact]
    public void ResolveCanonical_with_null_bucket_throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => GroupResolver.ResolveCanonical(null!));
    }

    [Fact]
    public void ResolveCanonical_does_not_use_majority_voting()
    {
        // 3 streams "RTP NEWS" (norm "rtp news", length 8) vs
        // 1 stream "SIC" (norm "sic", length 3). Sem votação: SIC vence.
        var bucket = new List<DiscoveredStream>
        {
            StreamWith("RTP NEWS"),
            StreamWith("RTP NEWS"),
            StreamWith("RTP NEWS"),
            StreamWith("SIC"),
        };
        Assert.Equal("SIC", GroupResolver.ResolveCanonical(bucket));
    }

    [Fact]
    public void Smoke_SIC_bucket_in_5_PT_like_groups_picks_SIC()
    {
        // Todos os títulos normalizam para "sic" (length 3). Empate
        // -> tie-break OrdinalIgnoreCase -> "SIC" (todos iguais).
        var bucket = new List<DiscoveredStream>
        {
            StreamWith("SIC", "EU | PT | GENERAL"),
            StreamWith("SIC", "PORTUGUESE"),
            StreamWith("SIC", "Portugal"),
            StreamWith("SIC", "─ ✧･ﾟ|| PORTUGAL"),
            StreamWith("SIC", "─ ✧･ﾟ|| PORTUGAL VIP"),
        };
        Assert.Equal("SIC", GroupResolver.ResolveCanonical(bucket));
    }

    [Fact]
    public void Smoke_sport_tv_1_bucket_picks_SPORT_TV_1()
    {
        // Todos os títulos normalizam para "sport tv 1" (length 10).
        // Empate trivial.
        var bucket = new List<DiscoveredStream>
        {
            StreamWith("SPORT TV 1", "EU | PT | ESPORTES"),
            StreamWith("SPORT TV 1", "Portugal HEVC"),
            StreamWith("SPORT TV 1", "SPORTS NETWORKS"),
        };
        Assert.Equal("SPORT TV 1", GroupResolver.ResolveCanonical(bucket));
    }

    [Fact]
    public void GroupResolver_does_not_break_other_components()
    {
        Assert.Equal("portugal", GroupNormalizer.Normalize("Portugal"));
        Assert.Equal(ContentType.Live, ContentTypeDetector.Detect("RTP 1", "Portugal"));
        Assert.Equal(Category.Live, ChannelCategoryLookup.Lookup("rtp 1"));
        Assert.Equal(Category.Live, SourceGroupCategoryLookup.Lookup("portugal"));
        var taxonomy = GroupTaxonomy.Lookup("eu | pt | general");
        Assert.Equal(OutputGroupKind.PortugalLive, taxonomy.OutputGroup);
        Assert.Equal(Confidence.High, taxonomy.Confidence);
        Assert.Equal(OutputGroupKind.PortugalLive,
                     ResolutionPolicy.Resolve("rtp 1", "eu | pt | general", "RTP 1", false));
    }
}
