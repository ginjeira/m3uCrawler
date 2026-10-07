# Wave ADR-0001 — Completude do ADR de propriedade de dados de país
- Data: 2026-09-19
- Estado: Concluída (documentação; sem alteração de código)
- Commits: 4de2773
- BÍBLIA/ADR: regra de completude e governação (`docs/Reestructure/00-BIBLE.md:54-79` §5/§6); decisões delegadas a ADR (`docs/Reestructure/24-DECISIONS.md:19-23`, `:31`); country media (`06-COUNTRY-MEDIA.md:5`, `:11-13`, `:39`); DL-001/DL-002/DL-106/DL-107 (`31-DECISION-LOCK.md:9`, `:12`, `:118-119`, `:121-122`)

## Contexto / Objectivo
Bible Audit do ADR-0001 contra a Regra de Completude reforçada (`00-BIBLE.md` §5/§6). O draft anterior estava `BIBLE_GAP` por não fechar o schema, as regras de versionamento/migração/merge, os contratos da API de país nem a rastreabilidade de requisitos. Objectivo: completar o ADR sem o promover a `Accepted` (a promoção é do proprietário).

## Âmbito implementado
- Schema canónico `country.json` (schemaVersion 1): `schemaVersion`, `version`, `country`, `locale`, `updatedAt`, `indicators`, `groupTokens`, `negativeEvidence`, `channelNameHints` (**evidence-only**), `removed`; campo `displayName` legado removido do schema canónico.
- Hard limits de `channelNameHints`: nunca criam `CanonicalChannel`; são candidatos a `ChannelAlias` apenas sob canal existente; sem match → Review.
- Invariantes (§4), versionamento/migração/merge (§5; migração 0→1 determinística e idempotente; ausência ≠ remoção via `removed`).
- Contratos completos da família Country (§6): `GET /api/countries`, `GET /api/country`, `GET /api/country/validate`, `POST /api/country/save` (auth, CSRF, erros, side effects, idempotência, auditoria).
- Matriz de conformidade com a BÍBLIA (§10) com IDs candidatos `CD-01`..`CD-16` e critérios de aceitação (§7).
- Contratos registados normativamente em `docs/Reestructure/22-API-CONTRACTS.md` §6 (Country) — ficheiro **untracked** (WIP do utilizador, não incluído neste commit).

## Ficheiros/componentes principais
- `docs/adr/ADR-0001-country-data-ownership.md`
- `docs/adr/README.md`
- `docs/PROJECT_STATUS.md` (actualização de ponteiro, incluída em `4de2773`)
- `docs/Reestructure/22-API-CONTRACTS.md` §6 (untracked; não commitado)

## Validação (build + suite + testes relevantes)
- docs-only; sem alteração de código/testes (n/d suite).

## Evidência
- Commit `4de2773` (`docs(adr): complete country data ownership ADR (schema, migration, API)`).
- Diff: 3 ficheiros, +407/-95.
- Status do ADR: **Proposed — complete — pending approval**; deixou de ser `BIBLE_GAP`.

## Divergências / dívida / follow-ups
- ADR-0001 permanece `Proposed` até aprovação explícita do proprietário; um ADR `Proposed` não é normativo e não deve ser implementado.
- A raiz única de `runtime-data` (C-08/F-21) é mandatada pelo ADR mas o valor concreto é decisão de deployment (ver `docs/PROJECT_STATUS.md`).
- `46-REQUIREMENT-TRACEABILITY.md` continua por preencher; IDs `CD-nn` apenas semeados.
