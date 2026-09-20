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
/// W4.1 (2026-09-20) — resolução fingerprint-aware do
/// <see cref="SourceSelectionStage"/>.
///
/// A persistência consolida <c>ChannelSource</c> por fingerprint, mas o join
/// da selecção usava apenas <c>CredentialSanitizer.SanitizeUrl</c>. URLs
/// fingerprint-equivalentes com representações sanitizadas diferentes
/// (casing do host, porta default, fragmento, credenciais em query) caiam em
/// <c>Unmatched</c> artificial. Estes testes provam que ambas as variantes
/// resolvem para a mesma <c>ChannelSource</c>.
/// </summary>
public class WaveW4p1SourceSelectionFingerprintResolutionTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private CatalogResolver _resolver = null!;

    public WaveW4p1SourceSelectionFingerprintResolutionTests()
    {
        _dbPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"wave-w4p1-{Guid.NewGuid():N}.db");
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
    // Test 1 — fingerprint-equivalent URLs, sanitized diferentes
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Fingerprint_equivalent_urls_with_different_sanitized_forms_both_resolve()
    {
        var (channel, source) = await NewChannelAndSourceAsync("w4p1-a");

        const string a = "http://HOST:80/live/alice/secretA/12345";
        const string b = "http://host/live/bob/secretB/12345";

        // Pré-condição: fingerprint igual, sanitized diferente, uma só row.
        Assert.Equal(StreamFingerprint.TryComputeFingerprint(a), StreamFingerprint.TryComputeFingerprint(b));
        Assert.NotEqual(CredentialSanitizer.SanitizeUrl(a), CredentialSanitizer.SanitizeUrl(b));

        var first = await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, a);
        var second = await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, b);
        Assert.Equal(first.Id, second.Id);

        var resultA = await RunStageAsync(a);
        var resultB = await RunStageAsync(b);

        AssertMatchedOn(resultA, channel.Id, source.Id);
        AssertMatchedOn(resultB, channel.Id, source.Id);
        Assert.Empty(resultA.Unmatched);
        Assert.Empty(resultB.Unmatched);
    }

    // Test 2 — scheme case
    [Fact]
    public async Task Scheme_case_is_resolved_by_fingerprint()
    {
        var (channel, source) = await NewChannelAndSourceAsync("w4p1-b");
        await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, "http://host/live/u/p/1");

        var result = await RunStageAsync("HTTP://host/live/u/p/1");

        AssertMatchedOn(result, channel.Id, source.Id);
        Assert.Empty(result.Unmatched);
    }

    // Test 3 — default port
    [Fact]
    public async Task Default_port_is_resolved_by_fingerprint()
    {
        var (channel, source) = await NewChannelAndSourceAsync("w4p1-c");
        await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, "http://host:80/live/u/p/2");

        var result = await RunStageAsync("http://host/live/u/p/2");

        AssertMatchedOn(result, channel.Id, source.Id);
        Assert.Empty(result.Unmatched);
    }

    // Test 4 — fragment
    [Fact]
    public async Task Fragment_is_resolved_by_fingerprint()
    {
        var (channel, source) = await NewChannelAndSourceAsync("w4p1-d");
        await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, "http://host/live/u/p/3");

        var result = await RunStageAsync("http://host/live/u/p/3#fragment");

        AssertMatchedOn(result, channel.Id, source.Id);
        Assert.Empty(result.Unmatched);
    }

    // Test 5 — query credentials (sem expor segredos)
    [Fact]
    public async Task Query_credentials_are_resolved_by_fingerprint_without_leaking_secrets()
    {
        var (channel, source) = await NewChannelAndSourceAsync("w4p1-e");
        await _resolver.RecordChannelSourceAsync(
            channel.Id, source.Id, "http://host/panel?username=user1&password=secret1&stream=77");

        var result = await RunStageAsync("http://host/panel?stream=77");

        AssertMatchedOn(result, channel.Id, source.Id);
        Assert.Empty(result.Unmatched);

        // O candidato transporta a URL real em memória (contrato existente);
        // nunca deve ser serializado. Aqui só confirmamos que não há segredo
        // no fingerprint persistido.
        await using var ctx = await _resolver.GetFactory().CreateDbContextAsync();
        var stored = await ctx.ChannelSources.AsNoTracking().SingleAsync();
        Assert.DoesNotContain("secret1", stored.Fingerprint ?? string.Empty);
        Assert.DoesNotContain("password", stored.Fingerprint ?? string.Empty);
    }

    // Test 6 — legacy row sem fingerprint
    [Fact]
    public async Task Legacy_row_without_fingerprint_still_resolves_by_sanitized_url()
    {
        var (channel, source) = await NewChannelAndSourceAsync("w4p1-f");
        await InsertLegacyChannelSourceAsync(channel.Id, source.Id, "http://host/legacy");

        var result = await RunStageAsync("http://host/legacy");

        AssertMatchedOn(result, channel.Id, source.Id);
        Assert.Empty(result.Unmatched);
    }

    // Test 7 — fingerprints diferentes não se associam
    [Fact]
    public async Task Different_fingerprints_are_not_associated()
    {
        var (channel, source) = await NewChannelAndSourceAsync("w4p1-g");
        await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, "http://host/live/u/p/100");

        var result = await RunStageAsync("http://host/live/u/p/200");

        Assert.Empty(result.Selected);
        Assert.Contains(result.Unmatched, s => s.Url == "http://host/live/u/p/200");
    }

    // Test 8 — SourceId isolation (fingerprint colide entre canais distintos)
    [Fact]
    public async Task Same_fingerprint_across_distinct_sources_is_ambiguous_not_crossed()
    {
        var (channelA, sourceA) = await NewChannelAndSourceAsync("w4p1-h-a");
        var (channelB, sourceB) = await NewChannelAndSourceAsync("w4p1-h-b");

        const string url = "http://host/live/u/p/300";
        await _resolver.RecordChannelSourceAsync(channelA.Id, sourceA.Id, url);
        await _resolver.RecordChannelSourceAsync(channelB.Id, sourceB.Id, url);

        await using (var ctx = await _resolver.GetFactory().CreateDbContextAsync())
        {
            // Isolamento na persistência: duas rows, uma por Source.
            Assert.Equal(2, await ctx.ChannelSources.CountAsync());
        }

        var result = await RunStageAsync(url);

        // Não escolhe arbitrariamente entre canais/sources distintos.
        Assert.Equal(1, result.AmbiguousCount);
        Assert.Empty(result.Selected);
        Assert.Contains(result.Unmatched, s => s.Url == url);
    }

    // Test 9 — unmatched
    [Fact]
    public async Task Unknown_url_remains_unmatched_and_is_passed_through()
    {
        var (channel, source) = await NewChannelAndSourceAsync("w4p1-i");
        await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, "http://host/live/u/p/1");

        var input = Stream("http://host/nothing/here");
        var result = await new SourceSelectionStage(_resolver)
            .ApplyAsync(new[] { input }, Policy());

        Assert.Empty(result.Selected);
        Assert.Contains(result.Unmatched, s => ReferenceEquals(s, input));
        Assert.Contains(result.Published, s => ReferenceEquals(s, input));
    }

    // Test 10 — determinismo
    [Fact]
    public async Task Resolution_is_deterministic_across_repeated_runs()
    {
        var (channel, source) = await NewChannelAndSourceAsync("w4p1-j");
        await _resolver.RecordChannelSourceAsync(channel.Id, source.Id, "http://HOST:80/live/alice/secretA/9");

        var first = await RunStageAsync("http://host/live/bob/secretB/9");
        var second = await RunStageAsync("http://host/live/bob/secretB/9");

        AssertMatchedOn(first, channel.Id, source.Id);
        AssertMatchedOn(second, channel.Id, source.Id);
        Assert.Equal(first.Selected[0].Candidate.SourceId, second.Selected[0].Candidate.SourceId);
        Assert.Equal(first.Selected[0].Candidate.StreamFingerprint, second.Selected[0].Candidate.StreamFingerprint);
    }

    // Extras — precedência fingerprint > URL sanitizada + fingerprint preenchido
    [Fact]
    public async Task Fingerprint_match_takes_precedence_over_sanitized_url_match()
    {
        var (channelFp, sourceFp) = await NewChannelAndSourceAsync("w4p1-k-fp");
        var (channelLegacy, sourceLegacy) = await NewChannelAndSourceAsync("w4p1-k-legacy");

        const string url = "http://host/live/u/p/400";
        await _resolver.RecordChannelSourceAsync(channelFp.Id, sourceFp.Id, url);

        // Row legacy (sem fingerprint) com a MESMA URL sanitizada, noutro canal.
        await InsertLegacyChannelSourceAsync(channelLegacy.Id, sourceLegacy.Id, url);

        var result = await RunStageAsync(url);

        // O fingerprint vence: resolve no canal com fingerprint, não no legacy.
        var single = Assert.Single(result.Selected);
        Assert.Equal(channelFp.Id, result.Channels.Single().CanonicalChannelId);
        Assert.Equal(sourceFp.Id, single.Candidate.SourceId);
        Assert.NotNull(single.Candidate.StreamFingerprint);
    }

    [Fact]
    public async Task Resolved_candidate_carries_the_persisted_fingerprint()
    {
        var (channel, source) = await NewChannelAndSourceAsync("w4p1-l");
        var stored = await _resolver.RecordChannelSourceAsync(
            channel.Id, source.Id, "http://host/live/u/p/500");

        var result = await RunStageAsync("http://HOST:80/live/u/p/500");

        var selected = Assert.Single(result.Selected);
        Assert.Equal(stored.Fingerprint, selected.Candidate.StreamFingerprint);
        Assert.Equal(StreamFingerprint.Version, stored.FingerprintVersion);
    }

    // ────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────

    private static M3uStream Stream(string url)
        => new() { Url = url, Title = "Canal W4.1", Group = "PT", IsWorking = true };

    private static SourceSelectionPolicy Policy()
        => new(MaxSourcesPerChannel: 10, PreferDistinctProviders: false);

    private async Task<SourceSelectionStageResult> RunStageAsync(string url)
    {
        var stage = new SourceSelectionStage(_resolver);
        return await stage.ApplyAsync(new[] { Stream(url) }, Policy());
    }

    private static void AssertMatchedOn(SourceSelectionStageResult result, long channelId, long sourceId)
    {
        var selected = Assert.Single(result.Selected);
        Assert.Equal(sourceId, selected.Candidate.SourceId);
        Assert.Equal(channelId, result.Channels.Single().CanonicalChannelId);
        Assert.Equal(1, result.MatchedChannelCount);
        Assert.Equal(0, result.AmbiguousCount);
    }

    private async Task<(CanonicalChannelEntity Channel, SourceEntity Source)> NewChannelAndSourceAsync(string key)
    {
        var channel = await _resolver.CreateCanonicalChannelAsync(
            key, key, EditorialCategory.Live, CanonicalEditorialGroup.PortugalLive,
            PublicationPolicy.CreateEligible, true, new List<string>(), country: "pt");
        var source = await _resolver.EnsureSourceAsync(
            key, key, SourceKind.M3U, $"file:///{key}.m3u", 0);
        return (channel, source);
    }

    private async Task InsertLegacyChannelSourceAsync(long channelId, long sourceId, string sanitizedUrl)
    {
        var now = DateTime.UtcNow;
        await using var ctx = await _resolver.GetFactory().CreateDbContextAsync();
        ctx.ChannelSources.Add(new ChannelSourceEntity
        {
            CanonicalChannelId = channelId,
            SourceId = sourceId,
            StreamUrl = sanitizedUrl,
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
}
