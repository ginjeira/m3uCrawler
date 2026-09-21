# 31 — Decision Lock: decisões normativas

**Estado:** REVISTO — BASE 1.1

Este documento existe para impedir que um implementador tenha de adivinhar decisões arquitecturais.

## A. Decisões FECHADAS

### DL-001 — O catálogo é a autoridade de identidade
`CanonicalChannel` é a identidade que o operador quer reconhecer. Não existe uma segunda lista funcional de canais.

### DL-002 — Unknown não cria identidade
Uma entrada desconhecida nunca cria implicitamente um `CanonicalChannel`.

### DL-003 — Review é obrigatório para incerteza
Unknown e Ambiguous seguem para Review. Uma heurística não pode esconder uma ambiguidade.

### DL-004 — Número de origem nunca é identidade
Número, posição e ordem de uma playlist são evidência de origem.

### DL-005 — Source enabled exprime intenção
Uma Source enabled é uma origem que o operador quer processar.

### DL-006 — Provider, Account e Source são conceitos distintos
Provider identifica o ecossistema; Account a conta; Source a origem concreta que será processada.

### DL-007 — ChannelSource é relação
`CanonicalChannel ↔ Source` é uma relação persistente. Não é uma segunda identidade.

### DL-008 — Validation não altera identidade
Observação técnica não pode criar, fundir, dividir ou renomear um canal.

### DL-009 — Eligibility é decisão derivada
Eligibility resulta de policy + evidence. Não é sinónimo de existência.

### DL-010 — Priority e Selection são distintas
Priority define preferência; Selection escolhe a stream concreta.

### DL-011 — Ordering não é identidade
Ordering atribui posição a um CanonicalChannel.

### DL-012 — VOD não usa ordering linear
VOD tem fluxo, grupos e output próprios.

### DL-013 — Dispatcharr não é autoridade
O estado remoto não altera o catálogo.

### DL-014 — Ownership protege recursos externos
Só `CrawlerManaged` pode ser removido automaticamente.

### DL-015 — Dry-run não produz efeitos remotos
Pode produzir plano/artifacts locais.

### DL-016 — Scheduler e manual usam o mesmo coordinator
Não existe pipeline alternativo.

### DL-017 — Run usa snapshot
Alterações feitas depois do início não alteram silenciosamente o significado do Run.

### DL-018 — Execuções são idempotentes
Repetição das mesmas entradas e políticas não cria duplicados nem churn injustificado.

### DL-019 — Output é atómico
Nunca publicar um ficheiro parcial.

### DL-020 — Secrets nunca entram em diagnóstico
Sanitização é obrigatória e centralizada.

### DL-021 — UI/CLI não são autoridade
São interfaces para os mesmos serviços da aplicação.

### DL-022 — SQLite é autoridade persistente do estado
Cache/artifacts não substituem a base de dados.

### DL-023 — Histórico e estado actual são distintos
Observation/Run/audit não devem ser destruídos para representar apenas o estado corrente.

### DL-024 — Uma ausência não é uma remoção
Não observar um canal numa execução não significa automaticamente apagar a relação.

### DL-025 — Dados externos são não confiáveis
Discovery, M3U, APIs e URLs descobertas passam por validação, limites e security policy.

## B. Decisões fechadas nesta revisão

### DL-101 — Selection é lexicográfico, não probabilístico
A implementação base usa uma ordenação determinística de critérios. Não há "score mágico".

Ordem:
1. override explícito do canal;
2. SourcePriority configurada;
3. Eligibility = Eligible;
4. preferência de media/qualidade definida na policy;
5. frescura da última validação bem sucedida, quando a policy a utilizar;
6. fingerprint estável;
7. ID estável.

Se a policy excluir um critério, ele é omitido. Se os critérios configurados não distinguirem duas opções, o último desempate é sempre ID estável.

### DL-102 — Priority é menor = mais prioritário
A prioridade é um inteiro ordinal. `1` é preferível a `2`.

### DL-103 — Precedência de policy
Para uma decisão:
`channel override → group override → global policy → system default`.

Uma camada só existe quando configurada; não existe "override vazio" que apague uma camada anterior.

### DL-104 — Histerese base
A mudança de estado publicada não ocorre por uma única falha transitória. A policy deve permitir definir limiar de falhas consecutivas e/ou janela temporal. Na ausência de configuração específica, conserva-se o último estado publicável enquanto existir evidência recente dentro da janela operacional.

O valor exacto da janela deve ser configuração operacional, não lógica escondida no código.

