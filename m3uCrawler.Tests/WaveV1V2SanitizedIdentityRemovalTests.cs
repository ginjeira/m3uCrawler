using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceSelection;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Wave V1/V2 (2026-10-01) — remoção do fallback de identidade por URL
/// sanitizada. A identidade interna passa a ser exclusivamente o fingerprint
/// canónico <c>sfp1</c>:
/// <list type="bullet">
///   <item>V1 — <c>CatalogResolver.RecordChannelSourceAsync</c> consolida por
///   fingerprint; rows legacy sem fingerprint não são correspondidas nem
///   enriquecidas por URL sanitizada.</item>
///   <item>V1b — <c>ReloadChannelSourceOnUniqueAsync</c> relê apenas por
///   fingerprint.</item>
///   <item>V2 — <c>SourceSelectionStage</c> junta runtime↔catálogo apenas por
///   fingerprint.</item>
/// </list>
/// <para>
/// <c>ChannelSource.StreamUrl</c> permanece a forma sanitizada — é apenas
/// apresentação/persistência e nunca uma chave de identidade.
/// </para>
/// </summary>
public class WaveV1V2SanitizedIdentityRemovalTests : IAsyncLifetime
{
    // Par "bare" Xtream que colapsa sob CredentialSanitizer.SanitizeUrl
    // (mesmo host/porta/último segmento) mas cujos fingerprints sfp1 diferem
    // (paths distintos: o prefixo não é live|movie|series, logo MaskXtreamPath
    // não os mascara). É a evidência central destas waves.
    private const string CollisionA = "http://host.example:8080/201788541134/SECAlpha99/12345";
    private const string CollisionB = "http://host.example:8080/998877665544/SECBravo77/12345";

    private readonly string _dbPath;
    private CatalogResolver _resolver = null!;

