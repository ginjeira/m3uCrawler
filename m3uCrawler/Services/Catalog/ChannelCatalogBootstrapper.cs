using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using m3uCrawler.Services.Matching;

namespace m3uCrawler.Services.Catalog;

/// <summary>
/// Gerencia o ciclo de vida da BD SQLite de catálogo:
///   - cria o directório se necessário;
///   - aplica migrations idempotentemente (apenas as pendentes);
///   - antes de migrations destrutivas, cria uma cópia de
///     segurança com timestamp no mesmo directório;
///   - nunca recria, trunca ou substitui a BD existente;
///   - aborta o arranque com erro claro se a migration falhar;
///   - protege contra concorrência (segundo processo que tenta
///     migrar em paralelo é bloqueado por um lock de ficheiro).
///
/// <para>
/// A primeira migration cria apenas o schema. Wave A: o arranque não semeia
/// canais, aliases nem grupos — as listas são do utilizador ou de um import
/// explícito. Migrations futuras adicionadas via
/// <c>dotnet ef migrations add</c> são aplicadas na ordem em que o EF Core
/// as descobriu.
/// </para>
/// </summary>
public sealed class ChannelCatalogBootstrapper
{
    private readonly string _dbPath;
    private readonly ILogger _logger;

    public ChannelCatalogBootstrapper(string dbPath, ILogger? logger = null)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Inicializa a BD (cria directório, abre lock exclusivo,
    /// copia de segurança se a versão do schema mudar e aplica as
    /// migrations de schema). Wave A: não semeia canais, aliases nem
    /// grupos. Idempotente: pode ser chamado em cada arranque.
    /// </summary>
    public async Task<ChannelCatalogDbContext> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            _logger.LogInformation("Created catalog directory {Directory}", directory);
        }

        // Lock exclusivo de ficheiro para evitar duas migrations em
        // paralelo (sinaliza "outro processo está a migrar" se alguém
        // já tem o lock). Se outra instância já está a migrar,
        // esperamos que ela termine antes de tentar de novo.
        // Para SQLite in-memory (e.g. file:...?mode=memory&cache=shared)
        // o lock de ficheiro não faz sentido, por isso é opcional.
        string? lockPath = !_dbPath.Contains("mode=memory", StringComparison.OrdinalIgnoreCase)
            ? _dbPath + ".lock"
            : null;
        FileStream? lockStream = null;
        if (lockPath != null)
        {
            lockStream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        // Agora temos o lock exclusivo.

        // Verifica se a BD existe. Se existir E a versão do schema
        // for diferente, cria uma cópia de segurança.
        bool dbExists = File.Exists(_dbPath);
        if (dbExists)
        {
            await EnsureBackupOnSchemaChangeAsync(cancellationToken);
        }

        var options = new DbContextOptionsBuilder<ChannelCatalogDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        var context = new ChannelCatalogDbContext(options);

        try
        {
            // EnsureCreated vs Migrate: usamos Migrate para suportar
            // migrations futuras. Na primeira execução a migration inicial
            // cria apenas o schema — Wave A: sem seed de canais, aliases
            // ou grupos.
            await context.Database.MigrateAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Falha de migration deve abortar o arranque/sync com
            // erro claro. Não fazemos sincronização parcial.
            _logger.LogError(ex, "Migration failed for catalog DB at {Path}", _dbPath);
            await context.DisposeAsync();
            throw new InvalidOperationException(
                $"Catalog migration failed for '{_dbPath}'. " +
                "Startup/sync aborted; a non-partial run is required. " +
                "Inspect the previous logs, restore the latest .pre-migration-<ts>.db backup if needed, and re-run.",
                ex);
        }
        finally
        {
            lockStream?.Dispose();
        }

        // Wave A — as listas canónicas (canais, aliases, grupos) são
        // geridas exclusivamente pelo utilizador. O arranque aplica
        // apenas as migrations de schema; NÃO semeia canais do seed
        // programático, NÃO importa a baseline e NÃO cria grupos
        // canónicos por omissão. A provisão é explícita, uma única vez,
        // por acção do utilizador:
        //   • --import-canonical-baseline <ficheiro.json>
        //   • --import-country-config <ficheiro.json>
        //   • --normalize-canonical-aliases (manutenção explícita)
        // SeedAsync e NormalizeExistingAliasesAsync permanecem
        // disponíveis para uso explícito (CLI/testes) e nunca são
        // invocados no arranque.
        lockStream?.Dispose();
        return context;
    }

