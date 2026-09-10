using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace m3uCrawler.Services.Catalog;

/// <summary>
/// Importa o catálogo canónico baseline (JSON versionado em
/// <c>docs/catalog/m3ucrawler_pt_canonical_catalog.json</c>) para
/// a persistência SQLite do projecto. A operação é:
///
/// <list type="bullet">
///   <item><b>Idempotente</b>: reimportar não duplica canais nem
///         aliases (chave de upsert por <c>Key</c> e
///         <c>NormalizedAlias</c>).</item>
///   <item><b>Aditiva</b>: canais que existem no seed programático
///         (<see cref="CatalogSeed"/>) mas não no baseline são
///         preservados (e vice-versa). Não há eliminação destrutiva.</item>
///   <item><b>Auditável</b>: devolve um relatório
///         <see cref="CatalogBaselineImportReport"/> com
///         contadores de canais criados, actualizados e aliases
///         adicionados. Não escreve nada fora do que é necessário
///         para o seed.</item>
/// </list>
///
/// <para>
/// Esta classe é parte de <b>PHASE 1 — Canonical Catalogue</b>
/// descrita em <c>docs/IMPLEMENTATION_ROADMAP.md</c>. Não substitui
/// <see cref="CatalogSeed"/>; coexiste com ele. O baseline
/// canónico versionado é a fonte primária do seed PT, e o seed
/// programático permanece como fallback de identidades
/// estabilizadas antes da introdução do baseline JSON.
/// </para>
/// </summary>
public static class CatalogBaselineImporter
{
    /// <summary>
    /// Resolve o <see cref="EditorialCategory"/> heurística a
    /// partir do <c>group id</c> do baseline (e.g.
    /// <c>pt-desporto</c> → <see cref="EditorialCategory.Desporto"/>).
    /// Mantida determinística e isolada para permitir testes.
    /// </summary>
    public static EditorialCategory ResolveCategoryFromGroupId(string? groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return EditorialCategory.Live;
        var g = groupId.ToLowerInvariant();
        if (g.Contains("desporto")) return EditorialCategory.Desporto;
        if (g.Contains("infantil")) return EditorialCategory.Infantil;
        if (g.Contains("documentario")) return EditorialCategory.Documentarios;
        // Generalistas + internacional + filmes + entretenimento → Live
        return EditorialCategory.Live;
    }

    /// <summary>
    /// Resolve o <see cref="CanonicalEditorialGroup"/> a partir do
    /// prefixo do <c>group.id</c> do baseline. Mantido determinístico
    /// — qualquer mapping novo deve ser adicionado aqui e coberto
    /// por teste.
    /// </summary>
    public static CanonicalEditorialGroup ResolveEditorialGroup(string? groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return CanonicalEditorialGroup.Other;
        var g = groupId.ToLowerInvariant();
        if (g == "pt-generalistas" || g.StartsWith("pt-generalistas"))
            return CanonicalEditorialGroup.PortugalLive;
        if (g == "pt-desporto" || g.StartsWith("pt-desporto"))
            return CanonicalEditorialGroup.PortugalDesporto;
        if (g == "pt-infantil" || g.StartsWith("pt-infantil"))
            return CanonicalEditorialGroup.PortugalInfantil;
        if (g == "pt-filmes-series" || g.StartsWith("pt-filmes-series"))
            return CanonicalEditorialGroup.PortugalFilmes24_7;
        if (g == "pt-documentarios" || g.StartsWith("pt-documentarios"))
            return CanonicalEditorialGroup.PortugalDocumentarios;
        if (g == "pt-entretenimento" || g.StartsWith("pt-entretenimento"))
            return CanonicalEditorialGroup.PortugalEntretenimento;
        if (g == "pt-tematicos" || g.StartsWith("pt-tematicos"))
            return CanonicalEditorialGroup.PortugalEntretenimento;
        if (g == "pt-musica" || g.StartsWith("pt-musica"))
            return CanonicalEditorialGroup.PortugalEntretenimento;
        if (g.StartsWith("pt-") || g.StartsWith("radio-pt"))
            return CanonicalEditorialGroup.PortugalLive;
        if (g.StartsWith("international"))
            return CanonicalEditorialGroup.Foreign;
        if (g == "adultos" || g.StartsWith("adultos"))
            return CanonicalEditorialGroup.PortugalPPV;
        if (g.StartsWith("vod-"))
            return CanonicalEditorialGroup.PortugalFilmes24_7;
        return CanonicalEditorialGroup.Other;
    }

