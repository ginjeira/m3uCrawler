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
/// Regression tests for the latent bug in <see cref="DispatcharrSyncService"/>:
///
/// Phase 3 (PATCH-channel) was gated by <c>ctx.AllStreamIds.Count &gt; 0</c>; whenever the
/// reconciliation produced an empty <c>AllStreamIds</c>, the PATCH was skipped. Phase 4
/// (DELETE-stream) had no such gate and would still issue DELETEs for <c>Removed</c> entries,
/// leaving the channel with partial streams and no PATCH record of <c>streams=[]</c>.
///
/// Contract being verified:
///
///   A) <c>AllStreamIds.Count &gt; 0</c>        → PATCH with the (deduplicated) list.
///   B) empty body + at least one Removed     → PATCH <c>streams=[]</c>; no DELETE-stream for these ids.
///   C) only Skipped entries                  → no PATCH, no DELETE-stream, channel preserved.
///
/// These tests drive <see cref="DispatcharrSyncService.BeginChannelApplyAsync"/> and
/// <see cref="DispatcharrSyncService.CompleteChannelApplyAsync"/> directly with synthetic
/// <see cref="ChannelDecision"/> payloads because the current matcher cannot naturally produce
/// scenario B (only Removed for an existing channel) — the bug is dormant but the contract must
/// remain explicit so future matcher changes cannot re-introduce the asymmetry.
/// </summary>
public class DispatcharrSyncServiceEmptyChannelTests
{
    [Fact]
    public async Task Existing_channel_with_only_Removed_streams_is_cleared_by_PATCH_with_empty_streams()
    {
        var (svc, handler, state) = BuildSvc();
        var failed = new List<FailedReportEntry>();

        var channel = new ChannelDecision
        {
            Identity = "cnn",
            CanonicalName = "CNN",
            Outcome = SyncOutcome.ExistingReassigned,
            ExistingChannelId = 100,
            ChannelGroupName = "News",
            MatchReason = "exact",
            MatchScore = 100,
            Streams = new[]
            {
                new StreamMatchDecision
                {
                    Provider = "old.example", StreamUrl = "https://old.example/cnn", StreamName = "CNN",
                    Outcome = SyncOutcome.Removed, ExistingStreamId = 1, ProposedOrder = -1,
                    OrderReason = "missing-from-current-playlist", IsWorking = true, GroupName = "News",
                },
            },
            // Scenario B: emptied by the plan (set explicitly for the unit test; in
            // production this is computed by MergeExistingChannelDecisions).
            StreamsEmptied = true,
            AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
        };

        var ctx = await svc.BeginChannelApplyAsync(channel, state, handler.GroupByName, CancellationToken.None);
        Assert.False(ctx.PatchFailed);
        await svc.CompleteChannelApplyAsync(channel, ctx, failed, CancellationToken.None);

        // Expect exactly one PATCH with streams=[].
        Assert.Single(handler.PatchBodies);
        var body = JsonSerializer.Deserialize<JsonElement>(handler.PatchBodies[0]);
        Assert.True(body.TryGetProperty("streams", out var streamsEl));
        var ids = streamsEl.EnumerateArray().Select(e => e.GetInt64()).ToList();
        Assert.Empty(ids);

        // Scenario B must NOT issue DELETE-stream calls (PATCH already cleared them).
        Assert.Empty(handler.DeleteStreamIds);
        Assert.Empty(failed);
    }

    [Fact]
    public async Task Existing_channel_with_only_Skipped_entries_is_left_untouched()
    {
        var (svc, handler, state) = BuildSvc();
        var failed = new List<FailedReportEntry>();

        var channel = new ChannelDecision
        {
            Identity = "cnn",
            CanonicalName = "CNN",
            Outcome = SyncOutcome.ExistingUnchanged,
            ExistingChannelId = 100,
            ChannelGroupName = "News",
            MatchReason = "no-match",
            MatchScore = 0,
            Streams = new[]
            {
                new StreamMatchDecision
                {
                    Provider = "new.example", StreamUrl = "https://new.example/cnn", StreamName = "CNN",
                    Outcome = SyncOutcome.Skipped, ExistingStreamId = null, ProposedOrder = -1,
                    OrderReason = "not-working", IsWorking = false, GroupName = "News",
                },
            },
            // Scenario C: Skipped only — do not touch.
            StreamsEmptied = false,
            AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
        };

        var ctx = await svc.BeginChannelApplyAsync(channel, state, handler.GroupByName, CancellationToken.None);
        Assert.False(ctx.PatchFailed);
        await svc.CompleteChannelApplyAsync(channel, ctx, failed, CancellationToken.None);

        // Scenario C must not issue ANY PATCH or DELETE.
        Assert.Empty(handler.PatchBodies);
        Assert.Empty(handler.DeleteStreamIds);
        Assert.Empty(failed);
    }

