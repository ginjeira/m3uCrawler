# Wave W1 — Discovery settings como SSOT operacional
- Data: 2026-09-18
- Estado: Concluída
- Commits: 1ca9f1e
- BÍBLIA/ADR: precedência e estado funcional persistido (`docs/Reestructure/14-CONFIGURATION.md:5-13`); mesmo significado em Dashboard/CLI/scheduler (`:44-46`); UI/CLI não são autoridade (DL-021, `docs/Reestructure/31-DECISION-LOCK.md:69-70`); RunSnapshot (`docs/Reestructure/13-RUNS.md:16-22`)

## Contexto / Objectivo
Achados F-03/F-04/history: `--history-hours` era CLI-only e não persistido, e os overrides (`keyword`/`historyHours`/`maxStreams`) de `POST /api/run/start` eram aceites mas ignorados pelo executor. Não existia fonte única de configuração operacional entre CLI, dashboard e scheduler.

## Âmbito implementado
- `DiscoverySettings` (historyHours/maxStreams/keyword) persistido em `app_settings.json`.
- `GET/POST /api/discovery/settings` (autenticação + CSRF, guarda de método/405).
- Precedência única `override > persistido > defaults`; overrides de `POST /api/run/start` efectivamente aplicados.
- Scheduler populado a partir do valor persistido.
- Reload por run de `DispatcharrConfig` e da validation policy no scheduler.

## Ficheiros/componentes principais
- `m3uCrawler/Services/Configuration/DiscoverySettings.cs`, `DiscoverySettingsProvider.cs`, `AppSettingsStore.cs`
- `m3uCrawler/Program.cs`, `m3uCrawler/Services/WebDashboardService.cs`
- `m3uCrawler/Services/Automation/ScheduledAutomationHost.cs`, `Scheduled*Action.cs`
- Testes: `WaveCDiscoverySettingsTests.cs`, `Phase94LiveRunApiTests.cs`

## Validação (build + suite + testes relevantes)
- Build Release: 0 errors / 52 warnings.
- Suite completa: 2063 passed / 1 flaky / 1 skipped (o flaky passa isoladamente).

## Evidência
- Commit `1ca9f1e` (`fix(config): persist discovery settings and apply run overrides`).
- Diff: 12 ficheiros, +1209/-75.

## Divergências / dívida / follow-ups
- Teste flaky de timing (não regressão) — estabilizar.
- `runtime-data` continua com duas raízes (C-08/F-21) — pendente.
