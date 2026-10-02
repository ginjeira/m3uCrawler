# m3uCrawler — Project Status

> Ponto de entrada para o estado corrente da implementação (`docs/Reestructure/00-BIBLE.md:30-41`, `:105-128`).
> Documento derivado: descreve o que foi construído e validado; não redefine conceitos da BÍBLIA.
> Registos históricos das waves: `docs/project/waves/`.
> Reconciliação documental: 2026-10-02 — HEAD `675521b`; ver `BIBLE_IMPLEMENTABILITY_GAP_MANIFEST_1.0.md` Anexos G–T e `docs/Reestructure/46-REQUIREMENT-TRACEABILITY.md`.
> Reconciliação anterior: 2026-09-20 — HEAD `780fa01`.

## Current State
- Branch: `feature/phase-9c-first-run-dashboard`
- HEAD: `675521b817e1e04a6122b4a36410b2448f862c7f` (2026-10-02)
- Phase: 9C / 13 — implementação das waves da BÍBLIA (reconstrução)
- Wave: waves de reconstrução `W1–W5.6` implementadas; waves pós-reconstrução `W-DEDUP` (`9cffc9b`), `W-HISTWIN` (`5f86ef2`) e Proveniência (`fcd442e`) implementadas, testadas e validadas em execução real, e `W-DASHBOARD` (`675521b`) implementada, commitada e com testes determinísticos verdes (não validada em execução real; ver `## Waves 2026-10`); `M.4` (snapshot→Run/pipeline) OPEN, sem wave atribuída; `W6b-3` definida e pendente; dívida funcional de reconstrução em aberto (ver `## Functional Debt (reconstruction)`)
- Status: PARCIALMENTE CONFORME — waves A/B, Onboarding, Admin Password, W1/W2/W3s/W4a/W4b/W5, ADR-0001 (completo — pendente de aprovação), W6a/W6b-1/W6b-2/W6c, waves de reconstrução W1–W5.6 e waves 2026-10 (W-DEDUP/W-HISTWIN/Proveniência) concluídas; W-DASHBOARD commitada (`675521b`); `M.4`, C7, Review→Output, gap de `resolve` W5.5 e matriz de rastreabilidade pendentes; W2-FU totalmente fechado
- Versão/deploy: a release `1.0.1` foi **preparada localmente** (commit de release + tag anotada `v1.0.1`, sem push); o push e a actualização de produção **não** foram feitos (a produção mantém a imagem anterior). A execução real de 2026-10-02 correu num runtime de teste com build local de `fcd442e`.
- Last validated commit (histórico, wave 9c): `b19d95b` (2026-09-19)
- Last commit coberto por validação registada: `675521b` (W-DASHBOARD; build Release 0 errors / 54 warnings; suite 2826 passed / 0 failed / 1 skipped)
- Last validation (2026-10-02, HEAD `675521b`): build Release 0 errors / 54 warnings (baseline); suite completa 2827 testes — 2826 passed / 0 failed / 1 skipped (`HttpTimeoutAutopsyTests.LEGACY_PATTERN_blackhole...`, by design). A falha pré-existente `WaveW6b2ObservabilityTests.Dashboard_endpoints_return_newly_produced_runs_and_observations` (bomba-relógio de data) foi corrigida no commit de release (fix apenas de teste). Testes novos de W-DASHBOARD: 5 HTML (`WebDashboardWaveDashboardHtmlTests`), 6 de metadata/actividades (`LiveRunActivityMetadataTests`) e extensões a `DashboardMetricsTests` e camelCase de `ImportHistoryEntry`.

