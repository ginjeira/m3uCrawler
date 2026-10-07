using System.Diagnostics;
using System.Text.Json;
using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.LiveRun;
using m3uCrawler.Services.Matching;
using m3uCrawler.Services.Validation;
using m3uCrawler.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace m3uCrawler.Tests;

/// <summary>
/// W-DEDUP (2026-10-01) — testes de integração da deduplicação de validação
/// física por <c>sfp1</c> (ValidationKey) dentro de um run.
///
/// <para>
/// Usa um <see cref="ScriptedXtreamServer"/> em loopback (ver
/// <c>StreamValidationProdWiringTests.DelayedHttpListener</c> para o precedente).
/// <c>M3uTesterService.ProbeOnceAsync</c> resolve o HttpClient a partir do
/// <c>HttpClientFactory</c> process-wide, pelo que um handler falso nunca
/// interceptaria os probes — só um listener real observa e conta GETs físicos.
/// </para>
///
/// <para>
/// Cada método indica, no XML doc, os números de cenário obrigatórios da wave
/// (S0-S24) que cobre.
/// </para>
/// </summary>
public class ProviderChannelValidationDedupTests
{
    private readonly ITestOutputHelper _output;

    public ProviderChannelValidationDedupTests(ITestOutputHelper output) => _output = output;

    // ─────────────────────────────────────────────────────────────
    // Helpers de construção
    // ─────────────────────────────────────────────────────────────

    private static AccountValidator NewValidator(ValidationKeyRegistry? registry = null)
    {
        var state = StreamValidationTesterFactory.CreateIsolatedState();
        state.Options.ConnectionTimeoutSeconds = 2;
        state.Options.OverallTimeoutSeconds = 5;
        state.Options.MaxRetries = 0;
        // EarlyExit==TestAll e HostFailure==Off são os defaults; asseridos em
        // ProbeDefaultsAreTestAllAndHostFailureOff.
        var tester = new M3uTesterService(state, SsrfGuard.CreatePermissiveForLocalTests());
        return registry is null
            ? new AccountValidator(state, tester)
            : new AccountValidator(state, tester, registry);
    }

    private static AccountValidationWork BuildWork(
        int port, string user, string pass, IReadOnlyList<AccountStreamWork> streams)
    {
        var playlistUrl = $"http://127.0.0.1:{port}/get.php?username={user}&password={pass}&type=m3u_plus";
        var username = AccountIdentity.ExtractUsername(playlistUrl);
        var accountId = AccountIdentity.Compute(AccountIdentity.ComputeSafeUrl(playlistUrl), username);
        return new AccountValidationWork(accountId, playlistUrl, username, streams);
    }

    private static AccountValidationWork Work(int port, string user, string pass, params string[] ids)
    {
        var streams = ids
            .Select(id => new AccountStreamWork(
                $"http://127.0.0.1:{port}/live/{user}/{pass}/{id}.ts", $"Ch{id}", "PT"))
            .ToList();
        return BuildWork(port, user, pass, streams);
    }

    private static AccountValidationWork BareWork(int port, string user, string pass, params string[] ids)
    {
        var streams = ids
            .Select(id => new AccountStreamWork(
                $"http://127.0.0.1:{port}/{user}/{pass}/{id}", $"Ch{id}", "PT"))
            .ToList();
        return BuildWork(port, user, pass, streams);
    }

    private static string Key(int port, string id)
        => StreamFingerprint.TryComputeFingerprint($"http://127.0.0.1:{port}/live/seed/seed/{id}.ts")!;

    private static async Task<AccountValidationResult> RunAsync(AccountValidator validator, AccountValidationWork work)
        => await validator.ValidateAccountAsync(work, CancellationToken.None);

    // ─────────────────────────────────────────────────────────────
    // S0 — sanity da premissa sfp1
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// S0 (sanity): <c>sfp1</c> colapsa dois URLs <c>/live/u/p/ID.ts</c> no mesmo
    /// host:port (credenciais mascaradas) e distingue porta e id diferentes.
    /// </summary>
    [Fact]
    public void S0_sfp1_collapses_credentials_same_hostport_and_distinguishes_port_and_id()
    {
        var a = StreamFingerprint.TryComputeFingerprint("http://127.0.0.1:5000/live/userAAAA/passAAAA/7.ts");
        var b = StreamFingerprint.TryComputeFingerprint("http://127.0.0.1:5000/live/userBBBB/passBBBB/7.ts");
        var differentPort = StreamFingerprint.TryComputeFingerprint("http://127.0.0.1:5001/live/userAAAA/passAAAA/7.ts");
        var differentId = StreamFingerprint.TryComputeFingerprint("http://127.0.0.1:5000/live/userAAAA/passAAAA/8.ts");

        Assert.NotNull(a);
        Assert.Equal(a, b);
        Assert.NotEqual(a, differentPort);
        Assert.NotEqual(a, differentId);
    }

