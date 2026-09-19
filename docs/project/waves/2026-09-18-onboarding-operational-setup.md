# Wave Onboarding — Setup operacional de primeira execução
- Data: 2026-09-18
- Estado: Concluída
- Commits: 39383fc, 661c8da, 3cce447, 22e7688, faa4ea2, fcd5e32, 99a2818
- BÍBLIA/ADR: bootstrap, readiness e wizard incrementais (`docs/Reestructure/15-APPLICATION.md:1-46`); CLI/Dashboard usam os mesmos serviços (`docs/Reestructure/15-APPLICATION.md:54-60`); secret lifecycle (`docs/Reestructure/14-CONFIGURATION.md:32-42`); configuração/segredos (`docs/Reestructure/39-CONFIG-SCHEMA.md`)

## Contexto / Objectivo
Tornar o arranque e a configuração inicial operáveis a partir da aplicação/dashboard, sem edição manual de ficheiros internos: armazenamento seguro de `wtelegram.config`, provisionamento de país, configuração/teste Dispatcharr read-only, autenticação Telegram por passos, readiness operacional com gate de scheduler, endpoints e UI de setup, e documentação.

## Âmbito implementado
| # | Commit | Componente | Validação |
|---|---|---|---|
| Onboarding 1 | 39383fc | `WtelegramConfigStore`; `CountryConfigProvisioner` | 1927/0/1; 0/52 |
| Onboarding 2 | 661c8da | `DispatcharrConfigurationService`; `DispatcharrConnectionTester` (read-only) | 1943/0/1; 0/52 |
| Onboarding 3 | 3cce447 | `TelegramAuthService` por passos (`ITelegramAuthBackend`, `WTelegramAuthBackend`, `TelegramAuthState`) | 1958/0/1; 0/52 |
| Onboarding 4 | 22e7688 | `OperationalReadinessService` + `ConfigurationGate`/gate de scheduler | 1979/0/1; 0/52 |
| Onboarding 5 | faa4ea2 | endpoints de configuração Telegram/Dispatcharr + readiness API | 1990/0/1; 0/52 |
| Onboarding 6 | fcd5e32 | painel "Setup Required" e vistas de setup no dashboard | 1994/0/1; 0/52 |
| Onboarding 7 | 99a2818 | documentação (docs-only) | n/d (sem código) |

## Ficheiros/componentes principais
- `m3uCrawler/Services/Configuration/WtelegramConfigStore.cs`, `CountryConfigProvisioner.cs`, `OperationalReadinessService.cs`, `ConfigurationGate.cs`
- `m3uCrawler/Services/Dispatcharr/DispatcharrConfigurationService.cs`, `DispatcharrConnectionTester.cs`
- `m3uCrawler/Services/Telegram/TelegramAuthService.cs`, `ITelegramAuthBackend.cs`, `WTelegramAuthBackend.cs`, `TelegramAuthState.cs`
- `m3uCrawler/Models/DispatcharrConfig.cs`, `m3uCrawler/Program.cs`, `m3uCrawler/Services/WebDashboardService.cs`
- Testes: `WtelegramConfigStoreTests`, `CountryConfigProvisionerTests`, `DispatcharrConfigurationServiceTests`, `DispatcharrConnectionTesterTests`, `TelegramAuthServiceTests`, `OperationalReadinessServiceTests`, `ConfigurationGateSchedulerTests`, `SetupConfigEndpointTests`, `WebDashboardSetupHtmlTests`
- Docs: `docs/architecture/configuration-lifecycle.md`, `m3uCrawler/README.md`, `docs/IMPLEMENTATION_ROADMAP.md`, `CHANGELOG.md`

## Validação (build + suite + testes relevantes)
- Evolução da suite: 1927 → 1943 → 1958 → 1979 → 1990 → 1994 passed; 0 failed / 1 skipped.
- Build Release: 0 errors / 52 warnings em todos os passos.

## Evidência
- Commits `39383fc`..`99a2818`; ver sub-tabela.
- `99a2818` é docs-only.

## Divergências / dívida / follow-ups
- Arranque com `--telegram` num fresh install ainda falhava antes do setup (resolvido em W5, `a2c2eae`).
- Readiness do Dispatcharr não testa ligação nem persiste o último teste (F-16; W6).
- `/api/dispatcharr/config` só edita um subconjunto de chaves (F-15; W6).
