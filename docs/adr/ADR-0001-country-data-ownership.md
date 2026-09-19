# ADR-0001: Country data ownership and canonical schema

- **Status:** Proposed
- **Date:** 2026-09-19
- **Decision class:** Operationalizes the constraints already locked by DL-106 and
  the classification-only rules; fixes the ownership/schema/location the BIBLE
  explicitly leaves open.
- **BIBLE anchors:** `docs/Reestructure/31-DECISION-LOCK.md:118` (DL-106),
  `docs/Reestructure/06-COUNTRY-MEDIA.md:5`, `docs/Reestructure/06-COUNTRY-MEDIA.md:39`,
  `docs/Reestructure/31-DECISION-LOCK.md:9` (DL-001), `docs/Reestructure/31-DECISION-LOCK.md:12` (DL-002),
  `docs/Reestructure/24-DECISIONS.md:19`, `docs/Reestructure/14-CONFIGURATION.md:13`,
  `docs/Reestructure/14-CONFIGURATION.md:30`
- **Status rationale:** DL-106 locks that country data is versioned reference,
  updatable without altering canonical identity, and that imports are versioned and
  auditable. DL-001/DL-002 and `06-COUNTRY-MEDIA.md:39` lock that country is
  classification only, never identity. The BIBLE lists *country data ownership* as a
  decision requiring an ADR (`24-DECISIONS.md:19`) and does not fix owner, schema or
  location. Those open parts make this **Proposed**.

## Context

Country data supports validation and classification; it is not the authority of
canonical identity (`06-COUNTRY-MEDIA.md:5`). The Country Gate answers "does this
entry satisfy the configured country/locale criteria?", never "which channel is
this?" (`06-COUNTRY-MEDIA.md:11-13`). Country and media are filters/classifiers,
not identities (`06-COUNTRY-MEDIA.md:39`).

Current implementation (evidence, not authority):

- `runtime-data/countries/<code>.json` holds `{ "country", "channels": [...] }`
  and is provisioned from an embedded resource or built-in PT baseline; the
  provisioner never overwrites existing files (`CountryConfigProvisioner.cs:63-86`).
- `runtime-data/channel-indicators.json` holds a flat `indicators` list used as
  recognition evidence.
- `docs/architecture/configuration-lifecycle.md:434-442` records that ownership and
  schema were deliberately *not* chosen in Wave W3s and must be decided by ADR.

## Decision

1. **Ownership.** Authored country reference data is owned by the repository
   (maintainers) as a versioned, reviewable artefact, distributed with releases.
   The running application never authors the distributed baseline implicitly.
   Per-instance edits belong to the operator.

2. **Canonical location.** The authored baseline lives in a versioned, non-runtime
   repository path `docs/country/<cc>.json` (mirroring the existing
   `docs/catalog/` baseline convention) and is embedded into the assembly as a
   fallback resource. The operator/runtime overlay lives in
   `runtime-data/countries/<cc>.json`. Provisioning copies the baseline only when
   the overlay file does not exist; it never overwrites.

3. **Canonical schema (v1, additive-compatible).**

   ```json
   {
     "schemaVersion": 1,
     "country": "pt",
     "version": "2026.09.19",
     "channels": ["RTP1", "RTP 1", "SIC"],
     "indicators": ["rtp", "sic"]
   }
   ```

   `country` is lower-case ISO 3166-1 alpha-2; `channels` are membership indicators;
   `indicators` are optional supplementary evidence (supersedes
   `channel-indicators.json`). Files lacking `schemaVersion`/`version` are read as
   legacy v1 and upgraded on next explicit save.

4. **Runtime is a reader.** Reads (`GET /api/countries`, `GET /api/country`) never
   create or mutate files. Persistence happens only through startup provisioning
   or an explicit `POST /api/country/save` operation.

5. **Versioning and auditability (DL-106).** Every import/save carries
   `schemaVersion` and `version`, is idempotent, and produces an import report or
   `AuditRecord` (actor, timestamp, operation, object, result) with no secrets.
   Baseline upgrades never overwrite operator-edited overlay values.

6. **Classification-only invariants (DL-001, DL-002, `06-COUNTRY-MEDIA.md:39`).**
   Country membership may filter/classify but MUST NOT create, merge, split,
   rename or re-key a `CanonicalChannel`. Country edits never change
   `CanonicalChannel.Key`. `CanonicalChannel.Country` is a classifier attribute,
   never part of functional identity.

7. **`channel-indicators.json`.** Treated as legacy/supplementary indicator
   evidence, not an authority for country membership; it MUST NOT create channels.
   New authored data uses the `indicators` field of the schema above; a later
   migration may fold the standalone file in.

## Consequences

**Positive.** Single owned authority for authored country data; deterministic,
auditable, additive upgrades; operator edits survive upgrades; country cannot leak
into identity (DL-002); removes the runtime-data-as-authored-source debt noted in
`docs/architecture/configuration-lifecycle.md:400-402`.

**Negative / follow-ups.** Introduces `docs/country/` and requires a migration of
the shipped `runtime-data/countries/pt.json` and `channel-indicators.json`; the
`CountryConfigProvisioner` must gain schema-version validation. Until approved,
implementation MUST NOT assume the new location.

## Alternatives considered

- **Store country data in SQLite.** Rejected: country data is a versioned reference
  artefact, not runtime state; overlap with DL-106 reference semantics.
- **Keep `runtime-data/countries/` as the authored source.** Rejected: mixes
  runtime state with distribution, and the Docker/CI build cannot see it
  (`docs/architecture/configuration-lifecycle.md:400-402`).
- **Single global read-only embedded baseline.** Rejected: prevents operator
  overlays, contradicting the per-instance edit path already present.

## References

- `docs/Reestructure/31-DECISION-LOCK.md:118` — DL-106 country data.
- `docs/Reestructure/31-DECISION-LOCK.md:9`, `:12` — DL-001/DL-002 identity.
- `docs/Reestructure/06-COUNTRY-MEDIA.md:5`, `:11-13`, `:39` — classification only.
- `docs/Reestructure/24-DECISIONS.md:19` — ADR required (country ownership).
- `docs/Reestructure/14-CONFIGURATION.md:13`, `:30` — persisted state, schema version.
- Implementation evidence: `m3uCrawler/Services/Configuration/CountryConfigProvisioner.cs`;
  `docs/architecture/configuration-lifecycle.md:434-442`.
