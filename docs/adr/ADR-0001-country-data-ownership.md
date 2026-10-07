# ADR-0001: Country data ownership and canonical schema

- **Status:** Proposed — **complete — pending approval**
- **Date:** 2026-09-19
- **Audit revision:** 2026-09-19 (Bible Audit against `00-BIBLE.md` §5/§6).
- **Pre-audit status (this review):** `BIBLE_GAP` — **incomplete**. The previous draft
  did not close the schema, versioning/migration/merge rules, the country API
  contracts, or requirement traceability required by `00-BIBLE.md:54-79` §5/§6 and
  `24-DECISIONS.md:19-23`, `:31`.
- **Post-audit status:** **complete — pending approval**. This revision closes every
  item the completeness rule requires. It remains **Proposed**; only the project
  owner promotes an ADR to `Accepted` (`docs/adr/README.md:17-19`). Not a `BIBLE_GAP`.
- **Decision class:** Operationalizes the constraints already locked by DL-106
  (versioned country reference), DL-001/DL-002 (identity) and DL-107 (baseline vs
  local state); fixes the ownership, schema, location, versioning, migration and API
  contracts the BIBLE explicitly delegates to an ADR
  (`24-DECISIONS.md:19-23`, `:31`).
- **BIBLE anchors:** `docs/Reestructure/00-BIBLE.md:54-79` (§5/§6),
  `docs/Reestructure/23-DATA-CONTRACTS.md:37-61` (§5),
  `docs/Reestructure/22-API-CONTRACTS.md:26-50` (§3),
  `docs/Reestructure/24-DECISIONS.md:19-23`, `:31`,
  `docs/Reestructure/31-DECISION-LOCK.md:9` (DL-001), `:12` (DL-002),
  `:118-119` (DL-106), `:121-122` (DL-107),
  `docs/Reestructure/05-CATALOGUE.md:11-27`, `:63-67`,
  `docs/Reestructure/06-COUNTRY-MEDIA.md:5`, `:11-13`, `:39`,
  `docs/Reestructure/14-CONFIGURATION.md:13`, `:30`,
  `docs/Reestructure/15-APPLICATION.md:18`,
  `docs/Reestructure/28-TRUTH-AND-TRACEABILITY.md:3-27`,
  `docs/Reestructure/46-REQUIREMENT-TRACEABILITY.md:50-52`,
  `docs/Reestructure/41-API-INVENTORY.md:3`.

## Context

Country data supports validation and classification; it is not the authority of
canonical identity (`06-COUNTRY-MEDIA.md:5`). The Country Gate answers "does this
entry satisfy the configured country/locale criteria?", never "which channel is
this?" (`06-COUNTRY-MEDIA.md:11-13`). Country and media are filters/classifiers,
not identities (`06-COUNTRY-MEDIA.md:39`). The catalogue is the single authority for
identity and for which channels exist (`05-CATALOGUE.md:11-27`); `UNKNOWN` must not
create a `CanonicalChannel` (`05-CATALOGUE.md:49-53`, `31-DECISION-LOCK.md:12`).

Current implementation (evidence, not authority):

- `runtime-data/countries/<cc>.json` holds `{ "country", "displayName", "channels" }`
  and is provisioned from an embedded resource or a built-in PT baseline; the
  provisioner never overwrites existing files
  (`m3uCrawler/Services/Configuration/CountryConfigProvisioner.cs:63-86`,
  `:96-110`, `:207-255`).
- `CountryChannelListService` reads the directory (`GetAllCountries`,
  `GetCountry`) and writes only through `SaveCountry`
  (`m3uCrawler/Services/CountryChannelListService.cs:25-107`).
- `CountryChannelValidator` treats the `channels` list as recognition aliases and
  merges `runtime-data/channel-indicators.json` as supplementary evidence
  (`m3uCrawler/Services/CountryChannelValidator.cs:356-487`).
- The dashboard exposes `/api/countries`, `/api/country`,
  `/api/country/validate` and `POST /api/country/save`
  (`m3uCrawler/Services/WebDashboardService.cs:528-604`).
- `docs/architecture/configuration-lifecycle.md:434-442` records that ownership and
  schema were deliberately *not* chosen in Wave W3s and must be decided by ADR.

Two consequences must be fixed, not preserved: country `channels` currently behave as
a parallel recognition source (`05-CATALOGUE.md:23-27`), and there are two runtime
roots `<cwd>/runtime-data` vs `/data` (known gap C-08/F-21). This ADR subordinates the
former and mandates a single resolution root for the latter.

