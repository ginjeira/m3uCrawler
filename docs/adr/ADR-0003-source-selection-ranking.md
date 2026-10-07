# ADR-0003: Source Selection ranking operationalization

- **Status:** Accepted
- **Date:** 2026-09-19
- **Decision class:** Faithful operationalization of the already-closed ranking.
  DL-101 fixes the lexicographic order and DL-102 fixes priority semantics; this
  ADR only maps them to a single implementation and does not reopen either.
- **BIBLE anchors:** `docs/Reestructure/31-DECISION-LOCK.md:86-98` (DL-101),
  `docs/Reestructure/31-DECISION-LOCK.md:100-101` (DL-102),
  `docs/Reestructure/09-SELECTION.md:25-38`,
  `docs/Reestructure/31-DECISION-LOCK.md:36` (DL-010),
  `docs/Reestructure/31-DECISION-LOCK.md:103` (DL-103),
  `docs/Reestructure/24-DECISIONS.md:8`
- **Status rationale:** `09-SELECTION.md:38` states the order is already closed by
  DL-101 and that an ADR may document the rationale but cannot choose a different
  order. `24-DECISIONS.md:8` frames the ranking ADR as "rationale and consequences
  of an order already defined normatively". This ADR therefore constrains only the
  implementation of a closed decision.

## Context

Source Priority answers "what preference should exist between origins?"; Source
Selection answers "given the eligible sources in this run, which concrete stream
should be used?" (`09-SELECTION.md:5-9`). They are distinct (DL-010). Selection is a
pure function of the run snapshot: eligible `ChannelSource`s, policy, priority,
availability, technical attributes and per-channel override (`09-SELECTION.md:13-21`).
It uses lexicographic ordering with no aggregate score (`09-SELECTION.md:25`);
the same snapshot and policy must produce the same result (`09-SELECTION.md:36`).

The normative base order (`31-DECISION-LOCK.md:89-98`):

1. explicit channel override;
2. configured SourcePriority;
3. Eligibility = Eligible;
4. media/quality preference from the policy;
5. freshness of the last successful validation, when the policy uses it;
6. stable fingerprint;
7. stable ID.

A criterion is omitted when the policy does not enable it; if the configured
criteria do not distinguish two options, the final tie-break is always the stable
ID (`31-DECISION-LOCK.md:98`).

## Decision

1. **One selector.** `IChannelSourceSelector.Select(candidates, policy)` in
   `ChannelSourceSelector` is the single ranking authority. Callers MUST NOT
   re-rank, pre-sort or add scoring; they pass snapshot candidates and a policy.

2. **Lexicographic implementation.** The closed order is implemented as successive
   comparisons, not a weighted sum. Each optional criterion is applied only when
   the policy enables it.

3. **Priority semantics (DL-102).** SourcePriority is an ordinal integer; **`1` is
   more preferred than `2`** (`31-DECISION-LOCK.md:100-101`). Selection MUST order
   ascending by priority (lower value first). Any surface that accepts priority
   from an operator MUST document and display the same orientation.

4. **Eligibility as a filter.** Only `Eligibility = Eligible` candidates enter the
   ranking. Non-eligible candidates appear in the result as rejected/not-eligible,
   never silently dropped (`09-SELECTION.md:47-54`).

5. **Deterministic tie-breaks.** After the closed criteria, the total-order
   fallback is: normalized URL (ordinal) → `SourceId` → `ExternalStreamId` (ordinal)
   → the candidate's full identity tuple. This realizes the "stable ID" rule of
   DL-101 item 7 without randomness, hashes or object references.

6. **Configurable vs fixed.**
   - **Fixed (normative):** the criteria set and their relative order.
   - **Configurable:** whether optional criteria (quality preference, validation
     freshness) are active, their values, and publication limits such as
     `MaxSourcesPerChannel`, provider diversity and per-provider caps. Policy
     precedence follows DL-103 (`31-DECISION-LOCK.md:103-107`).
   - Legacy `SourcePriorityPolicy.CriteriaJson` MUST NOT be used to reorder the
     DL-101 criteria.

7. **No side effects.** Selection never creates identity, mutates the catalogue or
   changes ordering (`09-SELECTION.md:42`).

## Consequences

**Positive.** A single, auditable, reproducible ranker; priority orientation is
unambiguous; deterministic results independent of input order; policy can tune
activation/limits without redefining the normative order.

**Negative / follow-ups — implementation gap (FACT/GAP).**

- **FACT:** `ChannelSourceSelector` orders `SourcePriority` descending
  (`.ThenByDescending(p => p.Candidate.SourcePriority)`,
  `ChannelSourceSelector.cs:125`), and tests encode higher-is-better
  (`DispatcharrSourceSelectionTests.cs:59`, `Selected(... priority: 100 - rank)`).
- **NORMATIVE REQUIREMENT:** `1` is more preferred (`31-DECISION-LOCK.md:100-101`).
- **GAP:** current implementation inverts the ordinal direction unless the stored
  value is pre-inverted; no inversion is applied at `SourceSelectionStage.cs:141`,
  `:183`.
- **PROPOSED CHANGE:** either order ascending in the selector, or invert once at
  the data boundary and document it. This MUST be fixed with tests before the
  ranking is considered compliant. Until then, the implementation diverges from
  DL-102.

## Alternatives considered

- **Weighted/probabilistic score.** Rejected by DL-101 (`31-DECISION-LOCK.md:86-87`).
- **Distributed ranking in callers.** Rejected: violates "one selector" and
  determinism; different callers could diverge.
- **Re-open the criteria order.** Rejected: `09-SELECTION.md:38` and
  `24-DECISIONS.md:8` forbid it; changing the order requires changing the BIBLE
  first.

## References

- `docs/Reestructure/31-DECISION-LOCK.md:86-101` — DL-101/DL-102.
- `docs/Reestructure/09-SELECTION.md:5-54` — inputs, determinism, result states.
- `docs/Reestructure/31-DECISION-LOCK.md:36`, `:103` — DL-010, DL-103.
- `docs/Reestructure/24-DECISIONS.md:8` — ADR scope (rationale of closed order).
- Implementation evidence: `m3uCrawler/Services/SourceSelection/ChannelSourceSelector.cs:99-213`;
  `m3uCrawler/Services/SourceSelection/SourceSelectionStage.cs:139-184`.
