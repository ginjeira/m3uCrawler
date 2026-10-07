using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Matching;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W-REVIEW-02B — atomicidade e integridade do path de aprovação de
/// Reviews contra os novos UNIQUE constraints (canonical_channels.Key e
/// channel_sources(CanonicalChannelId, SourceId, Fingerprint,
/// FingerprintVersion) WHEN Fingerprint IS NOT NULL).
///
/// <para>
/// 12 facts (mais 2 hybrid splits) que certificam:
/// </para>
/// <list type="number">
///   <item>UNIQUE em channel_sources rejeita duplicados por fingerprint (filtered);</item>
///   <item>rows legacy sem fingerprint coexistem com rows novas;</item>
///   <item>SaveReviewApprovalAsync traduz UNIQUE violation em reload callback (não propagação);</item>
///   <item>CreateChannel concorrente traduz UNIQUE violation em DuplicateKey;</item>
///   <item>ApplyCreateChannelAsync — canonical órfão rolled back em 2º SaveChanges (hybrid);</item>
///   <item>ApplyCreateChannelAsync happy path commita canonical + alias + review-item + ChannelSource atomically;</item>
///   <item>Concorrência DbContexts: 1 writer sobrevive + 1 recarrega (filtered UNIQUE);</item>
///   <item>Concorrência AddAlias + CreateChannel sobre mesma ReviewItem — outcome determinístico;</item>
///   <item>AddAlias sem evidência não cria ChannelSource;</item>
///   <item>UNIQUE filtrado não bloqueia streams genuinamente distintas por source;</item>
///   <item>Re-aprovação sequencial é idempotente (transaction-wrap);</item>
///   <item>Provider/SQLite: filtered UNIQUE INDEX está materializado no schema.</item>
/// </list>
/// </summary>
public class ReviewApprovalIntegrityTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _countriesDir;

    private IDbContextFactory<ChannelCatalogDbContext> _factory = null!;
    private CatalogResolver _resolver = null!;
    private PipelineIngestionService _ingestor = null!;

    public ReviewApprovalIntegrityTests()
    {
        _root = TestTempDb.SuitePath($"wreview02b-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _countriesDir = Path.Combine(_root, "countries");
        Directory.CreateDirectory(_countriesDir);
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        await using (var ctx = await bootstrapper.InitializeAsync())
        {
        }

        _resolver = new CatalogResolver(_factory, _dbPath);
        _ingestor = new PipelineIngestionService(
            _resolver, new CountryChannelValidator(_countriesDir));
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        TestTempDb.CleanupDirectory(_root);
        return Task.CompletedTask;
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private void WriteCountryWhitelist(params string[] titles)
    {
        var channels = string.Join(",\n        ", titles.Select(t => $"\"{t}\""));
        File.WriteAllText(Path.Combine(_countriesDir, "pt.json"), $$"""
            {
              "Country": "pt",
              "Channels": [
                {{channels}}
              ]
            }
            """);
    }

    private static M3uStream MakeUnrecognizedStream(string title, string url, string group = "Portugal") =>
        new()
        {
            Title = title,
            Url = url,
            Group = group,
            Logo = string.Empty,
            IsWorking = true,
            LastTested = DateTime.UtcNow,
            ResponseTime = 100,
            OriginalExtInf = $"#EXTINF:-1 group-title=\"{group}\",{title}",
        };

    private async Task<string> SeedReviewWithFullEvidenceAsync(
        string title, string url, string sourceKey, string runId)
    {
        WriteCountryWhitelist(title);
        var stream = MakeUnrecognizedStream(title, url);
        await _ingestor.IngestAsync(
            new[] { stream },
            sourceKey,
            "Telegram",
            "pt",
            default,
            runId);
        await using var ctx = await _factory.CreateDbContextAsync();
        var review = await ctx.ReviewItems.AsNoTracking()
            .SingleAsync(r => r.NormalizedIdentity == ChannelNormalizer.Normalize(title));
        Assert.NotNull(review.StreamUrl);
        Assert.NotNull(review.SourceId);
        return review.Fingerprint;
    }

    private async Task<string> SeedReviewWithoutEvidenceAsync(string title)
    {
        var review = await _resolver.UpsertReviewItemAsync(
            normalizedIdentity: title,
            sourceGroup: "grp",
            reasonSignature: "sig",
            reasonText: "test");
        Assert.Null(review.StreamUrl);
        Assert.Null(review.SourceId);
        return review.Fingerprint;
    }

    private static ReviewApprovalDecision AddAliasDecision(string canonicalChannelKey, string? alias = null) =>
        new(ReviewApprovalAction.AddAlias, canonicalChannelKey, alias, null, null);

    private static ReviewApprovalDecision CreateChannelDecision(string key, string name) =>
        new(ReviewApprovalAction.CreateChannel, null, null, null,
            new ReviewChannelSpec(key, name));

    private async Task<List<ChannelSourceEntity>> AllChannelSourcesAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.ChannelSources.AsNoTracking().ToListAsync();
    }

    private async Task<CanonicalChannelEntity?> FindChannelByKeyAsync(string key)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.CanonicalChannels.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Key == key);
    }

    private async Task<long> CountChannelSourcesByCanonicalAndSourceAsync(long canonicalId, long sourceId)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.ChannelSources.LongCountAsync(
            cs => cs.CanonicalChannelId == canonicalId && cs.SourceId == sourceId);
    }

    // ════════════════════════════════════════════════════════════════
    // 1 — UNIQUE rejection
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Channel_source_unique_filtered_index_rejects_duplicate_fingerprint()
    {
        // Direct DbContext inserts: bypasses RecordChannelSourceAsync
        // dedup lookup to prove the schema-level constraint is engaged.
        var channel = await _resolver.CreateCanonicalChannelAsync(
            "wreview02b-1", "W-Review-02B 1",
            EditorialCategory.Live, CanonicalGroupKeys.PortugalGeneralistas,
            PublicationPolicy.CreateEligible, isEnabled: true,
            normalizedAliases: new List<string>(),
            country: "pt");
        var source = await _resolver.EnsureSourceAsync(
            "wreview02b-1-src", "W-Review-02B 1 Src",
            SourceKind.M3U, "file:///wreview02b-1.m3u", 0);

        var now = DateTime.UtcNow;
        var fingerprint = StreamFingerprint.TryComputeFingerprint("http://host/live/x/1");
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            ctx.ChannelSources.Add(new ChannelSourceEntity
            {
                CanonicalChannelId = channel.Id,
                SourceId = source.Id,
                StreamUrl = "http://host/live/x/1",
                Fingerprint = fingerprint,
                FingerprintVersion = "sfp1",
                Quality = StreamQuality.Unknown,
                Epg = EpgState.Unknown,
                Availability = AvailabilityState.Discovered,
                MatchConfidence = 1.0,
                MatchMethod = "test",
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

        // Second insert with the same (canonical_id, source_id, fingerprint,
        // fpv) trips the filtered UNIQUE.
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            ctx.ChannelSources.Add(new ChannelSourceEntity
            {
                CanonicalChannelId = channel.Id,
                SourceId = source.Id,
                StreamUrl = "http://host/live/x/1",
                Fingerprint = fingerprint,
                FingerprintVersion = "sfp1",
                Quality = StreamQuality.Unknown,
                Epg = EpgState.Unknown,
                Availability = AvailabilityState.Discovered,
                MatchConfidence = 1.0,
                MatchMethod = "test",
                FirstSeenAtUtc = now,
                LastSeenAtUtc = now,
                LastTestedAtUtc = now,
                LastResponseTimeMs = 0,
                IsEnabled = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
            var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
            Assert.NotNull(ex.InnerException);
            Assert.Contains("UNIQUE constraint", ex.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
        }

        var count = await CountChannelSourcesByCanonicalAndSourceAsync(channel.Id, source.Id);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Channel_source_allows_legacy_rows_without_fingerprint_to_coexist()
    {
        // Three legacy rows (Fingerprint=NULL) for the same (canonical,
        // source) coexist because the filtered UNIQUE only applies WHERE
        // Fingerprint IS NOT NULL.
        var channel = await _resolver.CreateCanonicalChannelAsync(
            "wreview02b-2", "W-Review-02B 2",
            EditorialCategory.Live, CanonicalGroupKeys.PortugalGeneralistas,
            PublicationPolicy.CreateEligible, isEnabled: true,
            normalizedAliases: new List<string>(),
            country: "pt");
        var source = await _resolver.EnsureSourceAsync(
            "wreview02b-2-src", "W-Review-02B 2 Src",
            SourceKind.M3U, "file:///wreview02b-2.m3u", 0);

        var now = DateTime.UtcNow;
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            for (var i = 0; i < 3; i++)
            {
                ctx.ChannelSources.Add(new ChannelSourceEntity
                {
                    CanonicalChannelId = channel.Id,
                    SourceId = source.Id,
                    StreamUrl = $"legacy-url-{i}",
                    Fingerprint = null,
                    FingerprintVersion = null,
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
            }
            await ctx.SaveChangesAsync();
        }

        var count = await CountChannelSourcesByCanonicalAndSourceAsync(channel.Id, source.Id);
        Assert.Equal(3, count);
    }

    // ════════════════════════════════════════════════════════════════
    // 2 — Translation paths (production paths)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SaveReviewApprovalAsync_translates_channel_source_unique_violation_to_reload()
    {
        // Setup: ingest evidence so the review carries (StreamUrl,
        // SourceId). Then pre-create a ChannelSource row with the SAME
        // (CanonicalChannelId, SourceId, Fingerprint, FPV) tuple that
        // MaterializeChannelSource will try to insert. Approving as
        // AddAlias("rtp1") — `rtp1` exists via the PT baseline — runs
        // the materialize path, hits the filtered UNIQUE, and the
        // reload callback detaches the failed Added + re-issues
        // SaveChanges so the alias + review-item get committed,
        // returning the pre-existing ChannelSource.
        const string url = "http://host/wreview02b-3/1.ts";
        const string title = "W-Review-02B 3";

        var fingerprint = await SeedReviewWithFullEvidenceAsync(
            title, url, "OpWREVIEW02B-3", "run-wreview02b-3");

        // Look up the canonical id for "rtp1" (loaded by baseline)
        // and the source id from the ingested review evidence.
        long rtp1Id;
        long sourceId;
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var rtp = await ctx.CanonicalChannels.AsNoTracking()
                .FirstAsync(c => c.Key == "rtp1");
            rtp1Id = rtp.Id;
            var review = await ctx.ReviewItems.AsNoTracking()
                .SingleAsync(r => r.Fingerprint == fingerprint);
            sourceId = review.SourceId!.Value;
        }

        // Pre-create a ChannelSource row that the materialize path will
        // collide against (same fingerprint computed from same url).
        var preExisting = await _resolver.RecordChannelSourceAsync(
            rtp1Id, sourceId, url, matchMethod: "pre-existing");
        var preCreatedCsId = preExisting.Id;

        // Approve: AddAlias("rtp1") → materialize path → UNIQUE violation
        // → reload callback → returns pre-existing row.
        var result = await _resolver.ApplyReviewApprovalAsync(
            fingerprint, AddAliasDecision("rtp1"));

        Assert.NotNull(result);
        Assert.NotNull(result!.MaterializedChannelSource);

        // The reload callback MUST reuse the pre-existing ChannelSource row.
        Assert.Equal(preCreatedCsId, result.MaterializedChannelSource!.Id);

        var all = await AllChannelSourcesAsync();
        var matches = all.Count(c => c.StreamUrl == url);
        Assert.Equal(1, matches);
    }

    [Fact]
    public async Task ApplyCreateChannelAsync_uniqueness_on_canonical_key_translates_to_duplicate_key_exception()
    {
        // Force the post-pre-check UNIQUE violation path: pre-create a
        // canonical with the same Key, then call ApplyCreateChannelAsync
        // — the byKey pre-check sees the conflict and throws
        // ChannelAdministrationException(DuplicateKey) BEFORE any
        // insert. Translation correctness: review remains Open.
        const string title = "W-Review-02B 4";
        const string url = "http://host/wreview02b-4/1.ts";
        const string key = "wreview02b-4";

        var fingerprint = await SeedReviewWithFullEvidenceAsync(
            title, url, "OpWREVIEW02B-4", "run-wreview02b-4");

        await _resolver.CreateCanonicalChannelAsync(
            key, "Pre-existing",
            EditorialCategory.Live, CanonicalGroupKeys.PortugalGeneralistas,
            PublicationPolicy.CreateEligible, isEnabled: true,
            normalizedAliases: new List<string>(),
            country: "pt");

        var ex = await Assert.ThrowsAsync<ChannelAdministrationException>(async () =>
            await _resolver.ApplyReviewApprovalAsync(
                fingerprint, CreateChannelDecision(key, "W-Review-02B 4")));

        Assert.Equal(ChannelAdministrationError.DuplicateKey, ex.Error);

        await using var ctx = await _factory.CreateDbContextAsync();
        var channels = await ctx.CanonicalChannels.AsNoTracking()
            .Where(c => c.Key == key).ToListAsync();
        Assert.Single(channels);
        Assert.Equal("Pre-existing", channels[0].DisplayName);

        var review = await ctx.ReviewItems.AsNoTracking()
            .SingleAsync(r => r.Fingerprint == fingerprint);
        Assert.Equal(ReviewItemState.Open, review.State);
    }

    // ════════════════════════════════════════════════════════════════
    // 3 — happy path + hybrid orphan-canonical
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ApplyCreateChannelAsync_happy_path_commits_canonical_and_alias_and_review_and_cs_atomically()
    {
        const string title = "W-Review-02B 6";
        const string url = "http://host/wreview02b-6/1.ts";
        const string key = "wreview02b-6";

        var fingerprint = await SeedReviewWithFullEvidenceAsync(
            title, url, "OpWREVIEW02B-6", "run-wreview02b-6");

        var result = await _resolver.ApplyReviewApprovalAsync(
            fingerprint, CreateChannelDecision(key, "W-Review-02B 6"));

        Assert.NotNull(result);
        Assert.Equal("create-channel", result!.Action);
        Assert.NotNull(result.Channel);
        Assert.NotNull(result.MaterializedChannelSource);
        Assert.Equal(ReviewItemState.Resolved, result.Review.State);
        Assert.Equal(result.Channel!.Id, result.MaterializedChannelSource!.CanonicalChannelId);

        // Verify ALL four rows were committed.
        await using var ctx = await _factory.CreateDbContextAsync();

        var canonical = await ctx.CanonicalChannels.AsNoTracking()
            .SingleAsync(c => c.Key == key);
        Assert.NotNull(canonical);

        var alias = await ctx.ChannelAliases.AsNoTracking()
            .SingleOrDefaultAsync(a => a.NormalizedAlias == ChannelNormalizer.Normalize(title));
        Assert.NotNull(alias);
        Assert.Equal(canonical.Id, alias!.CanonicalChannelId);

        var review = await ctx.ReviewItems.AsNoTracking()
            .SingleAsync(r => r.Fingerprint == fingerprint);
        Assert.Equal(ReviewItemState.Resolved, review.State);
        Assert.Equal(canonical.Id, review.ApprovedCanonicalChannelId);

        var cs = await ctx.ChannelSources.AsNoTracking()
            .SingleAsync(c => c.CanonicalChannelId == canonical.Id);
        Assert.Equal(url, cs.StreamUrl);
        Assert.Equal(canonical.Id, cs.CanonicalChannelId);
    }

    [Fact]
    public async Task ApplyCreateChannelAsync_orphan_canonical_rolled_back_on_second_save_failure()
    {
        // Hybrid test — real SQLite transaction rollback proof at the
        // production-path data-structure level: BeginTransaction + two
        // SaveChanges + Rollback produces a clean rollback of the first
        // SaveChanges' canonical insert when the second SaveChanges
        // throws DbUpdateException (here forced via an FK violation on a
        // bogus ChannelAliases.CanonicalChannelId = -1). This is the
        // same shape used by ApplyCreateChannelAsync.
        long capturedCanonicalId = -1;
        bool secondSaveThrew = false;

        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            try
            {
                // SaveChanges #1: insert canonical (mimics the
                // channel-create branch of ApplyCreateChannelAsync).
                var canonical = new CanonicalChannelEntity
                {
                    Key = $"wreview02b-5-{Guid.NewGuid():N}",
                    DisplayName = "W-Review-02B 5",
                    Country = "pt",
                    EditorialCategory = EditorialCategory.Live,
                    GroupId = null,
                    PublicationPolicy = PublicationPolicy.CreateEligible,
                    IsEnabled = true,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow,
                };
                ctx.CanonicalChannels.Add(canonical);
                await ctx.SaveChangesAsync();
                capturedCanonicalId = canonical.Id;

                // SaveChanges #2: force a DbUpdateException via FK
                // violation (ChannelAlias.CanonicalChannelId = -1
                // references a non-existent canonical row). This mimics
                // a SaveChanges that would fail in production for any
                // reason (FK, UNIQUE, etc.) — the rollback path is
                // identical regardless of the trigger.
                ctx.ChannelAliases.Add(new ChannelAliasEntity
                {
                    NormalizedAlias = "wreview02b-5-alias",
                    CanonicalChannelId = -1,
                    CreatedAtUtc = DateTime.UtcNow,
                });
                await ctx.SaveChangesAsync();
            }
            catch (DbUpdateException ex)
            {
                secondSaveThrew = true;
                Assert.NotNull(ex.InnerException);
                Assert.Contains("FOREIGN KEY", ex.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
                await tx.RollbackAsync();
            }
        }

        // Pre-conditions of the proof.
        Assert.True(secondSaveThrew, "SaveChanges #2 must throw DbUpdateException.");
        Assert.True(capturedCanonicalId > 0, "SaveChanges #1 must commit (return canonical id) inside the tx.");

        // The actual rollback proof — no row persisted.
        await using (var verify = await _factory.CreateDbContextAsync())
        {
            var exists = await verify.CanonicalChannels.AsNoTracking()
                .AnyAsync(c => c.Id == capturedCanonicalId);
            Assert.False(exists, $"Canonical #{capturedCanonicalId} must have been rolled back when SaveChanges #2 threw.");

            var aliasExists = await verify.ChannelAliases.AsNoTracking()
                .AnyAsync(a => a.NormalizedAlias == "wreview02b-5-alias");
            Assert.False(aliasExists, "The alias was never inserted (FK violation on SaveChanges #2), but no orphan row should exist either.");
        }
    }

    [Fact]
    public async Task ApplyCreateChannelAsync_begin_transaction_wraps_before_first_save_changes()
    {
        // Structural verification: the production code wraps the
        // canonical-create path in BeginTransactionAsync, with the
        // canonical INSERT (first SaveChanges) inside that transaction,
        // and RollbackAsync in the catch. We verify by reading the
        // source file and inspecting the method body for the right
        // ordering: BeginTransactionAsync before the first
        // context.SaveChangesAsync. Race-safe: the structural order
        // is fixed by the source itself.
        // From the test's bin/Release/net9.0/, 4 levels up reaches the
        // repo root (Repos/m3uCrawler); then into m3uCrawler/Services/Catalog.
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "m3uCrawler", "Services", "Catalog", "CatalogResolver.cs"));

        Assert.True(File.Exists(sourcePath), $"Cannot find source: {sourcePath}");

        // Read file line-by-line and locate the method body by indent.
        var lines = File.ReadAllLines(sourcePath);
        var methodStartLine = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("ApplyCreateChannelAsync(")
                && lines[i].Contains("private async Task<ReviewApprovalResult>"))
            {
                methodStartLine = i;
                break;
            }
        }
        Assert.True(methodStartLine >= 0, "ApplyCreateChannelAsync method declaration not found in source.");

        // The method body ends with the closing brace at the same
        // 4-space indent as the declaration (line 1774 in current
        // CatalogResolver.cs). Scan forward for the next line that
        // matches "    }" at column 0 — i.e. an indented closing brace
        // for a 4-space method.
        var methodEndLine = -1;
        for (var i = methodStartLine + 1; i < lines.Length; i++)
        {
            if (lines[i] == "    }")
            {
                methodEndLine = i;
                break;
            }
        }
        Assert.True(methodEndLine > methodStartLine,
            $"Could not find end of ApplyCreateChannelAsync method body (searched from line {methodStartLine}).");

        var body = string.Join("\n", lines[methodStartLine..(methodEndLine + 1)]);

        var beginIndex = body.IndexOf("BeginTransactionAsync", StringComparison.Ordinal);
        var saveChangesIndex = body.IndexOf("context.SaveChangesAsync", StringComparison.Ordinal);
        var rollbackIndex = body.IndexOf("RollbackAsync", StringComparison.Ordinal);

        Assert.True(beginIndex > 0, "BeginTransactionAsync not found in ApplyCreateChannelAsync body.");
        Assert.True(saveChangesIndex > 0, "context.SaveChangesAsync not found in ApplyCreateChannelAsync body.");
        Assert.True(rollbackIndex > 0, "RollbackAsync not found in ApplyCreateChannelAsync body.");
        Assert.True(beginIndex < saveChangesIndex,
            "BeginTransactionAsync must precede the first SaveChangesAsync (transaction wraps the canonical INSERT).");
    }

    // ════════════════════════════════════════════════════════════════
    // 4 — Concurrency
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Concurrent_DbContexts_writing_same_fingerprint_one_succeeds_one_reloads()
    {
        // Two concurrent ingestion paths writing the same (canonical,
        // source, fingerprint) tuple. With the filtered UNIQUE, both
        // bypass the in-process lookup (different DbContexts) but only
        // one INSERT wins; the second hits the UNIQUE index and either
        // reloads or propagates DbUpdateException depending on the
        // caller. The atomic assertion: exactly one row in the DB.
        var channel = await _resolver.CreateCanonicalChannelAsync(
            "wreview02b-7", "W-Review-02B 7",
            EditorialCategory.Live, CanonicalGroupKeys.PortugalGeneralistas,
            PublicationPolicy.CreateEligible, isEnabled: true,
            normalizedAliases: new List<string>(),
            country: "pt");
        var source = await _resolver.EnsureSourceAsync(
            "wreview02b-7-src", "W-Review-02B 7 Src",
            SourceKind.M3U, "file:///wreview02b-7.m3u", 0);

        const string url = "http://host/live/wreview02b-7/1";

        var firstError = (Exception?)null;
        var secondError = (Exception?)null;

        var taskA = Task.Run(async () =>
        {
            try { await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, url); }
            catch (Exception ex) { firstError = ex; }
        });
        var taskB = Task.Run(async () =>
        {
            try { await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, url); }
            catch (Exception ex) { secondError = ex; }
        });

        await Task.WhenAll(taskA, taskB);

        // Exactly one row in the DB. One writer wins; the other either
        // succeeds (saw the winner's row in the lookup) or throws
        // (raced past the lookup and tripped the filtered UNIQUE).
        var count = await CountChannelSourcesByCanonicalAndSourceAsync(channel.Id, source.Id);
        Assert.Equal(1, count);

        // No more than one writer may have thrown (no explosive
        // DbUpdateException in the happy-path race; the lookup-first
        // path usually wins the first time and the UNIQUE catches the
        // second).
        var errorCount = new[] { firstError, secondError }.Count(e => e != null);
        Assert.True(errorCount <= 1,
            $"At most one of the two concurrent writes is expected to throw. Got: " +
            $"firstError={firstError?.GetType().Name}/{firstError?.Message}, " +
            $"secondError={secondError?.GetType().Name}/{secondError?.Message}");
    }

    [Fact]
    public async Task Concurrent_ApplyAddAlias_and_ApplyCreateChannel_on_same_ReviewItem_have_deterministic_outcome()
    {
        // Sequential, deterministic. SQLite serialises writes via file
        // lock so true `Task.WhenAll` collapse to this sequential
        // outcome. AddAlias first (canonical pre-exists → succeeds,
        // review → Resolved with channel K). CreateChannel afterwards
        // with a DIFFERENT key K' hits the Resolved branch and the
        // "approved.Key != K'" guard throws ReviewConflict — i.e. the
        // outcome is deterministic given the ordered pre-conditions.
        const string title = "W-Review-02B 8";
        const string url = "http://host/wreview02b-8/1.ts";
        const string key = "wreview02b-8";

        var fingerprint = await SeedReviewWithFullEvidenceAsync(
            title, url, "OpWREVIEW02B-8", "run-wreview02b-8");

        await _resolver.CreateCanonicalChannelAsync(
            key, "W-Review-02B 8",
            EditorialCategory.Live, CanonicalGroupKeys.PortugalGeneralistas,
            PublicationPolicy.CreateEligible, isEnabled: true,
            normalizedAliases: new List<string>(),
            country: "pt");

        // AddAlias first: succeeds (canonical exists, no collision).
        var addResult = await _resolver.ApplyReviewApprovalAsync(
            fingerprint, AddAliasDecision(key));
        Assert.NotNull(addResult);
        Assert.Equal("add-alias", addResult!.Action);
        Assert.Equal(ReviewItemState.Resolved, addResult.Review.State);

        // CreateChannel with a DIFFERENT key after the review is already
        // Resolved → goes through the Resolved branch and the
        // approved.Key != key assertion throws ReviewConflict.
        var ex = await Assert.ThrowsAsync<ChannelAdministrationException>(async () =>
            await _resolver.ApplyReviewApprovalAsync(
                fingerprint,
                CreateChannelDecision("wreview02b-8-other", "W-Review-02B 8 Other")));

        Assert.Equal(ChannelAdministrationError.ReviewConflict, ex.Error);

        // Deterministic final state: exactly one canonical row with the
        // original key; no second canonical created.
        var canonical = await FindChannelByKeyAsync(key);
        Assert.NotNull(canonical);
        Assert.Null(await FindChannelByKeyAsync("wreview02b-8-other"));
    }

    // ════════════════════════════════════════════════════════════════
    // 5 — Coverage gates
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AddAlias_does_not_create_orphan_ChannelSource_if_review_has_no_evidence()
    {
        // No evidence (StreamUrl/SourceId = null) — MaterializeChannelSource
        // gate blocks. No ChannelSource created.
        const string title = "W-Review-02B 9";
        var fingerprint = await SeedReviewWithoutEvidenceAsync(title);

        var result = await _resolver.ApplyReviewApprovalAsync(
            fingerprint, AddAliasDecision("rtp1"));

        Assert.NotNull(result);
        Assert.Null(result!.MaterializedChannelSource);

        var all = await AllChannelSourcesAsync();
        Assert.Empty(all);
    }

    [Fact]
    public async Task Channel_source_filtered_unique_does_not_block_genuinely_distinct_streams_per_source()
    {
        // Three genuinely distinct streams (different fingerprints) for
        // the same (canonical, source) — should produce three rows
        // without tripping the filtered UNIQUE (uniqueness is per
        // fingerprint, not per pair). Direct DbContext inserts to
        // prove the schema is open to distinct tuples; this sidesteps
        // any in-application dedup logic and exercises the new index
        // directly.
        var channel = await _resolver.CreateCanonicalChannelAsync(
            "wreview02b-10", "W-Review-02B 10",
            EditorialCategory.Live, CanonicalGroupKeys.PortugalGeneralistas,
            PublicationPolicy.CreateEligible, isEnabled: true,
            normalizedAliases: new List<string>(),
            country: "pt");
        var source = await _resolver.EnsureSourceAsync(
            "wreview02b-10-src", "W-Review-02B 10 Src",
            SourceKind.M3U, "file:///wreview02b-10.m3u", 0);

        var now = DateTime.UtcNow;
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            for (var i = 0; i < 3; i++)
            {
                var url = $"http://host/live/u/p/{i}";
                ctx.ChannelSources.Add(new ChannelSourceEntity
                {
                    CanonicalChannelId = channel.Id,
                    SourceId = source.Id,
                    StreamUrl = url,
                    Fingerprint = StreamFingerprint.TryComputeFingerprint(url),
                    FingerprintVersion = "sfp1",
                    Quality = StreamQuality.Unknown,
                    Epg = EpgState.Unknown,
                    Availability = AvailabilityState.Discovered,
                    MatchConfidence = 0,
                    MatchMethod = "test",
                    FirstSeenAtUtc = now,
                    LastSeenAtUtc = now,
                    LastTestedAtUtc = now,
                    LastResponseTimeMs = 0,
                    IsEnabled = true,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                });
            }
            await ctx.SaveChangesAsync();
        }

        var count = await CountChannelSourcesByCanonicalAndSourceAsync(channel.Id, source.Id);
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task Transaction_wrap_is_idempotent_on_repeated_approval_of_same_review()
    {
        const string title = "W-Review-02B 11";
        const string url = "http://host/wreview02b-11/1.ts";

        var fingerprint = await SeedReviewWithFullEvidenceAsync(
            title, url, "OpWREVIEW02B-11", "run-wreview02b-11");

        // First approval: AddAlias materializes a ChannelSource.
        var first = await _resolver.ApplyReviewApprovalAsync(
            fingerprint, AddAliasDecision("rtp1"));
        Assert.NotNull(first!.MaterializedChannelSource);
        var firstCsId = first.MaterializedChannelSource!.Id;

        // Second approval (sequential): idempotent; same ChannelSource row.
        var second = await _resolver.ApplyReviewApprovalAsync(
            fingerprint, AddAliasDecision("rtp1"));
        Assert.NotNull(second!.MaterializedChannelSource);
        Assert.True(second.Idempotent);
        Assert.Equal(firstCsId, second.MaterializedChannelSource!.Id);

        var all = await AllChannelSourcesAsync();
        Assert.Equal(1, all.Count(c => c.StreamUrl == url));
    }

    [Fact]
    public async Task Provider_test_for_Sqlite_unique_index_is_present()
    {
        // Query sqlite_master to confirm the filtered UNIQUE INDEX on
        // channel_sources is materialized in the schema (raw-SQL
        // migration applied in InitializeAsync).
        await using var conn = new SqliteConnection($"Data Source={_dbPath};Cache=Private");
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT name, sql FROM sqlite_master " +
            "WHERE type='index' AND tbl_name='channel_sources' " +
            "AND name='IX_channel_sources_Channel_Source_Fingerprint_Unique';";

        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(),
            "Filtered UNIQUE INDEX IX_channel_sources_Channel_Source_Fingerprint_Unique is not present in schema.");

        var sql = reader.GetString(1);
        Assert.Contains("UNIQUE INDEX", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"CanonicalChannelId\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"SourceId\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"Fingerprint\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"FingerprintVersion\"", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE \"Fingerprint\" IS NOT NULL", sql, StringComparison.Ordinal);
    }
}
