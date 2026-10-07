namespace m3uCrawler.Services.Telegram;

/// <summary>
/// Estado do fluxo de autenticação interactiva Telegram gerido por
/// <see cref="TelegramAuthService"/>.
/// </summary>
public enum TelegramAuthState
{
    NotConfigured,
    WaitingCode,
    WaitingPassword,
    Authenticated,
    Error
}