### DL-105 — Review lifecycle
`Open → InReview → Resolved`; `Open → Ignored` (motivo administrativo explícito, sem passagem obrigatória por `InReview`); `Open → InReview → Ignored`.
Reabertura `Resolved → Open` e `Ignored → Open`, sempre através de operação administrativa auditada e perante nova evidência materialmente incompatível. `InReview` significa início de tratamento administrativo.

### DL-106 — Country data
Country data é referência/versionada e pode ser actualizado sem alterar identidade canónica. Um import deve ser versionado e auditável.

### DL-107 — Baseline catalogue
O catálogo distribuído é baseline inicial. Alterações do operador constituem estado local. Um upgrade de baseline não pode apagar alterações locais sem operação explícita.

### DL-108 — Fingerprint
O fingerprint é versionado. A versão pertence ao algoritmo, não à aplicação global. Uma mudança incompatível cria nova versão e estratégia de coexistência/migração.

A versão inicial registada é `sfp1`: `Fingerprint = hex minúsculo de SHA-256(UTF-8("sfp1\n" + canonicalUrl))`, com a representação canónica e a política de credenciais fixadas em `04-PLAYLIST-STREAM.md §4.1`. O fingerprint é persistido em `ChannelSource` apenas como hash + versão; o URL canónico nunca é persistido. Múltiplas streams por `(CanonicalChannel, Source)` são suportadas; a identidade lógica persistente é `CanonicalChannelId + SourceId + Fingerprint + FingerprintVersion` (reconciliação com `07-SOURCES.md §5`).

### DL-109 — Scheduler
Por defeito existe uma única execução activa por runtime. O lock/lease deve ser adquirido antes de iniciar efeitos. Um lock perdido impede novas mutações e força estado de Run apropriado.

### DL-110 — API
Contratos incompatíveis recebem nova versão. Mudanças compatíveis podem evoluir no mesmo contrato.

### DL-111 — Provider adapters
Provider-specific code fica nos adapters. O domínio recebe contratos normalizados.

### DL-112 — Secrets
Secrets são armazenados por referência/secret store ou mecanismo equivalente isolado do modelo funcional. O mecanismo concreto deve ser compatível com o ambiente de deployment; nunca são duplicados desnecessariamente.

### DL-113 — Retenção
Retenção é configuração explícita. Nunca deve haver eliminação silenciosa de evidência essencial a uma execução ainda dentro da retenção.

### DL-114 — Single-instance baseline
A arquitectura alvo é single-instance por runtime. Alta disponibilidade/distribuição não faz parte da BÍBLIA 1.1.

### DL-115 — Transaction boundary
Transacções DB são curtas e delimitadas por mudança de estado; chamadas externas não permanecem dentro de uma transacção longa.

### DL-116 — External resource reconciliation
A aplicação calcula desired state a partir do snapshot e reconcilia o estado remoto observado. Nunca assume que uma chamada falhada não produziu efeito.

### DL-117 — Recognition fuzzy é opt-in
O passo de fuzzy matching só é executado com `RecognitionPolicy.Fuzzy.Enabled = true`. Sem policy activa, o reconhecimento não usa fuzzy. Abaixo do threshold: sem candidato → `UNKNOWN`; candidatos plausíveis abaixo do threshold → `AMBIGUOUS`. Um único candidato acima do threshold só produz `CANONICAL` se satisfizer as regras de desempate da policy. Threshold/margem/pesos são `PARAMETER_GAP` e a `RecognitionPolicy` é resolvida em snapshot imutável por Run.

### DL-118 — Contrato fuzzy de Recognition (W5.3)
O passo fuzzy (passo 6) compara a identidade normalizada apenas com `DisplayName` e `ChannelAlias.NormalizedAlias`, reutilizando `ChannelNormalizer.Normalize` e `FuzzyMatcher`. O score é inteiro `0..100` e **não** é `MatchConfidence`. Aceitação: `candidateScore >= Fuzzy.Threshold`. A banda `[Threshold - max(AmbiguityMargin,0), Threshold)` é plausível; sem candidato aceite, candidatos plausíveis produzem `AMBIGUOUS` e a ausência de candidatos produz `UNKNOWN`. Dois canais com diferença de score `<= AmbiguityMargin` são indistinguíveis → `AMBIGUOUS`; empates nunca escolhem o primeiro (sem `Id`/ordem de BD/inserção/playlist). Universo de candidatos: todos os `CanonicalChannel` activos e os respectivos aliases, sem dependência de Eligibility e sem blocking por provider/grupo/país. `Fuzzy.Weights` é JSON com chaves `displayName`/`alias` (chaves desconhecidas ignoradas; JSON inválido ≡ nulo ≡ pesos `1.0`). Threshold/margem/pesos permanecem `PARAMETER`; nulo/fora do intervalo com fuzzy ligado é fail-closed. O passo não cria identidade nem `ReviewItem` (W5.4). Contrato completo: `48-RECOGNITION-FUZZY-CONTRACT.md` F1–F14.

