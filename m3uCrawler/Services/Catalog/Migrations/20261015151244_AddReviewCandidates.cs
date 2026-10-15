using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "review_candidates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ReviewItemId = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceId = table.Column<long>(type: "INTEGER", nullable: true),
                    StreamUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Group = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Logo = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    TvgId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    StreamFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    StreamFingerprintVersion = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_candidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_review_candidates_review_items_ReviewItemId",
                        column: x => x.ReviewItemId,
                        principalTable: "review_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_review_candidates_ReviewItemId",
                table: "review_candidates",
                column: "ReviewItemId");

            migrationBuilder.CreateIndex(
                name: "IX_review_candidates_ReviewItemId_StreamFingerprint",
                table: "review_candidates",
                columns: new[] { "ReviewItemId", "StreamFingerprint" },
                unique: true,
                filter: "\"StreamFingerprint\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_review_candidates_ReviewItemId_StreamUrl",
                table: "review_candidates",
                columns: new[] { "ReviewItemId", "StreamUrl" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "review_candidates");
        }
    }
}
