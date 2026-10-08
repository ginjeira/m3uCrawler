using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace m3uCrawler.Services.Configuration;

/// <summary>
/// DC-9 (decisão DC-D2) — Overrides de discovery associados a um job
/// agendado. Persistido como JSON (camelCase) na coluna
/// <c>scheduled_jobs.DiscoveryJson</c>.
///
/// <para>
/// Semântica: cada campo é opcional; <c>null</c> (ou ausente no JSON)
/// herda o valor da configuração global resolvida por
/// <see cref="DiscoverySettingsProvider"/>. A aplicação é feita por
/// <see cref="DiscoverySettings.WithOverrides(string?, int?, int?, int?)"/>
/// com precedência <c>override &gt; global</c>.
/// </para>
/// </summary>
public sealed record DiscoveryOverrides(
    string? Keyword = null,
    int? MinHistoryHours = null,
    int? HistoryHours = null,
    int? MaxStreams = null)
{
    /// <summary>
    /// Serialização estável: camelCase, nulos omitidos (campos ausentes no
    /// JSON herdam a global) e tolerante a maiúsculas na leitura.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Nenhum override definido (todos os campos ausentes/vazios).</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Keyword)
        && MinHistoryHours is null
        && HistoryHours is null
        && MaxStreams is null;

    /// <summary>
    /// Aplica estes overrides sobre <paramref name="baseSettings"/>
    /// (normalmente a configuração global). Devolve uma nova instância;
    /// não muta <paramref name="baseSettings"/>.
    /// </summary>
    public DiscoverySettings ApplyTo(DiscoverySettings baseSettings)
    {
        ArgumentNullException.ThrowIfNull(baseSettings);
        return baseSettings.WithOverrides(Keyword, HistoryHours, MaxStreams, MinHistoryHours);
    }

    /// <summary>Serializa para JSON estável (camelCase, sem nulos).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>
    /// Desserializa de JSON de forma tolerante a falhas. Devolve
    /// <c>null</c> quando <paramref name="json"/> é vazio/whitespace, vazio
    /// (<c>{}</c>) ou inválido — um job corrompido herda a global em vez de
    /// rebentar o scheduler.
    /// </summary>
    public static DiscoveryOverrides? TryParse(string? json)
        => TryParse(json, out _);

    /// <summary>
    /// Mesma semântica de <see cref="TryParse(string)"/>, mas distingue a
    /// causa do <c>null</c> através de <paramref name="invalidJson"/>: é
    /// <c>true</c> quando o JSON não é vazio mas está sintaticamente
    /// inválido. Para JSON vazio/whitespace, <c>{}</c> ou sem overrides,
    /// <paramref name="invalidJson"/> fica <c>false</c> (a ausência de
    /// overrides não é um erro).
    /// </summary>
    public static DiscoveryOverrides? TryParse(string? json, out bool invalidJson)
    {
        invalidJson = false;
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<DiscoveryOverrides>(json, SerializerOptions);
            return parsed is null || parsed.IsEmpty ? null : parsed;
        }
        catch (JsonException)
        {
            invalidJson = true;
            return null;
        }
    }
}
