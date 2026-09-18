namespace m3uCrawler.Services.Telegram;

/// <summary>
/// Credenciais e caminho de sessão necessários para arrancar um login
/// Telegram. <see cref="ApiHash"/> e <see cref="Phone"/> nunca devem ser
/// registados nem devolvidos a consumidores sem sanitização.
/// </summary>
public sealed record TelegramBackendOptions(string ApiId, string ApiHash, string Phone, string SessionPath);

/// <summary>
/// Abstração do backend de autenticação Telegram. Permite testar o fluxo
/// stepwise (<c>Login(phone)</c> → <c>Login(code)</c> → …) sem rede.
/// </summary>
public interface ITelegramAuthBackend : IDisposable
{
    bool IsAuthenticated { get; }

    string? UserName { get; }

    /// <summary>
    /// Primeiro passo do login: submete o número de telefone. Devolve o
    /// próximo item pedido (<c>verification_code</c>, <c>password</c>, …)
    /// ou <c>null</c> quando o login conclui.
    /// </summary>
    Task<string?> BeginLoginAsync(TelegramBackendOptions options);

    /// <summary>
    /// Passo seguinte: submete o valor pedido pelo passo anterior.
    /// </summary>
    Task<string?> SubmitAsync(string value);
}
