using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddAffinityGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "affinity_groups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CanonicalChannelId = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_affinity_groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_affinity_groups_canonical_channels_CanonicalChannelId",
                        column: x => x.CanonicalChannelId,
                        principalTable: "canonical_channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "affinity_members",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NormalizedMember = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AffinityGroupId = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_affinity_members", x => x.Id);
                    table.ForeignKey(
                        name: "FK_affinity_members_affinity_groups_AffinityGroupId",
                        column: x => x.AffinityGroupId,
                        principalTable: "affinity_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_affinity_groups_CanonicalChannelId",
                table: "affinity_groups",
                column: "CanonicalChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_affinity_groups_Name",
                table: "affinity_groups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_affinity_members_AffinityGroupId",
                table: "affinity_members",
                column: "AffinityGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_affinity_members_NormalizedMember",
                table: "affinity_members",
                column: "NormalizedMember",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "affinity_members");

            migrationBuilder.DropTable(
                name: "affinity_groups");
        }
    }
}
