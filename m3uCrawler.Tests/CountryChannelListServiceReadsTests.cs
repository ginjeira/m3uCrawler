using System;
using System.IO;
using System.Linq;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Wave W3s — Leituras de país devem ser puras. <c>GetAllCountries</c> /
/// <c>GetCountry</c> (GET <c>/api/countries</c> e <c>/api/country</c>) nunca
/// criam nem modificam ficheiros; a persistência só acontece via
/// <c>SaveCountry</c> (POST <c>/api/country/save</c>) ou no arranque
/// (<c>CountryConfigProvisioner</c>).
/// </summary>
public sealed class CountryChannelListServiceReadsTests : IDisposable
{
    private readonly string _root;

    public CountryChannelListServiceReadsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"country-reads-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void GetAllCountries_on_empty_directory_returns_empty_and_creates_no_file()
    {
        Directory.CreateDirectory(_root);
        var service = new CountryChannelListService(_root);

        var items = service.GetAllCountries();

        Assert.Empty(items);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public void GetAllCountries_on_missing_directory_returns_empty_without_creating_it()
    {
        var missing = Path.Combine(_root, "nested", "countries");
        var service = new CountryChannelListService(missing);

        var items = service.GetAllCountries();

        Assert.Empty(items);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void GetCountry_without_file_returns_empty_result_and_creates_no_file()
    {
        Directory.CreateDirectory(_root);
        var service = new CountryChannelListService(_root);

        var country = service.GetCountry("pt");

        Assert.Equal("pt", country.Country);
        Assert.Empty(country.Channels);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public void Reads_do_not_modify_an_existing_file()
    {
        Directory.CreateDirectory(_root);
        var service = new CountryChannelListService(_root);
        service.SaveCountry(new CountryChannelList
        {
            Country = "pt",
            DisplayName = "Portugal",
            Channels = new System.Collections.Generic.List<string> { "RTP1", "SIC" },
        });

        var path = Path.Combine(_root, "pt.json");
        var before = File.ReadAllBytes(path);

        var all = service.GetAllCountries();
        var single = service.GetCountry("pt");

        Assert.Single(all);
        Assert.Equal("pt", single.Country);
        Assert.Equal(2, single.Channels.Count);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void SaveCountry_persists_and_is_read_back()
    {
        Directory.CreateDirectory(_root);
        var service = new CountryChannelListService(_root);

        service.SaveCountry(new CountryChannelList
        {
            Country = "ES",
            Channels = new System.Collections.Generic.List<string> { "La 1", "la 1", "Antena 3" },
        });

        Assert.True(File.Exists(Path.Combine(_root, "es.json")));

        var persisted = new CountryChannelListService(_root).GetCountry("es");
        Assert.Equal("es", persisted.Country);
        Assert.Equal(new[] { "Antena 3", "La 1" }, persisted.Channels.ToArray());
    }

    [Fact]
    public void SaveCountry_creates_missing_directory()
    {
        var missing = Path.Combine(_root, "nested", "countries");
        var service = new CountryChannelListService(missing);

        service.SaveCountry(new CountryChannelList
        {
            Country = "pt",
            Channels = new System.Collections.Generic.List<string> { "RTP1" },
        });

        Assert.True(File.Exists(Path.Combine(missing, "pt.json")));
    }

    [Fact]
    public void Reads_accept_case_insensitive_json_fields()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Path.Combine(_root, "es.json"),
            "{\"country\":\"es\",\"channels\":[\"La 1\",\"Antena 3\"]}");

        var items = new CountryChannelListService(_root).GetAllCountries();

        Assert.Single(items);
        Assert.Equal("es", items[0].Country);
        Assert.Equal(2, items[0].Channels.Count);
    }
}