    [Fact]
    public void ProbeDefaultsAreTestAllAndHostFailureOff()
    {
        var state = StreamValidationTesterFactory.CreateIsolatedState();
        Assert.Equal(EarlyExitPolicy.TestAll, state.Options.EarlyExit);
        Assert.Equal(HostFailurePolicy.Off, state.Options.HostFailure);
    }

    // ─────────────────────────────────────────────────────────────
    // S1, S2 — primeiro teste físico e reutilização por conta
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// S1 (cenário 1, critério A): primeira conta com CH1,CH2,CH3 → exactamente
    /// 3 GETs físicos, cada um com as credenciais desta conta.
    /// </summary>
    [Fact]
    public async Task S1_first_account_all_channels_physically_tested_with_own_credentials()
    {
        using var server = new ScriptedXtreamServer();
        server.Start();
        var registry = new ValidationKeyRegistry();
        var validator = NewValidator(registry);

        var work = Work(server.Port, "userAAAA", "passAAAA", "1", "2", "3");
        var result = await RunAsync(validator, work);

        Assert.Equal(3, result.Tested);
        Assert.Equal(0, result.Reused);
        Assert.Equal(3, result.Outcomes.Count);
        Assert.All(result.Outcomes, o => Assert.True(o.IsWorking));
        Assert.All(result.Outcomes, o =>
            Assert.NotEqual(default(DateTime), M3uTesterService
                .BuildStreamForAccountFromOutcome(o.Url, "t", "g", o).LastTested));
        Assert.Equal(3, registry.WorkingKeyCount);

        Assert.Equal(3, server.RequestCount);
        Assert.All(server.Requests, r =>
            Assert.Contains("/live/userAAAA/passAAAA/", r.PathAndQuery, StringComparison.Ordinal));
    }

    /// <summary>
    /// S2 (cenários 2/17/18, critérios B/C/E): segunda conta com os MESMOS três
    /// ids → exactamente 1 GET físico (probe obrigatório), 2 reutilizações;
    /// alinhamento posicional; outcomes reutilizados com <c>ReusedKnownWorking</c>
    /// e <c>LastTested == default</c>; URLs próprios da segunda conta (sem fusão).
    /// </summary>
    [Fact]
    public async Task S2_second_account_same_ids_reuses_all_but_mandatory_probe()
    {
        using var server = new ScriptedXtreamServer();
        server.Start();
        var registry = new ValidationKeyRegistry();
        var validator = NewValidator(registry);

        var first = Work(server.Port, "userAAAA", "passAAAA", "1", "2", "3");
        await RunAsync(validator, first);

        var second = Work(server.Port, "userBBBB", "passBBBB", "1", "2", "3");
        var result = await RunAsync(validator, second);

        Assert.Equal(1, result.Tested);
        Assert.Equal(2, result.Reused);
        Assert.Equal(3, result.Outcomes.Count);
        Assert.Equal(3, result.Working);

        for (var i = 0; i < result.Outcomes.Count; i++)
        {
            Assert.Equal(second.Streams[i].Url, result.Outcomes[i].Url);
        }

        var physical = result.Outcomes.Where(o => !o.ReusedKnownWorking).ToList();
        var reused = result.Outcomes.Where(o => o.ReusedKnownWorking).ToList();
        Assert.Single(physical);
        Assert.Equal(2, reused.Count);
        Assert.All(reused, o => Assert.True(o.IsWorking));

        // Projecção: físico com LastTested != default; reutilizado == default.
        Assert.NotEqual(default(DateTime), M3uTesterService
            .BuildStreamForAccountFromOutcome(physical[0].Url, "t", "g", physical[0]).LastTested);
        Assert.All(reused, o => Assert.Equal(default(DateTime), M3uTesterService
            .BuildStreamForAccountFromOutcome(o.Url, "t", "g", o).LastTested));

        // Nenhuma fusão de contas: os outcomes reutilizados trazem URLs da 2.ª conta.
        Assert.All(reused, o => Assert.Contains("/live/userBBBB/passBBBB/", o.Url, StringComparison.Ordinal));

        // 3 (1.ª conta) + 1 (probe da 2.ª) = 4 GETs físicos.
        Assert.Equal(4, server.RequestCount);
        Assert.Equal(3, registry.WorkingKeyCount);
    }

