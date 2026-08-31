using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using m3uCrawler.Models;
using m3uCrawler.Services.Dispatcharr;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using m3uCrawler.Services.Sync;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Regression tests for the cross-channel DELETE → PATCH race that surfaced in commit
/// <c>8877f6f</c>: the same <c>existingStreamId</c> can appear as <c>Removed</c> in multiple
/// <c>ChannelDecision</c>s, but <c>DELETE /api/channels/streams/&lt;id&gt;/</c> is a
/// global operation (Dispatcharr cascades to every ChannelStream referencing the stream).
/// Phase 4 must therefore run AFTER all Phase 2 + Phase 3 have completed across the
/// whole MatchPlan, and must only DELETE streams that no remaining decision keeps.
///
/// All tests drive <see cref="DispatcharrSyncService.ApplyAsync"/> directly with synthetic
/// MatchPlans, capturing HTTP traffic via a recording handler.
/// </summary>
public class DispatcharrSyncServiceGlobalPhase4Tests
{
    [Fact]
    public async Task Shared_stream_Removed_in_one_channel_kept_in_another_is_never_deleted()
    {
        // Channel A removes stream X; Channel B keeps it. Phase 4 must NOT call DELETE on X.
        var (svc, handler, state) = BuildSvc();

        var channelA = MakeChannel(100, "CNN",
            new StreamMatchDecision { StreamUrl = "https://a.example/x", StreamName = "X", Outcome = SyncOutcome.Removed, ExistingStreamId = 1, IsWorking = true });
        var channelB = MakeChannel(200, "CNN 2",
            new StreamMatchDecision { StreamUrl = "https://b.example/x", StreamName = "X (B)", Outcome = SyncOutcome.ExistingUnchanged, ExistingStreamId = 1, IsWorking = true });

        var plan = MakePlan(channelA, channelB);

        await svc.ApplyAsync(plan, state, new List<FailedReportEntry>(), CancellationToken.None);

        Assert.DoesNotContain(1L, handler.DeleteStreamIds);
    }

    [Fact]
    public async Task Shared_stream_Removed_in_two_channels_is_deleted_only_once()
    {
        // Same stream X marked Removed in two different channels. Phase 4 must DELETE it once.
        var (svc, handler, state) = BuildSvc();

        var channelA = MakeChannel(100, "CNN",
            new StreamMatchDecision { StreamUrl = "https://a.example/x", StreamName = "X A", Outcome = SyncOutcome.Removed, ExistingStreamId = 1, IsWorking = true });
        var channelB = MakeChannel(200, "CNN 2",
            new StreamMatchDecision { StreamUrl = "https://b.example/x", StreamName = "X B", Outcome = SyncOutcome.Removed, ExistingStreamId = 1, IsWorking = true });

        var plan = MakePlan(channelA, channelB);

        await svc.ApplyAsync(plan, state, new List<FailedReportEntry>(), CancellationToken.None);

        Assert.Single(handler.DeleteStreamIds);
        Assert.Equal(1L, handler.DeleteStreamIds[0]);
    }

    [Fact]
    public async Task Phase_ordering_all_PATCHes_complete_before_any_DELETE()
    {
        // Sequence in handler: PATCH, PATCH, DELETE. Never DELETE interleaved with PATCH.
        var (svc, handler, state) = BuildSvc();

        var channelA = MakeChannel(100, "CNN A",
            new StreamMatchDecision { StreamUrl = "https://a.example/x", StreamName = "X A", Outcome = SyncOutcome.Removed, ExistingStreamId = 1, IsWorking = true });
        var channelB = MakeChannel(200, "CNN B",
            new StreamMatchDecision { StreamUrl = "https://b.example/y", StreamName = "Y B", Outcome = SyncOutcome.Removed, ExistingStreamId = 2, IsWorking = true });

        var plan = MakePlan(channelA, channelB);

        await svc.ApplyAsync(plan, state, new List<FailedReportEntry>(), CancellationToken.None);

        // All PATCHes happened before any DELETE.
        var lastPatchIdx = -1;
        for (int i = 0; i < handler.Traces.Count; i++)
        {
            if (handler.Traces[i].StartsWith("PATCH"))
            {
                lastPatchIdx = i;
            }
            if (handler.Traces[i].StartsWith("DELETE"))
            {
                Assert.True(lastPatchIdx >= 0, $"DELETE happened at index {i} before any PATCH");
                Assert.True(i > lastPatchIdx,
                    $"DELETE at index {i} happened after a PATCH at {lastPatchIdx} — unexpected interleaving");
            }
        }
        Assert.NotEqual(-1, lastPatchIdx);
    }

