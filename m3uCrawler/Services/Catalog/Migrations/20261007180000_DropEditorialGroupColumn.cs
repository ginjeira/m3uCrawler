using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <summary>
    /// Wave D2 — remove o enum legado <c>CanonicalEditorialGroup</c> e a
    /// coluna <c>canonical_channels.EditorialGroup</c>. O grupo canónico
    /// passa a ser identificado exclusivamente pela <c>Key</c> do
    /// <see cref="CanonicalGroupEntity"/> e pela FK
    /// <c>canonical_channels.GroupId</c>.
    ///
    /// <para>
    /// <b>Backfill defensivo.</b> A migration anterior
    /// (<c>AddCanonicalChannelGroupFk</c>) já semeou os 9 grupos e fez
    /// backfill de <c>GroupId</c> a partir do enum, com guard contra
    /// <c>NULL</c>. Aqui repete-se o mapeamento por precaução (bases que
    /// possam ter ficado num estado intermédio) antes de remover a coluna.
    /// O mapeamento duplica (necessariamente, em SQL) o de
    /// <c>CanonicalGroupDefaults</c> — manter os dois em sincronia.
    /// </para>
    ///
    /// <para>
    /// <b>Down best-effort.</b> Recria a coluna <c>EditorialGroup</c>
    /// (<c>INTEGER NOT NULL DEFAULT 8</c>, i.e. <c>Other</c>) e mapeia de
    /// volta a partir de <c>Group.Key</c>. Grupos configuráveis que não
    /// correspondam a um valor histórico do enum caem em <c>Other</c>.
    /// </para>
    /// </summary>
    public partial class DropEditorialGroupColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill defensivo (idempotente): qualquer canal com
            // GroupId NULL neste ponto é mapeado do enum legado para a
            // Key do grupo correspondente. Não faz nada se o backfill da
            // migration anterior já tiver corrido.
            migrationBuilder.Sql(
                """
                UPDATE canonical_channels
                SET GroupId = (
                    SELECT g.Id FROM canonical_groups g
                    WHERE g.Key = CASE canonical_channels.EditorialGroup
                        WHEN 0 THEN 'pt-generalistas'
                        WHEN 1 THEN 'pt-filmes-series'
                        WHEN 2 THEN 'pt-entretenimento'
                        WHEN 3 THEN 'pt-desporto'
                        WHEN 4 THEN 'pt-infantil'
                        WHEN 5 THEN 'pt-documentarios'
                        WHEN 6 THEN 'pt-ppv'
                        WHEN 7 THEN 'international'
                        ELSE 'other'
                    END)
                WHERE GroupId IS NULL;
                """);

            // Remoção da coluna editorial legada. O provider SQLite do EF
            // Core executa um rebuild da tabela preservando dados, índices
            // e FKs (GroupId → canonical_groups).
            migrationBuilder.DropColumn(
                name: "EditorialGroup",
                table: "canonical_channels");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EditorialGroup",
                table: "canonical_channels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 8);

            // Best-effort: mapeia de volta a partir da Key do grupo
            // referenciado por GroupId. Chaves sem correspondência
            // histórica permanecem no default (Other = 8).
            migrationBuilder.Sql(
                """
                UPDATE canonical_channels
                SET EditorialGroup = COALESCE((
                    SELECT CASE g.Key
                        WHEN 'pt-generalistas' THEN 0
                        WHEN 'pt-filmes-series' THEN 1
                        WHEN 'pt-entretenimento' THEN 2
                        WHEN 'pt-desporto' THEN 3
                        WHEN 'pt-infantil' THEN 4
                        WHEN 'pt-documentarios' THEN 5
                        WHEN 'pt-ppv' THEN 6
                        WHEN 'international' THEN 7
                        ELSE 8
                    END
                    FROM canonical_groups g WHERE g.Id = canonical_channels.GroupId), 8);
                """);
        }
    }
}
