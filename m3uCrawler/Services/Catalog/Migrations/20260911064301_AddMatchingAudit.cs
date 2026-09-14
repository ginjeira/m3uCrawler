using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchingAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "matching_audits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NormalizedIdentity = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    OriginalTitle = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    SourceGroup = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ResolutionKind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CanonicalChannelId = table.Column<long>(type: "INTEGER", nullable: true),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    ReasonSignature = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_matching_audits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_matching_audits_AtUtc",
                table: "matching_audits",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_matching_audits_CanonicalChannelId",
                table: "matching_audits",
                column: "CanonicalChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_matching_audits_NormalizedIdentity",
                table: "matching_audits",
                column: "NormalizedIdentity");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "matching_audits");
        }
    }
}
