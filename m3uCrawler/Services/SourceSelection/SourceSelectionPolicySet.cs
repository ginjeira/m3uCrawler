using System;
using System.Collections.Generic;

namespace m3uCrawler.Services.SourceSelection;

/// <summary>
/// PHASE 13 (Wave 13-4b) — Fornecedor da política efectiva de selecção de
/// fontes para um canal canónico. Implementações podem devolver a global ou
/// um override por canal.
/// </summary>
public interface ISourceSelectionPolicyProvider
{
    /// <summary>
    /// Resolve a política efectiva para o canal canónico indicado. Uma chave
    /// nula/vazia resolve para a política global.
    /// </summary>
    SourceSelectionPolicy Resolve(string? canonicalChannelKey);
}

/// <summary>
/// PHASE 13 (Wave 13-4b) — Snapshot em memória, por execução, das políticas
/// de selecção de fontes (global + overrides por canal). Não há cache
/// persistente nem partilhada entre execuções: o conjunto é construído a
/// partir de uma leitura em lote ao catálogo no início de cada run
/// (<see cref="SourceSelectionPolicyResolver.LoadEffectivePoliciesAsync"/>).
///
/// <para>
/// Um override por canal é uma política <b>completa</b> que substitui a
/// global por inteiro (a entidade persistida não representa "campo não
/// definido"). Portanto <see cref="Resolve"/> devolve o override quando
/// existe e a global caso contrário — nunca faz merge campo a campo.
/// </para>
/// </summary>
public sealed class SourceSelectionPolicySet : ISourceSelectionPolicyProvider
{
    private readonly IReadOnlyDictionary<string, SourceSelectionPolicy> _overrides;

    public SourceSelectionPolicySet(
        SourceSelectionPolicy global,
        IReadOnlyDictionary<string, SourceSelectionPolicy>? overrides = null,
        bool hasExplicitGlobal = false)
    {
        Global = global ?? throw new ArgumentNullException(nameof(global));
        HasExplicitGlobal = hasExplicitGlobal;

        if (overrides is null || overrides.Count == 0)
        {
            _overrides = new Dictionary<string, SourceSelectionPolicy>(StringComparer.Ordinal);
        }
        else
        {
            // Normaliza para Ordinal independentemente do comparer do caller.
            var copy = new Dictionary<string, SourceSelectionPolicy>(overrides.Count, StringComparer.Ordinal);
            foreach (var kvp in overrides)
            {
                if (string.IsNullOrEmpty(kvp.Key)) continue;
                copy[kvp.Key] = kvp.Value;
            }
            _overrides = copy;
        }
    }

    /// <summary>Política global, usada quando não há override para o canal.</summary>
    public SourceSelectionPolicy Global { get; }

    /// <summary>
    /// PHASE 13 (Wave 13-5) — <c>true</c> quando a política global veio de uma
    /// linha persistida explícita, <c>false</c> quando é o default do sistema.
    /// Usado apenas para rotular o âmbito efectivo no preview.
    /// </summary>
    public bool HasExplicitGlobal { get; }

    /// <summary>Número de overrides por canal carregados neste snapshot.</summary>
    public int OverrideCount => _overrides.Count;

    /// <summary>
    /// PHASE 13 (Wave 13-5) — Indica se existe override para a chave canónica
    /// indicada. <c>false</c> para chave nula/vazia.
    /// </summary>
    public bool HasOverride(string? canonicalChannelKey)
        => !string.IsNullOrEmpty(canonicalChannelKey)
           && _overrides.ContainsKey(canonicalChannelKey);

    /// <summary>
    /// Resolve a política efectiva: override por canal quando a chave é
    /// não-vazia e existe, caso contrário a global.
    /// </summary>
    public SourceSelectionPolicy Resolve(string? canonicalChannelKey)
    {
        if (!string.IsNullOrEmpty(canonicalChannelKey)
            && _overrides.TryGetValue(canonicalChannelKey, out var policy))
        {
            return policy;
        }

        return Global;
    }

    /// <summary>Conjunto só com a política por defeito e sem overrides.</summary>
    public static SourceSelectionPolicySet Default { get; } =
        new(SourceSelectionDefaults.DefaultPolicy);

    /// <summary>
    /// Conjunto constante com a política indicada e sem overrides — usado
    /// pelo overload legado de <see cref="SourceSelectionStage.ApplyAsync"/>
    /// que recebe uma política única.
    /// </summary>
    public static SourceSelectionPolicySet Constant(SourceSelectionPolicy policy)
        => new(policy);
}
