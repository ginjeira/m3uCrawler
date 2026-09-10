using Microsoft.EntityFrameworkCore;

namespace m3uCrawler.Services.Catalog;

/// <summary>
/// Factory <c>IDbContextFactory&lt;ChannelCatalogDbContext&gt;</c>
/// para uso em runtime (não design-time). O
/// <c>ChannelCatalogDbContextFactory</c> já existe para o tooling
/// do <c>dotnet ef</c>; esta factory é a contrapartida para
/// <see cref="CatalogResolver"/> em produção e em testes de
/// integração do caminho real (Program + DispatcharrSyncService).
///
/// <para>
/// Cada chamada devolve um novo <c>DbContext</c> com
/// <c>Cache=Shared</c> desactivado (cada contexto possui o seu
/// próprio file handle) — equivalente ao
/// <c>TestDbContextFactory</c> dos testes, mas configurado para
/// um path persistente.
/// </para>
/// </summary>
public sealed class RuntimeChannelCatalogDbContextFactory
    : IDbContextFactory<ChannelCatalogDbContext>
{
    private readonly string _dbPath;

    public RuntimeChannelCatalogDbContextFactory(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            throw new System.ArgumentException(
                "dbPath required", nameof(dbPath));
        }
        _dbPath = dbPath;
    }

    public string DbPath => _dbPath;

    public ChannelCatalogDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ChannelCatalogDbContext>()
            // Cache=Private para que cada DbContext possua o seu
            // próprio file handle; necessário porque o
            // EF Core não é thread-safe e o ApplyAsync percorre
            // canais em paralelo via Task.WhenAll-like paths.
            .UseSqlite($"Data Source={_dbPath};Cache=Private")
            .Options;
        return new ChannelCatalogDbContext(options);
    }
}
