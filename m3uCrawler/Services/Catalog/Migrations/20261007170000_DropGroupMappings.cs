using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <summary>
    /// Wave D1 — remove a funcionalidade de <c>GroupMapping</c>
    /// (source group-title → grupo canónico), obsoleta depois de o grupo
    /// passar a pertencer ao canal canónico (Waves A–C). A tabela
    /// <c>group_mappings</c> deixa de existir; a entidade, os endpoints e
    /// os métodos do resolver foram removidos na mesma wave.
    ///
    /// <para>
    /// O enum <c>CanonicalEditorialGroup</c> e a coluna
    /// <c>canonical_channels.EditorialGroup</c> <b>não</b> são tocados
    /// (Wave D2). A entidade <c>CanonicalGroupEntity</c> permanece.
    /// </para>
    ///
    /// <para>
    /// <b>Irreversível em dados.</b> O <c>Down</c> recria apenas o schema
    /// (tabela, FK e índices); as linhas eliminadas no <c>Up</c> não são
    /// recuperáveis.
    /// </para>
    /// </summary>
    public partial class DropGroupMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guard: a tabela pode já não existir (instalação criada depois
            // da remoção). O IF EXISTS torna a migration idempotente nesse
            // caso. Usa-se SQL directo porque o DropTable do EF não emite
            // IF EXISTS.
            migrationBuilder.Sql("DROP TABLE IF EXISTS group_mappings;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Recria a tabela exactamente como em AddImportPoliciesAndGroups.
            // Não recupera dados: o Up elimina as linhas de mapping.
            migrationBuilder.CreateTable(
                name: "group_mappings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceKind = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceGroupTitle = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    CanonicalGroupId = table.Column<long>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_mappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_group_mappings_canonical_groups_CanonicalGroupId",
                        column: x => x.CanonicalGroupId,
                        principalTable: "canonical_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_group_mappings_CanonicalGroupId",
                table: "group_mappings",
                column: "CanonicalGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_group_mappings_SourceKind_SourceGroupTitle",
                table: "group_mappings",
                columns: new[] { "SourceKind", "SourceGroupTitle" },
                unique: true);
        }
    }
}