### DL-119 — Scope do lifecycle de Review (W5.4)
Fecha as fronteiras de scope de W5.4; **não** redefine a máquina de estados, que permanece em DL-105/`33` (`Open→InReview→Resolved`, `Open→Ignored`, `Open→InReview→Ignored`, reabertura `Resolved/Ignored→Open`). W5.4 implementa domínio, persistência, lifecycle, serviço interno e auditoria necessária ao lifecycle. A criação de `ReviewItem` a partir de `CatalogResolution.FuzzyDiagnostic` (`DecisionReason = fuzzy-ambiguous`) pertence a W5.4; W5.3 permanece exclusivamente reconhecimento fuzzy + diagnóstico e nunca cria `ReviewItem`. W5.4 altera o `ReviewItem` apenas no mínimo necessário ao lifecycle; `RunId`, `StreamId`, `Actor`, `Evidence`, `Candidates` e `Decision` não são inventados e, onde indispensáveis, ficam `OPEN`; a reconciliação completa do schema fica em W5.6 (`32` é autoridade). Reabertura em W5.4 é apenas operação administrativa explícita, auditada e justificada (`Resolved→Open`, `Ignored→Open`); deteção/reaabertura automática de "evidência materialmente incompatível" não faz parte de W5.4 e o critério permanece `OPEN`. W5.4 não cria rotas HTTP (W5.5 é a camada API, `22 §7`). `MatchMethod`/`MatchConfidence` permanecem W5.6 e não há conversão `FuzzyScore 0..100 → MatchConfidence 0..1`. M.4 (wiring do snapshot ao Run/pipeline) permanece `OPEN` e sem wave atribuída. O motor legacy `ChannelMatcher`/`MatchScorer`/`MatchingOptions` permanece fora de scope.

### DL-120 — Contrato da API HTTP de Review (W5.5)
Ratifica o scope e as fronteiras da camada HTTP de Review (`22 §7`; `41` Review), sem implementar nada.
**Operações (5, exactamente):** `GET /api/reviews`, `GET /api/review?id`, `POST /api/review/resolve`, `POST /api/review/ignore`, `POST /api/review/reopen`. **Não** se cria `/api/review/begin` nem qualquer sexta operação (D2): `InReview` é alcançado implicitamente por composição de arestas válidas (`resolve`: `Open→InReview→Resolved` ou `InReview→Resolved`; `ignore`: `Open→Ignored` ou `InReview→Ignored`).
**D1 — `resolve`:** operação administrativa única "declaração explícita de alteração + resolução"; termina em `Resolved`; nunca cria identidade implicitamente; a alteração de catálogo tem de ser declarada; `422 declared-change-invalid` para declaração inválida/incompatível. A declaração usa **apenas** capacidades de domínio comprovadamente existentes no estado actual (`canonicalChannel`, `channelAlias`, `externalIdentity`, `channelSource`); se faltar capacidade, reportar `W5.5 IMPLEMENTATION GAP` e **não** criar subsistema novo por inferência. Audit before/after.
**D3 — identidade:** `ReviewItem.Id` é a identidade normativa das novas APIs (`id`/`reviewItemId`); `Fingerprint` mantém-se apenas para compatibilidade das rotas legacy. Sem alteração de schema e sem migration.
**D4 — erros:** nas cinco novas rotas, formato `{ "error": <stable-code>, "message": <safe-message>, "correlationId": <opaque-id> }`; sem `Exception.Message`/detalhes internos. Códigos mínimos: `authentication-required`, `csrf-invalid`, `forbidden`, `invalid-filter`, `review-id-required`, `review-not-found`, `invalid-payload`, `reason-required`, `state-conflict`, `declared-change-invalid`, `persistence-error`; mapa `400/401/403/404/409/422/500`. `correlationId` opaco e por pedido; **não** se introduz um subsistema completo de tracing/correlation. Rotas legacy **não** são normalizadas nesta wave.
**D5 — legacy:** as rotas `GET /api/catalog/reviews` e `POST /api/catalog/reviews/{fingerprint}/approve|exclude` mantêm-se, sem alteração de semântica e sem remoção; depreciação/remoção definitiva é decisão futura. As novas rotas podem reutilizar serviços internos desde que não alterem a semântica legacy.
**D6 — paginação:** `state ∈ {Open,InReview,Resolved,Ignored}`; estado inválido → `400 invalid-filter`; `offset` operacional default `0`; `limit` default/máximo permanece `PARAMETER GAP` (um valor operacional só é aceitável se explicitamente documentado como tal, nunca como requisito normativo); ordenação determinística `CreatedAtUtc DESC, Id DESC`; rate limiting `OUT OF SCOPE`/`PARAMETER GAP`.
**D7 — `subject`/`runId`:** `subject = NormalizedIdentity` na listagem; **não** se cria `RunId` em `ReviewItemEntity` nem migration; `runId` permanece dependência de C7/W5.6 e a sua ausência é uma limitação actual do modelo (não uma solução normativa definitiva) — a implementação trata-a como tal.
**AuthZ:** as mutações usam o modelo actual de `Administrator` (autenticação = autorização enquanto só existir esse papel); **não** se cria migration de roles nem ACL/`Operator` em W5.5; RBAC fica fora de scope.
**Fronteiras:** W5.5 não altera `MatchMethod`/`MatchConfidence` (W5.6), o schema C7, M.4, o motor legacy, nem implementa reabertura automática. Contrato normativo: `22 §7`; scope/IN-OUT e dependências: Anexo Q do manifest e secção W5.5 de `46`. **Implementação (W5.5):** cinco rotas em `WebDashboardService.cs`; `resolve` suporta `channelAlias`/`canonicalChannel` (via `ApplyReviewApprovalAsync`); `externalIdentity`/`channelSource`/`none` são `W5.5 IMPLEMENTATION GAP` e devolvem `422 declared-change-invalid` (não se inventou domínio).

