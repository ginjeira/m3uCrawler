namespace m3uCrawler.Services.Configuration;

/// <summary>
/// Wave C — Ponto único de leitura/resolução dos parâmetros de discovery.
///
/// <para>
/// Lê sempre do <see cref="AppSettingsStore"/> (ficheiro
/// <c>runtime-data/app_settings.json</c>) de forma a que uma alteração
/// feita no dashboard seja observada no ciclo seguinte sem reiniciar o
/// processo. O scheduler e o executor do <c>POST /api/run/start</c>
/// resolvem por aqui, nunca a partir de um snapshot capturado no arranque.
/// </para>
/// </summary>
public sealed class DiscoverySettingsProvider
{
    private readonly AppSettingsStore _store;

    public DiscoverySettingsProvider(AppSettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Valores persistidos (já sanitizados pelo store).</summary>
    public DiscoverySettings Load() => _store.Load().Discovery;

    /// <summary>
    /// Resolve os valores efectivos para uma execução, aplicando os
    /// overrides explícitos sobre os valores persistidos.
    /// </summary>
    public DiscoverySettings Resolve(string? keyword, int? historyHours, int? maxStreams)
        => Load().WithOverrides(keyword, historyHours, maxStreams);
}
