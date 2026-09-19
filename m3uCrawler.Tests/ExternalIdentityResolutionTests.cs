using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Matching;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// WAVE W4b — canonicalização de identidade externa (tvg-id) segundo
/// ADR-0002 §5/§3. Puro, sem DB.
/// </summary>
public class ExternalIdentityNormalizerTests
{
    [Theory]
    [InlineData("  RTP1  ", "rtp1")]
    [InlineData("RTP  1", "rtp 1")]
    [InlineData("\"RTP1\"", "rtp1")]
    [InlineData("'RTP 1'", "rtp 1")]
    [InlineData("RTP 1 HD", "rtp 1 hd")]
    [InlineData("RÁDIO", "rádio")]
    public void Normalize_applies_case_whitespace_and_quote_rules(string raw, string expected)
        => Assert.Equal(expected, ExternalIdentityNormalizer.Normalize(raw));

    [Theory]
    [InlineData("HTTP://Example.COM:80/Path?A=B#frag", "http://example.com/Path?A=B")]
    [InlineData("https://Example.com:443/a#frag", "https://example.com/a")]
    [InlineData("https://example.com:8443/a", "https://example.com:8443/a")]
    [InlineData("http://example.com", "http://example.com/")]
    public void Normalize_canonicalizes_uri_shaped_values(string raw, string expected)
        => Assert.Equal(expected, ExternalIdentityNormalizer.Normalize(raw));

    [Fact]
    public void Normalize_returns_empty_for_null_or_blank()
    {
        Assert.Equal(string.Empty, ExternalIdentityNormalizer.Normalize(null));
        Assert.Equal(string.Empty, ExternalIdentityNormalizer.Normalize(""));
        Assert.Equal(string.Empty, ExternalIdentityNormalizer.Normalize("   "));
        Assert.Equal(string.Empty, ExternalIdentityNormalizer.Normalize("\"\""));
    }

    [Fact]
    public void Normalize_never_hashes_away_meaning()
    {
        var normalized = ExternalIdentityNormalizer.Normalize("RTP-1-ID");
        Assert.Equal("rtp-1-id", normalized);
        Assert.NotEqual(64, normalized.Length);
    }
}

/// <summary>
/// WAVE W4b — resolução por identidade externa + migração + import
/// idempotente.
/// </summary>
public class ExternalIdentityResolutionTests : IDisposable
{
    private const string PreviousMigration = "20260918081844_AddSourceSelectionPolicies";
    private const string NewMigration = "20260919063912_AddExternalIdentity";

    private readonly List<string> _dbPaths = new();

    public void Dispose()
    {
        foreach (var path in _dbPaths)
        {
            TestTempDb.Cleanup(path);
        }
    }

