using System;
using System.Collections.Generic;
using System.Linq;
using m3uCrawler.Services.Catalog;

namespace m3uCrawler.Services.SourceSelection;

/// <summary>
/// PHASE 13 (Wave 13-1) — Contrato puro da selecção de fontes de um canal.
/// </summary>
public interface IChannelSourceSelector
{
    /// <summary>
    /// Selecciona, de forma determinística, até
    /// <see cref="SourceSelectionPolicy.MaxSourcesPerChannel"/> fontes a
    /// partir de <paramref name="candidates"/>. Não faz I/O.
    /// </summary>
    SourceSelectionResult Select(
        IReadOnlyList<SelectionCandidate> candidates,
        SourceSelectionPolicy policy);
}

/// <summary>
/// PHASE 13 (Wave 13-1) — Algoritmo central de selecção/ranking de
/// <c>ChannelSource</c> para publicação no Dispatcharr.
///
/// <para>
/// Função pura <c>Candidates + Policy → SourceSelectionResult</c>, sem
/// filesystem, base de dados, HTTP, Dispatcharr, scheduler, Telegram ou
/// Dashboard. Determinística: o mesmo conjunto de candidatos e a mesma
/// política produzem exactamente a mesma selecção, ordem e motivos.
/// </para>
///
/// <para>
/// Ranking (todos os critérios já existentes no domínio, por ordem):
/// <list type="number">
///   <item><b>Availability</b> — Validated &gt; Reachable &gt; Discovered &gt; Timeout
///         (Dead/Unreachable são inelegíveis);</item>
///   <item><b>SourcePriority</b> descendente;</item>
///   <item><b>Quality</b> descendente (FourK &gt; UHD &gt; FHD &gt; HD &gt; SD &gt; Unknown);</item>
///   <item><b>Epg</b> — Available &gt; Unknown &gt; Unavailable;</item>
///   <item><b>LastResponseTimeMs</b> ascendente (0 = desconhecido, vai para o fim);</item>
///   <item><b>desempate estável</b> — URL normalizada (ordinal) → SourceId →
///         ExternalStreamId (ordinal) → <b>identidade total do candidato</b>
///         (Provider, URL ordinal, SourceId, ExternalStreamId, prioridade,
///         qualidade, EPG, disponibilidade, response time, IsWorking).</item>
/// </list>
/// O último critério é uma ordem total sobre todos os campos do candidato:
/// elimina qualquer dependência da ordem de entrada, mesmo em empates
/// extremos, sem usar hashes, referências de objecto ou aleatoriedade.
/// A URL <b>ordinal</b> é usada apenas como critério final e só distingue
/// candidatos que a URL normalizada não distingue (ex.: diferenças de
/// capitalização no path). Critérios do roadmap sem campo no candidato
/// (histórico/recência) ficam para waves seguintes.
/// </para>
///
/// <para>
/// Elegibilidade (define o domínio actual): URL http/https absoluta;
/// <see cref="SelectionCandidate.IsWorking"/> verdadeiro; Availability
/// não terminal (Dead/Unreachable). Qualquer outra combinação é
/// elegível (desconhecidos não são excluídos).
/// </para>
/// </summary>
public sealed class ChannelSourceSelector : IChannelSourceSelector
{
    /// <summary>
    /// Ordem total determinística sobre <see cref="SelectionCandidate"/>
    /// (Wave 13-1 hardening, R1). Compara todos os campos numa ordem fixa,
    /// sem hashes, referências de objecto ou aleatoriedade. Dois candidatos
    /// que comparem iguais são indistinguíveis em todos os campos expostos,
    /// pelo que a sua ordem relativa não é observável. Usado como critério
    /// final depois das chaves de ranking, garantindo que o resultado não
    /// depende da ordem de entrada.
    /// </summary>
    private static readonly IComparer<SelectionCandidate> CandidateIdentityComparer =
        Comparer<SelectionCandidate>.Create((a, b) =>
        {
            var c = string.CompareOrdinal(a.Provider.Key, b.Provider.Key);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.StreamUrl, b.StreamUrl);
            if (c != 0) return c;
            c = a.SourceId.CompareTo(b.SourceId);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.ExternalStreamId ?? string.Empty, b.ExternalStreamId ?? string.Empty);
            if (c != 0) return c;
            c = a.SourcePriority.CompareTo(b.SourcePriority);
            if (c != 0) return c;
            c = ((int)a.Quality).CompareTo((int)b.Quality);
            if (c != 0) return c;
            c = ((int)a.Epg).CompareTo((int)b.Epg);
            if (c != 0) return c;
            c = ((int)a.Availability).CompareTo((int)b.Availability);
            if (c != 0) return c;
            c = a.LastResponseTimeMs.CompareTo(b.LastResponseTimeMs);
            if (c != 0) return c;
            return a.IsWorking.CompareTo(b.IsWorking);
        });

    public SourceSelectionResult Select(
        IReadOnlyList<SelectionCandidate> candidates,
        SourceSelectionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.MaxSourcesPerChannel < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy), "MaxSourcesPerChannel deve ser >= 1.");
        }
        if (policy.MaxSourcesPerProvider is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy), "MaxSourcesPerProvider, quando definido, deve ser >= 1.");
        }

        var maxPerProvider = policy.MaxSourcesPerProvider ?? int.MaxValue;

        var prepared = candidates
            .Select(c => new Prepared(c, NormalizeUrl(c.StreamUrl)))
            .ToList();

        var eligible = prepared
            .Where(p => p.IneligibilityReason == null)
            .OrderBy(p => AvailabilityRank(p.Candidate.Availability))
            .ThenByDescending(p => p.Candidate.SourcePriority)
            .ThenByDescending(p => (int)p.Candidate.Quality)
            .ThenByDescending(p => EpgRank(p.Candidate.Epg))
            .ThenBy(p => ResponseTimeKey(p.Candidate.LastResponseTimeMs))
            .ThenBy(p => p.NormalizedUrl!, StringComparer.Ordinal)
            .ThenBy(p => p.Candidate.SourceId)
            .ThenBy(p => p.Candidate.ExternalStreamId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(p => p.Candidate, CandidateIdentityComparer)
            .ToList();

        var ineligible = prepared
            .Where(p => p.IneligibilityReason != null)
            .OrderBy(p => p.Candidate.StreamUrl, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Candidate, CandidateIdentityComparer)
            .ToList();

        // Deduplicação por URL normalizada, preservando o melhor rankeado.
        var deduped = new List<Prepared>(eligible.Count);
        var seenUrls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in eligible)
        {
            if (seenUrls.Add(p.NormalizedUrl!))
            {
                deduped.Add(p);
            }
            else
            {
                p.IsDuplicate = true;
            }
        }

        var selected = new List<SelectedSource>();
        var selectedItems = new HashSet<Prepared>();
        var selectedProviders = new HashSet<ProviderIdentity>();
        var perProvider = new Dictionary<ProviderIdentity, int>();

        bool ProviderHasRoom(ProviderIdentity provider) =>
            !perProvider.TryGetValue(provider, out var used) || used < maxPerProvider;

        void Take(Prepared p, string reason)
        {
            selected.Add(new SelectedSource(p.Candidate, selected.Count, reason));
            selectedItems.Add(p);
            selectedProviders.Add(p.Candidate.Provider);
            perProvider[p.Candidate.Provider] =
                perProvider.TryGetValue(p.Candidate.Provider, out var used) ? used + 1 : 1;
        }

        // Fase A — diversidade: um representante por fornecedor distinto.
        if (policy.PreferDistinctProviders)
        {
            foreach (var p in deduped)
            {
                if (selected.Count >= policy.MaxSourcesPerChannel) break;
                var provider = p.Candidate.Provider;
                if (selectedProviders.Contains(provider)) continue;
                if (!ProviderHasRoom(provider)) continue;
                Take(p, SelectionReasons.Diversity);
            }
        }

        // Fase B — preenchimento dos lugares restantes.
        foreach (var p in deduped)
        {
            if (selected.Count >= policy.MaxSourcesPerChannel) break;
            if (selectedItems.Contains(p)) continue;
            var provider = p.Candidate.Provider;
            if (!ProviderHasRoom(provider)) continue;
            if (selectedProviders.Contains(provider) && !policy.AllowFallbackToSameProvider) continue;
            Take(p, SelectionReasons.Fill);
        }

        var rejected = new List<RejectedSource>();
        foreach (var p in eligible)
        {
            if (selectedItems.Contains(p)) continue;
            rejected.Add(new RejectedSource(
                p.Candidate,
                p.IsDuplicate
                    ? SelectionReasons.DuplicateUrl
                    : RejectionReason(p, policy, maxPerProvider, selected.Count, selectedProviders, perProvider)));
        }
        foreach (var p in ineligible)
        {
            rejected.Add(new RejectedSource(p.Candidate, p.IneligibilityReason!));
        }

        return new SourceSelectionResult(selected, rejected, candidates.Count);
    }

    /// <summary>
    /// Motivo de rejeição de um candidato elegível não seleccionado.
    /// Precedência (ver <see cref="SelectionReasons"/>): limite do canal →
    /// limite por fornecedor → fallback desactivado. O ramo final é
    /// inalcançável por construção (a Fase B esgota os casos possíveis),
    /// pelo que <see cref="SelectionReasons.NotSelected"/> nunca é emitido.
    /// </summary>
    private static string RejectionReason(
        Prepared p,
        SourceSelectionPolicy policy,
        int maxPerProvider,
        int selectedCount,
        HashSet<ProviderIdentity> selectedProviders,
        IReadOnlyDictionary<ProviderIdentity, int> perProvider)
    {
        if (selectedCount >= policy.MaxSourcesPerChannel) return SelectionReasons.LimitReached;

        var provider = p.Candidate.Provider;
        if (perProvider.TryGetValue(provider, out var used) && used >= maxPerProvider)
        {
            return SelectionReasons.ProviderLimit;
        }
        if (selectedProviders.Contains(provider) && !policy.AllowFallbackToSameProvider)
        {
            return SelectionReasons.FallbackDisabled;
        }
        return SelectionReasons.NotSelected;
    }

    private static int AvailabilityRank(AvailabilityState state) => state switch
    {
        AvailabilityState.Validated => 0,
        AvailabilityState.Reachable => 1,
        AvailabilityState.Discovered => 2,
        AvailabilityState.Timeout => 3,
        _ => 4, // Dead/Unreachable são inelegíveis e não chegam aqui
    };

    private static int EpgRank(EpgState state) => state switch
    {
        EpgState.Available => 1,
        EpgState.Unknown => 0,
        _ => -1,
    };

    private static long ResponseTimeKey(long responseTimeMs) =>
        responseTimeMs <= 0 ? long.MaxValue : responseTimeMs;

    /// <summary>
    /// Chave de identidade de URL para deduplicação. Remove fragmento,
    /// normaliza scheme/host para minúsculas e remove a porta por
    /// omissão. Preserva path, query e userinfo (podem distinguir
    /// streams legítimas) — não é uma URL de apresentação e nunca deve
    /// ser logada/persistida.
    /// </summary>
    internal static string? NormalizeUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)) return null;
        if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        var defaultPort = scheme == "https" ? 443 : 80;
        var authority = uri.Port == defaultPort ? host : $"{host}:{uri.Port}";
        var userInfo = string.IsNullOrEmpty(uri.UserInfo) ? string.Empty : uri.UserInfo + "@";
        var path = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        return $"{scheme}://{userInfo}{authority}{path}{uri.Query}";
    }

    private sealed class Prepared
    {
        public Prepared(SelectionCandidate candidate, string? normalizedUrl)
        {
            Candidate = candidate;
            NormalizedUrl = normalizedUrl;
            IneligibilityReason = ComputeIneligibility(candidate, normalizedUrl);
        }

        public SelectionCandidate Candidate { get; }
        public string? NormalizedUrl { get; }
        public string? IneligibilityReason { get; }
        public bool IsDuplicate { get; set; }

        private static string? ComputeIneligibility(SelectionCandidate candidate, string? normalizedUrl)
        {
            if (normalizedUrl == null) return SelectionReasons.InvalidUrl;
            if (!candidate.IsWorking) return SelectionReasons.NotWorking;
            if (candidate.Availability is AvailabilityState.Dead or AvailabilityState.Unreachable)
            {
                return SelectionReasons.Unavailable;
            }
            return null;
        }
    }
}
