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

### DL-128 — Contrato HTTP de Dispatcharr dry-run / sync (ratificado; NÃO implementado)

Ratifica o contrato HTTP de Dispatcharr dry-run/sync (`22 §20.6`, `22 §20.7`, `41-API-INVENTORY.md` Dispatcharr). **NÃO** implementa endpoints, **NÃO** cria handlers, **NÃO** altera `WebDashboardService.cs`/`DispatcharrSyncService`/`DispatcharrSyncCoordinator`/`RunCoordinator`, **NÃO** cria migrations. Fecha o `DG-19` parcial e prepara a próxima wave de implementação.

**D1 — paths.** `POST /api/dispatcharr/dry-run` e `POST /api/dispatcharr/sync`. Forma exacta; **não** se criam variantes (`dryrun`, `dry_run`, etc.). Família usa prefixo `/api/dispatcharr/...` (`WebDashboardService.cs:900, 9653-9654, 9797, 9860`). Método diferente de `POST` → `405`.

**D2 — request.** Corpo `application/json` mínimo `{"playlistPath": "..."}`. `playlistPath` corresponde directamente ao parâmetro de domínio consumido por `DispatcharrSyncService.RunAsync(playlistPath, selection, ct)` (`DispatcharrSyncService.cs:119`). **Não** se introduz `PlaylistId`. **Não** se cria entidade `Playlist`. **Não** se associa `RunId → playlistPath`. **Não** se expõe `DispatcharrSourceSelection` no contrato HTTP — esse DTO contém `StreamUrl` (portador de credenciais sanitizadas via `CredentialSanitizer.SanitizeUrl`) e permanece artefacto interno/sanitizado; fica de fora desta superfície.

**D3 — sem override `dry_run`.** Os dois endpoints **não** aceitam qualquer campo de override. Concretamente: corpo **não** inclui `dry_run`, `dryRun`, `dry_run=false`, `dry_run=true` nem `dispatcharr_dry_run`; aceitar-e-ignorar está **proibido** (payload com `dry_run*` é `400 invalid-payload`); sem mecanismo alternativo (env, header, cookie, query string). `/dry-run` significa sempre dry-run; `/sync` significa sempre apply/sync per configuração persistida (`dispatcharr_dry_run` é decisão operacional explícita do administrador em `wtelegram.config`; AGENTS.md §2). Inventar override por request é nova decisão, não autorizada por esta DL — wave própria se for desejado.

**D4 — response.** Híbrida, sanitizada, sem embedding integral do plano/relatório:
```json
{ "status": "...", "mode": "dry-run|sync",
  "planPath": "...", "reportPath": "...", "counts": {} }
```
Forma **orientativa** (D4): fixa orientação estrutural — referência a artefactos e contagens, sem credenciais, sem conteúdo integral embutido, sem campos inventados. **Não** copiar verbatim para o código. Reutilizar tipos existentes (`SyncReportCounts`, `DispatcharrSyncResult`, `DispatcharrSyncOutcome`) precede criação de novos DTOs. Se a implementação revelar necessidade adicional, regista-se como discrepância — **não** se resolve silenciosamente.

**D5 — success status.** `200 OK` — operação síncrona neste contrato. `202 Accepted` **não** é aceitável; se a implementação revelar natureza assíncrona, é discrepância contratual que **bloqueia** a wave de implementação.

**D6 — errors.** Envelope canónico: `{"error": "...", "message": "...", "correlationId": "..."}` (consistente com DL-120/§22 §2/§8). Códigos: `401 authentication-required`; `403 forbidden`/`csrf-invalid`; `409 concurrency-conflict`; `422 invalid-payload`; `500 persistence-error`; `502 dispatcharr-comm-error` (apenas `/sync`); `503 dispatcharr-unavailable` (`CatalogUnavailable`; `DispatcharrSyncCoordinator.cs:127-125`). `Ambiguous` é **estado de domínio**, não erro HTTP — preserva-se `SyncOutcome.Ambiguous` e exposição via `counts.ambiguous` no payload. `409 dry-run-blocked` (placeholder da wave anterior) é **removido** por D3.