## Completed
| Phase | Wave | Result | Commit | Validation |
|---|---|---|---|---|
| 9C / 13 | 13-6 security | Sanitização de credenciais em `dispatcharr_report_*.json` (DL-020) | 26c91cd | 1917/0/1; 0 err / 52 warn |
| 9C / 13 | CI test fix | Classificação Linux `connection-refused` (test-only); build GHCR habilitado | 69b7779 | CI 35369150660 success; 1917/0/1 |
| 9C / 13 | Onboarding 1-7 | `WtelegramConfigStore`, `CountryConfigProvisioner`, Dispatcharr config/tester read-only, Telegram auth por passos, readiness + gate, endpoints/UI de setup, docs | 39383fc..99a2818 | 1927→1994/0/1; 0 err / 52 warn |
| 9C / 13 | Admin password 1-3 | Alteração de password, recovery no host, `POST /api/session/password`, docs | 419e23a, ab595a3, 899a030 | +11 testes; 2013/0/1 (flaky isolado) |
| 9C / 13 | Audit Wave A | Baseline canónico PT empacotado/embutido + `POST /api/catalog/channels` corrigido | 4dff2bf | 2023/0/1; 0 err / 52 warn |
| 9C / 13 | Audit Wave B | `Unknown` deixa de criar canal; aliases/affinity normalizados | 930215a | 2037/0/1; 0 err / 52 warn |
| 9C / 13 | Audit transversal | Findings F-01..F-27/D-F1..D-F16 + plano de waves (read-only) | (nenhum) | n/d |
| 9C / 13 | W1 | Discovery settings persistidas; `GET/POST /api/discovery/settings`; overrides; reload por run | 1ca9f1e | 2063/1 flaky/1 (isolado); 0 err / 52 warn |
| 9C / 13 | W2 | `RunPublicationService` partilhado; job id; gate por capacidade | 2f4b662 | 2070/0/1; 0 err / 52 warn |
| 9C / 13 | W3s | País só classificação; sem escrita em GET; readiness `countryData`; affinity com escopo | 2ac28d3 | 2090/0/1; 51 warn |
| 9C / 13 | ADR wave | 6 ADRs + índice (ADR-0003 Accepted; restantes Proposed) | 170acac | docs-only |
| 9C / 13 | W4a | DL-101 num único selector; priority `1` = preferido | fa1b593 | 2104/1 flaky/1; 52 warn |
| 9C / 13 | W4b | `ExternalIdentity` + migração aditiva; passo tvg-id no matching | 146b507 | 2131/0/1; 52 warn |
| 9C / 13 | W5 | Fresh install mantém-se de pé sem Telegram; cliente autenticado reutilizado | a2c2eae | 2136/1 flaky/1 (isolado 9/9); 52 warn |
| 9C / 13 | ADR-0001 completion | ADR-0001 completo: schema `country.json` v1, invariantes, migração/merge e contratos da API de país; `Proposed (completo — pendente de aprovação)` | 4de2773 | docs-only |
| 9C / 13 | W6a | `audit_records` + migração aditiva; `IAuditService` sanitizado e best-effort; `GET /api/audit` | 39eefe7 | 2148/0/1 |
| 9C / 13 | W6b-1 | Aprovação de Review aplica mudança explícita de catálogo (`add-alias`/`create-channel`/`exclude`), auditada e idempotente | db9959b | 2162/0/1 |
| 9C / 13 | W6b-2 | `SyncRun`+steps emitidos pelo sync Dispatcharr; `ChannelSourceObservation` por stream validado | ee7baab | 2170/0/1; 51 warn |
| 9C / 13 | W6c | Config Dispatcharr completa (patch/mascarada), teste de ligação persistido exigido pela readiness (≤24h, Connected); sync agendado aplica a selection | b19d95b | 2186/0/1; 51 warn |

## Reconstruction Waves (W1–W5.6)
Waves de reconstrução posteriores ao estado 9c registado acima. Gates registados em `docs/Reestructure/46-REQUIREMENT-TRACEABILITY.md` e `BIBLE_IMPLEMENTABILITY_GAP_MANIFEST_1.0.md` (Anexos G–S); **não re-executados nesta reconciliação documental**. `Estado` reflecte conformidade real, incluindo dívida funcional visível.

| Wave | Scope | Commit | Recorded gate | State |
|---|---|---|---|---|
| W1 | Identidade de discovery; resíduo de identidade de conta | `ab3f817` | n/d | Implementado; resíduo W1 (sem `FirstSeen`/`LastSeen`; `AccountIdentity.Compute` legado) — ver dívida 1 |
| W2 | Segurança de aquisição (SSRF/redirect/retry) | `ab3f817` | n/d | Testes de unidade verdes; W2-FU-1 FECHADO (cobertura de RunReport aggregation no Telegram live com sourceId=null); W2-FU-2A FECHADO 2026-09-23 (Option B — read-only `SourceId` resolution por `sourceKey`; keyword path); W2-FU-2 FECHADO 2026-09-23 por W2-FU-2-CLOSE (auditoria semântica dos 7 MUST_NOT_WIRE call sites; nenhum wiring com sourceId considerado semanticamente válido sob A/A/A; caminho Telegram canónico já fechado por W2-FU-2A) |
| W3 | Contrato de parsing M3U | `ab3f817` | 2302 passed / 1 skipped / 0 failed | Implementado |
| W4 | Normalização e fingerprinting de streams | `c73e134` | 2365 / 1 / 0 | Implementado; divergências de composição a jusante — ver dívida 4 |
| W4.1 | `SourceSelectionStage` ciente de fingerprint | `8142c0d` | 2377 / 1 / 0 | Implementado; divergências de output/selection a jusante — ver dívida 4 |
| W5.0 | Decisões normativas de Recognition/Fuzzy/Review (documental) | n/d | Documental, sem build/test (nenhuma alteração de código) | Baseline de decisões registada (`46-REQUIREMENT-TRACEABILITY.md` §W5.0) |
| W5.1 | `RecognitionPolicy` com resolução com escopo e snapshot de run | `5236364` | 2394 / 1 / 0 | Schema/snapshot implementados; sem integração de autoria/consumo em produção — ver dívida 5 |
| W5.2 | Ordem de reconhecimento determinística | `4721dc3` | 2413 / 1 / 0 | Implementado |
| W5.3 | Reconhecimento fuzzy e lifecycle de review | `84b35f5` | 2448 / 1 / 0 | Implementado, mas fuzzy depende de `RecognitionPolicy`; em produção `policy: null` → inalcançável (dependência M.4) — ver dívida 6 |
| W5.4 | Lifecycle de review | `84b35f5` | 2490 / 1 / 0 | Implementado; Review→Output não ligado — ver dívida 9 |
| W5.5 | API HTTP de review | `b8c9cda` | 2532 / 1 / 0 | Implementado; gap em `resolve` para `change.type` — ver dívida 8 |
| W5.6 | Semântica de confiança de matching | `b613502` | 2570 / 1 / 0 | Implementado |
| W5.6 follow-up (F7-B/F8-B/F9) | Follow-up de confiança de matching | `780fa01` | 2577 / 1 / 0 | Implementado |

