using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using m3uCrawler.Services;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Validation;

namespace m3uCrawler.Services.Automation;

/// <summary>
/// PHASE 12 — Acção agendada que re-testa todas as streams
/// actualmente em <c>output/playlist.m3u</c> e remove as que
/// falham, usando o <see cref="M3uTesterService"/> existente.
///
/// <para>
/// Nome de action (estável): <c>validatePlaylist</c>.
/// </para>
///
/// <para>
/// Mantém o invariante da aplicação:
/// <c>MergeStreams(stillWorkingMain, [])</c> devolve
/// <c>stillWorkingMain</c> — ou seja, um job de validação nunca
/// apaga tudo se a playlist estiver vazia.
/// </para>
/// </summary>
public sealed class ScheduledValidationAction : IScheduledAction
{
    public const string ActionName = "validatePlaylist";

    private readonly M3uTesterService _tester;
    private readonly PlaylistManagerService _playlistReader;
    private readonly ScheduledActionOptions _options;
    private readonly StreamValidationState? _validationState;

    public ScheduledValidationAction(
        M3uTesterService tester,
        PlaylistManagerService playlistReader,
        ScheduledActionOptions options,
        StreamValidationState? validationState = null)
    {
        _tester = tester;
        _playlistReader = playlistReader;
        _options = options;
        _validationState = validationState;
    }

    public string Name => ActionName;

    public string Description =>
        "Re-testa todas as streams de <output-dir>/playlist.m3u e reescreve o ficheiro mantendo apenas as que respondem. Se a playlist estiver ausente ou vazia, não a esvazia. Faz pedidos HTTP externos (probes).";

    /// <summary>
    /// Re-testa a playlist funcional; não depende do Telegram.
    /// </summary>
    public ScheduledActionCapabilities RequiredCapabilities =>
        ScheduledActionCapabilities.Output;

    public async Task<string> ExecuteAsync(CancellationToken cancellationToken)
    {
        // Wave C — Recarregar a policy de validação a cada execução. O
        // tester injectado captura as options no construtor, por isso
        // recriamos um tester a partir do state já recarregado. Sem state
        // (testes/legado) mantém-se o tester injectado.
        var tester = _tester;
        if (_validationState is not null)
        {
            _validationState.ReloadPolicy();
            tester = StreamValidationTesterFactory.CreateTester(_validationState);
        }

        var path = Path.Combine(_options.OutputDir, "playlist.m3u");
        if (!File.Exists(path))
        {
            return "no-playlist";
        }

        var existing = await _playlistReader.LoadFromM3uPlaylist(path);
        if (existing.Count == 0)
        {
            return "empty-playlist";
        }

        var retested = new List<m3uCrawler.Models.M3uStream>(existing.Count);
        foreach (var stream in existing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var t = await tester.TestM3u8Stream(stream.Url, stream.Title, stream.Group);
            retested.Add(t);
        }

        var working = retested.FindAll(s => s.IsWorking);
        await _playlistReader.SaveToM3uPlaylist(working, path);

        return $"tested={existing.Count} working={working.Count}";
    }
}
