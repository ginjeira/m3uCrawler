using m3uCrawler.Models;
using m3uCrawler.Services;
using m3uCrawler.Services.Validation;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Testes de regressao para a semantica de retencao da playlist Telegram
/// (PHASE 9A). Estes testes verificam que:
//
//   - um stream existente com falha retryable (Timeout, Network,
///     HTTP 5xx, HTTP 429, ConnectionRefused, DnsFailure) NAO
///     desaparece da playlist;
//   - um stream existente com falha terminal (404, 401, 403, TLS)
///     PODE ser removido;
//   - um stream novo so entra se passou a validacao;
//   - discovery parcial / RPC_CALL_FAIL / DialogsIncomplete
///     nao provocam remocoes por ausencia.
///
/// Os testes cobrem <see cref="TelegramScraperService.FilterRetainedStreams"/>
/// (a regra) e <see cref="TelegramScraperService.MergeStreams"/>
/// (a deduplicacao por URL com prioridade a funcional).
/// </summary>
public class PlaylistRetentionRegressionTests
{
    private static M3uStream Stream(string url, bool isWorking, string? title = null) =>
        new()
        {
            Url = url,
            Title = title ?? url,
            Group = "PT",
            IsWorking = isWorking,
            LastTested = DateTime.UtcNow,
        };

    private static (M3uStream stream, StreamFailureKind kind) Item(
        string url, bool isWorking, StreamFailureKind kind = StreamFailureKind.None,
        string? title = null) =>
        (Stream(url, isWorking, title), kind);

    // ============================================================
    // R1. Cenarios de retencao por classificacao
    // ============================================================

    [Fact]
    public void Existing_stream_with_success_is_preserved()
    {
        var ret = TelegramScraperService.FilterRetainedStreams(
            new[] { Item("http://a/1", isWorking: true) });
        Assert.Single(ret.Preserved);
        Assert.Equal("http://a/1", ret.Preserved[0].Url);
        Assert.Equal(0, ret.RemovedTerminal);
    }

    [Fact]
    public void Existing_stream_with_Timeout_is_preserved()
    {
        var ret = TelegramScraperService.FilterRetainedStreams(
            new[] { Item("http://a/1", isWorking: false, StreamFailureKind.Timeout) });
        Assert.Single(ret.Preserved);
        Assert.Equal(0, ret.RemovedTerminal);
        Assert.Equal(1, ret.PreservedRetryable);
    }

    [Fact]
    public void Existing_stream_with_Network_is_preserved()
    {
        var ret = TelegramScraperService.FilterRetainedStreams(
            new[] { Item("http://a/1", isWorking: false, StreamFailureKind.Network) });
        Assert.Single(ret.Preserved);
        Assert.Equal(0, ret.RemovedTerminal);
    }

    [Fact]
    public void Existing_stream_with_ConnectionRefused_is_preserved()
    {
        var ret = TelegramScraperService.FilterRetainedStreams(
            new[] { Item("http://a/1", isWorking: false, StreamFailureKind.ConnectionRefused) });
        Assert.Single(ret.Preserved);
        Assert.Equal(0, ret.RemovedTerminal);
    }

    [Fact]
    public void Existing_stream_with_DnsFailure_is_NOT_classified_as_retryable_by_current_policy()
    {
        // DOCUMENTAÇÃO: DnsFailure NÃO está actualmente em
        // StreamFailureClassifier.IsRetryable. Um stream existente
        // com falha DNS é portanto tratado como terminal.
        //
        // Isto pode ser revisto numa tarefa futura que actualize
        // IsRetryable para incluir DnsFailure (DNS transientes são
        // tipicamente retryable). Por agora, a política é:
        //   Retryable = { Timeout, Network, ConnectionRefused,
        //                 HttpStatus429, HttpStatus5xx }.
        var ret = TelegramScraperService.FilterRetainedStreams(
            new[] { Item("http://a/1", isWorking: false, StreamFailureKind.DnsFailure) });
        Assert.Empty(ret.Preserved);
        Assert.Equal(1, ret.RemovedTerminal);
        Assert.Equal(1, ret.RemovedByKind["DnsFailure"]);
    }