### DL-121 — Semântica de MatchMethod/MatchConfidence (W5.6)
Ratifica as decisões de contrato de W5.6 (decision pack `.kilo/plans/w56-decision-pack.md`); **não** implementa nem fixa valores concretos.
**D-W56-01 — versão da semântica:** a semântica de `MatchMethod`/`MatchConfidence` é **versionada**; a versão identifica as regras/algoritmo que produziram o reconhecimento. A versão pertence à semântica/algoritmo/policy e **não** é uma propriedade arbitrária introduzida individualmente pelo operador num `ChannelSource`. O mecanismo físico concreto (constante no algoritmo, coluna persistida em `ChannelSource`, ou campo de policy/snapshot) é definido na especificação W5.6, respeitando os precedentes existentes (`sfp1`/DL-108, policy `Version`/DL-110); não se inventa o nome do campo nesta decisão.
**D-W56-02 — `MatchConfidence` é method-specific:** domínio `double 0..1`, mas a semântica é específica do `MatchMethod`. Valores de métodos diferentes **não** são automaticamente comparáveis; **não** existe escala global "melhor método → maior número". Cada método normativo (`ExternalIdentityExact`, `TvgIdExact`, `CanonicalExact`, `NormalizedName`, `KnownAlias`, `ExplicitHeuristic`, `Fuzzy`, `ManualReview`) deve ter regra normativa própria, a documentar antes de produzir valores. Os valores/regras concretos permanecem **OPEN** (especificação W5.6).
**D-W56-03 — `FuzzyScore` ≠ `MatchConfidence` (já ratificado, preservado):** proibida a conversão directa `FuzzyScore 0..100 → MatchConfidence 0..1`. Fechado por DL-119; **não** reabrir.
**D-W56-04 — `Unknown`/`Ambiguous`:** `MatchConfidence = null` em ambos. `MatchConfidence` representa confiança do reconhecimento/resolução registado; em `Unknown` não há resolução canónica e em `Ambiguous` não há evidência suficiente para seleccionar uma resolução única. Proibido usar `0` ou o `FuzzyScore` do melhor candidato como substituto. Não altera o comportamento actual do Review.
**D-W56-05 — producer boundary:** a decisão semântica de `MatchMethod`/`MatchConfidence` pertence à camada de **Recognition**; `CatalogResolution` transporta o resultado semântico (`MatchMethod` e `MatchConfidence`); o **pipeline** apenas persiste em `ChannelSource`, nunca inventa/recalcula. O `const double confidence = 1.0` actual em `PipelineIngestionService` é **divergência a corrigir em W5.6**.
**D-W56-06 — fronteira C7:** W5.6 **não** implementa campos C7 sem contrato implementável (`RunId`, `StreamId`, `Actor`, `Evidence`, `Candidates`, `Decision`); pode documentar a fronteira e reconciliar documentação, mas **não** inventa modelo C7. **M.4 permanece OUT** (`W5.6 ≠ M.4`).
**D-W56-07 — endpoint manual:** o endpoint manual **não** pode persistir silenciosamente `MatchMethod` inválido; a implementação W5.6 valida `MatchMethod` contra os 8 valores normativos, `MatchConfidence` contra `0..1` e a semântica/versionamento, preservando compatibilidade sempre que possível. Não se altera o endpoint nesta decisão.
**OPEN (para a especificação/implementação W5.6):** valores/regras concretos por método; mecanismo/nome físico da versão; detalhes de compatibilidade do endpoint manual; eventual reconciliação documental de C7. `44` Q4 só fica satisfeito após a implementação de W5.6.

