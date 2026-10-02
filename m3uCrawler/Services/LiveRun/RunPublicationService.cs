using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Configuration;
using m3uCrawler.Services.SourceSelection;
using m3uCrawler.Services.Sync;

namespace m3uCrawler.Services.LiveRun;

/// <summary>
/// Parâmetros de uma publicação de ciclo Telegram. Tudo o que varia
/// entre os entry points (CLI single cycle, manutenção, dashboard e
/// scheduler) é expresso aqui; a sequência de etapas é fixa.
/// </summary>
public sealed class RunPublicationRequest
{
    public IReadOnlyList<M3uStream> Streams { get; init; } = Array.Empty<M3uStream>();

    public RunReport Report { get; init; } = new();

    public string Keyword { get; init; } = string.Empty;

    public int HistoryHours { get; init; } = DiscoverySettings.DefaultHistoryHours;

    public int MaxStreams { get; init; } = DiscoverySettings.DefaultMaxStreams;

    /// <summary>Filtro de domínio opcional, aplicado antes do country gate.</summary>
    public string? DomainFilter { get; init; }

    public string CountryCode { get; init; } = "pt";

    /// <summary>Modo gravado em <c>import_history.json</c>.</summary>
    public string HistoryMode { get; init; } = "TelegramSearch";

    /// <summary>
    /// Nome do ficheiro M3U. <c>null</c> usa
    /// <c>telegram_playlist_&lt;timestamp&gt;.m3u</c> (single cycle).
    /// </summary>
    public string? PlaylistFileName { get; init; }

    /// <summary>
    /// Nome do ficheiro de relatório JSON. <c>null</c> usa
    /// <c>telegram_report_&lt;timestamp&gt;.json</c> (single cycle).
    /// </summary>
    public string? JsonReportFileName { get; init; }

    /// <summary>
    /// Executa o country gate (<c>ValidateStreams</c>) antes da selecção.
    /// É diagnóstico (não filtra), preservando o comportamento histórico do
    /// single cycle.
    /// </summary>
    public bool RunCountryGate { get; init; } = true;

    public bool RecordHistory { get; init; } = true;

    /// <summary>Emite o log detalhado do single cycle (lista de streams).</summary>
    public bool Verbose { get; init; } = true;

    public int? NewFunctionalCountOverride { get; init; }

    public int? ExistingRetestedCount { get; init; }

    public int? ExistingStillWorkingCount { get; init; }

    public int? FinalPlaylistCount { get; init; }

    /// <summary>
    /// Quando <c>true</c>, o histórico grava o número de entradas
    /// publicadas em <c>FinalPlaylistCount</c> (modo manutenção). O single
    /// cycle mantém o default (0), preservando o comportamento anterior.
    /// </summary>
    public bool TrackFinalPlaylistCount { get; init; }
}

/// <summary>
/// Resultado da publicação. Os caminhos apontam para os artefactos
/// efectivamente publicados (já atómicos).
/// </summary>
public sealed record RunPublicationResult(
    IReadOnlyList<M3uStream> Published,
    SourceSelectionStageResult Selection,
    string PlaylistPath,
    string JsonReportPath,
    string RunReportPath,
    DispatcharrSyncOutcome Dispatcharr);