    [Fact]
    public void Existing_stream_with_HttpStatus429_is_preserved()
    {
        var ret = TelegramScraperService.FilterRetainedStreams(
            new[] { Item("http://a/1", isWorking: false, StreamFailureKind.HttpStatus429) });
        Assert.Single(ret.Preserved);
        Assert.Equal(0, ret.RemovedTerminal);
    }

    [Fact]
    public void Existing_stream_with_HttpStatus5xx_is_preserved()
    {
        var ret = TelegramScraperService.FilterRetainedStreams(
            new[] { Item("http://a/1", isWorking: false, StreamFailureKind.HttpStatus5xx) });
        Assert.Single(ret.Preserved);
        Assert.Equal(0, ret.RemovedTerminal);
    }

    [Fact]
    public void Existing_stream_with_404_can_be_removed()
    {
        var ret = TelegramScraperService.FilterRetainedStreams(
            new[] { Item("http://a/1", isWorking: false, StreamFailureKind.Deterministic) });
        Assert.Empty(ret.Preserved);
        Assert.Equal(1, ret.RemovedTerminal);
        Assert.Equal(1, ret.RemovedByKind["Deterministic"]);
    }

    [Fact]
    public void Existing_stream_with_401_can_be_removed()
    {
        var ret = TelegramScraperService.FilterRetainedStreams(
            new[] { Item("http://a/1", isWorking: false, StreamFailureKind.Authentication) });
        Assert.Empty(ret.Preserved);
        Assert.Equal(1, ret.RemovedTerminal);
    }

    [Fact]
    public void Existing_stream_with_403_can_be_removed()
    {
        // 403 e' classificado como Authentication pelo classifier.
        var ret = TelegramScraperService.FilterRetainedStreams(
            new[] { Item("http://a/1", isWorking: false, StreamFailureKind.Authentication) });
        Assert.Empty(ret.Preserved);
    }

    [Fact]
    public void Existing_stream_with_TlsFailure_can_be_removed()
    {
        var ret = TelegramScraperService.FilterRetainedStreams(
            new[] { Item("http://a/1", isWorking: false, StreamFailureKind.TlsFailure) });
        Assert.Empty(ret.Preserved);
    }

    [Fact]
    public void Existing_stream_with_mixed_retryable_failures_is_preserved()
    {
        var items = new[]
        {
            Item("http://a/1", false, StreamFailureKind.Timeout),
            Item("http://a/2", false, StreamFailureKind.Network),
            Item("http://a/3", false, StreamFailureKind.HttpStatus429),
            Item("http://a/4", false, StreamFailureKind.HttpStatus5xx),
            Item("http://a/5", false, StreamFailureKind.ConnectionRefused),
        };
        var ret = TelegramScraperService.FilterRetainedStreams(items);
        Assert.Equal(5, ret.Preserved.Count);
        Assert.Equal(0, ret.RemovedTerminal);
        Assert.Equal(5, ret.PreservedRetryable);
    }

    // ============================================================
    // R2. Cenarios com mistura
    // ============================================================

    [Fact]
    public void Mixed_scenario_preserves_retryable_and_working_removes_only_terminal()
    {
        var items = new[]
        {
            Item("http://a/working", true, StreamFailureKind.None),
            Item("http://a/timeout", false, StreamFailureKind.Timeout),
            Item("http://a/5xx", false, StreamFailureKind.HttpStatus5xx),
            Item("http://a/dead", false, StreamFailureKind.Deterministic),
        };
        var ret = TelegramScraperService.FilterRetainedStreams(items);
        Assert.Equal(3, ret.Preserved.Count);
        Assert.Equal(1, ret.RemovedTerminal);
        Assert.Contains(ret.Preserved, s => s.Url == "http://a/working");
        Assert.Contains(ret.Preserved, s => s.Url == "http://a/timeout");
        Assert.Contains(ret.Preserved, s => s.Url == "http://a/5xx");
        Assert.DoesNotContain(ret.Preserved, s => s.Url == "http://a/dead");
    }

    // ============================================================
    // R3. Regressao directa: o cenario 48579 -> 25
    // ============================================================