    // ─────────────────────────────────────────────────────────────
    // S5 — preferência do probe por chave já Working
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// S5 (cenário 5): lista <c>[knownWorkingId, newId]</c> → 2 GETs físicos e o
    /// PRIMEIRO request é o known-working id servido com as credenciais desta
    /// conta (prova preferência do probe + credenciais próprias).
    /// </summary>
    [Fact]
    public async Task S5_probe_prefers_known_working_stream_and_uses_own_credentials()
    {
        using var server = new ScriptedXtreamServer();
        server.Start();
        var registry = new ValidationKeyRegistry();
        registry.MarkWorking(Key(server.Port, "1"));
        var validator = NewValidator(registry);

        var work = Work(server.Port, "userAAAA", "passAAAA", "1", "2");
        var result = await RunAsync(validator, work);

        Assert.Equal(2, result.Tested);
        Assert.Equal(0, result.Reused);
        Assert.Equal(2, server.RequestCount);

        var firstRequestId = ScriptedXtreamServer.ExtractChannelId(
            new Uri("http://127.0.0.1" + server.Requests[0].PathAndQuery).AbsolutePath);
        Assert.Equal("1", firstRequestId);
        Assert.Contains("/live/userAAAA/passAAAA/1.ts", server.Requests[0].PathAndQuery, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────
    // S3/S6 — mistura conhecido + novo
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// S3/S6 (cenários 3 e 6): conta <c>[CH2 known, CH3 known, CH4 new]</c> →
    /// exactamente 2 GETs físicos (CH2 probe + CH4 novo); CH3 reutilizado.
    /// </summary>
    [Fact]
    public async Task S3_S6_known_and_new_mix_tests_only_probe_and_new_channel()
    {
        using var server = new ScriptedXtreamServer();
        server.Start();
        var registry = new ValidationKeyRegistry();
        registry.MarkWorking(Key(server.Port, "2"));
        registry.MarkWorking(Key(server.Port, "3"));
        var validator = NewValidator(registry);

        var work = Work(server.Port, "userAAAA", "passAAAA", "2", "3", "4");
        var result = await RunAsync(validator, work);

        Assert.Equal(2, result.Tested);
        Assert.Equal(1, result.Reused);

        var requestedIds = server.Requests
            .Select(r => ScriptedXtreamServer.ExtractChannelId(r.PathAndQuery))
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(new HashSet<string?> { "2", "4" }.SetEquals(requestedIds),
            $"Expected requests for ids {{2,4}}; got {{{string.Join(",", requestedIds)}}}.");
        Assert.DoesNotContain("3", requestedIds);
    }

    // ─────────────────────────────────────────────────────────────
    // S4 — sem conhecimento prévio; cobertura da união
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// S4 (cenário 4, critério A): conta sem canais previamente conhecidos →
    /// todos os canais fisicamente testados.
    /// </summary>
    [Fact]
    public async Task S4_account_with_no_known_channels_tests_all()
    {
        using var server = new ScriptedXtreamServer();
        server.Start();
        var validator = NewValidator();

        var result = await RunAsync(validator, Work(server.Port, "userAAAA", "passAAAA", "1", "2", "3"));

        Assert.Equal(3, result.Tested);
        Assert.Equal(0, result.Reused);
        Assert.Equal(3, server.RequestCount);
    }

    /// <summary>
    /// S4 (cenário 15): contas A[1,2,3], B[2,3,4], C[1,4,5] em sequência → a
    /// união {1,2,3,4,5} é coberta por GETs físicos registados.
    /// </summary>
    [Fact]
    public async Task S4_union_of_physical_gets_covers_all_channels_across_sequence()
    {
        using var server = new ScriptedXtreamServer();
        server.Start();
        var validator = NewValidator();

        await RunAsync(validator, Work(server.Port, "userAAAA", "passAAAA", "1", "2", "3"));
        await RunAsync(validator, Work(server.Port, "userBBBB", "passBBBB", "2", "3", "4"));
        await RunAsync(validator, Work(server.Port, "userCCCC", "passCCCC", "1", "4", "5"));

        var requestedIds = server.Requests
            .Select(r => ScriptedXtreamServer.ExtractChannelId(r.PathAndQuery))
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(new HashSet<string?> { "1", "2", "3", "4", "5" }.SetEquals(requestedIds),
            $"Expected union {{1,2,3,4,5}}; got {{{string.Join(",", requestedIds)}}}.");
    }

    // ─────────────────────────────────────────────────────────────
    // S7 — falhas nunca são reutilizáveis
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// S7 (cenário 7, critério D): conta A recebe 404 terminal em CH1; conta B com
    /// CH1 TEM de o testar fisicamente; o registo nunca o reporta Working.
    /// </summary>
    [Fact]
    public async Task S7_terminal_failure_is_not_reused()
    {
        using var server = new ScriptedXtreamServer(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["404"] = 404,
        });
        server.Start();
        var registry = new ValidationKeyRegistry();
        var validator = NewValidator(registry);

        var a = await RunAsync(validator, Work(server.Port, "userAAAA", "passAAAA", "404"));
        Assert.Equal(1, a.Tested);
        Assert.Equal(1, a.Failed);
        Assert.Equal(0, a.Reused);
        Assert.True(registry.TryGetState(Key(server.Port, "404"), out var stateA));
        Assert.Equal(ValidationKeyState.FailedTerminal, stateA);
        Assert.False(registry.IsKnownWorking(Key(server.Port, "404")));

        var b = await RunAsync(validator, Work(server.Port, "userBBBB", "passBBBB", "404"));
        Assert.Equal(1, b.Tested);
        Assert.Equal(0, b.Reused);
        Assert.False(b.Outcomes[0].ReusedKnownWorking);
        Assert.Contains("/live/userBBBB/passBBBB/404.ts", server.Requests[1].PathAndQuery, StringComparison.Ordinal);
        Assert.Equal(2, server.RequestCount);
    }

    /// <summary>
    /// S7b (critério D, transitório): conta A recebe 503 retryable → estado
    /// <c>FailedTransient</c>; conta B testa-o ainda fisicamente.
    /// </summary>
    [Fact]
    public async Task S7b_transient_failure_is_not_reused()
    {
        using var server = new ScriptedXtreamServer(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["503"] = 503,
        });
        server.Start();
        var registry = new ValidationKeyRegistry();
        var validator = NewValidator(registry);

        await RunAsync(validator, Work(server.Port, "userAAAA", "passAAAA", "503"));
        Assert.True(registry.TryGetState(Key(server.Port, "503"), out var state));
        Assert.Equal(ValidationKeyState.FailedTransient, state);
        Assert.False(registry.IsKnownWorking(Key(server.Port, "503")));

        var b = await RunAsync(validator, Work(server.Port, "userBBBB", "passBBBB", "503"));
        Assert.Equal(1, b.Tested);
        Assert.Equal(0, b.Reused);
        Assert.False(b.Outcomes[0].ReusedKnownWorking);
        Assert.Equal(2, server.RequestCount);
    }

