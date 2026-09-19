# ADR-0002: Stream fingerprint and canonicalization

- **Status:** Proposed
- **Date:** 2026-09-19
- **Decision class:** Operationalizes the versioned-fingerprint requirement and the
  URL-canonicalization restriction locked by DL-108 and `04-PLAYLIST-STREAM.md`;
  fixes the concrete algorithm inputs/normalization left to the initial ADR.
- **BIBLE anchors:** `docs/Reestructure/04-PLAYLIST-STREAM.md:34-40` (fingerprint),
  `docs/Reestructure/31-DECISION-LOCK.md:124` (DL-108),
  `docs/Reestructure/32-DOMAIN-SCHEMA.md:72-85` (Stream),
  `docs/Reestructure/02-DOMAIN.md:64-90` (identifiers and universal rules),
  `docs/Reestructure/31-DECISION-LOCK.md:18` (DL-004), `docs/Reestructure/31-DECISION-LOCK.md:9` (DL-001),
  `docs/Reestructure/31-DECISION-LOCK.md:12` (DL-002), `docs/Reestructure/05-CATALOGUE.md:57` (identity vs number),
  `docs/Reestructure/24-DECISIONS.md:7`
- **Status rationale:** DL-108 locks that the fingerprint is versioned and that an
  incompatible change creates a new version with a coexistence/migration strategy.
  `04-PLAYLIST-STREAM.md:34-40` locks that the initial algorithm is registered as an
  ADR and that authenticated URL information is never stripped by normalization.
  The concrete inputs and normalization are not fixed by the BIBLE, so this is
  **Proposed**.

## Context

A `Stream` carries `Fingerprint` and `FingerprintVersion`
(`32-DOMAIN-SCHEMA.md:77-78`). The fingerprint identifies *technical equivalence*
of streams according to a versioned normative algorithm (`04-PLAYLIST-STREAM.md:36`).
Duplicate streams in the same Source are consolidated by fingerprint
(`04-PLAYLIST-STREAM.md:44`); equal streams in different Sources MUST NOT be
confused, because origin remains relevant (`04-PLAYLIST-STREAM.md:46`).

Identity rules: canonical identity does not depend on the stream URL
(`02-DOMAIN.md:80`); a number/position is never identity (DL-004,
`31-DECISION-LOCK.md:18`); unknown never creates identity (DL-002). The fingerprint
is therefore **evidence**, never identity.

## Decision

1. **Version tag.** `FingerprintVersion = "sfp1"`. A stream persists the version it
   was computed with; changing inputs or normalization is a new version, never an
   in-place reinterpretation (DL-108). Old fingerprints remain valid under their
   version until an explicit migration.

2. **Fingerprint input (v1).** The fingerprint is
   `SHA-256(UTF8("sfp1\n" + canonicalUrl))`, lower-case hex (64 chars). Inputs are
   deliberately limited to the canonical URL; `tvg-id`, `tvg-name`, group, quality
   and numeric position are *not* inputs.

3. **URL canonicalization (v1).** In order:
   1. trim surrounding whitespace;
   2. require an absolute URI with scheme `http` or `https`; otherwise no
      fingerprint is produced;
   3. lower-case scheme and host; strip a single trailing dot on host;
   4. remove the port when it equals the scheme default; otherwise keep it;
   5. remove the fragment;
   6. preserve userinfo verbatim (never strip authentication);
   7. preserve path case-sensitively (`""` → `/`);
   8. preserve the query string verbatim, including auth-bearing parameters.

   Only elements explicitly defined above are removed. Normalization MUST NOT strip
   authentication and then reconstruct an insecure URL (`04-PLAYLIST-STREAM.md:38`).

4. **No persistence of raw canonical URL.** The canonical URL may exist in memory
   for the duration of a computation; only the fingerprint hash is persisted in
   fingerprint columns. Raw URLs remain in their existing endpoint-reference field
   with its sensitivity handling (`17-SECURITY.md:5-13`, DL-020).

5. **`OriginalTvgId` / external-id canonicalization.** A separate, deterministic
   normalizer produces an `ExternalIdentity` value: trim → Unicode-compatible
   lower-case → collapse internal whitespace → strip wrapping quotes. The original
   value is retained in `OriginalTvgId` (`04-PLAYLIST-STREAM.md:18`,
   `32-DOMAIN-SCHEMA.md:81`). Null/blank tvg-id produces **no** external identity.
   An `ExternalIdentity` is matcher evidence in the fixed recognition order
   (`05-CATALOGUE.md:34`) and NEVER creates a `CanonicalChannel` by itself
   (DL-002). `tvg-name` is display evidence, not identity.

6. **Consumption rules.**
   - Dedup by fingerprint applies only within the same Source/Playlist
     (`04-PLAYLIST-STREAM.md:44`); it never merges across Sources.
   - Fingerprint is the penultimate deterministic tie-break in Selection
     (DL-101 item 6, `31-DECISION-LOCK.md:95`) before stable ID.
   - Fingerprint is never used for catalogue matching or identity assignment
     (DL-001, `02-DOMAIN.md:79-80`).

7. **Determinism.** The same original URL and version always yield the same
   fingerprint; no randomness, locale-sensitive casing or platform-dependent
   formatting is permitted (`02-DOMAIN.md:90`).

## Consequences

**Positive.** Deterministic, reproducible stream equivalence; auth-preserving
canonicalization; bounded fingerprint length (64 hex) matches the existing schema
constraint; identity/evidence separation is preserved; a version bump can coexist
with historical fingerprints.

**Negative / follow-ups.** The existing `ReviewFingerprint` (`CatalogResolver.cs:2775`)
is a *different* fingerprint (review items) and MUST NOT be confused with the
stream fingerprint; naming/ownership must remain explicit. Query strings are not
sorted, so semantically equivalent query orders are distinct — acceptable for v1
and revisable only via a new version. Host-only variation (CDN mirrors) is not
collapsed; that is intentional (removing it would risk false equivalence).

## Alternatives considered

- **Raw URL string equality.** Rejected: brittle to case/default-port/fragment variation.
- **Include tvg-id/tvg-name in the fingerprint.** Rejected: conflates external
  identity evidence with technical equivalence and fails when those fields are
  absent (`04-PLAYLIST-STREAM.md:36`).
- **Fuzzy/perceptual hashing.** Rejected: contradicts the deterministic,
  lexicographic model (`02-DOMAIN.md:90`, DL-101).
- **Persist the canonical URL beside the fingerprint.** Rejected: duplicates
  auth-bearing material in a second location (DL-020, `17-SECURITY.md:5-13`).

## References

- `docs/Reestructure/04-PLAYLIST-STREAM.md:34-40`, `:44-46` — fingerprint, dedup, origin.
- `docs/Reestructure/31-DECISION-LOCK.md:124` — DL-108 fingerprint versioning.
- `docs/Reestructure/32-DOMAIN-SCHEMA.md:72-85` — Stream fields.
- `docs/Reestructure/02-DOMAIN.md:64-90` — identifiers, identity independence, determinism.
- `docs/Reestructure/05-CATALOGUE.md:34`, `:57` — tvg-id as matching evidence, not identity.
- Implementation evidence: `ChannelCatalogDbContext.cs:183` (Fingerprint ≤ 64); `ChannelSourceSelector.cs:270-287`.
