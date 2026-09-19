# Wave W2 — Paridade de execução (pipeline partilhado)
- Data: 2026-09-19
- Estado: Concluída
- Commits: 2f4b662
- BÍBLIA/ADR: scheduler e manual usam o mesmo coordinator (DL-016, `docs/Reestructure/31-DECISION-LOCK.md:54-55`); pipeline normativo (`docs/Reestructure/13-RUNS.md:24-40`); UI/CLI não são autoridade (DL-021); output atómico (DL-019)

## Contexto / Objectivo
Achado F-02: `telegramRun` (dashboard/scheduler, não-maintain) não publicava playlist/report/selection/sync/history. Achado F-20: `generatePlaylist` ignorava o `<id>` do job. Achado F-06: gate global exigia Telegram para jobs que não precisam dele.

## Âmbito implementado
- `RunPublicationService` partilhado: CLI, maintenance, dashboard e scheduler passam pelo mesmo pipeline (country gate → ingestão → source selection → publicação M3U atómica → report → history → sync Dispatcharr com dry-run).
- `TelegramLiveRunExecutor` e `DispatcharrSyncCoordinator` alinhados com o pipeline.
- `ScheduledPlaylistGenerationAction` honra o `<id>` do job.
- Gate por capacidade (`ActionCapabilityGate`, `ScheduledActionCapabilities`): acções não-Telegram não ficam bloqueadas por falta de auth Telegram.

## Ficheiros/componentes principais
- `m3uCrawler/Services/LiveRun/RunPublicationService.cs`, `TelegramLiveRunExecutor.cs`
- `m3uCrawler/Services/Sync/DispatcharrSyncCoordinator.cs`
- `m3uCrawler/Services/Automation/ScheduledPlaylistGenerationAction.cs`, `ScheduledJobRunner.cs`, `ActionCapabilityGate.cs`, `ScheduledActionCapabilities.cs`
- `m3uCrawler/Services/PlaylistManagerService.cs`, `m3uCrawler/Program.cs`
- Testes: `WaveW2PublicationTests.cs`

## Validação (build + suite + testes relevantes)
- Build Release: 0 errors / 52 warnings.
- Suite completa: 2070 passed / 0 failed / 1 skipped.
- Testes de paridade byte-equal CLI↔scheduler/dashboard.

## Evidência
- Commit `2f4b662` (`fix(runs): share the published pipeline between CLI, dashboard and scheduler`).
- Diff: 15 ficheiros, +1527/-337.

## Divergências / dívida / follow-ups
- `playlist.m3u` ainda pode ser escrito por caminhos que ignoram a selection (D-F10/F16) — alvo de W4a/W6.
- `--web-allow-trigger` ausente do compose ("Run now" 503) continua pendente (F-22).
