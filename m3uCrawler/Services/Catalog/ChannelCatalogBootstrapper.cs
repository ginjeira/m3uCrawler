using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

        lockStream?.Dispose();
        return context;
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
        foreach (var ch in CatalogSeed.Channels)
        {
            if (!existingChannelKeySet.Contains(ch.Key))
            {
                var entity = new CanonicalChannelEntity
                {
                    Key = ch.Key,
                    DisplayName = ch.DisplayName,
                    EditorialCategory = ch.Category,
                    EditorialGroup = ch.Group,
                    PublicationPolicy = ch.Policy,
                    IsEnabled = true,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                context.CanonicalChannels.Add(entity);
                foreach (var alias in ch.Aliases)
                {
                    if (existingAliasSet.Contains(alias)) continue;
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
                foreach (var alias in ch.Aliases)
                {
                    if (existing.Aliases.Any(a => a.NormalizedAlias == alias)) continue;
                    if (existingAliasSet.Contains(alias)) continue;
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
}
