using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.SourceSelection;
using m3uCrawler.Services.Sync;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE 13 (Wave 13-6) — Testes do artefacto
/// <see cref="DispatcharrSourceSelection"/>: projecção determinística do
/// resultado do <see cref="SourceSelectionStage"/> (ordem, identidade por
/// <c>CanonicalChannelKey</c>, contagens exactas) e sanitização de
/// credenciais na serialização.
///
/// <para>
/// Sem rede e sem base de dados: os resultados do stage são construídos à
/// mão, o que torna cada asserção independente do selector (de-tautologização).
/// </para>
/// </summary>
public class DispatcharrSourceSelectionTests
{
    private static readonly DateTime FixedNow =
        new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

    private static readonly SourceSelectionPolicy Policy = new(10, true, null, true);

    // ---------------- helpers ----------------

    private static SelectionCandidate Candidate(
        string url,
        long sourceId = 1,
        string provider = "prov-a",
        int priority = 0)
        => new(
            StreamUrl: url,
            SourceId: sourceId,
            SourcePriority: priority,
            Quality: StreamQuality.Unknown,
            Epg: EpgState.Unknown,
            Availability: AvailabilityState.Reachable,
            LastResponseTimeMs: 0,
            ExternalStreamId: null,
            Provider: ProviderIdentity.Normalize(provider),
            IsWorking: true);

    private static SelectedSource Selected(
        string url,
        long sourceId,
        int rank,
        string reason,
        string provider = "prov-a")
        => new(Candidate(url, sourceId, provider, priority: 100 - rank), rank, reason);

    private static M3uStream Stream(string url)
        => new() { Url = url, Title = "Canal", Group = "PT", IsWorking = true };

    private static SourceSelectionChannelResult Channel(
        long id,
        string key,
        IReadOnlyList<SelectedSource> selected,
        IReadOnlyList<RejectedSource> rejected)
        => new(id, key, Policy, selected, rejected);

    private static DispatcharrSourceSelection SingleChannelResult(
        string key,
        string url,
        long sourceId = 7)
    {
        var selected = new[] { Selected(url, sourceId, 0, SelectionReasons.Fill) };
        var result = new SourceSelectionStageResult(
            Published: Array.Empty<M3uStream>(),
            Selected: selected,
            Rejected: Array.Empty<RejectedSource>(),
            Unmatched: Array.Empty<M3uStream>(),
            MatchedChannelCount: 1,
            AmbiguousCount: 0,
            Applied: true)
        {
            Channels = new[] { Channel(1, key, selected, Array.Empty<RejectedSource>()) },
        };
        return DispatcharrSourceSelectionFactory.FromStageResult(result, policies: null, FixedNow);
    }

    // ---------------- factory projection ----------------

    [Fact]
    public void Factory_preserves_channel_order_and_uses_canonical_key_as_identity()
    {
        var selA0 = Selected("http://one.example/a0.ts", 1, 0, SelectionReasons.Diversity);
        var selA1 = Selected("http://two.example/a1.ts", 2, 1, SelectionReasons.Fill, "prov-b");
        var rejA = new RejectedSource(Candidate("http://one.example/a2.ts", 3), SelectionReasons.LimitReached);
        var selB0 = Selected("http://three.example/b0.ts", 4, 0, SelectionReasons.Diversity, "prov-c");

        var chA = Channel(11, "key-a", new[] { selA0, selA1 }, new[] { rejA });
        var chB = Channel(22, "key-b", new[] { selB0 }, Array.Empty<RejectedSource>());

        var result = new SourceSelectionStageResult(
            Published: Array.Empty<M3uStream>(),
            Selected: new[] { selA0, selA1, selB0 },
            Rejected: new[] { rejA },
            Unmatched: Array.Empty<M3uStream>(),
            MatchedChannelCount: 2,
            AmbiguousCount: 0,
            Applied: true)
        {
            Channels = new[] { chA, chB },
        };

        var selection = DispatcharrSourceSelectionFactory.FromStageResult(result, policies: null, FixedNow);

        Assert.Equal(FixedNow.ToString("o"), selection.GeneratedAtUtc);

        // Ordem preservada e identidade é a Key (não o Id).
        Assert.Equal(new[] { "key-a", "key-b" },
            selection.Channels.Select(c => c.CanonicalChannelKey).ToArray());
        // O Id é apenas transportado.
        Assert.Equal(new long?[] { 11, 22 },
            selection.Channels.Select(c => c.CanonicalChannelId).ToArray());

        var mappedA = selection.Channels[0];
        Assert.Equal(
            new[]
            {
                ("http://one.example/a0.ts", 0, "prov-a", SelectionReasons.Diversity, 1L),
                ("http://two.example/a1.ts", 1, "prov-b", SelectionReasons.Fill, 2L),
            },
            mappedA.Selected.Select(s => (s.StreamUrl, s.Rank, s.Provider, s.Reason, s.SourceId)).ToArray());
        Assert.Equal(3, mappedA.CandidateCount);
        Assert.Equal(1, mappedA.RejectedCount);
        Assert.Null(mappedA.PolicyScope);

        var mappedB = selection.Channels[1];
        Assert.Equal(1, mappedB.CandidateCount);
        Assert.Equal(0, mappedB.RejectedCount);
    }

