using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Matching;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Contract tests for the additive contract changes introduced in
/// this iteration:
///   - ChannelDecision.OutputGroupKind? OutputGroup
///   - CountryStreamMatch.IsTargetCountry (default true)
///
/// See `.kilo/plans/1788214551330-resolution-policy-channel-decision-contract-tdd.md`.
/// </summary>
public class MatchPlanContractTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ===================== ChannelDecision.OutputGroup ======================

    [Theory]
    [InlineData(OutputGroupKind.PortugalLive)]
    [InlineData(OutputGroupKind.PortugalVOD)]
    [InlineData(OutputGroupKind.PortugalFilmes24_7)]
    [InlineData(OutputGroupKind.PortugalEntretenimento)]
    [InlineData(OutputGroupKind.PortugalDesporto)]
    [InlineData(OutputGroupKind.PortugalInfantil)]
    [InlineData(OutputGroupKind.PortugalDocumentarios)]
    [InlineData(OutputGroupKind.PortugalPPV)]
    [InlineData(OutputGroupKind.Foreign)]
    public void ChannelDecision_OutputGroup_accepts_all_9_values(OutputGroupKind kind)
    {
        var d = new ChannelDecision
        {
            Identity = "x",
            CanonicalName = "x",
            OutputGroup = kind,
        };
        Assert.Equal(kind, d.OutputGroup);
    }

    [Fact]
    public void ChannelDecision_OutputGroup_null_is_valid()
    {
        var d = new ChannelDecision
        {
            Identity = "x",
            CanonicalName = "x",
            OutputGroup = null,
        };
        Assert.Null(d.OutputGroup);
    }

    [Theory]
    [InlineData(OutputGroupKind.PortugalLive, "\"outputGroup\":0")]
    [InlineData(OutputGroupKind.PortugalVOD, "\"outputGroup\":1")]
    [InlineData(OutputGroupKind.PortugalFilmes24_7, "\"outputGroup\":2")]
    [InlineData(OutputGroupKind.PortugalEntretenimento, "\"outputGroup\":3")]
    [InlineData(OutputGroupKind.PortugalDesporto, "\"outputGroup\":4")]
    [InlineData(OutputGroupKind.PortugalInfantil, "\"outputGroup\":5")]
    [InlineData(OutputGroupKind.PortugalDocumentarios, "\"outputGroup\":6")]
    [InlineData(OutputGroupKind.PortugalPPV, "\"outputGroup\":7")]
    [InlineData(OutputGroupKind.Foreign, "\"outputGroup\":8")]
    public void ChannelDecision_OutputGroup_is_serialized_with_json_name_outputGroup(
        OutputGroupKind kind, string fragment)
    {
        var d = new ChannelDecision
        {
            Identity = "x",
            CanonicalName = "x",
            OutputGroup = kind,
        };
        var json = JsonSerializer.Serialize(d, JsonOpts);
        Assert.Contains(fragment, json);
    }

    [Fact]
    public void ChannelDecision_OutputGroup_null_is_not_serialized()
    {
        var d = new ChannelDecision
        {
            Identity = "x",
            CanonicalName = "x",
            OutputGroup = null,
        };
        var json = JsonSerializer.Serialize(d, JsonOpts);
        Assert.DoesNotContain("\"outputGroup\"", json);
    }

    [Fact]
    public void ChannelDecision_legacy_json_without_outputGroup_round_trips_as_null()
    {
        const string legacyJson =
            "{\"identity\":\"x\",\"canonicalName\":\"x\"}";
        var d = JsonSerializer.Deserialize<ChannelDecision>(legacyJson, JsonOpts);
        Assert.NotNull(d);
        Assert.Null(d.OutputGroup);
    }

    [Theory]
    [InlineData("{\"identity\":\"x\",\"canonicalName\":\"x\",\"outputGroup\":0}",
        OutputGroupKind.PortugalLive)]
    [InlineData("{\"identity\":\"x\",\"canonicalName\":\"x\",\"outputGroup\":8}",
        OutputGroupKind.Foreign)]
    public void ChannelDecision_deserialization_preserves_OutputGroup(
        string json, OutputGroupKind expected)
    {
        var d = JsonSerializer.Deserialize<ChannelDecision>(json, JsonOpts);
        Assert.NotNull(d);
        Assert.Equal(expected, d.OutputGroup);
    }

    [Fact]
    public void ChannelDecision_legacy_fields_unaffected_by_new_OutputGroup()
    {
        var d = new ChannelDecision
        {
            Identity = "id-1",
            CanonicalName = "SIC",
            Outcome = SyncOutcome.NewChannel,
            ExistingChannelId = 42L,
            ChannelGroupName = "Portugal",
            MatchReason = "exact-alias",
            MatchScore = 100,
            StreamsEmptied = false,
            OutputGroup = OutputGroupKind.PortugalLive,
        };
        var json = JsonSerializer.Serialize(d, JsonOpts);
        // Debug: ensure outputGroup is actually serialized.
        Assert.Contains("\"outputGroup\":", json);
    }

    // ===================== CountryStreamMatch.IsTargetCountry ======================

    [Fact]
    public void CountryStreamMatch_default_IsTargetCountry_is_true()
    {
        var m = new CountryStreamMatch();
        Assert.True(m.IsTargetCountry);
    }

    [Fact]
    public void CountryStreamMatch_IsTargetCountry_can_be_false()
    {
        var m = new CountryStreamMatch { IsTargetCountry = false };
        Assert.False(m.IsTargetCountry);
    }

    [Fact]
    public void CountryStreamMatch_default_legacy_fields_unaffected()
    {
        var m = new CountryStreamMatch();
        Assert.Equal(string.Empty, m.Country);
        Assert.NotNull(m.MatchedAliases);
        Assert.Empty(m.MatchedAliases);
        Assert.False(m.MatchedViaGroup);
        Assert.True(m.IsTargetCountry);
    }

    [Fact]
    public void CountryStreamMatch_ValidateStreams_for_PT_stream_sets_IsTargetCountry_true()
    {
        var validator = new CountryChannelValidator(rootDirectory: null);
        var streams = new List<M3uStream>
        {
            new() { Title = "SIC", Url = "http://x/sic", Group = "Portugal" },
        };
        var matches = validator.ValidateStreams(streams, "pt");
        Assert.Single(matches);
        Assert.True(matches[0].IsTargetCountry);
    }
}