    // ─────────────────────────────────────────────────────────────
    // S9/S10 — contas e providers distintos
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// S9/S10 (cenários 9/10): duas contas permanecem distintas — cada request
    /// usa as credenciais da conta processada, sem vazamento de outcomes.
    /// </summary>
    [Fact]
    public async Task S9_S10_accounts_stay_distinct_and_requests_use_own_credentials()
    {
        using var server = new ScriptedXtreamServer();
        server.Start();
        var validator = NewValidator();

        var a = await RunAsync(validator, Work(server.Port, "userAAAA", "passAAAA", "1", "2"));
        var b = await RunAsync(validator, Work(server.Port, "userBBBB", "passBBBB", "1", "2"));

        Assert.Equal(2, a.Tested);
        Assert.Equal(1, b.Tested);
        Assert.Equal(1, b.Reused);
        Assert.All(b.Outcomes, o => Assert.Contains("/live/userBBBB/passBBBB/", o.Url, StringComparison.Ordinal));
        Assert.All(a.Outcomes, o => Assert.Contains("/live/userAAAA/passAAAA/", o.Url, StringComparison.Ordinal));

        Assert.Contains("/live/userAAAA/passAAAA/1.ts", server.Requests[0].PathAndQuery, StringComparison.Ordinal);
        Assert.Contains("/live/userAAAA/passAAAA/2.ts", server.Requests[1].PathAndQuery, StringComparison.Ordinal);
        Assert.Contains("/live/userBBBB/passBBBB/", server.Requests[2].PathAndQuery, StringComparison.Ordinal);
        Assert.Equal(3, server.RequestCount);
    }

