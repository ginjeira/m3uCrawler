using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_records",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ActorType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ActorName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    ObjectType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    ObjectId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    BeforeJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    AfterJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Result = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_records", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_records_ObjectId",
                table: "audit_records",
                column: "ObjectId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_records_ObjectType",
                table: "audit_records",
                column: "ObjectType");

            migrationBuilder.CreateIndex(
                name: "IX_audit_records_OccurredAtUtc",
                table: "audit_records",
                column: "OccurredAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_records");
        }
    }
}
