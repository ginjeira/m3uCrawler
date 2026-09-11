using System;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services.Catalog;

namespace m3uCrawler.Services.Automation;

/// <summary>
/// Opções lidas do ambiente / config para as acções agendadas.
/// Mantém o acoplamento mínimo necessário para que cada action
/// seja determinística sem receber parâmetros via
/// <see cref="ScheduledJobEntity"/>.
/// </summary>
public sealed class ScheduledActionOptions
{
    /// <summary>
    /// Pasta de saída das playlists geradas (default: <c>output</c>).
    /// </summary>
    public string OutputDir { get; init; } = "output";

    /// <summary>
    /// Termo de pesquisa default usado por
    /// <see cref="ScheduledM3uDiscoveryAction"/> quando o job não
    /// embute um termo no nome.
    /// </summary>
    public string DefaultDiscoveryTerm { get; init; } = "iptv portugal";

    /// <summary>
    /// Limite de streams testadas por cada execução agendada de
    /// discovery. Mantém o job previsível e curto.
    /// </summary>
    public int MaxDiscoveryStreams { get; init; } = 200;
}
