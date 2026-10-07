using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace m3uCrawler.Services.Catalog;

/// <summary>
/// DTOs que reflectem o schema do ficheiro
/// <c>docs/catalog/m3ucrawler_pt_canonical_catalog.json</c>.
/// Apenas os campos relevantes para a baseline do projecto são
/// materializados; restantes campos são preservados no JsonElement
/// correspondente mas não deserializados.
/// </summary>
public sealed class CatalogBaseline
{
    [JsonPropertyName("catalog_id")]
    public string CatalogId { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("country")]
    public string Country { get; set; } = string.Empty;

    [JsonPropertyName("language")]
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// Mapa de "numero": "nome do canal". Usado apenas como fonte
    /// secundária para resolver DisplayName de canais canónicos.
    /// </summary>
    [JsonPropertyName("numbering")]
    public Dictionary<string, string> Numbering { get; set; } = new();

    /// <summary>
    /// Lista de grupos editoriais persistentes. Cada grupo
    /// descreve um range de posições no numbering (e.g. 1..29 =
    /// "Portugal | Generalistas").
    /// </summary>
    [JsonPropertyName("groups")]
    public List<GroupBaseline> Groups { get; set; } = new();

    /// <summary>
    /// Definições de matching (canonical_id, name, aliases). É a
    /// fonte canónica do seed PT.
    /// </summary>
    [JsonPropertyName("matching")]
    public MatchingBaseline Matching { get; set; } = new();
}

/// <summary>
/// Grupo editorial persistente (numbering range + nome).
/// </summary>
public sealed class GroupBaseline
{
    /// <summary>
    /// Identificador estável (e.g. "pt-generalistas", "pt-desporto",
    /// "radio-pt").
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Range [start, end] inclusivo. Null quando o grupo é só
    /// virtual (e.g. vod-filmes).
    /// </summary>
    [JsonPropertyName("range")]
    public int[]? Range { get; set; }
}

/// <summary>
/// Bloco matching do catálogo baseline.
/// </summary>
public sealed class MatchingBaseline
{
    [JsonPropertyName("canonical_fields")]
    public List<string> CanonicalFields { get; set; } = new();

    [JsonPropertyName("match_priority")]
    public List<string> MatchPriority { get; set; } = new();

    /// <summary>
    /// Mapa de "key" (e.g. "rtp1") -> definição do canal canónico.
    /// A <c>canonical_id</c> é o slug estável (e.g. "pt.rtp1");
    /// o <c>name</c> é o nome editorial; <c>aliases</c> são as
    /// variações reconhecidas.
    /// </summary>
    [JsonPropertyName("examples")]
    public Dictionary<string, ChannelBaseline> Examples { get; set; } = new();
}

/// <summary>
/// Definição de um canal canónico dentro do baseline matching.
/// </summary>
public sealed class ChannelBaseline
{
    /// <summary>
    /// Identificador estável e versionado, slug-friendly.
    /// Exemplo: "pt.rtp1". Usado como <c>Key</c> no
    /// <c>CanonicalChannelEntity</c> (com prefixo country
    /// removido para evitar duplicação — "pt.rtp1" → "rtp-1").
    /// </summary>
    [JsonPropertyName("canonical_id")]
    public string CanonicalId { get; set; } = string.Empty;

    /// <summary>
    /// Nome editorial (e.g. "RTP 1"). Usado como
    /// <c>DisplayName</c>.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("aliases")]
    public List<string> Aliases { get; set; } = new();

    /// <summary>
    /// Identidades externas conhecidas do canal (ex.: <c>tvg-id</c>).
    /// Opcional: o baseline PT actual não as fornece, pelo que a
    /// tabela <c>external_identities</c> permanece vazia após o
    /// import. O mecanismo existe para baselines futuros.
    /// </summary>
    [JsonPropertyName("tvg_ids")]
    public List<string> TvgIds { get; set; } = new();

    /// <summary>
    /// Mapa namespace → valor de identidades externas adicionais
    /// (ex.: <c>"provider:meo": "rtp1"</c>). Opcional.
    /// </summary>
    [JsonPropertyName("external_ids")]
    public Dictionary<string, string> ExternalIds { get; set; } = new();
}
