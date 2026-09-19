# Wave W6a — Registo de auditoria de mutações administrativas
- Data: 2026-09-19
- Estado: Concluída
- Commits: 39eefe7
- BÍBLIA/ADR: operações administrativas auditáveis (`docs/Reestructure/22-API-CONTRACTS.md:56-58`); segurança e sanitização (`17-SECURITY.md`); persistência no catálogo SQLite (`16-PERSISTENCE.md`); DL-021

## Contexto / Objectivo
F-01/DL-021 e a secção de segurança exigem que qualquer alteração administrativa produza um registo com actor, timestamp, operação, objecto, antes/depois e resultado, sem segredos. Antes desta wave não existia entidade nem endpoint de auditoria.

## Âmbito implementado
- Entidade `audit_records` + migração aditiva `20260919075139_AddAuditRecords` (tabela, índices em `ObjectId`, `ObjectType`, `OccurredAtUtc`).
- `IAuditService`/`AuditService`: uma operação `RecordAsync`, best-effort (uma falha de auditoria não aborta a mutação), timestamp atribuído pelo serviço com relógio injectável.
- Sanitização centralizada via `CredentialSanitizer` (`SanitizeJson`/`SanitizeSensitiveText`); `Before`/`After`/`Detail` nunca persistem segredos; campos truncados por comprimento máximo.
- Wiring a mutações administrativas: canal/alias, affinity-group, identity-rule, ordering-list/item, priority-policy, source-selection-policy, source, channel-source, validation-policy, app-settings, discovery-settings, telegram config/auth e dispatcharr config/test.
- `GET /api/audit`: read-only; filtros opcionais `objectType`, `objectId` e `limit` (1..1000, por defeito 100); método ≠ GET → `405` com `Allow: GET`; serviço ausente/erro → `503 audit-unavailable`.
- Actor `user` (sessão humana, com Id/username best-effort) ou `system` (máquina/bootstrap/pipeline); resultado `success`/`failure`.

## Ficheiros/componentes principais
- `m3uCrawler/Services/Audit/AuditModels.cs`, `AuditService.cs`
- `m3uCrawler/Services/Catalog/AuditRecordEntity.cs`, `ChannelCatalogDbContext.cs`
- Migração: `20260919075139_AddAuditRecords.cs`/`.Designer.cs`, `ChannelCatalogDbContextModelSnapshot.cs`
- `m3uCrawler/Services/CredentialSanitizer.cs`
- `m3uCrawler/Services/WebDashboardService.cs`, `m3uCrawler/Program.cs`
- `m3uCrawler/Services/Auth/AdminUserStore.cs`, `AuthService.cs`
- Testes: `m3uCrawler.Tests/AuditRecordTests.cs` (427 linhas)
- Docs: `CHANGELOG.md`

## Validação (build + suite + testes relevantes)
- Build Release: 0 errors (warnings n/d).
- Suite completa: 2148 passed / 0 failed / 1 skipped.
- Cobertura: persistência, sanitização de segredos, best-effort e contrato `GET /api/audit` (filtros, 405, 503).

## Evidência
- Commit `39eefe7` (`feat(audit): record administrative mutations`).
- Diff: 15 ficheiros, +2842/-8.

## Divergências / dívida / follow-ups
- F-27 (guardas de método/405 apenas parciais noutros handlers) permanece `Parcial`.
- Endpoints administrativos fora dos listados continuam sem auditoria; extensão é incremental.
