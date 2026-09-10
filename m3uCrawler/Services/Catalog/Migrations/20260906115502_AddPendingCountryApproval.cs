using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingCountryApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_country_approvals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NormalizedIdentity = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    OriginalTitle = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CountryCode = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    StreamUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    SourceGroup = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ReasonSignature = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_country_approvals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pending_country_approvals_CountryCode",
                table: "pending_country_approvals",
                column: "CountryCode");

            migrationBuilder.CreateIndex(
                name: "IX_pending_country_approvals_NormalizedIdentity",
                table: "pending_country_approvals",
                column: "NormalizedIdentity");

            migrationBuilder.CreateIndex(
                name: "IX_pending_country_approvals_State",
                table: "pending_country_approvals",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_country_approvals");
        }
    }
}