**D7 — artifacts.** Canónicos e inalterados: `dispatcharr_plan_<ts>.json` e `dispatcharr_report_<ts>.json` (`DispatcharrSyncService.cs:169, 251`); opcionalmente `dispatcharr_selection_<ts>.json` quando há selecção aplicada (`:178-180`). HTTP devolve **referências/paths**, **não** conteúdo integral embutido. Sanitização obrigatória (`MatchPlanSerializer.SanitizeForSerialization` + `CredentialSanitizer.SanitizeUrl`), filesystem-local canónico, geradores de artefactos inalterados. Credenciais Xtream **nunca** expostas via HTTP.

**D8 — concurrency.** No máximo **uma** Dispatcharr sync activa por runtime. Segunda tentativa concorrente → `409 concurrency-conflict`. O mecanismo concreto é um **gate de concorrência dedicado ao Dispatcharr** — segue o **mesmo princípio atómico** de `RunCoordinator` (CAS em memória; `RunCoordinator.cs:66, 105`; DL-109) mas **não** o reutiliza directamente (gate Telegram-scoped; sem mistura de semânticas): aquisição atómica; falha do segundo candidato com conflito; libertação garantida em `finally`; não dependente apenas de um flag `IsRunning` (TOCTOU); não cria segunda semântica de Run Telegram; não altera `RunCoordinator`. Concretização diferida para a wave de implementação (não pertence a esta wave).

**D9 — Administrator.** `Administrator` = administrador autenticado corrente; **não** se introduz Role/Claim/Permission/Group/ACL/migração RBAC. `AuthModeResolver` permanece intocado. `RequireAdministratorAsync` não é criado (redundante). Autenticação = autorização enquanto só existir este papel — convenção ratificada por `31-DECISION-LOCK.md` (preservada); Operator/RBAC futuro é wave própria.

**D10 — audit.** `SyncRunEntity` permanece registo canónico de auditoria (`running` → `ok`/`partial`/`error:<ExceptionType>`/`dry-run`; passos `read-plan`/`selection`/`apply`/`apply-create`/`apply-associate`/`apply-remove`/`apply-protected`/`apply-errors`/`dry-run`). Apenas `Type` da excepção é persistido em falha, nunca a mensagem (DL-020). **Não** se duplica a mesma operação através de um segundo mecanismo de auditoria HTTP. Se um evento HTTP-nível for necessário por observabilidade/compliance, demonstra-se durante a implementação — **não** se inventa agora.

**Fora do scope desta DL:**
- Endpoint HTTP propriamente dito (handlers, DTOs HTTP, parser). Wave própria de implementação.
- Gate de concorrência dedicado ao Dispatcharr (D8) — concretização diferida.
- Rate limit — `PARAMETER_GAP` (família `22 §20`/`§8`).
- Schemas concretos (`counts`, `status`) — orientativos; enumerar durante implementação, com registo de discrepâncias.
- `reconciliation` e `ownership view` (`22 §20.8`) — permanecem `NOT IMPLEMENTED`; esta DL fecha apenas `dry-run` e `sync`.
- `Ordering`/`Q-D` (§17.9) — **não tocado** por esta DL.

**Estado operacional:** `22 §20.6` e `22 §20.7` actualizados de `NÃO IMPLEMENTADO` para `CONTRACT RATIFIED (DL-128)`. `46-REQUIREMENT-TRACEABILITY.md` linha `API-Dispatcharr` actualizada de `PARTIAL (dry-run/sync reconciliation/ownership HTTP não expostos via dashboard endpoints)` para `PARTIAL (contrato RATIFICADO por DL-128; NOT IMPLEMENTED)`. Implementação (handlers, gate de concorrência, tests HTTP) pertence a wave subsequente.

**Estado operacional** (verificação cross-cutting):

