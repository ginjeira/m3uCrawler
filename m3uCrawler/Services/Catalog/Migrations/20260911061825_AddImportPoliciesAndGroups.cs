using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddImportPoliciesAndGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "canonical_groups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Country = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canonical_groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "import_policies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MediaKind = table.Column<int>(type: "INTEGER", nullable: false),
                    VodPolicy = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetGroupsCsv = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    ExcludedGroupsCsv = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_policies", x => x.Id);
                });

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
                name: "IX_canonical_groups_Key",
                table: "canonical_groups",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_group_mappings_CanonicalGroupId",
                table: "group_mappings",
                column: "CanonicalGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_group_mappings_SourceKind_SourceGroupTitle",
                table: "group_mappings",
                columns: new[] { "SourceKind", "SourceGroupTitle" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_import_policies_MediaKind",
                table: "import_policies",
                column: "MediaKind",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_mappings");

            migrationBuilder.DropTable(
                name: "import_policies");

            migrationBuilder.DropTable(
                name: "canonical_groups");
        }
    }
}
