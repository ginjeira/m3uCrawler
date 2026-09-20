using System;
using System.Collections.Generic;

namespace m3uCrawler.Services.Recognition;

/// <summary>Resultado da resolução: qual o âmbito efectivo.</summary>
public enum RecognitionPolicyScope
{
    System = 0,
    Global = 1,
    Group = 2,
    Channel = 3,
}

/// <summary>
/// W5.1 — Conjunto imutável de políticas de reconhecimento carregado para uma
/// execução (snapshot em memória). Resolução determinística:
/// <c>channel → group → global → system/default</c>
/// (<c>38-POLICIES.md §5.1</c>; DL-103).
///
/// <para>
/// Não há cache partilhada entre execuções: o conjunto é construído a partir de
/// uma leitura em lote ao catálogo no início de cada Run. Políticas de âmbitos
/// mais específicos substituem por inteiro as menos específicas (não há merge
/// campo-a-campo nesta wave).
/// </para>
/// </summary>
public sealed class RecognitionPolicySet
{
    private readonly IReadOnlyDictionary<string, RecognitionPolicy> _groups;
    private readonly IReadOnlyDictionary<string, RecognitionPolicy> _channels;

    public RecognitionPolicySet(
        RecognitionPolicy? global = null,
        IReadOnlyDictionary<string, RecognitionPolicy>? groups = null,
        IReadOnlyDictionary<string, RecognitionPolicy>? channels = null,
        RecognitionPolicy? systemDefault = null)
    {
        Global = global;
        SystemDefault = systemDefault ?? RecognitionPolicy.Default;
        _groups = groups ?? new Dictionary<string, RecognitionPolicy>(StringComparer.Ordinal);
        _channels = channels ?? new Dictionary<string, RecognitionPolicy>(StringComparer.Ordinal);
    }

    /// <summary>Política global explícita; <c>null</c> quando não existe (usa-se o default).</summary>
    public RecognitionPolicy? Global { get; }

    /// <summary>Default do sistema, usado quando não existe camada aplicável.</summary>
    public RecognitionPolicy SystemDefault { get; }

    public int GroupOverrideCount => _groups.Count;
    public int ChannelOverrideCount => _channels.Count;

    public static RecognitionPolicySet Default { get; } = new();

    /// <summary>
    /// Resolve a política efectiva: canal → grupo → global → system/default.
    /// Chaves nulas/vazias saltam o respectivo passo.
    /// </summary>
    public RecognitionPolicy Resolve(string? canonicalChannelKey, string? groupKey)
        => ResolveWithScope(canonicalChannelKey, groupKey, out _);

    /// <summary>Igual a <see cref="Resolve"/>, expondo o âmbito efectivo.</summary>
    public RecognitionPolicy ResolveWithScope(
        string? canonicalChannelKey,
        string? groupKey,
        out RecognitionPolicyScope scope)
    {
        if (!string.IsNullOrWhiteSpace(canonicalChannelKey)
            && _channels.TryGetValue(canonicalChannelKey.Trim(), out var channelPolicy))
        {
            scope = RecognitionPolicyScope.Channel;
            return channelPolicy;
        }

        if (!string.IsNullOrWhiteSpace(groupKey)
            && _groups.TryGetValue(groupKey.Trim(), out var groupPolicy))
        {
            scope = RecognitionPolicyScope.Group;
            return groupPolicy;
        }

        if (Global is not null)
        {
            scope = RecognitionPolicyScope.Global;
            return Global;
        }

        scope = RecognitionPolicyScope.System;
        return SystemDefault;
    }
}
