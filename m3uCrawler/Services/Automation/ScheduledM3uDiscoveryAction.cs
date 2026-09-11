using System;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;

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
/// </summary>
public sealed class ScheduledM3uDiscoveryAction : IScheduledAction
{
    public const string ActionName = "discoverM3u";

    private readonly M3uCrawlerService _crawler;
    private readonly M3uTesterService _tester;
    private readonly PlaylistManagerService _playlistWriter;
    private readonly ScheduledActionOptions _options;

    public ScheduledM3uDiscoveryAction(
        M3uCrawlerService crawler,
        M3uTesterService tester,
        PlaylistManagerService playlistWriter,
        ScheduledActionOptions options)
    {
        _crawler = crawler;
        _tester = tester;
        _playlistWriter = playlistWriter;
        _options = options;
    }

    public string Name => ActionName;

    public async Task<string> ExecuteAsync(CancellationToken cancellationToken)
    {
        var term = _options.DefaultDiscoveryTerm;
        var maxStreams = _options.MaxDiscoveryStreams;

        var found = await _crawler.SearchM3u8Files(term, maxStreams);
        cancellationToken.ThrowIfCancellationRequested();

        if (found.Count == 0)
        {
            return "no-streams-found";
        }

        var tested = await _tester.TestMultipleStreams(found, maxConcurrency: 10);
        cancellationToken.ThrowIfCancellationRequested();

        var working = tested.FindAll(s => s.IsWorking);
        var path = System.IO.Path.Combine(_options.OutputDir, "playlist.m3u");
        await _playlistWriter.SaveToM3uPlaylist(working, path);

        return $"found={found.Count} working={working.Count}";
    }
}
