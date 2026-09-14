using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelSourceObservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "channel_source_observations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChannelSourceId = table.Column<long>(type: "INTEGER", nullable: false),
                    Quality = table.Column<int>(type: "INTEGER", nullable: false),
                    Epg = table.Column<int>(type: "INTEGER", nullable: false),
                    Availability = table.Column<int>(type: "INTEGER", nullable: false),
                    ResponseTimeMs = table.Column<long>(type: "INTEGER", nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_source_observations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_channel_source_observations_ChannelSourceId_ObservedAtUtc",
                table: "channel_source_observations",
                columns: new[] { "ChannelSourceId", "ObservedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_source_observations");
        }
    }
}
