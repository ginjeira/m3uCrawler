using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelSourceStreamFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Fingerprint",
                table: "channel_sources",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FingerprintVersion",
                table: "channel_sources",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_channel_sources_Channel_Source_Fingerprint",
                table: "channel_sources",
                columns: new[] { "CanonicalChannelId", "SourceId", "Fingerprint" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_channel_sources_Channel_Source_Fingerprint",
                table: "channel_sources");

            migrationBuilder.DropColumn(
                name: "Fingerprint",
                table: "channel_sources");

            migrationBuilder.DropColumn(
                name: "FingerprintVersion",
                table: "channel_sources");
        }
    }
}
