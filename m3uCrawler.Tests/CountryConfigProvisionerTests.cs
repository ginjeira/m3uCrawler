using System.Text.Json;
using m3uCrawler.Services.Configuration;
using Xunit;

namespace m3uCrawler.Tests;

public sealed class CountryConfigProvisionerTests : IDisposable
{
    private readonly string _root;

    public CountryConfigProvisionerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "m3ucrawler-country-provisioner-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Seeds_pt_json_with_country_and_channels()
    {
        var countriesDir = Path.Combine(_root, "runtime-data", "countries");

        CountryConfigProvisioner.EnsureProvisioned(countriesDir);

        var path = Path.Combine(countriesDir, "pt.json");
        Assert.True(File.Exists(path));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("pt", doc.RootElement.GetProperty("country").GetString());

        var channels = doc.RootElement.GetProperty("channels")
            .EnumerateArray()
            .Select(c => c.GetString() ?? string.Empty)
            .ToList();

        Assert.True(channels.Count >= 14, $"esperado >=14 canais, obtido {channels.Count}");
        Assert.Contains("RTP1", channels);
    }

    [Fact]
    public void Does_not_overwrite_existing_pt_json()
    {
        var countriesDir = Path.Combine(_root, "countries");
        Directory.CreateDirectory(countriesDir);
        var path = Path.Combine(countriesDir, "pt.json");
        const string sentinel = "{\"country\":\"SENTINEL\"}";
        File.WriteAllText(path, sentinel);

        CountryConfigProvisioner.EnsureProvisioned(countriesDir);

        Assert.Equal(sentinel, File.ReadAllText(path));
    }

    [Fact]
    public void Creates_directory_if_missing()
    {
        var countriesDir = Path.Combine(_root, "deep", "nested", "countries");
        Assert.False(Directory.Exists(countriesDir));

        CountryConfigProvisioner.EnsureProvisioned(countriesDir);

        Assert.True(Directory.Exists(countriesDir));
        Assert.True(File.Exists(Path.Combine(countriesDir, "pt.json")));
    }

    [Fact]
    public void Is_idempotent_on_second_call()
    {
        var countriesDir = Path.Combine(_root, "countries");

        CountryConfigProvisioner.EnsureProvisioned(countriesDir);
        var path = Path.Combine(countriesDir, "pt.json");
        var firstWrite = File.ReadAllBytes(path);

        CountryConfigProvisioner.EnsureProvisioned(countriesDir);

        Assert.Equal(firstWrite, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(countriesDir));
    }

    [Fact]
    public void IsCountryDataAvailable_true_for_provisioned_file_with_channels()
    {
        var countriesDir = Path.Combine(_root, "countries");
        CountryConfigProvisioner.EnsureProvisioned(countriesDir);

        Assert.True(CountryConfigProvisioner.IsCountryDataAvailable(countriesDir, "pt"));
    }

    [Fact]
    public void IsCountryDataAvailable_true_for_builtin_baseline_when_file_missing()
    {
        var missingDir = Path.Combine(_root, "does-not-exist");

        Assert.True(CountryConfigProvisioner.IsCountryDataAvailable(missingDir, "pt"));
        Assert.False(Directory.Exists(missingDir));
    }

    [Fact]
    public void IsCountryDataAvailable_false_for_unknown_country_without_file()
    {
        var countriesDir = Path.Combine(_root, "countries");

        Assert.False(CountryConfigProvisioner.IsCountryDataAvailable(countriesDir, "zz"));
        Assert.False(Directory.Exists(countriesDir));
    }

    [Fact]
    public void IsCountryDataAvailable_false_when_file_has_no_channels()
    {
        var countriesDir = Path.Combine(_root, "countries");
        Directory.CreateDirectory(countriesDir);
        File.WriteAllText(Path.Combine(countriesDir, "es.json"), "{\"country\":\"es\",\"channels\":[]}");

        Assert.False(CountryConfigProvisioner.IsCountryDataAvailable(countriesDir, "es"));
    }

    [Fact]
    public void IsCountryDataAvailable_is_case_insensitive_and_pure()
    {
        var countriesDir = Path.Combine(_root, "countries");
        Directory.CreateDirectory(countriesDir);
        var path = Path.Combine(countriesDir, "es.json");
        File.WriteAllText(path, "{\"country\":\"es\",\"Channels\":[\"La 1\"]}");
        var before = File.ReadAllBytes(path);

        Assert.True(CountryConfigProvisioner.IsCountryDataAvailable(countriesDir, "ES"));
        Assert.Equal(before, File.ReadAllBytes(path));
    }
}