## Decision

### 1. Role — classification and validation only, never identity

Country data classifies and validates entries; it never defines canal identity.

- The Country Gate decides membership ("does this entry satisfy the country/locale
  criteria?"), never "which channel is this?" (`06-COUNTRY-MEDIA.md:11-13`).
- `CanonicalChannel` remains the single identity authority (`05-CATALOGUE.md:11-12`,
  DL-001). Country data is not a second functional channel list (DL-001,
  `05-CATALOGUE.md:23-27`).
- An unrecognised entry never creates a `CanonicalChannel`; it goes to Review
  (DL-002, DL-003, `05-CATALOGUE.md:49-53`).
- `CanonicalChannel.Country`/locale are classifier attributes, never part of
  functional identity and never part of `CanonicalChannel.Key` (DL-002,
  `06-COUNTRY-MEDIA.md:39`).
- DL-106 compliance: country data is a versioned reference; it can be updated without
  altering canonical identity; every import/save is versioned and auditable
  (`31-DECISION-LOCK.md:118-119`).

### 2. Authoring, authority and precedence

Three layers, resolved in this precedence order:

| Layer | Location | Owner | Nature |
|---|---|---|---|
| **Operator overlay** | `runtime-data/countries/<cc>.json` | operator (per instance) | runtime state; explicit edits |
| **Repository baseline** | `docs/country/<cc>.json` | maintainers (repository) | versioned, reviewable authored reference; distributed with releases |
| **Embedded fallback** | assembly EmbeddedResource, mirroring `docs/country/` | build/release | immutable fallback shipped in the image |

Rules:

1. **Operator overlay > repository baseline > embedded fallback.**
2. The repository baseline is the **versioned authored reference**; it is embedded at
   build time as the fallback and is never written by the running application.
3. Provisioning seeds the overlay from the baseline **only when the overlay file does
   not exist**; it never overwrites an existing overlay (DL-107,
   `CountryConfigProvisioner.cs:207-255`).
4. **Absence is not deletion** (DL-024): a missing overlay means "fall back to
   baseline", not "no country data"; a token absent from a newer baseline does not
   delete an operator token.
5. Operator edits are never overwritten by a baseline upgrade. Removing an operator
   token requires an explicit removal marker in the overlay (`removed`, §3), never
   inferred from absence.
6. A single runtime-data root MUST be resolved deterministically (closes C-08/F-21);
   the ADR does not invent a new root, it fixes the resolution to one value per
   process and documents it in readiness.
7. Reads are pure: no endpoint and no resolver creates or mutates a file
   (`14-CONFIGURATION.md:13`; see §6).

### 3. Canonical JSON schema (`country.json`), schemaVersion 1

`country.json` is a JSON object, UTF-8, no BOM, `Content-Type` `application/json`.
Unknown fields are ignored on read and preserved when safe on write
(`14-CONFIGURATION.md:21-26`). Arrays are serialized in ordinal, case-insensitive
order to keep writes deterministic.

| Field | Type | Required | Nullable | Semantics / constraints |
|---|---|---|---|---|
| `schemaVersion` | integer | yes | no | Schema of this document. Current canonical value: `1`. Missing → legacy `0` (§5). |
| `version` | string | yes | no | Content version, `YYYY.MM.DD` optionally suffixed `-<shorthex>` (content-derived). Changes only when content changes. |
| `country` | string | yes | no | ISO 3166-1 alpha-2, lower-case (`^[a-z]{2}$`). Uniqueness key of the document. |
| `locale` | string | yes | no | BCP-47 classification locale (e.g. `pt-PT`). Derived deterministically when absent (`pt`→`pt-PT`, else `<cc>`). |
| `updatedAt` | string | yes | no | ISO-8601 UTC timestamp of last explicit write; not churned by idempotent re-import. |
| `indicators` | array<string> | no | no (empty allowed) | Recognition evidence tokens (lower-cased, deduplicated). Supersedes `channel-indicators.json`. |
| `groupTokens` | array<string> | no | no (empty allowed) | Group-title category tokens that authorise the group fallback for this country. |
| `negativeEvidence` | array<string> | no | no (empty allowed) | ISO codes that, as the first title token, mark the entry as foreign for this country. |
| `channelNameHints` | array<string> | no | no (empty allowed) | **Evidence-only** channel-name variants. See the hard limits below. |
| `removed` | array<string> | no | no (empty allowed) | Explicit operator-removal markers for tokens previously present. Absence is never deletion. |

**Hard limits of `channelNameHints`** (invariant, not advice):

- `channelNameHints` **MUST NOT** create a `CanonicalChannel` and **MUST NOT** be the
  authority for which channels exist (DL-001, DL-002, `05-CATALOGUE.md:23-27`).
- They are **migration candidates for `ChannelAlias`** under the catalogue, and only
  where a `CanonicalChannel` already exists in the catalogue; the catalogue stays
  authoritative (`05-CATALOGUE.md:11-25`). A hint with no canonical match goes to
  Review, never to auto-creation (`05-CATALOGUE.md:49-53`, DL-003).
- `channelNameHints` are recognition evidence of the same class as `indicators`; no
  downstream step may treat them as identity.

Example canonical document:

```json
{
  "schemaVersion": 1,
  "version": "2026.09.19",
  "country": "pt",
  "locale": "pt-PT",
  "updatedAt": "2026-09-19T00:00:00Z",
  "indicators": ["rtp1", "rtp 1", "sic", "tvi"],
  "groupTokens": ["portugal", "pt"],
  "negativeEvidence": ["be", "bg", "es", "fr"],
  "channelNameHints": ["RTP1", "RTP 1", "SIC", "TVI"],
  "removed": []
}
```

Legacy shape currently on disk (evidence, to be migrated — §5):

```json
{ "country": "pt", "displayName": "Portugal", "channels": ["RTP1", "SIC", "TVI"] }
```

### 4. Invariants

1. Country data never creates, merges, splits, renames or re-keys a
   `CanonicalChannel`; country edits never change `CanonicalChannel.Key`
   (DL-001, DL-002, DL-008).
2. `channelNameHints` are subordinate to the catalogue: they may become
   `ChannelAlias` only under an existing canonical channel, and never a parallel
   source of truth about which channels exist (`05-CATALOGUE.md:23-27`).
3. `country`/`locale` are classification attributes only
   (`06-COUNTRY-MEDIA.md:39`).
4. **No cross-country canal duplication:** the same logical channel may be classified
   for multiple countries, but must exist as exactly one `CanonicalChannel`; country
   membership is a classifier, not an identity key (`28-TRUTH-AND-TRACEABILITY.md:17-19`).
5. `country` is the document uniqueness key; a file `<cc>.json` MUST NOT declare a
   different `country`.
6. Every persisted/exported document declares `schemaVersion` and `version`
   (`23-DATA-CONTRACTS.md:56`).
7. Reads never create or mutate files (`14-CONFIGURATION.md:13`).

### 5. Versioning, migration and merge

**Versioning.** Canonical schema is `schemaVersion` 1. Any incompatible field change
raises the number and ships a migration path; compatible additive fields may stay at
the same number. `version` is content-derived and reproducible.

**Legacy detection.** A file with no `schemaVersion` is read as legacy `schemaVersion`
0 (`{ country, displayName?, channels[] }`).

**Migration 0 → 1 (deterministic, idempotent).**

- `country` → `country` (normalised lower-case).
- `locale` → derived deterministically from `country`.
- legacy `channels[]` → `channelNameHints[]` (evidence-only, deduplicated), and a
  normalised form of each hint → `indicators[]` (recognition evidence).
- `groupId`-style tokens, when present, → `groupTokens[]`.
- `version` and `updatedAt` are written on the first explicit migration; `version` is
  derived from content, so a second run with unchanged content produces the **same
  canonical document** and performs **no write** (idempotent; DL-018).
- Migration never deletes: tokens absent from the source are preserved, and an
  operator removal is only ever expressed through `removed`.
- Unknown/future `schemaVersion` (n > 1) on an overlay is read best-effort, never
  downgraded and never rewritten; readiness surfaces a warning.

**Authoring update / baseline upgrade.**

- Baseline upgrade merges **new** baseline entries into the overlay by case-insensitive
  union, minus anything listed in `removed`, and never overwrites overlay scalars
  (`locale`, `version`, `updatedAt`) (DL-107, `14-CONFIGURATION.md:13`).
- Lists are merged without duplication (`23-DATA-CONTRACTS.md:59`).
- Provisioning seeds only when the overlay is absent and never overwrites an existing
  file (DL-107, `CountryConfigProvisioner.cs:207-255`).

**Conflict resolution (import/merge).** Local/operator edits win over any incoming
baseline; conflicting scalar fields resolve to the operator overlay; lists union
without duplicates; deletions require an explicit `removed` marker; no silent
deletion of local data by file diff (`23-DATA-CONTRACTS.md:25`, `:59`).

**Channel-name migration.** Legacy `channels` names are imported as
`channelNameHints`. For each hint the catalogue is consulted: if a `CanonicalChannel`
exists, the hint becomes a `ChannelAlias` there; otherwise it is routed to Review
(`05-CATALOGUE.md:49-53`, DL-003). The catalogue is never modified from country data
without an explicit, audited catalogue operation (`05-CATALOGUE.md:63-67`).

### 6. Country API contracts

These contracts are registered normatively in `22-API-CONTRACTS.md` §6 (Country) and
follow `22-API-CONTRACTS.md:26-50` §3. Common rules:

- **Auth:** session cookie `m3u_session` (human) or machine token `--web-token`
  (CLI/automation), enforced by the single gate (`WebDashboardService.cs:445-492`).
- **CSRF:** mutating endpoints require header `X-CSRF-Token` matching the session;
  missing/invalid → `403 csrf-invalid` (`WebDashboardService.cs:478-490`).
- **Error envelope:** `{ "error": "<stable-code>", "message": "<safe>", "correlationId": "<id>" }`
  (`22-API-CONTRACTS.md:16-24`). Never returns secrets.
- **Invariant:** `GET` endpoints are read-only and **never write or create files**.

| # | Method | Path | Auth | CSRF | Side effects | Idempotent |
|---|---|---|---|---|---|---|
| 1 | GET | `/api/countries` | session or token | n/a | none (read-only) | yes |
| 2 | GET | `/api/country?country=<cc>` | session or token | n/a | none (read-only) | yes |
| 3 | GET | `/api/country/validate?country=<cc>` | session or token | n/a | none (read-only) | yes |
| 4 | POST | `/api/country/save` | session | required | writes operator overlay only | yes |

**1. `GET /api/countries`** — list country summaries.

- Request: no body. Optional query: none.
- `200 OK` `application/json`:
  ```json
  [ { "country": "pt", "displayName": "Portugal", "locale": "pt-PT",
      "schemaVersion": 1, "version": "2026.09.19",
      "updatedAt": "2026-09-19T00:00:00Z", "channelCount": 23,
      "source": "operator-overlay" } ]
  ```
- Errors: `401 authentication-required`; `403 bootstrap-required`.
- Side effects: none. Reads never create/write files.

**2. `GET /api/country?country=<cc>`** — one classification document.

- Query: `country` (required, ISO alpha-2).
- `200 OK`: the canonical document of §3 (fields `schemaVersion`, `version`, `country`,
  `locale`, `updatedAt`, `indicators`, `groupTokens`, `negativeEvidence`,
  `channelNameHints`, `removed`); for an absent file, a non-persisted empty document
  with empty arrays (`channelNameHints`/`indicators` empty), never a file creation.
- Errors: `400 country-required`; `401 authentication-required`.
- Side effects: none.

**3. `GET /api/country/validate?country=<cc>`** — read-only validation of the generated
playlist against country evidence.

- Query: `country` (optional, default `pt`).
- `200 OK`:
  ```json
  { "country": "pt", "displayName": "Portugal", "isMatch": true,
    "matchedAliases": ["RTP1", "SIC"], "recognizedChannelCount": 2,
    "threshold": 3, "totalChannels": 23, "playlistLength": 10240,
    "sample": ["#EXTM3U", "#EXTINF:-1,RTP1"] }
  ```
- Errors: `401 authentication-required`.
- Side effects: none; reads `runtime-data/.../playlist.m3u` only if present.

**4. `POST /api/country/save`** — explicit operator overlay write.

- Headers: `Content-Type: application/json`, `X-CSRF-Token: <session token>`.
- Request body: canonical document of §3 (minimum `country`; other fields optional and
  defaulted). Example:
  ```json
  { "country": "pt", "locale": "pt-PT",
    "indicators": ["rtp1", "sic"], "groupTokens": ["portugal", "pt"],
    "negativeEvidence": ["be", "bg"],
    "channelNameHints": ["RTP1", "SIC"] }
  ```
- `200 OK`: the saved canonical document (server-assigned `schemaVersion`, `version`,
  `updatedAt`).
- Errors: `400 country-required` / `400 invalid-payload`; `401 authentication-required`;
  `403 csrf-invalid`; `409 version-conflict`; `500 persistence-error`.
- Side effects: atomic write of `runtime-data/countries/<cc>.json` (operator overlay)
  plus an audit record (actor, timestamp, operation, object, result) with no secrets.
  It never writes the repo baseline or the embedded fallback.
- Idempotency: identical canonical content produces no new `version`/`updatedAt` and no
  duplicate entries.
- Audit: administrative mutation, must be auditable (`22-API-CONTRACTS.md:56-58`).

### 7. Tests and acceptance criteria

Verifiable acceptance criteria for the implementing wave:

1. **Schema validation** — required fields present and typed; `country` matches
   `^[a-z]{2}$`; arrays contain strings; unknown fields ignored on read and preserved
   when safe. Invalid files degrade readiness, they do not crash the dashboard
   (`23-DATA-CONTRACTS.md:61`).
2. **Migration idempotency** — migrating a legacy `channels` file twice yields byte-
   identical canonical output and no duplicate tokens; run 2 performs no write.
3. **No-write-on-GET** — after `GET /api/countries`, `/api/country`,
   `/api/country/validate`, file bytes/mtimes under the country directory are
   unchanged and no file is created.
4. **Overlay precedence** — an operator overlay wins over the repo baseline and the
   embedded fallback; provisioning never overwrites an existing overlay.
5. **Absence is not deletion** — a baseline token absent from the overlay is not
   deleted; only an explicit `removed` marker removes a token.
6. **No identity creation** — importing `channelNameHints`/`indicators` never creates
   or mutates a `CanonicalChannel`; unmatched hints become Review items.
7. **Cross-country uniqueness** — the same canonical channel classified under two
   countries stays a single `CanonicalChannel`.
8. **API contract tests** — per `22-API-CONTRACTS.md:50`, integration/mock tests cover
   each of the four endpoints, including `401`, `403 csrf-invalid`, `400`, and the
   read-only guarantee.
9. **Deterministic serialization** — array ordering and `version` derivation are
   reproducible.

### 8. Consequences

**Positive.** Single owned authority for authored country data; deterministic,
auditable, additive upgrades; operator edits survive upgrades; `channelNameHints` are
explicitly subordinated to the catalogue, removing the parallel-recognition debt;
country cannot leak into identity (DL-002); closes the schema/versioning/migration/API
gaps that kept this ADR a `BIBLE_GAP`.

**Negative / follow-ups.** Introduces `docs/country/` and requires migrating the
shipped `runtime-data/countries/pt.json` and `channel-indicators.json`;
`CountryConfigProvisioner` must gain schema-version validation and single-root
resolution; `CountryChannelValidator` must read `channelNameHints`/`indicators` only as
evidence; until approved, implementation MUST NOT assume the new location or fields
(a `Proposed` ADR is not normative).

### 9. Alternatives considered

- **Store country data in SQLite.** Rejected: country data is a versioned reference
  artefact, not runtime state; overlaps with DL-106 reference semantics.
- **Keep `runtime-data/countries/` as the authored source.** Rejected: mixes runtime
  state with distribution, and the Docker/CI build cannot see it
  (`docs/architecture/configuration-lifecycle.md:400-402`).
- **Single global read-only embedded baseline.** Rejected: prevents operator overlays,
  contradicting the per-instance edit path already present.
- **Treat `channels`/`channelNameHints` as a recognition channel list.** Rejected:
  creates a second source of truth about which channels exist, violating DL-001 and
  `05-CATALOGUE.md:23-27`; they are evidence only.

## 10. Conformidade com a BÍBLIA (Bible Audit)

Audit result for this revision. Each row maps a normative requirement to the ADR
section that satisfies it. Requirement IDs (`CD-nn`) are candidate IDs to seed
`46-REQUIREMENT-TRACEABILITY.md` (still unfilled).

| ID | Requirement (normative source) | ADR section | Status |
|---|---|---|---|
| CD-01 | Country data is classification/validation, never identity (`06-COUNTRY-MEDIA.md:5`, `:39`) | §1 | Satisfied |
| CD-02 | Country Gate answers membership, not identity (`06-COUNTRY-MEDIA.md:11-13`) | §1 | Satisfied |
| CD-03 | Country is versioned reference, updatable without changing identity; import versioned+auditable (DL-106, `31-DECISION-LOCK.md:118-119`) | §1, §2, §5 | Satisfied |
| CD-04 | Catalogue is identity authority; no parallel channel list (DL-001, `05-CATALOGUE.md:11-27`; DL-002, `31-DECISION-LOCK.md:12`) | §1, §3, §4 | Satisfied |
| CD-05 | Complete serialized contract: required/optional, types, nullability, enums, keys, encoding, unknown fields (`23-DATA-CONTRACTS.md:39-52`) | §3 | Satisfied |
| CD-06 | Essential fields `schemaVersion`, `version`, `timestamp` (`23-DATA-CONTRACTS.md:56`) | §3, §5 | Satisfied |
| CD-07 | Invariants, incl. no rename without new id (`23-DATA-CONTRACTS.md:57`) | §4 | Satisfied |
| CD-08 | Versioning and schema migration (`23-DATA-CONTRACTS.md:58`, `14-CONFIGURATION.md:30`, `00-BIBLE.md:73`) | §5 | Satisfied |
| CD-09 | Conflict/merge: local edits win, lists merged without duplication (`23-DATA-CONTRACTS.md:59`, DL-107 `:121-122`) | §2, §5 | Satisfied |
| CD-10 | Baseline `docs/country/` → `runtime-data/`, updates preserve operator changes without overwrite (`24-DECISIONS.md:22`) | §2, §5 | Satisfied |
| CD-11 | Country API endpoints have complete contracts registered in `22-API-CONTRACTS.md` (`24-DECISIONS.md:23`, `22-API-CONTRACTS.md:26-50`, `41-API-INVENTORY.md:3`) | §6 (+ `22` §6) | Satisfied |
| CD-12 | Reads never create/write files (`14-CONFIGURATION.md:13`; C-10/W3s) | §6, §7 | Satisfied |
| CD-13 | Requirement IDs and traceability matrix (`46-REQUIREMENT-TRACEABILITY.md:50-52`, `28-TRUTH-AND-TRACEABILITY.md:21-27`) | §10 | Satisfied (IDs seeded; matrix still to fill) |
| CD-14 | Single authority, no duplicated authority (`28-TRUTH-AND-TRACEABILITY.md:3-19`) | §2, §4 | Satisfied |
| CD-15 | Country data is an operational-readiness requirement (`15-APPLICATION.md:18`) | §7 | Satisfied |
| CD-16 | Deterministic, idempotent operations (DL-018 `31-DECISION-LOCK.md:60-61`, `00-BIBLE.md:73`) | §5, §7 | Satisfied |

**Pre-audit status.** `BIBLE_GAP` — the previous draft left CD-05..CD-13 and CD-16
materially open (schema fields, migration/merge, API contracts, traceability).

**Post-audit status.** Complete against `00-BIBLE.md` §5/§6; still **Proposed** (not
`Accepted`, not normative) pending owner approval.

**Residual notes (not gaps in this ADR).**

- `41-API-INVENTORY.md` does not enumerate a Country family; the four contracts are
  the de facto country surface and are registered in `22-API-CONTRACTS.md` §6.
- The single runtime-data root (C-08/F-21) is mandated here but its concrete value is a
  deployment decision tracked in `docs/PROJECT_STATUS.md`.

## References

- `docs/Reestructure/00-BIBLE.md:54-79` — completeness and governance (§5/§6).
- `docs/Reestructure/22-API-CONTRACTS.md:26-50` — minimum endpoint contract.
- `docs/Reestructure/23-DATA-CONTRACTS.md:37-61` — minimum data contract.
- `docs/Reestructure/24-DECISIONS.md:19-23`, `:31` — required complete ADR.
- `docs/Reestructure/28-TRUTH-AND-TRACEABILITY.md:3-27` — single authority, traceability.
- `docs/Reestructure/46-REQUIREMENT-TRACEABILITY.md:50-52` — requirement IDs / matrix.
- `docs/Reestructure/31-DECISION-LOCK.md:9`, `:12`, `:118-119`, `:121-122` — DL-001,
  DL-002, DL-106, DL-107.
- `docs/Reestructure/05-CATALOGUE.md:11-27`, `:49-53`, `:63-67` — catalogue authority,
  unknown→Review, audited catalogue mutation.
- `docs/Reestructure/06-COUNTRY-MEDIA.md:5`, `:11-13`, `:39` — classification only.
- `docs/Reestructure/14-CONFIGURATION.md:13`, `:21-30` — persisted state, atomic
  writers, schema version.
- `docs/Reestructure/15-APPLICATION.md:18` — country data readiness.
- Implementation evidence: `m3uCrawler/Services/CountryChannelListService.cs`,
  `m3uCrawler/Services/CountryChannelValidator.cs`,
  `m3uCrawler/Services/Configuration/CountryConfigProvisioner.cs`,
  `m3uCrawler/Services/WebDashboardService.cs:528-604`;
  `docs/architecture/configuration-lifecycle.md:400-402`, `:434-442`.
