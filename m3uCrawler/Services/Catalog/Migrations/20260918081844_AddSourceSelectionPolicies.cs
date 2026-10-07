using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceSelectionPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "source_selection_policies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScopeKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    CanonicalChannelKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    MaxSourcesPerChannel = table.Column<int>(type: "INTEGER", nullable: false),
                    PreferDistinctProviders = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxSourcesPerProvider = table.Column<int>(type: "INTEGER", nullable: true),
                    AllowFallbackToSameProvider = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_selection_policies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_source_selection_policies_ScopeKey",
                table: "source_selection_policies",
                column: "ScopeKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "source_selection_policies");
        }
    }
}
