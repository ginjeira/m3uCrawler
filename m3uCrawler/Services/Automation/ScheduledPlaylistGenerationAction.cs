using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services.Catalog;

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
/// (<see cref="ScheduledJobEntity.Name"/>) no formato
/// <c>generatePlaylist:&lt;id&gt;</c>. Quando o sufixo não é um id válido
/// ou o id não existe, a acção usa deterministicamente a primeira
/// ordering list (ordenada por nome) e regista o fallback no
/// <c>LastResult</c>.
/// </para>
/// </summary>
public sealed class ScheduledPlaylistGenerationAction : IScheduledAction, IJobAwareScheduledAction
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

    public string Description =>
        "Compõe <output-dir>/playlist.m3u a partir de uma OrderingList do catálogo canónico. O nome do job 'generatePlaylist:<id>' seleciona a lista; sem id válido usa a primeira lista, e um id válido inexistente regista fallback para a primeira lista. Não faz pedidos externos.";

    /// <summary>
    /// Composição usa o catálogo canónico e escreve o output.
    /// </summary>
    public ScheduledActionCapabilities RequiredCapabilities =>
        ScheduledActionCapabilities.Catalog | ScheduledActionCapabilities.Output;

    /// <summary>Execução sem contexto de job (testes/legado): primeira lista.</summary>
    public Task<string> ExecuteAsync(CancellationToken cancellationToken)
        => ExecuteCoreAsync(null, cancellationToken);

    public Task<string> ExecuteAsync(
        ScheduledJobContext job, CancellationToken cancellationToken)
        => ExecuteCoreAsync(job, cancellationToken);

    private async Task<string> ExecuteCoreAsync(
        ScheduledJobContext? job, CancellationToken cancellationToken)
    {
        var lists = await _catalog.ListOrderingListsAsync(cancellationToken).ConfigureAwait(false);
        if (lists.Count == 0)
        {
            throw new InvalidOperationException("Nenhuma OrderingList disponível para gerar playlist.");
        }

        var (orderingListId, fallbackNote) = ResolveOrderingListId(job, lists);
        var composition = await _composer
            .ComposeAsync(orderingListId, null, cancellationToken)
            .ConfigureAwait(false);

        var path = System.IO.Path.Combine(_outputDir, "playlist.m3u");
        await _playlistWriter.WriteComposedAsync(composition, path).ConfigureAwait(false);

        var summary = $"composed:{composition.Entries.Count} entries, missing={composition.MissingChannels.Count}";
        return fallbackNote is null ? summary : $"{fallbackNote} {summary}";
    }

    /// <summary>
    /// Resolve o id da ordering list a partir do nome do job. O sufixo
    /// numérico após o último <c>:</c> é o id documentado. Sem sufixo
    /// válido, devolve a primeira lista (ordenação estável por nome).
    /// </summary>
    internal static (long Id, string? FallbackNote) ResolveOrderingListId(
        ScheduledJobContext? job, IReadOnlyList<OrderingListEntity> lists)
    {
        var parsedId = TryParseOrderingListId(job?.Name);
        if (parsedId.HasValue)
        {
            if (lists.Any(l => l.Id == parsedId.Value))
            {
                return (parsedId.Value, null);
            }
            return (lists[0].Id, $"fallback:ordering-list-{parsedId.Value}-not-found");
        }

        return (lists[0].Id, null);
    }

    /// <summary>
    /// Extrai o <c>&lt;id&gt;</c> de um nome de job
    /// <c>generatePlaylist:&lt;id&gt;</c>. Aceita o separador
    /// <c>:</c>. Devolve <c>null</c> quando não há sufixo numérico positivo.
    /// </summary>
    internal static long? TryParseOrderingListId(string? jobName)
    {
        if (string.IsNullOrWhiteSpace(jobName))
        {
            return null;
        }

        var colon = jobName.LastIndexOf(':');
        if (colon < 0 || colon == jobName.Length - 1)
        {
            return null;
        }

        var suffix = jobName[(colon + 1)..].Trim();
        if (!long.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || id <= 0)
        {
            return null;
        }

        return id;
    }
}
