# Architecture Decision Records (ADRs)

This directory holds the project's ADRs. The BIBLE (`docs/Reestructure/`) is the
normative source of truth; an ADR records rationale and decisions that need
history. An ADR MUST NOT contradict the BIBLE without first updating the affected
normative section (`docs/Reestructure/00-BIBLE.md:20-27`,
`docs/Reestructure/31-DECISION-LOCK.md:151-155`).

## Convention

- Location: `docs/adr/`.
- Filename: `ADR-NNNN-<slug>.md` (four-digit, zero-padded, monotonic).
- Required sections: `# ADR-NNNN: <title>`, `Status`, `Date`, `Context`,
  `Decision`, `Consequences`, `Alternatives considered`, `References`.
- Status vocabulary: `Proposed` | `Accepted` | `Deprecated` | `Superseded`.
- Every ADR cites BIBLE references as `docs/Reestructure/<file>.md:<line>`.
- `Accepted` is reserved for a faithful operationalization of an already-closed
  BIBLE decision (e.g. a `31-DECISION-LOCK.md` DL). Decisions the BIBLE explicitly
  leaves to an ADR start as `Proposed` and are promoted after approval.
- The decision list that requires ADRs is `docs/Reestructure/24-DECISIONS.md:5-23`.

## Index

| ADR | Title | Status | One-line decision |
|---|---|---|---|
| [ADR-0001](ADR-0001-country-data-ownership.md) | Country data ownership and canonical schema | Proposed (complete — pending approval) | Authored country data is a versioned repository-owned reference in `docs/country/<cc>.json`, embedded as fallback; operator overlay wins in `runtime-data/countries/<cc>.json`; `country.json` schema v1 (`schemaVersion`/`version`/`locale`/`updatedAt`/`indicators`/`groupTokens`/`negativeEvidence`/evidence-only `channelNameHints`); deterministic migration/merge (local edits win, no silent deletion); four country endpoints contracted in `22-API-CONTRACTS.md` §6; country is classification only, never identity (DL-106, DL-001/DL-002, DL-107). |
| [ADR-0002](ADR-0002-stream-fingerprint-canonicalization.md) | Stream fingerprint and canonicalization | Proposed | `FingerprintVersion = "sfp1"`; fingerprint is SHA-256 of a canonicalized URL that preserves auth/path/query and only removes fragment/default-port/case noise; `OriginalTvgId` is canonicalized as `ExternalIdentity` evidence and never creates identity (DL-108, DL-001/DL-002). |
| [ADR-0003](ADR-0003-source-selection-ranking.md) | Source Selection ranking operationalization | Accepted | One selector implements the closed lexicographic order of DL-101 with priority `1` = most preferred (DL-102) and a total-order stable-ID tie-break; criteria order is fixed, activation/limits are policy. Documents a current priority-direction gap to fix. |
| [ADR-0004](ADR-0004-secret-storage-lifecycle.md) | Secret storage and lifecycle | Proposed | Secrets live in an isolated, restricted-permission reference store, never in the functional model; lifecycle covers set/rotate/validate/revoke/delete; sanitization is centralized and backups are confidential (DL-112, DL-020). |
| [ADR-0005](ADR-0005-catalog-baseline-vs-runtime.md) | Catalog baseline vs runtime state | Proposed | The distributed baseline is initial only; SQLite is the identity authority; import/upgrade is additive and idempotent and never overwrites operator changes; absence is not deletion (DL-107, DL-024). |
| [ADR-0006](ADR-0006-sqlite-migration-rollback.md) | SQLite migration and rollback strategy | Proposed | EF Core versioned migrations, additive by default; destructive migrations need an explicit review, pre-migration backup and restore-based rollback; a failing migration aborts startup non-partially (16-PERSISTENCE, DL-022/DL-023). |

## Status rationale summary

`24-DECISIONS.md:8` treats the Source Selection ranking ADR as documentation of a
normative order already closed by DL-101, so ADR-0003 is `Accepted`. The remaining
items (`24-DECISIONS.md:7`, `:12`, `:16`, `:19`, `:21`) explicitly require an ADR
and leave the concrete decision open, so they are `Proposed` until approved.

ADR-0001 (`24-DECISIONS.md:19`) was re-audited against the strengthened completeness
rule (`00-BIBLE.md` §5/§6): it is now **complete — pending approval** (schema,
versioning/migration/merge, API contracts, traceability) but remains `Proposed`;
only the owner promotes it to `Accepted`. The other `Proposed` ADRs above still await
the same re-audit.
