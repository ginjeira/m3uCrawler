namespace m3uCrawler.Services.Catalog;

/// <summary>
/// W6b-1 — Acção de catálogo declarada por uma aprovação de Review.
///
/// <para>
/// <c>05-CATALOGUE.md §9</c>: uma aprovação de Review que altere o
/// catálogo deve declarar exactamente que mudança produz. A acção é
/// obrigatória e explícita; não existe caminho implícito que crie ou
/// aliase um <see cref="CanonicalChannelEntity"/> a partir de uma
/// observação.
/// </para>
/// </summary>
public enum ReviewApprovalAction
{
    /// <summary>Adiciona o valor observado como alias de um canal existente.</summary>
    AddAlias = 0,

    /// <summary>
    /// Cria explicitamente um canal canónico declarado pelo
    /// administrador e adiciona-lhe o alias observado. É o único
    /// caminho de criação de canal a partir de uma Review.
    /// </summary>
    CreateChannel = 1,

    /// <summary>Marca a Review como excluída; sem alteração de catálogo.</summary>
    Exclude = 2,
}

/// <summary>
/// Especificação do canal canónico a criar numa aprovação
/// <see cref="ReviewApprovalAction.CreateChannel"/>. A identidade
/// (<see cref="Key"/>) e o nome são declarados pelo administrador.
/// Os campos editoriais são opcionais e assumem valores neutros.
/// </summary>
public sealed record ReviewChannelSpec(
    string Key,
    string Name,
    string? Country = null,
    EditorialCategory? EditorialCategory = null,
    CanonicalEditorialGroup? EditorialGroup = null,
    PublicationPolicy? PublicationPolicy = null);

/// <summary>
/// Declaração explícita do efeito de uma aprovação/exclusão de Review.
/// A validação de campos obrigatórios é feita em
/// <see cref="CatalogResolver.ApplyReviewApprovalAsync"/> e traduzida
/// em <c>400</c> pela camada HTTP.
/// </summary>
public sealed record ReviewApprovalDecision(
    ReviewApprovalAction Action,
    string? CanonicalChannelKey = null,
    string? Alias = null,
    string? Reason = null,
    ReviewChannelSpec? Channel = null);

/// <summary>
/// Resultado de <see cref="CatalogResolver.ApplyReviewApprovalAsync"/>:
/// o item resolvido, o canal/alias resultante e a indicação de
/// idempotência (sem alteração de catálogo numa re-aprovação).
///
/// <para>
/// W-REVIEW-02 — adicionada propriedade opcional
/// <see cref="MaterializedChannelSource"/> com default <c>null</c>
/// (estritamente aditiva; callers existentes não-break). Quando não-nula,
/// indica que a materialização Review→ChannelSource correu (create ou update).
/// Quando <c>null</c>, a materialização foi saltada (gate falhou) ou não
/// aplicável (Exclude).
/// </para>
/// </summary>
public sealed record ReviewApprovalResult(
    ReviewItemEntity Review,
    ReviewItemState PriorState,
    long? PriorApprovedCanonicalChannelId,
    CanonicalChannelEntity? Channel,
    ChannelAliasEntity? Alias,
    bool Idempotent,
    bool CatalogueChanged,
    string Action,
    ChannelSourceEntity? MaterializedChannelSource = null);