    /// <summary>
    /// S10b (cenário 10, providers diferentes): dois listeners em portas
    /// distintas, mesmo id → ambos testados fisicamente; sem reutilização
    /// cross-provider.
    /// </summary>
    [Fact]
    public async Task S10b_different_providers_same_id_both_physically_tested()
    {
        using var providerA = new ScriptedXtreamServer();
        using var providerB = new ScriptedXtreamServer();
        providerA.Start();
        providerB.Start();
        var registry = new ValidationKeyRegistry();
        var validator = NewValidator(registry);

        var a = await RunAsync(validator, Work(providerA.Port, "userAAAA", "passAAAA", "1"));
        var b = await RunAsync(validator, Work(providerB.Port, "userBBBB", "passBBBB", "1"));

        Assert.Equal(1, a.Tested);
        Assert.Equal(0, a.Reused);
        Assert.Equal(1, b.Tested);
        Assert.Equal(0, b.Reused);
        Assert.Equal(1, providerA.RequestCount);
        Assert.Equal(1, providerB.RequestCount);
        Assert.Equal(2, registry.WorkingKeyCount);
    }

    /// <summary>
    /// S11 (cenário 11): dois ids diferentes no mesmo provider → duas chaves
    /// distintas → ambos testados, <c>Reused == 0</c>.
    /// </summary>
    [Fact]
    public async Task S11_different_ids_same_provider_both_tested_no_reuse()
    {
        using var server = new ScriptedXtreamServer();
        server.Start();
        var validator = NewValidator();

        var result = await RunAsync(validator, Work(server.Port, "userAAAA", "passAAAA", "1", "2"));

        Assert.Equal(2, result.Tested);
        Assert.Equal(0, result.Reused);
        Assert.Equal(2, server.RequestCount);
    }

    // ─────────────────────────────────────────────────────────────
    // S12 — URLs não fingerprintáveis
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// S12 (cenário 12, critério F): URLs não fingerprintáveis
    /// (<c>rtmp://</c>, <c>udp://</c>) processados por duas contas nunca são
    /// reutilizados nem registados.
    /// </summary>
    [Fact]
    public async Task S12_non_fingerprintable_urls_never_reused_nor_recorded()
    {
        var registry = new ValidationKeyRegistry();
        var validator = NewValidator(registry);

        static AccountValidationWork NonFingerprintable(string user, string pass)
        {
            var streams = new List<AccountStreamWork>
            {
                new("rtmp://127.0.0.1:1935/live/stream", "Rtmp", "PT"),
                new("udp://@239.0.0.1:1234", "Udp", "PT"),
            };
            return BuildWork(1935, user, pass, streams);
        }

        var a = await RunAsync(validator, NonFingerprintable("userAAAA", "passAAAA"));
        var b = await RunAsync(validator, NonFingerprintable("userBBBB", "passBBBB"));

        Assert.All(a.Outcomes, o => Assert.False(o.ReusedKnownWorking));
        Assert.All(b.Outcomes, o => Assert.False(o.ReusedKnownWorking));
        Assert.Equal(0, registry.Count);
        Assert.Equal(0, registry.ReuseCount);
        Assert.Equal(4, registry.PhysicalCount);
    }

    // ─────────────────────────────────────────────────────────────
    // S13 — forma bare do Xtream: sanitizado igual, sfp1 distinto
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// S13 (cenários 13/22/23, critério G): forma bare
    /// <c>scheme://host:port/{user}/{pass}/{id}</c> para duas contas com o mesmo
    /// id. (i) <see cref="CredentialSanitizer.SanitizeUrl"/> é IDÊNTICO para as
    /// duas; (ii) ambas são testadas fisicamente (2 GETs) — prova que a URL
    /// sanitizada não é a chave de dedup.
    /// </summary>
    [Fact]
    public async Task S13_bare_xtream_sanitized_url_equal_but_sfp1_distinct_both_tested()
    {
        using var server = new ScriptedXtreamServer();
        server.Start();
        var registry = new ValidationKeyRegistry();
        var validator = NewValidator(registry);

        var urlA = $"http://127.0.0.1:{server.Port}/userAAAA/passAAAA/4242";
        var urlB = $"http://127.0.0.1:{server.Port}/userBBBB/passBBBB/4242";

        Assert.Equal(CredentialSanitizer.SanitizeUrl(urlA), CredentialSanitizer.SanitizeUrl(urlB));

        var a = await RunAsync(validator, BareWork(server.Port, "userAAAA", "passAAAA", "4242"));
        var b = await RunAsync(validator, BareWork(server.Port, "userBBBB", "passBBBB", "4242"));

        Assert.Equal(1, a.Tested);
        Assert.Equal(0, a.Reused);
        Assert.Equal(1, b.Tested);
        Assert.Equal(0, b.Reused);
        Assert.True(a.Outcomes[0].IsWorking);
        Assert.True(b.Outcomes[0].IsWorking);
        Assert.Equal(2, server.RequestCount);
        Assert.Equal(2, registry.Count);
    }