## Waves 2026-10
Waves pós-reconstrução, implementadas, com testes determinísticos e validadas em execução real (ver `## Registo de execução real (2026-10-02)`). Não alteram a BÍBLIA nem os contratos de segurança (sanitização, `sfp1`, ownership). A **W-DASHBOARD** foi commitada em `675521b` e não foi validada em execução real (apenas testes determinísticos).

| Wave | Scope | Commit | State |
|---|---|---|---|
| W-DEDUP | Validação física deduplicada por run no caminho de descoberta Telegram: `ValidationKey = sfp1`; `Working` reutilizável; falhas nunca reutilizáveis entre `AccountKey`s; probe físico obrigatório ≥1 por conta elegível; `RunReport.StreamsTested` conta apenas validações físicas e o novo `StreamsSkippedAlreadyValidated` conta os GETs evitados; registo em memória, por run, não persistido. Detalhe normativo em `docs/Reestructure/08-VALIDATION.md §6`. | `9cffc9b` (2026-10-01) | Implementado e validado |
| W-HISTWIN | Janela de histórico Min/Max: `DiscoverySettings.MinHistoryHours` (default `0`) + `HistoryHours` = máximo; janela inclusiva `Min <= idade <= Max`; cutoffs do mesmo instante UTC por ciclo; paginação inalterada e filtro client-side; validação `Min <= Max` e `HistoryHours ∈ [1,1440]` (tecto alargado 720→1440); CLI `--min-history-hours`; `app_settings.json` → `discovery.minHistoryHours`; `GET/POST /api/discovery/settings` devolvem/aceitam `minHistoryHours`. A UI HTML do dashboard **não** expunha ainda estes parâmetros (superado pela W-DASHBOARD, 2026-10-02), e `POST /api/run/start` mantém o contrato antigo (sem `minHistoryHours`, tecto próprio 720h). | `5f86ef2` (2026-10-02) | Implementado e validado |
| Proveniência | `CandidatePlaylist.SourceMessageId`/`SourceMessageDateUtc`; `DiscoveredPlaylist.CandidateId`/`MessageId`/`MessageDateUtc`; helper `ApplyTelegramMessageProvenance` no loop de `ProcessOneTelegramMessageAsync` (source = chatTitle + id + data UTC); herança em `PromoteXtreamAccount` (fan-out HTML herda do pai; promoções `t.me/c` pós-enumeração usam o `messageId` referenciado com data `NULL`); exposto em `telegram_run_report.json` (`discoveredPlaylists`) e em `GET /api/discovered-playlists` (camelCase). Limitações: candidatos que falham antes do parse não geram linha; origem fora de mensagens enumeradas (ex.: `--scan-domain`) → proveniência `null`; `telegram_run_report.json` é sobrescrito por run. | `fcd442e` (2026-10-02) | Implementado e validado |
| W-DASHBOARD | Configuração de descoberta no Dashboard (card na vista Descoberta: `keyword`, `MinHistoryHours`, `HistoryHours`/Max, `MaxStreams`, via `GET/POST /api/discovery/settings`; janela inclusiva explicada; erros do backend inline; recarrega os valores persistidos) e Live View com contexto (activities com `metadata` no wire + `runId` nas fases; `category` como badge; cadeia `messageId`→`candidateId`→`parentCandidateId`; W-DEDUP visível: activity por ronda de conta + `StreamsSkippedAlreadyValidated` no Live Run/Overview/Execuções; proveniência nas tabelas de Descoberta e em `GET /api/discovery/summary`). Sem novo endpoint nem nova fonte de verdade (`app_settings.json#discovery`); deliberadamente não expostos: credenciais, deployment/restart, modos legacy e constantes técnicas (ver `m3uCrawler/README.md`). Detalhe em `docs/architecture/run-observability-and-manual-trigger.md` §18.10 e `docs/Reestructure/22-API-CONTRACTS.md`. | `675521b` (2026-10-02) | Commitado; testes determinísticos verdes, sem validação em execução real |

**Cadeia de observabilidade resultante.** `run → mensagem (messageId, messageDateUtc, chat em source) → candidateId → estado/workingStreams da playlist`. Nota: em modo CLI não existe `runId` operacional de coordenador; existe apenas o `runId` de diagnóstico do `PipelineTrace` (processo-scoped, só activo com `M3UCRAWLER_TRACE`). O `runId` operacional (`LiveRun`) só existe em runs via dashboard/scheduler. Não confundir os dois (ver `docs/Reestructure/13-RUNS.md §1` e `docs/Reestructure/31-DECISION-LOCK.md` DL-124).

