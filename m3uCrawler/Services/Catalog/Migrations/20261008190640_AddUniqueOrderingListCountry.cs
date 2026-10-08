using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueOrderingListCountry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DC-11a — índice parcial único sobre ordering_lists.Country, com
            // colação NOCASE para que 'PT' e 'pt' colidam (unicidade de país
            // case-insensitive). EF Core 9.0.0 não expõe `collation` em
            // `MigrationBuilder.CreateIndex` nem colação a nível de índice, pelo
            // que se emite SQL explícito. É aditivo (cria apenas um índice) e
            // não provoca rebuild da tabela — ao contrário de alterar a colação
            // da coluna via `UseCollation`, que reconstruiria a tabela.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_ordering_lists_Country\" " +
                "ON \"ordering_lists\" (\"Country\" COLLATE NOCASE) " +
                "WHERE \"Country\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DROP INDEX IF EXISTS \"IX_ordering_lists_Country\";");
        }
    }
}
