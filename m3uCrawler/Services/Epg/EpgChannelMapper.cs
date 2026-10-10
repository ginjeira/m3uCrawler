using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using m3uCrawler.Services.Catalog;

namespace m3uCrawler.Services.Epg;

/// <summary>
/// Um canal declarado num documento XMLTV (<c>&lt;channel id="…"&gt;</c>).
/// Apenas os campos necessários ao mapeamento são extraídos.
/// </summary>
/// <param name="Id">Valor do atributo <c>id</c> do <c>&lt;channel&gt;</c> (a
/// identidade EPG que será gravada como <c>tvg-id</c>).</param>
/// <param name="DisplayName">Primeiro <c>&lt;display-name&gt;</c> não vazio.</param>
/// <param name="IconSrc">Primeiro <c>&lt;icon src="…"/&gt;</c> encontrado.</param>
/// <param name="Lang">Atributo <c>lang</c> do <c>display-name</c> escolhido.</param>
public sealed record EpgChannel(string Id, string? DisplayName, string? IconSrc, string? Lang);

/// <summary>
/// Mapeamento inequívoco entre um canal canónico e um <c>id</c> de EPG.
/// </summary>
public sealed record EpgMapping(string ChannelKey, long CanonicalChannelId, string EpgId);

/// <summary>Canal canónico do país sem qualquer candidato na EPG.</summary>
public sealed record EpgUnmatchedChannel(string ChannelKey, string DisplayName);

/// <summary>
/// Canal canónico com mais de um candidato EPG no mesmo nível de preferência:
/// não é mapeado (reportado para decisão humana).
/// </summary>
public sealed record EpgAmbiguousChannel(string ChannelKey, IReadOnlyList<string> CandidateIds);

/// <summary>
/// Canal canónico cujo único candidato EPG só existe numa forma
/// não-normalizada (dotted/maiúsculas). Normalizá-la mudaria o valor e o
/// casamento por <c>tvg-id</c> pode falhar por case, pelo que <b>não</b> é
/// mapeado — fica "incerto".
/// </summary>
public sealed record EpgUncertainChannel(string ChannelKey, string CandidateId);

/// <summary>
/// Resultado de <see cref="EpgChannelMapper.Plan"/>. Determinístico para o
/// mesmo input.
/// </summary>
public sealed class EpgMappingPlan
{
    /// <summary>Mapeamentos inequívocos (canal canónico → id de EPG).</summary>
    public IReadOnlyList<EpgMapping> Mappings { get; init; } = Array.Empty<EpgMapping>();

    /// <summary>Canais do país sem candidato na EPG.</summary>
    public IReadOnlyList<EpgUnmatchedChannel> Unmatched { get; init; } = Array.Empty<EpgUnmatchedChannel>();

    /// <summary>Canais com múltiplos candidatos (não mapeados).</summary>
    public IReadOnlyList<EpgAmbiguousChannel> Ambiguous { get; init; } = Array.Empty<EpgAmbiguousChannel>();

    /// <summary>Canais cujo único candidato é uma variante não-normalizada (não mapeados).</summary>
    public IReadOnlyList<EpgUncertainChannel> Uncertain { get; init; } = Array.Empty<EpgUncertainChannel>();

    /// <summary>Observações não-fatais (inclui os motivos dos incertos).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Mapeador canal canónico → id de EPG (XMLTV). Lógica pura, sem rede: o
/// parsing recebe um <see cref="Stream"/> já descomprimido e o plano trabalha
/// apenas sobre dados em memória (testável sem infraestrutura).
///
/// <para>
/// <b>Ressalva de case (importante).</b> O valor persistido como
/// <c>tvg-id</c> passa por <see cref="ExternalIdentityNormalizer.Normalize"/>,
/// que faz <c>lowercase</c>; é esse valor normalizado que o sync emite. Como
/// a EPG pode declarar <c>id</c>s dotted/PascalCase (ex.: <c>RTP.1.HD.pt</c>),
/// o mapeamento <b>prioriza</b> ids que já estejam na forma normalizada
/// (<c>Normalize(id) == id</c>) e <b>sem</b> <c>HD</c>. Se só existir a
/// variante dotted/maiúsculas, o canal é marcado como <see cref="EpgMappingPlan.Uncertain"/>
/// e <b>não</b> é gravado (evita gravar um valor que pode não casar por case).
/// </para>
/// </summary>
public static class EpgChannelMapper
{
    /// <summary>
    /// Extrai os canais (<c>&lt;channel&gt;</c>) de um documento XMLTV. Usa
    /// <see cref="XmlReader"/> em streaming (o ficheiro real tem ~32 MB
    /// comprimidos) e não carrega o documento completo em memória.
    /// </summary>
    public static async Task<IReadOnlyList<EpgChannel>> ParseAsync(
        Stream xmlStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(xmlStream);

        var channels = new List<EpgChannel>();
        var settings = new XmlReaderSettings
        {
            Async = true,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
            DtdProcessing = DtdProcessing.Ignore,
            CloseInput = false,
        };

        using var reader = XmlReader.Create(xmlStream, settings);

        var inChannel = false;
        var channelDepth = -1;
        string? id = null;
        string? displayName = null;
        string? displayLang = null;
        string? iconSrc = null;
        string? currentElement = null;
        var currentDepth = -1;
        var textBuffer = new StringBuilder();
        string? pendingLang = null;

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    var local = reader.LocalName;
                    if (!inChannel)
                    {
                        // Os canais XMLTV são filhos directos do elemento raiz
                        // (<tv>): exige-se Depth 1 para não confundir um
                        // <channel> aninhado (ex.: dentro de <programme>).
                        if (local == "channel" && reader.Depth == 1)
                        {
                            inChannel = true;
                            channelDepth = reader.Depth;
                            id = reader.GetAttribute("id");
                            displayName = null;
                            displayLang = null;
                            iconSrc = null;
                            currentElement = null;
                            textBuffer.Clear();
                            pendingLang = null;
                            if (reader.IsEmptyElement)
                            {
                                FinishChannel();
                            }
                        }

                        break;
                    }

                    if (local == "display-name")
                    {
                        currentElement = "display-name";
                        currentDepth = reader.Depth;
                        pendingLang = reader.GetAttribute("lang");
                        textBuffer.Clear();
                    }
                    else if (local == "icon")
                    {
                        var src = reader.GetAttribute("src");
                        if (string.IsNullOrWhiteSpace(iconSrc) && !string.IsNullOrWhiteSpace(src))
                        {
                            iconSrc = src.Trim();
                        }
                    }

                    break;
                }

                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                {
                    if (inChannel && currentElement == "display-name")
                    {
                        textBuffer.Append(reader.Value);
                    }

                    break;
                }

