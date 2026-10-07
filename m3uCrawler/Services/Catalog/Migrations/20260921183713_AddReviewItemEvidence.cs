using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewItemEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RunId",
                table: "review_items",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SourceId",
                table: "review_items",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StreamFingerprint",
                table: "review_items",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StreamFingerprintVersion",
                table: "review_items",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StreamUrl",
                table: "review_items",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RunId",
                table: "review_items");

            migrationBuilder.DropColumn(
                name: "SourceId",
                table: "review_items");

            migrationBuilder.DropColumn(
                name: "StreamFingerprint",
                table: "review_items");

            migrationBuilder.DropColumn(
                name: "StreamFingerprintVersion",
                table: "review_items");

            migrationBuilder.DropColumn(
                name: "StreamUrl",
                table: "review_items");
        }
    }
}
