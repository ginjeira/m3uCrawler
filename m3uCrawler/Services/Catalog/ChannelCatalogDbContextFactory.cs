using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace m3uCrawler.Services.Catalog;

/// <summary>
/// Design-time factory para o <c>dotnet ef</c>. Sem ela o tooling
/// não consegue instanciar o <c>ChannelCatalogDbContext</c> para
/// gerar migrations. Em runtime, a factory não é usada — o
/// <c>ChannelCatalogBootstrapper</c> cria o contexto com
/// <c>DbContextOptions</c> explícitos.
/// </summary>
public sealed class ChannelCatalogDbContextFactory : IDesignTimeDbContextFactory<ChannelCatalogDbContext>
{
    public ChannelCatalogDbContext CreateDbContext(string[] args)
    {
        // Para tooling ef, o path da BD não importa — apontamos para
        // um ficheiro temporário que será descartado.
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "channel-catalog-design.db");
        var options = new DbContextOptionsBuilder<ChannelCatalogDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        return new ChannelCatalogDbContext(options);
    }
}
