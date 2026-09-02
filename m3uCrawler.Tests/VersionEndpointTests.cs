using System.Text.Json;
using m3uCrawler.Build;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

public class VersionEndpointTests
{
    [Fact]
    public void BuildVersionPayload_contains_application_field()
    {
        var payload = WebDashboardService.BuildVersionPayload();
        var json = JsonSerializer.Serialize(payload);
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("application", out var app));
        Assert.Equal("m3uCrawler", app.GetString());
    }

    [Fact]
    public void BuildVersionPayload_contains_version_field_non_empty()
    {
        var payload = WebDashboardService.BuildVersionPayload();
        var json = JsonSerializer.Serialize(payload);
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("version", out var v));
        Assert.False(string.IsNullOrWhiteSpace(v.GetString()));
    }

    [Fact]
    public void BuildVersionPayload_contains_commit_field_non_empty()
    {
        var payload = WebDashboardService.BuildVersionPayload();
        var json = JsonSerializer.Serialize(payload);
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("commit", out var c));
        Assert.False(string.IsNullOrWhiteSpace(c.GetString()));
    }

    [Fact]
    public void BuildVersionPayload_contains_build_field()
    {
        var payload = WebDashboardService.BuildVersionPayload();
        var json = JsonSerializer.Serialize(payload);
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("build", out var b));
        Assert.True(b.GetInt32() >= 0);
    }

    [Fact]
    public void BuildVersionPayload_contains_buildDate_iso8601_utc()
    {
        var payload = WebDashboardService.BuildVersionPayload();
        var json = JsonSerializer.Serialize(payload);
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("buildDate", out var d));
        var raw = d.GetString();
        Assert.False(string.IsNullOrWhiteSpace(raw));
        Assert.EndsWith("Z", raw);
        Assert.True(DateTimeOffset.TryParse(
            raw,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed));
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
    }

    [Fact]
    public void BuildVersionPayload_reflects_OverrideForTesting()
    {
        var original = BuildInfo.Current;
        try
        {
            BuildInfo.OverrideForTesting(
                "9.9.9-endpoint-test",
                "feedface0001",
                12345,
                new DateTimeOffset(2026, 9, 2, 11, 22, 33, TimeSpan.Zero));

            var payload = WebDashboardService.BuildVersionPayload();
            var json = JsonSerializer.Serialize(payload);
            var doc = JsonDocument.Parse(json);
            Assert.Equal("m3uCrawler", doc.RootElement.GetProperty("application").GetString());
            Assert.Equal("9.9.9-endpoint-test", doc.RootElement.GetProperty("version").GetString());
            Assert.Equal("feedface0001", doc.RootElement.GetProperty("commit").GetString());
            Assert.Equal(12345, doc.RootElement.GetProperty("build").GetInt32());
            Assert.Equal("2026-09-02T11:22:33Z", doc.RootElement.GetProperty("buildDate").GetString());
        }
        finally
        {
            BuildInfo.ResetForTesting();
            Assert.Same(original, BuildInfo.Current);
        }
    }

    [Fact]
    public void BuildVersionPayload_serializes_with_default_dashboard_options()
    {
        // Confirmar que o payload respeita as opções do dashboard
        // (camelCase, indentado). Isto evita drift entre o contrato
        // interno e o contrato público do JSON.
        var payload = WebDashboardService.BuildVersionPayload();
        var json = JsonSerializer.Serialize(payload);
        Assert.Contains("\"application\"", json);
        Assert.Contains("\"version\"", json);
        Assert.Contains("\"commit\"", json);
        Assert.Contains("\"build\"", json);
        Assert.Contains("\"buildDate\"", json);
    }
}
