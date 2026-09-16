using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using m3uCrawler.Models;

namespace m3uCrawler.Services.LiveRun;

/// <summary>
/// PHASE 9C.4 — Totalizadores de uma execução operacional
/// (Live Run). Modelo fortemente tipado que espelha os contadores
/// reais já existentes em <see cref="RunReport"/> (Telegram,
/// Playlists, Xtream, Streams) e acrescenta contadores obtidos em
/// pontos reais do ciclo de manutenção (lista alvo + Dispatcharr).
///
/// <para>
/// <b>Uso:</b> o <see cref="LiveRunMonitor"/> espelha este modelo
/// para o campo <c>CountsJson</c> de <see cref="Catalog.LiveRunEntity"/>
/// quando há uma transição de fase. Nenhum valor é persistido em
/// tabela própria — o JSON vive apenas no run.
/// </para>
///
/// <para>
/// <b>Semântica:</b> todos os campos derivados de <see cref="RunReport"/>
/// são copiados (nunca contados) a partir do relatório activo, para
/// não duplicar contagens. <see cref="TargetPlaylistEntries"/> e os
/// contadores <c>Dispatcharr*</c> são incrementados nos pontos reais
/// do ciclo de manutenção (<c>MergeStreams</c> e
/// <c>TrySyncToDispatcharrAsync</c>).
/// </para>
/// </summary>
public sealed class LiveRunCounts
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    // ===================== Telegram =====================

    public int DialogsTotal { get; set; }
    public int DialogsIncomplete { get; set; }
    public int MessagesAnalyzed { get; set; }
    public int MessagesWithMedia { get; set; }
    public int MessagesWithDocumentMedia { get; set; }

    // ===================== Playlists / candidatos =====================

    public int CandidatesFound { get; set; }
    public int PlaylistsDownloaded { get; set; }
    public int PlaylistsInvalid { get; set; }
    public int PlaylistsRejected { get; set; }
    public int CountryMatches { get; set; }

    // ===================== Xtream (publicações -> contas) =====================

    public int PublicationsDiscovered { get; set; }
    public int PublicationsResolved { get; set; }
    public int PublicationsResolutionFailed { get; set; }
    public int PublicationsRequiresReview { get; set; }
    public int XtreamAccountsDiscovered { get; set; }
    public int XtreamAccountsAfterDedup { get; set; }
    public int XtreamAccountsForwarded { get; set; }

    // ===================== Streams =====================

    public int ChannelsRecognized { get; set; }
    public int StreamsExtracted { get; set; }

    /// <summary>
    /// Lista alvo de validação: streams individuais que passaram o
    /// gate por país e vão ser testados (<c>StreamsAfterCountryFilter</c>).
    /// É o denominador honesto do progresso da fase de validação.
    /// </summary>
    public int StreamsAfterCountryFilter { get; set; }

    public int StreamsRejectedByCountry { get; set; }
    public int StreamsTested { get; set; }
    public int StreamsWorking { get; set; }
    public int StreamsFailed { get; set; }

    // ===================== Lista alvo final (merge) =====================

    /// <summary>
    /// Entradas da playlist final após <c>MergeStreams</c> (modo
    /// manutenção). 0 nos ciclos não-manutenção, onde não há merge.
    /// </summary>
    public int TargetPlaylistEntries { get; set; }

    /// <summary>
    /// Streams existentes retestados a partir da playlist alvo
    /// (<c>playlist.m3u</c>) antes do merge.
    /// </summary>
    public int ExistingPlaylistRetested { get; set; }

    // ===================== Dispatcharr =====================

    public int DispatcharrSyncAttempted { get; set; }
    public int DispatcharrSyncCompleted { get; set; }
    public int DispatcharrSyncFailed { get; set; }
    public int DispatcharrSyncSkipped { get; set; }

    /// <summary>
    /// Espelha os contadores autoritativos de <paramref name="report"/>
    /// (cópia, não contagem). Campos não presentes no relatório
    /// (<see cref="TargetPlaylistEntries"/>, Dispatcharr, ...) são
    /// preservados.
    /// </summary>
    public void MirrorFrom(RunReport? report)
    {
        if (report is null) return;

        DialogsTotal = report.DialogsTotal;
        DialogsIncomplete = report.DialogsIncomplete;
        MessagesAnalyzed = report.MessagesAnalyzed;
        MessagesWithMedia = report.MessagesWithMedia;
        MessagesWithDocumentMedia = report.MessagesWithDocumentMedia;

        CandidatesFound = report.CandidatesFound;
        PlaylistsDownloaded = report.PlaylistsDownloaded;
        PlaylistsInvalid = report.PlaylistsInvalid;
        PlaylistsRejected = report.PlaylistsRejected;
        CountryMatches = report.CountryMatches;

        PublicationsDiscovered = report.PublicationsDiscovered;
        PublicationsResolved = report.PublicationsResolved;
        PublicationsResolutionFailed = report.PublicationsResolutionFailed;
        PublicationsRequiresReview = report.PublicationsRequiresReview;
        XtreamAccountsDiscovered = report.XtreamAccountsDiscovered;
        XtreamAccountsAfterDedup = report.XtreamAccountsAfterDedup;
        XtreamAccountsForwarded = report.XtreamAccountsForwarded;

        ChannelsRecognized = report.ChannelsRecognized;
        StreamsExtracted = report.StreamsExtracted;
        StreamsAfterCountryFilter = report.StreamsAfterCountryFilter;
        StreamsRejectedByCountry = report.StreamsRejectedByCountry;
        StreamsTested = report.StreamsTested;
        StreamsWorking = report.StreamsWorking;
        StreamsFailed = report.StreamsFailed;
    }

    /// <summary>Serializa para o formato persistido em <c>CountsJson</c>.</summary>
    public string ToJson()
    {
        try
        {
            return JsonSerializer.Serialize(this, JsonOptions);
        }
        catch
        {
            return "{}";
        }
    }

    /// <summary>
    /// Desserializa <c>CountsJson</c>. JSON inválido ou vazio devolve
    /// um modelo zerado (nunca lança).
    /// </summary>
    public static LiveRunCounts FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new LiveRunCounts();
        try
        {
            return JsonSerializer.Deserialize<LiveRunCounts>(json, JsonOptions)
                   ?? new LiveRunCounts();
        }
        catch
        {
            return new LiveRunCounts();
        }
    }

    /// <summary>Cópia defensiva (sem referências partilhadas).</summary>
    public LiveRunCounts Clone() => (LiveRunCounts)MemberwiseClone();
}
