# Wave 13-6 — Sanitização de credenciais no relatório Dispatcharr
- Data: 2026-09-18
- Estado: Concluída
- Commits: 26c91cd
- BÍBLIA/ADR: DL-020 (`docs/Reestructure/31-DECISION-LOCK.md:66-67`); segredos nunca em reports/artifacts (`docs/Reestructure/17-SECURITY.md:5-14`); backups/artefactos confidenciais (`docs/Reestructure/17-SECURITY.md:56-59`)

## Contexto / Objectivo
O snapshot read-only do ambiente real (2026-09-18) confirmou que `dispatcharr_report_*.json` continha URLs Xtream com credenciais embutidas no path (`/live/<user>/<pass>/<id>`), enquanto o artefacto `dispatcharr_plan_*.json` já era sanitizado. Isto violava o invariante DL-020 e era bloqueador de deployment da 13-6.

## Âmbito implementado
- `MatchPlanSerializer.SerializeReport` passa a sanitizar o relatório por projecção de modelo, em vez de serializar o `SyncReport` em bruto.
- Testes dedicados garantem que nenhum par user/pass sobrevive em `dispatcharr_report_*.json` nem em `dispatcharr_plan_*.json`.

## Ficheiros/componentes principais
- `m3uCrawler/Services/Sync/MatchPlanSerializer.cs`
- `m3uCrawler.Tests/DispatcharrReportSerializationSanitizationTests.cs`
- `CHANGELOG.md`

## Validação (build + suite + testes relevantes)
- Build Release: 0 errors / 52 warnings.
- Suite completa: 1917 passed / 0 failed / 1 skipped.
- Testes de sanitização de report/plan verdes.

## Evidência
- Commit `26c91cd` (`fix(13): sanitize dispatcharr report credentials`).
- Diff: 3 ficheiros, +279/-26; teste novo com 208 linhas.

## Divergências / dívida / follow-ups
- Nenhuma divergência normativa introduzida; DL-020 passa a aplicar-se ao report.
