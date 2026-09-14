using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderingListsAndSourcePriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ordering_lists",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Country = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ordering_lists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "source_priority_policies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    CanonicalChannelId = table.Column<long>(type: "INTEGER", nullable: true),
                    CriteriaJson = table.Column<string>(type: "TEXT", nullable: false),
                    PreferredQuality = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    AllowFallback = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_priority_policies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ordering_items",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderingListId = table.Column<long>(type: "INTEGER", nullable: false),
                    CanonicalChannelId = table.Column<long>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ordering_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ordering_items_canonical_channels_CanonicalChannelId",
                        column: x => x.CanonicalChannelId,
                        principalTable: "canonical_channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ordering_items_ordering_lists_OrderingListId",
                        column: x => x.OrderingListId,
                        principalTable: "ordering_lists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ordering_items_CanonicalChannelId",
                table: "ordering_items",
                column: "CanonicalChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_ordering_items_OrderingListId_CanonicalChannelId",
                table: "ordering_items",
                columns: new[] { "OrderingListId", "CanonicalChannelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ordering_items_OrderingListId_Position",
                table: "ordering_items",
                columns: new[] { "OrderingListId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ordering_lists_Key",
                table: "ordering_lists",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_source_priority_policies_Scope",
                table: "source_priority_policies",
                column: "Scope",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ordering_items");

            migrationBuilder.DropTable(
                name: "source_priority_policies");

            migrationBuilder.DropTable(
                name: "ordering_lists");
        }
    }
}
