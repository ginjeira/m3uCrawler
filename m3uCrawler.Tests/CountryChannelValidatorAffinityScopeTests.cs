using System;
using System.Collections.Generic;
using System.IO;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Wave W3s — os membros de afinidade <c>Kind=Country</c> são
/// <b>escopo de instância</b>: injectados na construção do
/// <see cref="CountryChannelValidator"/> e nunca num dicionário estático
/// partilhado. A resolução de afinidade <c>Kind=Channel</c> é feita pelo
/// <c>m3uCrawler.Services.Catalog.CatalogResolver</c> e mantém-se inalterada
/// (cobertura em
/// <c>Phase93AffinityCatalogTests.Resolution_uses_channel_affinity_and_survives_display_name_rename</c>).
/// Membros de país são apenas classificação: não criam identidade de canal.
/// </summary>
public sealed class CountryChannelValidatorAffinityScopeTests : IDisposable
{
    private const string AffinityAlias = "AFINIDADE CANAL";
    private const string Playlist = "#EXTM3U\n#EXTINF:-1,AFINIDADE CANAL\nhttp://example.com/1";

    private readonly string _root;

    public CountryChannelValidatorAffinityScopeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"affinity-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "pt.json"),
            "{\"country\":\"pt\",\"channels\":[\"RTP1\"]}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static Dictionary<string, IEnumerable<string>> CountryMembers(
        string country, params string[] members)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [country] = members,
        };

    [Fact]
    public void Constructor_injected_country_affinity_is_used()
    {
        var validator = new CountryChannelValidator(_root, CountryMembers("pt", AffinityAlias));

        var result = validator.AnalyzePlaylist(Playlist, "pt", threshold: 1);

        Assert.True(result.IsMatch);
        Assert.Equal(1, result.RecognizedChannelCount);
    }

    [Fact]
    public void Constructor_injected_country_affinity_is_not_global()
    {
        var withAffinity = new CountryChannelValidator(_root, CountryMembers("pt", AffinityAlias));
        var withoutAffinity = new CountryChannelValidator(_root);

        Assert.True(withAffinity.AnalyzePlaylist(Playlist, "pt", threshold: 1).IsMatch);
        Assert.False(withoutAffinity.AnalyzePlaylist(Playlist, "pt", threshold: 1).IsMatch);
        Assert.Equal(0, withoutAffinity.AnalyzePlaylist(Playlist, "pt", threshold: 1).RecognizedChannelCount);
    }

    [Fact]
    public void SetAffinityMembers_is_instance_scoped()
    {
        var first = new CountryChannelValidator(_root);
        var second = new CountryChannelValidator(_root);

        first.SetAffinityMembers("pt", new[] { AffinityAlias });

        Assert.True(first.AnalyzePlaylist(Playlist, "pt", threshold: 1).IsMatch);
        Assert.False(second.AnalyzePlaylist(Playlist, "pt", threshold: 1).IsMatch);
    }

    [Fact]
    public void ClearAffinityMembers_reverts_only_this_instance()
    {
        var validator = new CountryChannelValidator(_root, CountryMembers("pt", AffinityAlias));
        Assert.True(validator.AnalyzePlaylist(Playlist, "pt", threshold: 1).IsMatch);

        validator.ClearAffinityMembers("pt");

        Assert.False(validator.AnalyzePlaylist(Playlist, "pt", threshold: 1).IsMatch);
    }

    [Fact]
    public void Constructor_country_key_lookup_is_case_insensitive()
    {
        var validator = new CountryChannelValidator(_root, CountryMembers("PT", AffinityAlias));

        var result = validator.AnalyzePlaylist(Playlist, "pt", threshold: 1);

        Assert.True(result.IsMatch);
    }

    [Fact]
    public void ClearCache_drops_injected_affinity_and_reloads_file_aliases()
    {
        var validator = new CountryChannelValidator(_root, CountryMembers("pt", AffinityAlias));
        Assert.True(validator.AnalyzePlaylist(Playlist, "pt", threshold: 1).IsMatch);

        validator.ClearCache();

        Assert.False(validator.AnalyzePlaylist(Playlist, "pt", threshold: 1).IsMatch);
        Assert.True(validator.AnalyzePlaylist(
            "#EXTM3U\n#EXTINF:-1,RTP1\nhttp://example.com/2", "pt", threshold: 1).IsMatch);
    }
}