/// <summary>
/// Contrato do serviço de publicação única de um ciclo Telegram.
/// </summary>
public interface IRunPublicationService
{
    Task<RunPublicationResult> PublishAsync(
        RunPublicationRequest request,
        ILiveRunProgress? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Serviço de aplicação único que transforma o resultado de discovery
/// em output publicado. Esta é a <b>única</b> implementação da cauda do
/// pipeline Telegram, partilhada por:
/// <list type="bullet">
///   <item>ciclo único da CLI (<c>--telegram</c>);</item>
///   <item>ciclo de manutenção (<c>--telegram-maintain</c>);</item>
///   <item><c>POST /api/run/start</c> (dashboard);</item>
///   <item><c>ScheduledTelegramRunAction</c> (scheduler).</item>
/// </list>
/// Não existe um segundo pipeline: qualquer atalho passa por aqui.
///
/// <para>
/// Ordem normativa executada (P5 → P15):
/// country gate → selecção de fontes (política persistida; só filtra
/// quando <c>Applied</c>) → composição/publicação atómica → relatório
/// de execução → histórico → sincronização Dispatcharr (honrando
/// <c>dry_run</c> configurado).
/// </para>
///
/// <para>
/// <b>Ingestão de catálogo (P8):</b> ocorre durante a discovery, via
/// <c>PipelineIngestionService</c> injectado no
/// <c>TelegramScraperService</c>. Este serviço recebe o resultado já
/// ingerido e não re-ingere, evitando duplicação.
/// </para>
///
/// <para>
/// Determinismo: o timestamp é calculado uma única vez por publicação a
/// partir do relógio injectado (default <see cref="DateTime.Now"/>), de
/// modo que o mesmo snapshot produza os mesmos nomes e conteúdos.
/// </para>
/// </summary>
public sealed class RunPublicationService : IRunPublicationService
{
    private readonly string _outputDir;
    private readonly PlaylistManagerService _playlistManager;
    private readonly ImportHistoryService? _importHistory;
    private readonly CountryChannelValidator? _countryValidator;
    private readonly CatalogResolver? _catalog;
    private readonly DispatcharrSyncCoordinator _dispatcharr;
    private readonly Func<DateTime> _clock;
    private readonly Func<DateTime> _utcClock;

    public RunPublicationService(
        string outputDir,
        PlaylistManagerService playlistManager,
        ImportHistoryService? importHistory,
        CountryChannelValidator? countryValidator,
        CatalogResolver? catalog,
        DispatcharrSyncCoordinator? dispatcharr = null,
        Func<DateTime>? clock = null,
        Func<DateTime>? utcClock = null)
    {
        _outputDir = outputDir ?? throw new ArgumentNullException(nameof(outputDir));
        _playlistManager = playlistManager ?? throw new ArgumentNullException(nameof(playlistManager));
        _importHistory = importHistory;
        _countryValidator = countryValidator;
        _catalog = catalog;
        _dispatcharr = dispatcharr ?? new DispatcharrSyncCoordinator();
        _clock = clock ?? (() => DateTime.Now);
        _utcClock = utcClock ?? (() => DateTime.UtcNow);
    }

    public async Task<RunPublicationResult> PublishAsync(
        RunPublicationRequest request,
        ILiveRunProgress? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var report = request.Report ?? new RunReport();
        var streams = request.Streams.ToList();

        // ---- Filtro de domínio (pré-country gate) ----
        if (!string.IsNullOrWhiteSpace(request.DomainFilter))
        {
            int beforeFilter = streams.Count;
            streams = streams
                .Where(s => UrlMatchesDomain(s.Url, request.DomainFilter!))
                .ToList();
            if (request.Verbose)
            {
                Console.WriteLine($"🌐 Após filtro de domínio: {streams.Count}/{beforeFilter} streams");
            }
        }

        if (request.Verbose)
        {
            Console.WriteLine($"\n✅ Streams funcionais encontradas no Telegram: {streams.Count}");
            foreach (var stream in streams)
            {
                Console.WriteLine($"  • {stream.Title} ({stream.ResponseTime}ms) :: {CredentialSanitizer.SanitizeUrl(stream.Url)}");
            }
        }

        // ---- P5 — Country gate (diagnóstico, não filtra) ----
        if (request.RunCountryGate && _countryValidator is not null)
        {
            var countryMatches = _countryValidator.ValidateStreams(streams, request.CountryCode);
            Console.WriteLine(
                $"📡 Validação por canais {request.CountryCode.ToUpperInvariant()}: {countryMatches.Count} stream(s) correspondentes.");
            foreach (var match in countryMatches.Take(10))
            {
                Console.WriteLine(
                    $"  • {request.CountryCode.ToUpperInvariant()} match: {match.Stream.Title} -> {string.Join(", ", match.MatchedAliases)}");
            }
        }

        // ---- P12 — Selecção de fontes com a política persistida ----
        var policies = _catalog is null
            ? SourceSelectionPolicySet.Default
            : await new SourceSelectionPolicyResolver(_catalog)
                .LoadEffectivePoliciesAsync(cancellationToken)
                .ConfigureAwait(false);
        var selection = await new SourceSelectionStage(_catalog)
            .ApplyAsync(streams, policies, cancellationToken)
            .ConfigureAwait(false);
        report.SourceSelection = selection.ToReport();
        if (selection.Applied)
        {
            Console.WriteLine(
                $"🧩 Selecção Phase 13: selected={selection.Selected.Count} " +
                $"rejected={selection.Rejected.Count} unmatched={selection.Unmatched.Count} " +
                $"canais={selection.MatchedChannelCount} ambíguos={selection.AmbiguousCount}");
        }
        streams = selection.Published.ToList();

        // ---- P14 — Composição/publicação atómica ----
        var generatedAt = _clock();
        var timestamp = generatedAt.ToString("yyyyMMdd_HHmmss");
        var playlistPath = ResolveArtifactPath(request.PlaylistFileName, $"telegram_playlist_{timestamp}.m3u");
        var jsonReportPath = ResolveArtifactPath(request.JsonReportFileName, $"telegram_report_{timestamp}.json");
        var runReportPath = Path.Combine(_outputDir, "telegram_run_report.json");

        await _playlistManager.SaveToM3uPlaylistAtomic(streams, playlistPath, generatedAt).ConfigureAwait(false);
        await _playlistManager.SaveToJsonReport(streams, jsonReportPath, generatedAt).ConfigureAwait(false);
        await SaveRunReportAsync(runReportPath, report).ConfigureAwait(false);

        if (request.Verbose)
        {
            Console.WriteLine($"\n✨ Arquivos gerados:");
            Console.WriteLine($"   • Playlist: {playlistPath}");
            Console.WriteLine($"   • Relatório: {jsonReportPath}");
            Console.WriteLine($"   • Relatório de execução: {runReportPath}");
            if (streams.Count == 0)
            {
                Console.WriteLine("❌ Nenhum stream funcional encontrado no Telegram.");
            }
        }

        // ---- P15 — Dispatcharr (dry_run respeitado pelo serviço de sync) ----
        var dispatcharrSelection = selection.Applied
            ? DispatcharrSourceSelectionFactory.FromStageResult(selection, policies, _utcClock())
            : null;
        var dispatcharrOutcome = await _dispatcharr.RunAsync(
            playlistPath,
            _outputDir,
            _catalog,
            dispatcharrSelection,
            progress,
            cancellationToken).ConfigureAwait(false);

        // ---- Histórico ----
        if (request.RecordHistory && _importHistory is not null)
        {
            await _importHistory.RecordImportAsync(BuildHistoryEntry(request, report, streams)).ConfigureAwait(false);
        }
        return new RunPublicationResult(
            Published: streams,
            Selection: selection,
            PlaylistPath: playlistPath,
            JsonReportPath: jsonReportPath,
            RunReportPath: runReportPath,
            Dispatcharr: dispatcharrOutcome);
    }

    private string ResolveArtifactPath(string? explicitName, string defaultName)
        => string.IsNullOrWhiteSpace(explicitName)
            ? Path.Combine(_outputDir, defaultName)
            : Path.Combine(_outputDir, explicitName!);

    private ImportHistoryEntry BuildHistoryEntry(
        RunPublicationRequest request,
        RunReport report,
        IReadOnlyList<M3uStream> published)
    {
        return new ImportHistoryEntry
        {
            Timestamp = _utcClock(),
            Mode = request.HistoryMode,
            SearchTerm = request.Keyword,
            HistoryHours = request.HistoryHours,
            MaxStreams = request.MaxStreams,
            NewFunctionalCount = request.NewFunctionalCountOverride ?? published.Count,
            ExistingRetestedCount = request.ExistingRetestedCount ?? 0,
            ExistingStillWorkingCount = request.ExistingStillWorkingCount ?? 0,
            FinalPlaylistCount = request.FinalPlaylistCount
                ?? (request.TrackFinalPlaylistCount ? published.Count : 0),
            MessagesAnalyzed = report.MessagesAnalyzed,
            CandidatesFound = report.CandidatesFound,
            PlaylistsDownloaded = report.PlaylistsDownloaded,
            CountryMatches = report.CountryMatches,
            PlaylistsRejected = report.PlaylistsRejected,
            StreamsExtracted = report.StreamsExtracted,
            StreamsTested = report.StreamsTested,
            StreamsWorking = report.StreamsWorking,
            StreamsFailed = report.StreamsFailed,
            StreamsSkippedAlreadyValidated = report.StreamsSkippedAlreadyValidated,
        };
    }

    private static async Task SaveRunReportAsync(string path, RunReport report)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(
                report,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                });
            await File.WriteAllTextAsync(path, json, Encoding.UTF8).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Não foi possível guardar o relatório de execução: {ex.Message}");
        }
    }

    private static bool UrlMatchesDomain(string url, string domainFilter)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        string host = uri.Host;
        return host.Equals(domainFilter, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith($".{domainFilter}", StringComparison.OrdinalIgnoreCase);
    }
}
