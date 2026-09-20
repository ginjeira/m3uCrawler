using System;

namespace m3uCrawler.Services.Catalog;

/// <summary>
/// W5.4 — Máquina de estados do <see cref="ReviewItemEntity"/>.
///
/// <para>
/// Normativo: <c>33-STATE-MACHINES.md §ReviewItem</c>, DL-105 e DL-119.
/// Pura e determinística (não depende de EF/DB), para que as transições sejam
/// testáveis sem infraestrutura.
/// </para>
///
/// <para>Transições permitidas (exactamente estas):</para>
/// <list type="bullet">
///   <item><c>Open → InReview</c></item>
///   <item><c>Open → Ignored</c></item>
///   <item><c>InReview → Resolved</c></item>
///   <item><c>InReview → Ignored</c></item>
///   <item><c>Resolved → Open</c> (reabertura manual auditada)</item>
///   <item><c>Ignored → Open</c> (reabertura manual auditada)</item>
/// </list>
/// </summary>
public static class ReviewLifecycle
{
    /// <summary>Transição permitida entre dois estados do lifecycle.</summary>
    public static bool CanTransition(ReviewItemState from, ReviewItemState to)
        => (from, to) switch
        {
            (ReviewItemState.Open, ReviewItemState.InReview) => true,
            (ReviewItemState.Open, ReviewItemState.Ignored) => true,
            (ReviewItemState.InReview, ReviewItemState.Resolved) => true,
            (ReviewItemState.InReview, ReviewItemState.Ignored) => true,
            (ReviewItemState.Resolved, ReviewItemState.Open) => true,
            (ReviewItemState.Ignored, ReviewItemState.Open) => true,
            _ => false,
        };

    /// <summary>
    /// Garante que <paramref name="to"/> é alcançável a partir de
    /// <paramref name="from"/>; caso contrário lança <see cref="InvalidOperationException"/>.
    /// </summary>
    public static void EnsureTransition(ReviewItemState from, ReviewItemState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException(
                $"Transição de ReviewItem inválida: {from} → {to}.");
        }
    }

    /// <summary>Estado terminal do lifecycle (<c>Resolved</c>/<c>Ignored</c>).</summary>
    public static bool IsTerminal(ReviewItemState state)
        => state is ReviewItemState.Resolved or ReviewItemState.Ignored;

    /// <summary>Estado inicial do lifecycle.</summary>
    public static bool IsOpen(ReviewItemState state) => state == ReviewItemState.Open;
}
