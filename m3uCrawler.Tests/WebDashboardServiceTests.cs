using System.Text.Json;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.Sync;
using Xunit;

namespace m3uCrawler.Tests;

public class WebDashboardServiceTests
{
    // ---- IsAuthorized (lógica pura) ----

    [Fact]
    public void IsAuthorized_no_token_configured_allows_everything()
    {
        // Sem token configurado: comportamento aberto (compatibilidade).
        Assert.True(WebDashboardService.IsAuthorized(null, null, null));
        Assert.True(WebDashboardService.IsAuthorized("", "", ""));
        Assert.True(WebDashboardService.IsAuthorized("Bearer anything", "anything", ""));
    }

    [Fact]
    public void IsAuthorized_with_token_rejects_missing_credentials()
    {
        Assert.False(WebDashboardService.IsAuthorized(null, null, "secret123"));
        Assert.False(WebDashboardService.IsAuthorized("", "", "secret123"));
    }

    [Fact]
    public void IsAuthorized_accepts_valid_bearer_header()
    {
        Assert.True(WebDashboardService.IsAuthorized("Bearer secret123", null, "secret123"));
        Assert.True(WebDashboardService.IsAuthorized("bearer secret123", null, "secret123")); // case-insensitive prefix
        Assert.True(WebDashboardService.IsAuthorized("Bearer  secret123 ", null, "secret123")); // trim
    }

    [Fact]
    public void IsAuthorized_accepts_valid_query_token()
    {
        Assert.True(WebDashboardService.IsAuthorized(null, "secret123", "secret123"));
        Assert.True(WebDashboardService.IsAuthorized(null, "  secret123  ", "secret123")); // trim
    }

    [Fact]
    public void IsAuthorization_accepts_either_header_or_query()
    {
        // Header tem prioridade, mas se query também servir deve passar.
        Assert.True(WebDashboardService.IsAuthorized("Bearer secret123", "secret123", "secret123"));
    }

    [Fact]
    public void IsAuthorized_rejects_wrong_token()
    {
        Assert.False(WebDashboardService.IsAuthorized("Bearer wrong", null, "secret123"));
        Assert.False(WebDashboardService.IsAuthorized(null, "wrong", "secret123"));
        Assert.False(WebDashboardService.IsAuthorized("Bearer ", null, "secret123")); // empty value
        Assert.False(WebDashboardService.IsAuthorized("Basic secret123", null, "secret123")); // wrong scheme
    }

    [Fact]
    public void IsAuthorized_rejects_partial_match()
    {
        // Ataque por prefixo/suffixo não deve passar (comparação exacta).
        Assert.False(WebDashboardService.IsAuthorized("Bearer secret1234", null, "secret123"));
        Assert.False(WebDashboardService.IsAuthorized("Bearer xsecret123", null, "secret123"));
        Assert.False(WebDashboardService.IsAuthorized("Bearer SECRET123", null, "secret123")); // case-sensitive value
    }

    [Fact]
    public void IsAuthorized_rejects_when_xtream_credential_in_header_or_query()
    {
        // Garante que uma password Xtream (se por algum motivo vier num header/query errado)
        // não é aceite como token do dashboard.
        var xtreamPassword = "alice:secret@host/live/alice/secret/1.ts";
        Assert.False(WebDashboardService.IsAuthorized($"Bearer {xtreamPassword}", null, "dashboardToken"));
        Assert.False(WebDashboardService.IsAuthorized(null, xtreamPassword, "dashboardToken"));
    }

    // ---- Contrato JSON dos endpoints do dashboard ----
    //
    // O JavaScript inlined em WebDashboardService.BuildHtmlPage() lê os campos em
    // camelCase. Estes testes fixam o contrato de serialização (nomes + tolerância
    // de leitura) partilhado por WriteJsonAsync e pela leitura do
    // telegram_run_report.json. Se alguém remover a política camelCase ou o
    // PropertyNameCaseInsensitive, o dashboard volta a mostrar "undefined" / "Invalid Date".

    private static JsonSerializerOptions DashboardJsonOptions => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void Dashboard_endpoints_emit_camelCase_for_RunReport()
    {
        var report = new RunReport
        {
            StartedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            FinishedAt = new DateTime(2026, 1, 2, 3, 5, 6, DateTimeKind.Utc),
            DurationMs = 61000,
            Status = "ok",
            MessagesAnalyzed = 100,
            CandidatesFound = 10,
            PlaylistsDownloaded = 4,
            CountryMatches = 2,
            StreamsExtracted = 200,
            StreamsTested = 200,
            StreamsWorking = 150,
            StreamsFailed = 50
        };

        var json = JsonSerializer.Serialize(report, DashboardJsonOptions);

        Assert.Contains("\"startedAt\"", json);
        Assert.Contains("\"finishedAt\"", json);
        Assert.Contains("\"durationMs\"", json);
        Assert.Contains("\"status\"", json);
        Assert.Contains("\"messagesAnalyzed\"", json);
        Assert.Contains("\"candidatesFound\"", json);
        Assert.Contains("\"playlistsDownloaded\"", json);
        Assert.Contains("\"countryMatches\"", json);
        Assert.Contains("\"streamsExtracted\"", json);
        Assert.Contains("\"streamsTested\"", json);
        Assert.Contains("\"streamsWorking\"", json);
        Assert.Contains("\"streamsFailed\"", json);
        Assert.DoesNotContain("\"StartedAt\"", json);
        Assert.DoesNotContain("\"MessagesAnalyzed\"", json);
        Assert.DoesNotContain("\"StreamsWorking\"", json);
    }

