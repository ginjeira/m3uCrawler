# Wave W6c — Config Dispatcharr completa, teste de ligação persistido e sync agendado com selection
- Data: 2026-09-19
- Estado: Concluída
- Commits: b19d95b
- BÍBLIA/ADR: contratos de API (`docs/Reestructure/22-API-CONTRACTS.md:1-46`); readiness de Dispatcharr (`15-APPLICATION.md:48-52`); output usa a selection (`09-SELECTION.md`); paridade scheduler/manual (DL-016)

## Contexto / Objectivo
F-15/F-16 e D-F10/F16: `/api/dispatcharr/config` só editava um subconjunto de chaves, o teste de ligação não era persistido nem exigido pela readiness, e o sync agendado podia ignorar a selection persistida. Objectivo: fechar a configuração, tornar o teste de ligação um gate de readiness e dar paridade de selection ao caminho agendado.

## Âmbito implementado
- `DispatcharrConfigurationService`: cobre todas as chaves `dispatcharr_*` lidas pelo `DispatcharrConfigLoader` (`enabled`, `base_url`, `dry_run`, `api_key`, `username`, `password`, `match_threshold`, `target_group_name`, `alias_file`, `auto_create_groups`, `provider_priority`); escrita por patch (`null` mantém o valor, string vazia limpa); credenciais nunca devolvidas por `GetForDisplay` (apenas existência).
- `DispatcharrConnectionTestStore` persiste o último teste no `AppSettingsStore` (`runtime-data/app_settings.json`, chave `dispatcharrTest`): status, versão e instante UTC; sem segredos.
- `OperationalReadinessService` exige, quando Dispatcharr está activado, um teste bem sucedido e recente: `Connected` e idade ≤ `DispatcharrTestMaxAge` (24h); mais antigo → `stale`; ausente → "ligação ainda não testada".
- `ScheduledDispatcharrSyncAction` resolve a selection a partir da política persistida e aplica-a com o mesmo `SourceSelectionStage` do caminho manual; ausência é registada explicitamente (`selection=none` / step `selection/skipped`) em vez de ignorada.
- `Program.cs`/`WebDashboardService.cs`: wiring do store e registo de auditoria `dispatcharr.config.update`/`dispatcharr.test`.

## Ficheiros/componentes principais
- `m3uCrawler/Services/Configuration/DispatcharrConfigurationService.cs`, `DispatcharrConnectionTestStore.cs`, `OperationalReadinessService.cs`, `AppSettingsStore.cs`
- `m3uCrawler/Services/Automation/ScheduledDispatcharrSyncAction.cs`
- `m3uCrawler/Services/WebDashboardService.cs`, `m3uCrawler/Program.cs`
- Testes: `DispatcharrConfigurationServiceTests.cs`, `DispatcharrConnectionTestStoreTests.cs`, `OperationalReadinessServiceTests.cs`, `SetupConfigEndpointTests.cs`, `WaveW6cScheduledDispatcharrSelectionTests.cs`
- Docs: `m3uCrawler/README.md` (via commits da wave)

## Validação (build + suite + testes relevantes)
- Build Release: 0 errors / 51 warnings.
- Suite completa: 2186 passed / 0 failed / 1 skipped.
- Cobertura: patch/mascaramento das chaves, persistência do teste, gate de readiness (connected/stale/ausente) e paridade de selection no sync agendado.

## Evidência
- Commit `b19d95b` (`fix(dispatcharr): complete config, persist connection test, selection-aware scheduled sync`).
- Diff: 13 ficheiros, +1044/-62.

## Divergências / dívida / follow-ups
- D-F2/D-F3/D-F4/D-F5/D-F14 permanecem pendentes (unicidade de policy, identidade de `ChannelSource`, matching determinístico, separação Ordering≠Priority, constraints).
- `--web-allow-trigger` ausente do compose (F-22) permanece pendente.
