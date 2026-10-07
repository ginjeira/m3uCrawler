# 44 — Quality Gates da BÍBLIA

## Gate Q1 — Domain
Sem duplicação de autoridade; invariantes testadas.

## Gate Q2 — Ingestion
Fixtures determinísticas; limites e cancelamento.

## Gate Q3 — Recognition
Exact match, alias, unknown e ambiguity testados.

Estado W5.0: os gates existentes cobrem exact/alias/unknown/ambiguity. A verificação do gate fuzzy opt-in (`RecognitionPolicy.Fuzzy.Enabled`), do passo de nome normalizado e do scoping por namespace de provider fica **pendente** de W5.1–W5.3.

Estado W5.1–W5.3: gate fuzzy opt-in, passo de nome normalizado e scoping por namespace de provider verificados — **satisfeito** (W5.1 `WaveW51RecognitionPolicyTests`, W5.2 `WaveW52RecognitionOrderTests`, W5.3 `WaveW53FuzzyRecognitionTests`; notar que o passo fuzzy só corre com uma `RecognitionPolicy`/snapshot — M.4 permanece `OPEN`).

## Gate Q4 — Review
Transições e auditoria testadas.

Estado W5.0: as transições `InReview`/`Resolved`/`Ignored` e a reabertura auditada (DL-105) estão especificadas mas **não implementadas/testadas**; o gate Q4 só é considerado satisfeito após W5.4–W5.5. A implementação histórica `Open/Approved/Excluded` sem reopen é `DIVERGENT` e não satisfaz este gate.

Estado W5.4: lifecycle **implementado** (DL-119) — estados `Open/InReview/Resolved/Ignored`, transições normativas, `Ignore` com motivo, reabertura manual auditada/justificada, auditoria e `ReviewItem` de fuzzy ambiguity. Evidência: `WaveW54ReviewLifecycleTests.cs` (42). O gate Q4 só fica **satisfeito** após W5.5 (API HTTP `22 §7`) e W5.6 (`MatchMethod`/`MatchConfidence`); a reabertura automática permanece `OPEN`.

Estado W5.5: API HTTP **implementada** (DL-120) — 5 rotas de `22 §7`, identidade `ReviewItem.Id`, formato de erro `{error,message,correlationId}`, integração com o lifecycle W5.4, audit e testes (`WaveW55ReviewApiTests`, 38). `W5.5 IMPLEMENTATION GAP`: no `resolve`, `externalIdentity`/`channelSource`/`none` não têm operação de domínio declarada e são rejeitados com `422`. Q4 fica **satisfeito** apenas após W5.6 (`MatchMethod`/`MatchConfidence`).

Estado W5.6: **implementado** (DL-121/DL-122; `49-W56-MATCH-CONFIDENCE-SPECIFICATION.md`) — valores concretos por método (`ExternalIdentityExact`/`TvgIdExact`/`CanonicalExact`/`NormalizedName`/`KnownAlias`=`1.0`, `ExplicitHeuristic`=`0.80`, `Fuzzy`=`0.60`, `ManualReview`=`1.0`), semântica method-specific sem escala global, `MatchSemanticsVersion="msm1"` persistida em `ChannelSource` (migration `AddMatchSemanticsVersionAndNullableMatchConfidence`), `Unknown`/`Ambiguous → null`, producer em Recognition (`CatalogResolution.MatchConfidence`), persistência sem recálculo pelo pipeline (fim do `const 1.0`) e validação HTTP do endpoint manual (OD-E). Evidência: `WaveW56MatchConfidenceTests` (45/45, follow-up F7-B/F8-B/F9 `780fa01`); suite 2577/1/0. C7 permanece `OPEN`/limitado e M.4 permanece `OUT`. Q4 fica **satisfeito** (W5.4 + W5.5 + W5.6; a reabertura automática permanece `OPEN`).

## Gate Q5 — Source state
ChannelSource/Observation/Eligibility separados.

## Gate Q6 — Selection
Ranking e desempate determinísticos.

## Gate Q7 — Output
Ordering, groups e M3U verificáveis.

## Gate Q8 — Dispatcharr
Ownership, dry-run e partial failure.

## Gate Q9 — Runtime
Snapshot, lock, cancellation, restart.

## Gate Q10 — Security
Secrets, auth, authz, SSRF, parser limits.

## Gate Q11 — Operations
Fresh install, upgrade, backup/restore.

## Gate Q12 — Full E2E
Execução real de ponta a ponta com fixtures/controlos apropriados.

## Gate Q13 — Rastreabilidade
Toda a aceitação deve ser rastreável como `Requirement → BÍBLIA → implementação → teste → evidência → gate`. Nenhum requisito é aceite sem um gate rastreável.

Nenhum gate é concluído sem evidência reproduzível.