    [Fact]
    public void Dashboard_endpoints_emit_camelCase_for_ImportHistoryEntry()
    {
        var entry = new ImportHistoryEntry
        {
            Timestamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Mode = "telegram",
            SearchTerm = "portugal",
            HistoryHours = 72,
            MaxStreams = 500,
            NewFunctionalCount = 10,
            ExistingRetestedCount = 20,
            ExistingStillWorkingCount = 15,
            FinalPlaylistCount = 25
        };

        var json = JsonSerializer.Serialize(entry, DashboardJsonOptions);

        Assert.Contains("\"timestamp\"", json);
        Assert.Contains("\"mode\"", json);
        Assert.Contains("\"searchTerm\"", json);
        Assert.Contains("\"historyHours\"", json);
        Assert.Contains("\"maxStreams\"", json);
        Assert.Contains("\"newFunctionalCount\"", json);
        Assert.Contains("\"existingRetestedCount\"", json);
        Assert.Contains("\"existingStillWorkingCount\"", json);
        Assert.Contains("\"finalPlaylistCount\"", json);
        Assert.DoesNotContain("\"Timestamp\"", json);
        Assert.DoesNotContain("\"HistoryHours\"", json);
    }

    [Fact]
    public void Dashboard_endpoints_emit_camelCase_for_CountryChannelList()
    {
        var country = new CountryChannelList
        {
            Country = "pt",
            DisplayName = "Portugal",
            Channels = new List<string> { "RTP1", "SIC" }
        };

        var json = JsonSerializer.Serialize(country, DashboardJsonOptions);

        Assert.Contains("\"country\"", json);
        Assert.Contains("\"displayName\"", json);
        Assert.Contains("\"channels\"", json);
        Assert.DoesNotContain("\"Country\"", json);
        Assert.DoesNotContain("\"DisplayName\"", json);
        Assert.DoesNotContain("\"Channels\"", json);
    }

    [Fact]
    public void Dashboard_endpoints_emit_camelCase_for_DiscoveredPlaylist()
    {
        var item = new DiscoveredPlaylist
        {
            Source = "https://example/playlist.m3u",
            Name = "example",
            CountryDetected = "pt",
            ChannelsRecognized = 5,
            StreamCount = 100,
            WorkingStreams = 80,
            State = "ok"
        };

        var json = JsonSerializer.Serialize(item, DashboardJsonOptions);

        Assert.Contains("\"source\"", json);
        Assert.Contains("\"name\"", json);
        Assert.Contains("\"countryDetected\"", json);
        Assert.Contains("\"channelsRecognized\"", json);
        Assert.Contains("\"streamCount\"", json);
        Assert.Contains("\"workingStreams\"", json);
        Assert.Contains("\"state\"", json);
        Assert.DoesNotContain("\"CountryDetected\"", json);
        Assert.DoesNotContain("\"WorkingStreams\"", json);
    }