    private string NewDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wave-w4b-{Guid.NewGuid():N}.db");
        _dbPaths.Add(path);
        return path;
    }

    private static DbContextOptions<ChannelCatalogDbContext> OptionsFor(string dbPath) =>
        new DbContextOptionsBuilder<ChannelCatalogDbContext>()
            .UseSqlite($"Data Source={dbPath};Cache=Private")
            .Options;

    private async Task<CatalogResolver> NewBootstrappedResolverAsync(string dbPath)
    {
        var bootstrapper = new ChannelCatalogBootstrapper(dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        return new CatalogResolver(new TestDbContextFactory(dbPath), dbPath);
    }

    private static async Task<bool> TableExistsAsync(ChannelCatalogDbContext ctx, string table)
    {
        var count = await ctx.Database
            .SqlQueryRaw<long>(
                "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='table' AND name = {0}",
                table)
            .SingleAsync();
        return count > 0;
    }

    private static Task<List<string>> AppliedMigrationsAsync(ChannelCatalogDbContext ctx) =>
        ctx.Database
            .SqlQueryRaw<string>("SELECT MigrationId AS Value FROM __EFMigrationsHistory")
            .ToListAsync();

    // =====================================================================
    // 1. Migração: fresh DB e DB existente
    // =====================================================================

    [Fact]
    public async Task Migration_applies_cleanly_on_fresh_database()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();
            Assert.True(await TableExistsAsync(ctx, "external_identities"));
            Assert.Contains(NewMigration, await AppliedMigrationsAsync(ctx));
            // Aditiva: o schema existente mantém-se.
            Assert.True(await TableExistsAsync(ctx, "canonical_channels"));
            Assert.True(await TableExistsAsync(ctx, "channel_aliases"));
        }
    }

    [Fact]
    public async Task Migration_applies_cleanly_on_existing_database_and_preserves_data()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            Assert.False(await TableExistsAsync(ctx, "external_identities"));

            var now = DateTime.UtcNow.ToString("o");
            await ctx.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO canonical_channels (Key, DisplayName, EditorialCategory, EditorialGroup, PublicationPolicy, IsEnabled, CreatedAtUtc, UpdatedAtUtc) VALUES ({"w4b-legacy"}, {"Legacy"}, 0, 0, 0, 1, {now}, {now});");
        }

        await using (var ctx = new ChannelCatalogDbContext(options))
        {
            await ctx.Database.MigrateAsync();

            Assert.True(await TableExistsAsync(ctx, "external_identities"));
            Assert.Contains(NewMigration, await AppliedMigrationsAsync(ctx));

            var legacy = await ctx.CanonicalChannels.AsNoTracking()
                .SingleAsync(c => c.Key == "w4b-legacy");
            Assert.Equal("Legacy", legacy.DisplayName);
        }
    }

    // =====================================================================
    // 2. Exact match vence nome/alias; passo exacto não é contradito
    // =====================================================================

    [Fact]
    public async Task External_identity_exact_match_wins_over_normalized_name_and_alias()
    {
        var dbPath = NewDbPath();
        var resolver = await NewBootstrappedResolverAsync(dbPath);

        var channelA = await resolver.CreateCanonicalChannelAsync(
            "w4b-ext-a", "Ext A", EditorialCategory.Live,
            CanonicalEditorialGroup.PortugalLive, PublicationPolicy.CreateEligible,
            isEnabled: true, normalizedAliases: Array.Empty<string>());
        var channelB = await resolver.CreateCanonicalChannelAsync(
            "w4b-ext-b", "Ext B", EditorialCategory.Live,
            CanonicalEditorialGroup.PortugalLive, PublicationPolicy.CreateEligible,
            isEnabled: true, normalizedAliases: new[] { "canal partilhado" });

        // Sem identidade externa, o nome/alias resolve para B.
        var byName = await resolver.ResolveAsync("canal partilhado");
        Assert.Equal(CatalogResolutionKind.Canonical, byName.Kind);
        Assert.Equal(channelB.Id, byName.CanonicalChannelId);

        // Identidade externa exacta "ext-1" → A.
        var created = await resolver.RecordExternalIdentityAsync(
            channelA.Id, providerId: null,
            @namespace: ExternalIdentityNamespaces.TvgId,
            rawValue: "  EXT-1  ", origin: "operator", confidence: 1.0);
        Assert.Equal(RecordExternalIdentityOutcome.Created, created);

        // O exacto vence nome/alias.
        var resolved = await resolver.ResolveAsync("canal partilhado", "ext-1");
        Assert.Equal(CatalogResolutionKind.Canonical, resolved.Kind);
        Assert.Equal(channelA.Id, resolved.CanonicalChannelId);

        // Sem nome, mas com identidade externa: continua a resolver A.
        var externalOnly = await resolver.ResolveAsync(string.Empty, "EXT-1");
        Assert.Equal(CatalogResolutionKind.Canonical, externalOnly.Kind);
        Assert.Equal(channelA.Id, externalOnly.CanonicalChannelId);
    }

    [Fact]
    public async Task Exact_external_match_to_disabled_channel_does_not_fall_through_to_name_alias()
    {
        var dbPath = NewDbPath();
        var resolver = await NewBootstrappedResolverAsync(dbPath);

        var disabled = await resolver.CreateCanonicalChannelAsync(
            "w4b-ext-disabled", "Ext Disabled", EditorialCategory.Live,
            CanonicalEditorialGroup.PortugalLive, PublicationPolicy.CreateEligible,
            isEnabled: false, normalizedAliases: Array.Empty<string>());
        var active = await resolver.CreateCanonicalChannelAsync(
            "w4b-ext-active", "Ext Active", EditorialCategory.Live,
            CanonicalEditorialGroup.PortugalLive, PublicationPolicy.CreateEligible,
            isEnabled: true, normalizedAliases: new[] { "canal activo" });

        Assert.Equal(RecordExternalIdentityOutcome.Created,
            await resolver.RecordExternalIdentityAsync(
                disabled.Id, null, ExternalIdentityNamespaces.TvgId, "ext-disabled", "operator", 1.0));

        // Exacto (desactivado) não pode ser contradito pelo alias do activo.
        var resolved = await resolver.ResolveAsync("canal activo", "ext-disabled");
        Assert.NotEqual(CatalogResolutionKind.Canonical, resolved.Kind);
        Assert.NotEqual(active.Id, resolved.CanonicalChannelId);
    }

    // =====================================================================
    // 3. Ambiguidade → Review, sem canal e sem criação
    // =====================================================================

    [Fact]
    public async Task Ambiguous_external_identity_resolves_to_ambiguous_not_first()
    {
        var dbPath = NewDbPath();
        var resolver = await NewBootstrappedResolverAsync(dbPath);

        var channelA = await resolver.CreateCanonicalChannelAsync(
            "w4b-amb-a", "Amb A", EditorialCategory.Live,
            CanonicalEditorialGroup.PortugalLive, PublicationPolicy.CreateEligible,
            isEnabled: true, normalizedAliases: Array.Empty<string>());
        var channelB = await resolver.CreateCanonicalChannelAsync(
            "w4b-amb-b", "Amb B", EditorialCategory.Live,
            CanonicalEditorialGroup.PortugalLive, PublicationPolicy.CreateEligible,
            isEnabled: true, normalizedAliases: Array.Empty<string>());

        Assert.Equal(RecordExternalIdentityOutcome.Created,
            await resolver.RecordExternalIdentityAsync(
                channelA.Id, null, ExternalIdentityNamespaces.TvgId, "dup-value", "operator", 1.0));
        Assert.Equal(RecordExternalIdentityOutcome.Created,
            await resolver.RecordExternalIdentityAsync(
                channelB.Id, null, "provider:test", "dup-value", "operator", 1.0));

        var resolved = await resolver.ResolveAsync(string.Empty, "dup-value");
        Assert.Equal(CatalogResolutionKind.Ambiguous, resolved.Kind);
        Assert.Null(resolved.CanonicalChannelId);
    }

    [Fact]
    public async Task Ambiguous_external_identity_goes_to_review_without_creating_identity()
    {
        var dbPath = NewDbPath();
        var resolver = await NewBootstrappedResolverAsync(dbPath);

        var channelA = await resolver.CreateCanonicalChannelAsync(
            "w4b-ambp-a", "AmbP A", EditorialCategory.Live,
            CanonicalEditorialGroup.PortugalLive, PublicationPolicy.CreateEligible,
            isEnabled: true, normalizedAliases: Array.Empty<string>());
        var channelB = await resolver.CreateCanonicalChannelAsync(
            "w4b-ambp-b", "AmbP B", EditorialCategory.Live,
            CanonicalEditorialGroup.PortugalLive, PublicationPolicy.CreateEligible,
            isEnabled: true, normalizedAliases: Array.Empty<string>());

        await resolver.RecordExternalIdentityAsync(
            channelA.Id, null, ExternalIdentityNamespaces.TvgId, "amb-pipeline", "operator", 1.0);
        await resolver.RecordExternalIdentityAsync(
            channelB.Id, null, "provider:test", "amb-pipeline", "operator", 1.0);

        var slug = Guid.NewGuid().ToString("N")[..12];
        var title = $"PT CANAL {slug.ToUpperInvariant()}";
        var stream = new M3uStream
        {
            Title = title,
            Url = $"http://x.example/{slug}.ts",
            Group = "Portugal",
            OriginalTvgId = "amb-pipeline",
            IsWorking = true,
        };

        var result = await new PipelineIngestionService(
            resolver, new CountryChannelValidator(CountriesDir()))
            .IngestAsync(new[] { stream }, $"w4b-amb-{slug}", "Telegram", "pt");

        Assert.Equal(0, result.IngestedCount);
        Assert.Equal(0, result.MatchedCount);
        Assert.Equal(0, result.AutoCreatedCount);

        var resolved = await resolver.ResolveAsync(ChannelNormalizer.Normalize(title), "amb-pipeline");
        Assert.Equal(CatalogResolutionKind.Ambiguous, resolved.Kind);

        var normalized = ChannelNormalizer.Normalize(title);
        var reviews = (await resolver.ListAllReviewItemsAsync())
            .Where(r => r.NormalizedIdentity == normalized)
            .ToList();
        Assert.Single(reviews);
        Assert.Equal("ambiguous-external-identity", reviews[0].ReasonSignature);
    }

    // =====================================================================
    // 4. Canonicalização ponta-a-ponta na resolução
    // =====================================================================

    [Fact]
    public async Task Resolution_canonicalizes_tvg_id_before_lookup()
    {
        var dbPath = NewDbPath();
        var resolver = await NewBootstrappedResolverAsync(dbPath);

        var channel = await resolver.CreateCanonicalChannelAsync(
            "w4b-canon", "Canon", EditorialCategory.Live,
            CanonicalEditorialGroup.PortugalLive, PublicationPolicy.CreateEligible,
            isEnabled: true, normalizedAliases: Array.Empty<string>());

        await resolver.RecordExternalIdentityAsync(
            channel.Id, null, ExternalIdentityNamespaces.TvgId, "RTP  1", "operator", 1.0);

        // Case + espaços canonicalizam para o mesmo valor.
        var resolved = await resolver.ResolveAsync(string.Empty, "  rtp 1  ");
        Assert.Equal(CatalogResolutionKind.Canonical, resolved.Kind);
        Assert.Equal(channel.Id, resolved.CanonicalChannelId);
    }

    // =====================================================================
    // 5. Parser captura OriginalTvgId
    // =====================================================================

    [Fact]
    public void Parser_captures_original_tvg_id()
    {
        var parser = new M3uParserService();
        var content = "#EXTM3U\n#EXTINF:-1 tvg-id=\"RTP1.pt\" tvg-name=\"RTP 1\" group-title=\"PT\",RTP 1\nhttps://x/rtp1\n";
        var streams = parser.Parse(content);

        Assert.Single(streams);
        Assert.Equal("RTP1.pt", streams[0].OriginalTvgId);
    }

    [Fact]
    public void Parser_leaves_original_tvg_id_empty_when_absent()
    {
        var parser = new M3uParserService();
        var streams = parser.Parse("#EXTM3U\n#EXTINF:-1,RTP 1\nhttps://x/rtp1\n");
        Assert.Single(streams);
        Assert.Equal(string.Empty, streams[0].OriginalTvgId);
    }

    // =====================================================================
    // 6. Idempotência e não-sobreposição
    // =====================================================================

    [Fact]
    public async Task Re_ingest_does_not_duplicate_external_identity()
    {
        var dbPath = NewDbPath();
        var resolver = await NewBootstrappedResolverAsync(dbPath);
        var ingestor = new PipelineIngestionService(
            resolver, new CountryChannelValidator(CountriesDir()));

        var stream = new M3uStream
        {
            Title = "RTP 1",
            Url = "http://x.example/rtp1-w4b.ts",
            Group = "Portugal",
            OriginalTvgId = "rtp1-w4b-ext",
            IsWorking = true,
        };

        var first = await ingestor.IngestAsync(new[] { stream }, "w4b-idem", "Telegram", "pt");
        var second = await ingestor.IngestAsync(new[] { stream }, "w4b-idem", "Telegram", "pt");

        Assert.Equal(1, first.MatchedCount);
        Assert.Equal(1, second.MatchedCount);

        // A segunda ingestão resolveu pela identidade externa (exacta)
        // e não duplicou a linha.
        var resolved = await resolver.ResolveAsync("nao interessa", "rtp1-w4b-ext");
        Assert.Equal(CatalogResolutionKind.Canonical, resolved.Kind);

        var factory = new TestDbContextFactory(dbPath);
        await using var ctx = factory.CreateDbContext();
        var rows = await ctx.ExternalIdentities
            .AsNoTracking()
            .Where(e => e.Value == "rtp1-w4b-ext")
            .ToListAsync();
        Assert.Single(rows);
    }

    [Fact]
    public async Task Unknown_stream_with_unknown_tvg_id_stays_unknown_and_creates_no_identity()
    {
        var dbPath = NewDbPath();
        var resolver = await NewBootstrappedResolverAsync(dbPath);
        var ingestor = new PipelineIngestionService(
            resolver, new CountryChannelValidator(CountriesDir()));

        var slug = Guid.NewGuid().ToString("N")[..12];
        var stream = new M3uStream
        {
            Title = $"PT CANAL {slug.ToUpperInvariant()}",
            Url = $"http://x.example/{slug}.ts",
            Group = "Portugal",
            OriginalTvgId = $"unknown-{slug}",
            IsWorking = true,
        };

        var result = await ingestor.IngestAsync(new[] { stream }, $"w4b-unknown-{slug}", "Telegram", "pt");

        Assert.Equal(0, result.IngestedCount);
        Assert.Equal(0, result.AutoCreatedCount);

        var factory = new TestDbContextFactory(dbPath);
        await using var ctx = factory.CreateDbContext();
        Assert.Equal(0, await ctx.ExternalIdentities.CountAsync(e => e.Value == $"unknown-{slug}"));
    }

    [Fact]
    public async Task Baseline_import_without_external_ids_leaves_table_empty()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        await using var ctx = new ChannelCatalogDbContext(options);
        await ctx.Database.MigrateAsync();

        var baseline = new CatalogBaseline
        {
            CatalogId = "pt-w4b-empty",
            Version = "1.0",
            Country = "PT",
            Matching = new MatchingBaseline
            {
                Examples =
                {
                    ["rtp1"] = new ChannelBaseline
                    {
                        CanonicalId = "pt.rtp1",
                        Name = "RTP 1",
                        Aliases = new() { "RTP1" },
                    },
                },
            },
        };

        var report = await CatalogBaselineImporter.ImportAsync(ctx, baseline);

        Assert.Equal(0, report.ExternalIdentitiesAdded);
        Assert.Equal(0, await ctx.ExternalIdentities.CountAsync());
    }

    [Fact]
    public async Task Baseline_import_seeds_external_identities_idempotently()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        await using var ctx = new ChannelCatalogDbContext(options);
        await ctx.Database.MigrateAsync();

        var baseline = new CatalogBaseline
        {
            CatalogId = "pt-w4b-seed",
            Version = "1.0",
            Country = "PT",
            Matching = new MatchingBaseline
            {
                Examples =
                {
                    ["rtp1"] = new ChannelBaseline
                    {
                        CanonicalId = "pt.rtp1",
                        Name = "RTP 1",
                        Aliases = new() { "RTP1" },
                        TvgIds = new() { "RTP1.pt" },
                        ExternalIds = new() { ["provider:meo"] = "rtp-um" },
                    },
                },
            },
        };

        var first = await CatalogBaselineImporter.ImportAsync(ctx, baseline);
        var second = await CatalogBaselineImporter.ImportAsync(ctx, baseline);

        Assert.Equal(2, first.ExternalIdentitiesAdded);
        Assert.Equal(0, second.ExternalIdentitiesAdded);
        Assert.True(second.ExternalIdentitiesSkipped >= 2);

        var rows = await ctx.ExternalIdentities.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Namespace == ExternalIdentityNamespaces.TvgId && r.Value == "rtp1.pt");
        Assert.Contains(rows, r => r.Namespace == "provider:meo" && r.Value == "rtp-um");
    }

    [Fact]
    public async Task Baseline_import_does_not_overwrite_operator_edited_external_identity()
    {
        var dbPath = NewDbPath();
        var options = OptionsFor(dbPath);

        await using var ctx = new ChannelCatalogDbContext(options);
        await ctx.Database.MigrateAsync();

        var baseline = new CatalogBaseline
        {
            CatalogId = "pt-w4b-edit",
            Version = "1.0",
            Country = "PT",
            Matching = new MatchingBaseline
            {
                Examples =
                {
                    ["rtp1"] = new ChannelBaseline
                    {
                        CanonicalId = "pt.rtp1",
                        Name = "RTP 1",
                        Aliases = new() { "RTP1" },
                        TvgIds = new() { "rtp1.pt" },
                    },
                },
            },
        };

        var first = await CatalogBaselineImporter.ImportAsync(ctx, baseline);
        Assert.Equal(1, first.ExternalIdentitiesAdded);

        // Simular edição do operador: re-apontar a identidade para
        // outro canal.
        var other = new CanonicalChannelEntity
        {
            Key = "w4b-other",
            DisplayName = "Other",
            EditorialCategory = EditorialCategory.Live,
            EditorialGroup = CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy = PublicationPolicy.CreateEligible,
            IsEnabled = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        ctx.CanonicalChannels.Add(other);
        await ctx.SaveChangesAsync();

        var row = await ctx.ExternalIdentities.SingleAsync(e => e.Value == "rtp1.pt");
        row.CanonicalChannelId = other.Id;
        await ctx.SaveChangesAsync();

        var second = await CatalogBaselineImporter.ImportAsync(ctx, baseline);

        Assert.Equal(0, second.ExternalIdentitiesAdded);
        Assert.True(second.ExternalIdentityConflicts >= 1);

        var after = await ctx.ExternalIdentities.AsNoTracking().SingleAsync(e => e.Value == "rtp1.pt");
        Assert.Equal(other.Id, after.CanonicalChannelId);
        Assert.Equal(1, await ctx.ExternalIdentities.CountAsync(e => e.Value == "rtp1.pt"));
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static string CountriesDir()
    {
        var cwd = Directory.GetCurrentDirectory();
        var repoRoot = Path.GetFullPath(Path.Combine(cwd, "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "m3uCrawler", "runtime-data", "countries");
    }
}