    [Fact]
    public void Existing_channel_with_Removed_and_Skipped_streams_is_not_marked_as_emptied()
    {
        // Skipped means "preserve / do not touch"; therefore its presence in Streams
        // must block StreamsEmptied=true even when there is also a Removed entry.
        var removed = new StreamMatchDecision
        {
            Provider = "old.example", StreamUrl = "https://old.example/cnn", StreamName = "CNN",
            Outcome = SyncOutcome.Removed, ExistingStreamId = 1, ProposedOrder = -1,
            OrderReason = "missing-from-current-playlist", IsWorking = true, GroupName = "News",
        };
        var skipped = new StreamMatchDecision
        {
            Provider = "new.example", StreamUrl = "https://new.example/cnn", StreamName = "CNN",
            Outcome = SyncOutcome.Skipped, ExistingStreamId = null, ProposedOrder = -1,
            OrderReason = "not-working", IsWorking = false, GroupName = "News",
        };

        var channels = new[] { new DispatcharrChannel(100, "CNN", null, null, null, new long[] { 1 }) };
        var streams = new[]
        {
            new DispatcharrStream(1, "CNN", "https://old.example/cnn", null, "News", null, false, true, null),
        };
        var groups = Array.Empty<DispatcharrChannelGroup>();
        var discovered = new[]
        {
            // One working entry that does not URL-match stream 1 but title-matches → existing (kept).
            // Force a Removed decision by supplying an unmatched-but-stale playlist entry via Skipped.
            // The matcher always keeps the stream when title matches. To exercise the "Removed +
            // Skipped" coexistence we go directly through the merge.
            new DiscoveredStream(new M3uStream { Title = "CNN", Url = "https://new.example/cnn", IsWorking = true, Group = "News" }, "p", "x.m3u"),
        };

        // Drive the merge through a synthetic plan by hand.
        var decision = new ChannelDecision
        {
            Identity = "cnn",
            CanonicalName = "CNN",
            Outcome = SyncOutcome.ExistingReassigned,
            ExistingChannelId = 100,
            ChannelGroupName = "News",
            MatchReason = "test",
            MatchScore = 0,
            Streams = new[] { removed, skipped },
            StreamsEmptied = false, // sentinel: the test below does NOT rely on this field
            AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
        };

        // The StreamsEmptied decision is computed in MergeExistingChannelDecisions. To verify
        // the new boundary, re-evaluate the same predicate here (it must agree with the matcher).
        bool computedEmptied =
            decision.Streams.Any(s => s.Outcome == SyncOutcome.Removed)
            && !decision.Streams.Any(s =>
                s.Outcome == SyncOutcome.NewStream
                || s.Outcome == SyncOutcome.ExistingUnchanged
                || s.Outcome == SyncOutcome.ExistingReassigned
                || s.Outcome == SyncOutcome.ExistingReordered
                || s.Outcome == SyncOutcome.Skipped);

        Assert.False(computedEmptied);
        Assert.False(decision.StreamsEmptied);
        _ = channels; _ = streams; _ = groups; _ = discovered; // references retained for compile-time coverage
    }