    [Fact]
    public void Dashboard_can_read_camelCase_RunReport_written_by_Program_cs()
    {
        // telegram_run_report.json é escrito por Program.cs com JsonNamingPolicy.CamelCase.
        // Se o dashboard deserializar sem PropertyNameCaseInsensitive, perde todos os campos
        // e mostra "Invalid Date" / "-" no diagnóstico.
        var fileJson = JsonSerializer.Serialize(new RunReport
        {
            StartedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            FinishedAt = new DateTime(2026, 1, 2, 3, 5, 6, DateTimeKind.Utc),
            DurationMs = 61000,
            Status = "ok",
            MessagesAnalyzed = 100,
            CandidatesFound = 10,
            PlaylistsDownloaded = 4,
            CountryMatches = 2,
            StreamsExtracted = 200,
            StreamsTested = 200,
            StreamsWorking = 150,
            StreamsFailed = 50,
            DiscoveredPlaylists = new List<DiscoveredPlaylist>
            {
                new()
                {
                    Source = "https://example/playlist.m3u",
                    Name = "example",
                    CountryDetected = "pt",
                    ChannelsRecognized = 5,
                    StreamCount = 100,
                    WorkingStreams = 80,
                    State = "ok"
                }
            }
        }, DashboardJsonOptions);

        var report = JsonSerializer.Deserialize<RunReport>(fileJson, DashboardJsonOptions);

        Assert.NotNull(report);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), report!.StartedAt);
        Assert.Equal(61000, report.DurationMs);
        Assert.Equal("ok", report.Status);
        Assert.Equal(100, report.MessagesAnalyzed);
        Assert.Equal(10, report.CandidatesFound);
        Assert.Equal(4, report.PlaylistsDownloaded);
        Assert.Equal(2, report.CountryMatches);
        Assert.Equal(200, report.StreamsExtracted);
        Assert.Equal(200, report.StreamsTested);
        Assert.Equal(150, report.StreamsWorking);
        Assert.Equal(50, report.StreamsFailed);
        Assert.Single(report.DiscoveredPlaylists);
        Assert.Equal("https://example/playlist.m3u", report.DiscoveredPlaylists[0].Source);
        Assert.Equal("pt", report.DiscoveredPlaylists[0].CountryDetected);
        Assert.Equal(80, report.DiscoveredPlaylists[0].WorkingStreams);
    }

    [Fact]
    public void Dashboard_read_without_case_insensitive_returns_default_RunReport()
    {
        // Documenta o bug original: deserialização default (sem tolerância de caixa)
        // ignora os campos camelCase do telegram_run_report.json e deixa os campos
        // que dependem do ficheiro nos valores default da classe (StartedAt = UtcNow
        // por auto-init, restantes numéricos a 0, Status = "pending").
        var fileJson = JsonSerializer.Serialize(new RunReport
        {
            Status = "ok",
            MessagesAnalyzed = 100,
            StreamsWorking = 150
        }, DashboardJsonOptions);

        var report = JsonSerializer.Deserialize<RunReport>(fileJson);

        Assert.NotNull(report);
        Assert.Equal("pending", report!.Status);
        Assert.Equal(0, report.MessagesAnalyzed);
        Assert.Equal(0, report.StreamsWorking);
        Assert.Equal(0, report.StreamsFailed);
    }

    [Fact]
    public void MatchPlan_serialization_round_trips_classification_exclusions()
    {
        // A nova fronteira Classification -> Matching exige que o
        // MatchPlan.Serialize preserve ClassifiedExclusions + a chave
        // "classification" em Counts. A serialização tem de manter os
        // nomes das propriedades em camelCase (JSON publico do
        // dashboard / relatórios).
        var plan = new MatchPlan
        {
            Counts = new SyncReportCounts
            {
                Classification = new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["Channel"] = 42,
                    ["Bundle"] = 7,
                    ["Vod"] = 3,
                    ["LiveCam"] = 1,
                    ["Unknown"] = 2,
                    ["Placeholder"] = 4,
                },
            },
            ClassifiedExclusions = new[]
            {
                new ClassifiedExclusion
                {
                    Title = "PT - NO EVENT",
                    Group = "VIP | LIGA PORTUGAL BETCLIC",
                    Kind = ChannelKind.Vod,
                    Reason = "ppv-betclic-group",
                },
                new ClassifiedExclusion
                {
                    Title = "Filmes Batman 24/7 ( Exclusivo ) PT",
                    Group = "Portugal - Canais 24-7",
                    Kind = ChannelKind.Bundle,
                    Reason = "loop-group-pattern",
                },
            },
        };

        var json = MatchPlanSerializer.Serialize(plan);
        var back = MatchPlanSerializer.Deserialize(json);

        Assert.NotNull(back);
        Assert.Equal(42, back!.Counts.Classification["Channel"]);
        Assert.Equal(7, back.Counts.Classification["Bundle"]);
        Assert.Equal(3, back.Counts.Classification["Vod"]);
        Assert.Equal(1, back.Counts.Classification["LiveCam"]);
        Assert.Equal(2, back.Counts.Classification["Unknown"]);
        Assert.Equal(4, back.Counts.Classification["Placeholder"]);
        Assert.Equal(2, back.ClassifiedExclusions.Count);
        Assert.Equal("PT - NO EVENT", back.ClassifiedExclusions[0].Title);
        Assert.Equal(ChannelKind.Vod, back.ClassifiedExclusions[0].Kind);
        Assert.Equal("ppv-betclic-group", back.ClassifiedExclusions[0].Reason);
        Assert.Equal("Filmes Batman 24/7 ( Exclusivo ) PT", back.ClassifiedExclusions[1].Title);
        Assert.Equal(ChannelKind.Bundle, back.ClassifiedExclusions[1].Kind);
        // No credentials, no URLs.
        Assert.Empty(back.ClassifiedExclusions[0].GetType().GetProperties().Where(p => p.Name is "StreamUrl" or "Provider" or "Url"));
    }
}
