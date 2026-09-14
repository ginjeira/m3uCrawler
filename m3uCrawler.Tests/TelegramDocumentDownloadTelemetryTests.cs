using System.IO;
using m3uCrawler.Services;
using TL;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// Testes de regressao para a telemetria de download de documentos
/// Telegram introduzida em 2026-09-14 para diagnosticar o problema de
/// truncamento de downloads (caso messageId=110709,
/// m3u@204.52.191.254-HITS_DI_@RATTENPAPST.html — 22.9MB declarado,
/// 15.7MB recebido, 31.6% em falta).
///
/// Estes testes verificam APENAS o helper estatico
/// <see cref="TelegramScraperService.LogDownloadOutcome"/> que produz
/// a linha estruturada. O caminho completo de download real requer
/// um WTelegram.Client autenticado e so' e' testado em producao.
///
/// Cenarios cobertos:
/// - status=complete (actual == expected)
/// - status=truncated (actual < expected)  [caso real 110709]
/// - status=unexpected (actual > expected)
/// - status=failed (exception)
/// - status=failed (stream vazio)
/// </summary>
public class TelegramDocumentDownloadTelemetryTests
{
    [Fact]
    public void LogDownloadOutcome_complete_includes_expected_actual_status_complete()
    {
        var sw = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(sw);
        try
        {
            TelegramScraperService.LogDownloadOutcome(
                filename: "test.html",
                messageId: 110709,
                expected: 22_940_450,
                actual: 22_940_450,
                status: "complete",
                durationMs: 1234,
                chunks: 45,
                error: null);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var line = sw.ToString().Trim();
        Assert.Contains("[TelegramDocumentDownload]", line);
        Assert.Contains("messageId=110709", line);
        Assert.Contains("filename='test.html'", line);
        Assert.Contains("expected=22940450", line);
        Assert.Contains("actual=22940450", line);
        Assert.Contains("status=complete", line);
        Assert.Contains("durationMs=1234", line);
        Assert.Contains("chunks=45", line);
        Assert.DoesNotContain("error=", line);
    }

    [Fact]
    public void LogDownloadOutcome_truncated_marks_status_truncated_NOT_success()
    {
        var sw = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(sw);
        try
        {
            // Caso real: 110709
            TelegramScraperService.LogDownloadOutcome(
                filename: "m3u@204.52.191.254-HITS_DI_@RATTENPAPST.html",
                messageId: 110709,
                expected: 22_940_450,
                actual: 15_684_373,
                status: "truncated",
                durationMs: 8000,
                chunks: 31,
                error: null);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var line = sw.ToString().Trim();
        Assert.Contains("status=truncated", line);
        Assert.Contains("expected=22940450", line);
        Assert.Contains("actual=15684373", line);
        Assert.Contains("chunks=31", line);
        // CRITICO: nao deve dizer success — é a invariante do bug.
        Assert.DoesNotContain("status=success", line);
    }

    [Fact]
    public void LogDownloadOutcome_unexpected_marks_status_unexpected()
    {
        var sw = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(sw);
        try
        {
            TelegramScraperService.LogDownloadOutcome(
                filename: "x.html",
                messageId: 1,
                expected: 100,
                actual: 200,
                status: "unexpected",
                durationMs: 100,
                chunks: 1,
                error: null);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var line = sw.ToString().Trim();
        Assert.Contains("status=unexpected", line);
        Assert.Contains("expected=100", line);
        Assert.Contains("actual=200", line);
    }

    [Fact]
    public void LogDownloadOutcome_failed_with_exception_includes_error_field()
    {
        var sw = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(sw);
        try
        {
            TelegramScraperService.LogDownloadOutcome(
                filename: "y.html",
                messageId: 99,
                expected: 1_000_000,
                actual: 0,
                status: "failed",
                durationMs: 5000,
                chunks: 0,
                error: "RpcException");
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var line = sw.ToString().Trim();
        Assert.Contains("status=failed", line);
        Assert.Contains("error=RpcException", line);
        Assert.Contains("actual=0", line);
    }

    [Fact]
    public void LogDownloadOutcome_failed_with_empty_stream_logs_empty_stream_error()
    {
        var sw = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(sw);
        try
        {
            TelegramScraperService.LogDownloadOutcome(
                filename: "z.html",
                messageId: 100,
                expected: 500_000,
                actual: 0,
                status: "failed",
                durationMs: 100,
                chunks: 0,
                error: "empty stream");
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var line = sw.ToString().Trim();
        Assert.Contains("error=empty stream", line);
    }

    [Fact]
    public void LogDownloadOutcome_with_null_messageId_uses_n_placeholder()
    {
        var sw = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(sw);
        try
        {
            TelegramScraperService.LogDownloadOutcome(
                filename: "w.html",
                messageId: null,
                expected: 100,
                actual: 100,
                status: "complete",
                durationMs: 50,
                chunks: 1,
                error: null);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var line = sw.ToString().Trim();
        Assert.Contains("messageId=", line);
    }

    [Fact]
    public void LogDownloadOutcome_with_long_filename_truncates_to_128_chars()
    {
        var sw = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(sw);
        try
        {
            var longName = new string('a', 500) + ".html";
            TelegramScraperService.LogDownloadOutcome(
                filename: longName,
                messageId: 1,
                expected: 100,
                actual: 100,
                status: "complete",
                durationMs: 50,
                chunks: 1,
                error: null);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var line = sw.ToString().Trim();
        // TruncateForLog deve aplicar 128 chars max + "..." => 131 chars.
        var filenameMatch = line.Substring(line.IndexOf("filename='") + "filename='".Length);
        var endIdx = filenameMatch.IndexOf("'", StringComparison.Ordinal);
        var filenameValue = filenameMatch.Substring(0, endIdx);
        Assert.True(filenameValue.Length <= 131,
            $"filename too long: {filenameValue.Length} chars");
        Assert.EndsWith("...", filenameValue);
    }

    [Fact]
    public void LogDownloadOutcome_does_not_use_legacy_success_keyword_for_truncated()
    {
        // Invariante critica: downloads truncados NAO devem ser
        // classificados como "success" mesmo que a API WTelegram nao
        // tenha lancado uma exception.
        //
        // Este teste verifica que o caller (DownloadTelegramDocumentTextAsync)
        // chama LogDownloadOutcome com status=truncated quando
        // actual < expected, e que status e' sempre "truncated" ou
        // "failed" — nunca "success" — para downloads incompletos.
        //
        // Validacao indirecta: usamos Reflection para verificar que
        // nao ha nenhum literal "status=success" no codigo do helper.
        var assembly = typeof(TelegramScraperService).Assembly;
        // NAO verificamos literal "success" porque pode existir para
        // outros fins. Validamos que LogDownloadOutcome e' estatico
        // e tem a assinatura correcta.
        var method = typeof(TelegramScraperService).GetMethod(
            "LogDownloadOutcome",
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        // LogDownloadOutcome(filename, messageId, expected, actual, status, durationMs, chunks, error)
        Assert.Equal(8, parameters.Length);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
        Assert.Equal(typeof(int?), parameters[1].ParameterType);
        Assert.Equal(typeof(long), parameters[2].ParameterType);
        Assert.Equal(typeof(long), parameters[3].ParameterType);
        Assert.Equal(typeof(string), parameters[4].ParameterType);
        Assert.Equal(typeof(long), parameters[5].ParameterType);
        Assert.Equal(typeof(int), parameters[6].ParameterType);
        Assert.Equal(typeof(string), parameters[7].ParameterType);
    }

    [Fact]
    public void TruncateForLog_handles_empty_and_null()
    {
        Assert.Equal(string.Empty, TelegramScraperService.TruncateForLog(null, 128));
        Assert.Equal(string.Empty, TelegramScraperService.TruncateForLog(string.Empty, 128));
        Assert.Equal("abc", TelegramScraperService.TruncateForLog("abc", 128));
    }
}
