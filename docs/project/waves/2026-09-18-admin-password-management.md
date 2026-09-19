# Wave Admin Password — Gestão e recuperação de password de administrador
- Data: 2026-09-18
- Estado: Concluída
- Commits: 419e23a, ab595a3, 899a030
- BÍBLIA/ADR: autenticação/autorização e CSRF (`docs/Reestructure/17-SECURITY.md:39-44`); operações administrativas auditáveis (`docs/Reestructure/22-API-CONTRACTS.md:52-54`); CLI/Dashboard chamam os mesmos serviços (`docs/Reestructure/15-APPLICATION.md:54-60`)

## Contexto / Objectivo
Não existia alteração de password de administrador nem recuperação a partir do host. O reset não podia transformar-se num endpoint HTTP nem em argumento de linha de comandos, e não podia reabrir o bootstrap.

## Âmbito implementado
- `AdminUserStore.ChangePassword*` (hash/segurança), com testes.
- `AdminPasswordResetService` + CLI interactivo `--admin-reset-password <username>` (password pedida interactivamente; nunca por argumento).
- Endpoint `POST /api/session/password` (autenticação + CSRF) e UI de alteração de password no dashboard; comportamento de sessões após alteração/reset.
- Documentação: criação do primeiro admin, alteração normal, recuperação/reset no host, requisitos de segurança e comportamento de sessões.

## Ficheiros/componentes principais
- `m3uCrawler/Services/Auth/AdminUserStore.cs`, `AdminPasswordResetService.cs`, `AuthService.cs`
- `m3uCrawler/Program.cs`, `m3uCrawler/Services/WebDashboardService.cs`
- Testes: `AdminUserStoreChangePasswordTests`, `AdminPasswordResetServiceTests`, `AdminSessionStoreTests`, `DashboardPasswordChangeEndpointTests`
- Docs: `docs/architecture/configuration-lifecycle.md`, `m3uCrawler/README.md`, `docs/IMPLEMENTATION_ROADMAP.md`, `CHANGELOG.md`

## Validação (build + suite + testes relevantes)
- W10a (`419e23a`): +11 testes; 2 falhas flaky pré-existentes em `XtreamAccountLockManagerTests` que passam isoladamente.
- W10b (`ab595a3`): 2013 passed / 0 failed / 1 skipped (a única falha é um teste de concorrência flaky pré-existente, passa isolado).
- W10c (`899a030`): docs-only.
- Build Release: 0 errors / 52 warnings.

## Evidência
- Commits `419e23a`, `ab595a3`, `899a030`.
- `POST /api/session/password` coberto (auth/CSRF/erros) em `DashboardPasswordChangeEndpointTests`.

## Divergências / dívida / follow-ups
- Reset é CLI interactivo no host; deliberadamente não é endpoint HTTP.
- Testes flaky de timing continuam a estabilizar (ver `PROJECT_STATUS.md`).
