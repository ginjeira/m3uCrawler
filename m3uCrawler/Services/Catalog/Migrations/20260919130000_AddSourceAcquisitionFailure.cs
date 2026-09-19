using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <summary>
    /// W2 (2026-09-19) — adiciona campos de falha de aquisição à tabela
    /// <c>sources</c> (<c>docs/Reestructure/19-FAILURE-MODEL.md §6</c>).
    ///
    /// <para>
    /// <b>Não destrutiva.</b> Apenas adiciona colunas nullable. Não faz drop,
    /// truncate nem update de dados existentes; instalações existentes mantêm
    /// todas as rows e as Sources ficam com os novos campos a <c>NULL</c>.
    /// </para>
    /// </summary>
    public partial class AddSourceAcquisitionFailure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastAcquisitionFailureKind",
                table: "sources",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastAcquisitionFailureAtUtc",
                table: "sources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastAcquisitionHttpStatus",
                table: "sources",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastAcquisitionFailureDetail",
                table: "sources",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastAcquisitionFailureKind",
                table: "sources");

            migrationBuilder.DropColumn(
                name: "LastAcquisitionFailureAtUtc",
                table: "sources");

            migrationBuilder.DropColumn(
                name: "LastAcquisitionHttpStatus",
                table: "sources");

            migrationBuilder.DropColumn(
                name: "LastAcquisitionFailureDetail",
                table: "sources");
        }
    }
}