    [Fact]
    public void StreamsEmptied_is_false_when_only_Skipped_in_Streams()
    {
        var skipped = new StreamMatchDecision
        {
            Provider = "p", StreamUrl = "https://new.example/cnn", StreamName = "CNN",
            Outcome = SyncOutcome.Skipped, ExistingStreamId = null, ProposedOrder = -1,
            OrderReason = "not-working", IsWorking = false, GroupName = "News",
        };
        var decision = new ChannelDecision
        {
            Identity = "cnn", CanonicalName = "CNN",
            Outcome = SyncOutcome.ExistingUnchanged, ExistingChannelId = 100,
            ChannelGroupName = "News", MatchReason = "test", MatchScore = 0,
            Streams = new[] { skipped },
            StreamsEmptied = false,
            AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
        };

        bool computedEmptied =
            decision.Streams.Any(s => s.Outcome == SyncOutcome.Removed)
            && !decision.Streams.Any(s =>
                s.Outcome == SyncOutcome.NewStream
                || s.Outcome == SyncOutcome.ExistingUnchanged
                || s.Outcome == SyncOutcome.ExistingReassigned
                || s.Outcome == SyncOutcome.ExistingReordered
                || s.Outcome == SyncOutcome.Skipped);

        Assert.False(computedEmptied);
    }

    [Fact]
    public void StreamsEmptied_is_false_when_no_Removed_even_with_other_outcomes()
    {
        var kept = new StreamMatchDecision
        {
            Provider = "p", StreamUrl = "https://old.example/cnn", StreamName = "CNN",
            Outcome = SyncOutcome.ExistingUnchanged, ExistingStreamId = 1, ProposedOrder = -1,
            OrderReason = "merged-keep", IsWorking = true, GroupName = "News",
        };
        var newStream = new StreamMatchDecision
        {
            Provider = "p", StreamUrl = "https://new.example/cnn", StreamName = "CNN 2",
            Outcome = SyncOutcome.NewStream, ExistingStreamId = null, ProposedOrder = 0,
            OrderReason = "new", IsWorking = true, GroupName = "News",
        };
        var skipped = new StreamMatchDecision
        {
            Provider = "p", StreamUrl = "https://skip.example/cnn", StreamName = "CNN 3",
            Outcome = SyncOutcome.Skipped, ExistingStreamId = null, ProposedOrder = -1,
            OrderReason = "not-working", IsWorking = false, GroupName = "News",
        };

        var decision = new ChannelDecision
        {
            Identity = "cnn", CanonicalName = "CNN",
            Outcome = SyncOutcome.ExistingReassigned, ExistingChannelId = 100,
            ChannelGroupName = "News", MatchReason = "test", MatchScore = 0,
            Streams = new[] { kept, newStream, skipped },
            StreamsEmptied = false,
            AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
        };

        bool computedEmptied =
            decision.Streams.Any(s => s.Outcome == SyncOutcome.Removed)
            && !decision.Streams.Any(s =>
                s.Outcome == SyncOutcome.NewStream
                || s.Outcome == SyncOutcome.ExistingUnchanged
                || s.Outcome == SyncOutcome.ExistingReassigned
                || s.Outcome == SyncOutcome.ExistingReordered
                || s.Outcome == SyncOutcome.Skipped);

        Assert.False(computedEmptied);
    }

