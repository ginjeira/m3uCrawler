using System.Globalization;
using System.Text.RegularExpressions;
using m3uCrawler.Services.Configuration;

namespace m3uCrawler.Services.Telegram;

/// <summary>
/// Resultado de um passo do fluxo de autenticação. <see cref="Detail"/> é
/// sempre sanitizado: nunca contém código, password, api_hash ou telefone.
/// </summary>
public sealed record TelegramAuthStatus(TelegramAuthState State, string? UserName, string? Detail, bool Configured);

/// <summary>
/// Projecção sem segredos da configuração relevante para o dashboard/API.
/// <see cref="HasApiHash"/> indica existência; o valor nunca é exposto.
/// </summary>
public sealed record TelegramConfigDisplay(string? ApiId, string? PhoneNumber, bool HasApiHash, string SessionPath, bool WouldPromptConsole);

/// <summary>
/// Orquestra o login interactivo Telegram por passos, persistindo as
/// credenciais em <c>wtelegram.config</c> e mantendo o estado entre
/// <c>Start</c> → <c>SubmitCode</c>/<c>SubmitPassword</c>.
///
/// <para>
/// Invariantes: nenhum método lê da consola; nenhum segredo é registado;
/// todas as operações são serializadas por um <see cref="SemaphoreSlim"/>.
/// </para>
/// </summary>
public sealed class TelegramAuthService
{
    internal const string DefaultSessionPath = "session.dat";
    private static readonly TimeSpan ResumeCacheDuration = TimeSpan.FromSeconds(30);

    private readonly IWtelegramConfigStore _store;
    private readonly Func<TelegramBackendOptions, ITelegramAuthBackend> _backendFactory;
    private readonly Func<string, bool> _sessionFileExists;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ITelegramAuthBackend? _backend;
    private TelegramAuthState _state = TelegramAuthState.NotConfigured;
    private string? _userName;
    private string? _detail;
    private DateTime _resumeProbeAtUtc = DateTime.MinValue;
    private TelegramAuthStatus? _cachedResumeStatus;