### DL-122 — Valores, versão e política do endpoint manual de W5.6 (especificação ratificada)
Ratifica as decisões que estavam `OPEN` em DL-121 e fixa a especificação normativa
`49-W56-MATCH-CONFIDENCE-SPECIFICATION.md` (implementação **pendente**; **não** implementa código,
schema ou endpoints).

**OD-A — `ExplicitHeuristic`.** `MatchConfidence = 0.80` (constante; heurística explícita,
não-exacta).

**OD-B — `Fuzzy`.** `MatchConfidence = 0.60` (constante). **Estrtamente proibido** calcular a
partir de `FuzzyScore`: sem `FuzzyScore/100` nem qualquer transformação equivalente. `FuzzyScore`
permanece `int? 0..100` **diagnóstico**, sem relação numérica com `MatchConfidence`.
`Ambiguous → null`; `Unknown → null`.

**OD-C — `ManualReview`.** `MatchConfidence = 1.0`, significado "explicitamente confirmado via
review" (autoridade da decisão humana), **não** certeza matemática e **sem** comparabilidade
global com os restantes valores.

**Métodos exactos (ratificados):** `ExternalIdentityExact = 1.0`, `TvgIdExact = 1.0`,
`CanonicalExact = 1.0`, `NormalizedName = 1.0`, `KnownAlias = 1.0`.

**Tabela normativa (8 métodos):**

| MatchMethod | MatchConfidence |
|---|---|
| ExternalIdentityExact | 1.0 |
| TvgIdExact | 1.0 |
| CanonicalExact | 1.0 |
| NormalizedName | 1.0 |
| KnownAlias | 1.0 |
| ExplicitHeuristic | 0.80 |
| Fuzzy | 0.60 |
| ManualReview | 1.0 |

**Regra global:** `double 0..1`; **method-specific**; **não** existe escala global comparável
(nunca ordenar métodos pelo número); `Unknown → null`; `Ambiguous → null`;
`FuzzyScore ≠ MatchConfidence`.

**OD-D — versionamento.** `MatchSemanticsVersion = "msm1"`, **persistida em `ChannelSource`**
a par de `MatchMethod`/`MatchConfidence`. Identifica as regras/algoritmo que produziram o par
(proveniência derivada do algoritmo), **não** uma propriedade arbitrária do operador. Rows novas
recebem `"msm1"`. **Não** é exigida de clientes legacy do endpoint manual (o servidor atribui a
versão corrente). **Proibido** usar `RecognitionPolicy` como substituto; **distinta** de
`FingerprintVersion` (conceitos independentes).

**OD-E — política do endpoint manual.** Se o payload **omite** `MatchMethod` → preservar o
comportamento existente (não converter payload legacy em erro W5.6). Se `MatchMethod` é
fornecido → aceitar **apenas** os 8 valores normativos. Se `MatchConfidence` é fornecido →
aceitar **apenas** `double` em `0..1`. Se ambos → validar a combinação per W5.6.
`MatchSemanticsVersion` não é exigido no payload; o servidor atribui `"msm1"` a registos novos.
Valores explicitamente inválidos → erro de validação, **sem persistência parcial**, mantendo o
contrato de erro da API onde compatível com W5.5. **Não** remover/alterar endpoints legacy.

**Fronteiras:** C7 permanece `OPEN`/limitado (nenhum campo C7 é adicionado); M.4 permanece `OUT`
(`W5.6 ≠ M.4`). Especificação normativa: `49`; `05 §4.1`, `32` (ChannelSource), `44`, `46` e
manifest (Anexo R/S) actualizados. `44` Q4 continua **não satisfeito** até à implementação.