- **RATIFIED** — DL-128 mantém-se ratificada nesta wave (sem reabertura arquitectural).
- **HTTP IMPLEMENTATION** — endpoints `POST /api/dispatcharr/dry-run` e `POST /api/dispatcharr/sync` SHIPPED at HEAD `4b6643e77bd87328d94d1df1097e727545de7b30` (commit `feat(dispatcharr-http): implement POST /api/dispatcharr/{dry-run,sync} per DL-128`). Ver `m3uCrawler/Services/WebDashboardService.cs:976,985`, `m3uCrawler/Services/Sync/DispatcharrSyncCoordinator.cs:98`, `m3uCrawler/Services/Sync/DispatcharrConcurrencyGate.cs`, `m3uCrawler.Tests/W2DispatcharrHttpTests.cs`.
- **RUNTIME-VALIDATED (dry-run)** — pipeline dry-run exercida end-to-end via `W-RUNTIME-DISPATCHARR-DRYRUN-CONTROLLED` contra `m3uCrawler.Tests/TestData/m3ucrawler_playlist_20260831_204529.m3u` (579 streams), num Dispatcharr simulado em `127.0.0.1:18080`. Resultado: `Matched=0  NewChannels=43  NewStreams=164  RemovedStreams=0  Skipped=139  Ambiguous=0  Unchanged=0  Failed=0`. HTTP observado: 4 GETs read-only (`/api/core/version/`, `/api/channels/{channels,streams,groups}/`), 0 writes. Configuração temporária restaurada byte-a-byte.
- **NOT RUNTIME-VALIDATED (apply/sync)** — `POST /api/dispatcharr/sync` (apply real) ainda não foi exercitado contra Dispatcharr real. Wave runtime exerceu apenas o branch dry-run. Esta lacuna é mantida aberta como **OPEN** e pertence a wave futura (não fecha nesta).

### DL-129 — `ReviewItem.StreamUrl` como fonte funcional de publicação para `playlist.m3u` (ratificado)

Ratifica a aceitação de `ReviewItemEntity.StreamUrl` (RAW verbatim, persistido em SQLite como evidência de ingestion) como **fonte interna autorizada** para construir `M3uStream.Url` e republicar `playlist.m3u` após `POST /api/review/resolve`, sem voltar a executar Telegram discovery/ingestion e sem introduzir `ProviderUrlResolver`. **Não** implementa o caminho Review→Output (D4-A vs D4-B permanece por decidir em wave própria); **não** altera schema/migrations; **não** resolve SecretStore (DL-112 continua OPEN/PARTIAL).

#### Contexto

A aprovação de uma Review (`Open → Resolved`) materializa um `ChannelSourceEntity` com `StreamUrl` **sanitizado** (chokepoint `CatalogResolver.cs:3137`). Para republicar `playlist.m3u` — artefacto funcional que por definição do projecto preserva URLs Xtream reais (AGENTS.md §2) — é necessário construir `M3uStream` com URL **RAW**. O pipeline normal obtém essa URL via Telegram scrape em memória; essa informação **não** é persistida. `StreamFingerprint` é one-way (DL-108), e `ProviderAccount.CredentialsReference` é write-only (DL-112 não implementado). Resta, portanto, `ReviewItemEntity.StreamUrl` como a única fonte RAW persistida que pode servir esse papel sem nova infra-estrutura.

A análise da wave `W-REVIEW-03-D4-RAW-URL-PUBLICATION-DECISION-PREP` classificou a reutilização como `RAW_REVIEW_URL_ACCEPTABLE_WITH_EXPLICIT_DECISION` e identificou que a escolha se aplica tanto a D4-A (novo Run) como a D4-B (publicação inline). Esta DL-129 formaliza o **prerequisite comum** a ambas.

#### D1 — Semântica dupla reconhecida.

`ReviewItemEntity.StreamUrl` permanece semanticamente **evidência observada de ingestion** (W-REVIEW-01; doc XML em `CatalogEntities.cs:418`) **e** passa também a ser **fonte funcional interna de publicação** quando uma Review aprovada necessitar de regenerar a playlist. Esta duplicidade é deliberada e **não** é uma consequência acidental da implementação.

#### D2 — Restrição de destino (RAW sink permitido).

A URL RAW extraída de `ReviewItem.StreamUrl` **só** pode ser entregue a:

- `M3uStream.Url` construído por um pipeline de publicação; **e**
- `playlist.m3u` (e equivalentes `output/telegram_playlist_<ts>.m3u`).

**Não** pode ser entregue a (lista exaustiva):
- Review API (`GET /api/review`, `GET /api/review?id`, `POST /api/review/{resolve,ignore,reopen}`);
- DTOs de Review (`ReviewSummaryJson`, `ReviewDetailJson`, response de resolve);
- Logs de qualquer tipo (`Console.WriteLine`, structured logging, exception messages);
- Audit (`AuditService.RecordAsync` — `BeforeJson`, `AfterJson`, `Detail`);
- `RunReport` (`telegram_run_report.json`);
- JSON de relatório/diagnóstico (`SaveToJsonReport`);
- `import_history.json`;
- Documentação, fixtures versionadas, dumps de DB.

#### D3 — Compatibilidade com DL-020.