    [Fact]
    public void Factory_counts_are_exact_and_unmatched_is_disjoint_from_ambiguous()
    {
        var selA = Selected("http://one.example/a0.ts", 1, 0, SelectionReasons.Diversity);
        var rejA1 = new RejectedSource(Candidate("http://one.example/a1.ts", 2), SelectionReasons.LimitReached);
        var rejA2 = new RejectedSource(Candidate("http://one.example/a2.ts", 3), SelectionReasons.LimitReached);
        var selB = Selected("http://two.example/b0.ts", 4, 0, SelectionReasons.Diversity, "prov-b");

        var chA = Channel(11, "key-a", new[] { selA }, new[] { rejA1, rejA2 });
        var chB = Channel(22, "key-b", new[] { selB }, Array.Empty<RejectedSource>());

        var unmatched = new[]
        {
            Stream("http://u1.example/x.ts"),
            Stream("http://u2.example/x.ts"),
            Stream("http://u3.example/x.ts"),
            Stream("http://amb1.example/x.ts"),
            Stream("http://amb2.example/x.ts"),
        };
        // Ambíguas são um subconjunto (referências iguais) de Unmatched.
        var ambiguous = new[] { unmatched[3], unmatched[4] };

        var result = new SourceSelectionStageResult(
            Published: Array.Empty<M3uStream>(),
            Selected: new[] { selA, selB },
            Rejected: new[] { rejA1, rejA2 },
            Unmatched: unmatched,
            MatchedChannelCount: 2,
            AmbiguousCount: ambiguous.Length,
            Applied: true)
        {
            Channels = new[] { chA, chB },
            AmbiguousStreams = ambiguous,
        };

        var counts = DispatcharrSourceSelectionFactory
            .FromStageResult(result, policies: null, FixedNow)
            .Counts;

        Assert.Equal(2, counts.Channels);
        Assert.Equal(4, counts.Candidates); // chA: 3 (1 sel + 2 rej) + chB: 1 (1 sel + 0 rej)
        Assert.Equal(2, counts.Selected);
        Assert.Equal(2, counts.Rejected);
        Assert.Equal(3, counts.Unmatched);  // 5 - 2 ambíguas
        Assert.Equal(2, counts.Ambiguous);

        // Partição: unmatched + ambiguous == total unmatched, sem sobreposição.
        Assert.Equal(unmatched.Length, counts.Unmatched + counts.Ambiguous);
        Assert.Equal(unmatched.Length - ambiguous.Length, counts.Unmatched);
        Assert.All(ambiguous, a => Assert.Contains(a, unmatched));
    }

