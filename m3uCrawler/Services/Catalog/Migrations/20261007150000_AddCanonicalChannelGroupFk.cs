using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <summary>
    /// Wave A, step 1 — FK <c>canonical_channels.GroupId</c> →
    /// <c>canonical_groups.Id</c>.
    ///
    /// <para>
    /// <b>Aditiva e preservadora de comportamento.</b> Introduce a
    /// coluna <c>GroupId</c> (nullable), o índice e a FK; o enum
    /// <c>EditorialGroup</c> permanece intacto e deixa de ser a fonte
    /// configurável apenas numa wave posterior.
    /// </para>
    ///
    /// <para>
    /// <b>Seed idempotente + backfill.</b> Semeia os 9 grupos canónicos
    /// correspondentes ao enum <c>CanonicalEditorialGroup</c>
    /// (<c>INSERT OR IGNORE</c> por <c>Key</c> único) e faz backfill de
    /// <c>canonical_channels.GroupId</c> a partir do valor inteiro do
    /// enum. Um guard aborta a migration (rollback transaccional) se
    /// alguma linha ficar com <c>GroupId IS NULL</c>. O mapeamento
    /// duplica (necessariamente, em SQL) o de
    /// <see cref="CanonicalGroupDefaults"/> — manter os dois em
    /// sincronia.
    /// </para>
    ///
    /// <para>
    /// <b>Reversibilidade.</b> O <c>Down</c> remove FK, índice e coluna.
    /// Os grupos semeados <b>não</b> são removidos: podem ter sido
    /// adoptados/editados pelo operador, referenciados por
    /// <c>group_mappings</c> ou tornar-se a base da próxima wave; removê-los
    /// exigiria guards adicionais sem benefício. A remoção fica para a
    /// migration que retirar o enum.
    /// </para>
    /// </summary>
    public partial class AddCanonicalChannelGroupFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "GroupId",
                table: "canonical_channels",
                type: "INTEGER",
                nullable: true);

            // Seed dos 9 grupos (idempotente por Key) + backfill por enum
            // + guard de NULL. Duplica CanonicalGroupDefaults (C#) —
            // ver o comentário de classe.
            migrationBuilder.Sql(
                """
                INSERT OR IGNORE INTO canonical_groups
                    (Key, DisplayName, Country, "Order", IsEnabled, IsDefault, CreatedAtUtc, UpdatedAtUtc)
                VALUES
                    ('pt-generalistas',   'PortugalLive',             NULL, 0, 1, 1, strftime('%Y-%m-%dT%H:%M:%fZ','now'), strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                    ('pt-filmes-series',  'PortugalFilmes24_7',       NULL, 1, 1, 0, strftime('%Y-%m-%dT%H:%M:%fZ','now'), strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                    ('pt-entretenimento', 'PortugalEntretenimento',   NULL, 2, 1, 0, strftime('%Y-%m-%dT%H:%M:%fZ','now'), strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                    ('pt-desporto',       'PortugalDesporto',         NULL, 3, 1, 0, strftime('%Y-%m-%dT%H:%M:%fZ','now'), strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                    ('pt-infantil',       'PortugalInfantil',         NULL, 4, 1, 0, strftime('%Y-%m-%dT%H:%M:%fZ','now'), strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                    ('pt-documentarios',  'PortugalDocumentarios',    NULL, 5, 1, 0, strftime('%Y-%m-%dT%H:%M:%fZ','now'), strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                    ('pt-ppv',            'PortugalPPV',              NULL, 6, 1, 0, strftime('%Y-%m-%dT%H:%M:%fZ','now'), strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                    ('international',     'Foreign',                  NULL, 7, 1, 0, strftime('%Y-%m-%dT%H:%M:%fZ','now'), strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                    ('other',             'Other',                    NULL, 8, 1, 0, strftime('%Y-%m-%dT%H:%M:%fZ','now'), strftime('%Y-%m-%dT%H:%M:%fZ','now'));

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

                DROP TABLE IF EXISTS "__wavea_group_guard";
                CREATE TEMP TABLE "__wavea_group_guard" (
                    "value" INTEGER NOT NULL,
                    CONSTRAINT "WAVE-A group: canonical_channels com GroupId NULL apos o backfill" CHECK ("value" = 1)
                );
                INSERT INTO "__wavea_group_guard" ("value")
                SELECT CASE WHEN EXISTS (
                    SELECT 1 FROM canonical_channels WHERE GroupId IS NULL
                ) THEN 0 ELSE 1 END;
                DROP TABLE "__wavea_group_guard";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_canonical_channels_GroupId",
                table: "canonical_channels",
                column: "GroupId");

            migrationBuilder.AddForeignKey(
                name: "FK_canonical_channels_canonical_groups_GroupId",
                table: "canonical_channels",
                column: "GroupId",
                principalTable: "canonical_groups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Os grupos semeados NÃO são removidos (ver doc de classe):
            // podem ter sido editados/adoptados ou referenciados por
            // group_mappings. A remoção fica para a wave que retirar o
            // enum EditorialGroup.
            migrationBuilder.DropForeignKey(
                name: "FK_canonical_channels_canonical_groups_GroupId",
                table: "canonical_channels");

            migrationBuilder.DropIndex(
                name: "IX_canonical_channels_GroupId",
                table: "canonical_channels");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "canonical_channels");
        }
    }
}
