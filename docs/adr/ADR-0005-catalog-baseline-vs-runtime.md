# ADR-0005: Catalog baseline vs runtime state

- **Status:** Proposed
- **Date:** 2026-09-19
- **Decision class:** Operationalizes the invariant locked by DL-107 (baseline is
  initial; operator changes are local; an upgrade cannot erase local changes
  without an explicit operation) and the non-destructive import rule of
  `23-DATA-CONTRACTS.md:25`; fixes the upgrade mechanics the BIBLE leaves to an ADR.
- **BIBLE anchors:** `docs/Reestructure/31-DECISION-LOCK.md:121-122` (DL-107),
  `docs/Reestructure/23-DATA-CONTRACTS.md:5-25`, `docs/Reestructure/05-CATALOGUE.md:23-27`,
  `docs/Reestructure/31-DECISION-LOCK.md:9-13` (DL-001/DL-002), `docs/Reestructure/31-DECISION-LOCK.md:78` (DL-024),
  `docs/Reestructure/24-DECISIONS.md:21`, `docs/Reestructure/14-CONFIGURATION.md:13`
- **Status rationale:** DL-107 locks the outcome (distributed catalogue is the
  initial baseline; operator changes are local; an upgrade cannot delete local
  changes without an explicit operation), and `23-DATA-CONTRACTS.md:25` locks that
  local information is never silently deleted by a file difference. The concrete
  apply/upgrade mechanics are not fixed and `24-DECISIONS.md:21` lists
  "catálogo baseline/runtime" as requiring an ADR, so this is **Proposed**.

## Context

The catalogue is the authority of identity; unknown never creates identity
(DL-001/DL-002). A distributed baseline is the *initial* catalogue; operator
changes constitute local state; a baseline upgrade cannot erase local changes
without an explicit operation (DL-107). Import must define create/update/conflict/
delete/dry-run, preserve IDs and never silently delete local information
(`23-DATA-CONTRACTS.md:16-25`). There must not be a parallel list that decides which
channels exist; auxiliary recognition data is not another source of truth
(`05-CATALOGUE.md:23-27`).

Current implementation (evidence): a versioned JSON baseline
(`docs/catalog/m3ucrawler_pt_canonical_catalog.json`) plus an embedded fallback is
imported by `CatalogBaselineImporter.ImportAsync`, which is idempotent, additive and
never deletes (`CatalogBaselineImporter.cs:18-30`, `:275-431`); source precedence is
`M3U_BASELINE_PATH` → `CWD/docs/catalog` → `BaseDirectory/docs/catalog` → embedded
(`ChannelCatalogBootstrapper.cs:212-257`).

## Decision

1. **Two layers, one authority.** The distributed baseline is an immutable,
   versioned, read-only artefact used for initial import. After import, the SQLite
   catalogue is the identity authority (DL-001). The baseline is never read as a
   live authority at runtime.

2. **Baseline identity.** A baseline declares `schemaVersion`, `catalogId`,
   `version` and `country` (`23-DATA-CONTRACTS.md:5-12`). These are recorded on
   import as provenance.

3. **Additive, idempotent import.** Import is upsert keyed by
   `CanonicalChannel.Key` and `NormalizedAlias`; it creates missing channels,
   adds missing aliases, and never truncates or deletes existing rows
   (`23-DATA-CONTRACTS.md:25`).

4. **Operator state survives upgrades.** Operator edits (create/edit/alias/merge/
   deactivate, `Country`, publication policy) are local state. A baseline upgrade
   may:
   - create channels absent locally;
   - add aliases absent locally;
   - update baseline-owned fields (e.g. `DisplayName`, `Country`) **only** when the
     local value still equals the previous baseline value.
   It MUST NOT overwrite operator-edited fields. Conflicts are reported, never
   silently applied.

5. **Absence is not deletion (DL-024).** A channel or alias missing from a newer
   baseline is never deleted because of that difference. Deactivation/removal is an
   explicit operator operation.

6. **Provenance and reporting.** Each import records the applied `catalogId`,
   `version`, source (file/embedded) and a report with created/updated/skipped
   counters and warnings, sanitized of secrets. Import outcomes are auditable.

7. **Determinism and dry-run.** The same baseline against the same catalogue
   state produces the same result (idempotent). A dry-run/preview mode reports the
   expected changes without writing (`23-DATA-CONTRACTS.md:20`), consistent with
   DL-015 for non-mutating previews.

8. **Explicit destructive operations only.** Replacing or removing the baseline,
   or applying a destructive reconciliation, requires an explicit operator
   operation (CLI/UI) with confirmation — never a startup side effect
   (`14-CONFIGURATION.md:13`, `39-CONFIG-SCHEMA.md:45`).

## Consequences

**Positive.** Operator curation cannot be destroyed by a release; upgrades are
predictable and auditable; identity authority stays in SQLite; provenance makes
"which baseline produced this state" answerable; no parallel source of truth.

**Negative / follow-ups.** Per-field baseline ownership must be tracked to decide
when an update is safe; without an explicit "baseline-owned vs operator-owned"
marker, the rule in point 4 is only as accurate as the comparison it uses. Removed
baseline entries linger by design and may need an explicit cleanup workflow.
Conflicts that are only reported require operator follow-up.

## Alternatives considered

- **Baseline as the live authority.** Rejected: destroys operator state and
  contradicts DL-107.
- **Destructive sync (mirror the file).** Rejected: violates
  `23-DATA-CONTRACTS.md:25` and DL-024.
- **No distributed baseline.** Rejected: `05-CATALOGUE.md:23-27` requires a
  canonical initial catalogue rather than an empty or parallel list.

## References

- `docs/Reestructure/31-DECISION-LOCK.md:121-122` — DL-107 baseline.
- `docs/Reestructure/23-DATA-CONTRACTS.md:5-25` — catalogue contract, import, no silent delete.
- `docs/Reestructure/05-CATALOGUE.md:23-27` — one source of catalogue truth.
- `docs/Reestructure/31-DECISION-LOCK.md:9-13`, `:78` — DL-001/DL-002, DL-024.
- `docs/Reestructure/24-DECISIONS.md:21` — ADR required (baseline/runtime).
- Implementation evidence: `m3uCrawler/Services/Catalog/CatalogBaselineImporter.cs`;
  `m3uCrawler/Services/Catalog/ChannelCatalogBootstrapper.cs:121-257`.
