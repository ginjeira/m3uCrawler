using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class MakeAffinityGroupNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_affinity_groups_canonical_channels_CanonicalChannelId",
                table: "affinity_groups");

            migrationBuilder.AlterColumn<long>(
                name: "CanonicalChannelId",
                table: "affinity_groups",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<string>(
                name: "CountryCode",
                table: "affinity_groups",
                type: "TEXT",
                maxLength: 10,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_affinity_groups_CountryCode",
                table: "affinity_groups",
                column: "CountryCode");

            migrationBuilder.AddForeignKey(
                name: "FK_affinity_groups_canonical_channels_CanonicalChannelId",
                table: "affinity_groups",
                column: "CanonicalChannelId",
                principalTable: "canonical_channels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_affinity_groups_canonical_channels_CanonicalChannelId",
                table: "affinity_groups");

            migrationBuilder.DropIndex(
                name: "IX_affinity_groups_CountryCode",
                table: "affinity_groups");

            migrationBuilder.DropColumn(
                name: "CountryCode",
                table: "affinity_groups");

            migrationBuilder.AlterColumn<long>(
                name: "CanonicalChannelId",
                table: "affinity_groups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_affinity_groups_canonical_channels_CanonicalChannelId",
                table: "affinity_groups",
                column: "CanonicalChannelId",
                principalTable: "canonical_channels",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