## Registo de execução real (2026-10-02)
Execução real validada a 2026-10-02 num runtime de teste isolado (`/opt/m3ucrawler-first-test`, imagem local `m3ucrawler:first-real-test-fcd442e`, build de `fcd442e`), janela de histórico `Min=425`/`Max=450` horas, Dispatcharr em dry-run, exit 0. **Este registo é o home canónico**; os restantes documentos fazem cross-reference.

- **Contadores do `RunReport`:** `messagesAnalyzed` 296; `candidatesFound` 11; `playlistsDownloaded` 676; `playlistsInvalid` 83; `playlistsRejected` 18; `channelsRecognized` 5264; `streamsExtracted` 16 575 585; `streamsAfterCountryFilter` 76 318; `streamsTested` 865; `streamsWorking` 759; `streamsFailed` 106; `streamsSkippedAlreadyValidated` 15 585; `messagesWithMedia` 44; `messagesWithDocumentMedia` 29; `messagesWithPhotoMedia` 6; `documentDownloadSuccesses` 2; `documentDownloadFailures` 0; `dialogsTotal` 176; `dialogsIncomplete` 0; `dialogErrors` 0.
- **Artefactos produzidos:** playlist M3U, `telegram_report`, `telegram_run_report`, plano Dispatcharr e relatório Dispatcharr.
- **Dispatcharr (dry-run):** Matched 20; New channels 4; New streams 5387; Removed 0; Skipped 0; Ambiguous 1; Unchanged 0; Failed 0.
- **Exemplo de proveniência real** (no `telegram_run_report.json`): `source` "xtream publication"; `candidateId` `00685b14676642258fd9742b64524c86`; `messageId` `110751`; `messageDateUtc` `2026-09-14T01:11:01Z`; `state` `accepted`; `streamCount` 25362; `streamsAfterCountryFilter` 116; `workingStreams` 19. Pelo menos duas playlists derivaram da mesma mensagem `110751`.
- **Testes determinísticos associados:** `TelegramHistoryWindowBandTests` (W-HISTWIN), `CandidateMessageProvenanceTests` (Proveniência) e extensões a `WaveCDiscoverySettingsTests`; W-DEDUP tem testes próprios desde `9cffc9b`.

## In Progress
| Wave | Objective | Status | Branch | Commit |
|---|---|---|---|---|
| W6b-3 | Ligar/esconder funcionalidades inertes decididas: `import-policies`, `canonical-groups`/`group-mappings`, `pending-country-approvals` | Definida / pendente (a decidir quando ligar vs. esconder) | feature/phase-9c-first-run-dashboard | n/d |
| M.4 | Ligar o snapshot de `RecognitionPolicy` ao Run/pipeline (desbloqueia o fuzzy W5.3 em produção) | **OPEN** — sem wave atribuída; Decision Pack em preparação/ratificação; snapshot→Run/pipeline não wired. Implementação não decidida | feature/phase-9c-first-run-dashboard | n/d |
| W2-FU-1 | Wire do `CatalogAcquisitionFailureObserver` no Telegram live run (RunReport aggregation com `sourceId=null`) | **FECHADO** — observer instalado em `SearchAndTestM3UInTelegramAsync` via `SetCatalogResolver` + nova sobrecarga da factory; `sourceId` permanece `null` (persistência em `Source` fica para W2-FU-2) | feature/phase-9c-first-run-dashboard | n/d |
| W2-FU-2 | Source persistence para Telegram via bridge `peer/chat → SourceId` | **CLOSED** (2026-09-23 por W2-FU-2-CLOSE) — auditoria semântica dos 7 MUST_NOT_WIRE call sites concluída; nenhum wiring com `sourceId` considerado semanticamente válido sob A/A/A (Q9.1 sem peer identity, Q9.2 sem backfill, Q9.3 sem `SourceId` improvisation); caminho Telegram canónico já fechado por W2-FU-2A; `TelegramBotService /test` classificado como `WIRE_RUNREPORT_ONLY` mas diferido; restantes 6 (`Program.cs --scan-domain`, `Program.cs:1405/1412`, `ScheduledValidationAction`, `ScheduledM3uDiscoveryAction`, `ScheduledAutomationHost`, `/api/validation/test`) classificados como `DO_NOT_WIRE`. Ver `docs/Reestructure/07-SOURCES.md` §7 "Source identity boundary" para a matriz completa. | feature/phase-9c-first-run-dashboard | n/d |

