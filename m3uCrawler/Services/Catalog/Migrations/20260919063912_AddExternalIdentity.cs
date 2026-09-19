using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "external_identities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CanonicalChannelId = table.Column<long>(type: "INTEGER", nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    Namespace = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Origin = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_identities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_external_identities_canonical_channels_CanonicalChannelId",
                        column: x => x.CanonicalChannelId,
                        principalTable: "canonical_channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_external_identities_CanonicalChannelId",
                table: "external_identities",
                column: "CanonicalChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_external_identities_Namespace_Value",
                table: "external_identities",
                columns: new[] { "Namespace", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_external_identities_Value",
                table: "external_identities",
                column: "Value");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_identities");
        }
    }
}
