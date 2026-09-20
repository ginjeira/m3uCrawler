namespace m3uCrawler.Services.Catalog;

/// <summary>
/// W5.4 — Resultado de uma operação de lifecycle de Review.
/// <see cref="Changed"/> distingue uma transição efectiva de uma reexecução
/// idempotente (o item já estava no estado pretendido).
/// </summary>
public sealed record ReviewLifecycleResult(
    ReviewItemEntity Review,
    ReviewItemState PriorState,
    bool Changed,
    string Operation);
