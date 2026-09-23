# 46 — Requirement Traceability

## Finalidade

Este documento liga a especificação normativa à implementação verificável.

A matriz deve permitir responder:

> "Onde está definido este requisito, onde está implementado, que teste o demonstra e qual é o acceptance gate correspondente?"

## Estados

- `UNMAPPED` — ainda sem mapeamento;
- `COMPLIANT` — implementação e evidência conformes;
- `PARTIAL` — apenas parte demonstrada;
- `MISSING` — não implementado;
- `DIVERGENT` — implementação contradiz a BÍBLIA;
- `BLOCKED` — dependência/decisão impede conclusão;
- `BIBLE_GAP` — requisito não é suficientemente especificado.

## Formato

| Requirement ID | Fonte normativa | Conceito | Implementação | Teste | Acceptance Gate | Estado | Gap/Nota |
|---|---|---|---|---|---|---|---|
| TBD | `xx-DOC.md §x` | TBD | TBD | TBD | TBD | UNMAPPED | |
| TBD | `xx-DOC.md §x` | TBD | TBD | TBD | TBD | UNMAPPED | |

A matriz deve materializar a cadeia `Requirement ID → Bible section → Domain/API/schema → implementação → teste → evidência → gate`. Onde um elemento for desconhecido, a célula correspondente mantém-se `TBD`; não preencher por inferência.

## Regras

1. O requisito deve apontar para uma secção normativa concreta.
2. A implementação deve apontar para ficheiro/classe/método quando conhecido.
3. O teste deve demonstrar comportamento, não apenas existência de código.
4. O acceptance gate deve ser o critério que permite fechar o requisito.
5. Não marcar `COMPLIANT` apenas porque existe uma classe com nome semelhante.
6. Quando a implementação divergir, preservar a evidência da divergência.
7. A matriz deve ser actualizada durante cada wave relevante.
8. Cada requisito deve ser mapeável como `Requirement ID → Bible section → Domain/API/schema → implementação → teste → evidência → gate`.
9. Toda a alteração normativa deve ter uma decisão de origem e os artefactos afectados identificados.

## Primeiro preenchimento

A primeira versão completa desta matriz deve ser produzida pelo **BIBLE AUDIT** do código actual.

Não preencher por inferência antes da auditoria.

## Regra de reconstrução

Um requisito sem implementação não é automaticamente um bug: pode ser uma wave futura.

Um requisito com implementação contraditória é uma divergência que deve ser analisada antes de continuar downstream.

## Round 2 — decisões aplicadas

Bloco de rastreabilidade das seis decisões Round 2 (Q-A..Q-F). A implementação e o teste não foram localizados no estado actual do repositório, pelo que as células correspondentes ficam `ausente`; cada teste indica apenas a demonstração prevista. Não preencher por inferência.

| ID | Decisão | Fonte normativa | Contrato/API | Área | Implementação | Teste |
|---|---|---|---|---|---|---|
| Q-A | Acquisition failure classification | `19-FAILURE-MODEL.md` | Source aggregation | acquisition | `ausente` | `ausente` — teste previsto: terminal vs retryable |
| Q-B | Recognition/Review | `05-CATALOGUE.md`, `38-POLICIES.md`, `33-STATE-MACHINES.md` | Review API (`22-API-CONTRACTS.md` §7) | recognition | `ausente` | `ausente` — teste previsto: fuzzy policy + resolve/ignore effects |
| Q-C | Validation→Eligibility | `08-VALIDATION.md`, `33-STATE-MACHINES.md` | Eligibility data/API | validation | `ausente` | `ausente` — teste previsto: category mapping + recovery |
| Q-D | Ordering/Output | `10-ORDERING.md`, `11-OUTPUT.md`, `23-DATA-CONTRACTS.md` | Ordering/Playlists API | output | `ausente` | `ausente` — teste previsto: uniqueness, unlisted, publication state, stable path |
| Q-E | Config/Policies | `14-CONFIGURATION.md`, `38-POLICIES.md`, `39-CONFIG-SCHEMA.md`, `35-SECURITY-MODEL.md` | Policies/Config API | config | `ausente` | `ausente` — teste previsto: Authority/ACL/merge |
| Q-F | Backup/Restore | `20-OPERATIONS.md`, `17-SECURITY.md`, `16-PERSISTENCE.md` | Operations API | operations | `ausente` | `ausente` — teste previsto: secrets excluded + migration order |

## DG-19 — Contratos de API (fechamento)

Fichas normativas em `22-API-CONTRACTS.md` (secções 6–21) para cada família do inventário `41-API-INVENTORY.md`. Onde a BÍBLIA não define rota/método/payload, a célula fica `TBD` no contrato; a implementação e o teste não foram localizados, pelo que ficam `ausente`.

| Requirement ID | Fonte normativa | Conceito | Contrato/API | Implementação | Teste | Estado |
|---|---|---|---|---|---|---|
| API-System | `41` System | lifecycle/readiness/health/version | `22` §9 | `WebDashboardService.cs:579, 605, 9911` (lifecycle/version; readiness parcial) | `WebDashboardService` unit tests | PARTIAL | Gap: `GET /api/health` (ready/degraded) não implementado; lifecycle/version presentes. |
| API-Auth | `41` Auth; `35`; `17` | login/logout/current user/CSRF | `22` §10 | `WebDashboardService.cs:506–511, 9032–9126` (login/logout/current user/CSRF; token optional auth) | `DashboardPasswordChangeEndpointTests.cs`; auth endpoint tests | COMPLIANT | Implementação completa (login, logout, current user, CSRF em mutações). |
| API-Telegram | `41` Telegram; `14` | configuração/sessão Telegram | `22` §11 | `WebDashboardService.cs:9648–9791` (status, connect, auth flow; session/config) | Telegram dashboard tests | PARTIAL | Gap: `POST /api/telegram/auth/disconnect` não implementado (logout Telegram não exposto). |
| API-Sources | `41` Sources; `07`; `14` | CRUD/enable/test/status | `22` §12 | `WebDashboardService.cs:2658–2744` (CRUD/enable/test/status) | Source endpoint tests | COMPLIANT | Implementação completa. |
| API-Discovery | `41` Discovery; `03` | start/list/details/accept-reject | `22` §13 | `WebDashboardService.cs:789–824, 945–955` (start/list) | Discovery endpoint tests | PARTIAL | Gap: accept/reject HTTP + candidate details endpoint não implementados. |
| API-Catalogue | `41` Catalogue; `05`; `23` | channels/aliases/external identities/import-export | `22` §14 | `WebDashboardService.cs:968–996, 2591–2622, 2932–3145` (channels/aliases/external identities) | Catalogue endpoint tests | PARTIAL | Gap: import/export HTTP não implementados. |
| API-Country | `41` Country; `06`; `22 §6` | países/validação/save | `22` §6, §21 | `WebDashboardService.cs:621–697` (countries/validation/save) | Country endpoint tests | COMPLIANT | Implementação completa. |
| API-Review | `41` Review; `05`; `33` | list/details/resolve/ignore/reopen | `22` §7 | `WebDashboardService.cs:1299–1308, 3443–3467` (list/detail/ignore/reopen; resolve via handle) | `WaveW55ReviewApiTests` | COMPLIANT | `422 declared-change-invalid` documentado em `22-API-CONTRACTS.md:228–233` como comportamento contratualmente definido (DL-120; não é gap de implementação a corrigir). Ver `ReviewApprovalEndpointTests.cs`. |
| API-Validation | `41` Validation; `08` | run/observations/eligibility | `22` §15 | `WebDashboardService.cs:3194–3291` + `2598–2622` (run/observations; eligibility ad-hoc) | Validation endpoint tests | PARTIAL | Gap: `GET /api/validation/eligibility` não implementado como endpoint dedicado. |
| API-Policies | `41` Policies; `38` | CRUD/effective preview | `22` §16 | `WebDashboardService.cs:1936–2242, 2427–2567` (CRUD/effective preview) | Policy endpoint tests; `WaveW51RecognitionPolicyTests` | COMPLIANT | Implementação completa. |
| API-Ordering | `41` Ordering; `10` | lists/items/import-export/preview | `22` §17 | `WebDashboardService.cs:1702–1936` (lists/items/preview) | Ordering endpoint tests | PARTIAL | Gap: import/export HTTP não implementados. |
| API-Runs | `41` Runs; `13` | start/cancel/status/history/artifacts | `22` §18 | `WebDashboardService.cs:573–955` (start/status/history) | Run endpoint tests | PARTIAL | Gap: `POST /api/run/cancel` + `GET /api/run/{id}/artifacts` não implementados. |
| API-Playlists | `41` Playlists; `11`; `23` | outputs/download/validation result | `22` §19 | `WebDashboardService.cs:699–773, 919–…` (outputs/download) | Playlist endpoint tests | PARTIAL | Gap: rota dedicada de validation-result não implementada. |
| API-Dispatcharr | `41` Dispatcharr; `12` | config/test/dry-run/sync/reconciliation/ownership | `22` §20 | `WebDashboardService.cs:900–902, 9797–9909` + `DispatcharrSyncCoordinator` (config/test) | Dispatcharr endpoint tests; `DispatcharrConnectionTesterTests` | PARTIAL | Gap: dry-run/sync/reconciliation/ownership HTTP não expostos via dashboard endpoints. |