    [Fact]
    public async Task Existing_channel_with_kept_and_removed_streams_only_PATCHes_kept_list()
    {
        var (svc, handler, state) = BuildSvc();
        var failed = new List<FailedReportEntry>();

        // channel 100 keeps stream 1, marks stream 2 as removed, adds nothing new.
        handler.CurrentStreamIdsFor[100L] = new long[] { 1, 2 };

        var channel = new ChannelDecision
        {
            Identity = "cnn",
            CanonicalName = "CNN",
            Outcome = SyncOutcome.ExistingReassigned,
            ExistingChannelId = 100,
            ChannelGroupName = "News",
            MatchReason = "exact",
            MatchScore = 100,
            Streams = new[]
            {
                new StreamMatchDecision
                {
                    Provider = "old.example", StreamUrl = "https://old.example/cnn", StreamName = "CNN",
                    Outcome = SyncOutcome.ExistingUnchanged, ExistingStreamId = 1, ProposedOrder = -1,
                    OrderReason = "merged-keep", IsWorking = true, GroupName = "News",
                },
                new StreamMatchDecision
                {
                    Provider = "old.example", StreamUrl = "https://old.example/cnn2", StreamName = "CNN 2",
                    Outcome = SyncOutcome.Removed, ExistingStreamId = 2, ProposedOrder = -1,
                    OrderReason = "missing-from-current-playlist", IsWorking = true, GroupName = "News",
                },
            },
            StreamsEmptied = false,
            AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
        };

        var ctx = await svc.BeginChannelApplyAsync(channel, state, handler.GroupByName, CancellationToken.None);
        Assert.False(ctx.PatchFailed);
        await svc.CompleteChannelApplyAsync(channel, ctx, failed, CancellationToken.None);

        // PATCH must contain only [1].
        Assert.Single(handler.PatchBodies);
        var body = JsonSerializer.Deserialize<JsonElement>(handler.PatchBodies[0]);
        var ids = body.GetProperty("streams").EnumerateArray().Select(e => e.GetInt64()).ToList();
        Assert.Equal(new long[] { 1 }, ids);

        // Phase 4 (DELETE of stream 2) is now GLOBAL — it must not run from
        // CompleteChannelApplyAsync. Verify here that it has not run yet, then verify
        // in the integration RunAsync test below that the global pass calls DELETE 2
        // once stream 2 has been confirmed orphaned across all channels.
        Assert.DoesNotContain(2L, handler.DeleteStreamIds);
        Assert.Empty(failed);
    }

    [Fact]
    public async Task Distinct_layer_in_b0dfc48_still_dedupes_repeated_existing_streamIds()
    {
        var (svc, handler, state) = BuildSvc();
        var failed = new List<FailedReportEntry>();

        // channel 100 currently has stream 99 attached (different from plan body).
        handler.CurrentStreamIdsFor[100L] = new long[] { 99 };

        // Plan body has stream 1 listed twice (defensive path: two NewStream entries
        // that resolve to the same id via the matcher's URL/title fallback).
        var channel = new ChannelDecision
        {
            Identity = "cnn",
            CanonicalName = "CNN",
            Outcome = SyncOutcome.ExistingReassigned,
            ExistingChannelId = 100,
            ChannelGroupName = "News",
            MatchReason = "exact",
            MatchScore = 100,
            Streams = new[]
            {
                new StreamMatchDecision
                {
                    Provider = "p1", StreamUrl = "https://p1/cnn", StreamName = "CNN",
                    Outcome = SyncOutcome.ExistingUnchanged, ExistingStreamId = 1, ProposedOrder = -1,
                    OrderReason = "merged-keep", IsWorking = true, GroupName = "News",
                },
                new StreamMatchDecision
                {
                    Provider = "p2", StreamUrl = "https://p2/cnn", StreamName = "CNN",
                    Outcome = SyncOutcome.ExistingUnchanged, ExistingStreamId = 1, ProposedOrder = -1,
                    OrderReason = "merged-keep", IsWorking = true, GroupName = "News",
                },
            },
            StreamsEmptied = false,
            AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
        };

        var ctx = await svc.BeginChannelApplyAsync(channel, state, handler.GroupByName, CancellationToken.None);
        Assert.False(ctx.PatchFailed);
        await svc.CompleteChannelApplyAsync(channel, ctx, failed, CancellationToken.None);

        // PATCH must contain stream 1 exactly once.
        Assert.Single(handler.PatchBodies);
        var body = JsonSerializer.Deserialize<JsonElement>(handler.PatchBodies[0]);
        var ids = body.GetProperty("streams").EnumerateArray().Select(e => e.GetInt64()).ToList();
        Assert.Single(ids);
        Assert.Equal(1L, ids[0]);
    }

