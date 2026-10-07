using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddLiveRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "live_run_runs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TerminalStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    LastMessage = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CountsJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_live_run_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "live_run_steps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LiveRunId = table.Column<long>(type: "INTEGER", nullable: false),
                    Phase = table.Column<int>(type: "INTEGER", nullable: false),
                    PhaseIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    PhaseStartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PhaseFinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Result = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_live_run_steps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_live_run_steps_live_run_runs_LiveRunId",
                        column: x => x.LiveRunId,
                        principalTable: "live_run_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_live_run_runs_FinishedAtUtc",
                table: "live_run_runs",
                column: "FinishedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_live_run_runs_RunId",
                table: "live_run_runs",
                column: "RunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_live_run_runs_StartedAtUtc",
                table: "live_run_runs",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_live_run_steps_LiveRunId_Phase",
                table: "live_run_steps",
                columns: new[] { "LiveRunId", "Phase" });

            migrationBuilder.CreateIndex(
                name: "IX_live_run_steps_LiveRunId_PhaseIndex",
                table: "live_run_steps",
                columns: new[] { "LiveRunId", "PhaseIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "live_run_steps");

            migrationBuilder.DropTable(
                name: "live_run_runs");
        }
    }
}
