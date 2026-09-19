# Wave ADRs — Decisões abertas que exigem ADR
- Data: 2026-09-19
- Estado: Concluída (documentação; sem alteração de código)
- Commits: 170acac
- BÍBLIA/ADR: ADRs exigidos por `docs/Reestructure/24-DECISIONS.md:5-23`; autoridade e documentação derivada (`docs/Reestructure/00-BIBLE.md:20-28`, `:30-41`); decisões fechadas DL-101..DL-116 (`docs/Reestructure/31-DECISION-LOCK.md:84-155`)

## Contexto / Objectivo
Desbloquear normativamente as waves ADR-gated (W4/W5/W6), registando racional sem alterar a BÍBLIA. A BÍBLIA proíbe escolher silenciosamente decisões em aberto.

## Âmbito implementado
- 6 ADRs + índice:
  - ADR-0001 country-data-ownership — Proposed
  - ADR-0002 stream-fingerprint-canonicalization — Proposed
  - ADR-0003 source-selection-ranking — Accepted
  - ADR-0004 secret-storage-lifecycle — Proposed
  - ADR-0005 catalog-baseline-vs-runtime — Proposed
  - ADR-0006 sqlite-migration-rollback — Proposed
- Nota registada: `ChannelSourceSelector` ordenava a prioridade em sentido descendente, enquanto DL-102 fixa `1 = mais preferido` (divergência a corrigir em W4a).

## Ficheiros/componentes principais
- `docs/adr/ADR-0001-country-data-ownership.md`
- `docs/adr/ADR-0002-stream-fingerprint-canonicalization.md`
- `docs/adr/ADR-0003-source-selection-ranking.md`
- `docs/adr/ADR-0004-secret-storage-lifecycle.md`
- `docs/adr/ADR-0005-catalog-baseline-vs-runtime.md`
- `docs/adr/ADR-0006-sqlite-migration-rollback.md`
- `docs/adr/README.md`

## Validação (build + suite + testes relevantes)
- docs-only; sem alteração de código/testes (n/d suite).

## Evidência
- Commit `170acac` (`docs(adr): country data, fingerprint, selection, secrets, baseline and migrations`).
- Diff: 7 ficheiros, +727.

## Divergências / dívida / follow-ups
- ADR-0001/0002/0004/0005/0006 permanecem `Proposed` (aprovação pendente).
- A divergência de direcção de prioridade foi corrigida em W4a (`fa1b593`).
