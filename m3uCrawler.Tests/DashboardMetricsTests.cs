using m3uCrawler.Models;
using m3uCrawler.Services;
using Xunit;

namespace m3uCrawler.Tests;

public class DashboardMetricsTests
{
    [Fact]
    public void SuccessRate_with_working_and_failed_returns_percentage()
    {
        Assert.Equal(59.3, DashboardMetrics.SuccessRate(1951, 1339));
    }

    [Fact]
    public void SuccessRate_zero_total_returns_null()
    {
        Assert.Null(DashboardMetrics.SuccessRate(0, 0));
    }

    [Fact]
    public void SuccessRate_only_working_is_100()
    {
        Assert.Equal(100.0, DashboardMetrics.SuccessRate(10, 0));
    }

    [Fact]
    public void Coverage_normal_case()
    {
        Assert.Equal(61.5, DashboardMetrics.Coverage(8, 13));
    }

    [Fact]
    public void Coverage_zero_total_returns_null()
    {
        Assert.Null(DashboardMetrics.Coverage(5, 0));
    }

    [Fact]
    public void DeriveRunStatus_ok_when_status_completed_and_streams_working()
    {
        var r = new RunReport
        {
            Status = "completed",
            StreamsWorking = 10,
            StreamsTested = 12,
            StreamsFailed = 2,
        };
        Assert.Equal("ok", DashboardMetrics.DeriveRunStatus(r));
    }

    [Fact]
    public void DeriveRunStatus_sem_streams_when_no_tests()
    {
        var r = new RunReport { Status = "completed", StreamsTested = 0, StreamsWorking = 0 };
        Assert.Equal("sem-streams", DashboardMetrics.DeriveRunStatus(r));
    }

    [Fact]
    public void DeriveRunStatus_falhou_when_invalid_but_no_downloads()
    {
        var r = new RunReport { Status = "pending", PlaylistsDownloaded = 0, PlaylistsInvalid = 5 };
        Assert.Equal("falhou", DashboardMetrics.DeriveRunStatus(r));
    }

    [Fact]
    public void DeriveRunStatus_null_input()
    {
        Assert.Equal("sem-relatorio", DashboardMetrics.DeriveRunStatus(null));
    }