DL-020 ("Secrets nunca entram em diagnóstico. Sanitização é obrigatória e centralizada") **continua intacta**. A URL RAW em `ReviewItem.StreamUrl` é excepção já aceite pela doc XML (`CatalogEntities.cs:418`) e **não** é nova autorização genérica para expor credenciais. A fronteira DL-020 = "diagnóstico / API / log / audit / report / documentação / artefactos versionados = proibido" **mantém-se**. O único destino deliberadamente RAW continua a ser a playlist funcional que por definição do projecto requer URL real.

#### D4 — Compatibilidade com DL-112.

DL-112 ("Secrets são armazenados por referência/secret store ou mecanismo equivalente isolado do modelo funcional") continua **OPEN/PARTIAL** (`46-REQUIREMENT-TRACEABILITY.md` linha 157). `ProviderAccountEntity.CredentialsReference` permanece write-only; **não** existe `SecretStore` implementado. Esta DL-129 **não** substitui nem elimina a futura necessidade de um mecanismo de secrets adequado; **não** afirma que o uso de RAW em `ReviewItem.StreamUrl` é o modelo final de secrets — é dívida técnica conhecida, registada aqui para tracking.

#### D5 — Compatibilidade com DL-108, DL-115, DL-116, DL-124, DL-125, DL-126, DL-128.

- **DL-108** (`sfp1` fingerprint): compatível. `StreamFingerprint` continua a ser a identidade operacional de canal; `ReviewItem.StreamFingerprint`/`StreamFingerprintVersion` persistidos são consistentes.
- **DL-115** (transaction boundary): compatível. A leitura de `ReviewItem.StreamUrl` ocorre após `ApplyReviewApprovalAsync` commit; **não** mantém transacção DB aberta.
- **DL-116** (external resource reconciliation): compatível. Não toca reconciliação.
- **DL-124** (`RunCoordinator.RunId` autoritativo): compatível. Não introduz RunId para esta decisão. Se D4-A for escolhido, RunId é fabricável via `RunCoordinator`.
- **DL-125** (`discovery_candidates.RunId`): compatível. Não toca discovery.
- **DL-126** (Recognition Policy snapshot): compatível. Não toca snapshot. Se D4-A for escolhido, snapshot é criado antes do pipeline execute (DL-126 invariant).
- **DL-128** (Dispatcharr gate dedicado): compatível. Não reabre DL-128; não toca Dispatcharr gate.

#### D6 — Consequências arquitecturais registadas.

A utilização de `ReviewItem.StreamUrl` como fonte funcional de publicação cria uma **dependência** da publicação relativamente à **longevidade** da `ReviewItemEntity` row. Concretamente:

- **Limpeza futura de Review (`review_items`)**: se um futuro mecanismo purgar `ReviewItem` (DL-113 actualmente não cobre este caso; ver §OPEN), o caminho de publicação perde a fonte RAW. Mitigação eventual: **NÃO** aplicável nesta wave; o operador/equipa decide se aceita o risco ou constrói `ProviderUrlResolver` antes do purge.
- **Mudança futura do formato de Review evidence**: se `StreamUrl` deixar de ser persistido (substituído por fingerprint-only), o caminho de publicação precisa de migração.
- **Substituição futura de Xtream por outro provider**: o caminho assume URL-com-credenciais no formato Xtream; outro formato pode exigir novas regras de sanitização.
- **Export/arquivo futuro de `review_items`**: dumps de DB contêm RAW. Risco operacional **existente** (não criado por esta DL); AGENTS.md §6 e §8 cobrem parcialmente.

#### D7 — Retenção.

**Não existe actualmente** política de retenção/purge para `review_items`. Consequência: `ReviewItem.StreamUrl` RAW pode permanecer no SQLite **indefinidamente**. Esta persistência RAW não-temporária é fronteira de segurança **existente** (anterior a DL-129), **não** criada por esta decisão. DL-113 ("Retenção é configuração explícita") continua a aplicar-se no sentido de que qualquer purge futuro **deve** ser configuração explícita; esta DL-129 **não** autoriza nem proíbe purge.

#### D8 — Acoplamento ao lifecycle de Review.

A publicação fica **acoplada** à existência da Review row. **Não** é criado mecanismo de redundância. **Não** é criada cópia RAW em `ChannelSource` (essa cópia sanitizada já existe). Wave futura que decida D4-A/D4-B deve considerar este acoplamento como entrada explícita no plano de implementação.

