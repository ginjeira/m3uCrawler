using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using m3uCrawler.Services.Catalog;
using m3uCrawler.Services.Security;

namespace m3uCrawler.Services.Configuration;

/// <summary>
/// CRUD genérico de segredos de aplicação (chave → valor cifrado) sobre
/// a mesma BD que o catálogo (<c>app_secrets</c>). O valor persistido é
/// sempre o resultado de <see cref="SecretProtector.Protect"/>; o valor
/// devolvido por <see cref="GetAsync"/> é sempre decifrado. Construído a
/// partir do <see cref="RuntimeChannelCatalogDbContextFactory"/>.
/// </summary>
public sealed class AppSecretStore
{
    private readonly IDbContextFactory<ChannelCatalogDbContext> _factory;
    private readonly SecretProtector _protector;

    public AppSecretStore(RuntimeChannelCatalogDbContextFactory factory, SecretProtector protector)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    /// <summary>Valor decifrado do segredo, ou <c>null</c> se não existir.</summary>
    public async Task<string?> GetAsync(string key)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));

        await using var context = await _factory.CreateDbContextAsync();
        var entity = await context.AppSecrets
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == key);

        return entity is null ? null : _protector.Unprotect(entity.Value);
    }

    /// <summary>
    /// Cria ou substitui o segredo (upsert), cifrando o valor e
    /// registando <c>UpdatedAtUtc</c> em UTC (ISO-8601).
    /// </summary>
    public async Task SetAsync(string key, string value)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        if (value is null) throw new ArgumentNullException(nameof(value));

        var payload = _protector.Protect(value);
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        await using var context = await _factory.CreateDbContextAsync();
        var entity = await context.AppSecrets.FirstOrDefaultAsync(s => s.Key == key);
        if (entity is null)
        {
            context.AppSecrets.Add(new AppSecretEntity
            {
                Key = key,
                Value = payload,
                UpdatedAtUtc = now,
            });
        }
        else
        {
            entity.Value = payload;
            entity.UpdatedAtUtc = now;
        }

        await context.SaveChangesAsync();
    }

    public async Task<bool> ExistsAsync(string key)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));

        await using var context = await _factory.CreateDbContextAsync();
        return await context.AppSecrets.AnyAsync(s => s.Key == key);
    }

    /// <summary>Remove o segredo, se existir (idempotente).</summary>
    public async Task DeleteAsync(string key)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));

        await using var context = await _factory.CreateDbContextAsync();
        var entity = await context.AppSecrets.FirstOrDefaultAsync(s => s.Key == key);
        if (entity is null) return;

        context.AppSecrets.Remove(entity);
        await context.SaveChangesAsync();
    }
}
