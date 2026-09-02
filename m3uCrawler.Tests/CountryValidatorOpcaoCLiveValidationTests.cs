using System.Text.Json;
using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Live validation harness for Opção C + 'si' against the current
/// production playlist (snapshot 2026-08-31 22:39:14).
///
/// Confirms that the systematic scan of all foreign-prefix entries
/// in the current playlist produces 0 wrongly-accepted streams.
/// </summary>
public class CountryValidatorOpcaoCLiveValidationTests
{
    // Known Grupo B entries that exist in the current production
    // playlist. Each MUST be rejected by Opção C.
    private static readonly string[] GrupoBPrefixes = new[]
    {
        "FR", "BE", "SW", "BG", "SI", "LT", "UY", "GT", "KH",
    };

    [Fact]
    public void Current_playlist_opção_c_rejects_all_known_foreign_prefix_entries()
    {
        var playlistPath = Path.Combine(AppContext.BaseDirectory, "TestData",
            "m3ucrawler_playlist_20260831_223914.m3u");
        Assert.True(File.Exists(playlistPath),
            $"Live playlist fixture missing: {playlistPath}");

        var content = File.ReadAllText(playlistPath);
        var parser = new M3uParserService();
        var liveStreams = parser.Parse(content);

        // Sample titles that begin with foreign prefixes
        var foreignSamples = liveStreams
            .Where(s => HasForeignPrefix(s.Title ?? ""))
            .Take(25)
            .ToList();

        var validatorRoot = Path.Combine(Path.GetTempPath(),
            "ccv_root_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(validatorRoot);
        CopyIndicators(validatorRoot);
        var v = new CountryChannelValidator(validatorRoot);

        var wronglyAccepted = new List<string>();
        var allForeignEntries = new List<(string Title, string Group, bool Rejected)>();

        foreach (var s in liveStreams)
        {
            var title = s.Title ?? "(no-title)";
            if (!HasForeignPrefix(title)) continue;

            var matches = v.ValidateStreams(new List<M3uStream> { s }, "pt");
            var rejected = matches.Count == 0;
            allForeignEntries.Add((title, s.Group ?? "", rejected));

            if (!rejected)
                wronglyAccepted.Add($"{title} :: group={s.Group}");
        }

        // Group count by prefix
        var byPrefix = allForeignEntries
            .GroupBy(e => ExtractPrefix(e.Title))
            .OrderBy(g => g.Key)
            .Select(g => new { Prefix = g.Key, Total = g.Count(), Rejected = g.Count(x => x.Rejected), Accepted = g.Count(x => !x.Rejected) })
            .ToList();

        Assert.Empty(wronglyAccepted);

        // Print summary
        Console.WriteLine("==== Opção C live validation (current playlist) ====");
        Console.WriteLine($"Playlist input: {liveStreams.Count} streams");
        Console.WriteLine($"Foreign-prefix entries found: {allForeignEntries.Count}");
        Console.WriteLine($"First 5 foreign-prefix titles (debug):");
        foreach (var s in foreignSamples)
        {
            Console.WriteLine($"  sample: title='{s.Title}' group='{s.Group}'");
        }
        Console.WriteLine();
        foreach (var bp in byPrefix)
        {
            Console.WriteLine($"  {bp.Prefix}: total={bp.Total} rejected={bp.Rejected} accepted={bp.Accepted}");
        }

        // Dump for report
        var dump = new
        {
            playlist = playlistPath,
            inputStreamCount = liveStreams.Count,
            foreignEntryCount = allForeignEntries.Count,
            foreignSampleTitles = foreignSamples.Select(s => new { s.Title, s.Group }).ToList(),
            byPrefix,
            wronglyAccepted,
        };
        var json = JsonSerializer.Serialize(dump,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(Path.GetTempPath(),
            $"country_opcao_c_live_validation_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json"), json);
    }

    private static bool HasForeignPrefix(string title)
    {
        // Match XX- or XXX- (hyphen adjacent) OR XX/XXX followed by
        // whitespace-then-hyphen (e.g. "BE - RTL TVI HEVC").
        var m = System.Text.RegularExpressions.Regex.Match(title, @"^([A-Z]{2,3})[\s]*[\-]");
        if (!m.Success) return false;
        var prefix = m.Groups[1].Value;
        // Exclude non-country prefixes that the playlist employs.
        return prefix != "PT" && prefix != "EU" && prefix != "AM" &&
               prefix != "AS" && prefix != "AF" && prefix != "VIP";
    }

    private static string ExtractPrefix(string title)
    {
        var m = System.Text.RegularExpressions.Regex.Match(title, @"^([A-Z]{2,3})[\s]*[\-]");
        return m.Success ? m.Groups[1].Value : "(none)";
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