    // ─────────────────────────────────────────────────────────────
    // S14 — concorrência sobre o mesmo registo
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// S14 (cenário 14, critério H): duas contas processadas CONCORRENTEMENTE no
    /// mesmo <see cref="AccountValidator"/> / registo, partilhando uma chave já
    /// Working → sem excepção, registo consistente; a chave partilhada é testada
    /// fisicamente ≥1 e ≤2 vezes (duplicação de corrida limitada); termina
    /// rápido (sem lock global).
    /// </summary>
    [Fact]
    public async Task S14_concurrent_accounts_shared_registry_consistent_no_global_lock()
    {
        using var server = new ScriptedXtreamServer();
        server.Start();
        var registry = new ValidationKeyRegistry();
        registry.MarkWorking(Key(server.Port, "1")); // chave partilhada por A e B
        registry.MarkWorking(Key(server.Port, "2")); // conhecida, só na conta A (reutilizada)
        var validator = NewValidator(registry);

        var a = Work(server.Port, "userAAAA", "passAAAA", "1", "2");
        var b = Work(server.Port, "userBBBB", "passBBBB", "1", "3");

        var sw = Stopwatch.StartNew();
        var results = await Task.WhenAll(RunAsync(validator, a), RunAsync(validator, b));
        sw.Stop();

        _output.WriteLine($"S14 elapsed={sw.ElapsedMilliseconds}ms physical={registry.PhysicalCount} reuse={registry.ReuseCount}");

        Assert.True(sw.ElapsedMilliseconds < 10_000,
            $"S14 took {sw.ElapsedMilliseconds}ms; expected fast completion (<10000ms) without a global lock.");

        // A: probe id1 físico + id2 reutilizado. B: probe id1 físico + id3 físico.
        Assert.True(registry.PhysicalCount >= 2 && registry.PhysicalCount <= 3);
        // O registo cobre exactamente 4 outcomes (2 por conta): cada um é físico
        // ou reutilizado, pelo que a soma tem de fechar sempre em 4. A conta A
        // reutiliza obrigatoriamente a chave id2 (Working só nessa conta), logo
        // o reuse acontece de facto e a chave partilhada id1 passa pelo probe
        // físico de cada conta — dentro do limite de corrida [1, 2] (ver acima).
        Assert.InRange(registry.ReuseCount, 1, 2);
        Assert.Equal(4, registry.PhysicalCount + registry.ReuseCount);

        // Chave partilhada testada ≥1 e ≤2 vezes.
        var sharedRequests = server.Requests.Count(r => r.PathAndQuery.EndsWith("/1.ts", StringComparison.Ordinal));
        Assert.InRange(sharedRequests, 1, 2);

        // Registo consistente depois da corrida.
        Assert.True(registry.IsKnownWorking(Key(server.Port, "1")));
        Assert.True(registry.IsKnownWorking(Key(server.Port, "2")));
        Assert.True(registry.IsKnownWorking(Key(server.Port, "3")));
        Assert.Equal(3, registry.WorkingKeyCount);
        Assert.Equal(2, results.Length);
    }

    // ─────────────────────────────────────────────────────────────
    // S16 — cada conta tem um GET físico com o seu username
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// S16 (cenário 16, critério C): numa sequência de 4 contas, TODAS têm ≥1 GET
    /// físico registado contendo o seu próprio username.
    /// </summary>
    [Fact]
    public async Task S16_every_account_has_physical_get_with_own_username()
    {
        using var server = new ScriptedXtreamServer();
        server.Start();
        var validator = NewValidator();

        var accounts = new[] { "userAAAA", "userBBBB", "userCCCC", "userDDDD" };
        for (var i = 0; i < accounts.Length; i++)
        {
            var user = accounts[i];
            var before = server.RequestCount;
            await RunAsync(validator, Work(server.Port, user, $"pass{user[4..]}", "1", "2"));
            var newRequests = server.Requests.Skip(before).ToList();
            Assert.NotEmpty(newRequests);
            Assert.Contains(newRequests, r => r.PathAndQuery.Contains($"/live/{user}/", StringComparison.Ordinal));
        }
    }

    // ─────────────────────────────────────────────────────────────
    // S19/S20 — AccumulateValidationCounters
    // ─────────────────────────────────────────────────────────────

    private static M3uStream Physical(string url, bool working) =>
        new() { Url = url, IsWorking = working, LastTested = DateTime.Now };

    private static M3uStream Reused(string url) =>
        new() { Url = url, IsWorking = true, LastTested = default };