## Pending
| Item | Origin | Dependency | Status |
|---|---|---|---|
| W6 — audit records + wires | Plano de waves (auditoria transversal) | ADRs (schemas/contratos), `17-SECURITY.md:45-53`, `22-API-CONTRACTS.md:52-54` | Concluída: W6a (39eefe7), W6b-1 (db9959b), W6b-2 (ee7baab), W6c (b19d95b); resta W6b-3 |
| Wire/hide de funcionalidades inertes: import-policies, canonical-groups/group-mappings, pending-country-approvals | F-12/D-F12 | Decisão de produto (ligar vs. esconder) | Pendente (W6b-3) |
| E2E de dois ciclos (idempotência: sem duplicar channels/streams/sources/ownership; sem apagar externos) | Critério de sucesso 7 | W6b-3 (restante) | Pendente |
| Descoberta: dedup por `AccountId`; `max-streams` por candidato | Perf (auditoria transversal) | Decisão de produto | Pendente |
| Raiz única de `runtime-data` | C-08 / F-21 | Decisão de deployment/compose | Pendente |
| Secret store isolado conforme ADR-0004 | ADR-0004 | Aprovação do ADR | Pendente |
| Seeding de `tvg-id` no baseline (`ExternalIdentity`) | W4b / ADR-0002 | Fonte de mapeamento tvg-id→canal + ADR-0002 | Pendente |
| Estabilizar testes flaky de timing (`XtreamAccountLockManagerTests`, `Phase93AccountGateCoordinatorTests`) | Dívida de testes | — | Pendente |
| Re-auditar os restantes ADRs `Proposed` (ADR-0002/0004/0005/0006) contra a Regra de Completude reforçada (`00-BIBLE.md` §5/§6). ADR-0001 re-auditado: **completo — pendente de aprovação**. | Governação BÍBLIA §5/§6 | Aprovação do proprietário | Em curso |
| Definir IDs de requisitos e preencher a matriz de rastreabilidade (`46-REQUIREMENT-TRACEABILITY.md`). IDs candidatos `CD-01`..`CD-16` introduzidos em `ADR-0001` §10. Linhas `TBD`/`UNMAPPED` (famílias de API) por preencher. | `46-REQUIREMENT-TRACEABILITY.md` | — | Pendente (IDs semeados; `TBD`/`UNMAPPED` por preencher) |
| M.4 — wiring do snapshot de `RecognitionPolicy` ao Run/pipeline (desbloqueia fuzzy W5.3 em produção) | Dívida de reconstrução 6/7 | Decision Pack (em preparação/ratificação) — implementação não decidida | **OPEN** (sem wave atribuída) |
| W2-FU — ligar `CatalogAcquisitionFailureObserver` em produção | Dívida de reconstrução 2 | Wiring do observer em `StreamValidationTesterFactory`/`WebDashboardService` | Resolvido: W2-FU-1 (2026-09-22), W2-FU-2A (2026-09-23), W2-FU-2 (2026-09-23 por W2-FU-2-CLOSE — fecho documental sem wiring adicional) |
| C7 — `ReviewItem` com `RunId`/`StreamId`/`Actor`/`Evidence`/`Candidates`/`Decision` | Dívida de reconstrução 10 | — | **OPEN** — não implementado |
| Review→Output — resolver Review deve produzir/seleccionar `ChannelSource`/output e re-publicar | Dívida de reconstrução 9 | M.4 / composição de output | Pendente (não ligado) |
| Gap de `resolve` W5.5 — suportar `change.type` `externalIdentity`/`channelSource`/`none` | Dívida de reconstrução 8 | Contrato de `resolve` | Pendente (`422 declared-change-invalid`) |

## Known Gaps / Divergences
IDs da auditoria transversal (`docs/project/waves/2026-09-18-audit-transversal-findings.md`).
`Resolvido` = endereçado num commit; `Parcial` = apenas parte; `Recalibrado` = não é divergência após revisão normativa; `Pendente` = por resolver.

