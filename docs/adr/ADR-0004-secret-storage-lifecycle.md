# ADR-0004: Secret storage and lifecycle

- **Status:** Proposed
- **Date:** 2026-09-19
- **Decision class:** Operationalizes the storage-by-reference isolation locked by
  DL-112 and the non-leakage rules of DL-020/`17-SECURITY.md`; fixes the concrete
  mechanism the BIBLE delegates to an ADR.
- **BIBLE anchors:** `docs/Reestructure/31-DECISION-LOCK.md:136-137` (DL-112),
  `docs/Reestructure/14-CONFIGURATION.md:32-42`, `docs/Reestructure/17-SECURITY.md:5-14`,
  `docs/Reestructure/39-CONFIG-SCHEMA.md:26-35`, `docs/Reestructure/31-DECISION-LOCK.md:66` (DL-020),
  `docs/Reestructure/24-DECISIONS.md:12`, `docs/Reestructure/17-SECURITY.md:45-59`
- **Status rationale:** DL-112 locks that secrets are stored by reference/secret
  store or equivalent isolated mechanism, never duplicated unnecessarily, and that
  the concrete mechanism must fit the deployment environment. `14-CONFIGURATION.md:42`
  explicitly says the exact mechanism is decided by ADR, and `24-DECISIONS.md:12`
  requires an ADR. Because the mechanism itself is the open decision, this is
  **Proposed**.

## Context

Secrets include Telegram api hash and phone/session credentials, provider
credentials, Dispatcharr credentials and tokens
(`39-CONFIG-SCHEMA.md:26-35`). They are sensitive data and MUST NEVER appear in
logs, reports, artifacts, error messages, application-generated screenshots or
metric labels (`17-SECURITY.md:5-13`). Secrets live in a class distinct from
technical and functional configuration (`39-CONFIG-SCHEMA.md:5`,
`14-CONFIGURATION.md:17`).

Current implementation (evidence): `wtelegram.config` is read/written by
`WtelegramConfigStore`, which preserves unknown keys and writes atomically with
best-effort `0600` permissions (`WtelegramConfigStore.cs:76-175`); Dispatcharr
reads/writes `dispatcharr_api_key` / `dispatcharr_password` and exposes only a
`HasApiKey` view (`DispatcharrConfigurationService.cs:13-70`). The Telegram
session file (`session.dat`) is runtime secret material and is never committed
(`AGENTS.md:10-11`).

## Decision

1. **Reference-based isolation (DL-112).** A secret store abstraction separates
   secret values from the functional configuration model. Functional configuration,
   the SQLite catalogue and API responses store only references/presence flags
   (e.g. `HasApiKey`), never secret values.

2. **Backing store v1.** File-backed, restricted-permission store:
   - `wtelegram.config` remains the authority for Telegram api id/hash/phone/session
     parameters;
   - Dispatcharr and provider credentials live in the same isolated secret store
     (not in the functional model), keyed by stable names
     (`dispatcharr_api_key`, `dispatcharr_password`, provider credentials);
   - writes are atomic (temp file + replace) and `0600` on POSIX; directories are
     restricted. Environment variables may override at deployment for bootstrap,
     but they are not the persistence authority.

3. **Resolution.** Secrets are resolved at `ConfigurationSnapshot` construction or
   at the point of external call; a snapshot carries references, not raw values.
   Raw values stay in memory only for the minimum required duration and are never
   serialized into artifacts or the audit trail.

4. **Lifecycle operations.** The store MUST support, each as an explicit audited
   operation: set/create, replace (rotate), validate (connectivity check),
   revoke/clear, and delete on uninstall (`14-CONFIGURATION.md:34-40`). Validation
   failure never silently invalidates a previously working secret.

5. **Rotation.** Rotation writes the new value atomically and validates it. The old
   value is discarded only after successful validation; on failure the old value is
   retained (fail-safe). Dual active secrets are allowed only inside an explicit,
   time-bounded rotation window.

6. **Non-leakage guarantees.**
   - Central sanitization/redaction is mandatory before logging, errors, reports,
     artifacts and metrics (`31-DECISION-LOCK.md:66`, `17-SECURITY.md:5-13`).
   - Admin mutations produce `AuditRecord` without secret values in before/after
     diffs (`17-SECURITY.md:45-55`).
   - Backups may contain secrets and are treated as confidential
     (`17-SECURITY.md:59`).
   - Secrets are excluded from the DB model, artifacts and all read views
     (`39-CONFIG-SCHEMA.md:35`).

7. **Deployment compatibility.** Mechanism selection favours a portable file store
   consistent with the single-instance deployment (DL-114). If a platform secret
   manager is adopted, it must implement the same interface and preserve the same
   lifecycle guarantees.

## Consequences

**Positive.** Clear separation of secret values from functional state; one auditable
lifecycle; rotation and revocation are supported operations; centralized
sanitization gives a single leakage barrier; backups are explicitly classified.

**Negative / follow-ups.** A portable file store is only as protected as filesystem
permissions and the host; it does not defend against a compromised host or root
user, and must therefore never hold more than necessary. Migration of any secret
currently stored inside functional configuration files is required. Multi-secret
rotation windows add state that must be bounded to avoid ambiguity.

## Alternatives considered

- **Serialize secrets in the functional configuration model.** Rejected:
  contradicts DL-112 and `14-CONFIGURATION.md:17`.
- **Encrypt secrets in SQLite with an in-app key.** Rejected: relocates key
  management without removing it, and complicates backup/restore.
- **OS keyring / DPAPI only.** Rejected: platform-specific and brittle across the
  supported deployment environments; may be added later behind the same interface.

## References

- `docs/Reestructure/31-DECISION-LOCK.md:136-137` — DL-112 secrets.
- `docs/Reestructure/31-DECISION-LOCK.md:66` — DL-020 no secrets in diagnostics.
- `docs/Reestructure/14-CONFIGURATION.md:32-42` — secret lifecycle, ADR mechanism.
- `docs/Reestructure/17-SECURITY.md:5-14`, `:45-59` — leakage and backups.
- `docs/Reestructure/39-CONFIG-SCHEMA.md:26-35` — secrets class.
- `docs/Reestructure/24-DECISIONS.md:12` — ADR required.
- Implementation evidence: `m3uCrawler/Services/Configuration/WtelegramConfigStore.cs`;
  `m3uCrawler/Services/Configuration/DispatcharrConfigurationService.cs`.