#### D9 — Pré-requisito ratificado.

DL-129 ratifica apenas o **pré-requisito comum** a D4-A e D4-B: a possibilidade técnica de construir `M3uStream.Url` RAW a partir de `ReviewItem.StreamUrl` após `ApplyReviewApprovalAsync`. **Não** escolhe D4-A nem D4-B. **Não** implementa nenhum caminho de publicação. **Não** cria nova infra-estrutura (`ProviderUrlResolver`, secret store, retention policy). **Não** altera API contracts.

#### Non-goals (registados explicitamente).

- **Não** resolve SecretStore (DL-112 continua OPEN).
- **Não** resolve retention/purge.
- **Não** escolhe D4-A vs D4-B.
- **Não** implementa Review→Output.
- **Não** altera schema/migrations.
- **Não** altera Review API contracts.
- **Não** adiciona `M3uStream.Url` a RunReport ou logs.
- **Não** adiciona RAW URL a audit ou DTOs.
- **Não** altera `CredentialSanitizer`.

#### Test gap registado (não implementado nesta wave).

Wave futura que implemente D4-A ou D4-B deve incluir teste que valide, em conjunto:

- `ReviewItem.StreamUrl` RAW com credenciais Xtream (`http://user:secret@host/live.m3u8`);
- após `POST /api/review/resolve` (AddAlias/CreateChannel/Exclude) e pipeline de publicação;
- `playlist.m3u` contém URL RAW funcional (esperado);
- `report_<ts>.json` contém URL sanitizada (esperado);
- `RunReport` (`telegram_run_report.json`) não contém URL RAW (esperado);
- `import_history.json` não contém URL field (esperado);
- Logs verbose (se activos) sanitizados (esperado);
- Audit não contém `r.StreamUrl` (esperado);
- Review API (`GET /api/review`, `GET /api/review?id`) não contém `r.StreamUrl` (esperado).

O test `Responses_do_not_expose_credentials` (`WaveW55ReviewApiTests.cs:792-808`) cobre parcialmente o caso via `normalizedIdentity`; deve ser estendido para cobrir o path `streamUrl`-bearing quando Review→Output for implementado.

### DL-130 — Review Approval / Publication Pending Lifecycle (ratificado)

Ratifica o lifecycle em que uma `Review Approval` actualiza o catálogo mas **não** publica directamente `playlist.m3u` e **não** invoca directamente Dispatcharr. A publicação funcional continua a ser responsabilidade do lifecycle normal de Run/ingestion (DL-128). O estado `publication_pending` é **derivado** de cursors existentes — não é uma propriedade persistida de `ReviewItemEntity`. Não há schema migration. Não há reconstrução autónoma de URLs Xtream (per `W-REVIEW-03-D4-PROVIDER-MODEL-RECON`).

#### Contexto

O lifecycle actual (per `W-REVIEW-03-D4-PENDING-STATE-MODEL` e `W-REVIEW-03-D4-CATALOG-PUBLICATION-CURSOR-RECON`) já é estruturalmente compatível com este modelo: `POST /api/review/resolve` actualiza o catálogo (atomic, DL-127), emite audit, e termina. A próxima Telegram cycle / full Run republica `playlist.m3u` via `RunPublicationService.PublishAsync` (atomic write, DL-019) e invoca Dispatcharr via gate dedicado (DL-128). A única questão técnica em aberto era a representação do estado de "publicação pendente" entre estas duas fases. Esta DL-130 ratifica que essa representação é um **cursor derivado** de timestamps já existentes em `LiveRunEntity` e nas entidades de catálogo — não uma nova coluna.

#### D1 — Review Approval não publica `playlist.m3u` directamente.

`POST /api/review/resolve` actualiza o catálogo (atomic, DL-127), emite audit (`catalog.review.resolve` + `catalog.review.approval.materialize_*`), e responde 200 OK. **Não** invoca `SaveToM3uPlaylistAtomic`. **Não** cria `LiveRunEntity`. O `playlist.m3u` mantém-se como o último output funcional publicado pelo último Run bem sucedido.

#### D2 — Review Approval não chama Dispatcharr.

Por consequência de D1, e por respeito ao gate dedicado (DL-128 D8), o handler de resolve **não** invoca `DispatcharrSyncCoordinator.RunAsync` nem adquire o `DispatcharrConcurrencyGate`. Dispatcharr é actualizado exclusivamente pelo Run normal.

