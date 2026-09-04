using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace m3uCrawler.Services.Catalog;

/// <summary>
/// Gera o payload SQL do seed a partir de <see cref="CatalogSeed"/>.
/// Usado pela migration inicial (que é gerada uma vez e depois
/// congelada) e por <c>ChannelCatalogBootstrapper.SeedAsync</c> (que
/// pode re-aplicar parcialmente se necessário). Mantém o seed
/// versionado num único ficheiro (este) em vez de espalhado em
/// SQL inline na migration.
/// </summary>
public static class CatalogSeedSqlBuilder
{
    public static string BuildAllInsertsSql()
    {
        CatalogSeed.ValidateSeedConsistency();
        var sb = new StringBuilder();
        var now = DateTime.UtcNow;
        var nowIso = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        sb.AppendLine("-- canonical_channels");
        long chId = 1;
        var aliasId = 1L;
        // We use a CTE-friendly approach: pre-assign Ids in the seed
        // file (so the aliases FK can reference them). EF/SQLite handle
        // explicit Id values on INSERT.
        var channelIdMap = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var ch in CatalogSeed.Channels)
        {
            sb.AppendLine(string.Format(
                "INSERT OR IGNORE INTO canonical_channels (Id, Key, DisplayName, EditorialCategory, EditorialGroup, PublicationPolicy, IsEnabled, CreatedAtUtc, UpdatedAtUtc) VALUES ({0}, '{1}', '{2}', {3}, {4}, {5}, 1, '{6}', '{6}');",
                chId, Escape(ch.Key), Escape(ch.DisplayName),
                (int)ch.Category, (int)ch.Group, (int)ch.Policy, nowIso));
            channelIdMap[ch.Key] = chId;
            chId++;
        }

        sb.AppendLine();
        sb.AppendLine("-- channel_aliases");
        foreach (var ch in CatalogSeed.Channels)
        {
            var mapped = channelIdMap[ch.Key];
            foreach (var alias in ch.Aliases)
            {
                sb.AppendLine(string.Format(
                    "INSERT OR IGNORE INTO channel_aliases (Id, NormalizedAlias, CanonicalChannelId, CreatedAtUtc) VALUES ({0}, '{1}', {2}, '{3}');",
                    aliasId++, Escape(alias), mapped, nowIso));
            }
        }

        sb.AppendLine();
        sb.AppendLine("-- identity_rules");
        long ruleId = 1;
        foreach (var rule in CatalogSeed.IdentityRules)
        {
            sb.AppendLine(string.Format(
                "INSERT OR IGNORE INTO identity_rules (Id, NormalizedIdentity, Disposition, Reason, CreatedAtUtc, UpdatedAtUtc) VALUES ({0}, '{1}', {2}, '{3}', '{4}', '{4}');",
                ruleId++, Escape(rule.NormalizedIdentity), (int)rule.Disposition, Escape(rule.Reason), nowIso));
        }

        return sb.ToString();
    }

    private static string Escape(string s)
    {
        return s.Replace("'", "''");
    }
}
