using System;
using System.Collections.Generic;
using System.Linq;
using m3uCrawler.Models;

namespace m3uCrawler.Services.SourceSelection;

/// <summary>
/// PHASE 13 (Wave 13-3) — Resultado do <see cref="SourceSelectionStage"/>.
///
/// <para>
/// <see cref="Published"/> é a lista que o caller deve escrever na playlist
/// funcional: streams seleccionadas (na ordem de rank do selector, por canal
/// canónico ascendente) seguidas das streams não correspondidas (ordem de
/// entrada). Preserva as instâncias <see cref="M3uStream"/> originais — as
/// URLs reais nunca são reconstruídas a partir do catálogo.
/// </para>
///
/// <para>
/// <b>Sensível:</b> <see cref="Selected"/> e <see cref="Rejected"/> contêm
/// <see cref="SelectionCandidate.StreamUrl"/> com a URL real (credentials
/// incluídas) apenas em memória. Nunca devem ser serializados, persistidos
/// ou logados; para diagnóstico usar <see cref="ToReport"/>, que só tem
/// contagens agregadas.
/// </para>
/// </summary>
public sealed record SourceSelectionStageResult(
    IReadOnlyList<M3uStream> Published,
    IReadOnlyList<SelectedSource> Selected,
    IReadOnlyList<RejectedSource> Rejected,
    IReadOnlyList<M3uStream> Unmatched,
    int MatchedChannelCount,
    int AmbiguousCount,
    bool Applied)
{
    /// <summary>
    /// PHASE 13 (Wave 13-5) — Selecção por canal canónico, na ordem de
    /// processamento (canal canónico ascendente). Propriedade puramente
    /// aditiva, <c>init</c>-only e com default vazio: não altera a semântica
    /// nem a ordem de <see cref="Selected"/> / <see cref="Rejected"/>.
    /// Vazia em <see cref="NoOp"/>.
    /// </summary>
    public IReadOnlyList<SourceSelectionChannelResult> Channels { get; init; } =
        Array.Empty<SourceSelectionChannelResult>();

    /// <summary>
    /// PHASE 13 (Wave 13-5) — Subconjunto de <see cref="Unmatched"/> cuja URL
    /// mapeia para mais de um canal canónico (mapeamento ambíguo). Puramente
    /// aditivo, <c>init</c>-only e com default vazio: <see cref="Unmatched"/>
    /// continua a incluir estas streams (pass-through de produção inalterado).
    /// A identidade das referências é a mesma de <see cref="Unmatched"/>.
    /// Vazia em <see cref="NoOp"/>.
    /// </summary>
    public IReadOnlyList<M3uStream> AmbiguousStreams { get; init; } =
        Array.Empty<M3uStream>();

    /// <summary>
    /// Resultado no-op: nenhuma selecção aplicada, todas as streams
    /// passam inalteradas (catálogo indisponível, vazio ou falha de leitura).
    /// </summary>
    public static SourceSelectionStageResult NoOp(IReadOnlyList<M3uStream> streams)
    {
        ArgumentNullException.ThrowIfNull(streams);
        var copy = streams.ToList();
        return new SourceSelectionStageResult(
            Published: copy,
            Selected: Array.Empty<SelectedSource>(),
            Rejected: Array.Empty<RejectedSource>(),
            Unmatched: copy,
            MatchedChannelCount: 0,
            AmbiguousCount: 0,
            Applied: false);
    }

    /// <summary>
    /// Projecção agregada para diagnóstico/relatório. Só contagens — nunca
    /// URLs, usernames, passwords ou tokens.
    /// </summary>
    public SourceSelectionReport ToReport()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var reason in Rejected.Select(r => r.Reason))
        {
            counts[reason] = counts.TryGetValue(reason, out var n) ? n + 1 : 1;
        }

        return new SourceSelectionReport
        {
            Applied = Applied,
            MatchedChannelCount = MatchedChannelCount,
            AmbiguousCount = AmbiguousCount,
            SelectedCount = Selected.Count,
            RejectedCount = Rejected.Count,
            UnmatchedCount = Unmatched.Count,
            RejectionCounts = counts,
        };
    }
}
