using System;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Validation;

namespace m3uCrawler.Services.Automation;

/// <summary>
/// PHASE 12 — Acção agendada que executa o ciclo de discovery
/// (pesquisa de M3U8) seguido de validação de streams, gravando a
/// playlist funcional resultante em <c>output/playlist.m3u</c>.
///
/// <para>
/// Nome de action (estável): <c>discoverM3u</c>.
/// </para>
///
/// <para>
/// A descoberta é feita via <see cref="M3uCrawlerService.SearchM3u8Files"/>
/// e a validação via <see cref="M3uTesterService.TestMultipleStreams"/>,
/// ambos serviços existentes. Esta acção NÃO duplica o pipeline:
/// apenas orquestra os serviços já presentes na aplicação.
/// </para>
///
/// <para>
/// Wave W2 — <b>capacidade distinta e deliberada.</b> Esta é a
/// descoberta M3U por pesquisa web (não-Telegram). Publica a playlist
/// funcional <c>output/playlist.m3u</c> e não participa no
/// <c>RunCoordinator</c> Telegram. Declara apenas
/// <see cref="ScheduledActionCapabilities.Output"/>: pode correr quando o
/// Telegram não está autenticado, desde que o output seja gravável.
/// </para>
/// </summary>
public sealed class ScheduledM3uDiscoveryAction : IScheduledAction
{
    public const string ActionName = "discoverM3u";

    private readonly M3uCrawlerService _crawler;
    private readonly M3uTesterService _tester;
    private readonly PlaylistManagerService _playlistWriter;
    private readonly ScheduledActionOptions _options;
    private readonly StreamValidationState? _validationState;

    public ScheduledM3uDiscoveryAction(
        M3uCrawlerService crawler,
        M3uTesterService tester,
        PlaylistManagerService playlistWriter,
        ScheduledActionOptions options,
        StreamValidationState? validationState = null)
    {
        _crawler = crawler;
        _tester = tester;
        _playlistWriter = playlistWriter;
        _options = options;
        _validationState = validationState;
    }

    public string Name => ActionName;

    public string Description =>
        "Descoberta M3U8 por pesquisa web (M3uCrawlerService) seguida de validação; publica as streams funcionais em <output-dir>/playlist.m3u. O termo e o limite vêm de ScheduledActionOptions. Substitui a playlist funcional e faz pedidos HTTP externos.";

    /// <summary>
    /// Discovery M3U escreve a playlist funcional; não depende do Telegram
    /// nem do catálogo canónico.
    /// </summary>
    public ScheduledActionCapabilities RequiredCapabilities =>
        ScheduledActionCapabilities.Output;

    public async Task<string> ExecuteAsync(CancellationToken cancellationToken)
    {
        // Wave C — Recarregar a policy de validação a cada execução (o
        // tester injectado capturou as options no build do DI).
        var tester = _tester;
        if (_validationState is not null)
        {
            _validationState.ReloadPolicy();
            tester = StreamValidationTesterFactory.CreateTester(_validationState);
        }

        var term = _options.DefaultDiscoveryTerm;
        var maxStreams = _options.MaxDiscoveryStreams;

        var found = await _crawler.SearchM3u8Files(term, maxStreams);
        cancellationToken.ThrowIfCancellationRequested();

        if (found.Count == 0)
        {
            return "no-streams-found";
        }

        var tested = await tester.TestMultipleStreams(found, maxConcurrency: 10);
        cancellationToken.ThrowIfCancellationRequested();

        var working = tested.FindAll(s => s.IsWorking);
        var path = System.IO.Path.Combine(_options.OutputDir, "playlist.m3u");
        await _playlistWriter.SaveToM3uPlaylist(working, path);

        return $"found={found.Count} working={working.Count}";
    }
}
