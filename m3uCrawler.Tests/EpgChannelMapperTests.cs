using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Epg;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Testes de <see cref="EpgChannelMapper"/>: parsing XMLTV em streaming e
/// mapeamento canal canónico → id de EPG (matching exato/aliases, preferência
/// por forma normalizada sem HD, ambíguos, não-casados e país filtrado).
/// </summary>
public class EpgChannelMapperTests
{
    // =====================================================================
    // Parsing
    // =====================================================================

    [Fact]
    public async Task Parse_extracts_id_display_name_icon_and_lang()
    {
        var xml = Xmltv(
            ("rtp1.pt", "RTP 1", "pt", "https://logo.example/rtp1.png"));

        await using var stream = Utf8(xml);
        var channels = await EpgChannelMapper.ParseAsync(stream);

        var channel = Assert.Single(channels);
        Assert.Equal("rtp1.pt", channel.Id);
        Assert.Equal("RTP 1", channel.DisplayName);
        Assert.Equal("https://logo.example/rtp1.png", channel.IconSrc);
        Assert.Equal("pt", channel.Lang);
    }

    [Fact]
    public async Task Parse_reads_multiple_channels_and_ignores_programmes()
    {
        var xml =
            "<?xml version=\"1.0\"?>\n" +
            "<tv>\n" +
            "  <channel id=\"a.pt\"><display-name>A</display-name></channel>\n" +
            "  <channel id=\"b.pt\"><display-name>B</display-name></channel>\n" +
            "  <programme start=\"20260101000000 +0000\" channel=\"a.pt\">\n" +
            "    <title>Show</title>\n" +
            "    <channel id=\"should-not-be-read\"/>\n" +
            "  </programme>\n" +
            "</tv>\n";

        await using var stream = Utf8(xml);
        var channels = await EpgChannelMapper.ParseAsync(stream);

        Assert.Equal(2, channels.Count);
        Assert.Equal(new[] { "a.pt", "b.pt" }, channels.Select(c => c.Id).ToArray());
    }

    [Fact]
    public async Task Parse_handles_empty_channel_elements()
    {
        var xml =
            "<tv>\n" +
            "  <channel id=\"self-closing.pt\"/>\n" +
            "  <channel id=\"empty-child.pt\"><display-name/></channel>\n" +
            "  <channel id=\"ok.pt\"><display-name>OK</display-name></channel>\n" +
            "</tv>\n";

        await using var stream = Utf8(xml);
        var channels = await EpgChannelMapper.ParseAsync(stream);

        Assert.Equal(3, channels.Count);
        Assert.Equal("self-closing.pt", channels[0].Id);
        Assert.Null(channels[0].DisplayName);
        Assert.Equal("ok.pt", channels[2].Id);
        Assert.Equal("OK", channels[2].DisplayName);
    }

    [Fact]
    public async Task Parse_uses_first_non_empty_display_name()
    {
        var xml =
            "<tv>\n" +
            "  <channel id=\"x.pt\">\n" +
            "    <display-name lang=\"en\"></display-name>\n" +
            "    <display-name lang=\"pt\">Nome PT</display-name>\n" +
            "    <display-name lang=\"en\">Name EN</display-name>\n" +
            "  </channel>\n" +
            "</tv>\n";

        await using var stream = Utf8(xml);
        var channel = Assert.Single(await EpgChannelMapper.ParseAsync(stream));

        Assert.Equal("Nome PT", channel.DisplayName);
        Assert.Equal("pt", channel.Lang);
    }

    // =====================================================================
    // Plan — matching
    // =====================================================================

    [Fact]
    public void Plan_matches_exact_key()
    {
        var plan = Plan(
            new[] { Channel(1, "rtp1", "RTP 1") },
            new[] { Epg("rtp1.pt") },
            "pt");

        var mapping = Assert.Single(plan.Mappings);
        Assert.Equal("rtp1", mapping.ChannelKey);
        Assert.Equal(1, mapping.CanonicalChannelId);
        Assert.Equal("rtp1.pt", mapping.EpgId);
        Assert.Empty(plan.Unmatched);
        Assert.Empty(plan.Ambiguous);
        Assert.Empty(plan.Uncertain);
    }

    [Fact]
    public void Plan_matches_via_channel_alias_and_separator_insensitivity()
    {
        // Key "sport-tv-1" e alias "sport tv 1" normalizam ambos para
        // "sporttv1", que casa com "sporttv1.pt".
        var plan = Plan(
            new[] { Channel(2, "sport-tv-1", "Sport TV 1", "pt", "sport tv 1") },
            new[] { Epg("sporttv1.pt") },
            "pt");

        Assert.Equal("sporttv1.pt", Assert.Single(plan.Mappings).EpgId);
    }

    [Fact]
    public void Plan_matches_display_name()
    {
        var plan = Plan(
            new[] { Channel(3, "canal-hollywood", "Canal Hollywood") },
            new[] { Epg("canalhollywood.pt") },
            "pt");

        Assert.Equal("canalhollywood.pt", Assert.Single(plan.Mappings).EpgId);
    }

    [Fact]
    public void Plan_prefers_normalized_id_without_hd()
    {
        // Ambos casam com "rtp1" (o dotted passa pelo índice sem-hd), mas a
        // forma já normalizada e sem HD vence.
        var plan = Plan(
            new[] { Channel(1, "rtp1", "RTP 1") },
            new[] { Epg("RTP.1.HD.pt"), Epg("rtp1.pt") },
            "pt");

        Assert.Equal("rtp1.pt", Assert.Single(plan.Mappings).EpgId);
        Assert.Empty(plan.Uncertain);
    }