    /// <summary>
    /// Decide a origem do baseline: ficheiro quando
    /// <paramref name="resolvedPath"/> é não-nulo (precedência), caso
    /// contrário o recurso embutido. Interno para permitir testes
    /// determinísticos da precedência sem depender do filesystem do
    /// repositório. Não é invocado no arranque (Wave A).
    /// </summary>
    internal static Task<CatalogBaseline> LoadBaselineAsync(string? resolvedPath, CancellationToken cancellationToken = default)
        => resolvedPath is null
            ? CatalogBaselineImporter.LoadEmbeddedAsync(cancellationToken)
            : CatalogBaselineImporter.LoadFromFileAsync(resolvedPath, cancellationToken);

    /// <summary>
    /// Resolve o caminho do ficheiro baseline com entradas
    /// explícitas. Ordem de resolução (maior precedência primeiro):
    /// <list type="number">
    ///   <item>Override via variável de ambiente
    ///         <c>M3U_BASELINE_PATH</c>.</item>
    ///   <item><c>&lt;CWD&gt;/docs/catalog/m3ucrawler_pt_canonical_catalog.json</c>.</item>
    ///   <item><c>&lt;AppContext.BaseDirectory&gt;/docs/catalog/…</c>
    ///         (ficheiro empacotado em <c>/app/docs/catalog</c>).</item>
    ///   <item>Raiz do repo a partir do
    ///         <c>BaseDirectory</c> (5 e 4 níveis acima), para
    ///         desenvolvimento/testes.</item>
    /// </list>
    /// Devolve <c>null</c> quando nenhum existe. Wave A: já não é uma via de
    /// arranque — mantido apenas para testes determinísticos da resolução.
    /// </summary>
    internal static string? ResolveBaselinePathFor(
        string currentDirectory,
        string baseDirectory,
        string? envPath)
    {
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath)) return envPath;

        var fileName = CatalogBaselineImporter.BaselineFileName;
        var candidates = new[]
        {
            Path.Combine(currentDirectory, "docs", "catalog", fileName),
            Path.Combine(baseDirectory, "docs", "catalog", fileName),
            Path.Combine(baseDirectory, "..", "..", "..", "..", "..", "docs", "catalog", fileName),
            Path.Combine(baseDirectory, "..", "..", "..", "..", "docs", "catalog", fileName),
        };
        foreach (var c in candidates)
        {
            try
            {
                var full = Path.GetFullPath(c);
                if (File.Exists(full)) return full;
            }
            catch
            {
                // Ignorar caminhos inválidos; continuar a procurar.
            }
        }
        return null;
    }

    /// <summary>
    /// Se a BD existe mas a versão do schema mudou (i.e. há
    /// migrations pendentes que alteram schema), cria uma cópia
    /// de segurança com timestamp. Não substitui a BD.
    /// </summary>
    private async Task EnsureBackupOnSchemaChangeAsync(CancellationToken cancellationToken)
    {
        // Para verificar se há migrations pendentes sem aplicar,
        // abrimos a BD em modo read-only brevemente.
        var probe = new DbContextOptionsBuilder<ChannelCatalogDbContext>()
            .UseSqlite($"Data Source={_dbPath};Mode=ReadOnly")
            .Options;
        await using var ctx = new ChannelCatalogDbContext(probe);
        try
        {
            var pending = (await ctx.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            // Só faz backup se a migration for destrutiva (drop/create table).
            // Consideramos destrutiva qualquer migration que não seja
            // puramente aditiva. Como heurística conservadora:
            // qualquer migration pendente gera backup.
            if (pending.Count > 0)
            {
                var ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                var backupPath = _dbPath + $".pre-migration-{ts}.db";
                File.Copy(_dbPath, backupPath, overwrite: false);
                _logger.LogInformation(
                    "Created catalog DB pre-migration backup at {Backup}",
                    backupPath);
            }
        }
        catch
        {
            // Se não conseguir ler o schema (BD corrupta, lock,
            // etc.) não falhamos aqui — a MigrateAsync abaixo
            // produzirá o erro claro.
        }
    }

    /// <summary>
    /// Aplica o seed programático de forma idempotente. Wave A: <b>não</b>
    /// é invocado no arranque — só corre quando chamado explicitamente
    /// (testes/ferramentas). Cria os 9 grupos por omissão e os canais de
    /// <see cref="CatalogSeed"/>; a criação de listas é do utilizador ou de
    /// um import explícito.
    ///
    /// <para>
    /// Os aliases do seed são normalizados via
    /// <see cref="ChannelNormalizer.Normalize"/> antes de serem
    /// inseridos, para que uma instalação fresca fique imediatamente
    /// matchable. A normalização pode colapsar vários aliases numa
    /// única forma: esses duplicados são deduplicados; colisões com
    /// aliases já pertencentes a outro canal são ignoradas
    /// deterministicamente (o alias existente mantém o dono). As
    /// <c>Key</c> dos canais não são alteradas.
    /// </para>
    /// </summary>
    public static async Task SeedAsync(ChannelCatalogDbContext context, CancellationToken cancellationToken = default)
    {
        CatalogSeed.ValidateSeedConsistency();

        // Idempotente: insere apenas se não existir (match por
        // NormalizedAlias único).
        var existingAliases = await context.ChannelAliases
            .Select(a => a.NormalizedAlias)
            .ToListAsync(cancellationToken);
        var existingAliasSet = new System.Collections.Generic.HashSet<string>(
            existingAliases, System.StringComparer.Ordinal);

        var existingRules = await context.IdentityRules
            .Select(r => r.NormalizedIdentity)
            .ToListAsync(cancellationToken);
        var existingRuleSet = new System.Collections.Generic.HashSet<string>(
            existingRules, System.StringComparer.Ordinal);

        var existingChannelKeys = await context.CanonicalChannels
            .Select(c => c.Key)
            .ToListAsync(cancellationToken);
        var existingChannelKeySet = new System.Collections.Generic.HashSet<string>(
            existingChannelKeys, System.StringComparer.Ordinal);

        var now = DateTime.UtcNow;

        // Wave A — grupos canónicos configuráveis. Garantir os 9 grupos
        // por omissão (idempotente, por Key) ANTES de qualquer canal, para
        // que os canais possam ser criados já com GroupId. O mapeamento
        // vive em CanonicalGroupDefaults (única fonte C#).
        var groupIds = await EnsureCanonicalGroupsAsync(context, now, cancellationToken);

        foreach (var ch in CatalogSeed.Channels)
        {
            // Forma única matchable, deduplicada por canal.
            var channelAliases = new System.Collections.Generic.List<string>();
            var seenForChannel = new System.Collections.Generic.HashSet<string>(
                System.StringComparer.Ordinal);
            foreach (var rawAlias in ch.Aliases)
            {
                var normalizedAlias = ChannelNormalizer.Normalize(rawAlias);
                if (normalizedAlias.Length == 0) continue;
                if (seenForChannel.Add(normalizedAlias)) channelAliases.Add(normalizedAlias);
            }

            if (!existingChannelKeySet.Contains(ch.Key))
            {
                // TODO/ADR (Wave W3s): canais do seed programático ficam com
                // Country = null (o record CanonicalChannelSeed não tem
                // país). Não se introduz migration para os preencher: a
                // propriedade/ownership dos dados de país é um ADR em aberto
                // (docs/Reestructure/24-DECISIONS.md, "country data
                // ownership"). Country é apenas classificação, não
                // identidade. Fixado por CountryAttributeConsistencyTests.
                var entity = new CanonicalChannelEntity
                {
                    Key = ch.Key,
                    DisplayName = ch.DisplayName,
                    EditorialCategory = ch.Category,
                    GroupId = groupIds.TryGetValue(ch.GroupKey, out var groupId)
                            ? groupId
                            : null,
                    PublicationPolicy = ch.Policy,
                    IsEnabled = true,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                context.CanonicalChannels.Add(entity);
                foreach (var alias in channelAliases)
                {
                    if (!existingAliasSet.Add(alias)) continue;
                    context.ChannelAliases.Add(new ChannelAliasEntity
                    {
                        NormalizedAlias = alias,
                        CanonicalChannel = entity,
                        CreatedAtUtc = now,
                    });
                }
            }
            else
            {
                // Canal já existe: garantir aliases (caso uma migration
                // inicial tenha sido gerada antes deste seed).
                var existing = await context.CanonicalChannels
                    .Include(c => c.Aliases)
                    .FirstAsync(c => c.Key == ch.Key, cancellationToken);
                foreach (var alias in channelAliases)
                {
                    if (existing.Aliases.Any(a => a.NormalizedAlias == alias)) continue;
                    if (!existingAliasSet.Add(alias)) continue;
                    existing.Aliases.Add(new ChannelAliasEntity
                    {
                        NormalizedAlias = alias,
                        CanonicalChannelId = existing.Id,
                        CreatedAtUtc = now,
                    });
                }
            }
        }

        foreach (var rule in CatalogSeed.IdentityRules)
        {
            if (existingRuleSet.Contains(rule.NormalizedIdentity)) continue;
            context.IdentityRules.Add(new IdentityRuleEntity
            {
                NormalizedIdentity = rule.NormalizedIdentity,
                Disposition = rule.Disposition,
                Reason = rule.Reason,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Garante que os 9 grupos canónicos por omissão existem
    /// (idempotente, por <see cref="CanonicalGroupEntity.Key"/>) e
    /// devolve o mapa <c>Key → Id</c>. É a única implementação C# de
    /// garantia de grupos por omissão; o mapeamento em si vive em
    /// <see cref="CanonicalGroupDefaults"/>.
    ///
    /// <para>
    /// Wave A: sem caller de arranque — usada apenas pelo
    /// <see cref="SeedAsync"/> explícito (testes/ferramentas). O import de
    /// baseline <b>não</b> a usa: só cria os grupos que o ficheiro
    /// declarar.
    /// </para>
    /// </summary>
    internal static async Task<System.Collections.Generic.Dictionary<string, long>> EnsureCanonicalGroupsAsync(
        ChannelCatalogDbContext context,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var existing = await context.CanonicalGroups
            .AsNoTracking()
            .Select(g => new { g.Id, g.Key })
            .ToListAsync(cancellationToken);
        var idsByKey = new System.Collections.Generic.Dictionary<string, long>(
            System.StringComparer.Ordinal);
        foreach (var g in existing)
        {
            idsByKey[g.Key] = g.Id;
        }

        var added = false;
        foreach (var def in CanonicalGroupDefaults.All)
        {
            if (idsByKey.ContainsKey(def.Key)) continue;
            context.CanonicalGroups.Add(new CanonicalGroupEntity
            {
                Key = def.Key,
                DisplayName = def.DisplayName,
                Order = def.Order,
                IsEnabled = true,
                IsDefault = def.IsDefault,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
            added = true;
        }

        if (added)
        {
            await context.SaveChangesAsync(cancellationToken);
            idsByKey.Clear();
            var refreshed = await context.CanonicalGroups
                .AsNoTracking()
                .Select(g => new { g.Id, g.Key })
                .ToListAsync(cancellationToken);
            foreach (var g in refreshed)
            {
                idsByKey[g.Key] = g.Id;
            }
        }

        return idsByKey;
    }

    /// <summary>
    /// Wave B — normaliza in-place os <c>NormalizedAlias</c> de
    /// instalações pré-existentes para a forma matchable do
    /// <see cref="ChannelNormalizer.Normalize"/> (o que o matcher
    /// consulta). Idempotente e collision-safe:
    /// <list type="bullet">
    ///   <item>valor normalizado vazio ou já igual → sem alteração;</item>
    ///   <item>colisão com outro alias do MESMO canal → o alias
    ///         duplicado é removido (merge);</item>
    ///   <item>colisão com um alias de OUTRO canal → o alias existente
    ///         mantém o dono e o alias legacy é deixado como está
    ///         (skip determinístico; nunca lança).</item>
    /// </list>
    /// Devolve o número de aliases efectivamente alterados.
    /// </summary>
    internal static async Task<int> NormalizeExistingAliasesAsync(
        ChannelCatalogDbContext context,
        ILogger? logger,
        CancellationToken cancellationToken = default)
    {
        var aliases = await context.ChannelAliases
            .OrderBy(a => a.Id)
            .ToListAsync(cancellationToken);

        // value -> (aliasRowId, channelId) do dono actual. Mantido
        // actualizado à medida que renomeamos/removemos.
        var ownerByValue = new System.Collections.Generic.Dictionary<
            string, (long AliasId, long ChannelId)>(System.StringComparer.Ordinal);
        foreach (var alias in aliases)
        {
            ownerByValue[alias.NormalizedAlias] = (alias.Id, alias.CanonicalChannelId);
        }

        var changed = 0;
        var merged = 0;
        var skipped = 0;
        var toRemove = new System.Collections.Generic.List<ChannelAliasEntity>();

        foreach (var alias in aliases)
        {
            var current = alias.NormalizedAlias;
            var target = ChannelNormalizer.Normalize(current);
            if (target.Length == 0) continue;
            if (string.Equals(target, current, StringComparison.Ordinal)) continue;

            if (ownerByValue.TryGetValue(target, out var owner))
            {
                if (owner.ChannelId == alias.CanonicalChannelId)
                {
                    // Já existe a forma normalizada neste canal: o
                    // alias legacy é redundante → merge/remove.
                    toRemove.Add(alias);
                    ownerByValue.Remove(current);
                    merged++;
                    changed++;
                }
                else
                {
                    // Colisão com outro canal: não roubar identidade.
                    // Deixa o alias legacy como está (não-matchable,
                    // mas sem corromper o dono existente).
                    skipped++;
                }
                continue;
            }

            ownerByValue.Remove(current);
            alias.NormalizedAlias = target;
            ownerByValue[target] = (alias.Id, alias.CanonicalChannelId);
            changed++;
        }

        if (toRemove.Count > 0)
        {
            context.ChannelAliases.RemoveRange(toRemove);
        }

        if (changed > 0 || skipped > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        if (changed > 0)
        {
            logger?.LogWarning(
                "Channel alias normalization: {Changed} legacy aliases normalised " +
                "({Merged} merged, {Skipped} collision(s) skipped).",
                changed, merged, skipped);
        }
        else if (skipped > 0)
        {
            logger?.LogWarning(
                "Channel alias normalization: {Skipped} legacy alias collision(s) " +
                "skipped; no alias changed.",
                skipped);
        }

        return changed;
    }
}
