using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceSelection;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W4 (2026-09-19) — persistência e dedup intra-Source por fingerprint
/// (<c>docs/Reestructure/04-PLAYLIST-STREAM.md §5</c>,
/// <c>32-DOMAIN-SCHEMA.md</c> ChannelSource, <c>16-PERSISTENCE.md §3</c>).
///
/// <para>
/// D2: múltiplas streams por <c>(CanonicalChannel, Source)</c> são
/// suportadas. A identidade lógica persistente é
/// <c>CanonicalChannelId + SourceId + Fingerprint + FingerprintVersion</c>;
/// não é imposta unicidade em <c>(CanonicalChannelId, SourceId)</c>.
/// </para>
/// </summary>
public class WaveW4ChannelSourceFingerprintTests : IAsyncLifetime
{
    private const string PreviousMigration = "20260919130000_AddSourceAcquisitionFailure";

    private readonly string _dbPath;
    private ChannelCatalogBootstrapper _bootstrapper = null!;
    private CatalogResolver _resolver = null!;

    public WaveW4ChannelSourceFingerprintTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"wave-w4-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        _bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await _bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        _resolver = new CatalogResolver(new TestDbContextFactory(_dbPath), _dbPath);
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        return Task.CompletedTask;
    }

    // ════════════════════════════════════════════════════════════════
    // Dedup intra-Source
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Equivalent_urls_in_same_source_consolidate_into_one_channel_source()
    {
        var (channel, source) = await NewChannelAndSourceAsync("w4-a");

        var first = await _resolver.RecordChannelSourceAsync(
            channel.Id, source.Id, "http://HOST:80/live/alice/secretA/12345");
        var second = await _resolver.RecordChannelSourceAsync(
            channel.Id, source.Id, "http://host/live/bob/secretB/12345");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await CountChannelSourcesAsync(channel.Id, source.Id));
        Assert.NotNull(second.Fingerprint);
        Assert.Equal("sfp1", second.FingerprintVersion);
    }

    [Fact]
    public async Task Genuinely_distinct_urls_in_same_source_create_two_channel_sources()
    {
        var (channel, source) = await NewChannelAndSourceAsync("w4-b");

        await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, "http://host/live/u/p/1");
        await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, "http://host/live/u/p/2");
        await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, "http://host/other");

        Assert.Equal(3, await CountChannelSourcesAsync(channel.Id, source.Id));
    }

    [Fact]
    public async Task Same_fingerprint_in_different_sources_is_not_deduplicated()
    {
        var (channel, sourceA) = await NewChannelAndSourceAsync("w4-c-src-a");
        var sourceB = await _resolver.EnsureSourceAsync(
            "w4-c-src-b", "w4-c-src-b", SourceKind.M3U, "file:///w4-c", 0);

        const string url = "http://host/live/u/p/777";
        var a = await _resolver.RecordChannelSourceAsync(channel.Id, sourceA.Id, url);
        var b = await _resolver.RecordChannelSourceAsync(channel.Id, sourceB.Id, url);

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.Equal(2, await CountChannelSourcesAsync(channel.Id));
    }

    [Fact]
    public async Task Legacy_row_without_fingerprint_is_not_matched_by_sanitized_url()
    {
        var (channel, source) = await NewChannelAndSourceAsync("w4-d");

        var first = await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, "not-a-url");
        var second = await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, "not-a-url");

        // V1 — a identidade interna é o fingerprint. Um URL não-fingerprintável
        // não tem identidade e a URL sanitizada deixou de servir de fallback:
        // cria-se uma segunda row em vez de consolidar.
        Assert.NotEqual(first.Id, second.Id);
        Assert.Null(first.Fingerprint);
        Assert.Null(second.Fingerprint);
        Assert.Null(second.FingerprintVersion);
        Assert.Equal(2, await CountChannelSourcesAsync(channel.Id, source.Id));
    }

    [Fact]
    public async Task Legacy_row_is_enriched_when_a_fingerprintable_url_arrives()
    {
        var (channel, source) = await NewChannelAndSourceAsync("w4-e");

        var legacy = await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, "not-a-url");
        Assert.Null(legacy.Fingerprint);

        // URL fingerprintável: não colapsa com a row legacy (não há fallback
        // por URL sanitizada) e é persistida como nova row com fingerprint.
        var enriched = await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, "http://host/a");

        Assert.NotEqual(legacy.Id, enriched.Id);
        Assert.NotNull(enriched.Fingerprint);
        Assert.Equal("sfp1", enriched.FingerprintVersion);
    }

    [Fact]
    public async Task Fingerprint_and_version_are_persisted_and_match_the_independent_contract()
    {
        var (channel, source) = await NewChannelAndSourceAsync("w4-f");
        const string url = "https://Host.example:8443/live/alice/secret/99?b=2";

        var stored = await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, url);

        Assert.Equal(StreamFingerprint.TryComputeFingerprint(url), stored.Fingerprint);
        Assert.Equal(StreamFingerprint.Version, stored.FingerprintVersion);
        Assert.Equal(64, stored.Fingerprint!.Length);
    }

    [Fact]
    public async Task Persisted_fingerprint_never_contains_credentials()
    {
        var (channel, source) = await NewChannelAndSourceAsync("w4-sec");
        const string url = "http://alice:supersecret@host/live/alice/supersecret/1?password=supersecret&token=tok_abc";

        await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, url);

        await using var ctx = await _resolver.GetFactory().CreateDbContextAsync();
        var stored = await ctx.ChannelSources.AsNoTracking().SingleAsync(cs => cs.CanonicalChannelId == channel.Id);
        var fingerprint = stored.Fingerprint ?? string.Empty;

        Assert.DoesNotContain("supersecret", fingerprint);
        Assert.DoesNotContain("alice", fingerprint);
        Assert.DoesNotContain("tok_abc", fingerprint);
        Assert.DoesNotContain("http", fingerprint);
    }

    // ════════════════════════════════════════════════════════════════
    // Selection recebe o fingerprint persistido
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Source_selection_stage_populates_candidate_with_persisted_fingerprint()
    {
        var (channel, source) = await NewChannelAndSourceAsync("w4-g");
        const string url = "http://host/live/alice/secret/4242";
        var stored = await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, url);
        Assert.NotNull(stored.Fingerprint);

        var stage = new SourceSelectionStage(_resolver);
        var streams = new List<M3uStream>
        {
            new() { Url = url, Title = "Canal W4", IsWorking = true },
        };

        var result = await stage.ApplyAsync(
            streams,
            new SourceSelectionPolicy(MaxSourcesPerChannel: 10, PreferDistinctProviders: false));

        var selected = Assert.Single(result.Selected);
        Assert.Equal(stored.Fingerprint, selected.Candidate.StreamFingerprint);
    }

    [Fact]
    public async Task Playlist_composer_populates_candidate_with_persisted_fingerprint()
    {
        var (channel, source) = await NewChannelAndSourceAsync("w4-h");
        const string url = "http://host/live/alice/secret/5151";
        var stored = await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, url);

        var list = await _resolver.CreateOrderingListAsync("w4-h-list", "W4 H", "pt", null);
        await _resolver.AddOrderingItemAsync(list.Id, channel.Id);

        var capturing = new CapturingSelector();
        var composer = new PlaylistComposerService(_resolver.GetFactory(), capturing);

        var composition = await composer.ComposeAsync(list.Id);

        Assert.Single(composition.Entries);
        var candidate = Assert.Single(capturing.Candidates);
        Assert.Equal(stored.Fingerprint, candidate.StreamFingerprint);
    }

    [Fact]
    public async Task Legacy_row_with_null_fingerprint_is_unmatched_in_selection()
    {
        var (channel, source) = await NewChannelAndSourceAsync("w4-i");

        // Row legacy (criada antes de W4): URL http válida, fingerprint null.
        var now = DateTime.UtcNow;
        await using (var ctx = await _resolver.GetFactory().CreateDbContextAsync())
        {
            ctx.ChannelSources.Add(new ChannelSourceEntity
            {
                CanonicalChannelId = channel.Id,
                SourceId = source.Id,
                StreamUrl = "http://host/legacy",
                Quality = StreamQuality.Unknown,
                Epg = EpgState.Unknown,
                Availability = AvailabilityState.Discovered,
                MatchConfidence = 0,
                MatchMethod = "legacy",
                FirstSeenAtUtc = now,
                LastSeenAtUtc = now,
                LastTestedAtUtc = now,
                LastResponseTimeMs = 0,
                IsEnabled = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
            await ctx.SaveChangesAsync();
        }

        var stage = new SourceSelectionStage(_resolver);
        var streams = new List<M3uStream>
        {
            new() { Url = "http://host/legacy", Title = "Legacy", IsWorking = true },
        };

        var result = await stage.ApplyAsync(
            streams,
            new SourceSelectionPolicy(MaxSourcesPerChannel: 10, PreferDistinctProviders: false));

        // V2 — a junção é apenas por fingerprint. A row legacy não tem
        // fingerprint persistido, pelo que a stream não encontra
        // correspondência (sem fallback por URL sanitizada) e faz pass-through.
        Assert.Empty(result.Selected);
        Assert.Empty(result.Channels);
        Assert.Contains(result.Unmatched, s => s.Url == "http://host/legacy");
        Assert.Same(streams[0], result.Published.Single());
    }

    // ════════════════════════════════════════════════════════════════
    // Múltiplas streams por (canal, source) — sem unicidade imposta
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Multiple_streams_per_channel_and_source_are_supported()
    {
        var (channel, source) = await NewChannelAndSourceAsync("w4-j");

        for (var i = 0; i < 4; i++)
        {
            await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, $"http://host/stream/{i}");
        }

        Assert.Equal(4, await CountChannelSourcesAsync(channel.Id, source.Id));
    }

    // ════════════════════════════════════════════════════════════════
    // Migração aditiva
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Migration_is_additive_and_preserves_legacy_channel_source_rows()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"wave-w4-mig-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<ChannelCatalogDbContext>()
                .UseSqlite($"Data Source={dbPath};Cache=Private")
                .Options;

            await using (var ctx = new ChannelCatalogDbContext(options))
            {
                await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            }

            using (var conn = new SqliteConnection($"Data Source={dbPath};Cache=Private"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO canonical_channels (Key, DisplayName, EditorialCategory, EditorialGroup, PublicationPolicy, IsEnabled, CreatedAtUtc, UpdatedAtUtc) " +
                    "VALUES ('w4-legacy-ch','W4 Legacy',0,0,0,1,'2026-01-01 00:00:00','2026-01-01 00:00:00');" +
                    "INSERT INTO sources (Name, Key, Kind, Origin, IsEnabled, Priority, CreatedAtUtc, UpdatedAtUtc) " +
                    "VALUES ('W4 Legacy','w4-legacy-src',1,'file:///legacy',1,0,'2026-01-01 00:00:00','2026-01-01 00:00:00');" +
                    "INSERT INTO channel_sources (CanonicalChannelId, SourceId, StreamUrl, Quality, Epg, Availability, MatchConfidence, MatchMethod, FirstSeenAtUtc, LastSeenAtUtc, LastTestedAtUtc, LastResponseTimeMs, IsEnabled, CreatedAtUtc, UpdatedAtUtc) " +
                    "SELECT c.Id, s.Id, 'http://host/legacy', 0, 0, 0, 0, 'canonical-alias', '2026-01-01 00:00:00','2026-01-01 00:00:00','2026-01-01 00:00:00',0,1,'2026-01-01 00:00:00','2026-01-01 00:00:00' " +
                    "FROM canonical_channels c, sources s WHERE c.Key='w4-legacy-ch' AND s.Key='w4-legacy-src';";
                cmd.ExecuteNonQuery();
            }

            await using (var ctx = new ChannelCatalogDbContext(options))
            {
                await ctx.Database.MigrateAsync();
            }

            await using (var ctx = new ChannelCatalogDbContext(options))
            {
                var legacy = await ctx.ChannelSources.SingleAsync(cs => cs.StreamUrl == "http://host/legacy");
                Assert.Null(legacy.Fingerprint);
                Assert.Null(legacy.FingerprintVersion);
                Assert.Equal(1, await ctx.ChannelSources.CountAsync());
            }

            Assert.True(ColumnExists(dbPath, "channel_sources", "Fingerprint"));
            Assert.True(ColumnExists(dbPath, "channel_sources", "FingerprintVersion"));
        }
        finally
        {
            TestTempDb.Cleanup(dbPath);
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════════════

    private async Task<(CanonicalChannelEntity Channel, SourceEntity Source)> NewChannelAndSourceAsync(string key)
    {
        var channel = await _resolver.CreateCanonicalChannelAsync(
            key, key, EditorialCategory.Live, CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy.CreateEligible, true, new List<string>(), country: "pt");
        var source = await _resolver.EnsureSourceAsync(
            key, key, SourceKind.M3U, $"file:///{key}.m3u", 0);
        return (channel, source);
    }

    private async Task<int> CountChannelSourcesAsync(long canonicalChannelId, long sourceId)
    {
        await using var ctx = await _resolver.GetFactory().CreateDbContextAsync();
        return await ctx.ChannelSources.CountAsync(
            cs => cs.CanonicalChannelId == canonicalChannelId && cs.SourceId == sourceId);
    }

    private async Task<int> CountChannelSourcesAsync(long canonicalChannelId)
    {
        await using var ctx = await _resolver.GetFactory().CreateDbContextAsync();
        return await ctx.ChannelSources.CountAsync(cs => cs.CanonicalChannelId == canonicalChannelId);
    }

    private static bool ColumnExists(string dbPath, string table, string column)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Cache=Private");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private sealed class CapturingSelector : IChannelSourceSelector
    {
        private readonly IChannelSourceSelector _inner = new ChannelSourceSelector();

        public List<SelectionCandidate> Candidates { get; } = new();

        public SourceSelectionResult Select(
            IReadOnlyList<SelectionCandidate> candidates,
            SourceSelectionPolicy policy,
            SourceSelectionCriteria? criteria = null)
        {
            Candidates.AddRange(candidates);
            return _inner.Select(candidates, policy, criteria);
        }
    }
}
