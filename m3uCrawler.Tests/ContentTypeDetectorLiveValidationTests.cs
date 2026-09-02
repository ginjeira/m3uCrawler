using System.Text.Json;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Matching;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Read-only live validation of <see cref="ContentTypeDetector"/>
/// against the current production m3uCrawler playlist snapshot
/// (2026-08-31 22:39:14).
///
/// The ContentTypeDetector classifies by structural signals (title
/// format, SourceGroup markers). The numbers below are the
/// detector's classification of the 1031 streams, not the
/// investigation report's group-membership counts.
///
/// Divergence vs. investigation report (2026-08-31 22:39:14):
///   - Investigation reported VOD=244 (streams in group "VOD | PORTUGAL").
///     Detector classifies 243 as VOD by title regex
///     `^PT\s*-\s*.+?\s*-\s*\d{4}\s*$`. The 1 stream in the group that
///     does NOT match VOD title format is
///     "#f#11ffff00##### PORTUGAL ####" (a colour placeholder;
///     correctly classified as Live, since it does not look like a
///     VOD movie title).
///   - Investigation reported PPV=10 (streams in group
///     "VIP | LIGA PORTUGAL BETCLIC"). Detector classifies 11 as
///     PPV because it also matches SourceGroup "EU | SE | SPORT TV PPV"
///     (one stream: "SW - SVENSKBIL SPORT TV PPV 1 :"). That stream is
///     actually Foreign (handled by CountryChannelValidator, not by
///     this detector); the detector only flags its structural type.
///   - Filmes24_7=91 and Live=686 match exactly. Total=1031.
///
/// IMPORTANT: these counts are structural, not editorial. Foreign
/// streams are still classified as Live by this detector and excluded
/// by CountryChannelValidator at the Country step.
/// </summary>
public class ContentTypeDetectorLiveValidationTests
{
    [Fact]
    public void Live_playlist_content_type_distribution_matches_detector_counts()
    {
        var playlistPath = Path.Combine(AppContext.BaseDirectory, "TestData",
            "m3ucrawler_playlist_20260831_223914.m3u");
        Assert.True(System.IO.File.Exists(playlistPath),
            $"Live playlist fixture missing: {playlistPath}");

        var content = System.IO.File.ReadAllText(playlistPath);
        var streams = new M3uParserService().Parse(content);

        int live = 0, vod = 0, filmes24_7 = 0, ppv = 0;
        foreach (var s in streams)
        {
            switch (ContentTypeDetector.Detect(s.Title, s.Group))
            {
                case ContentType.Live: live++; break;
                case ContentType.VOD: vod++; break;
                case ContentType.Filmes24_7: filmes24_7++; break;
                case ContentType.PPV: ppv++; break;
            }
        }

        var dump = new
        {
            playlist = playlistPath,
            totalStreams = streams.Count,
            counts = new { live, vod, filmes24_7, ppv },
            notes = new
            {
                vod = "Detector title regex `^PT\\s*-\\s*.+?\\s*-\\s*\\d{4}\\s*$`. " +
                      "Investigation counted group-membership (244); 1 stream in " +
                      "the VOD group is a colour placeholder (`#f#...`) not matching VOD.",
                ppv = "Detector matches `\\b(PPV|BETCLIC)\\b`. " +
                      "Investigation counted 10 (BETCLIC group). Detector finds 11 " +
                      "(includes 1 stream in 'EU | SE | SPORT TV PPV', which is " +
                      "actually Foreign by CountryChannelValidator).",
                filmes24_7 = "Matches `(24/7|24-7)` in SourceGroup or title. " +
                              "Matches investigation count exactly.",
                live = "Fallback. Equals total - (vod + filmes24_7 + ppv). " +
                       "Includes Foreign streams (rejected at Country step).",
            },
        };
        var dumpPath = Path.Combine(Path.GetTempPath(),
            $"content_type_distribution_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
        System.IO.File.WriteAllText(dumpPath, JsonSerializer.Serialize(dump,
            new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine("==== ContentTypeDetector live validation ====");
        Console.WriteLine($"Total streams: {streams.Count}");
        Console.WriteLine($"Live: {live}");
        Console.WriteLine($"VOD: {vod}");
        Console.WriteLine($"Filmes24_7: {filmes24_7}");
        Console.WriteLine($"PPV: {ppv}");
        Console.WriteLine($"JSON dump: {dumpPath}");

        Assert.Equal(1031, streams.Count);
        Assert.Equal(243, vod);
        Assert.Equal(91, filmes24_7);
        Assert.Equal(11, ppv);
        Assert.Equal(686, live);
        Assert.Equal(streams.Count - vod - filmes24_7 - ppv, live);
    }
}