                case XmlNodeType.EndElement:
                {
                    if (!inChannel) break;

                    var local = reader.LocalName;
                    if (local == "channel" && reader.Depth == channelDepth)
                    {
                        FinishChannel();
                        break;
                    }

                    if (currentElement == "display-name"
                        && local == "display-name"
                        && reader.Depth == currentDepth)
                    {
                        var text = textBuffer.ToString().Trim();
                        if (string.IsNullOrWhiteSpace(displayName) && text.Length > 0)
                        {
                            displayName = text;
                            displayLang = pendingLang;
                        }

                        currentElement = null;
                    }

                    break;
                }
            }
        }

        return channels;

        void FinishChannel()
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                channels.Add(new EpgChannel(id!.Trim(), displayName, iconSrc, displayLang));
            }

            inChannel = false;
            channelDepth = -1;
            id = null;
            displayName = null;
            displayLang = null;
            iconSrc = null;
            currentElement = null;
            pendingLang = null;
        }
    }

    /// <summary>
    /// Constrói o plano de mapeamento. Para cada canal canónico do país
    /// (identificado por <see cref="CanonicalChannelEntity.Key"/>,
    /// <see cref="CanonicalChannelEntity.DisplayName"/> e
    /// <c>channel_aliases</c>), procura o <c>id</c> de EPG correspondente.
    /// </summary>
    /// <param name="countryCanonicalChannels">Canais canónicos do país.</param>
    /// <param name="epgChannels">Todos os canais extraídos da EPG.</param>
    /// <param name="countryCode">Código do país (ex.: <c>pt</c>); usado para
    /// remover o sufixo de país dos ids da EPG.</param>
    public static EpgMappingPlan Plan(
        IReadOnlyList<CanonicalChannelEntity> countryCanonicalChannels,
        IReadOnlyList<EpgChannel> epgChannels,
        string countryCode)
    {
        ArgumentNullException.ThrowIfNull(countryCanonicalChannels);
        ArgumentNullException.ThrowIfNull(epgChannels);

        var country = (countryCode ?? string.Empty).Trim().ToLowerInvariant();

        // 1. Indexar os ids da EPG: por forma normalizada e por forma sem HD.
        var byNormalized = new Dictionary<string, List<EpgChannel>>(StringComparer.Ordinal);
        var byNoHd = new Dictionary<string, List<EpgChannel>>(StringComparer.Ordinal);
        foreach (var epg in epgChannels)
        {
            if (epg is null || string.IsNullOrWhiteSpace(epg.Id)) continue;

            var key = MatchKey(epg.Id, country);
            if (key.Length == 0) continue;

            AddIndex(byNormalized, key, epg);

            var noHd = StripHdSuffix(key);
            AddIndex(byNoHd, noHd, epg);
        }

        var mappings = new List<EpgMapping>();
        var unmatched = new List<EpgUnmatchedChannel>();
        var ambiguous = new List<EpgAmbiguousChannel>();
        var uncertain = new List<EpgUncertainChannel>();
        var warnings = new List<string>();

        foreach (var channel in countryCanonicalChannels)
        {
            if (channel is null) continue;
            if (!IsCountryMatch(channel.Country, country)) continue;

            // 2. Reunir os candidatos que casam com qualquer identidade do canal.
            var matches = new List<EpgChannel>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidateKey in EnumerateCandidateKeys(channel, country))
            {
                if (candidateKey.Length == 0) continue;

                if (byNormalized.TryGetValue(candidateKey, out var exact))
                {
                    CollectCandidates(exact, matches, seen);
                }

                if (byNoHd.TryGetValue(candidateKey, out var relaxed))
                {
                    CollectCandidates(relaxed, matches, seen);
                }
            }

            if (matches.Count == 0)
            {
                unmatched.Add(new EpgUnmatchedChannel(channel.Key, channel.DisplayName));
                continue;
            }

            // 3. Preferência: (0) normalizado sem HD, (1) normalizado com HD,
            //    (2) variante não-normalizada (dotted/maiúsculas) — arriscada.
            var bestRank = matches.Min(m => Rank(m, country));
            var best = matches.Where(m => Rank(m, country) == bestRank).ToList();
            if (best.Count > 1)
            {
                ambiguous.Add(new EpgAmbiguousChannel(
                    channel.Key,
                    best.Select(b => b.Id).Distinct(StringComparer.Ordinal).ToList()));
                warnings.Add(
                    $"canal '{channel.Key}': {best.Count} candidatos EPG no mesmo nível de preferência; não mapeado.");
                continue;
            }

            var winner = best[0];
            if (bestRank == 2)
            {
                uncertain.Add(new EpgUncertainChannel(channel.Key, winner.Id));
                warnings.Add(
                    $"canal '{channel.Key}': único candidato '{winner.Id}' está numa forma não-normalizada " +
                    "(dotted/maiúsculas); não mapeado por risco de case.");
                continue;
            }

            mappings.Add(new EpgMapping(channel.Key, channel.Id, winner.Id));
            if (bestRank == 1)
            {
                warnings.Add(
                    $"canal '{channel.Key}': mapeado para a variante HD '{winner.Id}' " +
                    "(sem alternativa não-HD na EPG).");
            }
        }

        return new EpgMappingPlan
        {
            Mappings = mappings,
            Unmatched = unmatched,
            Ambiguous = ambiguous,
            Uncertain = uncertain,
            Warnings = warnings,
        };
    }

    private static void AddIndex(
        Dictionary<string, List<EpgChannel>> index, string key, EpgChannel channel)
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = new List<EpgChannel>();
            index[key] = list;
        }

        list.Add(channel);
    }

    private static void CollectCandidates(
        List<EpgChannel> source, List<EpgChannel> target, HashSet<string> seen)
    {
        foreach (var candidate in source)
        {
            if (seen.Add(candidate.Id))
            {
                target.Add(candidate);
            }
        }
    }

    private static IEnumerable<string> EnumerateCandidateKeys(
        CanonicalChannelEntity channel, string country)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var key = MatchKey(channel.Key, country);
        if (key.Length > 0 && seen.Add(key)) yield return key;

        var display = MatchKey(channel.DisplayName, country);
        if (display.Length > 0 && seen.Add(display)) yield return display;

        if (channel.Aliases is { Count: > 0 })
        {
            foreach (var alias in channel.Aliases)
            {
                if (alias is null) continue;
                var normalized = MatchKey(alias.NormalizedAlias, country);
                if (normalized.Length > 0 && seen.Add(normalized)) yield return normalized;
            }
        }
    }

    /// <summary>
    /// Nível de preferência de um id de EPG: 0 = normalizado e sem HD,
    /// 1 = normalizado mas com HD, 2 = não-normalizado (dotted/maiúsculas).
    /// </summary>
    private static int Rank(EpgChannel channel, string country)
    {
        var stable = string.Equals(
            ExternalIdentityNormalizer.Normalize(channel.Id), channel.Id, StringComparison.Ordinal);
        if (!stable) return 2;

        var hasHd = MatchKey(channel.Id, country).EndsWith("hd", StringComparison.Ordinal);
        return hasHd ? 1 : 0;
    }

    /// <summary>
    /// Forma de casamento: remove o sufixo de país da EPG, remove os
    /// separadores <c>.</c>/<c>-</c>/<c>_</c>/espaços e baixa para minúsculas.
    /// </summary>
    private static string MatchKey(string? rawId, string country)
    {
        if (string.IsNullOrWhiteSpace(rawId)) return string.Empty;

        var s = StripCountrySuffix(rawId.Trim(), country);

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is '.' or '-' or '_' or ' ' or '\t' or '\n' or '\r' or '/') continue;
            sb.Append(ch);
        }

        return sb.ToString().ToLowerInvariant();
    }

    private static string StripCountrySuffix(string id, string country)
    {
        if (country.Length == 0) return id;

        foreach (var separator in new[] { '.', '-', '_', ' ' })
        {
            var suffix = separator + country;
            if (id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return id[..^suffix.Length];
            }
        }

        return id;
    }

    private static string StripHdSuffix(string key)
        => key.EndsWith("hd", StringComparison.Ordinal) ? key[..^2] : key;

    private static bool IsCountryMatch(string? channelCountry, string country)
    {
        if (string.IsNullOrWhiteSpace(channelCountry)) return true;
        return string.Equals(channelCountry.Trim(), country, StringComparison.OrdinalIgnoreCase);
    }
}
