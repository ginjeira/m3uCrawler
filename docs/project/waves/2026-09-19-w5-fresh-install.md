# Wave W5 — Fresh install sem Telegram e reutilização do cliente autenticado
- Data: 2026-09-19
- Estado: Concluída
- Commits: a2c2eae
- BÍBLIA/ADR: bootstrap/readiness/wizard (`docs/Reestructure/15-APPLICATION.md:1-46`); Telegram como operação de aplicação, não `Console.ReadLine` (`:35-46`); CLI/Dashboard usam os mesmos serviços (`:54-60`); DL-021

## Contexto / Objectivo
Achados F-05/F-21/F-23: um fresh install com `--telegram` falhava no login antes do Setup, bloqueando o arranque; o ciclo de vida do dashboard estava acoplado ao loop Telegram; havia duplicação de cliente Telegram. Objectivo: arranque que se mantém de pé sem Telegram até ao setup e reutilização do cliente já autenticado.

## Âmbito implementado
- Arranque mantém-se de pé sem Telegram (login não-fatal).
- O pipeline reutiliza o cliente já autenticado (`ITelegramClientProvider`, `TelegramNotAuthenticatedException`).
- Removido o estado estático `_fileConfig`.
- Documentação de deployment/operações actualizada.

## Ficheiros/componentes principais
- `m3uCrawler/Program.cs`
- `m3uCrawler/Services/Telegram/ITelegramClientProvider.cs`, `ITelegramAuthBackend.cs`, `TelegramAuthService.cs`, `WTelegramAuthBackend.cs`, `TelegramNotAuthenticatedException.cs`
- `m3uCrawler/Services/TelegramScraperService.cs`
- Testes: `WaveW5TelegramClientReuseTests.cs` (293 linhas)
- Docs: `DEPLOYMENT.md`, `OPERATIONS.md`, `m3uCrawler/README.md`, `CHANGELOG.md`

## Validação (build + suite + testes relevantes)
- Build Release: 0 errors / 52 warnings.
- Suite completa: 2136 passed / 1 flaky / 1 skipped (o flaky passa isoladamente; os testes de reutilização de cliente passam 9/9 isoladamente).

## Evidência
- Commit `a2c2eae` (`fix(install): fresh install stays up without Telegram and reuses the authenticated client`).
- Diff: 13 ficheiros, +634/-28.

## Divergências / dívida / follow-ups
- `runtime-data` com duas raízes (C-08/F-21) permanece pendente.
- `--web-allow-trigger` ausente do compose (F-22) permanece pendente.