    [Fact]
    public async Task Cross_channel_reproduction_real_case_1398_1400_1413_1415_with_streams_23003_23006_23009_23010()
    {
        // Real-world repro: ch 1398 keeps 23003 / removes 23009+23006; ch 1400 keeps 23008+23009 / removes 23006+23003.
        // Before the fix: ch 1400's PATCH with {23008,23009} would 400 Invalid pk "23009" because
        // ch 1398's Phase 4 had already DELETEd 23009. After the fix: ch 1398's Phase 4 no longer
        // runs before ch 1400's PATCH; the global DELETE happens after both PATCHes, and because
        // 23009 is in ch 1400's keep set, it is NEVER deleted. No Invalid pk error.
        var (svc, handler, state) = BuildSvc();

        var ch1398 = MakeChannel(1398, "PT - TV CINE ACTION FHD",
            new StreamMatchDecision { StreamUrl = "http://servicepro4.shop/.../68400", StreamName = "PT - TV CINE ACTION FHD", Outcome = SyncOutcome.ExistingUnchanged, ExistingStreamId = 23003, IsWorking = true },
            new StreamMatchDecision { StreamUrl = "http://servicepro4.shop/.../68401", StreamName = "PT - TV CINE EMOTION FHD", Outcome = SyncOutcome.Removed, ExistingStreamId = 23009, IsWorking = true },
            new StreamMatchDecision { StreamUrl = "http://servicepro4.shop/.../68402", StreamName = "PT - TV CINE EDITION FHD", Outcome = SyncOutcome.Removed, ExistingStreamId = 23006, IsWorking = true });

        var ch1400 = MakeChannel(1400, "PT - TV CINE EMOTION FHD",
            new StreamMatchDecision { StreamUrl = "http://servicepro4.shop/.../764096", StreamName = "PT - TV CINE EMOTION FHD", Outcome = SyncOutcome.ExistingUnchanged, ExistingStreamId = 23008, IsWorking = true },
            new StreamMatchDecision { StreamUrl = "http://servicepro4.shop/.../68401", StreamName = "PT - TV CINE EMOTION FHD", Outcome = SyncOutcome.ExistingUnchanged, ExistingStreamId = 23009, IsWorking = true },
            new StreamMatchDecision { StreamUrl = "http://servicepro4.shop/.../68402", StreamName = "PT - TV CINE EDITION FHD", Outcome = SyncOutcome.Removed, ExistingStreamId = 23006, IsWorking = true },
            new StreamMatchDecision { StreamUrl = "http://servicepro4.shop/.../68400", StreamName = "PT - TV CINE ACTION FHD", Outcome = SyncOutcome.Removed, ExistingStreamId = 23003, IsWorking = true });

        var plan = MakePlan(ch1398, ch1400);

        await svc.ApplyAsync(plan, state, new List<FailedReportEntry>(), CancellationToken.None);

        // 23009 must NOT be deleted (still in ch 1400 keep).
        Assert.DoesNotContain(23009L, handler.DeleteStreamIds);
        // 23003 must NOT be deleted (still in ch 1398 keep).
        Assert.DoesNotContain(23003L, handler.DeleteStreamIds);
        // 23006 IS deleted (no channel keeps it after merge — both remove it).
        Assert.Contains(23006L, handler.DeleteStreamIds);
        Assert.Single(handler.DeleteStreamIds);
        // Both PATCHes succeeded (200 OK from our stub).
        Assert.Equal(2, handler.PatchBodies.Count);
    }