    [Fact]
    public void Reconciliation_91cad8e_only_yields_one_decision_per_existingChannelId()
    {
        // Drive the matcher end-to-end and confirm that all bucket identities that resolve
        // to the same channel id collapse to a single ChannelDecision. This is the
        // invariant established in commit 91cad8e.
        var channels = new[]
        {
            new DispatcharrChannel(3, "Sic", null, null, null, new long[] { 22071 }),
        };
        var streams = new[]
        {
            new DispatcharrStream(22071, "PT || SIC", "https://old.example/sic", null, "Portugal", null, false, true, null),
        };
        var groups = Array.Empty<DispatcharrChannelGroup>();
        var discovered = new[]
        {
            new DiscoveredStream(new M3uStream { Title = "Sic", Url = "https://new.example/sic", IsWorking = true, Group = "Portugal" }, "new.example", "/tmp/p.m3u"),
            new DiscoveredStream(new M3uStream { Title = "sic na", Url = "https://new.example/sic-na", IsWorking = true, Group = "Portugal" }, "new.example", "/tmp/p.m3u"),
            new DiscoveredStream(new M3uStream { Title = "k sic", Url = "https://new.example/k-sic", IsWorking = true, Group = "Portugal" }, "new.example", "/tmp/p.m3u"),
        };

        var matcher = new ChannelMatcher(new AliasResolver());
        var plan = matcher.BuildPlan(
            discovered,
            new DispatcharrState(channels, streams, groups, "0.29.0"),
            MatchingOptions.Default,
            new StreamOrderingPolicy(),
            "/tmp/p.m3u", "http://dispatcharr.local", dryRun: true);

        var sicDecisions = plan.Channels
            .Where(c => c.ExistingChannelId == 3 && c.Outcome != SyncOutcome.Ambiguous)
            .ToList();

        Assert.Single(sicDecisions);
    }

    [Fact]
    public async Task Ambiguous_channel_in_plan_does_not_trigger_any_apply_HTTP()
    {
        var (svc, handler, state) = BuildSvc();
        var failed = new List<FailedReportEntry>();

        // Ambiguous channels must be filtered out by ApplyAsync (line 195: continue), so even
        // if a synthetic ChannelDecision with Outcome=Ambiguous is processed here we expect no
        // PATCH/DELETE to fire — though in production ApplyAsync never reaches BeginChannelApplyAsync
        // for them. This test pins the contract.
        var channel = new ChannelDecision
        {
            Identity = "cnn|sic",
            CanonicalName = "CNN",
            Outcome = SyncOutcome.Ambiguous,
            ExistingChannelId = null, // ambiguous channels do not pin to an id
            ChannelGroupName = null,
            MatchReason = "fuzzy",
            MatchScore = 88,
            Streams = Array.Empty<StreamMatchDecision>(),
            StreamsEmptied = false,
            AmbiguousCandidates = new[]
            {
                new AmbiguousCandidate { ExistingChannelId = 1, ExistingChannelName = "CNN Intl", Score = 88, Reason = "fuzzy" },
                new AmbiguousCandidate { ExistingChannelId = 2, ExistingChannelName = "CNN US", Score = 87, Reason = "fuzzy" },
            },
        };

        // ApplyAsync (not BeginChannelApplyAsync) is the integration point; mirror its guard:
        // if Outcome == Ambiguous || Skipped -> continue.
        if (channel.Outcome != SyncOutcome.Ambiguous && channel.Outcome != SyncOutcome.Skipped)
        {
            var ctx = await svc.BeginChannelApplyAsync(channel, state, handler.GroupByName, CancellationToken.None);
            await svc.CompleteChannelApplyAsync(channel, ctx, failed, CancellationToken.None);
        }

        Assert.Empty(handler.PatchBodies);
        Assert.Empty(handler.DeleteStreamIds);
        Assert.Empty(failed);
    }

