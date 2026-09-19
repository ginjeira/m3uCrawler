# Wave W6b-1 — Aprovação de Review aplica uma mudança explícita de catálogo
- Data: 2026-09-19
- Estado: Concluída
- Commits: db9959b
- BÍBLIA/ADR: Review que altera o catálogo declara a mudança (`docs/Reestructure/05-CATALOGUE.md:63-67`); catálogo como autoridade de identidade (DL-001, DL-002); Review lifecycle (DL-105); operações administrativas auditáveis (`22-API-CONTRACTS.md:56-58`)

## Contexto / Objectivo
F-13: a aprovação de Review era inerte (nada consumia o estado `Approved`). Objectivo: a aprovação passa a declarar explicitamente a mudança de catálogo que produz e a aplicá-la de forma atómica, auditada e idempotente.

## Âmbito implementado
- `ReviewApprovalAction` obrigatória e explícita: `add-alias`, `create-channel`, `exclude`; sem caminho implícito de criação/alias.
- `ReviewChannelSpec` para `create-channel` (Key e Name declarados pelo administrador; campos editoriais opcionais).
- `CatalogResolver.ApplyReviewApprovalAsync`: aplica a mudança atómicamente, devolve `ReviewApprovalResult` com `Idempotent` e `CatalogueChanged`; a Review não é reaberta.
- Endpoints `/api/catalog/reviews/{fingerprint}/approve` e `/api/catalog/reviews/{fingerprint}/exclude`; validação de campos → `400`; conflito/estado → `409`; item inexistente → `404`.
- Registo de auditoria por operação (`catalog.review.approve.add-alias`, `.create-channel`, `catalog.review.exclude`), com `before`/`after` sanitizados; falha → auditoria `failure`.
- Correcções de bugs do handler: argumento de fingerprint e estado de erro.

## Ficheiros/componentes principais
- `m3uCrawler/Services/Catalog/ReviewApprovalModels.cs`, `CatalogResolver.cs`, `ChannelAdministrationException.cs`
- `m3uCrawler/Services/WebDashboardService.cs`
- Testes: `m3uCrawler.Tests/ReviewApprovalEndpointTests.cs` (594 linhas)

## Validação (build + suite + testes relevantes)
- Build Release: 0 errors (warnings n/d).
- Suite completa: 2162 passed / 0 failed / 1 skipped.
- Cobertura: três acções, idempotência (re-aprovação não altera o catálogo), conflito `409`, validação `400`, auditoria.

## Evidência
- Commit `db9959b` (`feat(review): apply explicit catalogue change on review approval`).
- Diff: 5 ficheiros, +1276/-43.

## Divergências / dívida / follow-ups
- Sem follow-ups funcionais identificados nesta wave; as restantes funcionalidades inertes passam para W6b-3 (import-policies, canonical-groups/group-mappings, pending-country-approvals).
