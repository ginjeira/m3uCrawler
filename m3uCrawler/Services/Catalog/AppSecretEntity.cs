namespace m3uCrawler.Services.Catalog;

/// <summary>
/// Segredo de aplicação persistido na BD do catálogo (tabela
/// <c>app_secrets</c>), identificado por uma chave estável. O
/// <see cref="Value"/> é sempre o resultado de <c>SecretProtector.Protect</c>
/// — nunca texto em claro.
/// </summary>
public sealed class AppSecretEntity
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string UpdatedAtUtc { get; set; } = string.Empty;
}
