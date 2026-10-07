using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddRecognitionPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "recognition_policies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ScopeKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    CanonicalChannelKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    GroupKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    FuzzyEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    FuzzyThreshold = table.Column<int>(type: "INTEGER", nullable: true),
                    FuzzyAmbiguityMargin = table.Column<int>(type: "INTEGER", nullable: true),
                    FuzzyWeightsJson = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recognition_policies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "recognition_policy_snapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ResolverVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PoliciesJson = table.Column<string>(type: "TEXT", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recognition_policy_snapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_recognition_policies_ScopeKey",
                table: "recognition_policies",
                column: "ScopeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recognition_policy_snapshots_RunId",
                table: "recognition_policy_snapshots",
                column: "RunId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recognition_policies");

            migrationBuilder.DropTable(
                name: "recognition_policy_snapshots");
        }
    }
}