    public TelegramAuthService(
        IWtelegramConfigStore store,
        Func<TelegramBackendOptions, ITelegramAuthBackend>? backendFactory = null,
        Func<string, bool>? sessionFileExists = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _backendFactory = backendFactory ?? (options => new WTelegramAuthBackend(options));
        _sessionFileExists = sessionFileExists
            ?? (path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    public bool IsAuthenticated => _state == TelegramAuthState.Authenticated;

    /// <summary>
    /// Persiste as credenciais (preservando as restantes chaves) e arranca
    /// o login com o número de telefone.
    /// </summary>
    public async Task<TelegramAuthStatus> StartAsync(string apiId, string apiHash, string phone, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var normalizedApiId = (apiId ?? string.Empty).Trim();
            var normalizedApiHash = (apiHash ?? string.Empty).Trim();
            var normalizedPhone = (phone ?? string.Empty).Trim();
            var sessionPath = ResolveSessionPath(_store.Read());

            _store.Upsert(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["api_id"] = normalizedApiId,
                ["api_hash"] = normalizedApiHash,
                ["phone_number"] = normalizedPhone,
                ["session_pathname"] = sessionPath,
            });

            DisposeBackend();
            InvalidateResumeCache();

            var options = new TelegramBackendOptions(normalizedApiId, normalizedApiHash, normalizedPhone, sessionPath);
            var backend = _backendFactory(options);
            _backend = backend;

            var next = await backend.BeginLoginAsync(options).ConfigureAwait(false);
            return ApplyMapping(next);
        }
        catch (Exception ex)
        {
            return SetError(ex, apiHash, phone);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<TelegramAuthStatus> SubmitCodeAsync(string code, CancellationToken ct = default)
        => SubmitStepAsync(TelegramAuthState.WaitingCode, code, ct);

    public Task<TelegramAuthStatus> SubmitPasswordAsync(string password, CancellationToken ct = default)
        => SubmitStepAsync(TelegramAuthState.WaitingPassword, password, ct);

    /// <summary>
    /// Estado corrente. Se não houver login activo mas existir uma sessão
    /// persistida, tenta validá-la via <c>BeginLoginAsync</c> (resultado
    /// cacheado durante <see cref="ResumeCacheDuration"/>).
    /// </summary>
    public async Task<TelegramAuthStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state != TelegramAuthState.NotConfigured && _backend is not null)
                return Snapshot(configured: true);

            if (_cachedResumeStatus is not null
                && DateTime.UtcNow - _resumeProbeAtUtc < ResumeCacheDuration)
            {
                return _cachedResumeStatus;
            }

            var cfg = _store.Read();
            var apiId = ReadValue(cfg, "api_id");
            var apiHash = ReadValue(cfg, "api_hash");
            var phone = ReadValue(cfg, "phone_number");
            var sessionPath = ResolveSessionPath(cfg);
            var configured = !string.IsNullOrWhiteSpace(apiId)
                && !string.IsNullOrWhiteSpace(apiHash)
                && !string.IsNullOrWhiteSpace(phone);

            if (!configured || !_sessionFileExists(sessionPath))
            {
                _state = TelegramAuthState.NotConfigured;
                _userName = null;
                _detail = null;
                return CacheResume(Snapshot(configured));
            }

            var options = new TelegramBackendOptions(apiId!, apiHash!, phone!, sessionPath);
            var status = await ProbeResumeAsync(options).ConfigureAwait(false);
            return CacheResume(status);
        }
        catch (Exception ex)
        {
            return SetError(ex, null, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Vista segura da configuração: nunca inclui o valor do api_hash.
    /// </summary>
    public TelegramConfigDisplay GetConfigForDisplay()
    {
        var cfg = _store.Read();
        return new TelegramConfigDisplay(
            ApiId: ReadValue(cfg, "api_id"),
            PhoneNumber: ReadValue(cfg, "phone_number"),
            HasApiHash: !string.IsNullOrWhiteSpace(ReadValue(cfg, "api_hash")),
            SessionPath: ResolveSessionPath(cfg),
            WouldPromptConsole: false);
    }

    /// <summary>
    /// Persiste (upsert) apenas os valores fornecidos, preservando todas as
    /// restantes chaves através do <see cref="IWtelegramConfigStore"/>.
    /// Valores <c>null</c> mantêm o valor existente; string vazia limpa o
    /// campo. <paramref name="sessionPath"/> nulo/vazio resolve para o
    /// caminho já persistido ou <see cref="DefaultSessionPath"/>.
    ///
    /// <para>
    /// Nunca devolve nem regista segredos: a projecção devolvida é a de
    /// <see cref="GetConfigForDisplay"/>. Não arranca login nem toca na
    /// sessão persistida.
    /// </para>
    /// </summary>
    public TelegramConfigDisplay SaveConfig(
        string? apiId,
        string? apiHash,
        string? phoneNumber,
        string? sessionPath = null)
    {
        var cfg = _store.Read();

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (apiId is not null)
            values["api_id"] = apiId.Trim();
        if (apiHash is not null)
            values["api_hash"] = apiHash.Trim();
        if (phoneNumber is not null)
            values["phone_number"] = phoneNumber.Trim();

        values["session_pathname"] = string.IsNullOrWhiteSpace(sessionPath)
            ? ResolveSessionPath(cfg)
            : sessionPath.Trim();

        _store.Upsert(values);
        InvalidateResumeCache();
        return GetConfigForDisplay();
    }

    /// <summary>
    /// Arranca o login com as credenciais persistidas. Lê os segredos
    /// internamente e delega em <see cref="StartAsync"/>, pelo que o
    /// chamador nunca precisa (nem recebe) o api_hash.
    /// </summary>
    public Task<TelegramAuthStatus> StartFromSavedAsync(CancellationToken ct = default)
    {
        var cfg = _store.Read();
        return StartAsync(
            ReadValue(cfg, "api_id") ?? string.Empty,
            ReadValue(cfg, "api_hash") ?? string.Empty,
            ReadValue(cfg, "phone_number") ?? string.Empty,
            ct);
    }

    /// <summary>
    /// Descarta o estado em memória (backend, login em curso, cache de
    /// resume). Não altera o ficheiro de configuração nem a sessão.
    /// </summary>
    public void Reset()
    {
        _gate.Wait();
        try
        {
            DisposeBackend();
            InvalidateResumeCache();
            _state = TelegramAuthState.NotConfigured;
            _userName = null;
            _detail = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TelegramAuthStatus> SubmitStepAsync(TelegramAuthState expected, string value, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_backend is null || _state != expected)
            {
                _state = TelegramAuthState.Error;
                _userName = null;
                _detail = "no-active-login";
                return Snapshot(configured: true);
            }

            var next = await _backend.SubmitAsync(value).ConfigureAwait(false);
            return ApplyMapping(next);
        }
        catch (Exception ex)
        {
            return SetError(ex, value);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TelegramAuthStatus> ProbeResumeAsync(TelegramBackendOptions options)
    {
        try
        {
            var backend = _backendFactory(options);
            _backend = backend;
            var next = await backend.BeginLoginAsync(options).ConfigureAwait(false);

            if (next is null)
            {
                _state = TelegramAuthState.Authenticated;
                _userName = backend.UserName;
                _detail = null;
                return Snapshot(configured: true);
            }

            // A sessão persistida não permite retomar sem passos interactivos.
            DisposeBackend();
            _state = TelegramAuthState.NotConfigured;
            _userName = null;
            _detail = null;
            return Snapshot(configured: true);
        }
        catch (Exception ex)
        {
            DisposeBackend();
            _state = TelegramAuthState.Error;
            _userName = null;
            _detail = SanitizeDetail(ex, options.ApiHash);
            return Snapshot(configured: true);
        }
    }

    private TelegramAuthStatus ApplyMapping(string? item)
    {
        switch (item)
        {
            case null:
                _state = TelegramAuthState.Authenticated;
                _userName = _backend?.UserName;
                _detail = null;
                break;

            case "verification_code":
                _state = TelegramAuthState.WaitingCode;
                _userName = null;
                _detail = null;
                break;

            case "password":
                _state = TelegramAuthState.WaitingPassword;
                _userName = null;
                _detail = null;
                break;

            case "name":
                _state = TelegramAuthState.Error;
                _userName = null;
                _detail = "signup-required";
                break;

            default:
                _state = TelegramAuthState.Error;
                _userName = null;
                _detail = $"unsupported:{item}";
                break;
        }

        return Snapshot(configured: true);
    }

    private TelegramAuthStatus SetError(Exception ex, params string?[] secrets)
    {
        _state = TelegramAuthState.Error;
        _userName = null;
        _detail = SanitizeDetail(ex, secrets);
        return Snapshot(configured: true);
    }

    private static string SanitizeDetail(Exception ex, params string?[] secrets)
    {
        var message = ex.Message ?? string.Empty;

        var floodWaitSeconds = TryParseFloodWaitSeconds(message);
        if (floodWaitSeconds.HasValue)
            return $"flood-wait:{floodWaitSeconds.Value}";

        return SanitizeDetail(message, secrets);
    }

    private static string SanitizeDetail(string? message, params string?[] secrets)
    {
        var text = message ?? string.Empty;

        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret))
                text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }

        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        const int maxLength = 160;
        if (text.Length > maxLength)
            text = text[..maxLength] + "…";

        return text.Length == 0 ? "unspecified" : text;
    }

    private static int? TryParseFloodWaitSeconds(string message)
    {
        if (string.IsNullOrEmpty(message)
            || !message.Contains("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var match = Regex.Match(message, @"FLOOD_WAIT[_\s(]*(\d+)", RegexOptions.IgnoreCase);
        if (match.Success
            && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds;
        }

        return 0;
    }

    private static string? ReadValue(IReadOnlyDictionary<string, string> cfg, string key)
        => cfg.TryGetValue(key, out var value) ? value : null;

    private static string ResolveSessionPath(IReadOnlyDictionary<string, string> cfg)
    {
        var existing = ReadValue(cfg, "session_pathname");
        return string.IsNullOrWhiteSpace(existing) ? DefaultSessionPath : existing.Trim();
    }

    private TelegramAuthStatus Snapshot(bool configured)
        => new(_state, _userName, _detail, configured);

    private TelegramAuthStatus CacheResume(TelegramAuthStatus status)
    {
        _cachedResumeStatus = status;
        _resumeProbeAtUtc = DateTime.UtcNow;
        return status;
    }

    private void InvalidateResumeCache()
    {
        _cachedResumeStatus = null;
        _resumeProbeAtUtc = DateTime.MinValue;
    }

    private void DisposeBackend()
    {
        var backend = _backend;
        _backend = null;
        if (backend is null)
            return;

        try
        {
            backend.Dispose();
        }
        catch
        {
            // Disposal é best-effort e nunca deve afectar o resultado do login.
        }
    }
}
