# Audit Wave B — Identidade de canal: Unknown não liga e aliases normalizados
- Data: 2026-09-18
- Estado: Concluída
- Commits: 930215a
- BÍBLIA/ADR: DL-002 (`docs/Reestructure/31-DECISION-LOCK.md:12-13`); DL-003 Review obrigatório (`:15-16`); DL-008 validação não altera identidade (`:30-31`); `UNKNOWN` → Review (`docs/Reestructure/05-CATALOGUE.md:49-53`); lifecycle (`docs/Reestructure/40-ENTITY-LIFECYCLE.md`)

## Contexto / Objectivo
Achado F-09/C-04: `PipelineIngestionService.IngestAsync` auto-criava `CanonicalChannel` `CreateEligible` a partir de `Unknown`, autorizado pelo gate de país (bypass da invariante "Unknown nunca cria identidade"). Achado B2: aliases gravados em bruto (`.Trim()`) ficavam inalcançáveis pelo matcher. Achado F-11: `UpsertReviewItemAsync` reabria decisões humanas `Approved/Excluded`.

## Âmbito implementado
- `Unknown` deixa de criar `CanonicalChannel`; sem reconhecimento inequívoco segue para Review.
- `Source.Priority` preservado no ingest (não apagado).
- Aliases e membros de affinity normalizados (`ChannelNormalizer`) no seed e na escrita; pass idempotente de normalização do legado no bootstrapper.
- Decisão humana de Review não é reaberta; documentação de `ApprovePendingCountryApproval` corrigida.

## Ficheiros/componentes principais
- `m3uCrawler/Services/Catalog/PipelineIngestionService.cs`, `CatalogResolver.cs`, `ChannelCatalogBootstrapper.cs`
- Testes: `CatalogAliasNormalizationTests.cs`, `CatalogR2ConsistencyTests.cs`, `ChannelCatalogIntegrationTests.cs`, `Phase9C6CanonicalIdentityTests.cs`, `PipelineIngestionBridgeTests.cs`, `PipelineIngestionCountryGateTests.cs`
- Docs: `docs/architecture/channel-catalog-and-ownership.md`, `m3uCrawler/README.md`, `CHANGELOG.md`

## Validação (build + suite + testes relevantes)
- Build Release: 0 errors / 52 warnings.
- Suite completa: 2037 passed / 0 failed / 1 skipped.

## Evidência
- Commit `930215a` (`fix(catalog): unknown stays unlinked and aliases normalised`).
- Diff: 13 ficheiros, +868/-207.

## Divergências / dívida / follow-ups
- `ChannelNormalizer` não é um fixpoint estrito (converge em passes sucessivos) — dívida registada.
- `channel-indicators.json` e listas PT hardcoded continuam a duplicar o catálogo (C-03/C-16; pendente).