    /// <summary>
    /// Converte o <c>canonical_id</c> do baseline (formato
    /// <c>pt.rtpnoticias</c>) em <c>Key</c> interno (formato
    /// <c>rtpnoticias</c> ou com hífen quando há múltiplas
    /// partes, e.g. <c>pt.sic.k</c> → <c>sic-k</c>) usado
    /// pelo <see cref="CanonicalChannelEntity.Key"/> e por
    /// <see cref="CatalogSeed"/>. Remove o prefixo country
    /// (primeiro segmento de 2-char ISO-like).
    /// </summary>
    public static string CanonicalIdToKey(string canonicalId)
    {
        if (string.IsNullOrWhiteSpace(canonicalId)) return string.Empty;
        var lower = canonicalId.Trim().ToLowerInvariant();
        var parts = lower.Split('.');
        // Remove prefixo país se for 2-char ISO-like.
        var remaining = parts.Length >= 2 && parts[0].Length == 2
            ? parts.Skip(1).ToArray()
            : parts;
        // Junta segmentos restantes com hífen. Para o baseline PT
        // real (e.g. "pt.sicnoticias" → "sicnoticias") isto preserva
        // o slug tal como está; para IDs multi-segmento como
        // "pt.sic.k" produz "sic-k".
        return string.Join("-", remaining);
    }

    /// <summary>
    /// Resolve o grupo editorial de um canal a partir do baseline.
    /// Olha para o <see cref="MatchingBaseline.Examples"/> e
    /// casa pelo número de position no <see cref="CatalogBaseline.Numbering"/>
    /// com os <see cref="GroupBaseline.Range"/>s declarados.
    /// </summary>
    public static (CanonicalEditorialGroup Group, EditorialCategory Category) ResolveEditorialFromBaseline(
        CatalogBaseline baseline,
        ChannelBaseline channelBaseline)
    {
        // Heurística simples: a maioria dos canais PT não tem
        // mapping directo número→grupo, então usamos um fallback
        // baseado nas aliases (e.g. "rtp noticias" sugere
        // generalistas). Para já, mapeamos pela presença de
        // palavras-chave nas aliases.
        var nameLower = channelBaseline.Name.ToLowerInvariant();
        foreach (var alias in channelBaseline.Aliases)
        {
            var a = alias.ToLowerInvariant();
            if (a.Contains("noticias") || a.Contains("generalistas")) return (CanonicalEditorialGroup.PortugalLive, EditorialCategory.Live);
            if (a.Contains("desporto") || a.Contains("sport")) return (CanonicalEditorialGroup.PortugalDesporto, EditorialCategory.Desporto);
            if (a.Contains("infantil") || a.Contains("kids")) return (CanonicalEditorialGroup.PortugalInfantil, EditorialCategory.Infantil);
            if (a.Contains("documentario") || a.Contains("history")) return (CanonicalEditorialGroup.PortugalDocumentarios, EditorialCategory.Documentarios);
        }
        if (nameLower.Contains("noticias") || nameLower.Contains("news")) return (CanonicalEditorialGroup.PortugalLive, EditorialCategory.Live);
        if (nameLower.Contains("sport")) return (CanonicalEditorialGroup.PortugalDesporto, EditorialCategory.Desporto);
        if (nameLower.Contains("infantil") || nameLower.Contains("kids")) return (CanonicalEditorialGroup.PortugalInfantil, EditorialCategory.Infantil);
        if (nameLower.Contains("documentario")) return (CanonicalEditorialGroup.PortugalDocumentarios, EditorialCategory.Documentarios);
        // Fallback: canais sem keyword de categoria entram no
        // bucket "Entretenimento" (PT default).
        return (CanonicalEditorialGroup.PortugalEntretenimento, EditorialCategory.Entretenimento);
    }

    /// <summary>
    /// Lê o JSON do baseline a partir de um caminho de ficheiro
    /// versionado.
    /// </summary>
    public static async Task<CatalogBaseline> LoadFromFileAsync(string jsonPath, CancellationToken ct = default)
    {
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException(
                $"Catálogo baseline não encontrado: {jsonPath}", jsonPath);

        await using var stream = File.OpenRead(jsonPath);
        var baseline = await JsonSerializer.DeserializeAsync<CatalogBaseline>(
            stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }, ct);

        if (baseline is null)
            throw new InvalidOperationException(
                $"Catálogo baseline vazio ou inválido: {jsonPath}");