### DL-123 — Follow-up W5.6: F7-B, F8-B e alinhamento F9 (ratificado; implementado)
Ratifica as decisões de compatibilidade do endpoint manual identificadas na revisão pós-W5.6 (decision pack `.kilo/plans/w56-followup-f7-f8-f9.md`). Estado textual alinhado com a implementação da wave de follow-up (F7-B em `WebDashboardService`; F8-B em `CatalogResolver.RecordChannelSourceAsync`; F9 documental).
**F7-B — `MatchMethod` presente ⇒ `MatchConfidence` obrigatória:** no endpoint manual, quando `MatchMethod` é fornecido explicitamente e `MatchConfidence` está ausente → **HTTP 400**, sem persistência. **Proibido** auto-preencher o valor do método (não fabricar `Fuzzy→0.60` etc.) e **proibido** persistir método normativo com `MatchConfidence = null`. A produção semântica de `MatchConfidence` continua em **Recognition**. Combinações fornecidas mantêm a validação da tabela (`49 §7`): `Fuzzy+0.60` válido; `Fuzzy+0.80`/`1.0` → 400; `ExplicitHeuristic+0.80` válido; `CanonicalExact+1.0` válido; `CanonicalExact+0.80` → 400; `ManualReview+1.0` válido; `ManualReview+0.0` → 400.
**F8-B — `msm1` só para pares W5.6:** `MatchSemanticsVersion = "msm1"` identifica apenas pares em que `MatchMethod` é um dos 8 valores normativos, `MatchConfidence` foi validado e o par corresponde à tabela. Payload legacy sem `MatchMethod` (método `"unknown"`, confidence `0`) fica com **`MatchSemanticsVersion = null`**; é **proibido** estampar `"msm1"` nesse caminho e é **proibido** criar valores como `"legacy"`.
**Relação legacy vs W5.6:** legacy payload (sem método) → `"unknown"` + `0` + versão `null`; payload W5.6 explícito (com método) → confidence obrigatória, combinação validada, versão `"msm1"`.
**F9 — alinhamento documental:** a rota real do endpoint manual é `POST /api/catalog/sources/{sourceId}/streams` (não `.../channel-sources`, que não existe e não deve ser criada); `ChannelSourceEntity.MatchConfidence` é `double?`. Alinhado em `49 §4`/§10/§12.
**Fronteiras:** sem migration/schema novo; C7 `OPEN`; M.4 `OUT`; motor legacy inalterado; sem conversão `FuzzyScore→MatchConfidence`.

### DL-124 — Identidade autoritativa do Run (M.4 D-M4-01; ratificado)

Ratifica a decisão humana **D-M4-01** do M.4 Decision Pack (HEAD `780fa01`). Resolve a ambiguidade A/B/híbrido sobre a identidade do Run. Esta decisão é **documental/governance**: **não** implementa M.4 e **não** fecha D-M4-02..D-M4-10 (que permanecem `OPEN / DECISION REQUIRED`, ver Anexo M do manifest).

**D-M4-01a — identidade autoritativa (RATIFIED = A).** `RunCoordinator.RunId` é o RunId operacional e a identidade autoritativa do Run. É a identidade usada para:
- snapshots de Recognition Policy (`recognition_policy_snapshots.RunId`);
- correlação de dados pertencentes ao Run (por exemplo `discovery_candidates.RunId`).

`PipelineTrace.RunId` **não** é identidade de Run; permanece exclusivamente um identificador de diagnóstico/observabilidade e **não** pode ser usado como substituto do RunId operacional.

**D-M4-01b — caminhos sem coordenador (RATIFIED = B1).** Caminhos sem `RunCoordinator` não têm Run operacional e **não** devem fabricar um RunId apenas para satisfazer o mecanismo de snapshots. Nesses caminhos: não criar artificialmente um novo RunId só para Recognition Policy; não associar `PipelineTrace.RunId` a um Run; não criar snapshot de Recognition Policy sem RunId operacional. O comportamento de Recognition nesses caminhos (policy default, `policy:null` ou outra semântica) **não é decidido aqui** e pertence a **D-M4-06** (`OPEN`).

