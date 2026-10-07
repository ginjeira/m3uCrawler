using System;
using System.Collections.Generic;

namespace m3uCrawler.Services.Recognition;

/// <summary>
/// W5.1 — Política de reconhecimento efectiva (modelo em memória).
///
/// <para>
/// Contrato normativo: <c>docs/Reestructure/38-POLICIES.md §5.1</c> e
/// <c>32-DOMAIN-SCHEMA.md</c> (RecognitionPolicy). O fuzzy é <b>opt-in</b>:
/// <see cref="FuzzyEnabled"/> é <c>false</c> por defeito. Os valores de
/// <see cref="FuzzyThreshold"/>, <see cref="FuzzyAmbiguityMargin"/> e
/// <see cref="FuzzyWeightsJson"/> são <c>PARAMETER_GAP</c> — a BÍBLIA não fixa
/// valores; quando <c>null</c> significam "não decidido".
/// </para>
/// </summary>
public sealed record RecognitionPolicy(
    bool Enabled,
    bool FuzzyEnabled,
    int? FuzzyThreshold,
    int? FuzzyAmbiguityMargin,
    string? FuzzyWeightsJson,
    int Version)
{
    /// <summary>
    /// Default normativo: reconhecimento activo, fuzzy desligado, sem valores
    /// de threshold/margem/pesos (PARAMETER_GAP). Não é uma decisão de produto:
    /// apenas o mínimo necessário para o sistema funcionar sem policy.
    /// </summary>
    public static RecognitionPolicy Default { get; } = new(
        Enabled: true,
        FuzzyEnabled: false,
        FuzzyThreshold: null,
        FuzzyAmbiguityMargin: null,
        FuzzyWeightsJson: null,
        Version: 1);
}

/// <summary>
/// W5.1 — Chaves de âmbito persistidas em <c>RecognitionPolicyEntity.ScopeKey</c>.
/// Scopes normativos: <c>system</c>, <c>global</c>, <c>group:{key}</c>,
/// <c>channel:{key}</c> (<c>38 §5.1</c>). Não existem scopes provider/source.
/// </summary>
public static class RecognitionPolicyScopes
{
    public const string System = "system";
    public const string Global = "global";
    public const string GroupPrefix = "group:";
    public const string ChannelPrefix = "channel:";

    public static string ForGroup(string groupKey)
    {
        if (string.IsNullOrWhiteSpace(groupKey))
        {
            throw new ArgumentException("Chave de grupo é obrigatória.", nameof(groupKey));
        }

        return GroupPrefix + groupKey.Trim();
    }

    public static string ForChannel(string canonicalChannelKey)
    {
        if (string.IsNullOrWhiteSpace(canonicalChannelKey))
        {
            throw new ArgumentException("Chave canónica é obrigatória.", nameof(canonicalChannelKey));
        }

        return ChannelPrefix + canonicalChannelKey.Trim();
    }

    public static bool IsGroup(string scopeKey)
        => !string.IsNullOrEmpty(scopeKey)
           && scopeKey.StartsWith(GroupPrefix, StringComparison.Ordinal);

    public static bool IsChannel(string scopeKey)
        => !string.IsNullOrEmpty(scopeKey)
           && scopeKey.StartsWith(ChannelPrefix, StringComparison.Ordinal);

    public static string? GroupKeyFromScope(string scopeKey)
    {
        if (!IsGroup(scopeKey)) return null;
        var key = scopeKey.Substring(GroupPrefix.Length);
        return string.IsNullOrEmpty(key) ? null : key;
    }

    public static string? ChannelKeyFromScope(string scopeKey)
    {
        if (!IsChannel(scopeKey)) return null;
        var key = scopeKey.Substring(ChannelPrefix.Length);
        return string.IsNullOrEmpty(key) ? null : key;
    }
}
