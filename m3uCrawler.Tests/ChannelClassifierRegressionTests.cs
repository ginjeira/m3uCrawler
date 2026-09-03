using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Matcher integration test that runs against the **live production
/// playlist fixture** (captured 2026-08-31). Proves that the new
/// classification boundary prevents non-channel kinds from
/// becoming <see cref="SyncOutcome.NewChannel"/> in
/// <see cref="MatchPlan.Channels"/>, while legitimate channels still
/// match.
///
/// This is the regression that motivated the architectural fix:
/// `PT - NO EVENT` was reaching the matcher as a NewChannel because
/// the bundle-guard regex did not match its title.
///
/// Per the brief: a single assertion of "NotEmpty(plan.Channels)" is
/// NOT enough. The fixture must prove simultaneously that:
///   1. falsos positivos removidos (PT - NO EVENT, Filmes 24/7, LiveCam
///      não aparecem em plan.Channels como NewChannel);
///   2. canais válidos não perdidos (a lista auditada de canais
///      legítimos continua presente no plano).
/// </summary>
public class ChannelClassifierRegressionTests
{
    // Audited list of legitimate PT channels expected to survive
    // classification and produce a NewChannel decision in the live
    // fixture. The list is the OUTPUT of the classifier for canonical
    // PT identities that should ALWAYS be promoted.
    private static readonly string[] LegitimateChannelsExpected = new[]
    {
        "sic",
        "tvi",
        "tvi 24",
        "cmtv",
        "rtp 1",
        "rtp 2",
        "rtp 3",
        "rtp noticias",
        "cnn",
        "euronews",
    };

    [Fact]
    public void Live_playlist_no_non_channel_kind_reaches_NewChannel()
    {
        var playlistPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "m3ucrawler_playlist_20260831_223914.m3u");
        Assert.True(File.Exists(playlistPath), $"Live playlist fixture missing: {playlistPath}");

        var content = File.ReadAllText(playlistPath);
        var streams = new M3uParserService().Parse(content);
        Assert.NotEmpty(streams);

        var discovered = streams
            .Select((s, i) => new DiscoveredStream(
                new M3uStream
                {
                    Title = s.Title,
                    Url = s.Url,
                    Group = s.Group,
                    Logo = s.Logo,
                    OriginalExtInf = s.OriginalExtInf,
                    IsWorking = true,
                    LastTested = s.LastTested,
                    ResponseTime = s.ResponseTime,
                },
                "live-fixture",
                $"line-{i}"))
            .ToList();

        var plan = new ChannelMatcher(new AliasResolver(null)).BuildPlan(
            discovered,
            new DispatcharrState(
                Array.Empty<DispatcharrChannel>(),
                Array.Empty<DispatcharrStream>(),
                Array.Empty<DispatcharrChannelGroup>(),
                null),
            MatchingOptions.Default,
            new StreamOrderingPolicy(),
            playlistPath,
            "http://x",
            dryRun: true);

        // Invariant: every ChannelDecision must have come from a Channel
        // classification. There is no way for a Bundle/Vod/LiveCam/Group/
        // Foreign/Unknown/Placeholder entry to reach NewChannel.
        Assert.NotEmpty(plan.Channels);
        foreach (var decision in plan.Channels)
        {
            Assert.NotNull(decision.Identity);
            Assert.NotEmpty(decision.Identity);
        }

        // RETENTION: a lista auditada de canais legítimos continua
        // presente no plano. Sem esta asserção, o teste poderia
        // passar trivialmente excluindo tudo.
        var planIdentities = plan.Channels
            .Select(c => c.Identity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var expected in LegitimateChannelsExpected)
        {
            Assert.Contains(expected, planIdentities);
        }

        // FALSE-POSITIVE REGRESSION: a fixture contém 8 ocorrências de
        // "PT - NO EVENT" no grupo "VIP | LIGA PORTUGAL BETCLIC" que
        // nunca devem tornar-se NewChannel.
        var noEventDecisions = plan.Channels
            .Where(c => c.CanonicalName.Contains("NO EVENT", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(noEventDecisions);

        // Filmes 24/7 também nunca.
        var filmesDecisions = plan.Channels
            .Where(c => c.CanonicalName.Contains("Filmes ", StringComparison.OrdinalIgnoreCase)
                     && c.CanonicalName.Contains("24/7", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(filmesDecisions);

        // LiveCam nunca.
        var liveCamDecisions = plan.Channels
            .Where(c => c.CanonicalName.Contains("LiveCam", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(liveCamDecisions);

        // As entradas não-canais contabilizadas como ClassifiedExclusions
        // (excluded) e como UnknownReviewRequired.
        var noEventExcluded = plan.ClassifiedExclusions
            .Count(e => e.Title.Contains("NO EVENT", StringComparison.OrdinalIgnoreCase));
        var noEventReview = plan.UnknownReviewRequired
            .Count(r => r.Title.Contains("NO EVENT", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(8, noEventExcluded + noEventReview);

        // Contadores por disposição: o contador "excluded" reflecte
        // todas as entradas Bundle/Vod/LiveCam/Foreign/Group da fixture.
        Assert.True(plan.Counts.MatchingDisposition["excluded"] > 0,
            "fixture must produce excluded entries (Bundle/Vod/LiveCam/Foreign)");

        // Com Dispatcharr vazio, todas as identidades curadas (SIC,
        // TVI, RTP, etc.) são promovidas a NewChannel.
        Assert.Equal(
            plan.Channels.Count(c => c.Outcome == SyncOutcome.NewChannel),
            plan.Counts.MatchingDisposition["newChannelsFromCuratedIdentity"]);
        // Com a lista auditada acima (SIC, TVI, RTP…) e Dispatcharr
        // vazio, todas essas identidades curadas SÃO promovidas a
        // NewChannel, mas algumas delas podem já estar em
        // NewChannelFromCuratedIdentity = ... de NewChannels no
        // counts. A asserção seguinte é apenas que NewChannels
        // está coerente com curated channels.

        // Sanity: NewChannels vem exclusivamente de identidades
        // curadas. Unknown NUNCA pode criar NewChannel, mesmo com
        // título não-canónico. Vamos iterar todas as decisões e
        // confirmar que cada NewChannel tem uma identidade curada
        // (presente em ChannelCategoryLookup).
        foreach (var ch in plan.Channels.Where(c => c.Outcome == SyncOutcome.NewChannel))
        {
            Assert.True(
                ChannelCategoryLookup.Contains(ch.Identity),
                $"NewChannel '{ch.Identity}' must be a curated identity; raw 'PT - NO EVENT' style entries are forbidden");
        }
    }
}