**Evidência (FACT; HEAD `780fa01`):**
- `RunCoordinator.RunId` = `Guid.NewGuid().ToString()` por run, persistido em `live_runs` (obrigatório, único): `RunCoordinator.cs:118,221`; `CatalogEntities.cs:547`; `ChannelCatalogDbContext.cs:685,695`.
- O RunId do coordenador já chega ao scraper via `ILiveRunProgress.RunId`: `Program.cs:512`; `ILiveRunProgress.cs:31`; `LiveRunMonitor.cs:82`.
- `PipelineTrace.RunId` é 12-hex e processo-scoped (um por processo, partilhado por vários runs), só existe com `M3UCRAWLER_TRACE`; por omissão é `null`: `PipelineTrace.cs:204-207`; `Program.cs:299-306`; `TelegramScraperService.cs:41,57-60`. Nota histórica: em HEAD `780fa01`, `TelegramScraperService.cs:452,561` alimentavam a ingestão com o trace; esse wiring foi removido por **D-M4-02a/DL-125** (ver abaixo).
- `PipelineIngestionService.IngestAsync` já tem o slot `string? runId`: `PipelineIngestionService.cs:150-156`.

**Fronteiras:** M.4 permanece `OPEN`/sem wave de implementação; esta ratificação não altera código, `PipelineTrace`, `RunCoordinator`, `TelegramScraperService`, `CatalogResolver`, `RecognitionPolicyResolver`, schema/migration ou testes funcionais. A enumeração e o estado de D-M4-02..D-M4-10 mantêm-se no M.4 Decision Pack e no Anexo M do `BIBLE_IMPLEMENTABILITY_GAP_MANIFEST_1.0.md`.

### DL-126 — D-M4-02: Recognition Policy Snapshot Lifecycle (ratificado; implementado)

Ratifica **D-M4-02**. O snapshot de policy é criado **antes** do `pipeline.ExecuteAsync` e é imutável (UNIQUE em `recognition_policy_snapshots.RunId`). A policy efectiva consumida pela ingestão provém exclusivamente do snapshot; nunca da policy mutável (B1).

- **A1 (citado de DL-103):** `channel → group → global → system/default`.
- **B1 modificado:** o `CatalogResolver.ResolveAsync` permanece um núcleo que recebe `RecognitionPolicy?` por injeção. **NÃO** consulta directamente as tabelas `recognition_policies`/`recognition_policy_snapshots`. A policy do snapshot é lida **uma vez** por ingestion call (em `TelegramScraperService`/`RecognitionPolicyResolver.GetSnapshotPolicyAsync(runId, null, null)`) e propagada ao overload de `ResolveAsync(normalized, tvgId, policy, ct)`. Política viva nunca é consumida em fallback.
- **C1 (race-safety):** a inserção em `recognition_policy_snapshots` está protegida por UNIQUE em `RunId`. O caminho rápido é a leitura prévia (curto-circuito). O caminho lento usa o mesmo idioma de `RecordExternalIdentityAsync` (`CatalogResolver.cs:466`): envolver o `SaveChangesAsync` em `try/catch (DbUpdateException)` e re-ler o registo do vencedor. Erros não-UNIQUE propagam-se.
- **D1 (insert-only por código):** o snapshot é criado por `RecognitionPolicyResolver.CreateSnapshotAsync`; uma vez persistido, nunca é regravado. Tentativas repetidas para o mesmo `RunId` devolvem o existente.
- **E1 (fail-closed no arranque do Run):** falha de criação do snapshot durante `RunCoordinator.RunCoreAsync` marca o run terminal `Failed` com `lastMessage = "failed: snapshot creation exception"`, **não** invoca `pipeline.ExecuteAsync`, e devolve `Succeeded = false`. `OperationCanceledException` propaga (não é engolida).
- **Run-less path (sem `ILiveRunProgress`):** sem RunId operacional ⇒ nenhum snapshot é criado, nenhuma `RunId` é fabricada, e nenhuma policy é propagada à ingestion. `PipelineTrace.RunId` continua a ser diagnóstico e nunca identidade de Run (DL-124/DL-125).

**Implementação:**
- `CatalogResolver.SaveRecognitionPolicySnapshotAsync` (`Services/Catalog/CatalogResolver.cs:3681-3758`) — race-safe via `DbUpdateException` re-leitura.
- `RunCoordinator.RunCoreAsync` (`Services/LiveRun/RunCoordinator.cs:319-371`) — orquestração snapshot → pipeline → terminal.
- `RunCoordinator.SetRecognitionPolicyResolver` — setter opcional para wiring via `LiveRunHost` sem alargar a superfície pública.
- `TelegramScraperService.SetRecognitionPolicyResolver` + leitura em `SearchAndTestM3UInTelegramAsync` (`Services/TelegramScraperService.cs:73-82`, `:600-617`).
- `PipelineIngestionService.IngestAsync` ganha parâmetro opcional `RecognitionPolicy? policy = null` (`Services/Catalog/PipelineIngestionService.cs:156`, `:282-289`).
- `Program.cs:430-450, 580-600` — wiring de `new RecognitionPolicyResolver(catalogForIngestion)` para `scraper` e para o `RunCoordinator` (caminho host + caminho direct).