    [Fact]
    public void Factory_maps_policy_scope_override_global_default_and_null()
    {
        var selected = new[] { Selected("http://one.example/a0.ts", 1, 0, SelectionReasons.Diversity) };
        var result = new SourceSelectionStageResult(
            Published: Array.Empty<M3uStream>(),
            Selected: selected,
            Rejected: Array.Empty<RejectedSource>(),
            Unmatched: Array.Empty<M3uStream>(),
            MatchedChannelCount: 2,
            AmbiguousCount: 0,
            Applied: true)
        {
            Channels = new[]
            {
                Channel(1, "key-a", selected, Array.Empty<RejectedSource>()),
                Channel(2, "key-b", selected, Array.Empty<RejectedSource>()),
            },
        };

        var global = new SourceSelectionPolicy(10, true, null, true);
        var overridePolicy = new SourceSelectionPolicy(1, false, 4, false);

        var withOverride = new SourceSelectionPolicySet(
            global,
            new Dictionary<string, SourceSelectionPolicy> { ["key-a"] = overridePolicy },
            hasExplicitGlobal: true);
        var s1 = DispatcharrSourceSelectionFactory.FromStageResult(result, withOverride, FixedNow);
        Assert.Equal("override", s1.Channels.Single(c => c.CanonicalChannelKey == "key-a").PolicyScope);
        Assert.Equal("global", s1.Channels.Single(c => c.CanonicalChannelKey == "key-b").PolicyScope);

        var implicitGlobal = new SourceSelectionPolicySet(global, hasExplicitGlobal: false);
        var s2 = DispatcharrSourceSelectionFactory.FromStageResult(result, implicitGlobal, FixedNow);
        Assert.Equal("default", s2.Channels.Single(c => c.CanonicalChannelKey == "key-b").PolicyScope);

        var noPolicies = DispatcharrSourceSelectionFactory.FromStageResult(result, policies: null, FixedNow);
        Assert.All(noPolicies.Channels, c => Assert.Null(c.PolicyScope));
    }

    // ---------------- serializer ----------------

    [Fact]
    public void Serializer_sanitizes_credentials_and_deserializes_the_sanitized_value()
    {
        const string raw = "http://user:secret@host/live/USER/PASS/1";
        var selection = SingleChannelResult("key-sanitize", raw);

        var json = DispatcharrSourceSelectionSerializer.Serialize(selection);

        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("USER", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PASS", json, StringComparison.Ordinal);
        Assert.Contains("***", json, StringComparison.Ordinal);

        var expected = CredentialSanitizer.SanitizeUrl(raw);
        var roundTripped = DispatcharrSourceSelectionSerializer.Deserialize(json);
        Assert.NotNull(roundTripped);
        var streamUrl = roundTripped!.Channels.Single().Selected.Single().StreamUrl;
        Assert.Equal(expected, streamUrl);
        Assert.Contains("***", streamUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", streamUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PASS", streamUrl, StringComparison.Ordinal);

        // Metadados não-sensíveis do candidato sobrevivem ao round-trip.
        Assert.Equal(0, roundTripped.Channels.Single().Selected.Single().Rank);
        Assert.Equal(7L, roundTripped.Channels.Single().Selected.Single().SourceId);
        Assert.Equal("key-sanitize", roundTripped.Channels.Single().CanonicalChannelKey);
    }

    [Fact]
    public async Task Serializer_WriteAsync_sanitizes_on_disk_and_ReadAsync_round_trips()
    {
        const string raw = "http://user:secret@host/live/USER/PASS/1";
        var selection = SingleChannelResult("key-write", raw);
        var path = Path.Combine(Path.GetTempPath(), $"selection-write-{Guid.NewGuid():N}.json");
        try
        {
            await DispatcharrSourceSelectionSerializer.WriteAsync(selection, path);

            var onDisk = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("secret", onDisk, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("USER", onDisk, StringComparison.Ordinal);
            Assert.DoesNotContain("PASS", onDisk, StringComparison.Ordinal);
            Assert.Contains("***", onDisk, StringComparison.Ordinal);

            var read = await DispatcharrSourceSelectionSerializer.ReadAsync(path);
            Assert.NotNull(read);
            Assert.Equal(
                CredentialSanitizer.SanitizeUrl(raw),
                read!.Channels.Single().Selected.Single().StreamUrl);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Serializer_empty_selection_serializes_and_round_trips_cleanly()
    {
        var empty = new DispatcharrSourceSelection
        {
            GeneratedAtUtc = FixedNow.ToString("o"),
            Channels = Array.Empty<ChannelSourceSelection>(),
            Counts = new SelectionCounts(),
        };

        var json = DispatcharrSourceSelectionSerializer.Serialize(empty);
        var roundTripped = DispatcharrSourceSelectionSerializer.Deserialize(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(FixedNow.ToString("o"), roundTripped!.GeneratedAtUtc);
        Assert.Empty(roundTripped.Channels);
        Assert.Equal(0, roundTripped.Counts.Channels);
        Assert.Equal(0, roundTripped.Counts.Candidates);
        Assert.Equal(0, roundTripped.Counts.Selected);
        Assert.Equal(0, roundTripped.Counts.Rejected);
        Assert.Equal(0, roundTripped.Counts.Unmatched);
        Assert.Equal(0, roundTripped.Counts.Ambiguous);
    }
}
