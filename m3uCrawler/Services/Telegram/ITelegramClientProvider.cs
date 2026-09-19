namespace m3uCrawler.Services.Telegram;

/// <summary>
/// Wave W5 — Fornece o cliente <c>WTelegram.Client</c> vivo detido pela
/// aplicação (tipicamente o <see cref="TelegramAuthService"/>).
///
/// <para>
/// A pipeline Telegram consome este cliente em vez de construir um
/// cliente próprio com login de consola. A autenticação é uma operação de
/// aplicação (dashboard), pelo que este caminho nunca lê da consola.
/// </para>
///
/// <para>
/// O cliente é lido a cada execução: uma re-autenticação no dashboard
/// passa a aplicar-se ao run seguinte sem reiniciar o processo. O cliente
/// pertence ao provider e o consumidor nunca o deve libertar.
/// </para>
/// </summary>
public interface ITelegramClientProvider
{
    /// <summary>Verdadeiro quando existe uma sessão autenticada utilizável.</summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Cliente vivo autenticado, ou <c>null</c> quando ainda não há
    /// sessão. Nunca deve ser libertado pelo consumidor.
    /// </summary>
    WTelegram.Client? LiveClient { get; }
}