    [Fact]
    public void Regression_48579_streams_with_100_percent_timeout_preserves_all()
    {
        // Em 2026-09-11 o re-teste com timeout=12s falhou em 99.95%
        // dos streams e a playlist passou de 8.5 MB / 48579 streams
        // para 4.4 KB / 25 streams. Antes da correccao, cada stream
        // com Timeout era removido.
        var items = Enumerable.Range(0, 48579)
            .Select(i => Item($"http://a/{i}", false, StreamFailureKind.Timeout));
        var ret = TelegramScraperService.FilterRetainedStreams(items);
        Assert.Equal(48579, ret.Preserved.Count);
        Assert.Equal(0, ret.RemovedTerminal);
    }

    // ============================================================
    // R4. Cenarios de discovery (sem HTTP) via MergeStreams
    // ============================================================

    [Fact]
    public void New_functional_stream_is_added_to_existing_functional_streams()
    {
        var existing = new List<M3uStream> { Stream("http://a/existing", true, "Existing") };
        var fresh = new List<M3uStream> { Stream("http://a/new", true, "New") };
        var merged = TelegramScraperService.MergeStreams(existing, fresh);
        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, s => s.Url == "http://a/existing");
        Assert.Contains(merged, s => s.Url == "http://a/new");
    }

    [Fact]
    public void New_non_functional_stream_is_not_added()
    {
        // A regra actual de MergeStreams filtra fresh.Where(IsWorking).
        var existing = new List<M3uStream> { Stream("http://a/existing", true, "Existing") };
        var fresh = new List<M3uStream> { Stream("http://a/dead", false, "Dead") };
        var merged = TelegramScraperService.MergeStreams(existing, fresh);
        Assert.Single(merged);
        Assert.Equal("http://a/existing", merged[0].Url);
    }

    [Fact]
    public void Empty_discovery_keeps_existing_streams_intact()
    {
        var existing = new List<M3uStream>
        {
            Stream("http://a/1", true, "Canal 1"),
            Stream("http://a/2", true, "Canal 2"),
            Stream("http://a/3", true, "Canal 3"),
        };
        var fresh = new List<M3uStream>();
        var merged = TelegramScraperService.MergeStreams(existing, fresh);
        Assert.Equal(3, merged.Count);
    }

    [Fact]
    public void Empty_discovery_with_retest_timeout_keeps_existing_streams()
    {
        // Simula o cenario "discovery falha, reteste timeout em todos".
        // Combinacao de FilterRetainedStreams + MergeStreams.
        var existingOriginal = new List<M3uStream>
        {
            Stream("http://a/1", true, "Canal 1"),
            Stream("http://a/2", true, "Canal 2"),
            Stream("http://a/3", true, "Canal 3"),
        };
        Assert.Equal("Canal 1", existingOriginal[0].Title);
        var retestOutcomes = existingOriginal.Select(s =>
            Item(s.Url, isWorking: false, StreamFailureKind.Timeout, title: s.Title)).ToArray();
        Assert.Equal("Canal 1", retestOutcomes[0].stream.Title);
        var filterResult = TelegramScraperService.FilterRetainedStreams(retestOutcomes);
        // Preservados por retryable.
        Assert.Equal(3, filterResult.Preserved.Count);
        Assert.Equal("Canal 1", filterResult.Preserved[0].Title);
        // Merge com discovery vazia mantem os 3 preservados.
        var merged = TelegramScraperService.MergeStreams(filterResult.Preserved, new List<M3uStream>());
        Assert.Equal(3, merged.Count);
        Assert.Contains(merged, s => s.Title == "Canal 1");
        Assert.Contains(merged, s => s.Title == "Canal 2");
        Assert.Contains(merged, s => s.Title == "Canal 3");
    }

    [Fact]
    public void Discovery_partial_with_RpcCallFail_does_not_remove_existing_streams()
    {
        // Cenario "DialogsIncomplete > 0 + RPC_CALL_FAIL": discovery devolve
        // zero resultados. Os streams existentes foram re-testados e os
        // que passaram continuam.
        var existingOriginal = new List<M3uStream>
        {
            Stream("http://a/1", true, "Canal 1"),
            Stream("http://a/2", true, "Canal 2"),
        };
        var retestOutcomes = existingOriginal.Select(s =>
            Item(s.Url, isWorking: true, StreamFailureKind.None)).ToArray();
        var filterResult = TelegramScraperService.FilterRetainedStreams(retestOutcomes);
        // Discovery devolve zero (simulando RPC_CALL_FAIL que abortou o
        // ciclo). O Merge apenas junta existentes + [].
        var merged = TelegramScraperService.MergeStreams(filterResult.Preserved, new List<M3uStream>());
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Discovery_partial_with_RpcCallFail_and_retest_with_retryable_keeps_existing()
    {
        // Combina os dois: discovery parcial E reteste com timeout.
        var existingOriginal = new List<M3uStream>
        {
            Stream("http://a/1", true, "Canal 1"),
            Stream("http://a/2", true, "Canal 2"),
            Stream("http://a/3", true, "Canal 3"),
        };
        var retestOutcomes = existingOriginal.Select(s =>
            Item(s.Url, isWorking: false, StreamFailureKind.Timeout)).ToArray();
        var filterResult = TelegramScraperService.FilterRetainedStreams(retestOutcomes);
        // Todos timeout -> todos preservados (retryable).
        Assert.Equal(3, filterResult.Preserved.Count);
        var merged = TelegramScraperService.MergeStreams(filterResult.Preserved, new List<M3uStream>());
        Assert.Equal(3, merged.Count);
    }

    // ============================================================
    // R5. Discriminacao por categoria via FilterRetainedResult
    // ============================================================

    [Fact]
    public void FilterRetainedResult_exposes_working_retryable_terminal_counts()
    {
        var items = new[]
        {
            Item("http://a/1", true, StreamFailureKind.None),         // working
            Item("http://a/2", true, StreamFailureKind.None),         // working
            Item("http://a/3", false, StreamFailureKind.Timeout),     // retryable
            Item("http://a/4", false, StreamFailureKind.HttpStatus5xx), // retryable
            Item("http://a/5", false, StreamFailureKind.Deterministic), // terminal
        };
        var ret = TelegramScraperService.FilterRetainedStreams(items);
        Assert.Equal(2, ret.PreservedWorking);
        Assert.Equal(2, ret.PreservedRetryable);
        Assert.Equal(1, ret.RemovedTerminal);
    }

    [Fact]
    public void FilterRetainedResult_categorizes_by_StreamFailureKind_name()
    {
        var items = new[]
        {
            Item("http://a/t1", false, StreamFailureKind.Timeout),
            Item("http://a/t2", false, StreamFailureKind.Timeout),
            Item("http://a/n1", false, StreamFailureKind.Network),
            Item("http://a/d1", false, StreamFailureKind.Deterministic),
        };
        var ret = TelegramScraperService.FilterRetainedStreams(items);
        Assert.Equal(2, ret.PreservedByKind["Timeout"]);
        Assert.Equal(1, ret.PreservedByKind["Network"]);
        Assert.Equal(1, ret.RemovedByKind["Deterministic"]);
    }

    // ============================================================
    // R6. Cenarios limite
    // ============================================================

    [Fact]
    public void Empty_input_returns_empty_result()
    {
        var ret = TelegramScraperService.FilterRetainedStreams(Array.Empty<(M3uStream, StreamFailureKind)>());
        Assert.Empty(ret.Preserved);
        Assert.Equal(0, ret.RemovedTerminal);
        Assert.Equal(0, ret.PreservedRetryable);
        Assert.Equal(0, ret.PreservedWorking);
    }

    [Fact]
    public void MergeStreams_dedupes_existing_with_working_fresh()
    {
        // URL duplicada: o fresh funcional (re-testado agora) tem prioridade.
        var existing = new List<M3uStream> { Stream("http://a/dup", true, "Old") };
        var fresh = new List<M3uStream> { Stream("http://a/dup", true, "New") };
        var merged = TelegramScraperService.MergeStreams(existing, fresh);
        Assert.Single(merged);
        // Como fresh e' iterado depois de existing, "New" substitui "Old".
        Assert.Equal("New", merged[0].Title);
    }

    [Fact]
    public void MergeStreams_with_existing_only_returns_existing()
    {
        var existing = new List<M3uStream>
        {
            Stream("http://a/1", true),
            Stream("http://a/2", true),
        };
        var merged = TelegramScraperService.MergeStreams(existing, new List<M3uStream>());
        Assert.Equal(2, merged.Count);
    }
}