**Testes:** `m3uCrawler.Tests/DM402SnapshotLifecycleTests.cs` (6): start creates snapshot; creation failure blocks pipeline & marks Failed; sequential idempotency; 16 concurrent calls never throw; snapshot frozen after policy change; snapshot.RunId is coordinator RunId (never trace). Sem migration, sem mudança de schema, sem dependências novas, sem alteração de `ResolveAsync`, fuzzy, W5.3/W5.5, Review→Output.

**Itens OPEN remanescentes:** D-F, D-G, D-H, D-I, D-J, D-M4-03..D-M4-10, C7, W2-FU, Review→Output.

### DL-125 — D-M4-02a: identidade de Run na ocorrência de descoberta (ratificado; implementado)

Ratifica **D-M4-02a**. `discovery_candidates.RunId` representa exclusivamente a identidade operacional `ILiveRunProgress.RunId` = `RunCoordinator.RunId`. `PipelineTrace.RunId` não é identidade operacional em nenhum ponto da ingestão de descoberta.

- **D-M4-02a-1:** `TelegramScraperService.ResolveOperationalRunId(ILiveRunProgress?)` fornece o RunId ao gate in-memory e à `PipelineIngestionService.IngestAsync(runId)`. É proibido usar `PipelineTrace.RunId`.
- **D-M4-02a-2:** sem Run operacional → `runId = null` (sem fabrico; sem fallback para o trace; sem dedup `(RunId, ProviderAccountId)`).
- **D-M4-02a-3:** `RunCoordinator.RunId` = identidade operacional; `PipelineTrace.RunId` = diagnóstico; não intercambiáveis.

**Correcção a DL-124:** a evidência anteriormente indicada em `TelegramScraperService.cs:452,561` descrevia o uso do trace na ingestão; esse uso foi removido por D-M4-02a. A identidade operacional continua a chegar ao scraper via `ILiveRunProgress.RunId`.

**Fronteiras:** sem alteração de schema/migrations, `DiscoveryCandidate`, índice, `RecognitionPolicy`, snapshots, `ResolveAsync`, fuzzy, W5.3/W5.5, Review→Output, C7, W2-FU, matcher legacy, Dispatcharr. D-M4-02..D-M4-10 permanecem `OPEN`.

### DL-127 — W-REVIEW-02B: Identidade persistente de `ChannelSource` + atomicidade de `CreateChannel` (ratificado; implementado)

- **Identidade persistente de `ChannelSource`** é `(CanonicalChannelId, SourceId, Fingerprint, FingerprintVersion)`. Aplicação impõe via lookup (`RecordChannelSourceAsync`); schema impõe via UNIQUE filtered index `IX_channel_sources_Channel_Source_Fingerprint_Unique WHERE "Fingerprint" IS NOT NULL` (W-REVIEW-02B). Coexistência de rows `Fingerprint IS NULL` preservada. Múltiplas streams distintas por `(CanonicalChannelId, SourceId)` continuam permitidas (D2).
- **`CreateChannel` é atómico** — `Database.BeginTransactionAsync` envolve ambos os `SaveChangesAsync` (canonical-create + alias+review+ChannelSource). O risco pré-existente de canonical órfão (SaveChanges #1 committed, SaveChanges #2 falhava) está eliminado.
- **Duplicate `CanonicalChannel.Key` race** é traduzido para `ChannelAdministrationException(DuplicateKey)` (HTTP 409) na primeira escrita de `CreateChannel`.
- **Sem `RowVersion` / `xmin` / optimistic concurrency token em qualquer entidade** — provider de produção é SQLite (sem row version system column). As UNIQUE pré-existentes em `channel_aliases.NormalizedAlias` e `canonical_channels.Key` + a nova UNIQUE de `channel_sources` fecham as janelas de corrida reais para o deployment single-instance actual. Concurrency tokens só serão reconsiderados se a topologia mudar para multi-instance (com eventual revisão do provider).
- **Reforço:** não foi adicionado navigation property em `CanonicalChannelEntity` para suportar single-save. A escolha deliberada é manter dois `SaveChanges` (necessários para conhecer `CanonicalChannel.Id` antes do FK assignment) dentro de uma única transacção explícita.

## C. Regra

Uma implementação que contradiga DL-001..127 está incorrecta relativamente à BÍBLIA.

Uma alteração destas decisões exige alteração explícita da BÍBLIA, testes e documentação derivada.
