namespace m3uCrawler.Services.Recognition;

/// <summary>
/// W5.2 — Resultado canónico do estágio de Recognition (P6,
/// <c>34-PIPELINE-CONTRACTS.md</c>; <c>05-CATALOGUE.md §4/§4.1</c>).
///
/// <para>
/// <see cref="Canonical"/> — identidade resolvida de forma inequívoca;
/// <see cref="Unknown"/> — sem evidência suficiente;
/// <see cref="Ambiguous"/> — evidência suficiente mas conflituosa
/// (qualificado por Stage; aqui <c>Stage=Recognition</c>);
/// <see cref="Excluded"/> — regra determinística de exclusão;
/// <see cref="Review"/> — <c>IdentityRule</c> explícita que não exclui.
/// </para>
///
/// <para>
/// <c>Excluded ≠ Unknown</c> e <c>Excluded ≠ Ambiguous</c>. <c>Rejected</c>
/// não é um resultado de Recognition (é do country gate, a montante).
/// </para>
/// </summary>
public enum RecognitionOutcome
{
    Canonical = 0,
    Unknown = 1,
    Ambiguous = 2,
    Excluded = 3,
    Review = 4,
}
