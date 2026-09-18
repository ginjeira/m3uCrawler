using System;

namespace m3uCrawler.Services.Catalog;

/// <summary>
/// PHASE 13 (Wave 13-4b) — Chaves de âmbito (<c>ScopeKey</c>) da política de
/// selecção de fontes persistida em
/// <see cref="SourceSelectionPolicyEntity"/>. Centraliza a construção e o
/// parsing do formato de override por canal para evitar literais espalhados.
///
/// <para>
/// A política global usa <see cref="Global"/>. Um override por canal usa
/// <c>channel:{canonicalChannelKey}</c>, onde a chave é a identidade estável
/// de <see cref="CanonicalChannelEntity.Key"/>. O override por canal é uma
/// política <b>completa</b>: quando existe, substitui a global por inteiro
/// (não há representação de "campo não definido").
/// </para>
/// </summary>
public static class SourceSelectionPolicyScopes
{
    /// <summary>Âmbito da política global por defeito do sistema.</summary>
    public const string Global = "global";

    /// <summary>Prefixo usado nos âmbitos de override por canal.</summary>
    public const string ChannelPrefix = "channel:";

    /// <summary>Constrói o âmbito persistido para o canal canónico indicado.</summary>
    public static string ForChannel(string canonicalChannelKey)
    {
        if (string.IsNullOrWhiteSpace(canonicalChannelKey))
        {
            throw new ArgumentException("Chave canónica é obrigatória.", nameof(canonicalChannelKey));
        }

        return ChannelPrefix + canonicalChannelKey.Trim();
    }

    /// <summary>Indica se o âmbito corresponde a um override por canal.</summary>
    public static bool IsChannel(string scopeKey)
        => !string.IsNullOrEmpty(scopeKey)
            && scopeKey.StartsWith(ChannelPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Extrai a chave canónica de um âmbito de canal. Devolve <c>null</c>
    /// quando o âmbito não é de canal ou não tem chave associada.
    /// </summary>
    public static string? ChannelKeyFromScope(string scopeKey)
    {
        if (!IsChannel(scopeKey)) return null;

        var key = scopeKey.Substring(ChannelPrefix.Length);
        return string.IsNullOrEmpty(key) ? null : key;
    }
}
