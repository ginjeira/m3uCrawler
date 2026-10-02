using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using m3uCrawler.Services.LiveRun;
using m3uCrawler.Services.Matching;

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
///
/// <para>
/// <b>W-DEDUP (2026-10-01).</b> A deduplicacao de validacao fisica e' feita
/// por um <see cref="ValidationKeyRegistry"/> de escopo por run. Uma chave
/// <c>sfp1</c> ja conhecida Working neste run nao volta a ser testada por
/// outra AccountKey; contudo cada AccountKey elegivel executa sempre pelo
/// menos um GET fisico (probe). As contas nunca sao fundidas.
/// </para>
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
    private readonly ValidationKeyRegistry _registry;

    // PHASE W-DASHBOARD — sink opcional de Live Run. A dedup física
    // (ReusedKnownWorking/probe físico por account) passa a ser visível no
    // feed sem alterar a sua lógica. null (default) preserva o
    // comportamento anterior (sem side-effects).
    private readonly ILiveRunProgress? _progress;

    public AccountValidator(
        StreamValidationState state,
        M3uTesterService tester,
        ValidationKeyRegistry? registry = null,
        ILiveRunProgress? progress = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _tester = tester ?? throw new ArgumentNullException(nameof(tester));
        _options = state.Options;
        _registry = registry ?? new ValidationKeyRegistry();
        _progress = progress;
    }

    /// <summary>Registo de deduplicacao fisica por run.</summary>
    public ValidationKeyRegistry Registry => _registry;

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
        int working = 0, failed = 0, shortCircuited = 0, tested = 0, reused = 0;

        var accountMetrics = new StreamValidationMetrics
        {
            TotalUrls = work.Streams.Count,
        };

        // ValidationKeys posicionais (uma por stream, parse/input order).
        // Chaves nulas = URL nao fingerprintavel: nunca deduplicado nem
        // registado, testado sempre fisicamente.
        var keys = new string?[work.Streams.Count];
        for (var i = 0; i < work.Streams.Count; i++)
        {
            keys[i] = StreamFingerprint.TryComputeFingerprint(work.Streams[i].Url);
        }

        // Probe obrigatorio desta account: escolhido sobre a composicao
        // COMPLETA da account e nunca satisfeito pelo resultado de outra
        // account. Garante pelo menos um GET fisico por AccountKey elegivel.
        var probeIndex = SelectMandatoryProbeIndex(keys);

        // hostCacheStatus e' LOCAL por account; cache e host tracker
        // sao partilhados via state.
        var hostCacheStatus = new ConcurrentDictionary<string, bool>(
            StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < work.Streams.Count; i++)
        {
            var stream = work.Streams[i];
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

            var key = keys[i];

            // Deducao: so' a chave do probe e' forcada a fisico; as
            // restantes aproveitam conhecimento Working desta run. Falhas
            // nunca dispensam um GET fisico.
            if (i != probeIndex && key is not null && _registry.IsKnownWorking(key))
            {
                outcomes.Add(new StreamTestOutcome(stream.Url, true, null, StreamFailureKind.None, 0, false, 0, false)
                    with { ReusedKnownWorking = true });
                reused++;
                working++;
                _registry.RecordReuse();
                continue;
            }

            var outcome = await _tester.TestStreamForAccountAsync(
                stream.Url,
                hostCacheStatus,
                accountMetrics,
                cancellationToken).ConfigureAwait(false);

            outcomes.Add(outcome);
            tested++;
            _registry.RecordPhysical();

            if (outcome.IsWorking) working++;
            else if (outcome.WasShortCircuited) shortCircuited++;
            else failed++;

            // Nunca registar chaves nulas nem curto-circuitados/empty como
            // Working. Uma falha nunca e' conhecimento reutilizavel.
            if (key is not null && !outcome.WasShortCircuited)
            {
                if (outcome.IsWorking) _registry.MarkWorking(key);
                else _registry.MarkFailed(key, outcome.FailureKind);
            }
        }

        sw.Stop();

        // Logs por account. Host:port + fingerprint sanitizado. Username
        // truncado (primeiros 2 chars). Password NUNCA aparece.
        var host = ExtractHost(work.PlaylistUrl);
        var logUser = SafePartialUsername(work.Username);
        Console.WriteLine(
            $"[VALIDATION_ACCOUNT] XTREAM_ACCOUNT host={host} user={logUser} " +
            $"fingerprint={work.AccountId} tested={tested} working={working} " +
            $"failed={failed} shortCircuited={shortCircuited} reused={reused} " +
            $"probe={probeIndex} durationMs={sw.ElapsedMilliseconds}");

        // PHASE W-DASHBOARD — expõe a dedup física no feed do Live Run.
        // Apenas apresentação: username mascarado (nunca a password) e
        // contadores. A lógica de decisão (ReusedKnownWorking, probe
        // obrigatório, ordem) não é tocada.
        if (_progress is not null)
        {
            _progress.ReportActivity(
                LiveRunActivityCategory.Stream,
                LiveRunActivityLevel.Info,
                $"account validation: {tested} physical, {reused} reused, {failed} failed",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["account"] = logUser,
                    ["probe"] = probeIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["physical"] = tested.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["reused"] = reused.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["failed"] = failed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        return new AccountValidationResult(
            work,
            outcomes,
            working,
            failed,
            shortCircuited,
            tested,
            reused);
    }

    /// <summary>
    /// Escolhe o indice do stream que a account testa OBRIGATORIAMENTE de
    /// forma fisica. A escolha considera a composicao COMPLETA da account e
    /// e' deterministica (parse/input order):
    /// <list type="number">
    ///   <item>primeiro stream cuja ValidationKey ja e' conhecida Working
    ///         neste run (probe barato/rapido que confirma as credenciais
    ///         desta account);</item>
    ///   <item>caso contrario, primeiro stream com ValidationKey nao nula;</item>
    ///   <item>caso contrario, primeiro stream elegivel.</item>
    /// </list>
    /// Devolve -1 quando nao ha streams. O probe e' decidido apenas sobre a
    /// composicao desta account; nunca e' satisfeito pelo resultado de outra
    /// AccountKey.
    /// </summary>
    private int SelectMandatoryProbeIndex(string?[] keys)
    {
        if (keys.Length == 0) return -1;

        // (1) primeiro stream cuja ValidationKey ja' e' conhecida Working
        // neste run.
        for (var i = 0; i < keys.Length; i++)
        {
            if (keys[i] is not null && _registry.IsKnownWorking(keys[i])) return i;
        }

        // (2) primeiro stream com ValidationKey nao nula.
        for (var i = 0; i < keys.Length; i++)
        {
            if (keys[i] is not null) return i;
        }

        // (3) primeiro stream elegivel.
        return 0;
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
