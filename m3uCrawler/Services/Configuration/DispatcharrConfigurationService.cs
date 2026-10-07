using System.Globalization;
using System.Linq;
using m3uCrawler.Models;

namespace m3uCrawler.Services.Configuration;

/// <summary>
/// Edição e leitura da configuração Dispatcharr persistida em
/// <c>wtelegram.config</c>. Escrita restrita às chaves Dispatcharr; todas
/// as restantes chaves (Telegram, desconhecidas) são preservadas pelo
/// <see cref="IWtelegramConfigStore"/>.
///
/// <para>
/// Contrato de patch (W6c): cada campo do
/// <see cref="DispatcharrConfigurationWrite"/> é opcional — <c>null</c>
/// significa "manter o valor existente". Os segredos
/// (<c>dispatcharr_api_key</c>, <c>dispatcharr_password</c>) nunca são
/// devolvidos por <see cref="GetForDisplay"/> nem registados; string vazia
/// limpa o valor. O conjunto de chaves suportadas cobre todos os
/// <c>dispatcharr_*</c> lidos por <see cref="DispatcharrConfigLoader"/>.
/// </para>
/// </summary>
public sealed class DispatcharrConfigurationService
{
    private const int MinMatchThreshold = 0;
    private const int MaxMatchThreshold = 100;

    private readonly IWtelegramConfigStore _store;

    public DispatcharrConfigurationService(IWtelegramConfigStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Releitura fresca da configuração efetiva.</summary>
    public DispatcharrConfig Get() => DispatcharrConfigLoader.Parse(_store.Read());

    /// <summary>
    /// Vista para UI/API: nunca inclui ApiKey nem Password, apenas a
    /// existência dos mesmos. Inclui todas as chaves <c>dispatcharr_*</c>
    /// configuráveis (lista de prioridade de providers incluída).
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
            HasPassword: !string.IsNullOrWhiteSpace(cfg.Password),
            MatchThreshold: cfg.MatchThreshold.ToString(CultureInfo.InvariantCulture),
            TargetGroupName: cfg.TargetGroupName,
            AliasFile: cfg.AliasFile,
            ProviderPriority: cfg.ProviderPriority,
            AutoCreateGroups: cfg.AutoCreateGroups);
    }

    /// <summary>
    /// Actualiza apenas as chaves fornecidas (semântica de patch). Campos a
    /// <c>null</c> deixam o valor existente intacto; em <see cref="BaseUrl"/>,
    /// <see cref="TargetGroupName"/>, <see cref="AliasFile"/> e nos segredos,
    /// string vazia limpa a chave. <see cref="ProviderPriority"/> a
    /// <c>null</c> mantém; lista (possivelmente vazia) substitui. Chaves
    /// desconhecidas e não-Dispatcharr são preservadas.
    /// </summary>
    public void Save(DispatcharrConfigurationWrite write)
    {
        if (write is null) throw new ArgumentNullException(nameof(write));

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (write.Enabled is { } enabled)
            values["dispatcharr_enabled"] = enabled ? "true" : "false";
        if (write.BaseUrl is not null)
            values["dispatcharr_base_url"] = write.BaseUrl.Trim();
        if (write.DryRun is { } dryRun)
            values["dispatcharr_dry_run"] = dryRun ? "true" : "false";

        if (write.ApiKey is not null)
            values["dispatcharr_api_key"] = write.ApiKey.Trim();
        if (write.Username is not null)
            values["dispatcharr_username"] = write.Username.Trim();
        if (write.Password is not null)
            values["dispatcharr_password"] = write.Password.Trim();

        if (write.MatchThreshold is { } threshold)
        {
            if (threshold < MinMatchThreshold || threshold > MaxMatchThreshold)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(write),
                    $"dispatcharr_match_threshold must be between {MinMatchThreshold} and {MaxMatchThreshold}.");
            }
            values["dispatcharr_match_threshold"] = threshold.ToString(CultureInfo.InvariantCulture);
        }

        if (write.TargetGroupName is not null)
            values["dispatcharr_target_group_name"] = write.TargetGroupName.Trim();
        if (write.AliasFile is not null)
            values["dispatcharr_alias_file"] = write.AliasFile.Trim();
        if (write.AutoCreateGroups is { } autoCreateGroups)
            values["dispatcharr_auto_create_groups"] = autoCreateGroups ? "true" : "false";

        if (write.ProviderPriority is not null)
        {
            var joined = string.Join(",", write.ProviderPriority
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim()));
            values["dispatcharr_provider_priority"] = joined;
        }

        if (values.Count == 0) return;
        _store.Upsert(values);
    }
}

/// <summary>
/// Input de escrita parcial (patch). <c>null</c> = manter; nos campos de
/// texto/segredo, vazio = limpar. Todas as chaves são opcionais.
/// </summary>
public sealed record DispatcharrConfigurationWrite(
    bool? Enabled = null,
    string? BaseUrl = null,
    bool? DryRun = null,
    string? ApiKey = null,
    string? Username = null,
    string? Password = null,
    int? MatchThreshold = null,
    string? TargetGroupName = null,
    IReadOnlyList<string>? ProviderPriority = null,
    string? AliasFile = null,
    bool? AutoCreateGroups = null);

/// <summary>Projeção sem segredos para UI/API.</summary>
public sealed record DispatcharrConfigDisplay(
    bool Enabled,
    string BaseUrl,
    bool DryRun,
    bool HasApiKey,
    bool HasUsername,
    bool HasPassword,
    string? MatchThreshold,
    string? TargetGroupName,
    string? AliasFile,
    IReadOnlyList<string> ProviderPriority,
    bool AutoCreateGroups);
