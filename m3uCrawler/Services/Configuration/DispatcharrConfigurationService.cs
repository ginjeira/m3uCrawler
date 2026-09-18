using System.Globalization;
using m3uCrawler.Models;

namespace m3uCrawler.Services.Configuration;

/// <summary>
/// Edição e leitura da configuração Dispatcharr persistida em
/// <c>wtelegram.config</c>. Escrita restrita às chaves Dispatcharr; todas
/// as restantes chaves (Telegram, desconhecidas) são preservadas pelo
/// <see cref="IWtelegramConfigStore"/>.
///
/// <para>
/// Invariante de segurança: os segredos (<c>dispatcharr_api_key</c>,
/// <c>dispatcharr_password</c>) nunca são devolvidos por
/// <see cref="GetForDisplay"/> nem registados. <see cref="Save"/> aceita
/// <c>null</c> para "manter o valor existente" e string vazia para
/// "limpar o valor".
/// </para>
/// </summary>
public sealed class DispatcharrConfigurationService
{
    private readonly IWtelegramConfigStore _store;

    public DispatcharrConfigurationService(IWtelegramConfigStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Releitura fresca da configuração efetiva.</summary>
    public DispatcharrConfig Get() => DispatcharrConfigLoader.Parse(_store.Read());

    /// <summary>
    /// Vista para UI/API: nunca inclui ApiKey nem Password, apenas a
    /// existência dos mesmos.
    /// </summary>
    public DispatcharrConfigDisplay GetForDisplay()
    {
        var cfg = Get();
        return new DispatcharrConfigDisplay(
            Enabled: cfg.Enabled,
            BaseUrl: cfg.BaseUrl,
            DryRun: cfg.DryRun,
            HasApiKey: !string.IsNullOrWhiteSpace(cfg.ApiKey),
            HasUsername: !string.IsNullOrWhiteSpace(cfg.Username),
            MatchThreshold: cfg.MatchThreshold.ToString(CultureInfo.InvariantCulture),
            TargetGroupName: cfg.TargetGroupName);
    }

    /// <summary>
    /// Actualiza apenas as chaves fornecidas. <c>ApiKey</c>/<c>Username</c>/
    /// <c>Password</c> a <c>null</c> deixam o valor existente intacto; string
    /// vazia limpa a chave.
    /// </summary>
    public void Save(DispatcharrConfigurationWrite write)
    {
        if (write is null) throw new ArgumentNullException(nameof(write));

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["dispatcharr_enabled"] = write.Enabled ? "true" : "false",
            ["dispatcharr_base_url"] = (write.BaseUrl ?? string.Empty).Trim(),
            ["dispatcharr_dry_run"] = write.DryRun ? "true" : "false",
        };

        if (write.ApiKey is not null)
            values["dispatcharr_api_key"] = write.ApiKey.Trim();
        if (write.Username is not null)
            values["dispatcharr_username"] = write.Username.Trim();
        if (write.Password is not null)
            values["dispatcharr_password"] = write.Password.Trim();

        _store.Upsert(values);
    }
}

/// <summary>Input de escrita parcial. Null = manter; vazio = limpar.</summary>
public sealed record DispatcharrConfigurationWrite(
    bool Enabled,
    string BaseUrl,
    bool DryRun,
    string? ApiKey,
    string? Username,
    string? Password);

/// <summary>Projeção sem segredos para UI/API.</summary>
public sealed record DispatcharrConfigDisplay(
    bool Enabled,
    string BaseUrl,
    bool DryRun,
    bool HasApiKey,
    bool HasUsername,
    string? MatchThreshold,
    string? TargetGroupName);
