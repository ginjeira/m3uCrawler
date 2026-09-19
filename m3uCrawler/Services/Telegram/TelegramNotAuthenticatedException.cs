namespace m3uCrawler.Services.Telegram;

/// <summary>
/// Wave W5 — Lançada quando uma operação Telegram da pipeline é pedida
/// sem uma sessão autenticada disponível.
///
/// <para>
/// No caminho de aplicação (web/scheduler) a autenticação é conduzida
/// pelo dashboard; o arranque não termina nem bloqueia a consola, e o
/// gate por capacidade (<c>ActionCapabilityGate</c>) impede execuções
/// automáticas antes da autenticação. Esta excepção é a salvaguarda
/// final se uma execução for tentada mesmo assim.
/// </para>
/// </summary>
public sealed class TelegramNotAuthenticatedException : Exception
{
    public TelegramNotAuthenticatedException()
        : base("Sessão Telegram não autenticada. Conclua o Setup do Telegram no dashboard.")
    {
    }
}