    /// <summary>
    /// S19/S20 (cenários 19/20): 3 físicos (2 working, 1 failed) + 2 reutilizados
    /// → contadores e invariante.
    /// </summary>
    [Fact]
    public void S19_AccumulateValidationCounters_counts_physical_and_reused()
    {
        var rep = new RunReport();
        var streams = new List<M3uStream>
        {
            Physical("http://x/1", true),
            Physical("http://x/2", true),
            Physical("http://x/3", false),
            Reused("http://x/4"),
            Reused("http://x/5"),
        };

        TelegramScraperService.AccumulateValidationCounters(rep, streams);

        Assert.Equal(3, rep.StreamsTested);
        Assert.Equal(2, rep.StreamsWorking);
        Assert.Equal(1, rep.StreamsFailed);
        Assert.Equal(2, rep.StreamsSkippedAlreadyValidated);
        Assert.Equal(rep.StreamsTested, rep.StreamsWorking + rep.StreamsFailed);
    }

    /// <summary>
    /// S20 (cenário 20): caso todos-físicos reproduz o comportamento pré-wave
    /// (<c>Skipped == 0</c>).
    /// </summary>
    [Fact]
    public void S20_AccumulateValidationCounters_all_physical_matches_prewave()
    {
        var rep = new RunReport();
        var streams = new List<M3uStream>
        {
            Physical("http://x/1", true),
            Physical("http://x/2", false),
        };

        TelegramScraperService.AccumulateValidationCounters(rep, streams);

        Assert.Equal(2, rep.StreamsTested);
        Assert.Equal(1, rep.StreamsWorking);
        Assert.Equal(1, rep.StreamsFailed);
        Assert.Equal(0, rep.StreamsSkippedAlreadyValidated);
    }

    /// <summary>
    /// S20 (paralelismo): 8 threads × N invocações não perdem incrementos.
    /// </summary>
    [Fact]
    public void S20b_AccumulateValidationCounters_parallel_no_lost_increments()
    {
        var rep = new RunReport();
        var streams = new List<M3uStream>
        {
            Physical("http://x/1", true),
            Physical("http://x/2", true),
            Physical("http://x/3", false),
        };

        Parallel.For(0, 8, _ => TelegramScraperService.AccumulateValidationCounters(rep, streams));

        Assert.Equal(24, rep.StreamsTested);
        Assert.Equal(16, rep.StreamsWorking);
        Assert.Equal(8, rep.StreamsFailed);
        Assert.Equal(0, rep.StreamsSkippedAlreadyValidated);
        Assert.Equal(rep.StreamsTested, rep.StreamsWorking + rep.StreamsFailed);
    }

    // ─────────────────────────────────────────────────────────────
    // S21 — reset entre runs
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// S21 (cenário 21): duas instâncias separadas de AccountValidator+registo
    /// (dois runs) voltam a testar tudo.
    /// </summary>
    [Fact]
    public async Task S21_registry_reset_between_runs_retests_everything()
    {
        using var server = new ScriptedXtreamServer();
        server.Start();

        var validator1 = NewValidator();
        var run1 = await RunAsync(validator1, Work(server.Port, "userAAAA", "passAAAA", "1", "2"));
        Assert.Equal(2, run1.Tested);
        Assert.Equal(0, run1.Reused);
        Assert.Equal(2, server.RequestCount);

        var validator2 = NewValidator();
        var run2 = await RunAsync(validator2, Work(server.Port, "userAAAA", "passAAAA", "1", "2"));
        Assert.Equal(2, run2.Tested);
        Assert.Equal(0, run2.Reused);
        Assert.Equal(4, server.RequestCount);
    }

    /// <summary>
    /// S21 (cenário 21): um <c>Reset()</c> explícito num registo partilhado tem o
    /// mesmo efeito — volta a testar tudo.
    /// </summary>
    [Fact]
    public async Task S21b_explicit_Reset_retests_everything()
    {
        using var server = new ScriptedXtreamServer();
        server.Start();
        var registry = new ValidationKeyRegistry();
        var validator = NewValidator(registry);

        var run1 = await RunAsync(validator, Work(server.Port, "userAAAA", "passAAAA", "1", "2"));
        Assert.Equal(2, run1.Tested);
        Assert.Equal(2, registry.PhysicalCount);

        registry.Reset();
        Assert.Equal(0, registry.Count);
        Assert.Equal(0, registry.PhysicalCount);

        // Credenciais diferentes para não colidir com a cache do state partilhado.
        var run2 = await RunAsync(validator, Work(server.Port, "userBBBB", "passBBBB", "1", "2"));
        Assert.Equal(2, run2.Tested);
        Assert.Equal(0, run2.Reused);
        // Reset zerou os contadores: só o run2 é contabilizado após o reset.
        Assert.Equal(2, registry.PhysicalCount);
        Assert.Equal(4, server.RequestCount);
    }