#### D3 — Publicação funcional é responsabilidade do lifecycle de Run.

`RunPublicationService.PublishAsync` permanece o único caminho para escrever `playlist.m3u` atomicamente (DL-019). `RunCoordinator.StartAsync` / `KickStartAsync` permanece o único caminho para criar um `LiveRunEntity` (DL-124). O fluxo `Telegram cycle → ingestion → validation → recognition → selection → publication → Dispatcharr` permanece inalterado.

#### D4 — Unidade de publicação é `playlist.m3u` completo.

Não existe unidade de publicação mais fina do que o `playlist.m3u` no sistema actual. **Não** há correspondência per-Review, per-ChannelSource, ou per-Source entre uma Review resolvida e o conteúdo de uma escrita específica de `playlist.m3u`. Esta conclusão está documentada em `W-REVIEW-03-D4-PENDING-STATE-MODEL` e é FACTUALMENTE provada pelo código: o Run lê o estado wholesale do catálogo (`RunPublicationService.PublishAsync`), não enumera `ReviewItemEntity` rows; `RunReport` e `ImportHistoryEntry` **não** carregam IDs de ReviewItem; `ReviewItem.RunId` é uma string nullable sem FK para `LiveRunEntity.RunId`.

#### D5 — `publication_pending` é estado DERIVADO, não persistido.

Definido por:

```
catalogChangedAtUtc      = MAX(UpdatedAtUtc) sobre entidades relevantes do catálogo
lastSuccessfulPublicationAtUtc = MAX(LiveRunEntity.FinishedAtUtc)
                               WHERE TerminalStatus = Completed
                               AND Mode IN ('Telegram', 'TelegramMaintain')
publicationPending       = catalogChangedAtUtc > lastSuccessfulPublicationAtUtc
pendingReviewsCount      = COUNT(*) FROM review_items
                            WHERE State = Resolved
                            AND UpdatedAtUtc > lastSuccessfulPublicationAtUtc
```

A lista exacta das entidades abrangidas por `catalogChangedAtUtc` deve ficar explicitamente documentada como contrato de implementação (ver Implementation Prerequisites). As entidades candidatas são: `CanonicalChannel`, `ChannelSource`, `Source`, `ReviewItem`, `ProviderAccount`, `ExternalIdentity`, `ChannelAlias` (cada uma com `UpdatedAtUtc` excepto onde aplicável).

#### D6 — Não será criado `ReviewItemEntity.PublishedAtUtc`.

Ratificado. O estado per-Review não pode ser provado pelo código sem diffing externo de `playlist.m3u`. Criar a coluna seria uma falsa promessa. **Esta decisão é vinculativa**: ondas futuras que requeiram um campo "published" devem usar queries de derivação, não armazenamento directo.

#### D7 — Não é necessário schema migration.

Os cursors D5 existem como queries em tabelas existentes (`LiveRunEntity`, `CanonicalChannel`, `ChannelSource`, `Source`, `ReviewItem`). A wave de implementação pode expor isto como endpoint `GET /api/publication/status` sem migrations.

#### D8 — Cursor actualiza por derivação.

O Run não precisa de fazer nada de especial para "limpar" o pending state. Quando um Run termina com `TerminalStatus = Completed`, a próxima query de `MAX(LiveRunEntity.FinishedAtUtc WHERE Completed)` reflecte automaticamente esse Run. Se `catalogChangedAtUtc ≤ lastSuccessfulPublicationAtUtc` após o Run, `publicationPending = false`.

#### D9 — Dispatcharr respeita DL-128 e `DispatcharrConcurrencyGate`.

`DispatcharrConcurrencyGate` continua independente do `RunCoordinator`. `DispatcharrSyncCoordinator.RunAsync` continua a ser invocado por `RunPublicationService.PublishAsync` após `SaveToM3uPlaylistAtomic`. A invocação respeita `cfg.DryRun` (DL-128 D6). O estado `publicationPending` é **independente** do sucesso de Dispatcharr: o cursor `lastSuccessfulPublicationAtUtc` representa "playlist.m3u escrito", não "Dispatcharr sincronizado".

#### D10 — Coalescing natural.

Vários `POST /api/review/resolve` consecutivos actualizam o catálogo atomicamente (DL-127); cada um emite audit; cada um actualiza `UpdatedAtUtc` da(s) row(s) afectada(s). Um único Run subsequente lê o estado wholesale e publica uma única vez. Não há locking necessário entre Reviews; o `RunCoordinator` CAS gate previne Runs concorrentes. A coalescing é **emergente** do design.