| ID | Bible requirement | Implementation | Impact | Status |
|---|---|---|---|---|
| A1 | Catálogo é a autoridade; baseline distribuído é inicial (DL-001, DL-107) | Baseline PT empacotado/embutido e importado; log Warning se ausente | — | Resolvido (4dff2bf) |
| F-01 | Operações administrativas reais (DL-021) | `POST /api/catalog/channels` deixa de ser shadowed e cria | — | Resolvido (4dff2bf) |
| F-09 / C-04 | `Unknown` não cria identidade (DL-002) | `PipelineIngestionService` deixa de auto-criar canal a partir de `Unknown` | — | Resolvido (930215a) |
| B2 | Aliases são fonte de reconhecimento (`05-CATALOGUE.md:15-17`) | Aliases/affinity normalizados; pass idempotente de legado | — | Resolvido (930215a) |
| F-11 (reabertura) | Review lifecycle (DL-105) | `UpsertReviewItemAsync` não reabre decisão humana | — | Resolvido (930215a) |
| F7 (sep) | Priority preservada (DL-010) | `Source.Priority` preservado no ingest | — | Resolvido (930215a) |
| F-03 / F-04 | Mesmo significado em Dashboard/CLI/scheduler (`14-CONFIGURATION.md:44-46`) | Discovery settings persistidas e overrides aplicados | — | Resolvido (1ca9f1e) |
| F-17 / F-18 | Run snapshot / config actual (`13-RUNS.md:16-22`) | Reload por run de `DispatcharrConfig` e validation policy | — | Resolvido (1ca9f1e) |
| F-02 | Scheduler e manual usam o mesmo coordinator (DL-016) | `RunPublicationService` partilhado (CLI/dashboard/scheduler) | — | Resolvido (2f4b662) |
| F-06 | Gates por capacidade (`15-APPLICATION.md:12-22`) | Gate por capacidade; jobs não-Telegram não bloqueados | — | Resolvido (2f4b662) |
| F-20 | Scheduler usa o mesmo pipeline (DL-016) | `generatePlaylist` honra o `<id>` do job | — | Resolvido (2f4b662) |
| C-05 | Affinity de país sem identidade de canal (`06-COUNTRY-MEDIA.md:1-18`) | Affinity de país com escopo por pedido (fim do store global) | — | Resolvido (2ac28d3) |
| C-10 | Leituras não devem ter efeitos colaterais | GETs de país deixam de escrever (sem auto-provisão) | — | Resolvido (2ac28d3) |
| C-12 | `Validation` não altera identidade (DL-008) | Removido `ValidatePlaylist` legado | — | Resolvido (2ac28d3) |
| F-05 / F-23 | Fresh install sem passos manuais (`15-APPLICATION.md:1-10`) | Arranque mantém-se de pé sem Telegram; cliente reutilizado | — | Resolvido (a2c2eae) |
| D-F6 | DL-102, `1` = mais preferido | Determinismo DL-101 com priority corrigida; single selector | — | Parcial (fa1b593); `StreamOrderingPolicy` ainda faz preferência de provider (D-F5) |
| D-F8 | Selection referencia identidade canónica (`09-SELECTION.md`) | Selection agrupa por `Key` | — | Resolvido (fa1b593) |
| D-F9 | Um único mecanismo de selecção | `IChannelSourceSelector` único | — | Resolvido (fa1b593) |
| — | Matching passo 2: tvg-id/provider identity (`05-CATALOGUE.md:31-41`) | `ExternalIdentity` + passo no resolver; ambiguidade→Review | — | Resolvido (146b507); seeding pendente |
| D-F1 | `OrderingItem` referencia identidade | `OrderingItem` referencia `CanonicalChannelId` | — | Recalibrado: normativo (`32-DOMAIN-SCHEMA.md:203-209`), não é divergência |
| C-01 / C-02 | País não é catálogo (`06-COUNTRY-MEDIA.md:1-18`) | `AnalyzePlaylist` ainda sintetiza identidades a partir de aliases de país | País como catálogo paralelo | Pendente |
| C-03 / C-16 | Uma fonte de canais (`05-CATALOGUE.md:23-27`) | Listas PT hardcoded + `countries/*.json` + `channel-indicators.json` duplicam o catálogo | Duplicação de reconhecimento | Pendente |
| C-08 / F-21 | Raiz de runtime única | Duas raízes `<cwd>/runtime-data` vs `/data` | Edições do operador podem ser ignoradas | Pendente |
| F-07 / F-25 | SSOT de país | Duas fontes de país disjuntas (ficheiros vs `AffinityGroup` DB); UI mostra só uma | Inconsistência | Pendente |
| F-08 | Mesma instância de serviços (DL-021) | Gate de ingestão usa instância de `CountryChannelValidator` diferente do scraper | Inconsistência | Pendente |
| F-13 | Review que altera catálogo deve declarar a mudança (`05-CATALOGUE.md:63-67`) | Aprovação declara e aplica a mudança (`add-alias`/`create-channel`/`exclude`), auditada e idempotente | — | Resolvido (db9959b) |
| F-14 | Observation/histórico (DL-023) | `ChannelSourceObservation` gravada no pipeline para streams validados (dedupe `ChannelSourceId`+`ObservedAtUtc`) | — | Resolvido (ee7baab) |
| F-11 (sync-runs) | Observabilidade de runs (DL-023) | `DispatcharrSyncService` emite `SyncRun`+steps (`ok`/`partial`/`error`/`dry-run`) | — | Resolvido (ee7baab) |
| F-15 | Contratos de API (`22-API-CONTRACTS.md:1-46`) | `/api/dispatcharr/config` cobre todas as chaves `dispatcharr_*` (patch; segredos mascarados) | — | Resolvido (b19d95b) |
| F-16 | Readiness de Dispatcharr (`15-APPLICATION.md:48-52`) | Readiness exige teste de ligação persistido e recente (≤24h, `Connected`) | — | Resolvido (b19d95b) |
| F-22 | Dashboard operacional (`15-APPLICATION.md`) | `--web-allow-trigger` ausente do compose → "Run now" 503 | Feature inacessível | Pendente |
| F-27 | Handlers com método explícito (`22-API-CONTRACTS.md:5-14`) | Guardas de método/405 apenas parciais | Menor | Parcial (4dff2bf) |
| D-F2 | Unicidade de policy (`16-PERSISTENCE.md:19-30`) | Prioridade por `Id` vs selection por `Key`; unicidade de `Scope` inconsistente | Risco de duplicados | Pendente |
| D-F3 | `ChannelSource` com chave estável (`32-DOMAIN-SCHEMA.md:150-163`) | `StreamUrl` sanitizada usada como identidade | Identidade frágil | Pendente |
| D-F4 | Matching determinístico (`05-CATALOGUE.md:29-41`) | Streams Dispatcharr associados por nome normalizado | Associação frágil | Pendente |
| D-F5 | Ordering ≠ Priority (DL-010, DL-011) | `StreamOrderingPolicy` faz ordenação e preferência de provider | Conceitos misturados | Pendente |
| D-F10 / F16 | Output usa a selection | Sync agendado aplica a selection persistida com o mesmo `SourceSelectionStage` do caminho manual | — | Resolvido (b19d95b) |
| D-F12 | Entidades com consumidor | `ImportPolicy`/`CanonicalGroup.Order`/`GroupMapping` sem consumidor runtime | Funcionalidade inerte | Pendente (W6b-3) |
| D-F14 | Constraints de invariantes (`16-PERSISTENCE.md:19-30`) | Índices de `channel_sources` não únicos → duplicados sob concorrência | Risco de duplicados | Pendente |
| ADR-0001 | Regra de Completude (`00-BIBLE.md` §5/§6) | ADR-0001 completo: schema `country.json` v1, invariantes, migração/merge e contratos da API de país; permanece `Proposed (completo — pendente de aprovação)` | — | Já não é `BIBLE_GAP`; Resolvido (4de2773) |
| Doc | Documentação derivada reflecte a BÍBLIA (`00-BIBLE.md:39-41`) | README/roadmap ainda descrevem país como fonte de aliases e ordem de matching inexistente | Documentação divergente | Pendente |
| Testes | Quality gates (`44-QUALITY-GATES.md`) | Flaky de timing; cobertura de endpoints esparsa | Regressões difíceis de isolar | Pendente |

