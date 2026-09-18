using System;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services.Catalog;

namespace m3uCrawler.Services.SourceSelection;

/// <summary>
/// PHASE 13 (Wave 13-4) — Resolve a política global de selecção de fontes
/// a partir do catálogo persistido. Não contém lógica de selecção nem
/// qualquer dependência de HTTP/Dashboard; a resolução por canal
/// (<c>channel:{key}</c>) fica reservada para 13-4b.
///
/// <para>
/// O catálogo é a fonte de verdade: a linha global é criada lazily com os
/// defaults (<see cref="SourceSelectionDefaults.DefaultPolicy"/>) se ainda
/// não existir. Se o catálogo devolver inesperadamente <c>null</c>, a
/// política por defeito é usada como fallback seguro.
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

        if (entity is null)
        {
            return SourceSelectionDefaults.DefaultPolicy;
        }

        return new SourceSelectionPolicy(
            MaxSourcesPerChannel: entity.MaxSourcesPerChannel,
            PreferDistinctProviders: entity.PreferDistinctProviders,
            MaxSourcesPerProvider: entity.MaxSourcesPerProvider,
            AllowFallbackToSameProvider: entity.AllowFallbackToSameProvider);
    }
}