    // ─────────────────────────────────────────────────────────────
    // S24 — retrocompatibilidade
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// S24 (cenário 24): o construtor posicional de 6 argumentos continua a
    /// compilar e produz <c>Reused == 0</c>; <c>StreamTestOutcome.Empty</c> tem
    /// <c>ReusedKnownWorking == false</c>.
    /// </summary>
    [Fact]
    public void S24_backwards_compatible_result_constructor_defaults_reused_zero()
    {
        var work = Work(1, "userAAAA", "passAAAA", "1");
        var outcomes = new[]
        {
            new StreamTestOutcome("http://x/1", true, 200, StreamFailureKind.None, 1, false, 1, false),
        };

        var result = new AccountValidationResult(work, outcomes, 1, 0, 0, 1);

        Assert.Equal(0, result.Reused);
        Assert.False(StreamTestOutcome.Empty("http://x/1").ReusedKnownWorking);
    }

    // ─────────────────────────────────────────────────────────────
    // PERF — 20 contas × 50 ids no mesmo provider
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// PERF (§7 da wave, evidência medida): 20 contas × os MESMOS 50 ids num
    /// provider. GETs físicos exactos = 50 (1.ª conta) + 19 × 1 (probe
    /// obrigatório por conta) = 69; reutilizações = 1000 - 69 = 931.
    /// </summary>
    [Fact]
    public async Task PERF_20_accounts_50_channels_physical_get_count_and_reuse()
    {
        const int accountCount = 20;
        const int channelCount = 50;

        using var server = new ScriptedXtreamServer();
        server.Start();
        var validator = NewValidator();

        var ids = Enumerable.Range(1, channelCount).Select(i => i.ToString()).ToArray();
        var totalReused = 0;
        var sw = Stopwatch.StartNew();

        for (var account = 0; account < accountCount; account++)
        {
            var work = Work(server.Port, $"user{account:D4}", $"pass{account:D4}", ids);
            var result = await RunAsync(validator, work);
            totalReused += result.Reused;
        }

        sw.Stop();

        var naiveCount = accountCount * channelCount;
        var expectedPhysical = channelCount + (accountCount - 1); // 69

        _output.WriteLine(
            $"PERF accounts={accountCount} distinctSfp1={channelCount} physicalGets={server.RequestCount} " +
            $"reuses={totalReused} naive={naiveCount} elapsedMs={sw.ElapsedMilliseconds} " +
            $"registryPhysical={validator.Registry.PhysicalCount} registryReuse={validator.Registry.ReuseCount}");

        Assert.Equal(expectedPhysical, server.RequestCount);
        Assert.Equal(expectedPhysical, validator.Registry.PhysicalCount);
        Assert.Equal(naiveCount - expectedPhysical, totalReused);
        Assert.Equal(naiveCount - expectedPhysical, validator.Registry.ReuseCount);
        Assert.Equal(1000, naiveCount);
        Assert.Equal(931, totalReused);
        Assert.True(sw.ElapsedMilliseconds < 60_000,
            $"PERF run took {sw.ElapsedMilliseconds}ms; expected well under 60000ms.");
    }

    // ─────────────────────────────────────────────────────────────
    // LiveRunCounts / DashboardMetrics mirror
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void LiveRunCounts_MirrorFrom_copies_StreamsSkippedAlreadyValidated()
    {
        var counts = new LiveRunCounts { StreamsSkippedAlreadyValidated = 999 };
        var report = new RunReport
        {
            StreamsTested = 10,
            StreamsWorking = 8,
            StreamsFailed = 2,
            StreamsSkippedAlreadyValidated = 5,
        };

        counts.MirrorFrom(report);

        Assert.Equal(5, counts.StreamsSkippedAlreadyValidated);
        Assert.Equal(10, counts.StreamsTested);
        Assert.Equal(8, counts.StreamsWorking);
        Assert.Equal(2, counts.StreamsFailed);
    }

    [Fact]
    public void DashboardMetrics_SummarizeRun_exposes_skipped_and_balanced()
    {
        var report = new RunReport
        {
            Status = "completed",
            StreamsTested = 3,
            StreamsWorking = 2,
            StreamsFailed = 1,
            StreamsSkippedAlreadyValidated = 7,
        };

        var json = JsonSerializer.Serialize(DashboardMetrics.SummarizeRun(report));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(7, root.GetProperty("streamsSkippedAlreadyValidated").GetInt32());
        Assert.Equal(3, root.GetProperty("streamsTested").GetInt32());
        Assert.True(root.GetProperty("testsBalanced").GetBoolean());
    }
}
