using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scheduled_jobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    CronExpression = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    ActionName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastRunAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextRunAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastResult = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scheduled_jobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_jobs_Name",
                table: "scheduled_jobs",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scheduled_jobs");
        }
    }
}
