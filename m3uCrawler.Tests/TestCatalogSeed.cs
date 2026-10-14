using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services.Catalog;

namespace m3uCrawler.Tests;

/// <summary>
/// Wave A — helper de teste para as listas geridas pelo utilizador.
///
/// <para>
/// O arranque da aplicação já <b>não</b> semeia canais canónicos nem
/// importa a baseline automaticamente: a provisão é explícita, por acção
/// do utilizador (<c>--import-canonical-baseline</c> /
/// <c>--import-country-config</c>). Os testes que precisam de um catálogo
/// populado aplicam-no <b>explicitamente</b> através deste helper — o
/// equivalente de teste ao comando CLI de import da baseline.
/// </para>
///
/// <para>
/// Aplica a mesma sequência que o antigo arranque aplicava: seed
/// programático, import da baseline canónica (recurso embutido) e
/// normalização de aliases legacy. Cada passo é idempotente.
/// </para>
/// </summary>
internal static class TestCatalogSeed
{
    public static async Task ApplyDefaultsAsync(
        ChannelCatalogDbContext context, CancellationToken ct = default)
    {
        await ChannelCatalogBootstrapper.SeedAsync(context, ct);
        var baseline = await CatalogBaselineImporter.LoadEmbeddedAsync(ct);
        await CatalogBaselineImporter.ImportAsync(context, baseline, ct);
        await ChannelCatalogBootstrapper.NormalizeExistingAliasesAsync(context, logger: null, ct);
    }
}