        return baseline;
    }

    /// <summary>
    /// Importa o baseline lido para o contexto de catálogo.
    /// Operação idempotente: nunca duplica; nunca elimina canais
    /// ou aliases que já existem. Devolve um relatório com
    /// contadores e warnings.
    /// </summary>
    public static async Task<CatalogBaselineImportReport> ImportAsync(
        ChannelCatalogDbContext context,
        CatalogBaseline baseline,
        CancellationToken ct = default)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (baseline is null) throw new ArgumentNullException(nameof(baseline));

        var report = new CatalogBaselineImportReport
        {
            CatalogId = baseline.CatalogId,
            Version = baseline.Version,
            Country = baseline.Country,
        };

        if (baseline.Matching?.Examples is null || baseline.Matching.Examples.Count == 0)
        {
            report.Warnings.Add("Baseline sem 'matching.examples' — nada a importar.");
            await context.SaveChangesAsync(ct);
            return report;
        }

        var now = DateTime.UtcNow;

        // Indexar canais existentes por Key.
        var existingChannels = await context.CanonicalChannels
            .Include(c => c.Aliases)
            .ToListAsync(ct);
        var channelsByKey = existingChannels.ToDictionary(c => c.Key, StringComparer.Ordinal);

        // Indexar aliases existentes por forma normalizada.
        var existingAliases = new HashSet<string>(
            existingChannels.SelectMany(c => c.Aliases.Select(a => a.NormalizedAlias)),
            StringComparer.Ordinal);

        foreach (var (sourceKey, channelBaseline) in baseline.Matching.Examples)
        {
            var key = CanonicalIdToKey(channelBaseline.CanonicalId);
            if (string.IsNullOrEmpty(key))
            {
                report.SkippedChannels++;
                report.Warnings.Add($"baseline entry '{sourceKey}' sem canonical_id utilizável.");
                continue;
            }

            var (group, category) = ResolveEditorialFromBaseline(baseline, channelBaseline);

            if (!channelsByKey.TryGetValue(key, out var channel))
            {
                channel = new CanonicalChannelEntity
                {
                    Key = key,
                    DisplayName = channelBaseline.Name,
                    EditorialCategory = category,
                    EditorialGroup = group,
                    PublicationPolicy = PublicationPolicy.CreateEligible,
                    IsEnabled = true,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                context.CanonicalChannels.Add(channel);
                channelsByKey[key] = channel;
                report.ChannelsCreated++;
            }
            else
            {
                // Actualizar DisplayName se o baseline fornece um
                // nome mais canónico. Não alteramos Group/Category
                // se já foram decididos pelo operador ou por uma
                // versão anterior do seed.
                var displayNameChanged = !string.Equals(
                    channel.DisplayName, channelBaseline.Name, StringComparison.Ordinal);
                if (displayNameChanged)
                {
                    channel.DisplayName = channelBaseline.Name;
                    channel.UpdatedAtUtc = now;
                    report.ChannelsUpdated++;
                }
            }

            // Aliases: ignorar a entrada "principal" (que tem o
            // mesmo nome que DisplayName); é redundante e gera
            // colisões entre CanonicalName e um alias.
            var channelNameNormalized = channel.DisplayName.Trim().ToLowerInvariant();
            foreach (var alias in channelBaseline.Aliases)
            {
                if (string.IsNullOrWhiteSpace(alias)) continue;
                var normalized = alias.Trim().ToLowerInvariant();
                if (normalized.Length == 0) continue;
                if (normalized == channelNameNormalized)
                {
                    // Alias idêntico ao nome do canal: redundante,
                    // ignorado. Contado em Skipped (informativo).
                    report.AliasesSkipped++;
                    continue;
                }

                if (existingAliases.Contains(normalized))
                {
                    report.AliasesSkipped++;
                    continue;
                }

                channel.Aliases.Add(new ChannelAliasEntity
                {
                    NormalizedAlias = normalized,
                    CanonicalChannelId = channel.Id,
                    CreatedAtUtc = now,
                });
                existingAliases.Add(normalized);
                report.AliasesAdded++;
            }
        }

        await context.SaveChangesAsync(ct);
        return report;
    }
}

/// <summary>
/// Resultado de uma operação de importação. Sem credenciais, sem
/// URLs; apenas contadores e warnings textuais.
/// </summary>
public sealed class CatalogBaselineImportReport
{
    public string CatalogId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;

    public int ChannelsCreated { get; set; }
    public int ChannelsUpdated { get; set; }
    public int SkippedChannels { get; set; }
    public int AliasesAdded { get; set; }
    public int AliasesSkipped { get; set; }

    public List<string> Warnings { get; } = new();

    public int TotalChanges => ChannelsCreated + ChannelsUpdated + AliasesAdded;
}