## Functional Debt (reconstruction)
Dívida funcional verificada no código após as waves de reconstrução. Cada item tem um ponteiro de evidência de uma linha. Esta secção não decide nem ratifica `M.4`.

| # | Item | Evidência |
|---|---|---|
| 1 | W1 residual: `DiscoveryCandidateEntity` persistido (tabela `discovery_candidates`) sem `FirstSeen`/`LastSeen` (by design); `AccountIdentity.Compute` legado (URL-sem-password + username) mantido para serialização de trabalho ao lado de `AccountKey.Compose` (namespace + identidade externa) | `ChannelCatalogDbContext.cs:336`; `AccountIdentity.Compute` |
| 2 | W2-FU-1 FECHADO 2026-09-22: `CatalogAcquisitionFailureObserver` ligado em produção no caminho Telegram live com `sourceId=null` (RunReport aggregation). **W2-FU-2A FECHADO 2026-09-23** (Option B — read-only `SourceId` resolution por `sourceKey` determinístico): caminho run-keyword (Telegram discovery) persiste `Source.LastAcquisitionFailure*` quando a `Source` já existe, e cai para RunReport-only quando ausente; `Source` continua a ser criada exclusivamente por `EnsureSourceAsync`. **W2-FU-2 FECHADO 2026-09-23** por W2-FU-2-CLOSE (auditoria semântica dos 7 MUST_NOT_WIRE call sites; nenhum wiring com `sourceId` considerado semanticamente válido sob A/A/A; caminho canónico já fechado por W2-FU-2A). | `TelegramScraperService.cs` (wiring via `SetCatalogResolver`; W2-FU-2A: `GetSourceIdByKeyAsync` em `CatalogResolver.cs`); `StreamValidationTesterFactory.cs` (sobrecarga) |
| 3 | W2 testado verde em unidade (SSRF/redirect/retry) — mantém-se implementado; a dívida é apenas o wiring do observer em produção | testes de unidade W2 |
| 4 | W4/W4.1: fingerprint + `SourceSelectionStage` ciente de fingerprint implementados; composição de output/selection mantém divergências documentadas a jusante | `c73e134`, `8142c0d` |
| 5 | W5.1: schema/snapshot de `RecognitionPolicy` implementado, mas sem integração de autoria/consumo em produção | `5236364` |
| 6 | W5.3: fuzzy depende de `RecognitionPolicy`; em produção `CatalogResolver.ResolveAsync` é sempre chamado com `policy: null`, logo fuzzy é inalcançável em runs reais (dependência M.4) | `84b35f5`; `CatalogResolver.ResolveAsync(policy: null)` |
| 7 | M.4 = wiring do snapshot de política ao Run/pipeline. Estado: **OPEN**, sem wave atribuída, Decision Pack em preparação/ratificação (como/quando implementar não está decidido) | — |
| 8 | W5.5 gap de `resolve`: `change.type` `externalIdentity`/`channelSource`/`none` devolve `422 declared-change-invalid`; apenas `channelAlias`/`canonicalChannel` suportados | `b8c9cda`; handler de `resolve` |
| 9 | Review→Output não ligado: resolver um Review aplica efeitos laterais de catálogo (alias/channel) mas não produz/selecciona `ChannelSource` nem output; sem trigger de re-publicação | `b8c9cda`; `84b35f5` |
| 10 | C7 (`ReviewItem` com `RunId`/`StreamId`/`Actor`/`Evidence`/`Candidates`/`Decision`) permanece **OPEN** — não implementado, não inventado | modelo `ReviewItem` |
| 11 | Rastreabilidade: matriz de requisitos com linhas `TBD`/`UNMAPPED` (famílias de API) ainda por preencher | `docs/Reestructure/46-REQUIREMENT-TRACEABILITY.md` |
| 12 | Matcher legado (`ChannelMatcher`/`MatchScorer`/`MatchingOptions`) ainda usado pelo caminho de sync Dispatcharr; permanece divergente | `ChannelMatcher`, `MatchScorer`, `MatchingOptions` |
| 13 | Reabertura automática de Review (`Resolved`/`Ignored`→`Open` em "evidência materialmente incompatível") permanece **OPEN** — critério de "evidência materialmente incompatível" não definido; distinto da reabertura manual já implementada | `UpsertReviewItemAsync` (manual); critério automático não definido |