#### D11 — Observabilidade de falhas preservada.

Falhas de `ApplyReviewApprovalAsync` são capturadas pelo handler resolve (responde 4xx/5xx; **não** persiste partial state — atomic transaction, DL-127). Falhas de Run são capturadas pelo `RunCoordinator` (`TerminalStatus = Failed`, `LastMessage` populado, `LiveRunEntity` persistido) e pelo `RecoverInterruptedRunsAsync`. O pending state **não** deve ser apagado artificialmente em caso de falha — isso iria obscurecer o real estado do sistema.

#### D12 — O modelo afirma apenas "catalog reflects no último output" — não "Review publicada".

FACTUALMENTE, uma Review resolvida NÃO implica que a stream correspondente esteja em `playlist.m3u`. A inclusão depende de: `ChannelSource.IsEnabled`, `SourceSelectionStage`, country gate, `MaxSourcesPerChannel`, e o Run ter lido a row actualizada. O modelo afirma apenas que **se** o cursor `catalogChangedAtUtc` avançou após a última publicação, **existe pelo menos uma alteração de catálogo não reflectida**. Não afirma qual nem quantas.

#### D13 — Sem reconstrução autónoma de URLs Xtream.

Esta DL-130 **não** fecha DL-112 (SecretStore). **Não** introduz `ProviderUrlResolver`. Para Xtream, a fonte funcional RAW continua a ser:
- `ReviewItem.StreamUrl` (DL-129) para streams com Review resolvida.
- `playlist.m3u` (DL-019) como cache da última publicação.

A eventual migração para Model A (catalog-derived) requer a wave `W-REVIEW-03-D4-RAW-SOURCE-ARCHITECTURE-RECON`/`W-REVIEW-03-D4-PROVIDER-MODEL-RECON` (schema + SecretStore + ProviderUrlResolver). **Fora do scope desta DL-130.**

#### D14 — DL-020 preservado.

Nenhuma nova exposição RAW é introduzida. Os sinks RAW permitidos (DL-129 D2) permanecem: `M3uStream.Url` + `playlist.m3u` + `output/telegram_playlist_<ts>.m3u`. O pending state é derivado de timestamps sanitizados (`UpdatedAtUtc`, `FinishedAtUtc`) — não transporta URLs RAW.

#### D15 — Persistência através de restart.

`publicationPending` é derivado de colunas `UpdatedAtUtc` / `FinishedAtUtc` que sobrevivem restart do processo. Não há estado em memória a preservar. O `RunCoordinator.RecoverInterruptedRunsAsync` cobre `LiveRunEntity` rows interrompidas; o cursor `lastSuccessfulPublicationAtUtc` é correcto após recovery (não inclui runs `Failed`).

#### Compatibilidade com DLs existentes

- **DL-019** (Output atómico): COMPLIANT — `playlist.m3u` continua a ser atomicamente substituído por Run.
- **DL-020** (Secrets nunca em diagnóstico): COMPLIANT — D14.
- **DL-112** (Secrets por referência): não afectada — esta DL-130 **não fecha** DL-112.
- **DL-124** (RunId autoritativo): COMPLIANT — RunCoordinator CAS gate inalterado.
- **DL-125** (`discovery_candidates.RunId`): COMPLIANT — não introduzimos candidates para Review.
- **DL-126** (Recognition Policy snapshot): COMPLIANT — Run normal continua a criar snapshot antes de pipeline execute.
- **DL-127** (ChannelSource materialization atomicity): COMPLIANT — Review resolve continua atómico.
- **DL-128** (Dispatcharr gate dedicado): COMPLIANT — D9.
- **DL-129** (`ReviewItem.StreamUrl` como source): COMPLIANT — esta DL-130 não contradiz DL-129; ratifica `ReviewItem.StreamUrl` como RAW source para streams com Review, dentro do lifecycle do Run.

#### Implementation Prerequisites (para wave futura, NÃO implementada por esta DL)

A próxima wave de implementação deve, no mínimo:

