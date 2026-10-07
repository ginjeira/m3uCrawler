using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <summary>
    /// PHASE 9C.3 — país no canal canónico, discriminator <c>AffinityKind</c>
    /// e identidade estável (<c>CanonicalChannel.Key</c>).
    ///
    /// <para>
    /// Reversibilidade: o Up cria a tabela de proveniência
    /// <c>affinity_migration_backup</c> (não mapeada no EF, fora do
    /// snapshot) que guarda o estado mínimo necessário para o Down
    /// reverter exactamente os artefactos que criou, sem heurísticas
    /// de nome e sem deduplicação arbitrária. A tabela é mantida após
    /// a migration para permitir rollback; a sua remoção será feita
    /// numa migration dedicada posterior.
    /// </para>
    /// </summary>
    public partial class AddCanonicalCountryAndAffinityKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // (1) Guard FK — ANTES de qualquer mutação de dados. Deteta
            //     CanonicalChannelId a apontar para um CanonicalChannel
            //     inexistente. Um CHECK violado aborta a migration e o
            //     EF reverte a transação completa (schema + dados +
            //     proveniência).
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS "__phase93_fk_guard";
                CREATE TEMP TABLE "__phase93_fk_guard" (
                    "value" INTEGER NOT NULL,
                    CONSTRAINT "PHASE93 fk: affinity_groups.CanonicalChannelId aponta para um CanonicalChannel inexistente" CHECK ("value" = 1)
                );
                INSERT INTO "__phase93_fk_guard" ("value")
                SELECT CASE WHEN EXISTS (
                    SELECT 1 FROM affinity_groups g
                    WHERE g.CanonicalChannelId IS NOT NULL
                      AND NOT EXISTS (
                          SELECT 1 FROM canonical_channels c
                          WHERE c.Id = g.CanonicalChannelId)
                ) THEN 0 ELSE 1 END;
                DROP TABLE "__phase93_fk_guard";
                """);

            migrationBuilder.DropIndex(
                name: "IX_affinity_members_NormalizedMember",
                table: "affinity_members");

            migrationBuilder.AddColumn<string>(
                name: "Country",
                table: "canonical_channels",
                type: "TEXT",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "affinity_members",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CanonicalChannelKey",
                table: "affinity_groups",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "affinity_groups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // (2-8) Proveniência, classificação, backfill, split, guard de
            //       correlação, cópia de membros e limpeza do CountryCode
            //       do grupo original.
            migrationBuilder.Sql(
                """
                -- (2) Proveniência: estado mínimo para o Down. Populado ANTES
                --     de qualquer alteração aos mixed groups.
                CREATE TABLE IF NOT EXISTS "affinity_migration_backup" (
                    "OriginalGroupId" INTEGER NOT NULL PRIMARY KEY,
                    "OriginalCountryCode" TEXT NOT NULL,
                    "GeneratedCountryGroupId" INTEGER NULL
                );

                INSERT INTO "affinity_migration_backup"
                    ("OriginalGroupId", "OriginalCountryCode", "GeneratedCountryGroupId")
                SELECT g.Id, TRIM(g.CountryCode), NULL
                FROM affinity_groups g
                WHERE g.CanonicalChannelId IS NOT NULL
                  AND g.CountryCode IS NOT NULL AND TRIM(g.CountryCode) <> '';

                -- (3) Classificação: Channel se tem canal; Country caso
                --     contrário (inclui órfãos, preservados).
                UPDATE affinity_groups
                SET Kind = CASE WHEN CanonicalChannelId IS NOT NULL THEN 0 ELSE 1 END;

                -- (3) Backfill da identidade estável por JOIN — NUNCA pelo nome.
                UPDATE affinity_groups
                SET CanonicalChannelKey = (
                    SELECT c.Key FROM canonical_channels c
                    WHERE c.Id = affinity_groups.CanonicalChannelId
                )
                WHERE CanonicalChannelId IS NOT NULL;

                -- (3) Espelhar o Kind do grupo nos membros (índice filtrado).
                UPDATE affinity_members
                SET Kind = (
                    SELECT g.Kind FROM affinity_groups g
                    WHERE g.Id = affinity_members.AffinityGroupId
                );

                -- (4) Contrapartida Country para cada mixed group. O nome
                --     determinístico serve apenas de correlação transitória
                --     dentro do Up; o Down usa GeneratedCountryGroupId.
                INSERT INTO affinity_groups
                    (Name, Kind, CanonicalChannelKey, CountryCode, CanonicalChannelId, CreatedAtUtc, UpdatedAtUtc)
                SELECT g.Name || ' #split-' || g.Id, 1, NULL, g.CountryCode, NULL,
                       g.CreatedAtUtc, g.UpdatedAtUtc
                FROM affinity_groups g
                JOIN "affinity_migration_backup" b ON b.OriginalGroupId = g.Id;

                -- (5) Registar o ID gerado.
                UPDATE "affinity_migration_backup"
                SET "GeneratedCountryGroupId" = (
                    SELECT ng.Id FROM affinity_groups ng
                    WHERE ng.Name = (
                        SELECT g.Name || ' #split-' || g.Id FROM affinity_groups g
                        WHERE g.Id = "affinity_migration_backup"."OriginalGroupId")
                )
                WHERE "GeneratedCountryGroupId" IS NULL;

                -- (6) Guard de correlação completa.
                DROP TABLE IF EXISTS "__phase93_split_guard";
                CREATE TEMP TABLE "__phase93_split_guard" (
                    "value" INTEGER NOT NULL,
                    CONSTRAINT "PHASE93 split: correlacao incompleta entre mixed groups e contrapartidas Country" CHECK ("value" = 1)
                );
                INSERT INTO "__phase93_split_guard" ("value")
                SELECT CASE WHEN EXISTS (
                    SELECT 1 FROM "affinity_migration_backup"
                    WHERE "GeneratedCountryGroupId" IS NULL
                ) THEN 0 ELSE 1 END;
                DROP TABLE "__phase93_split_guard";

                -- (7) Copiar membros para a contrapartida (por FK, não por nome).
                INSERT INTO affinity_members
                    (NormalizedMember, Kind, AffinityGroupId, CreatedAtUtc)
                SELECT m.NormalizedMember, 1, b.GeneratedCountryGroupId, m.CreatedAtUtc
                FROM affinity_members m
                JOIN "affinity_migration_backup" b ON b.OriginalGroupId = m.AffinityGroupId;

                -- (8) O grupo original de um mixed fica Channel puro.
                UPDATE affinity_groups
                SET CountryCode = NULL
                WHERE Id IN (SELECT "OriginalGroupId" FROM "affinity_migration_backup");
                """);

            migrationBuilder.CreateIndex(
                name: "IX_affinity_members_NormalizedMember_Channel",
                table: "affinity_members",
                column: "NormalizedMember",
                unique: true,
                filter: "\"Kind\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_affinity_groups_CanonicalChannelKey",
                table: "affinity_groups",
                column: "CanonicalChannelKey");

            migrationBuilder.CreateIndex(
                name: "IX_affinity_groups_Kind",
                table: "affinity_groups",
                column: "Kind");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // (1-3) Remover APENAS artefactos criados pelo Up e restaurar o
            //       CountryCode original — tudo por chaves explícitas da
            //       tabela de proveniência. Sem LIKE, sem heurística de
            //       nome, sem MIN(Id), sem deduplicação arbitrária.
            migrationBuilder.Sql(
                """
                DELETE FROM affinity_members
                WHERE AffinityGroupId IN (
                    SELECT "GeneratedCountryGroupId" FROM "affinity_migration_backup");

                DELETE FROM affinity_groups
                WHERE Id IN (
                    SELECT "GeneratedCountryGroupId" FROM "affinity_migration_backup");

                UPDATE affinity_groups
                SET CountryCode = (
                    SELECT b."OriginalCountryCode" FROM "affinity_migration_backup" b
                    WHERE b."OriginalGroupId" = affinity_groups.Id)
                WHERE Id IN (
                    SELECT "OriginalGroupId" FROM "affinity_migration_backup");
                """);

            // (4-5) Validar o estado remanescente contra a unicidade global
            //       do modelo anterior. Se existirem duplicados, abortar
            //       (rollback transacional) sem apagar nada. A mensagem
            //       descreve a condição observada, sem presumir a origem.
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS "__phase93_down_guard";
                CREATE TEMP TABLE "__phase93_down_guard" (
                    "value" INTEGER NOT NULL,
                    CONSTRAINT "PHASE93 down: existem NormalizedMember duplicados no estado remanescente que violam a unicidade global do modelo anterior; rollback exige intervencao manual" CHECK ("value" = 1)
                );
                INSERT INTO "__phase93_down_guard" ("value")
                SELECT CASE WHEN EXISTS (
                    SELECT 1 FROM affinity_members
                    GROUP BY "NormalizedMember" HAVING COUNT(*) > 1
                ) THEN 0 ELSE 1 END;
                DROP TABLE "__phase93_down_guard";
                """);

            // (6) Só depois de validado: remover a proveniência e as
            //     alterações de schema, restaurando o índice global.
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS "affinity_migration_backup";
                """);

            migrationBuilder.DropIndex(
                name: "IX_affinity_members_NormalizedMember_Channel",
                table: "affinity_members");

            migrationBuilder.DropIndex(
                name: "IX_affinity_groups_CanonicalChannelKey",
                table: "affinity_groups");

            migrationBuilder.DropIndex(
                name: "IX_affinity_groups_Kind",
                table: "affinity_groups");

            migrationBuilder.DropColumn(
                name: "Country",
                table: "canonical_channels");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "affinity_members");

            migrationBuilder.DropColumn(
                name: "CanonicalChannelKey",
                table: "affinity_groups");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "affinity_groups");

            migrationBuilder.CreateIndex(
                name: "IX_affinity_members_NormalizedMember",
                table: "affinity_members",
                column: "NormalizedMember",
                unique: true);
        }
    }
}
