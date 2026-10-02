using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.LiveRun;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// PHASE W-DASHBOARD — metadados no feed do Live Run: o payload da API
/// passa a expor <c>metadata</c>, as activities de fase ganham
/// <c>runId</c>, e os call-sites enriquecem o feed com proveniência
/// (candidate/mensagem) e a distinção física vs reutilizada.
/// </summary>
public class LiveRunActivityMetadataTests : IClassFixture<LiveRunInstrumentationFixture>
{
    private readonly LiveRunInstrumentationFixture _fixture;

    public LiveRunActivityMetadataTests(LiveRunInstrumentationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void ActivitiesToPayload_includes_metadata_when_present_and_null_when_absent()
    {
        var running = new LiveRunSnapshot
        {
            RunId = "run-meta",
            Mode = LiveRunMode.Telegram,
            Source = LiveRunSource.Manual,
            StartedAtUtc = DateTime.UtcNow.AddSeconds(-5),
            CurrentPhase = LiveRunPhase.Validating,
            IsRunning = true,
            PhaseIndex = 5,
            UpdatedAtUtc = DateTime.UtcNow,
            RecentActivities = new[]
            {
                new LiveRunActivity(
                    DateTime.UtcNow,
                    LiveRunActivityCategory.Telegram,
                    LiveRunActivityLevel.Info,
                    "detected 2 candidate(s)",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["messageId"] = "42",
                        ["chat"] = "chat-x",
                    }),
                new LiveRunActivity(
                    DateTime.UtcNow,
                    LiveRunActivityCategory.Phase,
                    LiveRunActivityLevel.Info,
                    "validating",
                    null),
            },
            Sanitized = true,
        };

        var payload = LiveRunApiMappings.ToStatusPayload(
            live: running,
            recentFinished: null,
            pipelineConfigured: true,
            webAllowTrigger: true);

        var json = JsonSerializer.Serialize(payload);
        using var doc = JsonDocument.Parse(json);
        var activities = doc.RootElement.GetProperty("recentActivities");
        Assert.Equal(2, activities.GetArrayLength());

        // A activity com metadados expõe o dicionário íntegro.
        var withMeta = activities[0];
        Assert.True(withMeta.TryGetProperty("metadata", out var meta));
        Assert.Equal(JsonValueKind.Object, meta.ValueKind);
        Assert.Equal("42", meta.GetProperty("messageId").GetString());
        Assert.Equal("chat-x", meta.GetProperty("chat").GetString());

        // A activity sem metadados expõe metadata=null (campo sempre presente).
        var withoutMeta = activities[1];
        Assert.True(withoutMeta.TryGetProperty("metadata", out var nullMeta));
        Assert.Equal(JsonValueKind.Null, nullMeta.ValueKind);

        // Campos pré-existentes não foram removidos.
        Assert.True(withMeta.TryGetProperty("timestampUtc", out _));
        Assert.True(withMeta.TryGetProperty("category", out _));
        Assert.True(withMeta.TryGetProperty("level", out _));
        Assert.True(withMeta.TryGetProperty("message", out _));
    }

    [Fact]
    public async Task PublishActivity_includes_runId_in_phase_metadata()
    {
        await _fixture.ResetAsync();

        var monitor = new LiveRunMonitor(
            _fixture.Factory,
            liveRunId: 0,
            runId: "run-phase-1",
            mode: LiveRunMode.Telegram,
            source: LiveRunSource.Manual,
            startedAtUtc: DateTime.UtcNow);

        await monitor.EnterPhaseAsync(LiveRunPhase.ReadingTelegram, "reading");

        var phase = monitor.ActivitiesSnapshot
            .First(a => a.Category == LiveRunActivityCategory.Phase);
        Assert.NotNull(phase.Metadata);
        Assert.Equal("run-phase-1", phase.Metadata!["runId"]);
        Assert.Equal("ReadingTelegram", phase.Metadata["phase"]);
        Assert.Equal("1", phase.Metadata["phaseIndex"]);
    }

    [Fact]
    public void BuildCandidateCreatedActivity_carries_provenance_without_credentials()
    {
        var candidate = new CandidatePlaylist
        {
            Id = "cand-1",
            Kind = CandidateSourceKind.Url,
            Source = "Chat A",
            Url = "http://user:secret@example.com/get.php?username=bob&password=hunter2",
            DetectedFrom = "m3u url",
            SourceMessageId = 4242,
        };

        var (message, metadata) = TelegramScraperService.BuildCandidateCreatedActivity(candidate);

        Assert.Contains("cand-1", message, StringComparison.Ordinal);
        Assert.Contains("kind=Url", message, StringComparison.Ordinal);
        Assert.Contains("from=m3u url", message, StringComparison.Ordinal);
        Assert.Equal("cand-1", metadata["candidateId"]);
        Assert.Equal("4242", metadata["messageId"]);
        Assert.Equal("Chat A", metadata["chat"]);

        // Sem credenciais: a URL nunca é incluída no metadata.
        var serialized = JsonSerializer.Serialize(metadata);
        Assert.DoesNotContain("hunter2", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("password", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildCandidateCreatedActivity_omits_messageId_when_provenance_absent()
    {
        var candidate = new CandidatePlaylist
        {
            Id = "cand-no-prov",
            Kind = CandidateSourceKind.Attachment,
            Source = "domain.example",
            DetectedFrom = "html attachment",
        };

        var (_, metadata) = TelegramScraperService.BuildCandidateCreatedActivity(candidate);

        Assert.Equal("cand-no-prov", metadata["candidateId"]);
        Assert.False(metadata.ContainsKey("messageId"));
        Assert.Equal("domain.example", metadata["chat"]);
    }

    [Fact]
    public void CountPhysicalAndReused_distinguishes_reused_from_physical()
    {
        var streams = new List<M3uStream>
        {
            new() { Url = "u1", IsWorking = true, LastTested = DateTime.UtcNow },
            new() { Url = "u2", IsWorking = true, LastTested = default }, // reused
            new() { Url = "u3", IsWorking = false, LastTested = DateTime.UtcNow },
            new() { Url = "u4", IsWorking = true, LastTested = default }, // reused
        };

        var (physical, reused) = TelegramScraperService.CountPhysicalAndReused(streams);

        Assert.Equal(2, physical);
        Assert.Equal(2, reused);
    }

    [Fact]
    public void ImportHistoryEntry_serializes_streamsSkippedAlreadyValidated_camelCase()
    {
        var entry = new ImportHistoryEntry { StreamsSkippedAlreadyValidated = 7 };

        var json = JsonSerializer.Serialize(
            entry,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.Contains("\"streamsSkippedAlreadyValidated\":7", json, StringComparison.Ordinal);
    }
}