    public WaveV1V2SanitizedIdentityRemovalTests()
    {
        _dbPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"wave-v1v2-{Guid.NewGuid():N}.db");
    }

    public async Task InitializeAsync()
    {
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        var ctx = await bootstrapper.InitializeAsync();
        await ctx.DisposeAsync();
        _resolver = new CatalogResolver(new TestDbContextFactory(_dbPath), _dbPath);
    }

    public Task DisposeAsync()
    {
        TestTempDb.Cleanup(_dbPath);
        return Task.CompletedTask;
    }

    // ────────────────────────────────────────────────────────────────
    // T1 — colisão sanitizada, fingerprints distintos ⇒ streams distintos
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task T1_colliding_sanitized_forms_with_distinct_fingerprints_are_distinct_streams()
    {
        var (channel, source) = await NewChannelAndSourceAsync("v1v2-t1");

        Assert.Equal(CredentialSanitizer.SanitizeUrl(CollisionA), CredentialSanitizer.SanitizeUrl(CollisionB));
        var fpA = StreamFingerprint.TryComputeFingerprint(CollisionA);
        var fpB = StreamFingerprint.TryComputeFingerprint(CollisionB);
        Assert.NotNull(fpA);
        Assert.NotNull(fpB);
        Assert.NotEqual(fpA, fpB);

        var first = await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, CollisionA);
        var second = await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, CollisionB);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(fpA, first.Fingerprint);
        Assert.Equal(fpB, second.Fingerprint);
        Assert.Equal(CredentialSanitizer.SanitizeUrl(CollisionA), first.StreamUrl);
        Assert.Equal(CredentialSanitizer.SanitizeUrl(CollisionB), second.StreamUrl);
        Assert.Equal(2, await CountChannelSourcesAsync(channel.Id, source.Id));
    }

    // ────────────────────────────────────────────────────────────────
    // T2 (V1) — row pré-existente com fpA + StreamUrl sanitizada não é
    //           correspondida por uma nova stream fpB com a mesma forma
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task T2_pre_existing_row_with_fpA_is_not_matched_by_fpB_sharing_sanitized_form()
    {
        var (channel, source) = await NewChannelAndSourceAsync("v1v2-t2");
        var fpA = StreamFingerprint.TryComputeFingerprint(CollisionA)!;
        var fpB = StreamFingerprint.TryComputeFingerprint(CollisionB)!;

        var seeded = await InsertLegacyChannelSourceAsync(
            channel.Id, source.Id,
            CredentialSanitizer.SanitizeUrl(CollisionA),
            fingerprint: fpA,
            fingerprintVersion: StreamFingerprint.Version);

        var created = await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, CollisionB);

        Assert.NotEqual(seeded.Id, created.Id);
        Assert.Equal(fpB, created.Fingerprint);
        Assert.Equal(2, await CountChannelSourcesAsync(channel.Id, source.Id));
    }

    // ────────────────────────────────────────────────────────────────
    // T3 (V2) — colisão sanitizada não associa a runtime ao ChannelSource errado
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task T3_sanitized_collision_does_not_associate_runtime_stream_to_wrong_channel_source()
    {
        var (channel, source) = await NewChannelAndSourceAsync("v1v2-t3");
        await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, CollisionA);

        // Runtime B partilha a forma sanitizada com A mas tem fingerprint distinto.
        var result = await RunStageAsync(CollisionB);

        Assert.Empty(result.Selected);
        Assert.Contains(result.Unmatched, s => s.Url == CollisionB);
        Assert.Equal(0, result.MatchedChannelCount);
    }

    // ────────────────────────────────────────────────────────────────
    // T4 — objetos equivalentes (mesmo fingerprint) continuam a resolver
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task T4_same_fingerprint_still_resolves()
    {
        var (channel, source) = await NewChannelAndSourceAsync("v1v2-t4");
        const string a = "http://HOST:80/live/alice/secretA/12345";
        const string b = "http://host/live/bob/secretB/12345";
        Assert.Equal(StreamFingerprint.TryComputeFingerprint(a), StreamFingerprint.TryComputeFingerprint(b));

        var stored = await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, a);

        var result = await RunStageAsync(b);

        var selected = Assert.Single(result.Selected);
        Assert.Equal(stored.Fingerprint, selected.Candidate.StreamFingerprint);
        Assert.Equal(channel.Id, result.Channels.Single().CanonicalChannelId);
        Assert.Equal(0, result.AmbiguousCount);
    }

    // ────────────────────────────────────────────────────────────────
    // T5 — legacy NULL fingerprint: não é correspondida nem enriquecida
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task T5_null_fingerprint_legacy_row_is_neither_matched_nor_enriched()
    {
        // (a) V2 — legacy sem fingerprint não é correspondida.
        var (channelU, sourceU) = await NewChannelAndSourceAsync("v1v2-t5-unmatched");
        await InsertLegacyChannelSourceAsync(channelU.Id, sourceU.Id, "http://host.example/legacy");

        var result = await RunStageAsync("http://host.example/legacy");

        Assert.Empty(result.Selected);
        Assert.Contains(result.Unmatched, s => s.Url == "http://host.example/legacy");

        // (b) V1 — uma stream fingerprintável com a MESMA forma sanitizada da
        //     row legacy não a enriquece (sem fallback): cria nova row.
        var (channelE, sourceE) = await NewChannelAndSourceAsync("v1v2-t5-enrich");
        var fpA = StreamFingerprint.TryComputeFingerprint(CollisionA)!;
        var legacy = await InsertLegacyChannelSourceAsync(
            channelE.Id, sourceE.Id, CredentialSanitizer.SanitizeUrl(CollisionA));

        var created = await _resolver.RecordChannelSourceAsync(channelE.Id, sourceE.Id, CollisionA);

        Assert.NotEqual(legacy.Id, created.Id);
        Assert.Equal(fpA, created.Fingerprint);
        Assert.Equal(2, await CountChannelSourcesAsync(channelE.Id, sourceE.Id));

        await using var ctx = await _resolver.GetFactory().CreateDbContextAsync();
        var reloadedLegacy = await ctx.ChannelSources.AsNoTracking().SingleAsync(cs => cs.Id == legacy.Id);
        Assert.Null(reloadedLegacy.Fingerprint);
        Assert.Null(reloadedLegacy.FingerprintVersion);
    }

    // ────────────────────────────────────────────────────────────────
    // T6 — URL não-fingerprintável: sem associação e sem identidade inventada
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task T6_non_fingerprintable_url_yields_no_association_and_no_invented_identity()
    {
        var (channel, source) = await NewChannelAndSourceAsync("v1v2-t6");

        var first = await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, "not-a-url");
        var second = await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, "not-a-url");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Null(first.Fingerprint);
        Assert.Null(second.Fingerprint);
        Assert.Equal(2, await CountChannelSourcesAsync(channel.Id, source.Id));

        var input = Stream("not-a-url");
        var result = await new SourceSelectionStage(_resolver)
            .ApplyAsync(new[] { input }, Policy());

        Assert.Empty(result.Selected);
        Assert.Contains(result.Unmatched, s => ReferenceEquals(s, input));
        Assert.Contains(result.Published, s => ReferenceEquals(s, input));
    }

    // ────────────────────────────────────────────────────────────────
    // T7 — ChannelSource.StreamUrl persistida continua sanitizada
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task T7_persisted_stream_url_is_sanitized_not_raw()
    {
        var (channel, source) = await NewChannelAndSourceAsync("v1v2-t7");
        const string raw =
            "http://alice.example:SECAlpha99@host.example/live/alice/SECAlpha99/12345";

        var stored = await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, raw);

        Assert.Equal(CredentialSanitizer.SanitizeUrl(raw), stored.StreamUrl);
        Assert.DoesNotContain("SECAlpha99", stored.StreamUrl);
        Assert.Contains("***", stored.StreamUrl);
        Assert.NotNull(stored.Fingerprint);

        await using var ctx = await _resolver.GetFactory().CreateDbContextAsync();
        var persisted = await ctx.ChannelSources.AsNoTracking().SingleAsync(cs => cs.Id == stored.Id);
        Assert.Equal(CredentialSanitizer.SanitizeUrl(raw), persisted.StreamUrl);
        Assert.DoesNotContain("SECAlpha99", persisted.StreamUrl);
    }

    // ────────────────────────────────────────────────────────────────
    // T8 — guarda: a resolução segue o fingerprint, não a forma sanitizada
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task T8_resolution_follows_the_fingerprint_not_the_sanitized_string()
    {
        var (channelA, sourceA) = await NewChannelAndSourceAsync("v1v2-t8-a");
        var (channelB, sourceB) = await NewChannelAndSourceAsync("v1v2-t8-b");

        Assert.Equal(CredentialSanitizer.SanitizeUrl(CollisionA), CredentialSanitizer.SanitizeUrl(CollisionB));
        var fpA = StreamFingerprint.TryComputeFingerprint(CollisionA)!;
        var fpB = StreamFingerprint.TryComputeFingerprint(CollisionB)!;
        Assert.NotEqual(fpA, fpB);

        await _resolver.RecordChannelSourceAsync(channelA.Id, sourceA.Id, CollisionA);
        await _resolver.RecordChannelSourceAsync(channelB.Id, sourceB.Id, CollisionB);

        var result = await RunStageAsync(CollisionA);

        // Com uma chave por URL sanitizada as duas rows seriam hits de canais
        // canónicos distintos (⇒ Ambiguous). A resolução só por fingerprint
        // devolve exactamente A.
        var selected = Assert.Single(result.Selected);
        Assert.Equal(fpA, selected.Candidate.StreamFingerprint);
        Assert.NotEqual(fpB, selected.Candidate.StreamFingerprint);
        Assert.Equal(0, result.AmbiguousCount);
        Assert.Equal(channelA.Id, result.Channels.Single().CanonicalChannelId);
        Assert.Equal(sourceA.Id, selected.Candidate.SourceId);
    }

    // ────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────

    private static M3uStream Stream(string url)
        => new() { Url = url, Title = "Canal V1V2", Group = "PT", IsWorking = true };

    private static SourceSelectionPolicy Policy()
        => new(MaxSourcesPerChannel: 10, PreferDistinctProviders: false);

    private async Task<SourceSelectionStageResult> RunStageAsync(string url)
    {
        var stage = new SourceSelectionStage(_resolver);
        return await stage.ApplyAsync(new[] { Stream(url) }, Policy());
    }

    private async Task<(CanonicalChannelEntity Channel, SourceEntity Source)> NewChannelAndSourceAsync(string key)
    {
        var channel = await _resolver.CreateCanonicalChannelAsync(
            key, key, EditorialCategory.Live, CanonicalGroupKeys.PortugalGeneralistas,
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

    private async Task<ChannelSourceEntity> InsertLegacyChannelSourceAsync(
        long channelId,
        long sourceId,
        string sanitizedUrl,
        string? fingerprint = null,
        string? fingerprintVersion = null)
    {
        var now = DateTime.UtcNow;
        await using var ctx = await _resolver.GetFactory().CreateDbContextAsync();
        var entity = new ChannelSourceEntity
        {
            CanonicalChannelId = channelId,
            SourceId = sourceId,
            StreamUrl = sanitizedUrl,
            Fingerprint = fingerprint,
            FingerprintVersion = fingerprintVersion,
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
        };
        ctx.ChannelSources.Add(entity);
        await ctx.SaveChangesAsync();
        return entity;
    }
}