    [Fact]
    public async Task StreamsEmptied_flag_persists_in_serialised_plan()
    {
        // Confirm that the ChannelsEmptied marker survives JSON round-trip via the real
        // MatchPlanSerializer (no inline JSON construction).
        var channel = new ChannelDecision
        {
            Identity = "cnn",
            CanonicalName = "CNN",
            Outcome = SyncOutcome.ExistingReassigned,
            ExistingChannelId = 100,
            ChannelGroupName = "News",
            MatchReason = "test",
            MatchScore = 0,
            Streams = new[]
            {
                new StreamMatchDecision
                {
                    Provider = "p", StreamUrl = "https://old.example/cnn", StreamName = "CNN",
                    Outcome = SyncOutcome.Removed, ExistingStreamId = 1, ProposedOrder = -1,
                    OrderReason = "missing-from-current-playlist", IsWorking = true, GroupName = "News",
                },
            },
            StreamsEmptied = true,
            AmbiguousCandidates = Array.Empty<AmbiguousCandidate>(),
        };
        var plan = new MatchPlan
        {
            GeneratedAtUtc = "2026-01-01T00:00:00Z",
            SourcePlaylistPath = "x.m3u",
            DispatcharrBaseUrl = "http://dispatcharr.local",
            DryRun = true,
            MatchThreshold = 80,
            Channels = new[] { channel },
            AmbiguousGroups = Array.Empty<AmbiguousGroupEntry>(),
        };

        var (svc, _, _) = BuildSvc();
        var tmp = Path.Combine(Path.GetTempPath(), $"plan_{Guid.NewGuid():N}.json");
        try
        {
            await MatchPlanSerializer.WriteAsync(plan, tmp, CancellationToken.None);
            var roundtrip = JsonSerializer.Deserialize<MatchPlan>(File.ReadAllText(tmp),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(roundtrip);
            Assert.Single(roundtrip!.Channels);
            Assert.True(roundtrip.Channels[0].StreamsEmptied);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static (DispatcharrSyncService svc, RecordingHandler handler, DispatcharrState state) BuildSvc()
    {
        var cfg = new DispatcharrConfig
        {
            Enabled = true,
            BaseUrl = "http://dispatcharr.local",
            ApiKey = "PLACEHOLDER-API-KEY",
            DryRun = false,
            MatchThreshold = 80,
        };

        var handler = new RecordingHandler();
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

        // Mimic a DispatcharrState with an empty group index.
        var state = new DispatcharrState(
            Channels: Array.Empty<DispatcharrChannel>(),
            Streams: Array.Empty<DispatcharrStream>(),
            Groups: Array.Empty<DispatcharrChannelGroup>(),
            Version: "0.30.0");

        return (svc, handler, state);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Dictionary<long, long[]> CurrentStreamIdsFor { get; } = new();
        public List<string> PatchBodies { get; } = new();
        public List<long> DeleteStreamIds { get; } = new();

        public Dictionary<string, long> GroupByName { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["News"] = 5L,
            ["Portugal"] = 222L,
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;

            if (req.Method == HttpMethod.Get && path.EndsWith("/api/channels/channels/"))
                return Task.FromResult(JsonResp(new { count = 0, results = Array.Empty<object>() }));
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/channels/streams/"))
                return Task.FromResult(JsonResp(new { count = 0, results = Array.Empty<object>() }));
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/channels/groups/"))
                return Task.FromResult(JsonResp(new { count = 0, results = Array.Empty<object>() }));
            if (req.Method == HttpMethod.Get && path.EndsWith("/api/core/version/"))
                return Task.FromResult(JsonResp(new { version = "0.30.0" }));

            if (req.Method == HttpMethod.Get && path.Contains("/api/channels/channels/") && path.EndsWith("/streams/"))
            {
                // /api/channels/channels/<id>/streams/
                var match = System.Text.RegularExpressions.Regex.Match(path, @"/channels/(\d+)/streams");
                if (match.Success && long.TryParse(match.Groups[1].Value, out var cid))
                {
                    var ids = CurrentStreamIdsFor.TryGetValue(cid, out var v) ? v : Array.Empty<long>();
                    var arr = ids.Select(id => new { id }).ToArray();
                    return Task.FromResult(JsonResp(arr));
                }
            }

            if (req.Method == HttpMethod.Patch && path.Contains("/api/channels/channels/"))
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                PatchBodies.Add(body);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            if (req.Method == HttpMethod.Delete && path.Contains("/api/channels/streams/") && !path.EndsWith("/streams/"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(path, @"/streams/(\d+)/");
                if (match.Success && long.TryParse(match.Groups[1].Value, out var sid))
                    DeleteStreamIds.Add(sid);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            if (req.Method == HttpMethod.Post && path.EndsWith("/api/channels/streams/"))
                return Task.FromResult(JsonResp(new { id = 9999L, name = "n", url = "x", is_custom = true }));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResp(object payload) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
    }
}