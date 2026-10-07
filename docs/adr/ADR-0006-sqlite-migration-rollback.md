# ADR-0006: SQLite migration and rollback strategy

- **Status:** Proposed
- **Date:** 2026-09-19
- **Decision class:** Operationalizes the migration properties locked by
  `16-PERSISTENCE.md:42-49` and the persistence/history invariants of DL-022/DL-023;
  fixes the mechanics and rollback expectations the BIBLE leaves to an ADR.
- **BIBLE anchors:** `docs/Reestructure/16-PERSISTENCE.md:42-49`,
  `docs/Reestructure/31-DECISION-LOCK.md:72-76` (DL-022/DL-023),
  `docs/Reestructure/16-PERSISTENCE.md:7-17`, `docs/Reestructure/16-PERSISTENCE.md:34`,
  `docs/Reestructure/31-DECISION-LOCK.md:139` (DL-113), `docs/Reestructure/31-DECISION-LOCK.md:142` (DL-114),
  `docs/Reestructure/31-DECISION-LOCK.md:145` (DL-115), `docs/Reestructure/24-DECISIONS.md:16`
- **Status rationale:** `16-PERSISTENCE.md:44-48` locks that each migration is
  deterministic, tested, data-preserving, declares incompatibilities and has a
  rollback/restore strategy when SQL rollback is unsafe. The choice of mechanics
  (tooling, additive-only default, backup-based rollback) is not fixed and
  `24-DECISIONS.md:16` requires an ADR, so this is **Proposed**.

## Context

SQLite is the persistent authority of application state (DL-022,
`16-PERSISTENCE.md:7`). History and current state are distinct and must not be
destroyed to represent only the current state (DL-023). The normal installation is
single-instance/single logical writer (DL-114, `16-PERSISTENCE.md:5`). Principles:
foreign keys active, versioned migrations, defined indexes, constraints for
invariants, explicit transactions, UTC timestamps, versioned enum/state values
(`16-PERSISTENCE.md:9-17`). Long network operations must not hold a DB transaction
open (`16-PERSISTENCE.md:34`, DL-115).

Current implementation (evidence): `ChannelCatalogBootstrapper` applies EF Core
migrations at startup under an exclusive file lock, creates a pre-migration backup
for any pending migration, never recreates/truncates the database, and aborts
startup with a clear error on migration failure (`ChannelCatalogBootstrapper.cs:13-30`,
`:48-136`, `:259-295`).

## Decision

1. **Tooling and versioning.** Schema changes are EF Core code-first migrations,
   ordered, versioned and committed alongside code (`dotnet ef migrations add`).
   Application uses `Database.MigrateAsync`; `EnsureCreated` is never used on an
   existing database.

2. **Additive-only default.** A migration MUST be additive: add table, column or
   index, or backfill data. Dropping/renaming a data-bearing column or table is a
   destructive operation and MUST NOT be bundled into an ordinary migration.

3. **Destructive migrations.** When genuinely unavoidable, a destructive migration
   MUST:
   1. be a dedicated, separately reviewed migration;
   2. declare the incompatibility and data affected (`16-PERSISTENCE.md:47`);
   3. create a pre-migration backup before applying;
   4. have a restore-based rollback path;
   5. be covered by a test that migrates a real legacy fixture and asserts data
      preservation.

4. **Rollback = backup restore.** SQL `Down()` is not the primary rollback
   mechanism. Rollback consists of restoring the pre-migration backup
   (`.pre-migration-<utc>.db`) and reverting the application version. Retention of
   these backups follows DL-113 and they are treated as confidential because they
   may contain secrets (`17-SECURITY.md:59`).

5. **Failure handling.** A failed migration aborts startup/sync with a clear,
   non-partial error; the operator restores the latest backup and re-runs
   (`ChannelCatalogBootstrapper.cs:98-109`). No partial synchronization is
   attempted.

6. **Concurrency.** The migrating process holds an exclusive file lock before
   mutating; a second writer must wait or refuse. This matches the single-instance
   model (DL-114) and the "lock before effects" spirit of DL-109.

7. **Testing.** Every migration is deterministic and tested, preserves data, and
   declares incompatibilities (`16-PERSISTENCE.md:44-48`); the initial seed is
   idempotent and re-applicable without duplication.

8. **Transactions.** Database transactions are short and delimited by a state
   change; external/network calls never remain inside a long transaction
   (DL-115, `16-PERSISTENCE.md:34`).

## Consequences

**Positive.** Predictable, reversible upgrades; data loss requires an explicit,
reviewed migration; rollback does not depend on reliable `Down()` methods;
single-writer safety at startup; history preserved (DL-023).

**Negative / follow-ups.** Additive-only growth can accumulate unused columns and
requires periodic explicit cleanup migrations. Backup-based rollback consumes disk
and requires a retention policy aligned with DL-113. Destructive migrations need a
review gate that is slower than routine schema evolution.

## Alternatives considered

- **`EnsureCreated` / recreate on schema change.** Rejected: destroys state and
  contradicts DL-022/DL-023.
- **Standalone raw SQL migration scripts.** Rejected: weaker parity with the EF
  model and easier to apply inconsistently.
- **Rely on `Down()` for rollback.** Rejected: not safe for all operations
  (`16-PERSISTENCE.md:48`); backup restore is the declared path.
- **Automatic destructive migrations at startup without backup.** Rejected: can
  erase evidence and local state.

## References

- `docs/Reestructure/16-PERSISTENCE.md:5-17`, `:34`, `:42-49` — SQLite principles and migrations.
- `docs/Reestructure/31-DECISION-LOCK.md:72-76` — DL-022/DL-023.
- `docs/Reestructure/31-DECISION-LOCK.md:139`, `:142`, `:145` — DL-113/DL-114/DL-115.
- `docs/Reestructure/17-SECURITY.md:59` — backups confidentiality.
- `docs/Reestructure/24-DECISIONS.md:16` — ADR required (SQLite migration strategy).
- Implementation evidence: `m3uCrawler/Services/Catalog/ChannelCatalogBootstrapper.cs`.
