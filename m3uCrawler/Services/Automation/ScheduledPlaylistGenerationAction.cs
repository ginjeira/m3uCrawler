using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace m3uCrawler.Services.Automation;

/// <summary>
/// PHASE 12 — Acção agendada que gera uma playlist M3U a partir de uma
/// <see cref="OrderingListEntity"/> usando o <see cref="PlaylistComposerService"/>
/// e persiste o resultado em <c>output/playlist.m3u</c>.
///
/// <para>
/// Nome de action (estável): <c>generatePlaylist</c>.
/// </para>
///
/// <para>
/// A ordering list alvo é seleccionada através do <c>JobName</c>
/// (<see cref="ScheduledJobEntity.Name"/>) no formato <c>generatePlaylist:&lt;id&gt;</c>.
/// Quando não é possível resolver o id, a acção tenta a primeira
/// ordering list disponível como fallback e regista esse fallback no
/// <c>LastResult</c>.
/// </para>
/// </summary>
public sealed class ScheduledPlaylistGenerationAction : IScheduledAction
{
    public const string ActionName = "generatePlaylist";

    private readonly CatalogResolver _catalog;
    private readonly PlaylistComposerService _composer;
    private readonly PlaylistManagerService _playlistWriter;
    private readonly string _outputDir;

    public ScheduledPlaylistGenerationAction(
        CatalogResolver catalog,
        PlaylistComposerService composer,
        PlaylistManagerService playlistWriter,
        ScheduledActionOptions options)
    {
        _catalog = catalog;
        _composer = composer;
        _playlistWriter = playlistWriter;
        _outputDir = options.OutputDir;
    }

    public string Name => ActionName;

    public async Task<string> ExecuteAsync(CancellationToken cancellationToken)
    {
        var orderingListId = ResolveOrderingListId();
        var composition = await _composer.ComposeAsync(orderingListId, null, cancellationToken);

        var path = System.IO.Path.Combine(_outputDir, "playlist.m3u");
        await _playlistWriter.WriteComposedAsync(composition, path);

        return $"composed:{composition.Entries.Count} entries, missing={composition.MissingChannels.Count}";
    }

    private long ResolveOrderingListId()
    {
        var lists = _catalog.ListOrderingListsAsync().GetAwaiter().GetResult();
        if (lists.Count == 0)
        {
            throw new InvalidOperationException("Nenhuma OrderingList disponível para gerar playlist.");
        }
        return lists[0].Id;
    }
}
