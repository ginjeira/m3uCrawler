using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncRunSteps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sync_run_steps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SyncRunId = table.Column<long>(type: "INTEGER", nullable: false),
                    Step = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    ItemsProcessed = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemsSucceeded = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemsFailed = table.Column<int>(type: "INTEGER", nullable: false),
                    Result = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_run_steps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sync_run_steps_sync_runs_SyncRunId",
                        column: x => x.SyncRunId,
                        principalTable: "sync_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sync_run_steps_SyncRunId_Step",
                table: "sync_run_steps",
                columns: new[] { "SyncRunId", "Step" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sync_run_steps");
        }
    }
}
