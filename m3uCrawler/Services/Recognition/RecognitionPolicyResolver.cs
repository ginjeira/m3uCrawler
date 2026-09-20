using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services.Catalog;

namespace m3uCrawler.Services.Recognition;

/// <summary>
/// W5.1 — Resolve a política de reconhecimento efectiva a partir do catálogo
/// persistido. Não contém lógica de reconhecimento nem dependências de
/// HTTP/Dashboard.
///
/// <para>
/// Resolução determinística: <c>channel → group → global → system/default</c>
/// (<c>38-POLICIES.md §5.1</c>; DL-103). A leitura é feita em lote (uma query
/// por execução) e devolve um <see cref="RecognitionPolicySet"/> imutável.
/// </para>
///
/// <para>
/// <b>Snapshot por Run:</b> <see cref="CreateSnapshotAsync"/> materializa a
/// política resolvida num registo persistido e imutável associado ao
/// <c>RunId</c>; criar novamente para o mesmo Run devolve o snapshot
/// existente, pelo que alterações posteriores à policy não alteram o
/// comportamento de um Run já iniciado.
/// </para>
/// </summary>
public sealed class RecognitionPolicyResolver
{
    /// <summary>Versão do resolvedor/semântica de snapshot (DL-110).</summary>
    public const string ResolverVersion = "rp1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly CatalogResolver _catalog;

    public RecognitionPolicyResolver(CatalogResolver catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>Carrega o conjunto efectivo a partir de todas as linhas persistidas.</summary>
    public async Task<RecognitionPolicySet> LoadEffectivePoliciesAsync(
        CancellationToken cancellationToken = default)
    {
        var entities = await _catalog
            .ListRecognitionPoliciesAsync(cancellationToken)
            .ConfigureAwait(false);
        return BuildSet(entities);
    }

    /// <summary>
    /// Variante estritamente read-only. Nesta wave não há criação lazy de
    /// linhas, pelo que o comportamento coincide com
    /// <see cref="LoadEffectivePoliciesAsync"/>.
    /// </summary>
    public Task<RecognitionPolicySet> LoadEffectivePoliciesReadOnlyAsync(
        CancellationToken cancellationToken = default)
        => LoadEffectivePoliciesAsync(cancellationToken);

    /// <summary>Resolve a política efectiva para um sujeito (canal/grupo).</summary>
    public async Task<RecognitionPolicy> ResolveEffectiveAsync(
        string? canonicalChannelKey,
        string? groupKey,
        CancellationToken cancellationToken = default)
    {
        var set = await LoadEffectivePoliciesAsync(cancellationToken).ConfigureAwait(false);
        return set.Resolve(canonicalChannelKey, groupKey);
    }

    /// <summary>
    /// Cria (ou devolve) o snapshot imutável da política efectiva associado ao
    /// Run. Idempotente por <c>RunId</c>: um snapshot existente nunca é
    /// alterado.
    /// </summary>
    public async Task<RecognitionPolicySnapshotEntity> CreateSnapshotAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("RunId é obrigatório.", nameof(runId));
        }

        var normalizedRunId = runId.Trim();
        var existing = await _catalog
            .GetRecognitionPolicySnapshotAsync(normalizedRunId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null) return existing;

        var entities = await _catalog
            .ListRecognitionPoliciesAsync(cancellationToken)
            .ConfigureAwait(false);
        var payload = Serialize(entities);

        return await _catalog
            .SaveRecognitionPolicySnapshotAsync(normalizedRunId, payload, ResolverVersion, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Devolve o snapshot de um Run, ou <c>null</c> se não existir.</summary>
    public Task<RecognitionPolicySnapshotEntity?> GetSnapshotAsync(
        string runId,
        CancellationToken cancellationToken = default)
        => _catalog.GetRecognitionPolicySnapshotAsync(runId, cancellationToken);

    /// <summary>
    /// W5.2 — Materializa o snapshot <b>persistido</b> de um Run num
    /// <see cref="RecognitionPolicySet"/> imutável. Devolve <c>null</c>
    /// quando o Run não tem snapshot. Não lê as políticas mutáveis: o
    /// conjunto é reconstruído exclusivamente a partir de
    /// <see cref="RecognitionPolicySnapshotEntity.PoliciesJson"/>.
    /// </summary>
    public async Task<RecognitionPolicySet?> GetSnapshotSetAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(runId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.PoliciesJson))
        {
            return null;
        }

