using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.SourceOrdering;
using m3uCrawler.Services.Sync;

namespace m3uCrawler.Services.Automation;

/// <summary>
/// PHASE 12 — Acção agendada que sincroniza a playlist funcional
/// (<c>output/playlist.m3u</c>) com Dispatcharr, reutilizando o
/// <see cref="DispatcharrSyncService"/> existente.
///
/// <para>
/// Nome de action (estável): <c>syncDispatcharr</c>.
/// </para>
///
/// <para>
/// Quando <c>dispatcharr_enabled=false</c> em <c>wtelegram.config</c>,
/// a acção termina precocemente com <c>dispatcharr-disabled</c> — não
/// escreve nada em HTTP nem em ficheiros extra. Esta decisão é
/// tomada aqui deliberadamente (em vez de delegar no sync service
/// para devolver DryRun) para que o resultado registado no
/// <c>LastResult</c> reflicta fielmente porque é que nada aconteceu.
/// </para>
/// </summary>
public sealed class ScheduledDispatcharrSyncAction : IScheduledAction
{
    public const string ActionName = "syncDispatcharr";

    private readonly DispatcharrConfig _config;
    private readonly string _outputDir;

    public ScheduledDispatcharrSyncAction(DispatcharrConfig config, ScheduledActionOptions options)
    {
        _config = config;
        _outputDir = options.OutputDir;
    }

    public string Name => ActionName;

    public async Task<string> ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!_config.Enabled)
        {
            return "dispatcharr-disabled";
        }

        var playlistPath = Path.Combine(_outputDir, "playlist.m3u");
        if (!File.Exists(playlistPath))
        {
            return "no-playlist";
        }

        var aliases = AliasResolver.FromFile(_config.AliasFile);
        var ordering = new StreamOrderingPolicy(_config.ProviderPriority);
        var matcher = new ChannelMatcher(aliases);
        var sync = new DispatcharrSyncService(
            _config, _outputDir,
            aliases: aliases,
            ordering: ordering,
            matcher: matcher);
        var result = await sync.RunAsync(playlistPath, cancellationToken);
        var counts = result.Report?.Counts;
        return $"newChannels={counts?.NewChannels ?? 0} newStreams={counts?.NewStreams ?? 0} matched={counts?.Matched ?? 0} ambiguous={counts?.Ambiguous ?? 0} dryRun={result.DryRun}";
    }
}