    [Fact]
    public void SummarizeRun_does_not_invent_data()
    {
        var r = new RunReport
        {
            StartedAt = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc),
            FinishedAt = new DateTime(2026, 8, 30, 0, 0, 1, DateTimeKind.Utc),
            DurationMs = 1000,
            Status = "completed",
            MessagesAnalyzed = 100,
            CandidatesFound = 50,
            PlaylistsDownloaded = 10,
            PlaylistsInvalid = 2,
            PlaylistsRejected = 3,
            StreamsExtracted = 200,
            StreamsAfterCountryFilter = 80,
            StreamsRejectedByCountry = 120,
            StreamsTested = 80,
            StreamsWorking = 50,
            StreamsFailed = 30,
            CountryMatches = 7,
        };
        var summary = DashboardMetrics.SummarizeRun(r);
        var json = System.Text.Json.JsonSerializer.Serialize(summary);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1000, root.GetProperty("durationMs").GetInt64());
        Assert.Equal(50, root.GetProperty("candidates").GetInt32());
        Assert.Equal(80, root.GetProperty("streamsTested").GetInt32());
        Assert.Equal(50, root.GetProperty("streamsWorking").GetInt32());
        Assert.Equal(30, root.GetProperty("streamsFailed").GetInt32());
        Assert.Equal(62.5, root.GetProperty("successRatePercent").GetDouble());
        Assert.True(root.GetProperty("testsBalanced").GetBoolean());
    }

    [Fact]
    public void SummarizeRun_flags_unbalanced_tests()
    {
        var r = new RunReport { StreamsTested = 100, StreamsWorking = 40, StreamsFailed = 30 };
        var json = System.Text.Json.JsonSerializer.Serialize(DashboardMetrics.SummarizeRun(r));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("testsBalanced").GetBoolean());
    }

    [Fact]
    public void DeduplicateBySourceName_collapses_duplicate_source_name_pairs()
    {
        var items = new List<DiscoveredPlaylist>
        {
            new() { Source = "Chat A", Name = "playlistX", CountryDetected = "pt", ChannelsRecognized = 5, StreamCount = 10, StreamsAfterCountryFilter = 8, WorkingStreams = 7, State = "accepted" },
            new() { Source = "Chat A", Name = "playlistX", CountryDetected = "pt", ChannelsRecognized = 5, StreamCount = 10, StreamsAfterCountryFilter = 8, WorkingStreams = 3, State = "accepted" },
            new() { Source = "Chat A", Name = "other",   CountryDetected = "pt", ChannelsRecognized = 1, StreamCount = 2,  StreamsAfterCountryFilter = 2, WorkingStreams = 0, State = "rejected" },
            new() { Source = "Chat B", Name = "playlistX", CountryDetected = "pt", ChannelsRecognized = 5, StreamCount = 10, StreamsAfterCountryFilter = 8, WorkingStreams = 9, State = "accepted" },
        };

        var dedup = DashboardMetrics.DeduplicateBySourceName(items);

        Assert.Equal(3, dedup.Count);

        // Diagnostic: log grouped keys to understand why dedup seems to keep duplicates
        var chatAplaylistXCount = dedup.Count(d => d.Source == "Chat A" && d.Name == "playlistX");
        Assert.True(chatAplaylistXCount == 1,
            $"Expected exactly 1 entry for (Chat A, playlistX); got {chatAplaylistXCount}. Distinct sources: " +
            string.Join(", ", dedup.Select(d => $"({d.Source}|{d.Name}|x{d.Occurrences})")));

        var chatAplaylistX = dedup.Single(d => d.Source == "Chat A" && d.Name == "playlistX");
        Assert.Equal(2, chatAplaylistX.Occurrences);
        // Duplicados da mesma playlist são consolidados com MAX (não SUM) para
        // evitar duplicar contagens de streams/workings nas re-entradas do mesmo
        // URL no mesmo run.
        Assert.Equal(7, chatAplaylistX.WorkingStreams);
        Assert.Equal(10, chatAplaylistX.StreamCount);
        Assert.Equal("accepted", chatAplaylistX.State);

        Assert.Single(dedup, d => d.Source == "Chat B" && d.Name == "playlistX");
    }

    [Fact]
    public void DeduplicateBySourceName_treats_distinct_origins_as_distinct()
    {
        var items = new List<DiscoveredPlaylist>
        {
            new() { Source = "Chat A", Name = "playlistX", CountryDetected = "pt", StreamCount = 10, WorkingStreams = 1, State = "accepted" },
            new() { Source = "Chat B", Name = "playlistX", CountryDetected = "pt", StreamCount = 10, WorkingStreams = 1, State = "accepted" },
            new() { Source = "https://cdn.example.com", Name = "playlistX", CountryDetected = "pt", StreamCount = 10, WorkingStreams = 1, State = "accepted" },
        };

        var dedup = DashboardMetrics.DeduplicateBySourceName(items);
        Assert.Equal(3, dedup.Count);
    }

    [Fact]
    public void ReadLatestDispatcharrSync_pairs_plan_and_report_by_timestamp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dash-paired-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Plan do run A às 12:00:00 + report do run A. E plan do run B 13:00:00 + report do run B.
            File.WriteAllText(Path.Combine(dir, "dispatcharr_plan_20260830_120000.json"),
                "{\"dryRun\":true,\"counts\":{\"matched\":1,\"newChannels\":0,\"newStreams\":0,\"removedStreams\":0,\"skipped\":0,\"ambiguous\":0,\"failed\":0,\"totalChannels\":1}}");
            File.WriteAllText(Path.Combine(dir, "dispatcharr_report_20260830_120000.json"),
                "{\"dispatcharrVersion\":\"0.30.0\",\"startedAtUtc\":\"2026-08-30T12:00:00Z\"}");

            File.WriteAllText(Path.Combine(dir, "dispatcharr_plan_20260830_130000.json"),
                "{\"dryRun\":false,\"counts\":{\"matched\":2,\"newChannels\":2,\"newStreams\":3,\"removedStreams\":0,\"skipped\":0,\"ambiguous\":0,\"failed\":0,\"totalChannels\":4}}");
            File.WriteAllText(Path.Combine(dir, "dispatcharr_report_20260830_130000.json"),
                "{\"dispatcharrVersion\":\"0.30.0\",\"startedAtUtc\":\"2026-08-30T13:00:00Z\"}");

            var state = DashboardMetrics.ReadLatestDispatcharrSync(dir);
            Assert.NotNull(state);
            var json = System.Text.Json.JsonSerializer.Serialize(state);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Plan mais recente = 13:00:00, report emparelhado deve ser o de 13:00:00 também.
            Assert.Equal(Path.Combine(dir, "dispatcharr_plan_20260830_130000.json"),
                root.GetProperty("latestPlanPath").GetString());
            Assert.Equal(Path.Combine(dir, "dispatcharr_report_20260830_130000.json"),
                root.GetProperty("latestReportPath").GetString());
            Assert.True(root.GetProperty("planReportPaired").GetBoolean());
            // We must NOT have grabbed the 12:00:00 report by mistake.
            Assert.Equal("2026-08-30T13:00:00Z", root.GetProperty("startedAtUtc").GetString());
            // Apply mode reflects latest plan (dyrUn=false).
            Assert.False(root.GetProperty("dryRun").GetBoolean());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadLatestDispatcharrSync_falls_back_to_latest_report_when_no_paired_plan()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dash-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Só report, sem plan — o sync pode ter morrido antes de escrever o plan,
            // mas o report sobreviveu. O endpoint deve ainda assim devolvê-lo.
            File.WriteAllText(Path.Combine(dir, "dispatcharr_report_20260830_150000.json"),
                "{\"dispatcharrVersion\":\"0.30.0\",\"startedAtUtc\":\"2026-08-30T15:00:00Z\"}");

            var state = DashboardMetrics.ReadLatestDispatcharrSync(dir);
            Assert.NotNull(state);
            var json = System.Text.Json.JsonSerializer.Serialize(state);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Null(root.GetProperty("latestPlanPath").GetString());
            Assert.NotNull(root.GetProperty("latestReportPath").GetString());
            Assert.Equal("2026-08-30T15:00:00Z", root.GetProperty("startedAtUtc").GetString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadLatestDispatcharrSync_tolerates_corrupt_plan_json()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dash-corrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "dispatcharr_plan_20260830_120000.json"), "{ not valid json");
            File.WriteAllText(Path.Combine(dir, "dispatcharr_report_20260830_120000.json"),
                "{\"dispatcharrVersion\":\"0.30.0\",\"startedAtUtc\":\"2026-08-30T12:00:00Z\"}");

            var state = DashboardMetrics.ReadLatestDispatcharrSync(dir);
            Assert.NotNull(state);
            var json = System.Text.Json.JsonSerializer.Serialize(state);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Plan mal-formado: ainda devolve o estado com version (do report) e erro descritivo.
            Assert.False(root.GetProperty("planValid").GetBoolean());
            Assert.True(root.GetProperty("reportValid").GetBoolean());
            Assert.NotNull(root.GetProperty("error").GetString());
            // Counts a zero porque o plan não foi parseável.
            Assert.Equal(0, root.GetProperty("matched").GetInt32());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Coverage_zero_zero_is_null_not_zero_percent()
    {
        // Sem base de comparação (denominador 0) não devemos reportar "0% cobertura":
        // o rácio é indefinido.
        Assert.Null(DashboardMetrics.Coverage(0, 0));
    }

    [Fact]
    public void Dedup_uses_max_not_sum_for_per_playlist_values()
    {
        // Garante que o agregador não duplica streams do mesmo playlist,
        // mesmo quando cada entrada tem contagens ligeiramente diferentes (re-entradas
        // do mesmo URL no mesmo run).
        var items = new List<DiscoveredPlaylist>
        {
            new() { Source = "Chat A", Name = "X", StreamCount = 100, WorkingStreams = 90, State = "accepted" },
            new() { Source = "Chat A", Name = "X", StreamCount = 100, WorkingStreams = 80, State = "accepted" },
        };
        var dedup = DashboardMetrics.DeduplicateBySourceName(items);
        Assert.Single(dedup);
        Assert.Equal(100, dedup[0].StreamCount);
        Assert.Equal(90, dedup[0].WorkingStreams);
        Assert.Equal(2, dedup[0].Occurrences);
    }

    [Fact]
    public void ReadLatestDispatcharrSync_returns_null_when_no_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dash-no-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(DashboardMetrics.ReadLatestDispatcharrSync(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadLatestDispatcharrSync_summarizes_latest_plan_and_report()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dash-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var older = Path.Combine(dir, "dispatcharr_plan_20260820_100000.json");
            var newer = Path.Combine(dir, "dispatcharr_plan_20260830_120000.json");
            var report = Path.Combine(dir, "dispatcharr_report_20260830_120000.json");
            File.WriteAllText(older, "{\"dryRun\":true,\"counts\":{\"matched\":1,\"newChannels\":0,\"newStreams\":0,\"removedStreams\":0,\"skipped\":0,\"ambiguous\":0,\"failed\":0,\"totalChannels\":1}}");
            File.WriteAllText(newer, "{\"dryRun\":true,\"counts\":{\"matched\":5,\"newChannels\":2,\"newStreams\":3,\"removedStreams\":1,\"skipped\":0,\"ambiguous\":1,\"failed\":0,\"totalChannels\":11}}");
            File.WriteAllText(report, "{\"dispatcharrVersion\":\"0.30.0\",\"startedAtUtc\":\"2026-08-30T12:00:00Z\"}");
            // Set last write time to ensure ordering
            File.SetLastWriteTimeUtc(older, new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(newer, new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(report, new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc));

            var state = DashboardMetrics.ReadLatestDispatcharrSync(dir);
            Assert.NotNull(state);
            var json = System.Text.Json.JsonSerializer.Serialize(state);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal(newer, root.GetProperty("latestPlanPath").GetString());
            Assert.Equal(report, root.GetProperty("latestReportPath").GetString());
            Assert.Equal("2026-08-30T12:00:00Z", root.GetProperty("startedAtUtc").GetString());
            Assert.Equal("0.30.0", root.GetProperty("dispatcharrVersion").GetString());
            Assert.True(root.GetProperty("dryRun").GetBoolean());
            Assert.Equal(11, root.GetProperty("totalChannels").GetInt32());
            Assert.Equal(5, root.GetProperty("matched").GetInt32());
            Assert.Equal(2, root.GetProperty("newChannels").GetInt32());
            Assert.Equal(3, root.GetProperty("newStreams").GetInt32());
            Assert.Equal(1, root.GetProperty("removedStreams").GetInt32());
            Assert.Equal(1, root.GetProperty("ambiguous").GetInt32());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