        return DeserializeSnapshot(snapshot.PoliciesJson);
    }

    /// <summary>
    /// W5.2 — Resolve a política efectiva do snapshot persistido de um
    /// Run. Consome o snapshot (não a policy mutável) e aplica a mesma
    /// precedência <c>channel → group → global → system/default</c>.
    /// Devolve <c>null</c> se o Run não tiver snapshot.
    /// </summary>
    public async Task<RecognitionPolicy?> GetSnapshotPolicyAsync(
        string runId,
        string? canonicalChannelKey,
        string? groupKey,
        CancellationToken cancellationToken = default)
    {
        var set = await GetSnapshotSetAsync(runId, cancellationToken).ConfigureAwait(false);
        return set?.Resolve(canonicalChannelKey, groupKey);
    }

    /// <summary>
    /// Reconstrói um <see cref="RecognitionPolicySet"/> a partir do
    /// payload JSON escrito por <see cref="Serialize"/>. Não altera o
    /// formato persistido.
    /// </summary>
    private static RecognitionPolicySet DeserializeSnapshot(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        RecognitionPolicy? systemDefault = null;
        if (root.TryGetProperty("system", out var system)
            && system.ValueKind == JsonValueKind.Object)
        {
            systemDefault = ReadPolicy(system);
        }

        RecognitionPolicy? global = null;
        if (root.TryGetProperty("global", out var globalElement)
            && globalElement.ValueKind == JsonValueKind.Object)
        {
            global = ReadPolicy(globalElement);
        }

        var groups = new Dictionary<string, RecognitionPolicy>(StringComparer.Ordinal);
        ReadScoped(root, "groups", groups);

        var channels = new Dictionary<string, RecognitionPolicy>(StringComparer.Ordinal);
        ReadScoped(root, "channels", channels);

        return new RecognitionPolicySet(global, groups, channels, systemDefault);
    }

    private static void ReadScoped(
        JsonElement root,
        string propertyName,
        Dictionary<string, RecognitionPolicy> target)
    {
        if (!root.TryGetProperty(propertyName, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("key", out var keyElement)
                || keyElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var key = keyElement.GetString();
            if (string.IsNullOrEmpty(key)) continue;
            if (!item.TryGetProperty("policy", out var policyElement)
                || policyElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!target.ContainsKey(key)) target[key] = ReadPolicy(policyElement);
        }
    }

    private static RecognitionPolicy ReadPolicy(JsonElement element)
        => new(
            Enabled: ReadBool(element, "enabled", fallback: true),
            FuzzyEnabled: ReadBool(element, "fuzzyEnabled", fallback: false),
            FuzzyThreshold: ReadNullableInt(element, "fuzzyThreshold"),
            FuzzyAmbiguityMargin: ReadNullableInt(element, "fuzzyAmbiguityMargin"),
            FuzzyWeightsJson: ReadNullableString(element, "fuzzyWeights"),
            Version: ReadInt(element, "version", fallback: 1));

    private static bool ReadBool(JsonElement element, string property, bool fallback)
    {
        if (!element.TryGetProperty(property, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static int ReadInt(JsonElement element, string property, int fallback)
    {
        if (!element.TryGetProperty(property, out var value)) return fallback;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;
    }

    private static int? ReadNullableInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
    }

    private static string? ReadNullableString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static RecognitionPolicySet BuildSet(IReadOnlyList<RecognitionPolicyEntity> entities)
    {
        RecognitionPolicy? global = null;
        RecognitionPolicy? systemDefault = null;
        var groups = new Dictionary<string, RecognitionPolicy>(StringComparer.Ordinal);
        var channels = new Dictionary<string, RecognitionPolicy>(StringComparer.Ordinal);

        foreach (var entity in entities)
        {
            var policy = ToPolicy(entity);

            if (string.Equals(entity.ScopeKey, RecognitionPolicyScopes.System, StringComparison.Ordinal))
            {
                systemDefault = policy;
                continue;
            }

            if (string.Equals(entity.ScopeKey, RecognitionPolicyScopes.Global, StringComparison.Ordinal))
            {
                global = policy;
                continue;
            }

            if (RecognitionPolicyScopes.IsGroup(entity.ScopeKey))
            {
                var key = entity.GroupKey ?? RecognitionPolicyScopes.GroupKeyFromScope(entity.ScopeKey);
                if (!string.IsNullOrEmpty(key) && !groups.ContainsKey(key)) groups[key] = policy;
                continue;
            }

            if (RecognitionPolicyScopes.IsChannel(entity.ScopeKey))
            {
                var key = entity.CanonicalChannelKey ?? RecognitionPolicyScopes.ChannelKeyFromScope(entity.ScopeKey);
                if (!string.IsNullOrEmpty(key) && !channels.ContainsKey(key)) channels[key] = policy;
            }
        }

        return new RecognitionPolicySet(global, groups, channels, systemDefault);
    }

    private static RecognitionPolicy ToPolicy(RecognitionPolicyEntity entity)
        => new(
            Enabled: entity.Enabled,
            FuzzyEnabled: entity.FuzzyEnabled,
            FuzzyThreshold: entity.FuzzyThreshold,
            FuzzyAmbiguityMargin: entity.FuzzyAmbiguityMargin,
            FuzzyWeightsJson: entity.FuzzyWeightsJson,
            Version: entity.Version);

    private static string Serialize(IReadOnlyList<RecognitionPolicyEntity> entities)
    {
        static object PolicyJson(bool enabled, bool fuzzyEnabled, int? threshold, int? margin, string? weights, int version)
            => new
            {
                version,
                enabled,
                fuzzyEnabled,
                fuzzyThreshold = threshold,
                fuzzyAmbiguityMargin = margin,
                fuzzyWeights = weights,
            };

        static object EntityJson(RecognitionPolicyEntity e)
            => PolicyJson(e.Enabled, e.FuzzyEnabled, e.FuzzyThreshold, e.FuzzyAmbiguityMargin, e.FuzzyWeightsJson, e.Version);

        var systemEntity = entities
            .Where(e => e.ScopeKey == RecognitionPolicyScopes.System)
            .OrderBy(e => e.Id)
            .FirstOrDefault();

        // O snapshot regista a política EFECTIVA: quando não existe linha
        // system, é inscrito o default normativo (fuzzy opt-in desligado).
        var system = systemEntity is not null
            ? EntityJson(systemEntity)
            : PolicyJson(
                RecognitionPolicy.Default.Enabled,
                RecognitionPolicy.Default.FuzzyEnabled,
                RecognitionPolicy.Default.FuzzyThreshold,
                RecognitionPolicy.Default.FuzzyAmbiguityMargin,
                RecognitionPolicy.Default.FuzzyWeightsJson,
                RecognitionPolicy.Default.Version);

        var payload = new
        {
            resolverVersion = ResolverVersion,
            system,
            global = entities
                .Where(e => e.ScopeKey == RecognitionPolicyScopes.Global)
                .OrderBy(e => e.Id)
                .Select(EntityJson)
                .FirstOrDefault(),
            groups = entities
                .Where(e => RecognitionPolicyScopes.IsGroup(e.ScopeKey))
                .OrderBy(e => e.ScopeKey, StringComparer.Ordinal)
                .Select(e => new { key = e.GroupKey ?? RecognitionPolicyScopes.GroupKeyFromScope(e.ScopeKey), policy = EntityJson(e) })
                .ToList(),
            channels = entities
                .Where(e => RecognitionPolicyScopes.IsChannel(e.ScopeKey))
                .OrderBy(e => e.ScopeKey, StringComparer.Ordinal)
                .Select(e => new { key = e.CanonicalChannelKey ?? RecognitionPolicyScopes.ChannelKeyFromScope(e.ScopeKey), policy = EntityJson(e) })
                .ToList(),
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }
}
