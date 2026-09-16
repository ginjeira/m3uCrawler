using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace m3uCrawler.Services.Validation;

/// <summary>
/// PHASE 9A.1 (2026-09-16): validador de account/playlist com a regra
/// funcional correcta.
///
/// - Concorrencia entre accounts diferentes: configuravel
///   (<see cref="StreamValidationOptions.MaxConcurrentAccounts"/>);
///   gerida por <c>AccountBoundedWorkerPool</c> com workers pinned.
/// - Concorrencia dentro da MESMA account: SEMPRE 1 (synchronous loop,
///   hardcoded). Cada account adquiri um SemaphoreSlim(1,1) keyed
///   por <see cref="IAccountWork.AccountId"/> para serializar todos os
///   work items dessa account, sem global lock.
///
/// SEM <c>Task.Run</c> por work item. SEM <c>Task.WhenAll</c> global.
/// SEM global lock. As contas sao serializadas APENAS por identidade
/// (URL + username); contas diferentes correm em paralelo ate'
/// <c>MaxConcurrentAccounts</c>.
/// </summary>
public sealed class AccountValidator
{
    /// <summary>
    /// Hardcoded por design. NAO e' configuravel.
    /// </summary>
    public const int MaxConcurrentChannelsPerAccount = 1;

    private readonly StreamValidationState _state;
    private readonly StreamValidationOptions _options;
    private readonly M3uTesterService _tester;

    public AccountValidator(
        StreamValidationState state,
        M3uTesterService tester)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _tester = tester ?? throw new ArgumentNullException(nameof(tester));
        _options = state.Options;
    }

    /// <summary>
    /// Valida UMA account/playlist sequencialmente.
    /// Canais sao testados um de cada vez (serial dentro da account).
    /// </summary>
    public async Task<AccountValidationResult> ValidateAccountAsync(
        AccountValidationWork work,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var outcomes = new List<StreamTestOutcome>(work.Streams.Count);
        int working = 0, failed = 0, shortCircuited = 0, tested = 0;

        var accountMetrics = new StreamValidationMetrics
        {
            TotalUrls = work.Streams.Count,
        };

        // hostCacheStatus e' LOCAL por account; cache e host tracker
        // sao partilhados via state.
        var hostCacheStatus = new ConcurrentDictionary<string, bool>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var stream in work.Streams)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // Early-exit por work item.
            if (ShouldEarlyExitPerWorkItem(working))
            {
                var shortOutcome = StreamTestOutcome.Empty(stream.Url)
                    with { WasShortCircuited = true };
                outcomes.Add(shortOutcome);
                shortCircuited++;
                accountMetrics.IncrementSkipped();
                accountMetrics.IncrementEarlyExits();
                continue;
            }

            var outcome = await _tester.TestStreamForAccountAsync(
                stream.Url,
                hostCacheStatus,
                accountMetrics,
                cancellationToken).ConfigureAwait(false);

            outcomes.Add(outcome);
            tested++;

            if (outcome.IsWorking) working++;
            else if (outcome.WasShortCircuited) shortCircuited++;
            else failed++;
        }

        sw.Stop();

        // Logs por account. Host:port + fingerprint sanitizado. Username
        // truncado (primeiros 2 chars). Password NUNCA aparece.
        var host = ExtractHost(work.PlaylistUrl);
        var logUser = SafePartialUsername(work.Username);
        Console.WriteLine(
            $"[VALIDATION_ACCOUNT] XTREAM_ACCOUNT host={host} user={logUser} " +
            $"fingerprint={work.AccountId} tested={tested} working={working} " +
            $"failed={failed} shortCircuited={shortCircuited} durationMs={sw.ElapsedMilliseconds}");

        return new AccountValidationResult(
            work,
            outcomes,
            working,
            failed,
            shortCircuited,
            tested);
    }

    /// <summary>
    /// Entry unificado para todos os caminhos de validacao de streams.
    /// </summary>
    public async Task<IReadOnlyList<AccountValidationResult>> ValidateAccountsAsync(
        IReadOnlyList<AccountValidationWork> works,
        int? maxConcurrentAccountsOverride,
        CancellationToken cancellationToken)
    {
        if (works.Count == 0) return Array.Empty<AccountValidationResult>();

        var workerCount = Math.Max(1,
            maxConcurrentAccountsOverride.HasValue && maxConcurrentAccountsOverride.Value > 0
                ? maxConcurrentAccountsOverride.Value
                : _options.MaxConcurrentAccounts);

        var channel = Channel.CreateUnbounded<AccountValidationWork>(
            new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = true,
            });

        foreach (var w in works)
        {
            await channel.Writer.WriteAsync(w, cancellationToken).ConfigureAwait(false);
        }
        channel.Writer.TryComplete();

        var pool = new AccountBoundedWorkerPool<AccountValidationWork, AccountValidationResult>(
            workerCount,
            channel,
            (work, ct) => new ValueTask<AccountValidationResult>(ValidateAccountAsync(work, ct)));

        return await pool.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool ShouldEarlyExitPerWorkItem(int validCount)
    {
        return _options.EarlyExit switch
        {
            EarlyExitPolicy.StopAfterFirstValid => validCount > 0,
            EarlyExitPolicy.StopAfterNValid => _options.EarlyExitThreshold > 0
                                              && validCount >= _options.EarlyExitThreshold,
            _ => false,
        };
    }

    private static string ExtractHost(string url)
    {
        if (string.IsNullOrEmpty(url)) return "?";
        try { return new Uri(url).Host + ":" + new Uri(url).Port; }
        catch { return "?"; }
    }

    /// <summary>
    /// Mostramos apenas primeiros 2 chars do username + reticencias para
    /// distinguir contas sem expor o username completo.
    /// </summary>
    private static string SafePartialUsername(string? username)
    {
        if (string.IsNullOrEmpty(username)) return "-";
        if (username.Length <= 2) return username[0] + "\u2026";
        return username[..2] + "\u2026";
    }
}
