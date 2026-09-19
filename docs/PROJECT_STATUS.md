# m3uCrawler — Project Status

> Ponto de entrada para o estado corrente da implementação (`docs/Reestructure/00-BIBLE.md:30-41`, `:105-128`).
> Documento derivado: descreve o que foi construído e validado; não redefine conceitos da BÍBLIA.
> Registos históricos das waves: `docs/project/waves/`.

## Current State
- Branch: `feature/phase-9c-first-run-dashboard`
- HEAD: `a2c2eae68992c09db2b037a36683a21eb9ae8957`
- Phase: 9C / 13 — implementação das waves da BÍBLIA (reconstrução)
- Wave: W5 concluída; W6 definida e pendente
- Status: PARCIALMENTE CONFORME — waves A/B, Onboarding, Admin Password e W1/W2/W3s/W4a/W4b/W5 concluídas; W6+ pendentes
- Last validated commit: `a2c2eae` (2026-09-19)
- Last validation: build 0 errors / 52 warnings; suite 2137 passed / 0 failed / 1 skipped (total 2138)

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

## In Progress
| Wave | Objective | Status | Branch | Commit |
|---|---|---|---|---|
| W6 | Audit records de mutações administrativas + ligar/esconder funcionalidades inertes (import-policies, canonical-groups/group-mappings, sync-runs, pending-country-approvals, degradation, aprovação de Review) | Definida / pendente (bloqueada por ADRs e contratos de API) | feature/phase-9c-first-run-dashboard | n/d |

## Pending
| Item | Origin | Dependency | Status |
|---|---|---|---|
| W6 — audit records + wires | Plano de waves (auditoria transversal) | ADRs (schemas/contratos), `17-SECURITY.md:45-53`, `22-API-CONTRACTS.md:52-54` | Pendente |
| Wire de funcionalidades inertes: import-policies, canonical-groups/group-mappings, sync-runs, pending-country-approvals, degradation, review approval | F-09(dash)/F-10/F-11/F-12/F-13/F-14 | Decisões de produto/ADR | Pendente |
| E2E de dois ciclos (idempotência: sem duplicar channels/streams/sources/ownership; sem apagar externos) | Critério de sucesso 7 | W6 | Pendente |
| Descoberta: dedup por `AccountId`; `max-streams` por candidato | Perf (auditoria transversal) | Decisão de produto | Pendente |
| Raiz única de `runtime-data` | C-08 / F-21 | Decisão de deployment/compose | Pendente |
| Secret store isolado conforme ADR-0004 | ADR-0004 | Aprovação do ADR | Pendente |
| Seeding de `tvg-id` no baseline (`ExternalIdentity`) | W4b / ADR-0002 | Fonte de mapeamento tvg-id→canal + ADR-0002 | Pendente |
| Estabilizar testes flaky de timing (`XtreamAccountLockManagerTests`, `Phase93AccountGateCoordinatorTests`) | Dívida de testes | — | Pendente |
| Re-auditar os restantes ADRs `Proposed` (ADR-0002/0004/0005/0006) contra a Regra de Completude reforçada (`00-BIBLE.md` §5/§6). ADR-0001 re-auditado: **completo — pendente de aprovação**. | Governação BÍBLIA §5/§6 | Aprovação do proprietário | Em curso |
| Definir IDs de requisitos e preencher a matriz de rastreabilidade (`46-REQUIREMENT-TRACEABILITY.md`). IDs candidatos `CD-01`..`CD-16` introduzidos em `ADR-0001` §10. | `46-REQUIREMENT-TRACEABILITY.md` | — | Pendente (IDs semeados) |

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
| F-13 | Review que altera catálogo deve declarar a mudança (`05-CATALOGUE.md:63-67`) | Aprovação de Review inerte (nada lê `Approved`) | Review sem efeito | Pendente (W6) |
| F-14 | Observation/histórico (DL-023) | Degradação só tem produtor manual; pipeline não grava observações | Histórico incompleto | Pendente (W6) |
| F-15 | Contratos de API (`22-API-CONTRACTS.md:1-46`) | `/api/dispatcharr/config` só edita subconjunto de chaves | Configuração incompleta | Pendente (W6) |
| F-16 | Readiness de Dispatcharr (`15-APPLICATION.md:48-52`) | Readiness não testa ligação; último teste não persistido nem gate | Readiness fraca | Pendente (W6) |
| F-22 | Dashboard operacional (`15-APPLICATION.md`) | `--web-allow-trigger` ausente do compose → "Run now" 503 | Feature inacessível | Pendente |
| F-27 | Handlers com método explícito (`22-API-CONTRACTS.md:5-14`) | Guardas de método/405 apenas parciais | Menor | Parcial (4dff2bf) |
| D-F2 | Unicidade de policy (`16-PERSISTENCE.md:19-30`) | Prioridade por `Id` vs selection por `Key`; unicidade de `Scope` inconsistente | Risco de duplicados | Pendente |
| D-F3 | `ChannelSource` com chave estável (`32-DOMAIN-SCHEMA.md:150-163`) | `StreamUrl` sanitizada usada como identidade | Identidade frágil | Pendente |
| D-F4 | Matching determinístico (`05-CATALOGUE.md:29-41`) | Streams Dispatcharr associados por nome normalizado | Associação frágil | Pendente |
| D-F5 | Ordering ≠ Priority (DL-010, DL-011) | `StreamOrderingPolicy` faz ordenação e preferência de provider | Conceitos misturados | Pendente |
| D-F10 / F16 | Output usa a selection | `playlist.m3u`/sync agendado podem ignorar a selection | Output inconsistente | Pendente (W6) |
| D-F12 | Entidades com consumidor | `ImportPolicy`/`CanonicalGroup.Order`/`GroupMapping` sem consumidor runtime | Funcionalidade inerte | Pendente (W6) |
| D-F14 | Constraints de invariantes (`16-PERSISTENCE.md:19-30`) | Índices de `channel_sources` não únicos → duplicados sob concorrência | Risco de duplicados | Pendente |
| Doc | Documentação derivada reflecte a BÍBLIA (`00-BIBLE.md:39-41`) | README/roadmap ainda descrevem país como fonte de aliases e ordem de matching inexistente | Documentação divergente | Pendente |
| Testes | Quality gates (`44-QUALITY-GATES.md`) | Flaky de timing; cobertura de endpoints esparsa | Regressões difíceis de isolar | Pendente |

## Decisions Since Last Status
ADRs (`docs/adr/README.md`):
- ADR-0001 country-data-ownership — Proposed (completo — pendente de aprovação)
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
- Build Release (`--no-incremental`): 0 errors / 52 warnings.
- Suite completa: 2137 passed / 0 failed / 1 skipped (total 2138).
- Skipped (by design): `HttpTimeoutAutopsyTests.LEGACY_PATTERN_blackhole_blocks_until_30s_HttpClient_timeout`.
- Flaky conhecidos que passam isoladamente: `XtreamAccountLockManagerTests`, `Phase93AccountGateCoordinatorTests`.

## Next Executable Step
W6 (audit records de mutações administrativas + ligar/esconder funcionalidades inertes), seguida do E2E de dois ciclos (idempotência Discovery→…→Dispatcharr). Os itens de W6 dependem de ADRs/contratos de API ainda em aberto.
