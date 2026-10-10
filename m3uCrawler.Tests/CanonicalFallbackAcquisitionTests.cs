using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using m3uCrawler.Services.Matching;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W-FEED (2026-10-10) — Fallback canónico na aquisição.
///
/// <para>
/// Dentro de uma playlist que passou o filtro de país (<c>AnalyzePlaylist</c>),
/// um stream sem token de país no título é aceite se resolver para um canal
/// canónico existente. NUNCA auto-cria canais e NUNCA sobrepõe a negative
/// evidence de prefixo estrangeiro. O país é parâmetro (<c>pt</c> por omissão),
/// nunca hardcoded no mecanismo.
/// </para>
///
/// <para>
/// Testa o helper do pipeline (<see cref="TelegramScraperService.FilterStreamsByCountryAsync"/>)
/// e o predicado do catálogo (<see cref="CatalogResolver.CanonicalChannelExistsByNormalizedIdentityAsync"/>),
/// sem rede.
/// </para>
/// </summary>
public class CanonicalFallbackAcquisitionTests : IAsyncLifetime
{
    private readonly string _root;
    private readonly string _dbPath;
    private readonly string _countriesDir;

    private TestDbContextFactory _factory = null!;
    private CatalogResolver _resolver = null!;

    public CanonicalFallbackAcquisitionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"wfeed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "channel-catalog.db");
        _countriesDir = Path.Combine(_root, "countries");
        Directory.CreateDirectory(_countriesDir);
        // Sem pt.json → o validador usa os aliases canónicos de fallback.
        File.WriteAllText(Path.Combine(_countriesDir, "channel-indicators.json"), "{}");
    }

    public async Task InitializeAsync()
    {
        _factory = new TestDbContextFactory(_dbPath);
        var bootstrapper = new ChannelCatalogBootstrapper(_dbPath);
        await using (var ctx = await bootstrapper.InitializeAsync())
        {
        }
        _resolver = new CatalogResolver(_factory, _dbPath);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private CountryChannelValidator CreateValidator() => new(_countriesDir);

    private static M3uStream Stream(string title, string url = "http://x/s.ts") => new()
    {
        Url = url,
        Title = title,
        Group = "Geral",
    };

    private async Task CreateCanonicalAsync(string key, string displayName, params string[] aliases)
        => await _resolver.CreateCanonicalChannelAsync(
            key,
            displayName,
            EditorialCategory.Live,
            CanonicalGroupKeys.PortugalGeneralistas,
            PublicationPolicy.CreateEligible,
            isEnabled: true,
            normalizedAliases: aliases);

    // ===================== DiscoverySettings =====================

    [Fact]
    public void Feed_canonical_fallback_defaults_to_true()
    {
        Assert.True(DiscoverySettings.DefaultFeedCanonicalFallback);
        Assert.True(new DiscoverySettings().FeedCanonicalFallback);
    }

    [Fact]
    public void Feed_canonical_fallback_survives_clone_and_overrides()
    {
        var settings = new DiscoverySettings { FeedCanonicalFallback = false };

        Assert.False(settings.Clone().FeedCanonicalFallback);

        // WithOverrides clona, pelo que o valor persistido é preservado.
        var resolved = settings.WithOverrides("iptv", 48, 100, 5);
        Assert.False(resolved.FeedCanonicalFallback);
    }

    [Fact]
    public void DiscoverySettingsProvider_resolves_feed_canonical_fallback()
    {
        var dir = Path.Combine(_root, "provider");
        var store = new AppSettingsStore(dir);
        var current = store.Load();
        current.Discovery = new DiscoverySettings { FeedCanonicalFallback = false };
        store.Save(current);

        var provider = new DiscoverySettingsProvider(store);

        Assert.False(provider.ResolveFeedCanonicalFallback());
        Assert.False(provider.Load().FeedCanonicalFallback);
        Assert.False(provider.Resolve("iptv", 24, 100, 0).FeedCanonicalFallback);
    }

    // ===================== CatalogResolver predicate =====================

    [Fact]
    public async Task Exists_by_normalized_identity_matches_canonical_key()
    {
        await CreateCanonicalAsync("wfeed-demo-42", "WFeed Demo 42");

        var normalized = ChannelNormalizer.Normalize("WFeed Demo 42");
        Assert.True(await _resolver.CanonicalChannelExistsByNormalizedIdentityAsync(normalized));
    }

    [Fact]
    public async Task Exists_by_normalized_identity_matches_channel_alias()
    {
        await CreateCanonicalAsync("wfeed-demo-42", "WFeed Demo 42", "WFeed Alias 42");

        var normalized = ChannelNormalizer.Normalize("wfeed-alias-42");
        Assert.True(await _resolver.CanonicalChannelExistsByNormalizedIdentityAsync(normalized));
    }

    [Fact]
    public async Task Exists_by_normalized_identity_is_false_for_unknown()
    {
        await CreateCanonicalAsync("wfeed-demo-42", "WFeed Demo 42");

        Assert.False(await _resolver.CanonicalChannelExistsByNormalizedIdentityAsync("canal inexistente"));
        Assert.False(await _resolver.CanonicalChannelExistsByNormalizedIdentityAsync(string.Empty));
    }

    [Fact]
    public async Task Exists_ignores_disabled_channels()
    {
        await _resolver.CreateCanonicalChannelAsync(
            "wfeed-demo-42",
            "WFeed Demo 42",
            EditorialCategory.Live,
            CanonicalGroupKeys.PortugalGeneralistas,
            PublicationPolicy.CreateEligible,
            isEnabled: false,
            normalizedAliases: Array.Empty<string>());

        var normalized = ChannelNormalizer.Normalize("WFeed Demo 42");
        Assert.False(await _resolver.CanonicalChannelExistsByNormalizedIdentityAsync(normalized));
    }

    // ===================== FilterStreamsByCountryAsync =====================

    [Fact]
    public async Task Fallback_accepts_stream_without_country_token_that_resolves_to_canonical()
    {
        await CreateCanonicalAsync("wfeed-demo-42", "WFeed Demo 42");
        var validator = CreateValidator();
        var streams = new List<M3uStream> { Stream("WFeed Demo 42") };

        var (accepted, rejected, viaFallback) = await TelegramScraperService.FilterStreamsByCountryAsync(
            validator, streams, "pt", _resolver, feedCanonicalFallback: true);

        var s = Assert.Single(accepted);
        Assert.Equal("WFeed Demo 42", s.Title);
        Assert.Equal(0, rejected);
        Assert.Equal(1, viaFallback);
    }

    [Fact]
    public async Task Fallback_off_preserves_current_behaviour()
    {
        await CreateCanonicalAsync("wfeed-demo-42", "WFeed Demo 42");
        var validator = CreateValidator();
        var streams = new List<M3uStream> { Stream("WFeed Demo 42") };

        var (accepted, rejected, viaFallback) = await TelegramScraperService.FilterStreamsByCountryAsync(
            validator, streams, "pt", _resolver, feedCanonicalFallback: false);

        Assert.Empty(accepted);
        Assert.Equal(1, rejected);
        Assert.Equal(0, viaFallback);
    }

    [Fact]
    public async Task Fallback_rejects_stream_without_canonical_channel()
    {
        var validator = CreateValidator();
        var streams = new List<M3uStream> { Stream("WFeed Demo 42") };

        var (accepted, rejected, viaFallback) = await TelegramScraperService.FilterStreamsByCountryAsync(
            validator, streams, "pt", _resolver, feedCanonicalFallback: true);

        Assert.Empty(accepted);
        Assert.Equal(1, rejected);
        Assert.Equal(0, viaFallback);
    }

    [Fact]
    public async Task Fallback_preserves_foreign_prefix_negative_evidence()
    {
        // Mesmo que o título resolva para um canal canónico, o prefixo "BE"
        // (Bélgica) mantém a rejeição — negative evidence preservada.
        await CreateCanonicalAsync("wfeed-demo-42", "WFeed Demo 42");
        var validator = CreateValidator();
        var streams = new List<M3uStream> { Stream("BE - WFeed Demo 42") };

        var (accepted, rejected, viaFallback) = await TelegramScraperService.FilterStreamsByCountryAsync(
            validator, streams, "pt", _resolver, feedCanonicalFallback: true);

        Assert.Empty(accepted);
        Assert.Equal(1, rejected);
        Assert.Equal(0, viaFallback);
    }

    [Fact]
    public async Task Fallback_without_catalog_resolver_is_noop()
    {
        var validator = CreateValidator();
        var streams = new List<M3uStream> { Stream("WFeed Demo 42") };

        var (accepted, rejected, viaFallback) = await TelegramScraperService.FilterStreamsByCountryAsync(
            validator, streams, "pt", catalogResolver: null, feedCanonicalFallback: true);

        Assert.Empty(accepted);
        Assert.Equal(1, rejected);
        Assert.Equal(0, viaFallback);
    }

    [Fact]
    public async Task Regression_country_streams_are_still_accepted_without_fallback()
    {
        await CreateCanonicalAsync("wfeed-demo-42", "WFeed Demo 42");
        var validator = CreateValidator();
        var streams = new List<M3uStream>
        {
            Stream("RTP1", "http://x/rtp1.ts"),
            Stream("SIC", "http://x/sic.ts"),
            Stream("TVI", "http://x/tvi.ts"),
        };

        var (accepted, rejected, viaFallback) = await TelegramScraperService.FilterStreamsByCountryAsync(
            validator, streams, "pt", _resolver, feedCanonicalFallback: true);

        Assert.Equal(3, accepted.Count);
        Assert.Equal(0, rejected);
        Assert.Equal(0, viaFallback); // aceites pelo país, não pelo fallback
    }

    [Fact]
    public async Task Fallback_is_country_parameterised_not_hardcoded_to_pt()
    {
        // Com país "es" o título não bate nos aliases ES, mas o fallback
        // canónico (agnóstico ao país) aceita: o mecanismo usa o countryCode
        // como parâmetro e não está preso a "pt".
        await CreateCanonicalAsync("wfeed-demo-42", "WFeed Demo 42");
        var validator = CreateValidator();
        var streams = new List<M3uStream> { Stream("WFeed Demo 42") };

        var (accepted, _, viaFallback) = await TelegramScraperService.FilterStreamsByCountryAsync(
            validator, streams, "es", _resolver, feedCanonicalFallback: true);

        Assert.Single(accepted);
        Assert.Equal(1, viaFallback);

        // O guard de prefixo estrangeiro (negative evidence) é específico de
        // "pt"; para "es" não é aplicado.
        Assert.True(CountryChannelValidator.HasForeignCountryPrefix("BE - WFeed Demo 42", "pt"));
        Assert.False(CountryChannelValidator.HasForeignCountryPrefix("BE - WFeed Demo 42", "es"));
    }

    [Fact]
    public void RunReport_canonical_fallback_counter_defaults_to_zero()
    {
        Assert.Equal(0, new RunReport().StreamsMatchedViaCanonicalFallback);
    }
}
