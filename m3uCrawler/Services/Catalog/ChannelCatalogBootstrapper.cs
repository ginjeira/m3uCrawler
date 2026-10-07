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
/// A primeira migration cria o schema + popula o seed (canais
/// canónicos, aliases, identity rules). Migrations futuras
/// adicionadas via <c>dotnet ef migrations add</c> são aplicadas
/// na ordem em que o EF Core as descobriu.
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
    /// copia de segurança se a versão do schema mudar, aplica
    /// migrations, popula seed idempotentemente). Idempotente:
    /// pode ser chamado em cada arranque.
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
            // migrations futuras. Para a primeira execução, a
            // migration inicial cria o schema e o seed.
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

        // O seed é parte da migration inicial (IdempotentSeed) —
        // não precisa de ser aplicado fora dela. Mas o método
        // SeedAsync fica aqui para suportar migrações manuais
        // (futuras) sem ter de criar nova migration.
        await SeedAsync(context, cancellationToken);

        // Baseline JSON versionado: se existir em docs/catalog/,
        // importa-o idempotentemente sobre o seed programático.
        // Este passo adiciona os canais e aliases da baseline
        // canónica portuguesa sem remover nada do seed legacy.
        await TryImportBaselineAsync(context, cancellationToken);

        // Wave B — normalização de aliases legacy. As instalações
        // pré-existentes podem ter aliases gravados em bruto (com
        // tokens de país/qualidade, maiúsculas, diacríticos). Sem
        // normalização esses aliases nunca são encontrados pelo
        // matcher. A operação é idempotente e collision-safe.
        await NormalizeExistingAliasesAsync(context, _logger, cancellationToken);

        lockStream?.Dispose();
        return context;
    }

    /// <summary>
    /// Importa o baseline canónico a partir do ficheiro resolvido; se
    /// não existir ficheiro, usa o recurso embutido na assembly. Esta
    /// segunda via garante que uma instalação fresca (imagem sem
    /// <c>docs/catalog/</c> no disco) continua a popular os canais PT
    /// generalistas. A operação é idempotente e aditiva — não remove
    /// nem duplica o que já existe.
    /// </summary>
    private async Task TryImportBaselineAsync(ChannelCatalogDbContext context, CancellationToken cancellationToken)
    {
        var baselinePath = ResolveBaselinePath();

        if (baselinePath is null)
        {
            // Antes: LogDebug (invisível em produção). Agora LogWarning
            // para o operador perceber que a baseline veio do recurso
            // embutido e não do ficheiro empacotado — mas nunca falha,
            // porque o recurso embutido está sempre disponível.
            _logger.LogWarning(
                "No baseline canonical catalog file found (checked M3U_BASELINE_PATH, " +
                "CWD/docs/catalog and AppContext.BaseDirectory/docs/catalog); " +
                "falling back to the embedded baseline resource.");
        }

        try
        {
            var baseline = await LoadBaselineAsync(baselinePath, cancellationToken);
            var report = await CatalogBaselineImporter.ImportAsync(context, baseline, cancellationToken);
            _logger.LogInformation(
                "Baseline import: source={Source} catalogId={CatalogId} version={Version} " +
                "channelsCreated={Created} channelsUpdated={Updated} aliasesAdded={AliasesAdded} " +
                "aliasesSkipped={AliasesSkipped} externalIdentitiesAdded={ExternalIdentitiesAdded} " +
                "externalIdentitiesSkipped={ExternalIdentitiesSkipped} externalIdentityConflicts={ExternalIdentityConflicts}",
                baselinePath ?? ("embedded:" + CatalogBaselineImporter.EmbeddedBaselineResourceName),
                report.CatalogId, report.Version, report.ChannelsCreated,
                report.ChannelsUpdated, report.AliasesAdded, report.AliasesSkipped,
                report.ExternalIdentitiesAdded, report.ExternalIdentitiesSkipped,
                report.ExternalIdentityConflicts);
            foreach (var warning in report.Warnings)
            {
                _logger.LogWarning("Baseline import warning: {Warning}", warning);
            }
        }
        catch (Exception ex)
        {
            // A baseline embutida deve estar sempre presente; uma falha
            // aqui é anómala e fica visível, mas não aborta o arranque
            // (o seed programático continua activo).
            _logger.LogWarning(ex,
                "Failed to load/import the baseline canonical catalog (path={Path}). " +
                "The programmed seed is still active.",
                baselinePath ?? "<embedded>");
        }
    }

    /// <summary>
    /// Decide a origem do baseline: ficheiro quando
    /// <paramref name="resolvedPath"/> é não-nulo (precedência), caso
    /// contrário o recurso embutido. Interno para permitir testes
    /// determinísticos da precedência sem depender do filesystem do
    /// repositório.
    /// </summary>
    internal static Task<CatalogBaseline> LoadBaselineAsync(string? resolvedPath, CancellationToken cancellationToken = default)
        => resolvedPath is null
            ? CatalogBaselineImporter.LoadEmbeddedAsync(cancellationToken)
            : CatalogBaselineImporter.LoadFromFileAsync(resolvedPath, cancellationToken);

    /// <summary>
    /// Resolve o caminho do ficheiro baseline usando o ambiente real
    /// (CWD, directório da assembly, env var).
    /// </summary>
    private string? ResolveBaselinePath()
        => ResolveBaselinePathFor(
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable("M3U_BASELINE_PATH"));

    /// <summary>
    /// Resolve o caminho do ficheiro baseline com entradas
    /// explícitas. Ordem de resolução (maior precedência primeiro):
    /// <list type="number">
    ///   <item>Override via variável de ambiente
    ///         <c>M3U_BASELINE_PATH</c> (usado em produção no
    ///         Dockerfile).</item>
    ///   <item><c>&lt;CWD&gt;/docs/catalog/m3ucrawler_pt_canonical_catalog.json</c>.</item>
    ///   <item><c>&lt;AppContext.BaseDirectory&gt;/docs/catalog/…</c>
    ///         (ficheiro empacotado em <c>/app/docs/catalog</c>).</item>
    ///   <item>Raiz do repo a partir do
    ///         <c>BaseDirectory</c> (5 e 4 níveis acima), para
    ///         desenvolvimento/testes.</item>
    /// </list>
    /// Devolve <c>null</c> quando nenhum existe; nesse caso o caller
    /// usa o recurso embutido.
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
    /// Aplica o seed de forma idempotente. Em condições normais a
    /// migration inicial já popula o seed, mas este método
    /// suporta cenários em que o seed é reaplicado manualmente.
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
    /// garantia de grupos; o mapeamento em si vive em
    /// <see cref="CanonicalGroupDefaults"/>.
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