Nota: `DG-19c` (concorrência/limites de Country) está especificado em `22-API-CONTRACTS.md` §21; valores numéricos de rate limit permanecem `PARAMETER_GAP`.

## BIBLE AUDIT — estado real da implementação (DG-21)

Auditoria read-only do código actual (`m3uCrawler/`, `m3uCrawler.Tests/`) contra a BÍBLIA. O código é evidência; nunca autoridade. Células preenchidas apenas com evidência localizada; `MISSING` quando não existe implementação/teste.

### Quality gates

```text
dotnet build m3uCrawler.sln --configuration Release --no-restore  → PASS (0 erros, 52 avisos)
dotnet test m3uCrawler.Tests --configuration Release --no-build    → PASS (2186 passed, 1 skipped, 0 failed)
```

### Matriz (por ordem de pipeline)

| Requirement | Bible | Implementação (evidência) | Teste | Estado |
|---|---|---|---|---|
| DiscoveryCandidate ocorrência/Run + ProviderAccountId | 03 §1, 32 | `CatalogEntities.cs:815` (entidade persistida, sem FirstSeen/LastSeen); `CatalogResolver.cs:2104` | `W1AccountIdentityModelTests.cs:T1,T6` | PARTIAL |
| AccountKey = namespace + identidade funcional | 03 §3, 32 | `AccountIdentity.cs:212-245` (`AccountKey.Compose`, NFKC+trim; namespaces em `:245`) | `W1AccountIdentityModelTests.cs:T4,T5` | PARTIAL (lock identity legado) |
| Candidate→Source explícito | 03 §2 | `PipelineIngestionService.cs:190-198`; `CatalogEntities.cs:815` (SourceId) | `PipelineIngestionBridgeTests.cs:82-102`; `W1AccountIdentityModelTests.cs:T1` | COMPLIANT |
| Dedup por identidade funcional | 03 §3 | `CatalogResolver.cs:2140` (dedup por RunId+ProviderAccountId); `PipelineIngestionService.cs:436` | `W1AccountIdentityModelTests.cs:T1,T2,T3` | PARTIAL (sem identidade → não dedup, conservador) |
| M3U input mínimo (#EXTM3U + #EXTINF + URL http/https) | 04 §7 | `M3uParserService.ParseDetailed` (cabeçalho obrigatório na primeira linha não vazia; entrada válida = `#EXTINF` + URL absoluto http/https) | `WaveW3M3uParsingContractTests.cs` | COMPLIANT |
| Entrada malformada registada + parsing parcial | 04 §7, 19 | `M3uParserService.ParseDetailed` (`M3uParseDiagnostic` Malformed; parsing continua) | `WaveW3M3uParsingContractTests.cs` | COMPLIANT |
| `Playlist.Status` tri-state | 04 §7, 32 | `Models/M3uPlaylistStatus.cs`; `M3uParseResult.Status`; consumidores adaptados (`CountryChannelValidator`, `TelegramScraperService`, `PlaylistReader`, `PlaylistManagerService`) | `WaveW3M3uParsingContractTests.cs` | COMPLIANT (modelo in-memory; sem persistência inventada) |
| Estrutura vs validade do alvo | 04 §7 | `M3uParserService.ParseDetailed` (`UnusableTarget` ≠ `Malformed`); `M3uParseResult.UnusableTargetCount` | `WaveW3M3uParsingContractTests.cs` | COMPLIANT |
| Normalização por campo | 04 §3 | `Matching/ChannelNormalizer.cs:20-65`; `GroupNormalizer.cs:48-54`; `Catalog/ExternalIdentityNormalizer.cs:54-64` | `ChannelNormalizerTests.cs:19-62` | PARTIAL/DIVERGENT |
| Stream fingerprint UTF-8/SHA-256/versionado | 04 §4/§4.1, 32 | `Matching/StreamFingerprint.cs` (`sfp1`; canonicalização + política de credenciais); `ChannelSourceEntity.Fingerprint`/`FingerprintVersion`; migração `20260919152739_AddChannelSourceStreamFingerprint` | `WaveW4StreamFingerprintTests.cs`; `WaveW4ChannelSourceFingerprintTests.cs` | COMPLIANT |
| Falha de aquisição: mapping + Source + Run | 19 §6 | `AcquisitionFailure.cs`; `AcquisitionFailureObserver.cs`; `CatalogResolver.MarkSourceAcquisitionFailureAsync`; `SourceEntity:917-922`; `RunReport:228-233` | `WaveW2AcquisitionRetryTests.cs`; `WaveW2AcquisitionPersistenceTests.cs` | COMPLIANT |
| Retry técnico vs reexecução | 13 §8, 19 §4 | `M3uTesterService.cs` (retry download + probe); `LiveRun/RunCoordinator.cs:118` | `Phase94RunCoordinatorTests.cs:128`; `WaveW2AcquisitionRetryTests.cs` | COMPLIANT |
| SSRF (protocolo/IP/DNS/redirect/fail-closed) | 17 §2 | `Validation/SsrfGuard.cs`; `AddressClassifier.cs`; `SafeConnector.cs`; `GuardedHttpRequest.cs`; `HttpClientFactory.cs:83-86` | `WaveW2SsrfGuardTests.cs` | COMPLIANT |
| Ordem de reconhecimento determinística | 05 §4 | `Catalog/CatalogResolver.cs:92-189` | `ExternalIdentityResolutionTests.cs:280-333` | COMPLIANT |
| Unknown/Ambiguous→Review; sem identidade implícita | 05 §4, DL-002/003 | `PipelineIngestionService.cs:306-347` | `UnknownExactMatchOnlyTests.cs:59-80` | COMPLIANT |
| Fuzzy off por defeito + RecognitionPolicy | 05 §4, 38 | `ChannelMatcher.cs:24,792-837` (fuzzy sempre activo); `MatchingOptions.cs:5` | MISSING | DIVERGENT |
| Abaixo do threshold UNKNOWN/AMBIGUOUS | 05 §4 | `MatchScorer.cs:10-19`; `ChannelMatcher.cs:828-836` | `FuzzyMatcherTests.cs:76-80` | DIVERGENT |
| Review Resolve explícito/auditado | 05 §4, 33, 22 §7 | `CatalogResolver.cs:943-1159` (só alias/create/exclude) | `ReviewApprovalEndpointTests.cs:174-222` | PARTIAL |
| Review Ignore exige motivo | 05 §4, 22 §7 | `CatalogResolver.cs:1161-1199` (motivo default) | MISSING | PARTIAL |
| ReviewItem estados Open/InReview/Resolved/Ignored | 33, DL-105 | `CatalogEntities.cs:401-406` (`Open/Approved/Excluded`) | MISSING | DIVERGENT |
| Reopen auditado (DL-105) | DL-105, 22 §7 | `CatalogResolver.cs:311-320` (proíbe reabrir) | `ReviewApprovalEndpointTests.cs:494-521` | MISSING |
| Múltiplas streams por (canal, source); identidade = canal+source+fingerprint+versão | 07 §5, 32, 16 | `ChannelCatalogDbContext.cs` (índice não único em `(CanonicalChannelId, SourceId)`; índice não único por fingerprint); dedup por fingerprint em `CatalogResolver.RecordChannelSourceAsync` | `WaveW4ChannelSourceFingerprintTests.cs` | COMPLIANT (reconciliado em W4; conflito `16:27`/`32:207` vs `07:48` preservado nas notas) |
| Observation factual/append + Run | 08 §1, 32 | `CatalogEntities.cs:1000-1012` (sem RunId) | `WaveW6b2ObservabilityTests.cs:213-252` | PARTIAL |
| Eligibility `Eligible/Ineligible/Unknown` derivada | 08 §3, 33, DL-009 | `ChannelSourceSelector.cs:382-391` (filtro ad-hoc) | MISSING | MISSING |
| Sem fonte elegível: omitir + registar | DG-11, 09, 11 | `PlaylistComposerService.cs:115-144` (em memória) | `SourceSelectionStageTests.cs:451-471` | PARTIAL |
| Ordering: Position única | 10, 16 | `ChannelCatalogDbContext.cs:345` | MISSING | COMPLIANT |
| Ordering: canal uma posição/lista | 10 | `CatalogResolver.cs:2217` | MISSING | COMPLIANT |
| Não listado não é publicado | 10 §2 | `RunPublicationService.cs:218-238`; `SourceSelectionStage.cs:259-263` | `SourceSelectionStageTests.cs:197` | DIVERGENT |
| Todas as listas Enabled → 1 artifact/lista | 11 §4 | `ScheduledPlaylistGenerationAction.cs:69-84` (uma lista) | MISSING | DIVERGENT |
| Ordering não define identidade | 10, DL-011 | `CatalogEntities.cs:852-857` | `SourceSelectionStageTests.cs:409` | COMPLIANT |
| Selection determinística sem score | 09, DL-101 | `ChannelSourceSelector.cs:147-164` | `ChannelSourceSelectorTests.cs:876` | COMPLIANT |
| Só Eligible entra / distinção no-eligible | 09, DL-101 | `ChannelSourceSelector.cs:147-148`; `PlaylistComposerService.cs:140-144` | `SourceSelectionStageTests.cs:452` | COMPLIANT |
| Provider adapter/strategy | DL-111 | (inexistente) | MISSING | MISSING |
| Composição determinística por snapshot | 11, DL-017 | `PlaylistManagerService.cs:97` (`DateTime.Now`) | MISSING | DIVERGENT |
| Composição/output atómico | 11, DL-019 | `PlaylistManagerService.cs:107` (escrita directa) | MISSING | DIVERGENT |
| `publication state ∈ {Generated,Published,Superseded,Failed}` | 11 §4, 23 | (inexistente) | MISSING | MISSING |
| Path estável por OrderingList | 11 §4, 23 | `RunPublicationService.cs:232-238` (nome por Run) | `WaveW2PublicationTests.cs:160` | DIVERGENT |
| Dispatcharr não é autoridade | 12, DL-013 | `DispatcharrSyncService.cs:143-160` | `DispatcharrLiveReadonlyIntegrationTests.cs` | COMPLIANT |
| Só CrawlerManaged removido | 12, DL-014 | `DispatcharrSyncService.cs:569-600` | `DispatcharrSyncServiceOwnershipGuardTests.cs:59` | COMPLIANT |
| Desired a partir do Run/Snapshot | 12 §2, DL-116 | `DispatcharrSyncService.cs:153-160` (sem snapshot) | MISSING | PARTIAL |
| Dry-run/test read-only | 12, 22 §5, DL-015 | `DispatcharrSyncService.cs:196-206`; `DispatcharrConnectionTester.cs:92` | `DispatcharrConnectionTesterTests.cs` | COMPLIANT |
| RunId único + 1 Run activa | 13, DL-109/114 | `RunCoordinator.cs:118,208-216` | `Phase94RunCoordinatorTests.cs:128,148` | COMPLIANT |
| Serialização incl. Dispatcharr | 13 §6 | `ScheduledDispatcharrSyncAction.cs:117-181` (bypassa coordinator) | MISSING | DIVERGENT |
| Lock/lease (DL-109) | DL-109 | `RunCoordinator.cs:66,105` (CAS em memória) | MISSING | MISSING |
| Restart Created/Running→Failed | 13, 33 | `RunCoordinator.cs:357-385` | `Phase94RunCoordinatorTests.cs:303` | COMPLIANT |
| Cancelamento (Cancelled + reconciliação) | 13 §7 | `RunCoordinator.cs:149-158` (marca Failed) | MISSING | MISSING |
| Scheduler timezone + UTC | 13, 14, 39 | `ScheduledJobRunner.cs:203-217` (só UTC) | MISSING | PARTIAL |
| RunSnapshot congelado | 13 §2, DL-017 | `RunPublicationService.cs:212-220` (resolve no fim) | MISSING | DIVERGENT |
| SQLite autoridade de estado funcional | 14, 39, DL-022 | `DispatcharrConfigurationService.cs:36`; `AppSettingsStore.cs:83` (ficheiros) | — | PARTIAL/DIVERGENT |
| CLI/ENV sem override silencioso | 14, 39 | `Program.cs:373-380` (persiste no arranque) | MISSING | DIVERGENT |
| 9 tipos de policy com schema | 38 §5-8 | `CatalogEntities.cs:1022,1048` (só 2 persistidos) | MISSING | MISSING/PARTIAL |
| Merge/precedência DL-103 | 38 §8, DL-103 | `SourceSelectionPolicySet.cs:20-32` (sem group; sem Enabled) | MISSING | PARTIAL |
| Secret lifecycle/store | 14, 17, DL-112 | `WtelegramConfigStore.cs` (plaintext, sem store) | MISSING | PARTIAL/BIBLE_GAP |
| Country API contrato | 22 §6 | `WebDashboardService.cs:593-666` (schema divergente, sem versão/audit) | `CountryChannelListServiceReadsTests.cs:31` | DIVERGENT |
| Review API rotas/estado | 22 §7 | `WebDashboardService.cs:1271-1482` (rotas diferentes; sem reopen) | `ReviewApprovalEndpointTests.cs` | DIVERGENT |
| CSRF em mutações | 22 §3, 17 | `WebDashboardService.cs:530-540` | `DashboardPasswordChangeEndpointTests.cs:168` | COMPLIANT |
| ACL Administrator/Operator | 35, 22 | `Auth/AdminEntities.cs:3-11` (só admin implícito) | MISSING | MISSING |
| Secrets não devolvidos | 22 §2, 17, DL-020 | `DispatcharrConfigurationService.cs:43-58`; `WebDashboardService.cs:666` (ex.Message) | parcial | PARTIAL |

### Primeira divergência

**Discovery — identidade e dedup.** `AccountIdentity` deriva a identidade de uma URL com credenciais + username com nome `AccountId` (`Validation/AccountIdentity.cs:11,29-31`), contrariando `03-DISCOVERY.md:28,30` e `32-DOMAIN-SCHEMA.md:33`; não existe `DiscoveryCandidate` persistido nem dedup por conta (`M3uCandidateDetector.cs:130-244`). É a primeira divergência do pipeline: contamina aquisição, dedup e rastreabilidade a jusante. A primeira divergência de segurança ocorre logo a seguir na aquisição (SSRF, `HttpClientFactory.cs:67-82`).

### ADR audit (re-auditoria)

| ADR | Estado | Conteúdo | Valor documental | Incompatível? | Acção |
|---|---|---|---|---|---|
| 0002 fingerprint | Proposed | canonicalização/fingerprint de stream | histórico | parcial — a BÍBLIA já fixa SHA-256/canónico em `04` §4 | reconciliar com `04`; não é autoridade |
| 0004 secret storage | Proposed | storage/lifecycle de secrets | relevante | a BÍBLIA exige store por referência (DL-112); mecanismo continua indefinido | manter como candidato; `BIBLE_GAP` de mecanismo |
| 0005 baseline/runtime | Proposed | catálogo baseline vs runtime | relevante | não | manter |
| 0006 migration/rollback | Proposed | estratégia SQLite/rollback | parcial | Round 2 decidiu downgrade=imagem+restore (`20`) | reconciliar; restante histórico |

Nenhum ADR `Proposed` foi tratado como autoridade.

### DG-21a..f — estado real

| Item | Estado | Evidência |
|---|---|---|
| DG-21a histerese | CLOSED (registo) / implementação MISSING | `08-VALIDATION.md` §5; sem código/teste de histerese |
| DG-21b scheduler locking | CLOSED (registo) / mecanismo MISSING | `13-RUNS.md` §8/§6; `RunCoordinator.cs:66,105` só CAS em memória |
| DG-21c decisões em ADRs Proposed | PARTIAL/BLOCKED | ADR-0004 (secret store) continua sem decisão normativa |
| DG-21d Country no inventário | CLOSED | `41-API-INVENTORY.md` §Country |
| DG-21e matriz | CLOSED | esta secção preenchida com evidência real |
| DG-21f re-auditoria ADRs | CLOSED | tabela ADR audit acima |

## W2 — Aquisição segura: SSRF, redirects, retry e persistência (estado real)

Implementação e testes concluídos. Mecanismos:

| Mecanismo | Implementação | Testes | Estado |
|---|---|---|---|
| Classificação IPv4/IPv6/mapped | `Validation/AddressClassifier.cs` | `WaveW2SsrfGuardTests.cs` | COMPLIANT |
| Guard de URL/literal/DNS fail-closed | `Validation/SsrfGuard.cs` (`IDnsResolver`, `SsrfBlockedException`) | `WaveW2SsrfGuardTests.cs` | COMPLIANT |
| Pinning resolve→validate→connect (anti-rebinding) | `Validation/SafeConnector.cs` (`ConnectCallback`) | `WaveW2SsrfGuardTests.cs` | COMPLIANT |
| Handler sem auto-redirect e sem proxy | `Validation/HttpClientFactory.cs:83-86` | `WaveW2SsrfGuardTests.cs` | COMPLIANT |
| Redirect manual revalidado por hop | `Validation/GuardedHttpRequest.cs:34-102` | `WaveW2SsrfGuardTests.cs` | COMPLIANT |
| Retry técnico na mesma Run | `Validation/AcquisitionFailure.cs`; `M3uTesterService.cs` | `WaveW2AcquisitionRetryTests.cs` | COMPLIANT |
| Source failure persistida (sanitizada) | `CatalogEntities.cs:917-922`; `CatalogResolver.MarkSourceAcquisitionFailureAsync`; migração `20260919130000_AddSourceAcquisitionFailure` | `WaveW2AcquisitionPersistenceTests.cs` | COMPLIANT (cobertura de `Source` exercitada em teste; ver W2-FU-1/W2-FU-2 abaixo para o wiring em produção) |
| Run aggregation | `RunReport.cs:228-233`; `LiveRunCounts` | `WaveW2AcquisitionPersistenceTests.cs`; `W2FU1ObserverWiringTests.cs` | COMPLIANT (W2-FU-1: agora exercitado em produção no caminho Telegram live com `sourceId=null`) |

PARAMETER_GAP (técnicos, não normativos): `StreamValidationOptions.MaxRedirects`, `MaxResponseBytes`; reutilizados `MaxRetries`, `RetryDelayMilliseconds`, `ConnectionTimeoutSeconds`, `OverallTimeoutSeconds`. CIDRs/metadata adicionais continuam PARAMETER_GAP.

Fora de scope: transporte Dispatcharr (BaseUrl operador-configured). W2-FU-2 fechada em 2026-09-23 por W2-FU-2-CLOSE (auditoria semântica dos 7 MUST_NOT_WIRE; ver §7 abaixo).

**W2-FU-1 (FECHADO 2026-09-22):** o `CatalogAcquisitionFailureObserver` é agora instalado em `TelegramScraperService.SearchAndTestM3UInTelegramAsync` quando o caller injecta um `CatalogResolver` via `SetCatalogResolver(...)` e está no caminho COM ingestion (`pipelineIngestor != null`). O observer é construído com `sourceId=null` e portanto apenas agrega em `RunReport.AcquisitionFailures`/`Retryable`/`Terminal` — não persiste em `Source`. Caminho legacy sem catalog preserva o comportamento actual (sem observer, sem side-effects).

**W2-FU-2A (FECHADO 2026-09-23):** Option B — read-only `SourceId` resolution por `sourceKey` determinístico. `CatalogResolver.GetSourceIdByKeyAsync(string sourceKey, CancellationToken)` adicionado como método read-only (consulta BD; sem cache de processo, sem efeitos colaterais; BD permanece a fonte de verdade de `Source.Id`). `TelegramScraperService.SearchAndTestM3UInTelegramAsync` resolve `sourceId` por `sourceKey = "telegram-<slug>"` antes da aquisição e passa-o ao observer — **sem `Source` pré-existente ⇒ `sourceId = null` (RunReport only)**. Semântica: `Source` existente ⇒ `MarkSourceAcquisitionFailureAsync` persiste `LastAcquisitionFailure*`; `Source` inexistente ⇒ acquisition failure fica apenas em `RunReport`. **Acquisition failure NUNCA cria `Source`.** `Source` continua a ser criada exclusivamente por `EnsureSourceAsync` (durante `IngestAsync`). Identidade: `Source.Key = telegram-<slug>`; múltiplos peers/ocorrências da mesma keyword convergem para a mesma `Source`; sem identidade de peer, sem `TelegramPeerEntity`, sem bridge `peer → Source`. Invariante de restart: zero estado de processo entre Runs. Alternativas rejeitadas: `_persistedSourceId` em `TelegramScraperService` (rejeitado — estado de processo, não restart-safe); `IngestionResult.SourceId` (rejeitado — resolução é pré-acquisition no caller). Ficheiros: `m3uCrawler/Services/Catalog/CatalogResolver.cs`, `m3uCrawler/Services/TelegramScraperService.cs`, `m3uCrawler.Tests/W2FU2ASourceIdResolutionTests.cs` (+6 facts). Suite focada 56/0/0; suite completa 2657/1/0 (delta +6 vs baseline W2-FU-1 2651); build Release 0 errors / 0 warnings. **Escopo:** W2-FU-2A fecha **apenas** o caminho de run-keyword (Telegram discovery).

**W2-FU-2 (CLOSED 2026-09-23 por W2-FU-2-CLOSE):** fecho documental por auditoria semântica dos 7 call sites MUST_NOT_WIRE. Conclusão: nenhum wiring com `sourceId` é considerado semanticamente válido sob as decisões A/A/A ratificadas em 2026-09-22 (Q9.1 sem peer identity, Q9.2 sem backfill, Q9.3 sem `SourceId` improvisation). Não foi derivado nem improvisado `SourceId` a partir de `peerId`, `chatTitle`, `CandidatePlaylist.Source`, `Source.Origin`/`Key`, heurísticas, queries ou `XtreamAccountInfo`. Nenhuma `Source` é criada por HTTP URL. O caminho run-keyword (Telegram discovery) está fechado por W2-FU-2A (acima). Classificações: `TelegramBotService /test` → `WIRE_RUNREPORT_ONLY` (deferred — wiring exigiria nova dependência que viola "no peer→Source bridge"); `Program.cs:898/905` (`--scan-domain`), `Program.cs:1405/1412` (legacy tester), `ScheduledValidationAction`, `ScheduledM3uDiscoveryAction`, `ScheduledAutomationHost`, `/api/validation/test` → `DO_NOT_WIRE` (ausência de Source canónica / fronteira deliberadamente fora do catálogo). Ver `docs/Reestructure/07-SOURCES.md` §7 "Source identity boundary" para a matriz completa e o racional por call site. **0 source code changes**, **0 testes novos**, **0 migrations**, **0 alterações de schema**. A/A/A preservadas. W2-FU-1 e W2-FU-2A intactos.

## W3 — Contrato de parsing M3U (estado real)

Implementação e testes concluídos (DG-03a/DG-03b fechados em Round 1/Q4; a BÍBLIA não foi alterada). Resultado: um único contrato de parsing, consumido por todas as camadas.

| Mecanismo | Implementação | Testes | Estado |
|---|---|---|---|
| Cabeçalho `#EXTM3U` obrigatório (primeira linha não vazia) | `M3uParserService.ParseDetailed`; `M3uParseDiagnosticKind.MissingHeader` | `WaveW3M3uParsingContractTests.cs` | COMPLIANT |
| Entrada válida = `#EXTINF` + URL absoluto http/https | `M3uParserService.ParseDetailed`; `Models/M3uParseResult.cs` | `WaveW3M3uParsingContractTests.cs` | COMPLIANT |
| Estado tri-state `Success/Partial/Failed` | `Models/M3uPlaylistStatus.cs` | `WaveW3M3uParsingContractTests.cs` | COMPLIANT |
| Malformado registado sem abortar | `M3uParseDiagnostic` (`Malformed`); contadores | `WaveW3M3uParsingContractTests.cs` | COMPLIANT |
| Alvo não utilizável ≠ malformado | `M3uParseDiagnosticKind.UnusableTarget` | `WaveW3M3uParsingContractTests.cs` | COMPLIANT |
| Metadados benignos + HLS `#EXT-X-STREAM-INF` | `M3uParserService.ParseDetailed` (`BenignMetadataCount`) | `WaveW3M3uParsingContractTests.cs` | COMPLIANT |
| Diagnósticos sanitizados (sem credenciais) | `CredentialSanitizer` em `M3uParserService.Sanitize` | `WaveW3M3uParsingContractTests.cs` | COMPLIANT |
| Consumidores convergentes | `CountryChannelValidator.AnalyzePlaylist`; `TelegramScraperService.ProcessCandidateAsync`; `PlaylistReader`; `PlaylistManagerService.LoadFromM3uPlaylist` | `WaveW3M3uParsingContractTests.cs` (convergência) | COMPLIANT |
| Contador aditivo `PlaylistsPartial` | `Models/RunReport.cs`; `LiveRun/LiveRunCounts.cs` | `Phase94LiveRunHardeningTests.RunReport_contract_is_frozen` (adaptado 59→60) | COMPLIANT |

Limites (`M3uParserOptions`: tamanho de documento, nº de entradas, comprimento de campo, tempo, cancelamento) são MECHANISM; os valores permanecem `PARAMETER_GAP` (`DG-04d`) e não são normativos.

Quality gate W3: `dotnet build` 0 erros/52 avisos (pré-existentes, nenhum de ficheiros W3); `dotnet test` 0 failed / 2302 passed / 1 skipped.


## W4 — Normalização e fingerprint de stream (estado real)

Implementação do fingerprint canónico versionado e da dedup intra-Source
(`04-PLAYLIST-STREAM.md §4/§4.1`, `32-DOMAIN-SCHEMA.md` Stream/ChannelSource,
DL-108). A BÍBLIA foi reconciliada nos documentos indicados. O ADR-0002
permanece `Proposed` e **não** foi tratado como autoridade.

| Mecanismo | Implementação | Testes | Estado |
|---|---|---|---|
| Canonicalização de URL `sfp1` (scheme/host/porta/fragmento/percent-encoding) | `Matching/StreamFingerprint.cs` | `WaveW4StreamFingerprintTests.cs` | COMPLIANT |
| Política de credenciais (userinfo excluído; query `username`/`password`/`token`/`authorization` removidos; path Xtream mascarado com `<ID>` preservado) | `Matching/StreamFingerprint.cs` (`MaskXtreamPath`, `RemoveCredentialParameters`) | `WaveW4StreamFingerprintTests.cs` (segurança) | COMPLIANT |
| Serialização `version + "\n" + canonical` + SHA-256 hex minúsculo | `Matching/StreamFingerprint.cs` (`Version = "sfp1"`) | `WaveW4StreamFingerprintTests.cs` (golden vectors independentes) | COMPLIANT |
| Persistência `Fingerprint`/`FingerprintVersion` em `ChannelSource` (aditiva) | `CatalogEntities.cs`; `ChannelCatalogDbContext.cs`; migração `20260919152739_AddChannelSourceStreamFingerprint` | `WaveW4ChannelSourceFingerprintTests.cs` (migração aditiva) | COMPLIANT |
| Dedup intra-Source (canal+source+fingerprint+versão); fallback legacy por URL sanitizada; sem cross-Source | `CatalogResolver.RecordChannelSourceAsync` | `WaveW4ChannelSourceFingerprintTests.cs` | COMPLIANT |
| Critério 6 de Selection alimentado pelo fingerprint persistido (fallback para URL normalizada) | `SourceSelectionStage.cs`; `PlaylistComposerService.cs` | `WaveW4ChannelSourceFingerprintTests.cs`; `SourceSelectionPreviewTests` (adaptado) | COMPLIANT |
| Conflito `16:27`/`32:207` vs `07:48` reconciliado (múltiplas streams; fingerprint distingue) | `04` §4.1/§5, `32` Stream/ChannelSource, `16` §3, `05` §4, `31` DL-108 | `WaveW4ChannelSourceFingerprintTests.cs` (múltiplas streams) | COMPLIANT |

Golden vectors computados **independentemente** (script Python isolado,
`hashlib.sha256("sfp1\n" + canonical)`) e congelados no teste; não derivados
do código sob teste. Query preservada por ordem nesta versão.

Limitações: sem backfill em massa de rows legacy (permanecem `null`, com
fallback na selecção); ADR-0002 não foi promovido nem usado como autoridade.

Quality gate W4: `dotnet build` 0 erros / 52 avisos (iguais ao baseline);
`dotnet test` 0 failed / 2365 passed / 1 skipped (baseline 2302/1/0; +63
testes W4).

## W4.1 — Resolução fingerprint-aware no SourceSelectionStage (correcção)

Corrige a divergência entre a identidade de consolidação (fingerprint, na persistência) e a identidade de junção (`CredentialSanitizer.SanitizeUrl`, no stage). Um URL fingerprint-equivalente com representação sanitizada diferente era classificado como `Unmatched` artificial.

Contrato de resolução (precedência):

1. **fingerprint** — `StreamFingerprint.TryComputeFingerprint(runtime.Url)` contra `(FingerprintVersion, Fingerprint)` persistidos;
2. **legacy sanitized-URL** — `CredentialSanitizer.SanitizeUrl(runtime.Url)` contra `ChannelSource.StreamUrl` (rows sem fingerprint);
3. **unmatched** — pass-through, sem associação.

Regras mantidas: correspondência com `>1 CanonicalChannelId` distinto ⇒ `Ambiguous` + `Unmatched`; empate ⇒ menor `Id`; `SourceId` permanece parte da identidade persistente; nenhuma credencial em diagnostics; `CredentialSanitizer` continua apenas para exposição segura.

| Requirement | Bible/contrato | Implementação | Teste | Estado |
|---|---|---|---|---|
| Precedência fingerprint > URL sanitizada na junção do stage | DL-101 crit. 6, 09 §3, 04 §4.1/§5, 16 §3 | `SourceSelectionStage.ResolveHits` (fingerprint-first, fallback legacy) | `WaveW4p1SourceSelectionFingerprintResolutionTests.cs` | COMPLIANT |
| URLs fingerprint-equivalentes com sanitized diferente resolvem a mesma `ChannelSource` | 04 §5, 32 ChannelSource | `SourceSelectionStage.cs` (`byFingerprint`) | idem (Test 1, scheme case, default port, fragment, query creds) | COMPLIANT |
| Fallback legacy sem fingerprint | 16 §3 | `SourceSelectionStage.cs` (índice por URL sanitizada) | idem (Test 6) | COMPLIANT |
| Isolamento de Source / não escolha arbitrária em colisão entre canais | 07 §5 | regra de ambiguidade existente + índice por fingerprint | idem (Test 8) | COMPLIANT |
| Determinismo da resolução | DL-101 | índices por execução | idem (Test 10) | COMPLIANT |

Quality gate W4.1: `dotnet build` 0 erros (52 avisos, iguais ao baseline); `dotnet test` 0 failed / 2377 passed / 1 skipped (baseline W4 2365/1/0; +12 testes W4.1).

## W5.0 — Decisões normativas de Recognition / Fuzzy / Review

Registo documental (sem implementação de código). Baseline `8142c0d`. As decisões D1–D11 foram inscritas na BÍBLIA.

| Decisão | Contrato | Documentos | Wave |
|---|---|---|---|
| D1 RecognitionPolicy (scopes, precedência, snapshot, schema) | `38 §5.1`; `32 RecognitionPolicy` | 38, 32 | W5.1 |
| D2 Fuzzy opt-in; sem candidato→UNKNOWN; plausíveis→AMBIGUOUS; único acima do threshold pode→CANONICAL | `05 §4`; DL-117 | 05, 31 | W5.3 |
| D3 Threshold/margem/pesos = PARAMETER_GAP | `38 §5.1`; `32` | 38, 32 | W5.3 |
| D4 Review lifecycle `Open→InReview→Resolved`, `Open→Ignored`, `Open→InReview→Ignored`, reopen `Resolved/Ignored→Open` | `33 ReviewItem`; DL-105 | 33, 31 | W5.4 |
| D5 API Review normativa = `22 §7`; `Ignore` exige motivo; auditoria before/after | `22 §7` | 22 | W5.5 |
| D6 MatchMethod/MatchConfidence normativos (`0..1`, versionados, registo não autoridade) | `05 §4.1`; `32 ChannelSource` | 05, 32 | W5.6 |
| D7 Nome normalizado = passo próprio (3) distinto de alias (4) | `05 §4/§4.1`; `34 P6` | 05, 34 | W5.2 |
| D8 IdentityRule explícita (`Review`/`Excluded`), não excepção silenciosa | `05 §4.1`; `34 P6` | 05, 34 | W5.2 |
| D9 Namespace de provider na comparação de identidade externa (sem novo scope) | `05 §4.1`; `32` | 05, 32 | W5.2 |
| D10 P6 = `Canonical/Unknown/Ambiguous/Excluded`; `Rejected` não é P6; `Excluded ≠ Unknown/Ambiguous` | `34 P6`; `05 §4.1` | 34, 05 | W5.2 |
| D11 `Ambiguous` qualificado por `Stage` (Recognition vs Selection) | `05 §4.1`; `34 P6` | 05, 34 | W5.2 |

As linhas `46:118-125` e `46:159` mantêm-se como **estado de implementação** (`DIVERGENT`/`PARTIAL`/`MISSING`) até W5.1–W5.6; as decisões normativas estão agora fechadas. `MatchConfidence`/`MatchMethod` passam a ter linha de rastreabilidade própria (D6).

Quality gate W5.0: documental, sem build/test (nenhuma alteração de código).

## W5.1 — RecognitionPolicy implementada

| Requirement | Contrato | Implementação | Teste | Estado |
|---|---|---|---|---|
| Scopes `system/global/group/channel` + precedência determinística | `38 §5.1`, DL-103 | `Services/Recognition/RecognitionPolicyScopes.cs`; `RecognitionPolicySet.cs` | `WaveW51RecognitionPolicyTests` (1–6) | COMPLIANT |
| Schema (`Enabled`, `Fuzzy.Enabled/Threshold/AmbiguityMargin/Weights`) com fuzzy opt-in por defeito | `05 §4`, `32`, DL-117 | `RecognitionPolicy.cs`; `RecognitionPolicyEntity`; `ChannelCatalogDbContext` | idem (1,7,8) | COMPLIANT |
| Snapshot imutável por Run | `38 §5.1`, DL-017 | `RecognitionPolicyResolver.CreateSnapshotAsync`; `RecognitionPolicySnapshotEntity` | idem (9, snapshot isolation) | COMPLIANT |
| Persistência + versionamento + auditoria | DL-110, `17 §5` | `CatalogResolver.UpsertRecognitionPolicyAsync`/`DeleteRecognitionPolicyAsync` (+ `AuditRecordEntity`) | idem (10,11) | COMPLIANT |
| Migration aditiva e reversível | `16` | `20260920093237_AddRecognitionPolicy` | idem (12,13) | COMPLIANT |

`PARAMETER_GAP` inalterados: `Fuzzy.Threshold`, `Fuzzy.AmbiguityMargin`, `Fuzzy.Weights`, defaults de campo (excepto `Fuzzy.Enabled=false`). Nenhum comportamento de reconhecimento/fuzzy/Review foi alterado nesta wave.

Quality gate W5.1: `dotnet build` 0 erros (52 avisos, iguais ao baseline); `dotnet test` 0 failed / 2394 passed / 1 skipped (baseline 2377/1/0; +17 testes W5.1).

## W5.5 — API HTTP de Review: IMPLEMENTED

Implementação do contrato ratificado (DL-120; `22 §7`) no handler manual de `WebDashboardService.cs`. Identidade = `ReviewItem.Id`; erros `{error,message,correlationId}`; audit before/after; auth/CSRF pelo gate existente.

| Requirement | Contrato | Implementação | Teste | Estado |
|---|---|---|---|---|
| `GET /api/reviews` (filtro `state`, `limit`/`offset`, ordem `CreatedAtUtc DESC, Id DESC`) | `22 §7.1`; DL-120 | `HandleReviewListAsync`; `subject = NormalizedIdentity`; `runId = null` (limitação C7/W5.6) | `WaveW55ReviewApiTests` (list) | COMPLIANT |
| `GET /api/review?id` | `22 §7.2`; DL-120 | `HandleReviewDetailAsync`; `400 review-id-required`; `404 review-not-found` | idem (detail) | COMPLIANT |
| `POST /api/review/ignore` (motivo obrigatório) | `22 §7.4`; DL-120 | `HandleReviewIgnoreAsync` → `IgnoreReviewAsync`; `400 reason-required`; `409 state-conflict` | idem (ignore) | COMPLIANT |
| `POST /api/review/reopen` (justificação) | `22 §7.5`; DL-120 | `HandleReviewReopenAsync` → `ReopenReviewAsync`; manual/auditado | idem (reopen) | COMPLIANT |
| `POST /api/review/resolve` (declaração + `Resolved`) | `22 §7.3`; DL-120 | `HandleReviewResolveAsync`; `channelAlias`→`AddAlias`, `canonicalChannel`→`CreateChannel` via `ApplyReviewApprovalAsync`; `Open→InReview→Resolved`/`InReview→Resolved`; `422` para declarações inválidas/independentes | idem (resolve) | PARTIAL (ver gap) |
| Formato de erro `{error,message,correlationId}` | `22 §2/§7`; DL-120 | `WriteReviewApiErrorAsync`; sem `Exception.Message`; legacy não normalizado | idem (error contract) | COMPLIANT |
| Identidade `ReviewItem.Id` | DL-120 | `GetReviewItemAsync`; rotas novas usam `id`/`reviewItemId` | idem | COMPLIANT |
| AuthZ `Administrator` (modelo actual, sem RBAC) | `22 §7`; `35`; DL-120 | gate existente (sessão/token + CSRF); sem role column | idem | COMPLIANT (implicit admin) |
| Lifecycle via W5.4 | DL-119; DL-120 | `ReviewLifecycle`/`Ignore`/`Reopen`; resolve usa `ApplyReviewApprovalAsync` (composição de arestas válidas) | idem | COMPLIANT |
| `subject`/`runId` | `22 §7.1`; DL-120 | `subject = NormalizedIdentity`; `runId = null` | idem | `runId` = W5.6/C7 |
| Rotas legacy mantidas | DL-120 | `/api/catalog/reviews/...` inalteradas | `ReviewApprovalEndpointTests` | COMPLIANT (OUT) |

**`W5.5 IMPLEMENTATION GAP` (resolve):** `change.type` `externalIdentity` e `channelSource` não têm operação de domínio de Review *declarada* demonstrada (só existem `RecordExternalIdentityAsync`/`RecordChannelSourceAsync`, gravadores de ingestão com parâmetros sem contrato de declaração), e `none` não tem semântica definida em `22 §7.3`. São rejeitados com `422 declared-change-invalid`; não se inventou domínio. Suportados: `channelAlias`, `canonicalChannel`.

Quality gate W5.5: `dotnet build` 0 erros; `WaveW55ReviewApiTests` 42/42; suite completa 2532 passed / 1 skipped / 0 failed (baseline W5.4 2490/1/0; +42). `git diff --check` clean. Estado Git preservado (sem commit/push).

Revisão pós-implementação (B1/B2): o gate emite o envelope `{error,message,correlationId}` também nos 401/403 das Review APIs (legacy inalterado) e excepções inesperadas são mapeadas para `500 persistence-error` no dispatcher; testes `Unexpected_exception_returns_500_persistence_error`, `Unauthenticated_review_api_uses_error_envelope`, `Csrf_error_on_review_api_uses_error_envelope`, `Legacy_gate_error_format_is_unchanged`.

## W5.6 — MatchMethod/MatchConfidence: IMPLEMENTED

Implementação da especificação normativa `49-W56-MATCH-CONFIDENCE-SPECIFICATION.md` (DL-121/DL-122). A tabela método→confidence vive em `Services/Recognition/RecognitionMatchMethods.cs`; `CatalogResolution` transporta `MatchConfidence` (`double?`, init-only aditivo); a `Recognition` (passos de `CatalogResolver.ResolveAsync`, `FromCanonical`/`FromRule`) atribui o valor por método; o pipeline (`PipelineIngestionService`) apenas persiste o par recebido, sem recalcular (o `const 1.0` foi removido); `ChannelSourceEntity` tem `MatchConfidence` nullable e `MatchSemanticsVersion` (`"msm1"`), com migration `AddMatchSemanticsVersionAndNullableMatchConfidence`; o endpoint manual legacy valida `MatchMethod`/`MatchConfidence` na camada HTTP (OD-E). C7 e M.4 permanecem `OUT`.

| Decisão | Contrato | Contratos/rastreio | Estado |
|---|---|---|---|
| D-W56-01 | Semântica versionada; versão das regras/algoritmo, não propriedade arbitrária por `ChannelSource`; mecanismo físico = `MatchSemanticsVersion="msm1"` persistida | DL-108/`sfp1`; DL-110; DL-122; `49 §10` | RATIFIED (mecanismo fechado) |
| D-W56-02 | `MatchConfidence` `double 0..1` **method-specific**; sem escala global; regra própria por método (8 valores); valores concretos RATIFIED | `05 §4.1`; `32:218`; DL-121; DL-122; `49 §6/§7` | RATIFIED (valores fechados) |
| D-W56-03 | `FuzzyScore` ≠ `MatchConfidence`; conversão proibida | DL-119; `48`; `43 #24` | RATIFIED (fechada) |
| D-W56-04 | `Unknown`/`Ambiguous` → `MatchConfidence = null` (nunca `0`/melhor `FuzzyScore`) | DL-121 | RATIFIED |
| D-W56-05 | Producer = Recognition (`CatalogResolution` transporta `MatchMethod`+`MatchConfidence`); pipeline persiste; `const 1.0` = divergência a corrigir | DL-121 | RATIFIED |
| D-W56-06 | C7 sem contrato não é inventado; M.4 `OUT` (`W5.6 ≠ M.4`) | `32:163`; manifest `C7`/`M.4` | RATIFIED |
| D-W56-07 | Endpoint manual valida `MatchMethod` (8 valores), `MatchConfidence` (`0..1`) e versão; compatibilidade preservada | DL-121; DL-122; `49 §12` | RATIFIED (política OD-E fechada) |

### Matriz de rastreabilidade da especificação W5.6 (DL-122; `49`)

| Requirement | Fonte normativa | Conceito | Implementação | Teste | Estado |
|---|---|---|---|---|---|
| W56-METHODS | `49 §5/§7`; DL-122 | Conjunto fechado de 8 `MatchMethod` | `RecognitionMatchMethods` (8 constantes + `IsKnownMethod`) | `WaveW52...`/`WaveW53...`; `WaveW56MatchConfidenceTests` | COMPLIANT |
| W56-TABLE | `49 §7`; DL-122; `05 §4.1` | Tabela normativa método→confidence (`1.0`/`0.80`/`0.60`/`1.0`) | `RecognitionMatchMethods.ConfidenceByMethod` + `TryGetMatchConfidence`; `CatalogResolution.FromCanonical`/`FromRule` | `WaveW56MatchConfidenceTests` (tabela + 8 métodos) | COMPLIANT |
| W56-VERSION | `49 §10`; DL-122/DL-123; DL-108 | `MatchSemanticsVersion="msm1"` só em pares normativos (método ∈ 8 + confidence igual à tabela); legacy → `null` | `RecognitionMatchMethods.MatchSemanticsVersion`; `CatalogResolver.RecordChannelSourceAsync` (F8-B); `ChannelSourceEntity.MatchSemanticsVersion`; migration `20260920200933_AddMatchSemanticsVersionAndNullableMatchConfidence` | `WaveW56MatchConfidenceTests` (persistência/versão normativa e legacy) | COMPLIANT (F8-B) |
| W56-PRODUCER | `49 §11`; DL-121/DL-122 | Recognition produz; `CatalogResolution` transporta `MatchMethod`+`MatchConfidence`; pipeline persiste sem recalcular | `CatalogResolution.MatchConfidence` (init-only); `PipelineIngestionService` transporta `resolution.MatchConfidence` (sem `const 1.0`) | `WaveW56MatchConfidenceTests` (pipeline exact/heuristic) | COMPLIANT |
| W56-NULL | `49 §9`; DL-121 | `Unknown`/`Ambiguous` → `null` | `CatalogResolution.FromCanonical` (método desconhecido→null); `Unknown()`/`Ambiguous()` sem confiança | `WaveW56MatchConfidenceTests` | COMPLIANT |
| W56-FUZZY-SEP | `49 §8`; `48 §5`; DL-119 | `FuzzyScore != MatchConfidence`; proibido `score/100`; `Fuzzy`=`0.60` independente | `RecognitionMatchMethods.TryGetMatchConfidence` (constante `0.60`) | `WaveW56MatchConfidenceTests` (0.60 vs `FuzzyScore=95`) | COMPLIANT |
| W56-ENDPOINT | `49 §12`; DL-122/DL-123; `22 §2` | OD-E + F7-B: omissão preserva; se `MatchMethod` fornecido, só os 8 valores **e** `MatchConfidence` obrigatória (ausente→400), só `0..1`, combinação validada; versão não exigida; inválidos→erro sem persistência parcial; legacy inalterado | `WebDashboardService` handler `POST /api/catalog/sources/{id}/streams` (validação HTTP, antes de `RecordChannelSourceAsync`) | `WaveW56MatchConfidenceTests` (normativo/divergente/método sem confidence/legacy) | COMPLIANT (F7-B) |
| W56-C7 | `32:163`; `49 §14`; DL-121 | C7 sem contrato **não** é inventado | inalterado | — | OPEN (OUT de W5.6) |
| W56-M4 | `49 §14`; DL-119 | M.4 (wiring snapshot→Run/pipeline) | inalterado | — | OUT (`W5.6 ≠ M.4`) |

Testes criados: `m3uCrawler.Tests/WaveW56MatchConfidenceTests.cs` — tabela dos 8 métodos; `Unknown`/`Ambiguous → null`; separação `FuzzyScore`/`MatchConfidence` (sem `/100`); transporte aditivo em `CatalogResolution`; persistência nullable + `msm1`; pipeline transporta sem recalcular; validação OD-E do endpoint manual (válido, método inválido, confidence fora de `0..1`, combinação inválida, método ausente, confidence ausente, legacy). Dependência de M.4 marcada como tal (não requisito W5.6).

Quality gate W5.6: `dotnet build` 0 erros / 0 warnings novos; `WaveW56MatchConfidenceTests` 38/38; suite completa 2570 passed / 1 skipped / 0 failed (baseline W5.5 2532/1/0; +38); `dotnet ef migrations has-pending-model-changes` = sem alterações pendentes; `git diff --check` clean. Q4 (`44`) passa a satisfeito para a semântica de matching de W5.6.

### W5.6 follow-up (F7-B/F8-B/F9): IMPLEMENTED

Revisão pós-implementação registou F7/F8/F9 (não bloqueantes). Ratificado em DL-123 e implementado sem migration/schema:

| Item | Decisão ratificada | Estado |
|---|---|---|
| F7-B | `MatchMethod` presente ⇒ `MatchConfidence` obrigatória; ausente → `400`, sem persistência; proibido auto-preencher/normalizar no endpoint | IMPLEMENTED (`WebDashboardService`; testes `Manual_endpoint_method_without_confidence_is_rejected_without_persistence`) |
| F8-B | `msm1` só para pares W5.6 (método ∈ 8 + confidence validada + tabela); legacy (`"unknown"`+`0`) → `MatchSemanticsVersion = null`; proibido `"legacy"` | IMPLEMENTED (`CatalogResolver.RecordChannelSourceAsync`; testes `RecordChannelSource_stamps_msm1_only_for_normative_pairs`, legacy/manual com versão `null`) |
| F9 | Alinhamento documental: rota real `POST /api/catalog/sources/{id}/streams`; `MatchConfidence` `double?` | APLICADO (docs) |

Quality gate do follow-up: `dotnet build` 0 erros / 0 warnings novos (52 avisos = baseline); `WaveW56MatchConfidenceTests` 45/45; suite completa 2577 passed / 1 skipped / 0 failed (baseline W5.6 2570/1/0; +7); `dotnet ef migrations has-pending-model-changes` = sem alterações pendentes; `git diff --check` clean. Sem migration/schema; C7 `OPEN`; M.4 `OUT`.

## Cobertura de requisitos

Todo o requisito/contrato da BÍBLIA deve ter um ID único e constar da matriz de rastreabilidade. A matriz DEVE incluir explicitamente os requisitos de delegação/completude (`00-BIBLE.md` §5/§6) e de contratos (`22`, `23`). Um requisito sem implementação é uma wave futura; um requisito com implementação contraditória é uma divergência a analisar antes de avançar downstream.
