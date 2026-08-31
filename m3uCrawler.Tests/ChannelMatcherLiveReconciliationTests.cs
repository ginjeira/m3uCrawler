using System.Text.Json;
using m3uCrawler.Models;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using Xunit;

namespace m3uCrawler.Tests;

[Trait("Category", "Live")]
public class ChannelMatcherLiveReconciliationTests
{
    [Fact]
    public void Real_data_each_problematic_channel_yields_exactly_one_decision()
    {
        var root = Environment.GetEnvironmentVariable("DISPATCHARR_TEST_DATA_DIR");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            // No real data shipped with the repo. Test only runs when a snapshot is provided.
            return;
        }

        var channelsRaw = File.ReadAllText(Path.Combine(root, "channels.json"));
        var streamsRaw = File.ReadAllText(Path.Combine(root, "streams.json"));
        var groupsRaw = File.ReadAllText(Path.Combine(root, "groups.json"));
        var playlistRaw = File.ReadAllText(Path.Combine(root, "playlist.m3u"));

        var channelsDoc = JsonDocument.Parse(channelsRaw);
        var streamsDoc = JsonDocument.Parse(streamsRaw);
        var groupsDoc = JsonDocument.Parse(groupsRaw);

        var channelsList = channelsDoc.RootElement.ValueKind == JsonValueKind.Array
            ? channelsDoc.RootElement
            : channelsDoc.RootElement.GetProperty("results");
        var streamsList = streamsDoc.RootElement.ValueKind == JsonValueKind.Array
            ? streamsDoc.RootElement
            : streamsDoc.RootElement.GetProperty("results");
        var groupsList = groupsDoc.RootElement.ValueKind == JsonValueKind.Array
            ? groupsDoc.RootElement
            : groupsDoc.RootElement.GetProperty("results");

        var channels = new List<JsonElement>();
        foreach (var e in channelsList.EnumerateArray()) channels.Add(e);
        var streams = new List<JsonElement>();
        foreach (var e in streamsList.EnumerateArray()) streams.Add(e);
        var groups = new List<JsonElement>();
        foreach (var e in groupsList.EnumerateArray()) groups.Add(e);

        var domainChannels = channels.Select(c => new DispatcharrChannel(
            c.GetProperty("id").GetInt64(),
            c.GetProperty("name").GetString() ?? string.Empty,
            c.TryGetProperty("channel_group_id", out var g) && g.ValueKind != JsonValueKind.Null ? g.GetInt64().ToString() : null,
            c.TryGetProperty("channel_number", out var cn) && cn.ValueKind != JsonValueKind.Number ? cn.GetDouble() : (double?)null,
            c.TryGetProperty("tvg_id", out var tvg) && tvg.ValueKind != JsonValueKind.Null ? tvg.GetString() : null,
            Array.Empty<long>()
        )).ToArray();

        var domainStreams = streams.Select(s => new DispatcharrStream(
            s.GetProperty("id").GetInt64(),
            s.GetProperty("name").GetString() ?? string.Empty,
            s.GetProperty("url").GetString() ?? string.Empty,
            s.TryGetProperty("tvg_id", out var tvg) && tvg.ValueKind != JsonValueKind.Null ? tvg.GetString() : null,
            s.TryGetProperty("group_name", out var gn) && gn.ValueKind != JsonValueKind.Null ? gn.GetString() : null,
            s.TryGetProperty("m3u_account_name", out var ma) && ma.ValueKind != JsonValueKind.Null ? ma.GetString() : null,
            s.TryGetProperty("is_custom", out var ic) && ic.ValueKind == JsonValueKind.True,
            s.TryGetProperty("is_working", out var iw) && iw.ValueKind == JsonValueKind.True,
            s.TryGetProperty("response_time", out var rt) && rt.ValueKind == JsonValueKind.Number ? rt.GetDouble() : (double?)null
        )).ToArray();

        var domainGroups = groups.Select(g => new DispatcharrChannelGroup(
            g.GetProperty("id").GetInt64(),
            g.GetProperty("name").GetString() ?? string.Empty
        )).ToArray();

        // Parse playlist.m3u (simple parser).
        var discovered = new List<DiscoveredStream>();
        string? pendingTitle = null;
        foreach (var line in playlistRaw.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("#EXTINF"))
            {
                var comma = t.IndexOf(',');
                pendingTitle = comma >= 0 ? t.Substring(comma + 1) : string.Empty;
            }
            else if (!t.StartsWith("#") && !string.IsNullOrWhiteSpace(t))
            {
                discovered.Add(new DiscoveredStream(
                    new M3uStream { Title = pendingTitle ?? string.Empty, Url = t, IsWorking = true, ResponseTime = 0, Group = string.Empty },
                    "live", "/opt/playlists/playlist.m3u"));
                pendingTitle = null;
            }
        }

        var matcher = new ChannelMatcher(new AliasResolver());
        var plan = matcher.BuildPlan(
            discovered,
            new DispatcharrState(domainChannels, domainStreams, domainGroups, "0.29.0"),
            MatchingOptions.Default,
            new StreamOrderingPolicy(),
            "/opt/playlists/playlist.m3u",
            "http://192.168.68.142:9191",
            dryRun: true);

        var problematic = new long[] { 3, 4, 16, 28 };
        foreach (var id in problematic)
        {
            var decisions = plan.Channels.Where(c => c.ExistingChannelId == id).ToList();
            Assert.True(decisions.Count <= 1,
                $"existingChannelId={id} produced {decisions.Count} decisions (expected <= 1)");
        }
    }
}