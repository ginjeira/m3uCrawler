using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddSourcesAndChannelSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sources",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Origin = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    LastDiscoveryAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastValidationAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "channel_sources",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CanonicalChannelId = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceId = table.Column<long>(type: "INTEGER", nullable: false),
                    StreamUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    ExternalStreamId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Quality = table.Column<int>(type: "INTEGER", nullable: false),
                    Epg = table.Column<int>(type: "INTEGER", nullable: false),
                    Availability = table.Column<int>(type: "INTEGER", nullable: false),
                    MatchConfidence = table.Column<double>(type: "REAL", nullable: false),
                    MatchMethod = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastTestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastResponseTimeMs = table.Column<long>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_sources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_channel_sources_canonical_channels_CanonicalChannelId",
                        column: x => x.CanonicalChannelId,
                        principalTable: "canonical_channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_channel_sources_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_channel_sources_CanonicalChannelId_SourceId",
                table: "channel_sources",
                columns: new[] { "CanonicalChannelId", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_channel_sources_SourceId",
                table: "channel_sources",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_sources_Key",
                table: "sources",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_sources");

            migrationBuilder.DropTable(
                name: "sources");
        }
    }
}
