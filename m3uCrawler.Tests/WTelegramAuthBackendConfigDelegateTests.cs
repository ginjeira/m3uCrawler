using System;
using System.Reflection;
using m3uCrawler.Services.Telegram;
using Xunit;

namespace m3uCrawler.Tests;

/// <summary>
/// W-FIX-WT-AUTH-PHONE-NUMBER — regressão para o bug descoberto durante o
/// First Real Test em que <see cref="WTelegramAuthBackend"/> configurava
/// <c>api_id</c>, <c>api_hash</c> e <c>session_pathname</c> no delegate
/// <c>Config</c> mas omitia <c>phone_number</c>. Como
/// <c>WTelegram.Client.LoginUserIfNeeded()</c> consulta o delegate para
/// <c>phone_number</c> mesmo quando <c>session.dat</c> está presente
/// (session-recovery check), o pipeline
/// (<c>TelegramScraperService.SearchM3UInTelegramInternal</c>) levantava
/// <c>WTException: You must provide a config value for phone_number</c>.
///
/// <para>
/// Estes testes invocam o delegate privado <c>Config(string what)</c> via
/// reflection (única via não-intrusiva: não altera a API pública nem
/// interna de <see cref="WTelegramAuthBackend"/>; respeita o princípio do
/// AGENTS.md §4 — não inventar APIs para satisfazer o teste).
/// </para>
/// </summary>
public class WTelegramAuthBackendConfigDelegateTests
{
    private const string ValidApiId = "12345";
    private const string ValidApiHash = "0123456789abcdef0123456789abcdef";
    private const string ValidPhone = "+351900000000";

    /// <summary>
    /// Invoca o delegate privado <c>Config(string what)</c> de
    /// <see cref="WTelegramAuthBackend"/> via reflection.
    ///
    /// <para>
    /// O delegate é privado e intencionalmente não exposto na API pública.
    /// Reflection é o caminho padrão em .NET para testar a contract de
    /// delegates privados sem alterar a superfície.
    /// </para>
    /// </summary>
    private static string? InvokeConfig(WTelegramAuthBackend backend, string what)
    {
        var method = typeof(WTelegramAuthBackend)
            .GetMethod("Config", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "WTelegramAuthBackend.Config(string) não encontrado via reflection — a contract privada mudou?");
        return (string?)method.Invoke(backend, new object[] { what });
    }

    private static WTelegramAuthBackend CreateBackend(string phone = ValidPhone)
    {
        // A WTelegram.Client valida api_id/api_hash no construtor mas
        // não tenta login (sem rede). Padrão idêntico a
        // WaveW5TelegramClientReuseTests.CreateTestClient.
        var sessionPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"wt-auth-backend-{Guid.NewGuid():N}.dat");
        return new WTelegramAuthBackend(
            new TelegramBackendOptions(ValidApiId, ValidApiHash, phone, sessionPath));
    }

    [Fact]
    public void Config_returns_phone_number_from_options()
    {
        // Arrange: backend construído com phone_number válido.
        using var backend = CreateBackend();

        // Act: invoca Config("phone_number") via reflection.
        var result = InvokeConfig(backend, "phone_number");

        // Assert: o delegate devolve o telefone tal como foi passado em
        // TelegramBackendOptions, sem transformação ou re-prompt.
        Assert.Equal(ValidPhone, result);
    }

    [Fact]
    public void Config_returns_api_id_and_api_hash_and_session_pathname_unchanged()
    {
        // Cobertura de regressão para os outros casos já existentes no
        // delegate — garante que o fix não quebrou nenhum deles.
        using var backend = CreateBackend();

        Assert.Equal(ValidApiId, InvokeConfig(backend, "api_id"));
        Assert.Equal(ValidApiHash, InvokeConfig(backend, "api_hash"));
        // session_pathname vem do TelegramBackendOptions.SessionPath (não
        // temos acesso directo aqui; basta garantir que está presente e
        // não-nulo).
        Assert.False(string.IsNullOrWhiteSpace(InvokeConfig(backend, "session_pathname")));
    }

    [Fact]
    public void Config_returns_null_for_unknown_keys()
    {
        // Garante que chaves desconhecidas continuam a cair no default
        // `_ => null` — incluindo `verification_code` e `password`, que
        // devem ser conduzidos explicitamente por TelegramAuthService.
        using var backend = CreateBackend();

        Assert.Null(InvokeConfig(backend, "verification_code"));
        Assert.Null(InvokeConfig(backend, "password"));
        Assert.Null(InvokeConfig(backend, "qualquer-chave-desconhecida"));
    }
}