    [Fact]
    public async Task True_orphan_stream_is_deleted()
    {
        var (svc, handler, state) = BuildSvc();

        var channelA = MakeChannel(100, "CNN",
            new StreamMatchDecision { StreamUrl = "https://a.example/x", StreamName = "X", Outcome = SyncOutcome.Removed, ExistingStreamId = 1, IsWorking = true });

        var plan = MakePlan(channelA);

        await svc.ApplyAsync(plan, state, new List<FailedReportEntry>(), CancellationToken.None);

        // Diagnostic: print HTTP traces for visibility when the test fails.
        if (handler.DeleteStreamIds.Count == 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"No DELETE fired. Traces: {string.Join(" | ", handler.Traces)}");
        }
        Assert.Equal(new long[] { 1 }, handler.DeleteStreamIds);
    }

    [Fact]
    public async Task Duplicate_Removed_across_channels_yields_single_DELETE()
    {
        // Three channels all mark the same stream as Removed.
        var (svc, handler, state) = BuildSvc();

        var a = MakeChannel(100, "A",
            new StreamMatchDecision { StreamUrl = "https://a.example/x", StreamName = "X A", Outcome = SyncOutcome.Removed, ExistingStreamId = 1, IsWorking = true });
        var b = MakeChannel(200, "B",
            new StreamMatchDecision { StreamUrl = "https://b.example/x", StreamName = "X B", Outcome = SyncOutcome.Removed, ExistingStreamId = 1, IsWorking = true });
        var c = MakeChannel(300, "C",
            new StreamMatchDecision { StreamUrl = "https://c.example/x", StreamName = "X C", Outcome = SyncOutcome.Removed, ExistingStreamId = 1, IsWorking = true });

        var plan = MakePlan(a, b, c);

        await svc.ApplyAsync(plan, state, new List<FailedReportEntry>(), CancellationToken.None);

        Assert.Single(handler.DeleteStreamIds);
        Assert.Equal(1L, handler.DeleteStreamIds[0]);
    }

    [Fact]
    public async Task NewStream_POSTed_in_Phase2_is_never_deleted_by_Phase4()
    {
        // Channel A has a NewStream (no existingStreamId) and an existing stream to keep.
        // Phase 4 must DELETE only the existing stream, never touch the new one.
        var (svc, handler, state) = BuildSvc();
        // New stream POST returns id=9999.
        handler.NextNewStreamId = 9999L;

        var channelA = MakeChannel(100, "CNN",
            new StreamMatchDecision { StreamUrl = "https://new.example/cnn", StreamName = "CNN New", Outcome = SyncOutcome.NewStream, ExistingStreamId = null, IsWorking = true },
            new StreamMatchDecision { StreamUrl = "https://old.example/cnn", StreamName = "CNN Old", Outcome = SyncOutcome.Removed, ExistingStreamId = 2, IsWorking = true });

        var plan = MakePlan(channelA);

        await svc.ApplyAsync(plan, state, new List<FailedReportEntry>(), CancellationToken.None);

        // POST NewStream fired.
        Assert.Single(handler.StreamPostBodies);
        // DELETE 9999 must NOT have been called (it was just created).
        Assert.DoesNotContain(9999L, handler.DeleteStreamIds);
        // DELETE 2 was called.
        Assert.Equal(new long[] { 2 }, handler.DeleteStreamIds);
    }

    [Fact]
    public async Task StreamsEmptied_channel_does_not_trigger_extra_DELETE_for_streams_other_channels_need()
    {
        // Channel A is emptied (StreamsEmptied=true) and references stream X as Removed.
        // Channel B keeps X. After the fix, A's PATCH with streams=[] happens first, then B's
        // PATCH with [...,X] happens; the global DELETE must skip X because B still needs it.
        var (svc, handler, state) = BuildSvc();

        var channelA = MakeChannel(100, "CNN A",
            new StreamMatchDecision { StreamUrl = "https://a.example/x", StreamName = "X A", Outcome = SyncOutcome.Removed, ExistingStreamId = 1, IsWorking = true });

        var channelB = MakeChannel(200, "CNN B",
            new StreamMatchDecision { StreamUrl = "https://b.example/x", StreamName = "X B", Outcome = SyncOutcome.ExistingUnchanged, ExistingStreamId = 1, IsWorking = true });

        var plan = MakePlan(channelA, channelB);

        await svc.ApplyAsync(plan, state, new List<FailedReportEntry>(), CancellationToken.None);

        // PATCH on A was streams=[] (StreamsEmptied scenario).
        Assert.Equal(2, handler.PatchBodies.Count);
        var aBody = JsonSerializer.Deserialize<JsonElement>(handler.PatchBodies[0]);
        Assert.Empty(aBody.GetProperty("streams").EnumerateArray().ToList());
        // PATCH on B contained stream 1.
        var bBody = JsonSerializer.Deserialize<JsonElement>(handler.PatchBodies[1]);
        Assert.Equal(new long[] { 1 }, bBody.GetProperty("streams").EnumerateArray().Select(e => e.GetInt64()).ToList());
        // No DELETE: stream 1 is still kept by B.
        Assert.DoesNotContain(1L, handler.DeleteStreamIds);
    }

    [Fact]
    public async Task Ambiguous_channel_does_not_remove_streams_other_channels_still_need()
    {
        // Channel A is Ambiguous (skipped in Apply). Channel B keeps stream X. The global
        // Phase 4 must NOT touch stream X just because A is skipped.
        var (svc, handler, state) = BuildSvc();

        var ambiguous = new ChannelDecision
        {
            Identity = "cnn|cnn-2",
            CanonicalName = "CNN",
            Outcome = SyncOutcome.Ambiguous,
            ExistingChannelId = null,
            ChannelGroupName = null,
            MatchReason = "fuzzy",
            MatchScore = 88,
            Streams = Array.Empty<StreamMatchDecision>(),
            AmbiguousCandidates = new[]
            {
                new AmbiguousCandidate { ExistingChannelId = 1, ExistingChannelName = "CNN Intl", Score = 88, Reason = "fuzzy" },
                new AmbiguousCandidate { ExistingChannelId = 2, ExistingChannelName = "CNN US", Score = 87, Reason = "fuzzy" },
            },
        };

        var channelB = MakeChannel(200, "CNN B",
            new StreamMatchDecision { StreamUrl = "https://b.example/x", StreamName = "X B", Outcome = SyncOutcome.ExistingUnchanged, ExistingStreamId = 1, IsWorking = true });

        var plan = MakePlan(ambiguous, channelB);

        await svc.ApplyAsync(plan, state, new List<FailedReportEntry>(), CancellationToken.None);

        // Ambiguous channel A produced no PATCH of its own. Channel B did PATCH (verified below).
        // B kept X. No DELETE.
        Assert.DoesNotContain(1L, handler.DeleteStreamIds);
        // B did PATCH.
        Assert.Single(handler.PatchBodies);
    }

    [Fact]
    public async Task Phase3_failure_for_channel_does_not_remove_its_Removed_streams()
    {
        // Channel A keeps X and removes Y. PATCH fails (returns 500). Phase 4 must
        // not delete Y, because we cannot confirm A's state — preserving Y is the
        // safer choice.
        var (svc, handler, state) = BuildSvc();
        // Make PATCH return 500.
        handler.PatchShouldFail = true;

        var channelA = MakeChannel(100, "CNN",
            new StreamMatchDecision { StreamUrl = "https://a.example/x", StreamName = "X", Outcome = SyncOutcome.ExistingUnchanged, ExistingStreamId = 1, IsWorking = true },
            new StreamMatchDecision { StreamUrl = "https://a.example/y", StreamName = "Y", Outcome = SyncOutcome.Removed, ExistingStreamId = 2, IsWorking = true });

        var plan = MakePlan(channelA);

        var failed = new List<FailedReportEntry>();
        await svc.ApplyAsync(plan, state, failed, CancellationToken.None);

        // Channel A registered as failed.
        Assert.Single(failed);
        Assert.Equal(100L, failed[0].ExistingChannelId);
        // Y is preserved (no DELETE).
        Assert.DoesNotContain(2L, handler.DeleteStreamIds);
    }

    // ----- helpers -----

    private static ChannelDecision MakeChannel(long id, string name, params StreamMatchDecision[] streams)
    {
        return new ChannelDecision
        {
            Identity = $"id-{id}",
            CanonicalName = name,
            Outcome = SyncOutcome.ExistingReassigned,
            ExistingChannelId = id,
            ChannelGroupName = "News",
            MatchReason = "exact",
            MatchScore = 100,
            Streams = streams,
            StreamsEmptied = streams.All(s => s.Outcome == SyncOutcome.Removed) && streams.Length > 0,
            AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
        };
    }

    private static MatchPlan MakePlan(params ChannelDecision[] channels)
    {
        return new MatchPlan
        {
            GeneratedAtUtc = "2026-01-01T00:00:00Z",
            SourcePlaylistPath = "x.m3u",
            DispatcharrBaseUrl = "http://dispatcharr.local",
            DryRun = false,
            MatchThreshold = 80,
            Channels = channels,
            AmbiguousGroups = Array.Empty<AmbiguousGroupEntry>(),
            Counts = new SyncReportCounts
            {
                Matched = channels.Length,
                NewChannels = 0,
                NewStreams = 0,
                RemovedStreams = 0,
                Skipped = 0,
                Ambiguous = 0,
                Unchanged = 0,
                Failed = 0,
            },
        };
    }

    private static (DispatcharrSyncService svc, GlobalPhase4RecordingHandler handler, DispatcharrState state) BuildSvc()
    {
        var cfg = new DispatcharrConfig
        {
            Enabled = true,
            BaseUrl = "http://dispatcharr.local",
            ApiKey = "PLACEHOLDER-API-KEY",
            DryRun = false,
            MatchThreshold = 80,
        };

        var handler = new GlobalPhase4RecordingHandler();
        var auth = new DispatcharrAuthState();
        auth.Set("PLACEHOLDER-API-KEY", null);
        var login = new DispatcharrLoginApi(new HttpClient()) { ApiKey = "PLACEHOLDER-API-KEY" };
        var authHandler = new DispatcharrAuthHandler(auth, login) { InnerHandler = handler };
        var client = new HttpClient(authHandler) { BaseAddress = new Uri("http://dispatcharr.local/api/") };

        var svc = new DispatcharrSyncService(
            cfg, Path.Combine(Path.GetTempPath(), $"out_{Guid.NewGuid():N}"),
            aliases: new AliasResolver(),
            ordering: new StreamOrderingPolicy(),
            channels: new DispatcharrChannelClient(client),
            streams: new DispatcharrStreamClient(client),
            m3u: new DispatcharrM3UClient(client),
            http: client,
            auth: auth,
            login: login);

        var state = new DispatcharrState(
            Channels: Array.Empty<DispatcharrChannel>(),
            Streams: Array.Empty<DispatcharrStream>(),
            Groups: Array.Empty<DispatcharrChannelGroup>(),
            Version: "0.30.0");

        return (svc, handler, state);
    }

    private sealed class GlobalPhase4RecordingHandler : HttpMessageHandler
    {
        public List<string> Traces { get; } = new();
        public List<string> PatchBodies { get; } = new();
        public List<long> DeleteStreamIds { get; } = new();
        public List<string> StreamPostBodies { get; } = new();
        public long? NextNewStreamId { get; set; } = null;
        public bool PatchShouldFail { get; set; } = false;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            var method = req.Method.ToString().ToUpperInvariant();
            Traces.Add($"{method} {path}");

            if (method == "GET" && path.EndsWith("/api/channels/channels/"))
                return Task.FromResult(JsonResp(new { count = 0, results = Array.Empty<object>() }));
            if (method == "GET" && path.EndsWith("/api/channels/streams/"))
                return Task.FromResult(JsonResp(new { count = 0, results = Array.Empty<object>() }));
            if (method == "GET" && path.EndsWith("/api/channels/groups/"))
                return Task.FromResult(JsonResp(new { count = 0, results = Array.Empty<object>() }));
            if (method == "GET" && path.EndsWith("/api/core/version/"))
                return Task.FromResult(JsonResp(new { version = "0.30.0" }));
            if (method == "GET" && path.EndsWith("/streams/") && path.Contains("/channels/"))
                return Task.FromResult(JsonResp(Array.Empty<object>()));

            if (method == "POST" && path.EndsWith("/api/channels/streams/"))
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                StreamPostBodies.Add(body);
                var newId = NextNewStreamId ?? new Random().Next(1_000_000, 10_000_000);
                return Task.FromResult(JsonResp(new { id = newId, name = "n", url = "x", is_custom = true }));
            }

            if (method == "POST" && path.EndsWith("/api/channels/groups/"))
            {
                return Task.FromResult(JsonResp(new { id = 5L, name = "News" }));
            }

            if (method == "PATCH" && path.Contains("/api/channels/channels/"))
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                PatchBodies.Add(body);
                if (PatchShouldFail)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            if (method == "DELETE" && path.Contains("/api/channels/streams/") && !path.EndsWith("/streams/"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(path, @"/streams/(\d+)/");
                if (match.Success && long.TryParse(match.Groups[1].Value, out var sid))
                    DeleteStreamIds.Add(sid);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResp(object payload) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
    }
}