## Decisions Since Last Status
ADRs (`docs/adr/README.md`):
- ADR-0001 country-data-ownership — Proposed (completo — pendente de aprovação; completude em 4de2773)
- ADR-0002 stream-fingerprint-canonicalization — Proposed
- ADR-0003 source-selection-ranking — Accepted
- ADR-0004 secret-storage-lifecycle — Proposed
- ADR-0005 catalog-baseline-vs-runtime — Proposed
- ADR-0006 sqlite-migration-rollback — Proposed

Nota de governação (BÍBLIA §5/§6): a BÍBLIA passa a exigir que os ADRs sejam **COMPLETOS** (`00-BIBLE.md` §5/§6); um ADR `Proposed` não é normativo e não deve ser implementado. O ADR-0001 foi re-auditado, está **completo — pendente de aprovação** e já **não é `BIBLE_GAP`**; permanece `Proposed` até aprovação do proprietário.

Recalibrações normativas (não alteram a BÍBLIA; clarificam leitura):
- `OrderingItem` referencia `CanonicalChannelId` é normativo (`32-DOMAIN-SCHEMA.md:203-209`); não é divergência (D-F1).
- `ChannelSource` é único por `(CanonicalChannelId, SourceId)` (`32-DOMAIN-SCHEMA.md:162-163`; `16-PERSISTENCE.md:26`).
- Priority inteiro com `1` = mais preferido (DL-102, `31-DECISION-LOCK.md:100-101`).
- Ordem de Selection lexicográfica fechada em DL-101 (`31-DECISION-LOCK.md:86-98`), documentada por ADR-0003.

## Latest Validation

**Coverage actual:** último commit coberto por validação registada — `675521b` (W-DASHBOARD); build Release 0 errors / 54 warnings; suite completa 2827 — 2826 passed / 0 failed / 1 skipped. Mantém-se a registada anteriormente para `780fa01` (W5.6 follow-up; `2577 passed / 1 skipped / 0 failed`) em `docs/Reestructure/46-REQUIREMENT-TRACEABILITY.md` e no manifest.

**Falha conhecida (não escondida):** nenhuma. A falha pré-existente `WaveW6b2ObservabilityTests.Dashboard_endpoints_return_newly_produced_runs_and_observations` (bomba-relógio de data) foi corrigida no commit de release (fix apenas de teste). O skip é `HttpTimeoutAutopsyTests.LEGACY_PATTERN_blackhole_blocks_until_30s_HttpClient_timeout` (by design).

**Histórico (fase 9c, commit `b19d95b`):**
- Build Release (`--no-incremental`): 0 errors / 51 warnings.
- Suite completa: 2186 passed / 0 failed / 1 skipped (total 2187).
- Skipped (by design): `HttpTimeoutAutopsyTests.LEGACY_PATTERN_blackhole_blocks_until_30s_HttpClient_timeout`.
- Flaky conhecidos que passam isoladamente (28/28): `XtreamAccountLockManagerTests`, `Phase93AccountGateCoordinatorTests`.

## Next Executable Step
Não existe passo único: os próximos passos são independentes e nenhum deve iniciar implementação de `M.4` sem Decision Pack ratificado.

0. **A release `1.0.1` está preparada LOCALMENTE** (commit de release + tag anotada `v1.0.1`; W-DASHBOARD já commitada em `675521b`). O passo seguinte passa a ser **publicar (push do branch e da tag) e, depois, actualizar a produção** — wave separada. A `1.0.1` ainda **não** foi publicada remotamente e a produção **não** foi actualizada.
1. `M.4` — **OPEN**: ratificar o Decision Pack do wiring do snapshot de `RecognitionPolicy` ao Run/pipeline (desbloqueia o fuzzy W5.3 em produção). Não existe desenho de implementação decidido.
2. Dívida funcional de reconstrução: C7 (campos de `ReviewItem`); Review→Output (produzir/seleccionar `ChannelSource`/output e re-publicar); gap de `resolve` W5.5 (`externalIdentity`/`channelSource`/`none`); preencher linhas `TBD`/`UNMAPPED` da matriz de rastreabilidade. W2-FU totalmente fechado (W2-FU-1 2026-09-22, W2-FU-2A 2026-09-23, W2-FU-2 2026-09-23 por W2-FU-2-CLOSE — fecho documental sem wiring adicional).
3. W6b-3 (ligar/esconder funcionalidades inertes: `import-policies`, `canonical-groups`/`group-mappings`, `pending-country-approvals`).
4. E2E de dois ciclos: deploy do HEAD para o runtime fresco e execução Discovery→Selection→Playlist→Dispatcharr duas vezes, com verificação de idempotência (sem duplicar channels/streams/sources/ownership; sem apagar externos).
