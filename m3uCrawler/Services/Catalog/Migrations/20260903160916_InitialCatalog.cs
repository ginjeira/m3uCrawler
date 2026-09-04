using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "canonical_channels",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    EditorialCategory = table.Column<int>(type: "INTEGER", nullable: false),
                    EditorialGroup = table.Column<int>(type: "INTEGER", nullable: false),
                    PublicationPolicy = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canonical_channels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "dispatcharr_stream_ownerships",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DispatcharrStreamId = table.Column<long>(type: "INTEGER", nullable: false),
                    DispatcharrChannelId = table.Column<long>(type: "INTEGER", nullable: false),
                    Ownership = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedBySyncRunId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dispatcharr_stream_ownerships", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "identity_rules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NormalizedIdentity = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Disposition = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_rules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sync_runs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AppVersion = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CountCreatedCrawlerManaged = table.Column<int>(type: "INTEGER", nullable: false),
                    CountMergedIntoExternal = table.Column<int>(type: "INTEGER", nullable: false),
                    CountProtectedExternalStreams = table.Column<int>(type: "INTEGER", nullable: false),
                    CountRemovedCrawlerManagedStreams = table.Column<int>(type: "INTEGER", nullable: false),
                    CountReviewRequired = table.Column<int>(type: "INTEGER", nullable: false),
                    CountExcluded = table.Column<int>(type: "INTEGER", nullable: false),
                    Result = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_runs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "channel_aliases",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NormalizedAlias = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CanonicalChannelId = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_aliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_channel_aliases_canonical_channels_CanonicalChannelId",
                        column: x => x.CanonicalChannelId,
                        principalTable: "canonical_channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "dispatcharr_channel_ownerships",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DispatcharrChannelId = table.Column<long>(type: "INTEGER", nullable: false),
                    Ownership = table.Column<int>(type: "INTEGER", nullable: false),
                    CanonicalChannelId = table.Column<long>(type: "INTEGER", nullable: true),
                    FirstObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Evidence = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dispatcharr_channel_ownerships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dispatcharr_channel_ownerships_canonical_channels_CanonicalChannelId",
                        column: x => x.CanonicalChannelId,
                        principalTable: "canonical_channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "review_items",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NormalizedIdentity = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SourceGroup = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ReasonSignature = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    ApprovedCanonicalChannelId = table.Column<long>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_review_items_canonical_channels_ApprovedCanonicalChannelId",
                        column: x => x.ApprovedCanonicalChannelId,
                        principalTable: "canonical_channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_canonical_channels_Key",
                table: "canonical_channels",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_channel_aliases_CanonicalChannelId",
                table: "channel_aliases",
                column: "CanonicalChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_channel_aliases_NormalizedAlias",
                table: "channel_aliases",
                column: "NormalizedAlias",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dispatcharr_channel_ownerships_CanonicalChannelId",
                table: "dispatcharr_channel_ownerships",
                column: "CanonicalChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_dispatcharr_channel_ownerships_DispatcharrChannelId",
                table: "dispatcharr_channel_ownerships",
                column: "DispatcharrChannelId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_dispatcharr_stream_ownerships_DispatcharrStreamId",
                table: "dispatcharr_stream_ownerships",
                column: "DispatcharrStreamId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_identity_rules_NormalizedIdentity",
                table: "identity_rules",
                column: "NormalizedIdentity",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_review_items_ApprovedCanonicalChannelId",
                table: "review_items",
                column: "ApprovedCanonicalChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_review_items_Fingerprint",
                table: "review_items",
                column: "Fingerprint",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_aliases");

            migrationBuilder.DropTable(
                name: "dispatcharr_channel_ownerships");

            migrationBuilder.DropTable(
                name: "dispatcharr_stream_ownerships");

            migrationBuilder.DropTable(
                name: "identity_rules");

            migrationBuilder.DropTable(
                name: "review_items");

            migrationBuilder.DropTable(
                name: "sync_runs");

            migrationBuilder.DropTable(
                name: "canonical_channels");
        }
    }
}
