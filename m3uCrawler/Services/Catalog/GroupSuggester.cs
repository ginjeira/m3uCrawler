using m3uCrawler.Services.Matching;

namespace m3uCrawler.Services.Catalog;

/// <summary>
/// Sugere a <c>Key</c> do grupo canónico de publicação a partir do
/// <c>group-title</c> da source e do título do canal.
///
/// <para>
/// A sugestão é <b>apenas pré-selecção</b> do formulário de canal: o valor
/// persistido é sempre a escolha explícita do operador (<c>groupKey</c>
/// submetido). O <c>group-title</c> da source nunca é autoridade sobre o
/// grupo do canal canónico (Wave D3).
/// </para>
///
/// <para>
/// Lógica pura/estática, sem acesso a base de dados. Primeiro tenta o mapa
/// curado <see cref="GroupTaxonomy"/> sobre o grupo normalizado; se não
/// houver correspondência, cai para heurísticas de tokens
/// (<c>desporto</c>, <c>sport</c>, …) sobre o grupo + título normalizados.
/// Sem sugestão devolve <c>null</c>.
/// </para>
/// </summary>
public static class GroupSuggester
{
    /// <summary>
    /// Devolve a <c>Key</c> estável (<see cref="CanonicalGroupKeys"/>) do
    /// grupo sugerido, ou <c>null</c> quando não há sugestão.
    /// </summary>
    public static string? SuggestGroupKey(string? sourceGroup, string? title)
    {
        var normalizedGroup = GroupNormalizer.Normalize(sourceGroup);
        var (kind, _) = GroupTaxonomy.Lookup(normalizedGroup);
        if (kind.HasValue)
        {
            return MapKind(kind.Value);
        }

        var haystack = (normalizedGroup + " " + GroupNormalizer.Normalize(title)).Trim();
        if (haystack.Length == 0)
        {
            return null;
        }

        if (Contains(haystack, "desporto") || Contains(haystack, "sport"))
        {
            return CanonicalGroupKeys.PortugalDesporto;
        }
        if (Contains(haystack, "infantil")
            || Contains(haystack, "kids")
            || Contains(haystack, "crianças"))
        {
            return CanonicalGroupKeys.PortugalInfantil;
        }
        if (Contains(haystack, "documentari")
            || Contains(haystack, "doc")
            || Contains(haystack, "history"))
        {
            return CanonicalGroupKeys.PortugalDocumentarios;
        }
        if (Contains(haystack, "filmes")
            || Contains(haystack, "séries")
            || Contains(haystack, "series")
            || Contains(haystack, "vod"))
        {
            return CanonicalGroupKeys.PortugalFilmesSeries;
        }
        if (Contains(haystack, "noticias")
            || Contains(haystack, "notícias")
            || Contains(haystack, "news")
            || Contains(haystack, "general")
            || Contains(haystack, "generalistas"))
        {
            return CanonicalGroupKeys.PortugalGeneralistas;
        }

        return null;
    }

    private static bool Contains(string haystack, string token)
        => haystack.IndexOf(token, System.StringComparison.Ordinal) >= 0;

    private static string MapKind(OutputGroupKind kind) => kind switch
    {
        OutputGroupKind.PortugalLive => CanonicalGroupKeys.PortugalGeneralistas,
        OutputGroupKind.PortugalVOD => CanonicalGroupKeys.PortugalFilmesSeries,
        OutputGroupKind.PortugalFilmes24_7 => CanonicalGroupKeys.PortugalFilmesSeries,
        OutputGroupKind.PortugalEntretenimento => CanonicalGroupKeys.PortugalEntretenimento,
        OutputGroupKind.PortugalDesporto => CanonicalGroupKeys.PortugalDesporto,
        OutputGroupKind.PortugalInfantil => CanonicalGroupKeys.PortugalInfantil,
        OutputGroupKind.PortugalDocumentarios => CanonicalGroupKeys.PortugalDocumentarios,
        OutputGroupKind.PortugalPPV => CanonicalGroupKeys.PortugalPPV,
        OutputGroupKind.Foreign => CanonicalGroupKeys.International,
        _ => CanonicalGroupKeys.Other,
    };
}
