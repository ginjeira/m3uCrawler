using m3uCrawler.Services.Catalog;

namespace m3uCrawler.Services.SourceSelection;

/// <summary>
/// PHASE 13 (Wave 13-1) — Identidade normalizada de fornecedor usada
/// apenas para diversidade/deduplicação de fornecedor na selecção de
/// fontes. Não faz descoberta de fornecedor nem I/O: assume que o
/// chamador já tem uma representação de fornecedor (host, conta,
/// origem) e limita-se a normalizá-la.
///
/// <para>
/// Todos os valores ausentes/desconhecidos colapsam numa <b>única</b>
/// identidade <see cref="Unknown"/>. Isto é deliberado: uma fonte
/// desconhecida não deve ganhar diversidade artificial por parecer
/// diferente de outra fonte também desconhecida.
/// </para>
///
/// <para>
/// Este tipo é uma chave de comparação interna. Não deve ser
/// apresentado ao operador como "fornecedor" sem enriquecimento
/// posterior (essa é a <c>ProviderDefinition</c> prevista na fase,
/// fora do âmbito desta wave).
/// </para>
/// </summary>
public readonly record struct ProviderIdentity(string Key)
{
    public const string UnknownKey = "<unknown>";

    /// <summary>Identidade única para fornecedor não determinável.</summary>
    public static readonly ProviderIdentity Unknown = new(UnknownKey);

    public bool IsUnknown => string.Equals(Key, UnknownKey, StringComparison.Ordinal);

    /// <summary>
    /// Normaliza uma representação textual de fornecedor. Tokens
    /// vazios ou sentinelas conhecidos (<c>(unknown)</c>, <c>unknown</c>,
    /// <c>&lt;unknown&gt;</c>) mapeiam para <see cref="Unknown"/>.
    /// </summary>
    public static ProviderIdentity Normalize(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return Unknown;

        var trimmed = provider.Trim();
        if (trimmed.Equals("(unknown)", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals(UnknownKey, StringComparison.OrdinalIgnoreCase))
        {
            return Unknown;
        }

        return new ProviderIdentity(trimmed.ToLowerInvariant());
    }

    public override string ToString() => Key;
}

/// <summary>
/// PHASE 13 (Wave 13-1) — Candidato de <c>ChannelSource</c> à selecção
/// de publicação. Transporta o mínimo de dados necessários para o
/// ranking/diversidade, reutilizando os enums de domínio existentes
/// (<see cref="StreamQuality"/>, <see cref="EpgState"/>,
/// <see cref="AvailabilityState"/>) em vez de os duplicar.
///
/// <para>
/// O candidato é deliberadamente <b>independente</b> de EF/DbContext,
/// ficheiros, HTTP e Dispatcharr. O chamador projeta
/// <c>ChannelSourceEntity</c> para este record. Campos que ainda não
/// sejam realistas de popular (ex.: <see cref="Quality"/>/<see cref="Epg"/>
/// são <c>Unknown</c> no catálogo actual) são suportados pelo contrato
/// e tratados como "desconhecido" pelo ranking.
/// </para>
/// </summary>
public sealed record SelectionCandidate(
    string StreamUrl,
    long SourceId,
    int SourcePriority,
    StreamQuality Quality,
    EpgState Epg,
    AvailabilityState Availability,
    long LastResponseTimeMs,
    string? ExternalStreamId,
    ProviderIdentity Provider,
    bool IsWorking = true);

/// <summary>
/// PHASE 13 (Wave 13-1) — Política de selecção de fontes por canal.
/// Valores concretos (ex.: <c>MaxSourcesPerChannel = 10</c>) são
/// fornecidos pelo chamador; o algoritmo não contém limites hardcoded.
/// </summary>
/// <param name="MaxSourcesPerChannel">
/// Número máximo de fontes seleccionadas por canal. Nunca ultrapassado.
/// </param>
/// <param name="PreferDistinctProviders">
/// Quando <c>true</c>, a Fase A favorece um representante por fornecedor
/// antes de preencher os lugares restantes.
/// </param>
/// <param name="MaxSourcesPerProvider">
/// Limite opcional por fornecedor. <c>null</c> significa sem limite.
/// Aplica-se em ambas as fases.
/// </param>
/// <param name="AllowFallbackToSameProvider">
/// Quando <c>true</c>, a Fase B pode preencher lugares restantes com
/// fontes adicionais de fornecedores já representados (respeitando
/// <paramref name="MaxSourcesPerProvider"/>). Quando <c>false</c>, a
/// Fase B só acrescenta fornecedores ainda não representados.
/// </param>
public sealed record SourceSelectionPolicy(
    int MaxSourcesPerChannel,
    bool PreferDistinctProviders,
    int? MaxSourcesPerProvider = null,
    bool AllowFallbackToSameProvider = true);

/// <summary>Fonte seleccionada, com a ordem final e o motivo.</summary>
public sealed record SelectedSource(SelectionCandidate Candidate, int Rank, string Reason);

/// <summary>Fonte rejeitada, com o motivo da rejeição.</summary>
public sealed record RejectedSource(SelectionCandidate Candidate, string Reason);

/// <summary>
/// Resultado determinístico da selecção. <see cref="Selected"/> está na
/// ordem final (Rank = índice); <see cref="Rejected"/> cobre todos os
/// restantes candidatos com motivo observável (preview/auditoria).
/// </summary>
public sealed record SourceSelectionResult(
    IReadOnlyList<SelectedSource> Selected,
    IReadOnlyList<RejectedSource> Rejected,
    int TotalCandidates);

/// <summary>
/// Vocabulário estável dos motivos de selecção/rejeição.
/// </summary>
public static class SelectionReasons
{
    /// <summary>Selecionada na Fase A (diversidade por fornecedor).</summary>
    public const string Diversity = "diversity";

    /// <summary>Selecionada na Fase B (preenchimento de lugares restantes).</summary>
    public const string Fill = "fill";

    /// <summary>URL vazia, não absoluta ou não http/https.</summary>
    public const string InvalidUrl = "invalid-url";

    /// <summary>Candidato marcado como não funcional.</summary>
    public const string NotWorking = "not-working";

    /// <summary>Disponibilidade terminal (Dead/Unreachable).</summary>
    public const string Unavailable = "unavailable";

    /// <summary>URL equivalente (após normalização) a um candidato já considerado.</summary>
    public const string DuplicateUrl = "duplicate-url";

    /// <summary>Limite do canal (<c>MaxSourcesPerChannel</c>) já atingido.</summary>
    public const string LimitReached = "limit-reached";

    /// <summary>Limite por fornecedor (<c>MaxSourcesPerProvider</c>) atingido.</summary>
    public const string ProviderLimit = "provider-limit";

    /// <summary>Fornecedor já representado e o fallback está desactivado.</summary>
    public const string FallbackDisabled = "fallback-disabled";

    /// <summary>Elegível mas não seleccionado por outro motivo não classificado.</summary>
    public const string NotSelected = "not-selected";
}
