using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <summary>
    /// Wave A, step 1 — FK <c>canonical_channels.GroupId</c> →
    /// <c>canonical_groups.Id</c>.
    ///
    /// <para>
    /// <b>Aditiva e sem seed.</b> Introduz a coluna <c>GroupId</c>
    /// (nullable), o índice e a FK <c>GroupId</c> →
    /// <c>canonical_groups.Id</c>. <b>Não</b> cria nem semeia grupos: a
    /// criação de grupos é exclusivamente do utilizador (UI/API) ou do
    /// import explícito de um baseline que os declare (Wave A, ownership
    /// das listas). O enum <c>EditorialGroup</c> é removido numa migration
    /// posterior.
    /// </para>
    ///
    /// <para>
    /// <b>Reversibilidade.</b> O <c>Down</c> remove FK, índice e coluna.
    /// Nenhum grupo é removido, porque esta migration já não cria nenhum.
    /// </para>
    /// </summary>
    public partial class AddCanonicalChannelGroupFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTA (Wave A): esta é a edição de uma migration JÁ APLICADA.
            // Para bases de dados existentes não volta a correr (as
            // migrations aplicadas não são re-executadas), pelo que a
            // remoção do seed não tem efeito nelas; para bases NOVAS deixa
            // de semear os 9 grupos canónicos. Nenhum grupo é criado
            // automaticamente — a criação é exclusiva do utilizador
            // (UI/API) ou do import explícito de um baseline que os declare.
            migrationBuilder.AddColumn<long>(
                name: "GroupId",
                table: "canonical_channels",
                type: "INTEGER",
                nullable: true);

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
            // Nada além de FK/índice/coluna: esta migration não cria
            // grupos (Wave A), pelo que não há grupos a remover.
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