1. Definir a lista exacta das entidades incluídas em `catalogChangedAtUtc`. Sugestão inicial (a confirmar): `CanonicalChannel`, `ChannelSource`, `Source`, `ReviewItem`, `ProviderAccount`, `ExternalIdentity`, `ChannelAlias`. Excluir `ChannelSourceObservation` (timestamp dedicado `ObservedAtUtc`; sem relevance directa para `playlist.m3u`).
2. Definir o filtro exacto dos Run Modes elegíveis para `lastSuccessfulPublicationAtUtc`. Sugestão: `Mode IN ('Telegram', 'TelegramMaintain')`; `TerminalStatus = Completed`. Manutenção só conta se o seu output for canónico (verificar se há política de "maintenance não substitui" — não há pelo código actual).
3. Tratar ausência de qualquer publicação anterior: `lastSuccessfulPublicationAtUtc = NULL` deve retornar `publicationPending = NULL` (estado desconhecido) **ou** `true` (conservador). Decidir.
4. Endpoint `GET /api/publication/status` retornando: `{ pendingReviewsCount: int?, catalogChangedAtUtc: DateTime?, lastSuccessfulPublicationAtUtc: DateTime?, publicationPending: bool? }`. O booleano é `null` quando falta baseline.
5. Dashboard tile opcional: `Publication Status: ⏳ pending / ✅ up-to-date / ❔ unknown`.
6. `pendingReviewsCount` é métrica **auxiliar**, não definidora do estado. O estado é o cursor.
7. Testes unitários das queries D5 (SQLite).
8. Testes de cenários: Review Approve + Run success; Review + Run fail; 3 Reviews + 1 Run; Restart mid-Run (recovery).
9. NÃO criar migrations.
10. NÃO criar `ReviewItemEntity.PublishedAtUtc` (D6 vinculativa).
11. NÃO criar novo `LiveRunMode` (DL-124 aplicável).
12. NÃO criar `ProviderUrlResolver`, `SecretStore` (DL-112 inalterado).

#### Non-goals (registados explicitamente)

- **Não** fecha DL-112 (SecretStore continua OPEN/PARTIAL).
- **Não** resolve retention/purge (DL-113 aplicável).
- **Não** implementa `ProviderUrlResolver`.
- **Não** cria `ReviewItemEntity.PublishedAtUtc`.
- **Não** implementa endpoint `GET /api/publication/status` (futuro).
- **Não** cria migrations.
- **Não** altera schema de qualquer entidade.
- **Não** introduz RAW URL em nova superfície.
- **Não** fecha D4-A vs D4-B (não aplicável — esta DL torna essa escolha desnecessária no curto prazo).
- **Não** migra para Model A (catalog-derived output).
- **Não** migra para Model B (immediate publication via playlist cache).
- **Não** altera `RunCoordinator`, `DispatcharrConcurrencyGate`, `RunPublicationService`, `M3uStream`, `PlaylistManagerService`.
- **Não** altera contract HTTP de `/api/review/resolve`.

#### Test gap (não implementado por esta DL)

A próxima wave de implementação deve incluir teste que valide:

- Review Approve → `publicationPending = true` (catalog changed, no Run since).
- Run success → `publicationPending = false` (catalog covered).
- Review Approve → Run fail → `publicationPending = true` (Run failed; catalog not covered).
- 3 Reviews → 1 Run → `publicationPending = false` (coalesced).
- Restart process → `publicationPending` preserved (derived from DB).
- Exclude/Ignore → `publicationPending` semantics (open: see D11).

#### Decisões arquitecturais abertas (registadas, não resolvidas por esta DL)

1. Se `lastSuccessfulPublicationAtUtc = NULL` deve retornar `publicationPending = NULL` (unknown) ou `true` (conservative). Decidir na implementação.
2. Se Dispatcharr success deve ser tracked separadamente (cursor paralelo `lastDispatcharrSuccessAtUtc`).
3. Se admin-`IsEnabled` operations devem contar para `catalogChangedAtUtc` (afectam `playlist.m3u` mas podem não tocar `UpdatedAtUtc`).
4. Se `ChannelSourceObservation` insertions devem contar (timestamp dedicado `ObservedAtUtc`).
5. Se Exclude/Ignore devem ser contados (afectam ReviewItem mas não playlist content).
6. Se manual M3U uploads (`ProviderType.Manual`) devem ser contados.

## C. Regra

Uma implementação que contradiga DL-001..130 está incorrecta relativamente à BÍBLIA.

Uma alteração destas decisões exige alteração explícita da BÍBLIA, testes e documentação derivada.
