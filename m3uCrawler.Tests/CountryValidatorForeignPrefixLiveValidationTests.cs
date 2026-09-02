using System.Text.Json;
using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Validation harness for Opção C (foreign ISO prefix negative
/// evidence) against the real m3uCrawler playlist captured at
/// 2026-08-31 20:45:29.
///
/// The m3uCrawler playlist (input to the crawler) does NOT contain
/// the Grupo B entries — those are only in the Dispatcharr-side
/// playlist. This test therefore:
///  - feeds the live 579-stream playlist to ValidateStreams and
///    asserts that ZERO legitimate PT channels are rejected;
///  - injects synthetic Grupo B entries on top and asserts they
///    are rejected by Opção C.
/// </summary>
public class CountryValidatorForeignPrefixLiveValidationTests
{
    private static readonly (string Title, string Group)[] GrupoBCases = new[]
    {
        ("BE - RTL TVI HEVC", "EU | BELGIUM"),
        ("BE - RTL TVI HD", "EU | BELGIUM"),
        ("BG - BTV HD", "EU | BULGARIA"),
        ("BG - BTV ACTION HD", "EU | BULGARIA"),
        ("BG - BTV CINEMA HD", "EU | BULGARIA"),
        ("BG - BTV COMEDY HD", "EU | BULGARIA"),
        ("BG - BTV STORY", "EU | BULGARIA"),
        ("GT - CANAL 11", "AM | LATINO"),
        ("KH - BTV NEWS", "AS | CAMBODIA"),
        ("LT - BTV HD", "EU | LITHUANIA"),
        ("SW - SVENSKBIL SPORT TV PPV 1 :", "EU | SE | SPORT TV PPV"),
        ("UY - CANAL 11 LAS PIEDRAS", "AM | LATINO"),
        ("UY - CANAL 11 TREINTA Y TRES", "AM | LATINO"),
    };

    private static readonly string[] LegitimatePTSamples = new[]
    {
        "RTP 1",
        "SIC",
        "TVI",
        "SPORT TV 1",
        "TV CINE ACTION",
        "CNN Portugal",
        "RTP Memória",
        "RTP África",
        "Porto Canal",
        "Globo Portugal",
        "SportTV 1",
        "DAZN 1",
    };

    [Fact]
    public void Live_playlist_Opcao_C_rejects_Grupo_B_and_keeps_PT_legitimate_channels()
    {
        var playlistPath = Path.Combine(AppContext.BaseDirectory, "TestData",
            "m3ucrawler_playlist_20260831_204529.m3u");
        Assert.True(File.Exists(playlistPath),
            $"Live playlist fixture missing: {playlistPath}");

        var content = File.ReadAllText(playlistPath);
        var parser = new M3uParserService();
        var liveStreams = parser.Parse(content);

        // Use a temp root with the production indicators so the validator
        // mirrors production behaviour.
        var validatorRoot = Path.Combine(Path.GetTempPath(),
            "ccv_root_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(validatorRoot);
        CopyIndicators(validatorRoot);
        var v = new CountryChannelValidator(validatorRoot);

        // === Validate live playlist: zero PT legitimate rejections ===
        var rejectedFromLive = new List<string>();
        foreach (var s in liveStreams)
        {
            var matches = v.ValidateStreams(new List<M3uStream> { s }, "pt");
            if (matches.Count == 0) rejectedFromLive.Add(s.Title ?? "(no-title)");
        }
        Assert.Empty(rejectedFromLive);

        // === Validate synthetic Grupo B entries: all rejected ===
        var grupoBStreamList = GrupoBCases.Select(g => new M3uStream
        {
            Title = g.Title,
            Url = $"http://x/{g.Title.Replace(' ', '_')}",
            Group = g.Group,
            IsWorking = true,
        }).ToList();
        var grupoBMatches = v.ValidateStreams(grupoBStreamList, "pt");
        Assert.Empty(grupoBMatches);

        // === Validate synthetic legitimate PT: all accepted ===
        var ptStreamList = LegitimatePTSamples.Select(t => new M3uStream
        {
            Title = t,
            Url = $"http://x/{t.Replace(' ', '_')}",
            Group = "Portugal",
            IsWorking = true,
        }).ToList();
        var ptMatches = v.ValidateStreams(ptStreamList, "pt");
        var ptAcceptedTitles = ptMatches.Select(m => m.Stream.Title).ToList();
        var ptRejected = LegitimatePTSamples.Except(ptAcceptedTitles).ToList();
        Assert.True(ptMatches.Count == LegitimatePTSamples.Length,
            $"Expected all {LegitimatePTSamples.Length} PT samples accepted; " +
            $"got {ptMatches.Count}. Rejected: {string.Join(", ", ptRejected)}");

        // === Dump diagnostics for the report ===
        var dump = new
        {
            livePlaylist = new
            {
                inputStreamCount = liveStreams.Count,
                rejectedCount = rejectedFromLive.Count,
                sampleRejected = rejectedFromLive.Take(20).ToList(),
            },
            grupoB = new
            {
                syntheticCases = GrupoBCases.Length,
                acceptedCount = grupoBMatches.Count,
            },
            legitimatePT = new
            {
                sampleCount = LegitimatePTSamples.Length,
                acceptedCount = ptMatches.Count,
            },
        };
        var json = JsonSerializer.Serialize(dump,
            new JsonSerializerOptions { WriteIndented = true });
        var dumpPath = Path.Combine(Path.GetTempPath(),
            $"country_opcao_c_validation_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
        File.WriteAllText(dumpPath, json);

        Console.WriteLine("==== OPCAO C VALIDATION REPORT ====");
        Console.WriteLine($"Live playlist input streams: {liveStreams.Count}");
        Console.WriteLine($"Live playlist rejected: {rejectedFromLive.Count}");
        Console.WriteLine($"Synthetic Grupo B cases rejected: " +
            $"{GrupoBCases.Length - grupoBMatches.Count}/{GrupoBCases.Length}");
        Console.WriteLine($"Synthetic legitimate PT accepted: " +
            $"{ptMatches.Count}/{LegitimatePTSamples.Length}");
        Console.WriteLine($"JSON dump: {dumpPath}");
    }

    private static void CopyIndicators(string targetDir)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
                "m3uCrawler", "runtime-data", "channel-indicators.json"),
            Path.Combine(AppContext.BaseDirectory, "m3uCrawler",
                "runtime-data", "channel-indicators.json"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                File.Copy(c, Path.Combine(targetDir, "channel-indicators.json"));
                return;
            }
        }
        var fallback = """
        { "indicators": [
          "cnn portugal", "porto canal", "porto canal hd",
          "euronews portugal", "rtp memoria", "rtp memória",
          "rtp madeira", "rtp acores", "rtp açores",
          "rtp africa", "rtp áfrica", "rtp internacional",
          "rtp noticias", "rtp n", "rtp play", "rtp play hd",
          "tvi 24", "tvi internacional", "tvi ficcao", "tvi reality",
          "tvi reality camera 1", "tvi reality camera 2",
          "tvi reality camera 3", "tvi reality camera 4",
          "v+ tvi",
          "sport tv 1", "sport tv 2", "sport tv 3", "sport tv 4",
          "sport tv 5", "sport tv 6", "sport tv 7",
          "sport tv nba", "sport tv news", "sport tv +",
          "sporttv 1", "sporttv 2", "sporttv 3", "sporttv 4", "sporttv 5",
          "dazn 1", "dazn 2", "dazn 3", "dazn 4", "dazn 5", "dazn 6", "dazns 2",
          "eleven sport 1", "eleven sport 2", "eleven sport 3",
          "eleven sport 4", "eleven sport 5", "eleven sport 6",
          "star channel", "star comedy", "star crime", "star life", "star movies",
          "btv 1", "btv 2", "btv 3", "benfica tv", "benfica tv 1", "benfica tv hd",
          "caca e pesca", "caca vision", "canal 11", "canal nos", "canal q",
          "canal 180", "cancao nova", "casa e cozinha", "cmtv", "cnn portugal",
          "combate", "kombat sport", "kuriakos kids", "kuriakos tv",
          "localvisao", "record tv", "record tv 1", "record news",
          "record news hd", "tca", "tpa international",
          "tv mana 1", "tv mana 2", "fatimatv", "zap viva", "alma lusa",
          "disney channel", "cartoon network", "nick jr", "nickelodeon",
          "tv cine action", "tv cine edition", "tv cine emotion",
          "tv cine top", "tv cine +"
        ]}
        """;
        File.WriteAllText(Path.Combine(targetDir, "channel-indicators.json"),
            fallback);
    }
}