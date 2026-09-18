namespace m3uCrawler.Services.Telegram;

/// <summary>
/// Implementação real de <see cref="ITelegramAuthBackend"/> sobre
/// <c>WTelegram.Client</c>. O delegate de configuração devolve apenas
/// <c>api_id</c>, <c>api_hash</c> e <c>session_pathname</c> — nunca
/// <c>verification_code</c> nem <c>password</c>, para que o fluxo
/// interactivo seja conduzido explicitamente por
/// <see cref="TelegramAuthService"/>.
/// </summary>
public sealed class WTelegramAuthBackend : ITelegramAuthBackend
{
    private readonly TelegramBackendOptions _options;
    private readonly WTelegram.Client _client;

    public WTelegramAuthBackend(TelegramBackendOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _client = new WTelegram.Client(Config);
    }

    public bool IsAuthenticated => _client.User != null;

    public string? UserName
    {
        get
        {
            var user = _client.User;
            return user?.username ?? user?.first_name;
        }
    }

    public async Task<string?> BeginLoginAsync(TelegramBackendOptions options)
    {
        var phone = options?.Phone ?? _options.Phone;
        return await Task.Run(() => _client.Login(phone)).ConfigureAwait(false);
    }

    public async Task<string?> SubmitAsync(string value)
    {
        return await Task.Run(() => _client.Login(value)).ConfigureAwait(false);
    }

    public void Dispose() => _client.Dispose();

    private string? Config(string what)
    {
        return what switch
        {
            "api_id" => _options.ApiId,
            "api_hash" => _options.ApiHash,
            "session_pathname" => _options.SessionPath,
            _ => null,
        };
    }
}
