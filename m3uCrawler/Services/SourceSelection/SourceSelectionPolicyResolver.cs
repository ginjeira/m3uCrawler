using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services.Catalog;

namespace m3uCrawler.Services.SourceSelection;

/// <summary>
/// PHASE 13 (Wave 13-4 / 13-4b) — Resolve a política efectiva de selecção de
/// fontes a partir do catálogo persistido. Não contém lógica de selecção nem
/// qualquer dependência de HTTP/Dashboard.
///
/// <para>
/// A resolução por canal (<c>channel:{key}</c>) está implementada: um
/// override por canal é uma política <b>completa</b> que substitui a global
/// por inteiro quando existe. A ordem de resolução é
/// <c>override por canal → global → defaults</c>. Orphans (overrides para
/// canais inexistentes) são inertes: nunca são resolvidos.
/// </para>
///
/// <para>
/// O catálogo é a fonte de verdade: a linha global é criada lazily com os
/// defaults (<see cref="SourceSelectionDefaults.DefaultPolicy"/>) se ainda
/// não existir. Se o catálogo devolver inesperadamente <c>null</c>, a
/// política por defeito é usada como fallback seguro.
/// </para>
///
/// <para>
/// <b>Leitura em lote:</b> <see cref="LoadEffectivePoliciesAsync"/> carrega a
/// global e todos os overrides com apenas duas queries por execução (sem
/// N+1) e devolve um snapshot <see cref="SourceSelectionPolicySet"/> válido
/// para a duração do run. Não há cache persistente entre execuções.
/// </para>
/// </summary>
public sealed class SourceSelectionPolicyResolver
{
    private readonly CatalogResolver _catalog;

    public SourceSelectionPolicyResolver(CatalogResolver catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public async Task<SourceSelectionPolicy> ResolveGlobalAsync(
        CancellationToken cancellationToken = default)
    {
        var entity = await _catalog
            .GetOrCreateGlobalSourceSelectionPolicyAsync(cancellationToken)
            .ConfigureAwait(false);

        return entity is null ? SourceSelectionDefaults.DefaultPolicy : ToPolicy(entity);
    }

    /// <summary>
    /// Resolve a política efectiva para o canal indicado: override por canal
    /// quando existe, caso contrário a global (criada lazily se necessário).
    /// Uma chave nula/vazia resolve directamente para a global.
    /// </summary>
    public async Task<SourceSelectionPolicy> ResolveEffectiveAsync(
        string? canonicalChannelKey,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(canonicalChannelKey))
        {
            var channelEntity = await _catalog
                .GetChannelSourceSelectionPolicyAsync(canonicalChannelKey, cancellationToken)
                .ConfigureAwait(false);
            if (channelEntity is not null)
            {
                return ToPolicy(channelEntity);
            }
        }

        return await ResolveGlobalAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Carrega num único snapshot a política global e todos os overrides por
    /// canal. Duas queries por execução: uma para os overrides
    /// (<c>ListChannelSourceSelectionPoliciesAsync</c>) e uma para a global
    /// (<c>GetOrCreateGlobalSourceSelectionPolicyAsync</c>).
    /// </summary>
    public async Task<SourceSelectionPolicySet> LoadEffectivePoliciesAsync(
        CancellationToken cancellationToken = default)
    {
        var channelEntities = await _catalog
            .ListChannelSourceSelectionPoliciesAsync(cancellationToken)
            .ConfigureAwait(false);

        var overrides = new Dictionary<string, SourceSelectionPolicy>(StringComparer.Ordinal);
        foreach (var entity in channelEntities)
        {
            var key = entity.CanonicalChannelKey;
            if (string.IsNullOrEmpty(key)) continue;
            // Guarda defensiva contra duplicados: mantém o primeiro.
            if (overrides.ContainsKey(key)) continue;
            overrides[key] = ToPolicy(entity);
        }

        var globalEntity = await _catalog
            .GetOrCreateGlobalSourceSelectionPolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        var globalPolicy = globalEntity is null
            ? SourceSelectionDefaults.DefaultPolicy
            : ToPolicy(globalEntity);

        return new SourceSelectionPolicySet(globalPolicy, overrides);
    }

    private static SourceSelectionPolicy ToPolicy(SourceSelectionPolicyEntity entity)
        => new(
            MaxSourcesPerChannel: entity.MaxSourcesPerChannel,
            PreferDistinctProviders: entity.PreferDistinctProviders,
            MaxSourcesPerProvider: entity.MaxSourcesPerProvider,
            AllowFallbackToSameProvider: entity.AllowFallbackToSameProvider);
}
