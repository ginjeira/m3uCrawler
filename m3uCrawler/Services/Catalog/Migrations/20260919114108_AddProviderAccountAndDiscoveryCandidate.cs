using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <summary>
    /// W1 (2026-09-19) — introduz <c>providers</c>, <c>provider_accounts</c>
    /// e <c>discovery_candidates</c>, e adiciona a coluna nullable
    /// <c>sources.ProviderAccountId</c>.
    ///
    /// <para>
    /// <b>Não destrutiva (DL-107 / 16-PERSISTENCE §5).</b> Apenas cria
    /// tabelas/colunas/índices: não faz drop, truncate nem update de dados
    /// existentes. Instalações existentes mantêm todas as rows.
    /// </para>
    ///
    /// <para>
    /// <b>Limitação de migração semântica.</b> As Sources já existentes
    /// ficam com <c>ProviderAccountId = NULL</c>: a identidade funcional da
    /// conta não pode ser inferida a partir da evidência legada (source
    /// key/origin) sem inventar uma fórmula de identidade, o que a BÍBLIA
    /// proíbe (03-DISCOVERY §3). A associação é reconstruída de forma
    /// explícita/observada em execuções futuras; o histórico não é apagado.
    /// </para>
    /// </summary>
    public partial class AddProviderAccountAndDiscoveryCandidate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ProviderAccountId",
                table: "sources",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "providers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Capabilities = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_providers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "provider_accounts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProviderId = table.Column<long>(type: "INTEGER", nullable: false),
                    AccountKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CredentialsReference = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_accounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_provider_accounts_providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "discovery_candidates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProviderId = table.Column<long>(type: "INTEGER", nullable: true),
                    ProviderAccountId = table.Column<long>(type: "INTEGER", nullable: true),
                    ExternalIdentity = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    NormalizedIdentity = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Evidence = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SourceId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discovery_candidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_discovery_candidates_provider_accounts_ProviderAccountId",
                        column: x => x.ProviderAccountId,
                        principalTable: "provider_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_discovery_candidates_providers_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "providers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_discovery_candidates_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sources_ProviderAccountId",
                table: "sources",
                column: "ProviderAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_discovery_candidates_NormalizedIdentity",
                table: "discovery_candidates",
                column: "NormalizedIdentity");

            migrationBuilder.CreateIndex(
                name: "IX_discovery_candidates_ProviderAccountId",
                table: "discovery_candidates",
                column: "ProviderAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_discovery_candidates_ProviderId",
                table: "discovery_candidates",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_discovery_candidates_RunId",
                table: "discovery_candidates",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_discovery_candidates_RunId_ProviderAccountId",
                table: "discovery_candidates",
                columns: new[] { "RunId", "ProviderAccountId" },
                unique: true,
                filter: "\"ProviderAccountId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_discovery_candidates_SourceId",
                table: "discovery_candidates",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_provider_accounts_ProviderId_AccountKey",
                table: "provider_accounts",
                columns: new[] { "ProviderId", "AccountKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_providers_Key",
                table: "providers",
                column: "Key",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_sources_provider_accounts_ProviderAccountId",
                table: "sources",
                column: "ProviderAccountId",
                principalTable: "provider_accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_sources_provider_accounts_ProviderAccountId",
                table: "sources");

            migrationBuilder.DropTable(
                name: "discovery_candidates");

            migrationBuilder.DropTable(
                name: "provider_accounts");

            migrationBuilder.DropTable(
                name: "providers");

            migrationBuilder.DropIndex(
                name: "IX_sources_ProviderAccountId",
                table: "sources");

            migrationBuilder.DropColumn(
                name: "ProviderAccountId",
                table: "sources");
        }
    }
}