    [Fact]
    public void Plan_multiple_normalized_candidates_are_ambiguous_not_mapped()
    {
        var plan = Plan(
            new[] { Channel(1, "rtp1", "RTP 1") },
            new[] { Epg("rtp1.pt"), Epg("rtp.1.pt") },
            "pt");

        Assert.Empty(plan.Mappings);
        var ambiguous = Assert.Single(plan.Ambiguous);
        Assert.Equal("rtp1", ambiguous.ChannelKey);
        Assert.Equal(2, ambiguous.CandidateIds.Distinct().Count());
    }

    [Fact]
    public void Plan_single_dotted_pascal_case_candidate_is_uncertain_not_mapped()
    {
        // Único candidato é a variante dotted/maiúsculas: normalizá-la mudaria
        // o valor (risco de não casar por case), pelo que não é mapeado.
        var plan = Plan(
            new[] { Channel(1, "rtp1", "RTP 1") },
            new[] { Epg("RTP.1.HD.pt") },
            "pt");

        Assert.Empty(plan.Mappings);
        var uncertain = Assert.Single(plan.Uncertain);
        Assert.Equal("RTP.1.HD.pt", uncertain.CandidateId);
        Assert.NotEmpty(plan.Warnings);
    }

    [Fact]
    public void Plan_unmatched_when_no_candidate_exists()
    {
        var plan = Plan(
            new[] { Channel(1, "sic", "SIC") },
            new[] { Epg("rtp1.pt") },
            "pt");

        Assert.Empty(plan.Mappings);
        Assert.Single(plan.Unmatched);
        Assert.Empty(plan.Ambiguous);
    }

    // =====================================================================
    // Plan — país
    // =====================================================================

    [Fact]
    public void Plan_ignores_epg_ids_with_other_country_suffix()
    {
        var plan = Plan(
            new[] { Channel(1, "rtp1", "RTP 1", "pt") },
            new[] { Epg("rtp1.es"), Epg("rtp1.pt") },
            "pt");

        Assert.Equal("rtp1.pt", Assert.Single(plan.Mappings).EpgId);
    }

    [Fact]
    public void Plan_respects_epg_country_parameter()
    {
        var plan = Plan(
            new[] { Channel(1, "rtp1", "RTP 1", "es") },
            new[] { Epg("rtp1.es") },
            "es");

        Assert.Equal("rtp1.es", Assert.Single(plan.Mappings).EpgId);
    }

    [Fact]
    public void Plan_skips_canonical_channel_of_other_country()
    {
        var plan = Plan(
            new[] { Channel(1, "rtp1", "RTP 1", "es") },
            new[] { Epg("rtp1.pt") },
            "pt");

        Assert.Empty(plan.Mappings);
        Assert.Empty(plan.Unmatched);
        Assert.Empty(plan.Ambiguous);
    }

    [Fact]
    public void Plan_includes_country_agnostic_channel()
    {
        var plan = Plan(
            new[] { Channel(1, "global-one", "Global One", country: null) },
            new[] { Epg("globalone.pt") },
            "pt");

        Assert.Equal("globalone.pt", Assert.Single(plan.Mappings).EpgId);
    }

    [Fact]
    public void Plan_is_deterministic_for_same_input()
    {
        var channels = new[] { Channel(1, "rtp1", "RTP 1"), Channel(2, "sic", "SIC") };
        var epg = new[] { Epg("rtp1.pt"), Epg("sic.pt") };

        var first = Plan(channels, epg, "pt");
        var second = Plan(channels, epg, "pt");

        Assert.Equal(
            first.Mappings.Select(m => (m.ChannelKey, m.EpgId)).ToArray(),
            second.Mappings.Select(m => (m.ChannelKey, m.EpgId)).ToArray());
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static EpgMappingPlan Plan(
        IReadOnlyList<CanonicalChannelEntity> channels,
        IReadOnlyList<EpgChannel> epg,
        string country)
        => EpgChannelMapper.Plan(channels, epg, country);

    private static EpgChannel Epg(string id, string? name = null) => new(id, name, null, null);

    private static CanonicalChannelEntity Channel(
        long id, string key, string displayName, string? country = "pt", params string[] aliases)
    {
        var entity = new CanonicalChannelEntity
        {
            Id = id,
            Key = key,
            DisplayName = displayName,
            Country = country,
            IsEnabled = true,
        };
        foreach (var alias in aliases)
        {
            entity.Aliases.Add(new ChannelAliasEntity
            {
                NormalizedAlias = alias,
                CanonicalChannelId = id,
            });
        }

        return entity;
    }

    private static string Xmltv(params (string Id, string Name, string? Lang, string? Icon)[] channels)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<tv generator-info-name=\"test\">\n");
        foreach (var channel in channels)
        {
            sb.Append("  <channel id=\"").Append(channel.Id).Append("\">\n");
            sb.Append("    <display-name");
            if (!string.IsNullOrEmpty(channel.Lang))
            {
                sb.Append(" lang=\"").Append(channel.Lang).Append('"');
            }

            sb.Append('>').Append(channel.Name).Append("</display-name>\n");
            if (!string.IsNullOrEmpty(channel.Icon))
            {
                sb.Append("    <icon src=\"").Append(channel.Icon).Append("\"/>\n");
            }

            sb.Append("  </channel>\n");
        }

        sb.Append("</tv>\n");
        return sb.ToString();
    }

    private static MemoryStream Utf8(string text)
        => new(Encoding.UTF8.GetBytes(text));
}
