# BIBLE IMPLEMENTABILITY GAP MANIFEST 1.0

> **Modo:** BIBLE AUDIT — read-only sobre a BÍBLIA 1.2.
> **Data:** 2026-09-19
> **Estado:** `AUDIT COMPLETE` + `GAP MANIFEST COMPLETE` + `DECISIONS REQUIRED IDENTIFIED`.
> **Revisão:** 1.0-reconciliado — auditoria interna de consistência do próprio manifesto (contagens, IDs, agrupamento, separação funcional/documental). Nenhuma decisão nova foi introduzida.
> **Não** é uma proposta de BÍBLIA 1.3. Nenhum documento normativo, ADR, código ou teste foi alterado.
> Todas as referências `file:line` foram verificadas nos ficheiros reais do repositório.

---

## 1. Scope

Este manifesto determina, para cada alegação de lacuna da BÍBLIA 1.2 (incluindo as 23 áreas do relatório anterior e os pontos de governação), se:

1. a regra já está fechada;
2. falta apenas um parâmetro operacional;
3. falta uma decisão semântica;
4. falta uma decisão de modelo;
5. falta um contrato;
6. falta um lifecycle;
7. falta uma autoridade;
8. falta rastreabilidade.

Critério central:

> Se apenas a BÍBLIA fosse entregue a dois programadores experientes, poderiam implementar o comportamento de forma materialmente diferente?

Material = altera estado persistido, entidade criada/removida, output, playlist, stream selecionada, ordem, side effect externo, Dispatcharr, lifecycle, retry, tratamento de erro, segurança, autorização ou histórico/auditoria. Diferenças puramente internas não contam.

**Fora de scope:** propor valores, escolher interpretações, criar ADRs automaticamente, criar waves de implementação, alterar a BÍBLIA.

---

## 2. Authority and source inventory

Hierarquia aplicada: `00-BIBLE.md:17-28` (proprietário > BÍBLIA > ADRs > documentação derivada > código > testes > comportamento > histórico).

### 2.1 Documentos normativos existentes (confirmados em disco)

`docs/Reestructure/`: `00-BIBLE.md`, `01-PRODUCT.md`, `02-DOMAIN.md`, `03-DISCOVERY.md`, `04-PLAYLIST-STREAM.md`, `05-CATALOGUE.md`, `06-COUNTRY-MEDIA.md`, `07-SOURCES.md`, `08-VALIDATION.md`, `09-SELECTION.md`, `10-ORDERING.md`, `11-OUTPUT.md`, `12-DISPATCHARR.md`, `13-RUNS.md`, `14-CONFIGURATION.md`, `15-APPLICATION.md`, `16-PERSISTENCE.md`, `17-SECURITY.md`, `18-OBSERVABILITY.md`, `19-FAILURE-MODEL.md`, `20-OPERATIONS.md`, `21-TESTING.md`, `22-API-CONTRACTS.md`, `23-DATA-CONTRACTS.md`, `24-DECISIONS.md`, `25-REQUIREMENTS.md`, `26-RECONSTRUCTION.md`, `27-GLOSSARY.md`, `28-TRUTH-AND-TRACEABILITY.md`, `29-AGENT-RULES.md`, `30-REVIEW-CHECKLIST.md`, `31-DECISION-LOCK.md`, `32-DOMAIN-SCHEMA.md`, `33-STATE-MACHINES.md`, `34-PIPELINE-CONTRACTS.md`, `35-SECURITY-MODEL.md`, `36-RECONSTRUCTION-ORDER.md`, `37-ARCHITECTURE.md`, `38-POLICIES.md`, `39-CONFIG-SCHEMA.md`, `40-ENTITY-LIFECYCLE.md`, `41-API-INVENTORY.md`, `42-EXAMPLES.md`, `43-ANTI-PATTERNS.md`, `44-QUALITY-GATES.md`, `45-BIBLE-AUDIT.md`, `46-REQUIREMENT-TRACEABILITY.md`, `47-BIBLE-AUDIT-FINDINGS.md`, `README.md`.

### 2.2 ADRs existentes (evidência documental complementar)

`docs/adr/`: `ADR-0001-country-data-ownership.md` (Proposed), `ADR-0002-stream-fingerprint-canonicalization.md` (Proposed), `ADR-0003-source-selection-ranking.md` (**Accepted**), `ADR-0004-secret-storage-lifecycle.md` (Proposed), `ADR-0005-catalog-baseline-vs-runtime.md` (Proposed), `ADR-0006-sqlite-migration-rollback.md` (Proposed), `README.md`.

> **Regra desta revisão:** um ADR é **evidência documental** de uma decisão, nunca a decisão em si. O estado `Proposed` de um ADR não cria, por si só, um gap funcional. Onde existe uma decisão funcional em falta, ela é registada como `DG-*` independentemente do ADR; onde existe apenas um problema de registo documental, é registada como `TRACEABILITY_GAP` (root DG-21) e **não** entra na lista de decisões funcionais do proprietário.

### 2.3 Documentos derivados usados apenas como evidência marcada (não normativos)

`docs/PROJECT_STATUS.md`, `docs/project/waves/`, `docs/IMPLEMENTATION_ROADMAP.md`, `docs/architecture/`, `m3uCrawler/README.md`. Sempre que citados, estão explicitamente marcados como não normativos (`00-BIBLE.md:32,39`).

### 2.4 Nota de rastreabilidade do relatório anterior

O relatório anterior citou ficheiros inexistentes. Mapa de correção:

| Nome alegado (inexistente) | Documento real |
|---|---|
| `05-PARSING.md` / `??-NORMALIZATION.md` | `04-PLAYLIST-STREAM.md`; `34-PIPELINE-CONTRACTS.md` P2/P3 |
| `06-RECOGNITION.md` | `05-CATALOGUE.md` |
| `07-REVIEW.md` | `33-STATE-MACHINES.md`; `41-API-INVENTORY.md`; `05-CATALOGUE.md` |
| `09-COUNTRY-MEDIA.md` | `06-COUNTRY-MEDIA.md` |
| `10-VALIDATION.md` | `08-VALIDATION.md` |
| `11-ELIGIBILITY.md` | `08-VALIDATION.md` §3; `33-STATE-MACHINES.md` |
| `12-SELECTION.md` / `09-SELECTION.md` | `09-SELECTION.md` (existe; número correto) |
| `13-ORDERING.md` | `10-ORDERING.md` |
| `14-COMPOSITION.md` / `15-PUBLICATION.md` | `11-OUTPUT.md` |
| `16-DISPATCHARR.md` | `12-DISPATCHARR.md` |
| `17-RUN-SCHEDULER.md` | `13-RUNS.md`; `33-STATE-MACHINES.md` |
| `14-CONFIGURATION.md` (semântica) | `14-CONFIGURATION.md` (existe; número correto) |
| `18-SECURITY.md` | `17-SECURITY.md`; `35-SECURITY-MODEL.md` |
| `19-BACKUP.md` / `19-UPGRADE.md` | `20-OPERATIONS.md`; `16-PERSISTENCE.md` |
| `21-POLICY.md` / `24-DECISIONS.md` (semântica de decisões) | `38-POLICIES.md`; `24-DECISIONS.md` (existe) |

---

## 3. Audit methodology

1. **Inventário de fontes:** confirmação dos ficheiros reais antes de qualquer citação (§2).
2. **Auditoria por domínio:** 9 blocos (`G-01`..`G-22` do pedido, com `G-*` inexistentes no repositório tratados como etiquetas do pedido).
3. **Extração de regra existente:** citação verbatim com `file:line` para cada comportamento alegadamente em falta.
4. **Teste de implementabilidade:** construção de duas Implementações A/B deriváveis apenas da BÍBLIA; comparação pelo critério material.
5. **Classificação única:** cada item recebe exactamente um dos nove estados.
6. **Agrupamento por decisão raiz:** gaps causados pela mesma decisão foram consolidados (`DG-*`); gaps documentais foram separados para `DG-21`.
7. **Cenários end-to-end:** S1–S15, com procura da **primeira divergência**, referenciando o `DG-*` já existente (nunca criando outro).
8. **Não invenção:** sempre que a BÍBLIA não decide, é registado `DECISÃO NECESSÁRIA:` e não uma resposta.
9. **Reconciliação interna (1.0-reconciliado):** duplicado funcional removido (retry nova/mesma Run), itens documentais separados dos funcionais, contagens derivadas mecanicamente dos itens (§3.2).

### 3.1 Matriz principal (visão consolidada)

| ID | Área | Estado | Fonte | Regra existente | Ambiguidade | Divergência material | Tipo | Decisão necessária | Documentos afectados |
|---|---|---|---|---|---|---|---|---|---|
| DG-01a | DiscoveryCandidate model | ABERTO | `32-DOMAIN-SCHEMA.md:47-58`; `02-DOMAIN.md:39,42`; `34-PIPELINE-CONTRACTS.md:5-6` | Entidade com `Id`, `FirstSeen`, `LastSeen`, `RunId`; classificada em "Origem" | Persistente única vs registo por ocorrência/Run | SIM | MODEL_DECISION_GAP | O `DiscoveryCandidate` é entidade persistente única (Id estável reutilizado, `LastSeen` atualizado) ou um registo por ocorrência/Run? | `03`, `32`, `33`, `40` |
| DG-01b | DiscoveryCandidate identity/normalização | ABERTO | `32-DOMAIN-SCHEMA.md:52-54`; `03-DISCOVERY.md:16` | Campos `ExternalIdentity`, `NormalizedIdentity` | Algoritmo/âmbito de `NormalizedIdentity` inexistente; colisão `AccountId` vs `AccountKey` | SIM | MODEL_DECISION_GAP | Qual o algoritmo e âmbito de `NormalizedIdentity` e a sua relação com `ExternalIdentity` e `ProviderAccount.AccountKey`? | `03`, `32`, `02` |
| DG-01c | Candidate→ProviderAccount→Source | ABERTO | `32-DOMAIN-SCHEMA.md:25,37,45,51-54`; `03-DISCOVERY.md:16` | Candidate tem `ProviderId` mas não `ProviderAccountId`; Source tem `ProviderAccountId` | Sem mapeamento determinístico; sem fallback quando não há identidade estável | SIM | MODEL_DECISION_GAP | Qual campo do candidate corresponde a `ProviderAccount.AccountKey` e qual a regra quando o provider não expõe identidade estável? | `03`, `07`, `32`, `16` |
| DG-01d | Candidate vs Observation | ABERTO | `27-GLOSSARY.md:19,27`; `32-DOMAIN-SCHEMA.md:53,58`; `28-TRUTH-AND-TRACEABILITY.md:12` | Candidate tem `Evidence`/`RunId`; Observation é evidência técnica | Mesma descoberta pode ser modelada como candidato ou observação | SIM | MODEL_DECISION_GAP | A re-descoberta da mesma conta é atualização de candidate, nova Observation, ou ambos? | `02`, `03`, `27`, `32` |
| DG-01e | Autoridade do candidate | ABERTO | `28-TRUTH-AND-TRACEABILITY.md:5,8-15`; `03-DISCOVERY.md:24`; `02-DOMAIN.md:70` | Autoridade única exigida; `ProviderAccount` tem identidade funcional | `DiscoveryCandidate` não consta da lista de autoridades; conta pode ser candidate ou ProviderAccount | SIM | AUTHORITY_GAP | Quem é a autoridade única para a identidade da conta descoberta? | `03`, `28`, `02` |
| DG-02a | Candidate `Expired` | ABERTO | `33-STATE-MACHINES.md:21`; `32-DOMAIN-SCHEMA.md:56-57`; `40-ENTITY-LIFECYCLE.md:9` | Estado `Expired` enumerado; `FirstSeen`/`LastSeen` existem | Nenhum trigger/TTL definido | SIM | LIFECYCLE_GAP | Que condição exacta faz transitar `DiscoveryCandidate` para `Expired`? | `03`, `33`, `40`, `32` |
| DG-02b | Candidate re-descoberta/reactivação | ABERTO | `33-STATE-MACHINES.md:21`; `40-ENTITY-LIFECYCLE.md:5-10,18` | Só ReviewItem tem regra de reabertura | Sem reactivação/TTL; `40` nem sequer cobre o candidate | SIM | LIFECYCLE_GAP | `Expired`/`Rejected` podem regressar por re-descoberta, e segundo que condição? | `03`, `33`, `40` |
| DG-03a | M3U input mínimo | ABERTO → FECHADO (Round 1/Q4) | `04-PLAYLIST-STREAM.md:50-57`; `34-PIPELINE-CONTRACTS.md:17-19`; `23-DATA-CONTRACTS.md:29` | Input "não confiável"; limites exigidos | Sem gramática mínima, atributos, comentários, ordenação | SIM | SEMANTIC_DECISION_GAP | Qual o input mínimo aceite de uma M3U (header, pares, atributos obrigatórios, comentários, ordenação)? | `04`, `34`, `23` |
| DG-03b | Parsing parcial vs total | ABERTO → FECHADO (Round 1/Q4) | `19-FAILURE-MODEL.md:32-37`; `32-DOMAIN-SCHEMA.md:69` | Sucesso parcial deve ser explícito; `Playlist.Status` sem enum | Sem regra por entrada inválida; sem classificação do resultado | SIM | SEMANTIC_DECISION_GAP | Uma entrada inválida é descartada individualmente ou aborta a playlist; qual o `Status` (success/partial/failed)? | `04`, `34`, `32`, `19` |
| DG-04a | Aquisição: terminal vs retryable | ABERTO | `19-FAILURE-MODEL.md:22-28`; `08-VALIDATION.md:38-42`; `03-DISCOVERY.md:34` | Cada falha deve declarar `retryable?`, impacto no Run/item; distinguir transitória/persistente | Sem mapeamento 2xx/3xx/4xx/5xx/timeout/ligação/redirect/vazio/oversized/encoding | SIM | SEMANTIC_DECISION_GAP | Que condições são terminais vs retryable e qual o efeito persistido em candidate/Source/Playlist/Run? | `03`, `19`, `08`, `17`, `33` |
| DG-04b | Retry: parâmetros | ABERTO | `19-FAILURE-MODEL.md:41`; `31-DECISION-LOCK.md:109-112` | Retries limitados, com backoff e cancelamento | Sem tentativas máximas, curva, cap, jitter, âmbito | SIM | PARAMETER_GAP | Quais os valores de tentativas/backoff/jitter e o âmbito (por URL vs host)? (depende de DG-04a) | `19`, `39` |
| DG-04d | Limites de aquisição/parser | ABERTO | `03-DISCOVERY.md:32`; `04-PLAYLIST-STREAM.md:51`; `17-SECURITY.md:23,33-34` | Limites obrigatórios e configuráveis | Sem valores máximos | SIM | PARAMETER_GAP | Quais os valores de tamanho/entradas/comprimento/tempo? | `03`, `04`, `17`, `39` |
| DG-05a | SSRF: semântica | ABERTO | `17-SECURITY.md:19-25`; `35-SECURITY-MODEL.md:58`; `24-DECISIONS.md:13`; `47-BIBLE-AUDIT-FINDINGS.md:29` | Controlo de redirects, limites, timeouts, validação de protocolos; bloquear redes internas "quando o threat model exigir" | Threat model indefinido; DNS/IPv4/IPv6/loopback/link-local/rebinding não tratados | SIM | SEMANTIC_DECISION_GAP | Que classes de URL bloquear e como tratar DNS, redirects e rebinding? | `17`, `35`, `24`, `47` |
| DG-05b | SSRF: valores concretos | ABERTO | `17-SECURITY.md:21-24` | Bloqueio condicional a um threat model | Sem CIDRs / listas / limites concretos | SIM | PARAMETER_GAP | Quais os CIDRs/limites/timeouts concretos? (depende de DG-05a) | `17`, `35`, `39` |
| DG-06a | Normalização por campo | ABERTO | `04-PLAYLIST-STREAM.md:28-40`; `32-DOMAIN-SCHEMA.md:80,107`; `05-CATALOGUE.md:35` | Determinística; preserva original; campos normalizados existem | Sem transformações (URL/whitespace/casing/nome/tvg-id/group/logo/language/codec/quality) | SIM | SEMANTIC_DECISION_GAP | Quais as transformações normativas por campo? | `04`, `32`, `05`, `10`, `23` |
| DG-06b | Metadados ausentes/nullability | ABERTO | `32-DOMAIN-SCHEMA.md:3,82`; `04-PLAYLIST-STREAM.md:30-32` | Nullability não pode variar sem decisão; original preservado | Sem regra para campo ausente | SIM | SEMANTIC_DECISION_GAP | Campo ausente é null, string vazia, entrada rejeitada, ou transformação omitida, e qual a nullability? | `04`, `32` |
| DG-06c | Fingerprint/URL canonicalization | ABERTO | `04-PLAYLIST-STREAM.md:34-40`; `ADR-0002:3,48-74` | O algoritmo deve ser normativo e versionado | Algoritmo concreto não está inscrito na BÍBLIA (existe apenas em ADR `Proposed`, usado só como evidência) | SIM | MODEL_DECISION_GAP | Qual o algoritmo normativo de canonicalização de URL e de fingerprint de stream? | `04`, `32`, `24` |
| DG-07a | Fuzzy: mandatório/opcional e policy | ABERTO | `05-CATALOGUE.md:31-41`; `38-POLICIES.md:34-44`; `43-ANTI-PATTERNS.md:7` | Fuzzy só com policy de confiança e desempate; tipos de policy mínimos sem tipo de reconhecimento | Sem tipo/scope de policy; sem decidir se fuzzy é usado por defeito | SIM | SEMANTIC_DECISION_GAP | O fuzzy é mandatório ou opcional e qual o tipo/scope de policy que o autoriza? | `05`, `38`, `34` |
| DG-07b | Fuzzy: métrica/limiar | ABERTO | `05-CATALOGUE.md:38` | Exige "política de confiança e desempate" | Sem métrica, limiar, normalização de comparação, pesos, desempate | SIM | PARAMETER_GAP | Qual a métrica/limiar/normalização/desempate? (depende de DG-07a) | `05`, `38` |
| DG-07c | Fuzzy: resultado abaixo do limiar | ABERTO | `05-CATALOGUE.md:43-53`; `34-PIPELINE-CONTRACTS.md:38-43` | Empate → `AMBIGUOUS` → Review; `UNKNOWN` → Review | Abaixo do limiar não classificado (Unknown vs Ambiguous vs Excluded) | SIM | SEMANTIC_DECISION_GAP | O que produz um resultado abaixo do limiar? | `05`, `34` |
| DG-08a | Review: efeito de Resolve | ABERTO | `34-PIPELINE-CONTRACTS.md:45-48`; `05-CATALOGUE.md:65-67`; `32-DOMAIN-SCHEMA.md:136-148` | Saída "decisão administrativa + alteração explícita"; mudança deve ser declarada e auditada | Não enumera se cria CanonicalChannel/alias/external identity/ChannelSource ou liga Stream | SIM | SEMANTIC_DECISION_GAP | Que entidades/relações cria ou altera exactamente um Resolve? | `05`, `33`, `34`, `32` |
| DG-08b | Review: efeito de Ignore | ABERTO | `33-STATE-MACHINES.md:26`; `05-CATALOGUE.md:65-67` | `Open → Ignored` com razão administrativa | Sem efeito sobre Stream/ChannelSource/output | SIM | SEMANTIC_DECISION_GAP | Que efeito tem Ignore sobre Stream, ChannelSource e output? | `05`, `33`, `34`, `32` |
| DG-08c | Review: alias/identity boundary | ABERTO | `32-DOMAIN-SCHEMA.md:101-110`; `31-DECISION-LOCK.md:12-13` | Alias tem `NormalizedValue`; DL-002 proíbe criação implícita | Sem regra de normalização de alias; sem fronteira create vs link | SIM | SEMANTIC_DECISION_GAP | Resolve cria alias/identidade e qual a sua normalização? | `05`, `32`, `31` |
| DG-08d | Review: critério de reabertura | ABERTO | `31-DECISION-LOCK.md:114-116`; `33-STATE-MACHINES.md:29` | Reabertura decidida (operação auditada por evidência incompatível) | Critério de "materialmente incompatível" e deteção automática vs manual indefinidos | SIM | SEMANTIC_DECISION_GAP | O que conta como evidência materialmente incompatível e quem a deteta? | `05`, `31`, `33` |
| DG-08e | Review: contrato de API | ABERTO | `22-API-CONTRACTS.md:46-48`; `41-API-INVENTORY.md:44-49` | Endpoints sem ficha são `BIBLE_GAP`; família Review sem ficha | Ficha inexistente (efeitos funcionais tratados em DG-08a/b/c) | SIM | CONTRACT_GAP | Qual a ficha normativa da família Review? | `22`, `41`, `36` |
| DG-09a | Validação→Eligibility: classificação | ABERTO | `08-VALIDATION.md:34-42`; `19-FAILURE-MODEL.md:20-28`; `32-DOMAIN-SCHEMA.md:172,176` | Distinguir transitória/persistente/ausência/removido; `ErrorCode` sem enum | Sem mapeamento outcome→estado; sem mapeamento de erro concreto | SIM | SEMANTIC_DECISION_GAP | Como se classifica cada outcome (ex.: 403/404/contentor vazio) e como mapeia para Eligible/Ineligible/Unknown? | `08`, `19`, `32`, `33` |
| DG-09b | Ausência de observação→Eligibility | ABERTO | `31-DECISION-LOCK.md:78-79`; `40-ENTITY-LIFECYCLE.md:17-18` | Ausência não é remoção | Sem estado Eligibility resultante da ausência | SIM | SEMANTIC_DECISION_GAP | Qual o estado Eligibility quando não há observação? | `08`, `31`, `40`, `33` |
| DG-09c | "Último estado publicável"/fora da janela | ABERTO | `31-DECISION-LOCK.md:109-112` | Conserva último publicável com evidência recente na janela | Termo não definido; sem regra fora da janela | SIM | SEMANTIC_DECISION_GAP | O que é "último estado publicável" e que estado vale fora da janela? | `31`, `08`, `33` |
| DG-09d | Histerese: valores | ABERTO | `31-DECISION-LOCK.md:111-112` | Limiar/janela configuráveis como config operacional | Sem valores | SIM | PARAMETER_GAP | Quais o número de falhas e a janela? (depende de DG-09a) | `31`, `38`, `39` |
| DG-10a | Recuperação de `Ineligible` | ABERTO | `33-STATE-MACHINES.md:37-41`; `08-VALIDATION.md` | Estados `Eligible/Ineligible/Unknown`; transição por policy+evidence | Sem transição de recuperação | SIM | LIFECYCLE_GAP | `Ineligible → Eligible` é automática por nova evidência ou administrativa? | `08`, `33`, `40` |
| DG-10b | Lifecycle de Eligibility | ABERTO | `40-ENTITY-LIFECYCLE.md:5-10` | Distingue disabled/inactive/missing/expired/deleted | `40` não cobre Eligibility | SIM | LIFECYCLE_GAP | Como se comporta Eligibility ao longo do tempo (novas entidades, expiração, histórico)? | `40`, `33`, `32` |
| DG-11a | Output sem fonte elegível | ABERTO | `09-SELECTION.md:44-54`; `10-ORDERING.md:38-40`; `11-OUTPUT.md:7-13`; `ADR-0003:59-61` | Selection distingue "no eligible source"; posição sem canal não é preenchida | Sem regra de output para canal listado sem fonte elegível | SIM | SEMANTIC_DECISION_GAP | Canal listado sem fonte elegível: omitir, emitir sem stream, erro, ou outro? | `09`, `10`, `11` |
| DG-12a | Ordering: unicidade/duplicados | ABERTO | `16-PERSISTENCE.md:27,30`; `10-ORDERING.md:11-16`; `32-DOMAIN-SCHEMA.md:203-209` | Unicidade condicional "quando uma posição for única"; constraint adiada | Sem regra de `Position` única nem de canal duplicado na mesma lista | SIM | SEMANTIC_DECISION_GAP | `Position` é única por lista? É permitido o mesmo canal em duas posições da mesma lista? | `10`, `16`, `32` |
| DG-12b | Ordering: canal não listado | ABERTO | `10-ORDERING.md:38-40`; `11-OUTPUT.md` | Só regra inversa (posição sem canal) | Sem regra para canal elegível ausente de toda a lista | SIM | SEMANTIC_DECISION_GAP | Canal elegível ausente de qualquer OrderingList é excluído, incluído, ou erro? | `10`, `11`, `32` |
| DG-12c | Ordering: múltiplas listas/outputs | ABERTO | `10-ORDERING.md:9,16`; `11-OUTPUT.md:26`; `32-DOMAIN-SCHEMA.md:224-233` | Múltiplas listas/posições permitidas; output usa número da lista | Sem regra de quantas listas, autoridade, publicação múltipla | SIM | SEMANTIC_DECISION_GAP | Podem publicar-se várias listas no mesmo Run e um canal aparecer em vários outputs? | `10`, `11`, `32` |
| DG-12d | Radio/VOD sem política | ABERTO | `10-ORDERING.md:26-36`; `06-COUNTRY-MEDIA.md:29-35`; `31-DECISION-LOCK.md:42-43` | Radio "quando publicada"; VOD só se a política permitir | Sem default na ausência de política | SIM | SEMANTIC_DECISION_GAP | Sem política, Radio e VOD são publicados ou omitidos? | `06`, `10`, `11` |
| DG-13a | Output: contrato físico M3U | ABERTO | `11-OUTPUT.md:21-28`; `23-DATA-CONTRACTS.md:37-52`; `04-PLAYLIST-STREAM.md:13-26` | "respeitar sintaxe M3U"; "metadados normalizados"; determinismo byte-a-byte/semântico | Sem lista de atributos emitidos/encoding | SIM | CONTRACT_GAP | Qual o contrato físico do M3U (`#EXTINF`, `tvg-*`, `group-title`, número, encoding)? | `11`, `23`, `32` |
| DG-13b | GeneratedPlaylist: publication state | ABERTO | `32-DOMAIN-SCHEMA.md:224-233` | Campo `publication state` existe, sem enum | Enum indefinido | SIM | CONTRACT_GAP | Qual a enumeração de `publication state`? | `11`, `32`, `23` |
| DG-13c | Output: naming/Path | ABERTO | `32-DOMAIN-SCHEMA.md:230`; `11-OUTPUT.md` | Campo `Path/reference` existe | Sem convenção de nome/local | SIM | CONTRACT_GAP | Qual a convenção de nome e `Path/reference` do output? | `11`, `32`, `39` |
| DG-13d | Output parcial / replace falhado | ABERTO | `11-OUTPUT.md:32-34`; `31-DECISION-LOCK.md:63-64`; `16-PERSISTENCE.md:45-49` | Output atómico; parcial nunca válido | Sem regra para persistir parcial diagnóstica e para o output anterior em replace falhado | SIM | SEMANTIC_DECISION_GAP | Um parcial pode ser persistido como artifact não publicado; o que acontece ao output anterior? | `11`, `16`, `31` |
| DG-14a | RunId: formato/estabilidade | ABERTO | `13-RUNS.md:6`; `32-DOMAIN-SCHEMA.md:248`; `18-OBSERVABILITY.md:7-8` | Campo obrigatório; logs correlacionam | Sem formato/geração/estabilidade | SIM | CONTRACT_GAP | Qual o formato e âmbito de estabilidade do RunId? | `13`, `32`, `18` |
| DG-14b | Run: retry novo vs mesmo | ABERTO | `19-FAILURE-MODEL.md:39-43`; `33-STATE-MACHINES.md:59`; `40-ENTITY-LIFECYCLE.md:30`; `13-RUNS.md:32-34` | Run terminado imutável/não regressa a Running; idempotência exigida | Sem regra de identidade da retentativa (absorve o antigo `DG-04c`) | SIM | SEMANTIC_DECISION_GAP | Retry é nova Run (novo RunId) ou a mesma Run com contador? | `13`, `19`, `33`, `40` |
| DG-14c | Run manual com run ativo | ABERTO | `13-RUNS.md:42-49`; `31-DECISION-LOCK.md:127-128` | Uma execução activa por runtime; lock antes de efeitos | Sem política para trigger durante run activa | SIM | SEMANTIC_DECISION_GAP | Pedido manual durante run activa: rejeitar, enfileirar ou anexar? | `13`, `31`, `22`, `41` |
| DG-14d | Concorrência: serializar vs coordenar | ABERTO | `13-RUNS.md:44` | "serializadas ou coordenadas por lease/lock" | Duas estratégias permitidas sem regra decisória | SIM | SEMANTIC_DECISION_GAP | Qual é a política normativa por defeito: serializar ou coordenar? | `13`, `31` |
| DG-14e | Lock perdido: estado do Run | ABERTO | `31-DECISION-LOCK.md:128` | Lock perdido impede mutações e "força estado de Run apropriado" | Estado indefinido | SIM | SEMANTIC_DECISION_GAP | Que estado de Run corresponde ao "estado apropriado" quando o lock é perdido? | `13`, `31`, `33` |
| DG-14f | Run após restart / transições em falta | ABERTO | `19-FAILURE-MODEL.md:45-47`; `33-STATE-MACHINES.md:43-59` | Identificar Runs incompletos após restart | Sem estado para run interrompida; `Created→Failed/Cancelled` não enumerado | SIM | LIFECYCLE_GAP | Que estado assume a Run interrompida e são válidas `Created→Failed/Cancelled`? | `13`, `19`, `33` |
| DG-14g | Cancelamento vs efeitos externos | ABERTO | `13-RUNS.md:51-55`; `33-STATE-MACHINES.md:57` | Cancelamento propaga; não é sucesso parcial silencioso | Sem regra para efeitos externos já aplicados/publicação | SIM | SEMANTIC_DECISION_GAP | O que acontece a efeitos externos já aplicados e pode uma Run cancelada ter publicado output? | `13`, `12`, `11` |
| DG-14h | Timezone/DST | ABERTO | `16-PERSISTENCE.md:16`; `32-DOMAIN-SCHEMA.md:251-252`; `18-OBSERVABILITY.md:27` | Timestamps em UTC | Sem base temporal do agendamento nem DST | SIM | SEMANTIC_DECISION_GAP | O agendamento é avaliado em UTC ou timezone configurada, e como em DST? | `13`, `14`, `16` |
| DG-15a | Config: autoridade dual-class | ABERTO | `39-CONFIG-SCHEMA.md:47`; `14-CONFIGURATION.md:5-13` | BÍBLIA "deve declarar" qual classe é autoridade para propriedade dual | Declaração em falta | SIM | AUTHORITY_GAP | Para cada propriedade dual, qual classe é autoridade? | `14`, `39` |
| DG-15b | Operação apply/migração CLI/env | ABERTO | `14-CONFIGURATION.md:13`; `39-CONFIG-SCHEMA.md:43-45` | CLI/env só alteram estado funcional via operação explícita | Operação nunca definida; resultado por interface não especificado | SIM | SEMANTIC_DECISION_GAP | Qual é a operação explícita, interface, escopo e resultado? | `14`, `39` |
| DG-15c | Matriz de permissões/ACL | ABERTO | `35-SECURITY-MODEL.md:5-12`; `17-SECURITY.md:41`; `22-API-CONTRACTS.md:67-68`; `41-API-INVENTORY.md:86` | Auth/authz obrigatórias; papéis Administrator/Operator | Sem mapeamento operação↔papel↔interface; "gate único" indefinido | SIM | CONTRACT_GAP | Qual a ACL que mapeia cada operação a Administrator/Operator e o que é o "gate único"? | `35`, `17`, `22`, `41`, `14` |
| DG-16a | Policy: schema de campos | ABERTO | `38-POLICIES.md:28,8-14`; `32-DOMAIN-SCHEMA.md` | Merge campo-a-campo "quando o schema declarar campos independentes" | Schema de `Parameters` inexistente | SIM | MODEL_DECISION_GAP | Qual o schema normativo por policy e quais campos são independentes? | `38`, `32`, `39`, `09` |
| DG-16b | Policy: semântica de merge | ABERTO | `38-POLICIES.md:26-28`; `31-DECISION-LOCK.md:103-107` | Precedência `channel > group > global > default`; camada só existe se configurada | Sem regra para scalar/object/collection/null/absent/inherit/disabled | SIM | SEMANTIC_DECISION_GAP | Qual o resultado de merge para cada caso, incluindo `Enabled=false` e `null`? | `38`, `31`, `39` |
| DG-17a | Autoridade: classificação de país | ABERTO | `28-TRUTH-AND-TRACEABILITY.md:8-15`; `06-COUNTRY-MEDIA.md:5` | País não é autoridade de identidade; autoridade da classificação não declarada normativamente | Autoridade (localização/precedência/versionamento) não declarada (ADR-0001 é evidência, não a razão do gap) | SIM | AUTHORITY_GAP | Quem é a autoridade única dos dados de classificação de país? | `28`, `06` |
| DG-17b | Autoridade: classificação de media | ABERTO | `06-COUNTRY-MEDIA.md:27`; `02-DOMAIN.md:19` | Resultado deve ser explícito; pode ser derivado | Sem autoridade declarada | SIM | AUTHORITY_GAP | Quem é a autoridade da classificação de media (evidência/política)? | `28`, `06`, `02` |
| DG-17c | Autoridade: source priority | ABERTO | `09-SELECTION.md:5`; `31-DECISION-LOCK.md:36-37`; `38-POLICIES.md:34-44` | Priority é preferência; Selection escolhe stream | Entidade-valor de prioridade sem autoridade declarada | SIM | AUTHORITY_GAP | Qual a autoridade do valor de prioridade entre origens? | `28`, `09`, `38` |
| DG-17d | Autoridade: estado actual do Dispatcharr | ABERTO | `12-DISPATCHARR.md:26-27`; `31-DECISION-LOCK.md:45-46,148-149` | Estado remoto não altera catálogo; sistema reconcilia | Sem declaração de posse/persistência do estado observado | SIM | AUTHORITY_GAP | Onde é possuído/persistido o estado remoto observado? | `12`, `28`, `16` |
| DG-17e | Autoridade: Policy/Run fora de 28 | ABERTO | `28-TRUTH-AND-TRACEABILITY.md:8-15`; `38-POLICIES.md:32`; `13-RUNS.md:5-14` | Snapshot de policies; Run com campos definidos | Policy e Run ausentes da lista de autoridades | SIM | AUTHORITY_GAP | Quem é a autoridade de cada policy e do Run? | `28`, `38`, `13`, `39` |
| DG-18a | Downgrade vs rollback | ABERTO | `20-OPERATIONS.md:20-26` | Upgrade preserva dados; rollback por imagem+restore | Sem procedimento normativo de downgrade | SIM | SEMANTIC_DECISION_GAP | Qual o procedimento de downgrade e como difere de rollback? | `20`, `16` |
| DG-18b | Secrets em backup/restore | ABERTO | `20-OPERATIONS.md:30-38`; `17-SECURITY.md:59`; `14-CONFIGURATION.md:32-42` | Backups podem conter secrets e são confidenciais | Sem regra de inclusão/exclusão/re-provisão/re-encriptação | SIM | SEMANTIC_DECISION_GAP | Que secrets entram no backup e como são restaurados/protegidos? | `20`, `17`, `14` |
| DG-18c | Restore fresh vs upgrade; estado parcial | ABERTO | `20-OPERATIONS.md:42-45`; `16-PERSISTENCE.md:44-48` | Pipeline `backup → restore → migration → health → functional test` | Sem diferenciação fresh/upgrade; sem estado válido de restore interrompido | SIM | SEMANTIC_DECISION_GAP | Restore difere em fresh install e upgrade; um restore parcial é estado válido? | `20`, `16`, `26` |
| DG-18d | Baseline upgrade vs schema migration | ABERTO | `31-DECISION-LOCK.md:121-122`; `16-PERSISTENCE.md:42-49` | Baseline upgrade não apaga alterações locais; migrations deterministas | Sem ordenação/procedimento combinado | SIM | SEMANTIC_DECISION_GAP | Como se ordenam baseline upgrade e schema migration? | `16`, `26`, `36` |
| DG-18f | Retenção: valores | ABERTO | `31-DECISION-LOCK.md:139-140`; `40-ENTITY-LIFECYCLE.md:32-34` | Retenção é configuração explícita; nunca eliminação silenciosa | Sem valores de retenção | SIM | PARAMETER_GAP | Quais os valores de retenção por entidade (Observation/Run/audit/artifacts)? | `31`, `40`, `16`, `39` |
| DG-19a | Fichas de API (13 famílias) | ABERTO | `41-API-INVENTORY.md:3,5-84`; `22-API-CONTRACTS.md:28-48` | Cada operação deve ter ficha completa; ausência é `BIBLE_GAP` | 13 famílias sem ficha | SIM | CONTRACT_GAP | Quais as fichas completas de cada família de API? | `22`, `41`, `23` |
| DG-19c | Country: limites/concorrência | ABERTO | `22-API-CONTRACTS.md:42-43,109-128` | Conflito de versão `409`; limites "quando aplicável" | Sem rate limits nem contrato explícito de concorrência | SIM | CONTRACT_GAP | Que limites e concorrência se aplicam aos endpoints Country? | `22`, `41` |
| DG-20a | Fronteira "detalhe físico" vs schema normativo | ABERTO | `32-DOMAIN-SCHEMA.md:3`; `16-PERSISTENCE.md:30` | Não alterar sem decisão: significado/tipo/nullability/cardinalidade/unicidade/referências/lifecycle/invariantes | "Detalhe físico relevante" não delimitado | SIM | SEMANTIC_DECISION_GAP | O que distingue detalhe físico aceitável de mudança que exige secção normativa? | `32`, `16` |
| DG-20b | Testes vs decisões não normativas | ABERTO | `00-BIBLE.md:24`; `46-REQUIREMENT-TRACEABILITY.md:32-34`; `43-ANTI-PATTERNS.md:19` | Teste demonstra comportamento; não marcar COMPLIANT por nome | Sem regra sobre testes que fixem valores não normativos | SIM | SEMANTIC_DECISION_GAP | Pode um teste fixar comportamento ausente da BÍBLIA e fechar um `BIBLE_GAP`? | `46`, `43`, `21`, `00` |
| DG-21a | Registo da decisão de histerese | ABERTO | `08-VALIDATION.md:44-46`; `24-DECISIONS.md:10` | O comportamento exacto deve ser versionado; decisão funcional em DG-09 | Decisão funcional ainda não tomada e sem registo normativo | NÃO (documental) | TRACEABILITY_GAP | Onde e quando fica registada a decisão funcional de histerese (DG-09)? | `08`, `24` |
| DG-21b | Registo do mecanismo de scheduler locking | ABERTO | `24-DECISIONS.md:14`; `47-BIBLE-AUDIT-FINDINGS.md:30` | Mecanismo deliberadamente aberto; decisão funcional em DG-14 | Mecanismo sem registo normativo | NÃO (documental) | TRACEABILITY_GAP | Onde e quando fica registado o mecanismo de lock (DG-14)? | `24`, `47`, `13` |
| DG-21c | Decisões registadas apenas em ADRs `Proposed` | ABERTO | `ADR-0001:3`; `ADR-0005`; `ADR-0006:3`; `docs/adr/README.md:44` | ADR `Proposed` não é normativo | Conteúdo de país/migração/baseline não inscrito na BÍBLIA | NÃO (documental) | TRACEABILITY_GAP | Como é inscrito na BÍBLIA o conteúdo das decisões funcionais de DG-17/DG-18? | `docs/adr/`, `20`, `16`, `06` |
| DG-21d | Família Country ausente do inventário | ABERTO | `41-API-INVENTORY.md:5-84`; `22-API-CONTRACTS.md:64-131` | 4 fichas Country existem no doc normativo de contratos | Inventário não enumera a família Country | NÃO (documental) | TRACEABILITY_GAP | A família Country deve ser adicionada ao inventário de API? | `22`, `41` |
| DG-21e | Matriz de rastreabilidade vazia | ABERTO | `46-REQUIREMENT-TRACEABILITY.md:23-26,40-42`; `28-TRUTH-AND-TRACEABILITY.md:27` | Matriz deve ligar requisito→BÍBLIA→contrato→teste | Todas as linhas `TBD`/`UNMAPPED` | NÃO (documental) | TRACEABILITY_GAP | Como é preenchida a matriz com IDs únicos por requisito? | `46`, `28`, `45` |
| DG-21f | Re-auditoria de ADRs `Proposed` | ABERTO | `45-BIBLE-AUDIT.md:55-58`; `docs/adr/README.md:40-44` | Critério de integridade de delegação | ADR-0002/0004/0005/0006 sem re-auditoria registada | NÃO (documental) | TRACEABILITY_GAP | Quando é completada a re-auditoria dos ADRs `Proposed`? | `45`, `docs/adr/` |

> **Nota de reconciliação (W3, 2026-09-19).** As linhas `DG-03a`/`DG-03b` acima mantêm o seu conteúdo histórico e passam a estar anotadas como **fechadas em Round 1/Q4** (ver Anexo D, Q4). O fecho foi depois implementado e testado na Wave W3 (ver Anexo I). A matriz original não foi reescrita: as contagens de §3.2 reflectem o estado da auditoria inicial e são preservadas como histórico; com este fecho, os itens `DG-*` abertos passam de 73 para 71.

### 3.2 Reconciliação de contagens

#### (a) Por classificação

| Classificação | IDs incluídos | Quantidade |
|---|---|---:|
| CLOSED | F-04, F-15, F-16, F-17, F-18, F-19, F-20, F-21, F-22, F-23 | 10 |
| FALSE_GAP | F-01, F-02, F-03, F-05, F-06, F-07, F-08, F-09, F-10, F-11, F-12, F-13, F-14 | 13 |
| PARAMETER_GAP | DG-04b, DG-04d, DG-05b, DG-07b, DG-09d, DG-18f | 6 |
| SEMANTIC_DECISION_GAP | DG-03a, DG-03b, DG-04a, DG-05a, DG-06a, DG-06b, DG-07a, DG-07c, DG-08a, DG-08b, DG-08c, DG-08d, DG-09a, DG-09b, DG-09c, DG-11a, DG-12a, DG-12b, DG-12c, DG-12d, DG-13d, DG-14b, DG-14c, DG-14d, DG-14e, DG-14g, DG-14h, DG-15b, DG-16b, DG-18a, DG-18b, DG-18c, DG-18d, DG-20a, DG-20b | 35 |
| MODEL_DECISION_GAP | DG-01a, DG-01b, DG-01c, DG-01d, DG-06c, DG-16a | 6 |
| CONTRACT_GAP | DG-08e, DG-13a, DG-13b, DG-13c, DG-14a, DG-15c, DG-19a, DG-19c | 8 |
| LIFECYCLE_GAP | DG-02a, DG-02b, DG-10a, DG-10b, DG-14f | 5 |
| AUTHORITY_GAP | DG-01e, DG-15a, DG-17a, DG-17b, DG-17c, DG-17d, DG-17e | 7 |
| TRACEABILITY_GAP | DG-21a, DG-21b, DG-21c, DG-21d, DG-21e, DG-21f | 6 |
| **TOTAL** | 23 afirmações (CLOSED+FALSE_GAP) + 73 itens `DG-*` | **96** |

#### (b) Por decisão raiz (apenas itens abertos `DG-*`)

| DG raiz | Sub-itens | Quantidade |
|---|---|---:|
| DG-01 | DG-01a, DG-01b, DG-01c, DG-01d, DG-01e | 5 |
| DG-02 | DG-02a, DG-02b | 2 |
| DG-03 | DG-03a, DG-03b | 2 |
| DG-04 | DG-04a, DG-04b, DG-04d | 3 |
| DG-05 | DG-05a, DG-05b | 2 |
| DG-06 | DG-06a, DG-06b, DG-06c | 3 |
| DG-07 | DG-07a, DG-07b, DG-07c | 3 |
| DG-08 | DG-08a, DG-08b, DG-08c, DG-08d, DG-08e | 5 |
| DG-09 | DG-09a, DG-09b, DG-09c, DG-09d | 4 |
| DG-10 | DG-10a, DG-10b | 2 |
| DG-11 | DG-11a | 1 |
| DG-12 | DG-12a, DG-12b, DG-12c, DG-12d | 4 |
| DG-13 | DG-13a, DG-13b, DG-13c, DG-13d | 4 |
| DG-14 | DG-14a, DG-14b, DG-14c, DG-14d, DG-14e, DG-14f, DG-14g, DG-14h | 8 |
| DG-15 | DG-15a, DG-15b, DG-15c | 3 |
| DG-16 | DG-16a, DG-16b | 2 |
| DG-17 | DG-17a, DG-17b, DG-17c, DG-17d, DG-17e | 5 |
| DG-18 | DG-18a, DG-18b, DG-18c, DG-18d, DG-18f | 5 |
| DG-19 | DG-19a, DG-19c | 2 |
| DG-20 | DG-20a, DG-20b | 2 |
| DG-21 | DG-21a, DG-21b, DG-21c, DG-21d, DG-21e, DG-21f | 6 |
| **TOTAL** | | **73** |

**Reconciliação:** soma das quantidades por classificação dos itens `DG-*` = 6+35+6+8+5+7+6 = **73**, igual ao total por DG raiz. Soma global com as 23 afirmações de §4 = **96**. Duas perspectivas matematicamente reconciliadas.

---

## 4. CLOSED / FALSE GAPs

**Separação de métricas (obrigatória):**
- **A. Alegações refutadas (`FALSE_GAP` = 13):** afirmações do relatório anterior que a BÍBLIA já determina.
- **B. Regras actualmente fechadas (`CLOSED` = 10):** regras verificadas como determinadas pela BÍBLIA, algumas sem alegação prévia.
- **C. Gaps existentes (`DG-*` = 73):** §3.1, §5–§11.

`FALSE_GAP: 13` e "afirmações refutadas: 13" são a **mesma** métrica. A versão anterior do resumo indicou "15 afirmações refutadas" — valor **incorreto**; a contagem correcta, derivada dos itens listados, é **13** (23 afirmações verificadas = 13 refutadas + 10 confirmadas fechadas).

| ID | Alegação anterior | Classificação | Documento real | Regra normativa | Razão pela qual não é gap |
|---|---|---|---|---|---|
| F-01 | Conflito nome vs tvg-id não resolvido | FALSE_GAP | `05-CATALOGUE.md` | Ordem determinística 1→7; passo posterior não contradiz exacto (`:31-41`) | A ordem fixa o vencedor; não há escolha material |
| F-02 | Consequência de "ambiguous" indefinida | FALSE_GAP | `05-CATALOGUE.md`; `43-ANTI-PATTERNS.md` | Empate → `AMBIGUOUS` → Review; proibido escolher (`:43-47`, `:7`) | Efeito definido normativamente |
| F-03 | Reabertura de Review não formalizada | FALSE_GAP | `31-DECISION-LOCK.md`; `33-STATE-MACHINES.md` | DL-105: reabertura por operação auditada (`:114-116`, `:29`) | Decisão bloqueada pela Decision Lock |
| F-04 | — | CLOSED | `33-STATE-MACHINES.md` | Estados `Open→InReview→Resolved`, `Open→Ignored` (`:23-29`) | Regra fechada |
| F-05 | Drift remoto Dispatcharr não tratado | FALSE_GAP | `12-DISPATCHARR.md`; `31-DECISION-LOCK.md` | Ownership + reconciliação desired/actual (`:17-31,45-47`; DL-013/014/116) | Regra fechada |
| F-06 | Sem lista formal de invariantes | FALSE_GAP | `31-DECISION-LOCK.md`; `32-DOMAIN-SCHEMA.md`; `40-ENTITY-LIFECYCLE.md` | DL-024; `Key` única (`:78-79`, `:99`, `:38`) | As condições citadas são explícitas (distribuídas ≠ ausentes) |
| F-07 | `/api/country/save` sem contrato | FALSE_GAP | `22-API-CONTRACTS.md` | Ficha completa (`:109-128`) | Contrato existe |
| F-08 | Output sem atomicidade | FALSE_GAP | `11-OUTPUT.md`; `31-DECISION-LOCK.md` | Escrita atómica; parcial nunca válido (`:32-34`; DL-019) | Regra fechada |
| F-09 | Falha parcial de publicação indefinida | FALSE_GAP | `11-OUTPUT.md`; DL-019 | Idem F-08 | Regra fechada |
| F-10 | Config vs estado persistido não separados | FALSE_GAP | `14-CONFIGURATION.md`; `39-CONFIG-SCHEMA.md` | Duas classes + precedência (`:5-13`; `:5,37-47`) | Separação fechada |
| F-11 | Selection sem fallback indefinido | FALSE_GAP | `09-SELECTION.md`; `ADR-0003` | Resultado "no eligible source"; sem fallback silencioso (`:44-54`, `:59-61`) | Regra fechada |
| F-12 | Delegação a ADR reduz completude | FALSE_GAP | `00-BIBLE.md` | §6 mantém Regra de Completude (`:79`) | Regra fechada |
| F-13 | ADR `Proposed` autoriza implementação | FALSE_GAP | `29-AGENT-RULES.md` | `Proposed` não é implementável (`:56,58`) | Regra fechada |
| F-14 | Sem auditoria de integridade de delegação | FALSE_GAP | `45-BIBLE-AUDIT.md`; `46` | Secção de integridade de delegação (`:55-58`; `:52`) | Regra fechada |
| F-15 | — | CLOSED | `19-FAILURE-MODEL.md` | Retries limitados, backoff, cancelamento (`:39-43`) | Princípio fechado |
| F-16 | — | CLOSED | `04-PLAYLIST-STREAM.md`; `34` | Determinismo + preservação do original (`:30-32`; P3) | Princípio fechado |
| F-17 | — | CLOSED | `31-DECISION-LOCK.md`; `ADR-0003` | DL-101/DL-102 ordem e priority | Decisão bloqueada (Decision Lock) |
| F-18 | — | CLOSED | `10-ORDERING.md` | Posição sem canal não é preenchida (`:38-40`) | Regra fechada |
| F-19 | — | CLOSED | `31-DECISION-LOCK.md`; `08-VALIDATION.md` | DL-008; falha não apaga ChannelSource (`:30-31`; `:20-24,35-36`) | Regra fechada |
| F-20 | — | CLOSED | `31-DECISION-LOCK.md` | DL-024 ausência não é remoção (`:78-79`) | Decisão bloqueada |
| F-21 | Country ownership em falta (por ADR Proposed) | CLOSED | `06-COUNTRY-MEDIA.md`; `05-CATALOGUE.md` | País não é autoridade de identidade (`:5`; `:23-27`) | **Papel de identidade** fechado; a autoridade dos **dados** de classificação é DG-17a, não um FALSE_GAP (ver §4.1) |
| F-22 | — | CLOSED | `00-BIBLE.md`; `28` | Proibição de alteração silenciosa de autoridade (`:28,39-41,77`; `:31-39`) | Regra fechada |
| F-23 | — | CLOSED | `22-API-CONTRACTS.md`; `41`; `17` | Auth/authz/audit obrigatórias (`:8-14,56-58,67-69`; `:86`; `:41`) | Regra fechada |

### 4.1 Reavaliação específica de "Country" (pedido §10)

| Dimensão | Determinação da BÍBLIA | Classificação |
|---|---|---|
| País como identidade canónica | Determinado: país não é autoridade de identidade (`06-COUNTRY-MEDIA.md:5`; `05-CATALOGUE.md:23-27`; DL-001) | **CLOSED** (F-21) |
| Autoridade/precedência/versionamento dos dados de classificação de país | Não declarado normativamente; ADR-0001 é apenas evidência documental | **DG-17a — AUTHORITY_GAP** (decisão funcional) |
| Família Country na API | Fichas existem no documento normativo de contratos (`22-API-CONTRACTS.md:64-131`); o inventário não a enumera | **DG-21d — TRACEABILITY_GAP** (documental) |

Conclusão: "Country" **não** é um gap por o ADR-0001 estar `Proposed`. Decompõe-se em uma regra fechada, uma decisão funcional de autoridade e um problema documental de inventário.

---

## 5. Parameter gaps

Arquitectura e semântica de enquadramento fechadas; falta apenas um valor operacional. **Não inventar valores.** Vários dependem de uma decisão semântica anterior (indicado).

| ID | Parâmetro em falta | Regra que o enquadra | Fonte | Depende de |
|---|---|---|---|---|
| DG-04b | Tentativas máximas, curva/cap de backoff, jitter, âmbito (URL vs host) | Retries limitados com backoff e cancelamento | `19-FAILURE-MODEL.md:41` | DG-04a |
| DG-04d | Tamanho máximo de documento, nº máximo de entradas, comprimento máximo de campos, tempo máximo de parsing | Limites obrigatórios e configuráveis | `04-PLAYLIST-STREAM.md:50-57`; `03-DISCOVERY.md:32`; `17-SECURITY.md:23` | — |
| DG-05b | CIDRs/listas bloqueadas, limite de resposta, timeouts concretos | Bloqueio de redes internas e limites | `17-SECURITY.md:19-25` | DG-05a |
| DG-07b | Métrica de similaridade, limiar, normalização de comparação, pesos, desempate | "política de confiança e desempate" | `05-CATALOGUE.md:38` | DG-07a |
| DG-09d | Nº de falhas consecutivas e/ou janela temporal | Histerese base configurável | DL-104 `31-DECISION-LOCK.md:111-112` | DG-09a |
| DG-18f | Valores de retenção por entidade (Observation/Run/audit/artifacts) | Retenção é configuração explícita; nunca eliminação silenciosa | DL-113 `31-DECISION-LOCK.md:139-140`; `40-ENTITY-LIFECYCLE.md:32-34` | — |

---

## 6. Semantic decision gaps

Regra geral existe; duas interpretações materialmente diferentes permanecem possíveis.

| ID | Semântica em aberto | Fonte da regra existente | Implementação A (leitura BÍBLIA) | Implementação B (leitura BÍBLIA) | Divergência material |
|---|---|---|---|---|---|
| DG-03a | Input mínimo M3U | `04-PLAYLIST-STREAM.md:50-57`; `34-PIPELINE-CONTRACTS.md:17-19` | Exige header/estrutura canónica; entradas fora do formato não são aceites | Aceita qualquer linha com URL, sem header nem atributos obrigatórios | SIM |
| DG-03b | Parsing parcial vs total | `19-FAILURE-MODEL.md:32-37`; `32-DOMAIN-SCHEMA.md:69` | Entrada inválida descartada individualmente; válidas preservadas; `Status=Partial` | Linha malformada aborta a playlist inteira; nada persistido; falha `Parsing` | SIM |
| DG-04a | Terminal vs retryable e efeito persistido | `19-FAILURE-MODEL.md:22-28`; `08-VALIDATION.md:38-42` | Não-2xx terminal → `Source.Status=Error`; Run `Failed` | Toda a falha é marcada na tentativa, sem tocar `Source`; Run `PartiallySucceeded` | SIM |
| DG-05a | SSRF: classes bloqueadas e DNS/redirects | `17-SECURITY.md:19-25` | Mínimo: protocolos/timeouts/limites, sem classificação DNS/IP | Resolve DNS e bloqueia loopback/link-local/privado, fixa endereço em redirects | SIM |
| DG-06a | Transformações por campo | `04-PLAYLIST-STREAM.md:28-40` | Nome trim+lower+collapse+diacríticos; tvg-id→ExternalIdentity; URL canónica | Nome trim+collapse apenas; tvg-id verbatim; URL verbatim; fingerprint cru | SIM |
| DG-06b | Metadata ausente | `32-DOMAIN-SCHEMA.md:3,82` | Campo ausente → null com nullability declarada | Campo ausente → string vazia; ou entrada rejeitada | SIM |
| DG-07a | Fuzzy mandatório/opcional e policy | `05-CATALOGUE.md:31-41` | Fuzzy activo via policy de confiança | Passo fuzzy omitido; não-exactos → UNKNOWN → Review | SIM |
| DG-07c | Resultado abaixo do limiar | `05-CATALOGUE.md:43-53` | Abaixo do limiar → `UNKNOWN` → Review | Abaixo do limiar → `AMBIGUOUS` → Review | SIM |
| DG-08a | Resolve | `34-PIPELINE-CONTRACTS.md:45-48`; `05-CATALOGUE.md:65-67` | Cria CanonicalChannel+alias+external identity+ChannelSource e liga Stream | Só liga a CanonicalChannel existente; criação é operação separada | SIM |
| DG-08b | Ignore | `33-STATE-MACHINES.md:26` | Fecha item e marca Stream não publicável | Fecha item sem tocar Stream/ChannelSource | SIM |
| DG-08c | Alias/identity boundary | `32-DOMAIN-SCHEMA.md:101-110`; DL-002 | Resolve cria alias com normalização especificada | Resolve nunca cria alias/identidade | SIM |
| DG-08d | Critério de reabertura | DL-105 decide reabertura, não o critério | Deteção automática na ingestão de nova evidência | Reabertura manual por operador | SIM |
| DG-09a | Validação→Eligibility | `08-VALIDATION.md:34-42` | Classifica por `ErrorCode` e só muda após limiar | Conta apenas sucessos/falhas, ignora categoria | SIM |
| DG-09b | Ausência→Eligibility | `31-DECISION-LOCK.md:78-79` | Ausência mantém último estado | Ausência → `Unknown` | SIM |
| DG-09c | Fora da janela | DL-104 | Mantém último publicável indefinidamente | Transita para `Unknown`/`Ineligible` | SIM |
| DG-11a | Canal listado sem fonte elegível | `09-SELECTION.md:44-54` | Omitido do output | Emitido sem stream / erro de Run | SIM |
| DG-12a | Position/duplicados | `16-PERSISTENCE.md:27,30` | Position única; duplicado proibido | Unicidade condicional; duplicados permitidos | SIM |
| DG-12b | Canal não listado | `10-ORDERING.md:38-40` | Excluído do output | Incluído no fim | SIM |
| DG-12c | Múltiplas listas | `10-ORDERING.md:9,16`; `11-OUTPUT.md:26` | Uma lista publicada por Run | Várias listas publicadas; canal em vários outputs | SIM |
| DG-12d | Radio/VOD default | `10-ORDERING.md:26-36`; `06-COUNTRY-MEDIA.md:29-35` | Omitidos sem política | Publicados por defeito | SIM |
| DG-13d | Output parcial / replace falhado | `11-OUTPUT.md:32-34` | Parcial nunca persistido; output anterior mantém-se | Parcial persistido como artifact não publicado | SIM |
| DG-14b | Retry identity | `19-FAILURE-MODEL.md:39-43`; `33-STATE-MACHINES.md:59` | Nova Run com novo `RunId` | Mesma Run com contador | SIM |
| DG-14c | Trigger durante run activa | `13-RUNS.md:42-49` | Rejeitar (conflito) | Enfileirar/anexar | SIM |
| DG-14d | Serializar vs coordenar | `13-RUNS.md:44` | Serialização estrita | Coordenação por lease | SIM |
| DG-14e | Estado com lock perdido | DL-109 | `Failed` | `Cancelled`/estado intermédio | SIM |
| DG-14g | Cancelamento vs efeitos externos | `13-RUNS.md:51-55` | Efeitos aplicados permanecem; reconciliação posterior | Compensação/rollback dos efeitos | SIM |
| DG-14h | Timezone/DST | `16-PERSISTENCE.md:16` | Agendamento em UTC | Timezone local com regras DST | SIM |
| DG-15b | Operação apply/migração CLI/env | `14-CONFIGURATION.md:13`; `39-CONFIG-SCHEMA.md:43-45` | Aplicação explícita de ficheiro/env no arranque | Aplicação apenas por comando dedicado, com resultado diferente | SIM |
| DG-16b | Merge de policies | `38-POLICIES.md:26-28` | Substituição por camada; `Enabled=false` cai para a camada inferior | `Enabled=false` desactiva a decisão; coleções substituem; null limpa | SIM |
| DG-18a | Downgrade vs rollback | `20-OPERATIONS.md:20-26` | Downgrade não suportado; só rollback+restore | Downgrade suportado por migrations aditivas | SIM |
| DG-18b | Secrets em backup | `20-OPERATIONS.md:30-38` | Secrets excluídos/re-provisionados | Secrets incluídos e restaurados | SIM |
| DG-18c | Restore fresh vs upgrade | `20-OPERATIONS.md:42-45` | Mesmo procedimento | Procedimentos distintos; parcial inválido | SIM |
| DG-18d | Ordenação baseline vs migration | DL-107; `16-PERSISTENCE.md:42-49` | Schema migration e depois baseline | Baseline e depois migration | SIM |
| DG-20a | Detalhe físico vs schema normativo | `32-DOMAIN-SCHEMA.md:3` | Campo aditivo é detalhe físico aceitável | Qualquer campo relevante exige secção normativa prévia | SIM |
| DG-20b | Testes fixam comportamento não normativo | `00-BIBLE.md:24`; `46:32-34` | Testes podem fixar expectativas de implementação | Teste só demonstra comportamento normativo | SIM |

---

## 7. Model decision gaps

A própria modelação não está suficientemente definida.

| ID | Decisão de modelo em falta | Evidência | Implementação A | Implementação B | Divergência material |
|---|---|---|---|---|---|
| DG-01a | `DiscoveryCandidate` persistente vs ocorrência | `32-DOMAIN-SCHEMA.md:47-58`; `34-PIPELINE-CONTRACTS.md:5-6` | Entidade única por conta, `Id` estável, `LastSeen` atualizado | Registo por Run; nova linha por descoberta; antigo terminal | SIM |
| DG-01b | Algoritmo/âmbito de `NormalizedIdentity` | `32-DOMAIN-SCHEMA.md:52-54`; `03-DISCOVERY.md:16` | Normalização do `ExternalIdentity`, com namespace de provider | Canonicalização nova do locator de conta, provider-agnóstica | SIM |
| DG-01c | Link Candidate→ProviderAccount e fallback | `32-DOMAIN-SCHEMA.md:25,37,45,51-54` | Mapeia para `AccountKey`; sem identidade estável → não deduplica / um único ProviderAccount | Sem mapeamento; cada candidato aceite cria novo ProviderAccount/Source | SIM |
| DG-01d | Candidate vs Observation para a mesma descoberta | `32-DOMAIN-SCHEMA.md:53,58`; `28:12` | Re-descoberta atualiza candidate | Re-descoberta cria Observation + candidate | SIM |
| DG-06c | Algoritmo de canonicalização de URL/fingerprint | `04-PLAYLIST-STREAM.md:34-40` | Canonicalização com remoção apenas de elementos não-identificadores, versionada | Canonicalização diferente (ex.: normalizar host/path) muda o fingerprint e a dedup | SIM |
| DG-16a | Schema de campos de `Parameters` por policy | `38-POLICIES.md:28`; `32-DOMAIN-SCHEMA.md` | Schema define campos independentes e merge campo-a-campo | Schema inexistente → substituição da policy inteira | SIM |

---

## 8. Contract gaps

Modelo/semântica decididos; falta a fronteira de implementação. Não usados para esconder decisões funcionais (essas estão em §6/§7).

| ID | Fronteira em falta | Evidência |
|---|---|---|
| DG-08e | Ficha normativa da família Review | `22-API-CONTRACTS.md:46-48`; `41-API-INVENTORY.md:44-49` |
| DG-13a | Contrato físico do M3U de output (atributos/encoding) | `11-OUTPUT.md:21-28`; `23-DATA-CONTRACTS.md:37-52` |
| DG-13b | Enumeração de `publication state` | `32-DOMAIN-SCHEMA.md:224-233` |
| DG-13c | Convenção de nome/`Path/reference` | `32-DOMAIN-SCHEMA.md:230` |
| DG-14a | Formato/estabilidade do `RunId` | `13-RUNS.md:6`; `32-DOMAIN-SCHEMA.md:248` |
| DG-15c | Matriz/ACL de permissões por interface; "gate único" | `35-SECURITY-MODEL.md:5-12`; `22-API-CONTRACTS.md:67-68` |
| DG-19a | 13 fichas de API em falta | `41-API-INVENTORY.md:3,5-84`; `22-API-CONTRACTS.md:28-48` |
| DG-19c | Limites/concorrência dos endpoints Country | `22-API-CONTRACTS.md:42-43,109-128` |

---

## 9. Lifecycle gaps

Entidade existe; criação/transição/expiração/reactivação/remoção insuficientemente definida.

| ID | Entidade / transição | Evidência |
|---|---|---|
| DG-02a | Trigger de `DiscoveryCandidate → Expired` | `33-STATE-MACHINES.md:21`; `40-ENTITY-LIFECYCLE.md:9` |
| DG-02b | Re-descoberta/reactivação/TTL do candidate | `33-STATE-MACHINES.md:21`; `40-ENTITY-LIFECYCLE.md:5-10` |
| DG-10a | Recuperação `Ineligible → ?` | `33-STATE-MACHINES.md:37-41`; `08-VALIDATION.md` |
| DG-10b | Lifecycle de Eligibility não coberto por `40` | `40-ENTITY-LIFECYCLE.md:5-10` |
| DG-14f | Estado da Run após restart; `Created→Failed/Cancelled` | `19-FAILURE-MODEL.md:45-47`; `33-STATE-MACHINES.md:43-59` |

---

## 10. Authority gaps

Dois documentos podem ser interpretados como autoridade do mesmo conceito, ou nenhuma autoridade declarada.

| ID | Conceito | Autoridades em conflito / ausência | Evidência |
|---|---|---|---|
| DG-01e | Identidade da conta descoberta | `DiscoveryCandidate` (não listado como identidade) vs `ProviderAccount` | `28-TRUTH-AND-TRACEABILITY.md:5,8-15`; `02-DOMAIN.md:39,42,70`; `03-DISCOVERY.md:24` |
| DG-15a | Propriedade dual técnica/funcional | Classes sem declaração de autoridade | `39-CONFIG-SCHEMA.md:47`; `14-CONFIGURATION.md:5-13` |
| DG-17a | Classificação de país | Sem declaração normativa da autoridade dos dados de classificação | `28:8-15`; `06-COUNTRY-MEDIA.md:5` |
| DG-17b | Classificação de media | Resultado derivado sem autoridade declarada | `06-COUNTRY-MEDIA.md:27`; `02-DOMAIN.md:19` |
| DG-17c | Source priority | Preferência declarada; entidade-valor sem autoridade | `09-SELECTION.md:5`; DL-010 `31:36-37` |
| DG-17d | Estado actual do Dispatcharr | Observado mas sem posse/persistência declarada | `12-DISPATCHARR.md:26-27`; DL-116 `31:148-149` |
| DG-17e | Policy e Run | Ausentes da lista de autoridades de `28` | `28:8-15`; `38-POLICIES.md:32`; `13-RUNS.md:5-14` |

---

## 11. Traceability gaps

Itens **documentais**: a decisão funcional está (ou será) em `DG-01..DG-20`; falta o registo/rastreabilidade normativa. Não são decisões do proprietário.

| ID | Rastreabilidade em falta | Evidência |
|---|---|---|
| DG-21a | Registo normativo da decisão de histerese (funcional em DG-09) | `08-VALIDATION.md:44-46`; `24-DECISIONS.md:10` |
| DG-21b | Registo normativo do mecanismo de scheduler locking (funcional em DG-14) | `24-DECISIONS.md:14`; `47-BIBLE-AUDIT-FINDINGS.md:30` |
| DG-21c | Decisões funcionais (DG-17/DG-18) registadas apenas em ADRs `Proposed`/evidência | `ADR-0001:3`; `ADR-0005`; `ADR-0006:3`; `docs/adr/README.md:44` |
| DG-21d | Família Country ausente do inventário de API | `41-API-INVENTORY.md:5-84`; `22-API-CONTRACTS.md:64-131` |
| DG-21e | Matriz de rastreabilidade vazia (`TBD`/`UNMAPPED`) | `46-REQUIREMENT-TRACEABILITY.md:23-26,40-42`; `28:27` |
| DG-21f | Re-auditoria de ADRs `Proposed` (0002/0004/0005/0006) pendente | `docs/adr/README.md:40-44`; `45-BIBLE-AUDIT.md:55-58` |

---

## 12. End-to-end scenario results

Formato obrigatório: **Cenário | Primeira decisão determinada | Fonte | Resultado | Se não determinada: DG**. Quando um cenário encontra uma ambiguidade já coberta por um `DG`, referencia esse `DG` (não cria outro).

| Cenário | Primeira decisão determinada | Fonte | Resultado | Se não determinada: DG |
|---|---|---|---|---|
| **S1 — Novo stream desconhecido** | Desconhecido → `UNKNOWN` → Review; sem criação implícita | `05-CATALOGUE.md:49-53`; DL-003 | **SIM** (para desconhecido genuíno) | Se quase-correspondência: **DG-07a/c** |
| **S2 — Mesmo canal em três fontes** | Um `ChannelSource` por Source; par único `(CanonicalChannelId, SourceId)`; Selection por DL-101 | `32-DOMAIN-SCHEMA.md:162-163`; `31-DECISION-LOCK.md:86-98`; `ADR-0003` | **SIM** | — |
| **S3 — Canal conhecido sem stream elegível** | Selection distingue "no eligible source" | `09-SELECTION.md:44-54`; `ADR-0003:59-61` | **NÃO** | **DG-11a** |
| **S4 — Stream publicado falha na execução seguinte** | Falha não apaga ChannelSource | `08-VALIDATION.md:35-36`; DL-024 | **NÃO** | **DG-09a** |
| **S5 — Stream falha e recupera** | Estados Eligibility definidos | `33-STATE-MACHINES.md:37-41`; DL-104 | **NÃO** | **DG-10a** |
| **S6 — Review resolvido através de alias** | Decisão administrativa auditada | `34-PIPELINE-CONTRACTS.md:45-48`; `05-CATALOGUE.md:65-67` | **NÃO** | **DG-08a / DG-08c** |
| **S7 — Nova evidência contradiz Review resolvido** | Reabertura por operação auditada | DL-105 `31-DECISION-LOCK.md:114-116` | **SIM** | Refinamento em **DG-08d** |
| **S8 — Ordering com dois canais na mesma posição** | Só a regra inversa (posição sem canal) | `10-ORDERING.md:38-40` | **NÃO** | **DG-12a** |
| **S9 — Canal conhecido fora de qualquer OrderingList** | Sem regra | — | **NÃO** | **DG-12b** |
| **S10 — Output falha durante publicação** | Output atómico; parcial nunca válido | `11-OUTPUT.md:32-34`; DL-019 | **NÃO** | **DG-13d** |
| **S11 — Dispatcharr tem recurso externo** | `External`/`Unknown` nunca apagados automaticamente | `12-DISPATCHARR.md:45-47`; DL-014 | **SIM** | — |
| **S12 — Run manual enquanto outro activo** | Uma execução activa por runtime; lock antes de efeitos | `31-DECISION-LOCK.md:127-128` | **NÃO** | **DG-14c** (e **DG-14d**) |
| **S13 — Config alterada por UI e restart** | Estado funcional não é sobrescrito no arranque | `14-CONFIGURATION.md:13`; `39-CONFIG-SCHEMA.md:43` | **SIM** | Exceção dual-class: **DG-15a** |
| **S14 — Restore de backup em instalação limpa** | Conteúdo de backup definido; pipeline restore definido | `20-OPERATIONS.md:30-36,44-45` | **NÃO** | **DG-18b / DG-18c** |
| **S15 — Discovery reencontra Candidate anterior** | Estado `Expired` existe | `33-STATE-MACHINES.md:21` | **NÃO** | **DG-01a / DG-02b** |

**Primeiras divergências identificadas:** DG-11a (S3), DG-09a (S4), DG-10a (S5), DG-08a/c (S6), DG-12a (S8), DG-12b (S9), DG-13d (S10), DG-14c/d (S12), DG-18b/c (S14), DG-01a/DG-02b (S15). Em S1 (desconhecido genuíno), S2, S7, S11 e S13 a BÍBLIA determina o resultado principal. Nenhum cenário criou um `DG` novo.

---

## 13. Root decision map

Validação de cada `DG` raiz: decisão, sub-itens, documentos, duplicação, independência e estado na BÍBLIA. Nenhuma raiz contém duas decisões independentes não relacionadas; nenhum sub-item pertence a outra raiz (o duplicado `DG-04c` foi absorvido por `DG-14b`; itens documentais foram separados em `DG-21`).

| DG | Decisão raiz exacta | Sub-itens | Documentos | Duplicada com? | Independente? | Já CLOSED? |
|---|---|---|---|---|---|---|
| DG-01 | Modelo e identidade do `DiscoveryCandidate` (persistência, identidade, link a conta, candidate vs observation, autoridade) | a, b, c, d, e | `02`, `03`, `07`, `16`, `27`, `28`, `32`, `33`, `34`, `40` | Não | Sim (pré-condição de DG-02 e S15) | Não |
| DG-02 | Lifecycle do `DiscoveryCandidate` (expiração/reactivação) | a, b | `03`, `32`, `33`, `40` | Não | Sim (depende de DG-01a) | Não |
| DG-03 | Parsing M3U (input mínimo e falha parcial/total) | a, b | `04`, `19`, `23`, `32`, `34` | Não | Sim | Não |
| DG-04 | Aquisição: semântica terminal/retryable e parâmetros | a, b, d | `03`, `08`, `17`, `19`, `33`, `39` | `DG-04c` fundido em **DG-14b** | Sim (DG-04a alimenta DG-09a) | Não |
| DG-05 | Política SSRF (semântica e valores) | a, b | `17`, `24`, `35`, `39`, `47` | Não | Sim | Não |
| DG-06 | Normalização e canonicalização (transformações, nullability, algoritmo de fingerprint) | a, b, c | `04`, `05`, `10`, `23`, `32` | Não | Sim (alimenta DG-07/DG-08c) | Não |
| DG-07 | Reconhecimento fuzzy (opcionalidade, métrica, abaixo do limiar) | a, b, c | `05`, `34`, `38`, `43` | Não | Sim (depende de DG-06a) | Não |
| DG-08 | Efeitos das ações de Review (Resolve, Ignore, alias, reabertura) + contrato | a, b, c, d, e | `05`, `22`, `31`, `32`, `33`, `34`, `41` | Não | Sim (a/b/c funcionais; e contratual) | Não |
| DG-09 | Validação→Eligibility e histerese | a, b, c, d | `08`, `19`, `31`, `32`, `33`, `38`, `39` | Não | Sim (DG-21a é o registo documental) | Não |
| DG-10 | Lifecycle de `Eligibility` (recuperação e cobertura) | a, b | `08`, `32`, `33`, `40` | Não | Sim (depende de DG-09) | Não |
| DG-11 | Output sem fonte elegível | a | `09`, `10`, `11` | Não | Sim | Não |
| DG-12 | Ordenação (unicidade, não listados, múltiplas listas, media kinds) | a, b, c, d | `06`, `10`, `11`, `16`, `32` | Não | Sim (media kind relaciona DG-17) | Não |
| DG-13 | Contrato de output/publicação (M3U, estado, naming, parcial) | a, b, c, d | `11`, `16`, `23`, `31`, `32` | Não | Sim | Não |
| DG-14 | Run/Scheduler (ID, retry, concorrência, lock, restart, cancelamento, DST) | a, b, c, d, e, f, g, h | `11`, `12`, `13`, `14`, `16`, `19`, `31`, `33`, `40` | Absorve `DG-04c` | Sim (DG-21b é o registo documental) | Não |
| DG-15 | Configuração e permissões (autoridade dual, apply, ACL) | a, b, c | `14`, `17`, `22`, `35`, `39`, `41` | Não | Sim | Não |
| DG-16 | Policy: schema de campos e semântica de merge | a, b | `09`, `31`, `32`, `38`, `39` | Não | Sim | Não |
| DG-17 | Matriz de autoridade em falta (país, media, priority, Dispatcharr actual, policy/Run) | a, b, c, d, e | `02`, `06`, `09`, `12`, `13`, `28`, `38` | Não | Cada sub-item é independente entre si, mas todos declaram autoridade — mesma decisão-tipo | Não |
| DG-18 | Backup/restore/migration/downgrade + retenção | a, b, c, d, f | `14`, `16`, `17`, `20`, `26`, `36`, `39` | Não | Sim | Não |
| DG-19 | Contratos de API em falta | a, c | `22`, `23`, `41` | Não | Sim (depende de semânticas de DG-08/09/14) | Não |
| DG-20 | Governação normativa (fronteira físico/normativo; testes) | a, b | `00`, `16`, `21`, `32`, `43`, `46` | Não | Sim | Não |
| DG-21 | Rastreabilidade documental (registos, inventário, matriz, re-auditoria) | a, b, c, d, e, f | `08`, `13`, `16`, `20`, `22`, `24`, `28`, `41`, `45`, `46`, `47`, `docs/adr/` | Não (é o destino dos antigos DG-09e/18e/19b) | Sim (documental; não é decisão funcional) | Não |

Relações entre roots (não duplicação):
- DG-01a alimenta DG-02 e S15.
- DG-04a alimenta DG-09a (falha de aquisição vs validação) e DG-14b (retry identity).
- DG-06a alimenta DG-07 (matching usa nome normalizado) e DG-08c (normalização de alias).
- DG-09 e DG-14 têm registos documentais em DG-21a / DG-21b (não são novas decisões funcionais).
- DG-17 é transversal a DG-12 (media kind), DG-15 (config) e DG-16 (policy).

---

## 14. Documents affected

| Documento | Roots que o afectam |
|---|---|
| `00-BIBLE.md` | DG-20 |
| `02-DOMAIN.md` | DG-01, DG-17 |
| `03-DISCOVERY.md` | DG-01, DG-02, DG-04 |
| `04-PLAYLIST-STREAM.md` | DG-03, DG-06 |
| `05-CATALOGUE.md` | DG-06, DG-07, DG-08 |
| `06-COUNTRY-MEDIA.md` | DG-12, DG-17 |
| `07-SOURCES.md` | DG-01 |
| `08-VALIDATION.md` | DG-09, DG-10, DG-21 |
| `09-SELECTION.md` | DG-11, DG-16, DG-17 |
| `10-ORDERING.md` | DG-06, DG-11, DG-12 |
| `11-OUTPUT.md` | DG-11, DG-12, DG-13 |
| `12-DISPATCHARR.md` | DG-14, DG-17 |
| `13-RUNS.md` | DG-04, DG-14, DG-21 |
| `14-CONFIGURATION.md` | DG-15, DG-18 |
| `16-PERSISTENCE.md` | DG-12, DG-13, DG-14, DG-18, DG-20, DG-21 |
| `17-SECURITY.md` | DG-04, DG-05, DG-15, DG-18 |
| `19-FAILURE-MODEL.md` | DG-03, DG-04, DG-09, DG-14 |
| `20-OPERATIONS.md` | DG-18, DG-21 |
| `21-TESTING.md` | DG-20 |
| `22-API-CONTRACTS.md` | DG-08, DG-15, DG-19, DG-21 |
| `23-DATA-CONTRACTS.md` | DG-03, DG-06, DG-13, DG-19 |
| `24-DECISIONS.md` | DG-21 |
| `26-RECONSTRUCTION.md` | DG-18 |
| `28-TRUTH-AND-TRACEABILITY.md` | DG-01, DG-17, DG-21 |
| `31-DECISION-LOCK.md` | DG-06, DG-08, DG-09, DG-12, DG-14, DG-15, DG-16, DG-18 |
| `32-DOMAIN-SCHEMA.md` | DG-01, DG-03, DG-06, DG-09, DG-12, DG-13, DG-16, DG-20 |
| `33-STATE-MACHINES.md` | DG-01, DG-02, DG-04, DG-08, DG-09, DG-10, DG-14 |
| `34-PIPELINE-CONTRACTS.md` | DG-01, DG-03, DG-06, DG-07, DG-08 |
| `35-SECURITY-MODEL.md` | DG-05, DG-15 |
| `36-RECONSTRUCTION-ORDER.md` | DG-08, DG-18 |
| `38-POLICIES.md` | DG-07, DG-09, DG-16, DG-17 |
| `39-CONFIG-SCHEMA.md` | DG-04, DG-05, DG-09, DG-15, DG-16 |
| `40-ENTITY-LIFECYCLE.md` | DG-01, DG-02, DG-09, DG-10, DG-14, DG-18 |
| `41-API-INVENTORY.md` | DG-08, DG-15, DG-19, DG-21 |
| `43-ANTI-PATTERNS.md` | DG-07, DG-20 |
| `45-BIBLE-AUDIT.md` | DG-21 |
| `46-REQUIREMENT-TRACEABILITY.md` | DG-20, DG-21 |
| `47-BIBLE-AUDIT-FINDINGS.md` | DG-05, DG-14, DG-21 |

---

## 15. Decisions required from owner (apenas decisões funcionais)

Somente decisões sobre produto/arquitetura/comportamento. Nenhuma aprovação de ADR é uma decisão funcional; o registo documental está em DG-21 e não consta desta lista.

1. **DG-01** — O `DiscoveryCandidate` é entidade persistente única por conta ou registo por Run? Qual a sua identidade/`NormalizedIdentity`, a ligação a `ProviderAccount`/`Source`, e a modelação de uma re-descoberta?
2. **DG-02** — O que provoca `Expired` e como se dá a re-descoberta/reactivação?
3. **DG-03** — Qual o input mínimo M3U e o comportamento de parsing perante entrada inválida/playlist totalmente inválida (resultado/`Status`)?
4. **DG-04** — Que falhas de aquisição são terminais vs retryable, e qual o efeito persistido em candidate/Source/Playlist/Run? (valores de retry = parâmetros)
5. **DG-05** — Qual a política SSRF: que classes de URL bloquear e como tratar DNS, redirects e rebinding? (CIDRs/timeouts = parâmetros)
6. **DG-06** — Quais as transformações de normalização por campo, a nullability de metadados ausentes, e qual o algoritmo normativo de canonicalização/fingerprint? (ADR-0002 é apenas evidência)
7. **DG-07** — O fuzzy é mandatório/opcional, qual a policy que o autoriza, e o que resulta abaixo do limiar? (métrica/limiar = parâmetros)
8. **DG-08** — Que efeitos exactos têm Resolve e Ignore, e existe criação de alias/identidade por Review?
9. **DG-09** — Como mapeiam os outcomes de Validation para Eligibility, o que é "último estado publicável", e qual o efeito de erros concretos (403/404/vazio)? (limiar/janela = parâmetros)
10. **DG-10** — A recuperação de `Ineligible` é automática por nova evidência ou administrativa, e qual o lifecycle completo de Eligibility?
11. **DG-11** — O que acontece no output a um canal listado sem fonte elegível?
12. **DG-12** — `Position` é única? São permitidos duplicados/não listados? Quantas listas se publicam? Radio/VOD por defeito?
13. **DG-13** — Qual o contrato físico do M3U, a enumeração de `publication state`, o naming do output e o que acontece a um parcial/no replace falhado?
14. **DG-14** — Retry (nova/mesma Run), trigger durante run activa, serializar vs coordenar, estado com lock perdido, estado após restart, cancelamento vs efeitos externos, timezone/DST.
15. **DG-15** — Qual a autoridade das propriedades dual-class, a operação explícita apply/migração, e a ACL de permissões?
16. **DG-16** — Qual o schema de campos de policy e a semântica de merge (null/ausente/disabled/coleções)?
17. **DG-17** — Quem é a autoridade de: classificação de país, classificação de media, source priority, estado actual do Dispatcharr, policy e Run?
18. **DG-18** — Como se diferenciam fresh install/restore/upgrade/downgrade; que secrets entram no backup; qual o estado de um restore interrompido; e como se ordenam baseline upgrade e schema migration? (retenção = parâmetros)
19. **DG-19** — Quais as fichas das 13 famílias de API e que limites/concorrência se aplicam?
20. **DG-20** — Qual a fronteira entre detalhe físico e decisão normativa, e podem testes fixar comportamento não normativo?

---

## 16. Proposed order for closing gaps

Ordem de **fecho de decisões** (não é um plano de implementação; não é uma wave de código). Itens documentais de DG-21 fecham-se depois, à medida que as decisões funcionais são inscritas na BÍBLIA.

1. **Bloco de modelo de dados:** DG-01, DG-02, DG-16a — identidades, persistência e schema; desbloqueia quase tudo o resto.
2. **Bloco de entrada:** DG-03, DG-04, DG-05, DG-06, DG-07 — parsing/aquisição/normalização/reconhecimento.
3. **Bloco de estado:** DG-08, DG-09, DG-10, DG-11 — Review, validação, elegibilidade e output sem fonte.
4. **Bloco de publicação/runtime:** DG-12, DG-13, DG-14 — ordering, output e scheduler.
5. **Bloco de configuração/políticas/autoridade:** DG-15, DG-16b, DG-17.
6. **Bloco de operações/contratos:** DG-18, DG-19.
7. **Bloco de governação/rastreabilidade:** DG-20 + DG-21 (inscrição na BÍBLIA, inventário de API, matriz de rastreabilidade, re-auditoria de ADRs).

Justificação: os blocos 1–3 determinam semântica que os blocos 4–6 referenciam (ex.: DG-19 depende de DG-08/DG-09/DG-14); DG-21 deve fechar por último para materializar a rastreabilidade de tudo o resto.

---

## 17. Acceptance criteria

O manifest é aceite quando:

1. nenhum ficheiro inexistente é citado (§2.1);
2. nenhum gap já coberto é repetido (§4, `FALSE_GAP`);
3. nenhuma decisão nova é introduzida implicitamente (§6-§11 têm apenas perguntas);
4. decisões bloqueadas pela Decision Lock não são reabertas (F-01, F-03, F-11, F-17, F-19, F-20);
5. cada gap tem causa raiz e está agrupado (§13);
6. cada `DECISÃO NECESSÁRIA` identifica exactamente a decisão em falta (§15, apenas funcional);
7. parâmetros não são confundidos com decisões de arquitectura (§5 vs §6);
8. os cenários S1–S15 identificam a primeira divergência e referenciam DGs existentes (§12);
9. as contagens reconciliam mecanicamente (§3.2a = §3.2b);
10. nenhuma decisão funcional está aberta apenas por causa do estado de um ADR (§2.2, §4.1, §11);
11. a BÍBLIA 1.3 poderá ser produzida mecanicamente a partir das decisões fechadas (sem novo trabalho de descoberta).

**Estado de fecho:** `AUDIT COMPLETE` + `GAP MANIFEST COMPLETE` + `DECISIONS REQUIRED IDENTIFIED`. A BÍBLIA 1.3 **não** é produzida nesta fase.

---

## 18. Audit limitations

- Auditoria **documental**; não executou código, não avaliou testes, não avaliou runtime.
- As referências `file:line` são válidas para o estado do repositório na data da auditoria; qualquer edição as invalida.
- A classificação `SEMANTIC_DECISION_GAP` assenta no critério de §3 (duas implementações materialmente diferentes); é um juízo fundamentado, não um teste mecânico.
- O relatório anterior é tratado como alegação, nunca como fonte; as suas referências inexistentes foram corrigidas em §2.4.
- Documentos derivados (`PROJECT_STATUS.md`, `docs/architecture/`, `IMPLEMENTATION_ROADMAP.md`) foram usados apenas como evidência marcada e nunca para fechar ou abrir um gap.
- `DG-*` não existe no repositório; é uma numeração deste manifesto, derivada das etiquetas `G-*` do pedido.
- A revisão `1.0-reconciliado` alterou apenas este manifesto (classificações, agrupamento e contagens); não introduziu decisões nem alterou a BÍBLIA, ADRs, código ou testes.

---

## Anexo A — Estado Git

```text
branch: feature/phase-9c-first-run-dashboard
HEAD: bde06122740b6b8d2c6ff493fff6a0e4326c4b9c
working tree: 1 ficheiro alterado/novo por esta auditoria (este manifesto); nenhum ficheiro normativo/ADR/código/teste alterado
alterações (pré-existentes, não causadas por esta auditoria):
   M AGENTS.md
   M CONTRIBUTING.md
   M README.md
   M ROADMAP.md
   ?? docs/Reestructure/   (BÍBLIA não rastreada neste estado do repo)
   ?? docs/RESTRUCTURE-GOVERNANCE.md
   ?? docs/architecture/README.md
   ?? (+ scripts .bat/.ps1 pré-existentes)
novo/alvo desta auditoria:
   ?? BIBLE_IMPLEMENTABILITY_GAP_MANIFEST_1.0.md
```

> Verificação por comandos read-only de Git. As modificações rastreadas (`AGENTS.md`, `CONTRIBUTING.md`, `README.md`, `ROADMAP.md`) e os restantes ficheiros `??` já existiam no working tree antes desta auditoria. Esta auditoria não alterou nenhum ficheiro rastreado, nenhum documento em `docs/Reestructure/`, nenhum ADR, código ou teste, e não criou commits. O único artefacto tocado é este manifesto, na raiz do repositório. O HEAD real (`bde0612`) difere do registado em `docs/PROJECT_STATUS.md:9` (`a2c2eae`); é uma divergência de estado de documentação derivada, fora do scope.

## Anexo B — Resumo quantitativo final

```text
CLOSED: 10
FALSE_GAP: 13
PARAMETER_GAP: 6
SEMANTIC_DECISION_GAP: 35
MODEL_DECISION_GAP: 6
CONTRACT_GAP: 8
LIFECYCLE_GAP: 5
AUTHORITY_GAP: 7
TRACEABILITY_GAP: 6

TOTAL (itens abertos DG-*): 73
TOTAL (afirmações verificadas §4): 23
TOTAL GERAL: 96
```

## Anexo C — Lista final de DG raiz

```text
DG-01 Modelo e identidade do DiscoveryCandidate
DG-02 Lifecycle do DiscoveryCandidate
DG-03 Parsing M3U
DG-04 Aquisição/retry (semântica e parâmetros)
DG-05 Política SSRF
DG-06 Normalização e canonicalização
DG-07 Reconhecimento fuzzy
DG-08 Efeitos das ações de Review + contrato
DG-09 Validação→Eligibility e histerese
DG-10 Lifecycle de Eligibility
DG-11 Output sem fonte elegível
DG-12 Ordenação
DG-13 Contrato de output/publicação
DG-14 Run/Scheduler
DG-15 Configuração e permissões
DG-16 Policy: schema e merge
DG-17 Matriz de autoridade
DG-18 Backup/restore/migration/downgrade + retenção
DG-19 Contratos de API
DG-20 Governação normativa
DG-21 Rastreabilidade documental
```

---

## Anexo D — Round 1: decisões do proprietário aplicadas (actualização 1.1)

> **Natureza:** a BÍBLIA em `docs/Reestructure/` foi actualizada para incorporar as decisões Round 1. Código, testes e migrations **não** foram alterados. Nenhum ADR foi criado.

### D.1 Decision Register (Round 1)

| ID | Decisão aplicada | Estado | DGs/subdecisões fechadas |
|---|---|---|---|
| A1 | Dois níveis de retry: técnico de operação (mesma Run) vs reexecução lógica (nova Run + relação causal) | `CLOSED_WITH_DOC_IMPACT` | DG-14b, DG-04c |
| A2 | Fingerprint normativo na BÍBLIA: representação canónica, conj. explícito de campos, UTF-8, SHA-256, versionado; sem ADR | `CLOSED_WITH_DOC_IMPACT` | DG-06c (C1 resolvido) |
| A3 | Tabela normativa de normalização por campo | `CLOSED_WITH_DOC_IMPACT` | DG-06a |
| A4 | `AccountKey` = namespace Provider + identidade externa funcional; fallback determinístico; sem posição/ordem/timestamp/credenciais | `CLOSED_WITH_DOC_IMPACT` | DG-01c, DG-01e, C2 resolvido |
| A5 | Candidate = ocorrência por Run; remover `FirstSeen`/`LastSeen`; histórico via Run/Observation | `CLOSED_WITH_DOC_IMPACT` | DG-01a, DG-01d |
| Q1 | Autoridades: Country→`CountryProfile`; Media→`MediaClassification`; Priority→`SourcePriorityPolicy`; Selection→`SourceSelectionPolicy`; Validation→`Observation`; Dispatcharr actual→evidência; desired→plano calculado; Policy→policy por tipo | `CLOSED_WITH_DOC_IMPACT` | DG-17a..e |
| Q2 | Explicitar entidades necessárias em `32` sem duplicação | `CLOSED_WITH_DOC_IMPACT` | DG-17 (estrutura) |
| Q3 | Fronteira `32:3` + secção normativa; TBD só para parâmetro/físico; aceitação por requisito com gate | `CLOSED_WITH_DOC_IMPACT` | DG-20a, DG-20b |
| Q4 | M3U: `#EXTM3U`+`#EXTINF`+URL; malformada registada com flag parcial; `Status`=Success/Partial/Failed; estrutura vs alvo | `CLOSED_WITH_DOC_IMPACT` | DG-03a, DG-03b |
| Q5 | SSRF: http/https; bloquear loopback/private/link-local/metadata IPv4+IPv6; resolver/pin; reclassificar redirects; fail-closed | `CLOSED_WITH_DOC_IMPACT` | DG-05a |
| Q6 | Normalização por tabela/campo; ausente=`null`; separar Normalization/Recognition/Fingerprint | `CLOSED_WITH_DOC_IMPACT` | DG-06a, DG-06b |
| Q7 | URL canonicalization mínima; conjunto de campos explícito; algoritmo normativo (A2) | `CLOSED_WITH_DOC_IMPACT` | DG-06c |
| Q8 | Candidate = ocorrência por Run | `CLOSED_WITH_DOC_IMPACT` | DG-01a, DG-01d |
| Q9 | AccountKey composto; `ProviderAccountId` no Candidate; identidade técnica não substitui funcional; unificar `AccountKey` | `CLOSED_WITH_DOC_IMPACT` | DG-01c, DG-01e (C2 resolvido) |
| Q10 | `RunId` opaco único; retry lógico = nova Run com relação causal | `CLOSED_WITH_DOC_IMPACT` | DG-14a, DG-14b |
| Q11 | Trigger concorrente rejeitado; serialização estrita; lock perdido→`Failed`; restart `Created`/`Running`→`Failed` | `CLOSED_WITH_DOC_IMPACT` | DG-14c, DG-14d, DG-14e, DG-14f |
| Q12 | Cancelamento mantém efeitos + reconciliação; scheduler com timezone e persistência UTC | `CLOSED_WITH_DOC_IMPACT` | DG-14g, DG-14h |

### D.2 Conflitos resolvidos

- **C1 (fingerprint/ADR):** removida a obrigação de ADR em `04-PLAYLIST-STREAM.md` (era linha ~40) e em `24-DECISIONS.md` (item 1). Fingerprint passa a definição normativa (canónica, determinística, versionada, UTF-8, SHA-256, conjunto explícito de campos).
- **C2 (AccountId/AccountKey):** unificado em `AccountKey` (`03-DISCOVERY.md` e `32-DOMAIN-SCHEMA.md`). Referências residuais a `AccountId` isolado: nenhuma (apenas `ProviderAccountId`, legítimo).

### D.3 Documentos normativos alterados (docs/Reestructure/)

`00-BIBLE.md`, `02-DOMAIN.md`, `03-DISCOVERY.md`, `04-PLAYLIST-STREAM.md`, `05-CATALOGUE.md`, `06-COUNTRY-MEDIA.md`, `08-VALIDATION.md`, `09-SELECTION.md`, `12-DISPATCHARR.md`, `13-RUNS.md`, `14-CONFIGURATION.md`, `16-PERSISTENCE.md`, `17-SECURITY.md`, `19-FAILURE-MODEL.md`, `23-DATA-CONTRACTS.md`, `24-DECISIONS.md`, `28-TRUTH-AND-TRACEABILITY.md`, `29-AGENT-RULES.md`, `32-DOMAIN-SCHEMA.md`, `33-STATE-MACHINES.md`, `34-PIPELINE-CONTRACTS.md`, `35-SECURITY-MODEL.md`, `38-POLICIES.md`, `39-CONFIG-SCHEMA.md`, `40-ENTITY-LIFECYCLE.md`, `43-ANTI-PATTERNS.md`, `44-QUALITY-GATES.md`, `45-BIBLE-AUDIT.md`, `46-REQUIREMENT-TRACEABILITY.md`.

### D.4 Gaps fechados (26 itens `DG-*`)

```text
DG-01a, DG-01c, DG-01d, DG-01e
DG-03a, DG-03b
DG-04c
DG-05a
DG-06a, DG-06b, DG-06c
DG-14a, DG-14b, DG-14c, DG-14d, DG-14e, DG-14f, DG-14g, DG-14h
DG-17a, DG-17b, DG-17c, DG-17d, DG-17e
DG-20a, DG-20b
```

Recontagem: 73 itens abertos − 26 fechados = **47 itens ainda abertos**. Os itens de tipo `PARAMETER_GAP` (valores de retry, limites, CIDRs, retenção, threshold de fuzzy) permanecem como parâmetros operacionais, não como decisões arquitecturais.

### D.5 Gaps ainda abertos (não fechar automaticamente)

```text
DG-01b  NormalizedIdentity (relação com ExternalIdentity/AccountKey) — Round 2
DG-02a/b lifecycle do candidate (expiry/reactivação no novo modelo) — Round 2
DG-04a  classificação terminal/retryable e efeito persistido — Round 2
DG-07a/c fuzzy — Round 2
DG-08a..e efeitos de Review + contrato — Round 2
DG-09a..c Validation→Eligibility — Round 2
DG-09e registo documental
DG-10a/b lifecycle de Eligibility — Round 2
DG-11a output sem fonte elegível — Round 2
DG-12a..d Ordering — Round 2
DG-13a..d Output/publicação — Round 2
DG-21b registo documental (mecanismo de lock)
DG-15a..c Config/permissões (autoridade dual, ACL) — Round 2
DG-16a/b Policy schema/merge — Round 2
DG-18a..f Backup/restore/migration/retention — Round 2
DG-19a/c API contracts — bloqueada
DG-21a..f rastreabilidade documental — bloqueada
```

### D.6 Traceability (A1–A5)

```text
A1 → 13-RUNS.md §8 / 19-FAILURE-MODEL.md §4 → DG-14b, DG-04c → teste: distinguir retry técnico vs reexecução
A2 → 04-PLAYLIST-STREAM.md §4 / 23-DATA-CONTRACTS.md §7 → DG-06c, C1 → teste: canonical/SHA-256/versão
A3 → 04-PLAYLIST-STREAM.md §3 → DG-06a → teste: tabela por campo
A4 → 03-DISCOVERY.md §3 / 32-DOMAIN-SCHEMA.md ProviderAccount → DG-01c/01e, C2 → teste: AccountKey determinístico
A5 → 32-DOMAIN-SCHEMA.md DiscoveryCandidate / 40-ENTITY-LIFECYCLE.md / 33-STATE-MACHINES.md → DG-01a/01d → teste: candidato por Run sem FirstSeen/LastSeen
```

### D.7 Validação efectuada

- `AccountId` isolado: 0 ocorrências; apenas `ProviderAccountId`.
- `FirstSeen`/`LastSeen` no `DiscoveryCandidate`: removidos; `LastSeen` remanescente pertence a `ChannelSource` (legítimo).
- Obrigação de ADR para fingerprint: removida de `04` e `24`.
- `08-VALIDATION.md` hysteresis ADR: substituído por definição normativa (consistente com Q3/A2).
- Coerência `13`↔`19`: dois níveis de retry explícitos em ambos.
- `24-DECISIONS.md` linha stale sobre country data: removida.

**Estado:** Round 1 aplicada. A BÍBLIA 1.3 ainda **não** é declarada; faltam Round 2 e fecho documental (DG-21).

---

## Anexo E — Round 2: decisões aplicadas

> **Natureza:** as decisões Round 2 foram inscritas na BÍBLIA em `docs/Reestructure/`. Código, testes e migrations **não** foram alterados. Nenhum ADR foi criado. Este anexo **reconcilia** os artefactos de análise: inventário de API, contratos, matriz de rastreabilidade e este manifesto. `DG-19` e `DG-21` **não** são fechados.

### E.1 Decision Register (Round 2)

| ID | Decisão aplicada | Estado | DGs/subdecisões fechadas |
|---|---|---|---|
| Q-A | Aquisição: classificação terminal vs retryable e efeito persistido | `CLOSED_WITH_DOC_IMPACT` | DG-04a |
| Q-B | Reconhecimento/Review: fuzzy por policy, efeitos de Resolve/Ignore/reabertura e ficha de API | `CLOSED_WITH_DOC_IMPACT` | DG-07a, DG-07c, DG-08a, DG-08b, DG-08c, DG-08d, DG-08e |
| Q-C | Validação→Eligibility: mapeamento por categoria e recuperação | `CLOSED_WITH_DOC_IMPACT` | DG-09a, DG-09b, DG-09c, DG-10a, DG-10b |
| Q-D | Ordering/Output: unicidade, não listados, estado de publicação e naming/Path | `CLOSED_WITH_DOC_IMPACT` | DG-11a, DG-12a, DG-12b, DG-12c, DG-12d, DG-13a, DG-13b, DG-13c, DG-13d |
| Q-E | Config/Policies: autoridade, ACL e merge | `CLOSED_WITH_DOC_IMPACT` | DG-15a, DG-15b, DG-15c, DG-16a, DG-16b |
| Q-F | Backup/Restore: exclusão de secrets e ordem de migração | `CLOSED_WITH_DOC_IMPACT` | DG-18a, DG-18b, DG-18c, DG-18d |

Fecho complementar de Round 2 (lifecycle/identidade do `DiscoveryCandidate` no modelo por ocorrência): **DG-01b, DG-02a, DG-02b** → `CLOSED_WITH_DOC_IMPACT`.

### E.2 OPEN-HUMAN fechados

Os **19 sub-itens `OPEN-HUMAN`** (decisão humana pendente) ficaram fechados por Round 2; `OPEN-HUMAN = 0`. A distribuição por decisão consta de E.1. `DG-08e` (ficha da família Review) foi fechada pela ficha adicionada em `22-API-CONTRACTS.md`. `DG-19a` e `DG-19c` foram fechados pela adição das 12 fichas de família e do contrato de concorrência/limites de Country. Pela BIBLE AUDIT (Anexo F), `DG-21a`, `DG-21b`, `DG-21e` e `DG-21f` foram fechados. Permanecem apenas `PARAMETER_GAP` (6) e `BLOCKED` (1).

### E.3 Documentos alterados

Decisões inscritas (normativos):

`05-CATALOGUE.md`, `33-STATE-MACHINES.md`, `34-PIPELINE-CONTRACTS.md`, `32-DOMAIN-SCHEMA.md`, `19-FAILURE-MODEL.md`, `08-VALIDATION.md`, `10-ORDERING.md`, `11-OUTPUT.md`, `23-DATA-CONTRACTS.md`, `38-POLICIES.md`, `14-CONFIGURATION.md`, `39-CONFIG-SCHEMA.md`, `35-SECURITY-MODEL.md`, `20-OPERATIONS.md`, `17-SECURITY.md`, `16-PERSISTENCE.md`, `03-DISCOVERY.md`, `40-ENTITY-LIFECYCLE.md`.

Artefactos de reconciliação:

`41-API-INVENTORY.md` (família Country adicionada), `22-API-CONTRACTS.md` (ficha da família Review + 12 fichas de família §9–20 + Country concorrência/limites §21), `46-REQUIREMENT-TRACEABILITY.md` (bloco Q-A..Q-F e bloco DG-19), `BIBLE_IMPLEMENTABILITY_GAP_MANIFEST_1.0.md` (este anexo).

### E.4 Contagens actualizadas

```text
DG-* (73): CLOSED = 66, PARAMETER_GAP = 6, OPEN-HUMAN = 0, BLOCKED = 1, FALSE_GAP = 0
§4 afirmações (23): CLOSED = 10, FALSE_GAP = 13
```

### E.5 Ainda aberto / bloqueado (não fechar)

- **DG-08e** — ficha da família Review: **fechada** (adicionada em `22-API-CONTRACTS.md` §7).
- **DG-19 — Contratos de API:** **fechada** (`DG-19a`, `DG-19c`).
- **DG-21 — Rastreabilidade documental:** `DG-21a`, `DG-21b`, `DG-21d`, `DG-21e`, `DG-21f` **fechados** pela BIBLE AUDIT (Anexo F); `DG-21c` permanece **BLOCKED** — a decisão normativa do mecanismo de secret store continua em falta (ADR-0004 `Proposed`), o que é um `BIBLE_GAP`.

**Estado:** Round 2 aplicada. `DG-19` fechada; BIBLE AUDIT (Anexo F) executada; `DG-21c` (mecanismo de secret store) permanece `BLOCKED`. A BÍBLIA 1.3 ainda **não** é declarada.

---

## Anexo F — DG-21: BIBLE AUDIT do código actual (resultado)

Auditoria read-only de `m3uCrawler/` e `m3uCrawler.Tests/` contra a BÍBLIA. A matriz completa está em `46-REQUIREMENT-TRACEABILITY.md` §BIBLE AUDIT.

### F.1 Quality gates

```text
dotnet build m3uCrawler.sln --configuration Release --no-restore  → PASS (0 erros, 52 avisos)
dotnet test m3uCrawler.Tests --configuration Release --no-build    → PASS (2186 passed, 1 skipped, 0 failed)
```

### F.2 Contagens (itens avaliados na matriz)

```text
itens avaliados na matriz = 58
COMPLIANT  = 15
PARTIAL    = 10
MISSING    = 12
DIVERGENT  = 21
```

O build e o teste passam (2186/2187; 1 skip), mas a conformidade com a BÍBLIA é baixa nas áreas não cobertas: o volume de testes não é evidência de conformidade (BIBLE AUDIT, `46` §BIBLE AUDIT).

### F.3 Primeira divergência

**Discovery — identidade/dedup (`AccountKey`).** `AccountIdentity.cs:11,29-31` deriva `AccountId` de URL com credenciais + username; não existe `DiscoveryCandidate` persistido nem dedup por conta. Contraria `03-DISCOVERY.md:28,30` e `32-DOMAIN-SCHEMA.md:33`. Primeira divergência de segurança imediatamente a seguir: SSRF em aquisição (`HttpClientFactory.cs:67-82`, sem protocolo/IP/DNS/redirect/fail-closed).

### F.4 ADR audit

`ADR-0002` (fingerprint) e `ADR-0006` (migration/rollback) parcialmente absorvidos pela BÍBLIA; `ADR-0004` (secret store) permanece com decisão normativa em falta (`BIBLE_GAP`); `ADR-0005` (baseline/runtime) mantém valor. Nenhum `Proposed` tratado como autoridade.

### F.5 DG-21a..f

```text
DG-21a histerese         — CLOSED (registo em 08); implementação MISSING
DG-21b scheduler locking — CLOSED (registo em 13); mecanismo MISSING
DG-21c ADRs Proposed     — BLOCKED (mecanismo de secret store por decidir)
DG-21d Country inventário— CLOSED
DG-21e matriz            — CLOSED (preenchida com evidência real)
DG-21f re-auditoria ADRs — CLOSED
```

### F.6 O que falta (implementação, não decisão)

As divergências `MISSING`/`DIVERGENT` da matriz são trabalho de implementação a planear em waves, começando pela primeira divergência (Discovery/AccountKey e SSRF). Não são gaps de decisão, excepto `DG-21c` (secret store).

---

## Anexo G — Wave W1: Discovery (AccountKey + DiscoveryCandidate por ocorrência)

Correcção da **primeira divergência** identificada na BIBLE AUDIT. Código alterado; BÍBLIA não alterada.

### G.1 Implementado

- `ProviderEntity` (`providers`), `ProviderAccountEntity` (`provider_accounts`, `AccountKey` único por provider), `DiscoveryCandidateEntity` (`discovery_candidates`, campos `ProviderId`/`ProviderAccountId`/`ExternalIdentity`/`Evidence`/`NormalizedIdentity`/`Status`/`RunId`/`SourceId`; **sem** `FirstSeen`/`LastSeen`).
- `SourceEntity.ProviderAccountId` (FK nullable) — uma conta suporta várias Sources.
- `AccountKey = Provider namespace + external functional identity`; normalização NFKC + trim (não-colapsante); password/URL-com-credenciais excluídos (`AccountIdentity.cs:212-245`).
- Dedup por `(RunId, ProviderAccountId)` (`CatalogResolver.cs:2140`; índice único filtrado em `ChannelCatalogDbContext.cs:349-352`); sem identidade → não deduplica (conservador).
- Candidate→Source explícito com `SourceId` no candidato.
- Migração `20260919114108_AddProviderAccountAndDiscoveryCandidate` — puramente aditiva (sem `DropTable`/`DropColumn`/`DELETE`/`UPDATE` no `Up`); legacy `sources.ProviderAccountId = NULL` documentado como limitação (não inferido).

### G.2 Testes

`W1AccountIdentityModelTests.cs`: T1 (mesma conta numa Run → 1 ocorrência), T2 (contas distintas → não dedup), T3 (evidência insuficiente → preservado sem dedup), T4 (mudança não-funcional não cria outra conta), T5 (password não participa), T6 (mesmo AccountKey em Runs diferentes → candidatos distintos por RunId), T7 (ProviderAccount reutilizado), T8/T8b (migração cria constraints e preserva dados).

### G.3 Quality gates

```text
dotnet build → PASS (0 erros)
dotnet test  → PASS (2195 passed, 1 skipped, 0 failed; baseline 2186 → +9, sem regressões)
```

### G.4 Residual (dentro de W1)

```text
PARTIAL — XtreamAccountLockManager/IAccountWork.AccountId ainda usa identidade legada (URL sanitizada + username);
          serialização continua a usar esta unidade em vez de AccountKey.
PARTIAL — Descobertas não-Xtream (M3U/HTML/attachment) sem identidade funcional estável → sem dedup (conservador).
PARTIAL — DiscoveryCandidate.RunId fisicamente nullable (nem sempre disponível no boundary de ingestão).
PARTIAL — Provider.Capabilities é JSON opaco (a BÍBLIA declara o campo, não o schema).
PARTIAL — Source.Key permanece globalmente único; a composite (ProviderAccount, Source Key) não é materializada separadamente.
```

### G.5 GAP não bloqueante

A BÍBLIA exige um Provider namespace mas não enumera os valores, e o algoritmo de `NormalizedIdentity` foi declarado fechado sem texto explícito. Resolveu-se com `Provider.Key`/`ProviderNamespaces` e NFKC+trim (hard semantics de W1). Mudança de namespace/algoritmo tem pontos únicos de alteração e exigiria migração dos `AccountKey` persistidos.

### G.6 Contagens do Manifest

Inalteradas (W1 é implementação, não fecha DG):

```text
DG-* (73): CLOSED = 66, PARAMETER_GAP = 6, OPEN-HUMAN = 0, BLOCKED = 1, FALSE_GAP = 0
```

`DG-01a`, `DG-01c`, `DG-01d`, `DG-01e` permanecem fechados como decisão; a implementação correspondente passa a `PARTIAL` (não `COMPLIANT`) em `46` até os residuais de G.4 serem resolvidos.

---

## Anexo H — Wave W2: Aquisição segura (SSRF, redirects, retry, failure persistence)

Correcção da divergência de segurança de aquisição identificada na BIBLE AUDIT. Código alterado; BÍBLIA não alterada; nenhum ADR criado.

### H.1 Implementado

- `AddressClassifier` — classificação IPv4/IPv6 (loopback/private/link-local/mapped `::ffff:a.b.c.d`).
- `SsrfGuard` — `http`/`https` apenas; IP literal classificado; resolução de todas as respostas; **qualquer endereço proibido → fail-closed**; DNS failure → fail-closed.
- `SafeConnector` — `SocketsHttpHandler.ConnectCallback` que resolve+valida+liga ao endereço validado; **sem segunda resolução**; hostname preservado (TLS/SNI/Host intactos).
- `GuardedHttpRequest` — `AllowAutoRedirect=false`; redirect manual por hop com revalidação de esquema/host/IP; limite configurável.
- `HttpClientFactory` — `AllowAutoRedirect=false`, `UseProxy=false`, `ConnectCallback=SafeConnector`.
- `AcquisitionFailure`/`AcquisitionFailureObserver` — retry técnico na mesma Run; persistência sanitizada na `Source` e agregação na `Run`.
- `SourceEntity` + Migração `20260919130000_AddSourceAcquisitionFailure` (4 colunas nullable, aditiva).
- `RunReport`/`LiveRunCounts` — 3 contadores de aquisição.
- Consumidores: `M3uTesterService` (probe + download), `TelegramScraperService`, `XtreamPublicationResolver` (IP literal via guard), `M3uCrawlerService` (substituído `new HttpClient()`).

### H.2 Quality gates

```text
dotnet build → PASS (0 errors)
dotnet test  → PASS (2276 passed, 1 skipped, 0 failed; baseline 2195 → +81)
WaveW2 filter → PASS (87 passed, 0 failed)
```

### H.3 PARAMETER_GAP (técnicos)

```text
MaxRedirects, MaxResponseBytes (novos, configuráveis)
MaxRetries, RetryDelayMilliseconds, ConnectionTimeoutSeconds, OverallTimeoutSeconds (reutilizados)
CIDRs adicionais, metadata endpoints extra (continuam em aberto — não hardcoded)
```

### H.4 Fora de scope / residual

```text
Dispatcharr transport — out-of-scope (BaseUrl operador-configured, não aquisição descoberta externamente).
Telegram Source wiring — o boundary de processamento de candidatos não tem entidade Source; o observer está disponível
  mas não ligado aí. Não bloqueia W2.
```

### H.5 Contagens do Manifest

Inalteradas (W2 é implementação):

```text
DG-* (73): CLOSED = 66, PARAMETER_GAP = 6, OPEN-HUMAN = 0, BLOCKED = 1, FALSE_GAP = 0
```

---

## Anexo I — Wave W3: contrato de parsing M3U

Implementação do contrato `DG-03a`/`DG-03b`, já fechados como decisão em **Round 1/Q4** (ver Anexo D e o registo de decisões do proprietário). A BÍBLIA (`docs/Reestructure/`) **não** foi alterada; nenhum ADR foi criado.

### I.1 Contrato implementado

- `#EXTM3U` obrigatório na primeira linha não vazia/não-BOM; ausência → `Failed`, 0 streams, entradas não ingeridas.
- `Status`: `Success` (≥1 válida, 0 malformada/inutilizável) / `Partial` (≥1 válida e ≥1 malformada/inutilizável) / `Failed` (0 válidas ou falha de parsing). `Partial` nunca equivale a sucesso.
- Entrada válida = `#EXTINF` seguido do próximo conteúdo não vazio, um URL absoluto `http`/`https`.
- Malformado registado, excluído e parsing continua (URL sem `#EXTINF`, `#EXTINF` sem URL, entrada incompleta).
- Alvo não utilizável (esquema ≠ http/https) é distinto de malformado.
- Metadados benignos (`#EXT-X-VERSION`, comentários, `#EXTGRP`, `#EXTVLCOPT`, `#KODIPROP`, `#FOO`) não são streams, não geram malformados e não alteram o estado.
- Variantes HLS `#EXT-X-STREAM-INF` preservam o comportamento existente (metadados; `OriginalExtInf` vazio; não é entrada `#EXTINF`).
- Diagnósticos sanitizados por `CredentialSanitizer`.

### I.2 Tipos e implementação

```text
m3uCrawler/Models/M3uPlaylistStatus.cs        (Success|Partial|Failed)
m3uCrawler/Models/M3uParseDiagnostic.cs       (Kind Malformed|UnusableTarget|MissingHeader|ParserFailure)
m3uCrawler/Models/M3uParseResult.cs           (Streams, Status, Diagnostics, contadores)
m3uCrawler/Models/M3uParserOptions.cs         (limites técnicos)
m3uCrawler/Services/M3uParserService.cs       (ParseDetailed; Parse retro-compatível)
```

Consumidores convergentes: `CountryChannelValidator.AnalyzePlaylist` (Failed não ingere; Partial ingere válidas), `TelegramScraperService.ProcessCandidateAsync` (Failed não conta como descarregada/válida; Partial conta `PlaylistsPartial`), `PlaylistReader`, `PlaylistManagerService.LoadFromM3uPlaylist`. Contador aditivo `RunReport.PlaylistsPartial` + espelho em `LiveRunCounts`.

### I.3 Quality gates

```text
dotnet build m3uCrawler.sln --configuration Release --no-restore  → PASS (0 errors, 52 warnings pré-existentes;
                                                                     nenhum warning de ficheiros W3)
dotnet test m3uCrawler.Tests ... --no-build --nologo              → PASS (2302 passed, 1 skipped, 0 failed)
WaveW3 filter                                                     → PASS (26 passed, 0 failed)
```

### I.4 PARAMETER_GAP (técnicos)

```text
M3uParserOptions.MaxDocumentLength, MaxEntries, MaxFieldLength, MaxParsingTime
(doc 04 §6 / DG-04d) — valores continuam PARAMETER_GAP; não hardcoded como normativos.
```

### I.5 Contagens do Manifest

W3 é implementação, não abre nem fecha novos `DG-*`. `DG-03a`/`DG-03b` permanecem como já registado em Round 1/Q4 (Anexo D). `DG-04d` (valores de limites) permanece `PARAMETER_GAP`.


---

## Anexo J — Wave W4: normalização e fingerprint de stream

Implementação do fingerprint canónico versionado e da dedup intra-Source
(`docs/Reestructure/04-PLAYLIST-STREAM.md` §4/§4.1, `32-DOMAIN-SCHEMA.md`,
DL-108). A BÍBLIA foi reconciliada nos documentos indicados; a versão inicial
`sfp1` fica registada. Nenhum ADR foi tratado como autoridade (ADR-0002
permanece `Proposed`).

### J.1 Implementado

- `m3uCrawler/Services/Matching/StreamFingerprint.cs` — canonicalização
  determinística do URL (`sfp1`) e
  `Fingerprint = hex minúsculo de SHA-256(UTF-8("sfp1\n" + canonicalUrl))`.
- `ChannelSourceEntity.Fingerprint` / `FingerprintVersion` (nullable) com
  mapeamento EF; migração
  `20260919152739_AddChannelSourceStreamFingerprint` — 2 colunas nullable +
  1 índice não único; **aditiva** (sem drop/delete/update de dados válidos).
- `CatalogResolver.RecordChannelSourceAsync` — dedup intra-Source por
  `(CanonicalChannelId, SourceId, Fingerprint, FingerprintVersion)`, com
  consolidação de LastSeen/LastTested/metadados; fallback legacy por
  `(CanonicalChannelId, SourceId, StreamUrl sanitizado)` quando o fingerprint
  é nulo. Nunca consolida entre Sources.
- `SourceSelectionStage` e `PlaylistComposerService` — critério 6 de DL-101
  alimentado por `ChannelSource.Fingerprint`; fallback à URL normalizada
  mantido para rows legacy.

### J.2 Contrato e política de credenciais

Representação canónica e política de credenciais fixadas em
`04-PLAYLIST-STREAM.md §4.1`. Em resumo: UTF-8; `scheme`/`host` minúsculos;
ponto final do host removido; porta por omissão removida; fragmento removido;
userinfo nunca incluído; path case-sensitive sem descodificar percent-encoding
(`%2F` ≠ `/`); path Xtream `/live|movie|series/<USER>/<PASS>/<ID>` preserva
`<ID>` e mascara `<USER>`/`<PASS>` como `***`; query remove apenas
`username`/`password`/`token`/`authorization`, preservando os restantes
parâmetros verbatim e pela ordem original. Só o hash e a versão são
persistidos; o URL canónico nunca é persistido.

### J.3 Quality gates

```text
dotnet build m3uCrawler.sln --configuration Release --no-restore --no-incremental → PASS (0 errors, 52 warnings; igual ao baseline)
dotnet test  m3uCrawler.Tests ... --no-build --nologo                              → PASS (2365 passed, 1 skipped, 0 failed; baseline 2302/1/0 → +63)
WaveW4 filter                                                                      → PASS (63 passed, 0 failed)
```

### J.4 Fora de scope / residual

```text
Backfill em massa de rows legacy — permanecem null; fallback na selecção.
Reordenação de query params — não nesta versão (ordem preservada).
Dedup cross-provider/cross-Source — fora de scope por decisão D1.
ADR-0002 continua Proposed; a BÍBLIA é a autoridade normativa.
```

### J.5 Contagens do Manifest

W4 é implementação; reconcilia um conflito documental anteriormente semântico
(`16:27`/`32:207` vs `07:48`) e não abre nem fecha novos `DG-*`.

```text
DG-* (73): CLOSED = 66, PARAMETER_GAP = 6, OPEN-HUMAN = 0, BLOCKED = 1, FALSE_GAP = 0
```

---

## Anexo K — Wave W4.1: resolução fingerprint-aware no SourceSelectionStage

Correcção do bloqueador identificado na revisão pós-W4. Código alterado; BÍBLIA não alterada; nenhum ADR.

### K.1 Problema

A persistência consolida `ChannelSource` por fingerprint (`CatalogResolver.RecordChannelSourceAsync`), mas `SourceSelectionStage` fazia o join apenas por `CredentialSanitizer.SanitizeUrl`. `StreamFingerprint` elimina casing do host, porta default, fragmento e credenciais em query; `SanitizeUrl` preserva-os. Logo `Fingerprint(A)==Fingerprint(B)` com `SanitizeUrl(A)!=SanitizeUrl(B)` produzia `Unmatched` artificial para uma das variantes.

### K.2 Correcção

- `SourceSelectionStage.ApplyAsync` constrói, por execução, um índice adicional `(FingerprintVersion, Fingerprint) → List<ChannelSource>` e resolve cada stream por **precedência**: (1) fingerprint, (2) URL sanitizada (fallback legacy), (3) `Unmatched`/pass-through (`ResolveHits`).
- Mantidos: ambiguidade quando hits têm `>1 CanonicalChannelId` distinto; desempate por menor `Id`; `SourceId` parte da identidade persistente; taxonomia `Matched/Unmatched/Ambiguous/Selected/Published` inalterada; `CredentialSanitizer` continua só para exposição; nenhuma credencial em diagnostics; `StreamFingerprint`/canonicalização/W2/W3 intactos.
- Âmbito: o runtime `M3uStream` não transporta `SourceId`, pelo que o stage permanece source-agnostic como antes; o isolamento entre Sources é garantido na persistência (chave inclui `SourceId`) e a colisão de fingerprint entre canais distintos é `Ambiguous`, não escolha arbitrária.

### K.3 Testes

`WaveW4p1SourceSelectionFingerprintResolutionTests.cs` (12): equivalente com sanitized diferente (A/B), scheme case, porta default, fragmento, query credentials (sem expor segredos), legacy null, fingerprints distintos, colisão entre canais/sources (ambíguo), unmatched/pass-through, determinismo, precedência fingerprint>URL, e candidato com fingerprint persistido.

### K.4 Quality gates

```text
dotnet build → 0 errors (52 warnings, iguais ao baseline)
dotnet test  → 2377 passed, 1 skipped, 0 failed (baseline W4 2365/1/0; +12)
git diff --check → clean (apenas avisos LF→CRLF)
```

### K.5 Contagens do Manifest

W4.1 é correcção de implementação; não abre nem fecha `DG-*`.

```text
DG-* (73): CLOSED = 66, PARAMETER_GAP = 6, OPEN-HUMAN = 0, BLOCKED = 1, FALSE_GAP = 0
```

---

## Anexo L — Wave W5.0: decisões normativas de Recognition / Fuzzy / Review

Wave **documental/design**. Nenhum código, schema, migration, API ou teste de comportamento alterado; nenhum commit. Baseline `8142c0d` (W4.1).

### L.1 Decisões fechadas (D1–D11)

| ID | Decisão | Documentos | Wave de implementação |
|---|---|---|---|
| D1 | `RecognitionPolicy`: scopes `system/default|global|group|channel`, precedência `channel>group>global>default`, snapshot imutável por Run, schema `Enabled`/`Fuzzy.Enabled`/`Fuzzy.Threshold`/`Fuzzy.AmbiguityMargin`/`Fuzzy.Weights` | `38 §5.1`, `32` | W5.1 |
| D2 | Fuzzy **opt-in**; sem candidato→`UNKNOWN`; plausíveis→`AMBIGUOUS`; único acima do threshold pode→`CANONICAL` | `05 §4`, **DL-117** | W5.3 |
| D3 | Threshold/margem/pesos = `PARAMETER_GAP` (não fixar valores) | `38 §5.1`, `32` | W5.3 |
| D4 | Review `Open→InReview→Resolved`, `Open→Ignored`, `Open→InReview→Ignored`; reopen `Resolved/Ignored→Open` auditado; `InReview` = início de tratamento | `33`, DL-105 | W5.4 |
| D5 | API Review normativa = `22 §7`; `Ignore` exige motivo; auditoria before/after; `/api/catalog/reviews/...` é divergência histórica | `22 §7` | W5.5 |
| D6 | `MatchMethod` (método efectivo) e `MatchConfidence` (`0..1`, não é score de fuzzy, não comparável entre métodos sem semântica) normativos, versionados, registo não autoridade | `05 §4.1`, `32` | W5.6 |
| D7 | Nome normalizado = passo próprio (3), distinto de alias (4) | `05 §4/§4.1`, `34` | W5.2 |
| D8 | `IdentityRule` explícita (`Review`/`Excluded`), não excepção silenciosa | `05 §4.1`, `34` | W5.2 |
| D9 | Namespace de provider respeitado na comparação de identidade externa; sem novo scope | `05 §4.1`, `32` | W5.2 |
| D10 | P6 = `Canonical|Unknown|Ambiguous|Excluded`; `Rejected` não é P6; `Excluded ≠ Unknown/Ambiguous` | `34`, `05 §4.1` | W5.2 |
| D11 | `Ambiguous` qualificado por `Stage` (Recognition vs Selection) | `05 §4.1`, `34` | W5.2 |

### L.2 Conflitos C1–C11 reconciliados

```text
C1  fuzzy default-active → BÍBLIA fixa opt-in (D2, DL-117). Código DIVERGENT (W5.3).
C2  thresholds hardcoded → PARAMETER_GAP (D3). Código DIVERGENT (W5.3).
C3  curated ambiguity (legacy) sem ReviewItem → BÍBLIA mantém Review. O motor legacy
    (`ChannelMatcher`) está fora de scope (OPEN-D2); NÃO é entregue em W5.3 nem em W5.4.
C4  Review states sem InReview/reopen → BÍBLIA fixa lifecycle (D4, DL-105). Código DIVERGENT (W5.4).
C5  rotas Review divergentes → 22 §7 normativa; alinhar em W5.5.
C6  MatchConfidence/MatchMethod sem base → normativizados (D6) em 05/32.
C7  ReviewItem schema divergente → W5.4 reconcilia apenas o mínimo necessário ao
    lifecycle (33/DL-105/DL-119), mantendo 32 como autoridade. A reconciliação
    completa dos campos conceptuais (RunId/StreamId/Actor/Evidence/Candidates/
    Decision) fica separada (W5.6, L.4), sem inventar contrato inexistente.
C8  IdentityRule/nome normalizado → documentados (D7/D8) em 05/34.
C9  roadmap auto-create → marcado SUPERSEDED em docs/IMPLEMENTATION_ROADMAP.md.
C10 gates Q3/Q4 sobrestimados → estado ajustado em 44.
C11 Open→Ignored directo → explicitado em 33/DL-105 (InReview não obrigatório).
```

### L.3 PARAMETER_GAP restantes

```text
- RecognitionPolicy.Fuzzy.Threshold
- RecognitionPolicy.Fuzzy.AmbiguityMargin
- RecognitionPolicy.Fuzzy.Weights (métrica/pesos)
- defaults por campo (excepto Fuzzy.Enabled=false fixo)
```

### L.4 Deliberadamente fora desta wave (W5.1–W5.7)

```text
W5.1 RecognitionPolicy (entidade/schema/snapshot/persistência)
W5.2 Ordem de reconhecimento (namespace, nome normalizado, IdentityRule, Excluded, Ambiguous por Stage)
W5.3 Fuzzy gate + below-threshold + diagnóstico (CatalogResolution; SEM ReviewItem)
W5.4 Review lifecycle (InReview/Resolved/Ignored + reopen) + ReviewItem de fuzzy
     ambiguity (a partir de CatalogResolution.FuzzyDiagnostic) + migração mínima
W5.5 Review API (22 §7) + motivo obrigatório + auditoria
W5.6 MatchMethod/MatchConfidence (semântica versionada; C6) e fronteira C7 documentada
     (DL-121: C7 não é inventado sem contrato implementável)
W5.7 Traceability/gates/docs (44, 46, manifest)
```

### L.5 Contagens do Manifest

W5.0 é decisão documental; não abre nem fecha `DG-*`. As linhas de implementação (`46:118-125,159`) permanecem `DIVERGENT`/`PARTIAL`/`MISSING` até W5.1–W5.6.

```text
DG-* (73): CLOSED = 66, PARAMETER_GAP = 6, OPEN-HUMAN = 0, BLOCKED = 1, FALSE_GAP = 0
```

---

## Anexo M — Wave W5.1: RecognitionPolicy implementada

Implementação de D1 (policy + resolução + snapshot). Nenhum comportamento de reconhecimento, fuzzy, Review, SourceSelection, fingerprint, Eligibility, Ordering, Output ou Dispatcharr foi alterado.

### M.1 Entregue

- `RecognitionPolicyEntity` (`recognition_policies`): `ScopeKey` único (`system`/`global`/`group:{key}`/`channel:{key}`), `CanonicalChannelKey`, `GroupKey`, `Enabled`, `FuzzyEnabled` (default `false`), `FuzzyThreshold?`, `FuzzyAmbiguityMargin?`, `FuzzyWeightsJson?`, `Version`, timestamps.
- `RecognitionPolicySnapshotEntity` (`recognition_policy_snapshots`): `RunId` único, `ResolverVersion`, `PoliciesJson`, `ResolvedAtUtc`; imutável (create-if-absent).
- `Services/Recognition/`: `RecognitionPolicy.cs` (modelo + defaults + scopes), `RecognitionPolicySet.cs` (resolução `channel → group → global → system/default`), `RecognitionPolicyResolver.cs` (`LoadEffectivePoliciesAsync`, `ResolveEffectiveAsync`, `CreateSnapshotAsync`, `GetSnapshotAsync`; `ResolverVersion = rp1`).
- `CatalogResolver`: `List/Get/Upsert/DeleteRecognitionPolicyAsync` (+ conveniências global/group/channel), `Get/SaveRecognitionPolicySnapshotAsync`; upsert incrementa `Version` e escreve `AuditRecordEntity` (`catalog.recognition-policy.upsert|delete`) na mesma transacção.
- Migração aditiva/reversível `20260920093237_AddRecognitionPolicy` (2 `CreateTable` + 2 índices únicos; `Down` remove as tabelas).

### M.2 Testes

`WaveW51RecognitionPolicyTests.cs` (17): system default; global; group>global; channel>group; fallback; determinismo; fuzzy off; round-trip de parâmetros; snapshot estável após alteração; versões coexistentes; auditoria; migração Up/Down; preservação de dados; isolamento channel/group; global não sobrepõe channel; snapshot não mutado por policy posterior.

### M.3 Quality gates

```text
dotnet build → 0 erros (52 avisos = baseline)
dotnet test  → 2394 passed, 1 skipped, 0 failed (baseline 2377/1/0; +17)
git diff --check → clean (apenas avisos LF→CRLF)
```

### M.4 PARAMETER_GAP / limitações

```text
PARAMETER_GAP: Fuzzy.Threshold, Fuzzy.AmbiguityMargin, Fuzzy.Weights e defaults de campo
               (excepto Fuzzy.Enabled=false, normativo).
Limitação: policy de scope mais específico substitui por inteiro (sem merge campo-a-campo nesta wave).
M.4: snapshot é criado quando CreateSnapshotAsync é invocado; o wiring ao RunCoordinator
     (chamada automática no início do Run) permanece:
       Status: OPEN
       Wave: não atribuída
     NÃO pertence a W5.4 (D-W54-03), W5.5 nem W5.6. Não alterar o pipeline para tornar
     o fuzzy automaticamente alcançável.
```

### M.5 Contagens do Manifest

W5.1 é implementação; não abre nem fecha `DG-*`.

```text
DG-* (73): CLOSED = 66, PARAMETER_GAP = 6, OPEN-HUMAN = 0, BLOCKED = 1, FALSE_GAP = 0
```

---

## Anexo N — Wave W5.2: ordem de reconhecimento implementada

Implementação de D7/D8/D9/D10/D11 (ordem P6, nome normalizado como passo
próprio, `IdentityRule` explícita, namespace de identidade externa e
`Ambiguous` de Recognition). Fuzzy continua inactivo; Review lifecycle e
Selection não foram tocados.

### N.1 Entregue

- `Services/Recognition/RecognitionOutcome.cs`: `Canonical | Unknown |
  Ambiguous | Excluded | Review` (P6 + decisão de regra).
- `Services/Recognition/RecognitionMatchMethods.cs`: conjunto mínimo e
  versionado `ExternalIdentityExact`, `TvgIdExact`, `CanonicalExact`,
  `NormalizedName`, `KnownAlias`, `ExplicitHeuristic`, `Fuzzy`,
  `ManualReview` (registo, não autoridade).
- `CatalogResolver.ResolveAsync` ganhou o núcleo determinístico
  `(normalizedIdentity, originalTvgId, RecognitionPolicy? policy, ct)`.
  Os overloads anteriores mantêm-se e delegam com `policy: null`.
  Ordem: `IdentityRule` → identidade externa exacta (namespace respeitado;
  conflito global → `Ambiguous`, nunca "primeiro") → `CanonicalExact`
  (Key normalizada) → `NormalizedName` (DisplayName normalizado) →
  `KnownAlias` → `ExplicitHeuristic` (AffinityMember Kind=Channel,
  Key autoritativa) → gate fuzzy (não executado) → `Unknown`.
  Ties em cada passo → `Ambiguous`.
- `CatalogResolution`: `MatchMethod` e `PolicyVersion` (init-only) e
  `Outcome` derivado do caminho. `FromCanonical` aceita método;
  `FromRule` marca `ManualReview`. `PolicyVersion` preenchido em todos os
  resultados.
- `RecognitionPolicyResolver`: `GetSnapshotSetAsync` /
  `GetSnapshotPolicyAsync` + `DeserializeSnapshot` (`System.Text.Json`),
  que consomem o snapshot **persistido** (formato inalterado).
- `PipelineIngestionService`: `MatchMethod` efectivo de
  `resolution.MatchMethod` (fallback histórico `canonical-alias`).

### N.2 Contrato de ordem (mapeamento MatchMethod)

```text
1. identidade externa exacta      → TvgIdExact | ExternalIdentityExact
2. tvg-id/canonical/provider      → idem (namespace decide o método)
3. nome normalizado (passo próprio)→ NormalizedName
4. alias conhecido                → KnownAlias
5. heurística explícita           → ExplicitHeuristic
6. fuzzy (opt-in)                 → Fuzzy (NÃO executado em W5.2)
7. Review (IdentityRule)          → ManualReview
   ambíguo                        → Ambiguous (sem MatchMethod)
   sem evidência                  → Unknown
```

### N.3 PARAMETER_GAP / OPEN

```text
PARAMETER_GAP: Fuzzy.Threshold, Fuzzy.AmbiguityMargin, Fuzzy.Weights e
               desempate fuzzy (pertence a W5.3). Não inventados em W5.2.
OPEN / INFERENCE: passo ExplicitHeuristic mapeia para AffinityMember
                  (Kind=Channel) → AffinityGroup → CanonicalChannelKey;
                  é a heurística explícita existente. A BÍBLIA não nomeia
                  o mecanismo concreto.
OPEN (custo): NormalizedName/CanonicalExact fazem scan em memória dos
              canais activos (Id/Key/DisplayName) por não existir coluna
              persistida normalizada; candidato escolhido é re-obtido.
CONFLITO resolvido: teste existente `Existing_raw_alias_becomes_matchable...`
              assumia pré-W5.2 (só alias resolvia); adaptado preservando o
              intent (alias cujo valor normalizado não colide com Key/DisplayName).
```

### N.4 Evidência

```text
dotnet build → 0 erros (52 avisos = baseline)
dotnet test  → 2413 passed, 1 skipped, 0 failed (baseline W5.1 2394; +19 W5.2)
git diff --check → clean (apenas avisos LF→CRLF)
WaveW52RecognitionOrderTests: 19 testes (ordem, MatchMethod, Ambiguous
nunca-primeiro, IdentityRule Excluded, namespace, snapshot persistido,
fuzzy off).
```

### N.5 Contagens do Manifest

W5.2 é implementação; não abre nem fecha `DG-*`. Fuzzy (W5.3), Review
(W5.4/W5.5) e `MatchMethod`/`MatchConfidence` versionados (W5.6) mantêm-se
como waves seguintes.

```text
DG-* (73): CLOSED = 66, PARAMETER_GAP = 6, OPEN-HUMAN = 0, BLOCKED = 1, FALSE_GAP = 0
```

---

## Anexo O — Wave W5.3: passo fuzzy de Recognition implementado

Implementação do passo 6 (fuzzy) de `CatalogResolver.ResolveAsync` e
ratificação documental do contrato `48-RECOGNITION-FUZZY-CONTRACT.md`
(F1–F14). Nenhuma migration, alteração de Review lifecycle/API, de ordem W5.2
ou do motor legacy `ChannelMatcher`/`MatchScorer`.

### O.1 Entregue

- `Services/Recognition/FuzzyRecognition.cs`: `FuzzyDecisionReasons`,
  `FuzzyCandidate`, `FuzzyCandidateDiagnostic`, `FuzzyRecognitionDiagnostic`,
  `FuzzyDecisionKind`, `FuzzyRecognitionDecision` e
  `FuzzyRecognitionEvaluator` (F5–F10). Reutiliza `FuzzyMatcher` e
  `ChannelNormalizer`; score `0..100`.
- `CatalogResolver.ResolveAsync` passo 6: substitui o placeholder por geração
  de candidatos (canais activos + aliases; projecção estendida) → avaliação
  fuzzy → `Canonical` (`MatchMethod=Fuzzy`), `Ambiguous`
  (`fuzzy-ambiguous`/`fuzzy-below-threshold`) ou `Unknown`, sempre com
  `PolicyVersion`. Fail-closed com diagnóstico quando o threshold é nulo/
  inválido.
- `CatalogResolution`: `FuzzyScore` (`int?`, `0..100`) e `FuzzyDiagnostic`
  (`FuzzyRecognitionDiagnostic?`), propriedades `init`-only aditivas; sem
  quebra de consumidores. Não é `MatchConfidence` (W5.6).
- Ratificação: `05 §4.2`, `32`, `38 §5.1`, DL-118, `46` (secção W5.3), `48`
  (estado IMPLEMENTADO).

### O.2 Decisões de scope

```text
OPEN-D2 RESOLVIDO: legacy matcher (ChannelMatcher/MatchScorer/MatchingOptions)
                   FORA de scope; permanece DIVERGENT (C2). Waves futuras.
OPEN-D3 RESOLVIDO: diagnóstico via CatalogResolution.FuzzyScore/FuzzyDiagnostic.
OPEN-N1 / OPEN-P2: fora do v1 (normalização por idioma; CandidateFloor).
PARAMETER: Fuzzy.Threshold, Fuzzy.AmbiguityMargin, Fuzzy.Weights (valores).
```

### O.3 Evidência

```text
dotnet build → 0 erros (52 avisos = baseline)
dotnet test  → 2448 passed, 1 skipped, 0 failed (baseline W5.2 2413/1/0; +35)
WaveW53FuzzyRecognitionTests: 35 (28 casos do contrato + integração + diagnóstico)
git diff --check → clean (apenas avisos LF→CRLF pré-existentes)
```

Nota: numa execução intermédia, o teste de concorrência pré-existente
`Phase93AccountGateCoordinatorTests.H_bounds_concurrency_even_with_unbounded_callers`
falhou com `ObjectDisposedException` sob carga paralela; passa isolado e na
reexecução completa, não sendo relacionado com W5.3.

### O.4 Contagens do Manifest

W5.3 é implementação; não abre nem fecha `DG-*`. Review (W5.4/W5.5),
`MatchMethod`/`MatchConfidence` versionados (W5.6) e a reconciliação do motor
legacy mantêm-se como waves seguintes.

```text
DG-* (73): CLOSED = 66, PARAMETER_GAP = 6, OPEN-HUMAN = 0, BLOCKED = 1, FALSE_GAP = 0
```

---

## Anexo P — W5.4: scope ratificado e implementado

Ratificação documental das decisões de scope identificadas na investigação W5.4
e respectiva implementação. **Sem migration** (a coluna `State` é `INTEGER` sem
CHECK; os valores `0/1/2` preservam-se e `InReview=3` é novo). Sem alteração de
API HTTP (W5.5), de `MatchMethod`/`MatchConfidence` (W5.6), de M.4 nem do motor
legacy.

```text
W5.4: Scope ratified / Implementation complete
```

### P.1 Decisões ratificadas

| ID | Decisão | Contrato |
|---|---|---|
| D-W54-01 | C7 / schema `ReviewItem`: W5.4 altera o `ReviewItem` apenas no mínimo necessário ao lifecycle. `RunId`, `StreamId`, `Actor`, `Evidence`, `Candidates`, `Decision` **não** são inventados; onde indispensáveis ficam `OPEN`. A reconciliação completa do schema permanece separada (W5.6, L.4). | DL-119; `32` |
| D-W54-02 | `ReviewItem` de fuzzy ambiguity pertence a **W5.4**, criado a partir de `CatalogResolution.FuzzyDiagnostic.DecisionReason = fuzzy-ambiguous`. W5.3 continua sem criar `ReviewItem`. | DL-119; `48 §12` |
| D-W54-03 | **M.4 não pertence a W5.4.** Permanece `OPEN` e **sem wave atribuída**; não é atribuído a W5.5/W5.6. | `M.4` |
| D-W54-04 | Reopen de W5.4 é apenas `Resolved→Open` e `Ignored→Open`, por operação administrativa explícita, auditada e **justificada** pelo operador. Deteção automática de "evidência materialmente incompatível" **não** faz parte de W5.4 e o critério permanece `OPEN`. | DL-119; DL-105 |
| D-W54-05 | W5.4 = domínio/persistência/lifecycle/serviço interno/auditoria; **sem novas rotas HTTP**. W5.5 = API HTTP (`22 §7`). | DL-119; `22 §7` |

### P.2 Estados do ReviewItem (ratificados)

```text
Open | InReview | Resolved | Ignored
legacy: Approved → Resolved ; Excluded → Ignored
```

`InReview` é o novo estado. `33`/DL-105 mantêm-se como autoridade das transições.

### P.3 Contradições corrigidas

```text
W5.3 "+ ReviewItem de ambiguidade"      → removido; W5.3 só reconhecimento + diagnóstico (L.4)
W5.3 cria ReviewItem de fuzzy ambiguity → W5.4 cria (D-W54-02; L.4; 48 §12)
C7 W5.4 vs W5.6                         → W5.4 mínimo; reconciliação completa em W5.6 (C7, L.4)
M.4 atribuído a W5.4                    → M.4 OPEN / sem wave atribuída (D-W54-03; M.4)
reopen automático vs manual             → W5.4 manual/auditado/justificado; automático OPEN (D-W54-04)
```

`W5.6` continua responsável por `MatchMethod`/`MatchConfidence`; não existe conversão
`FuzzyScore 0..100 → MatchConfidence 0..1`. O motor legacy
(`ChannelMatcher`/`MatchScorer`/`MatchingOptions`) permanece fora de scope.

### P.4 Implementação (W5.4)

```text
Código:  Services/Catalog/ReviewLifecycle.cs (máquina pura, 6 transições)
         Services/Catalog/ReviewLifecycleModels.cs
         CatalogResolver: BeginReviewAsync/ResolveReviewAsync/IgnoreReviewAsync/
                          ReopenReviewAsync (auditadas via IAuditService opcional);
                          Approve/Exclude/ApplyReviewApproval adaptados (Resolved/Ignored)
         CatalogEntities: ReviewItemState { Open=0, Resolved=1, Ignored=2, InReview=3 }
         PipelineIngestionService.AmbiguousReasonSignature (fuzzy-ambiguous → reasonSignature)
         WebDashboardService: stats/badges de estado (sem novas rotas)
         Program.cs: CatalogResolver com AuditService
Testes:  WaveW54ReviewLifecycleTests.cs (42)
Evidência: build 0 erros; suite 2490 passed / 1 skipped / 0 failed
Migration: nenhuma necessária (State INTEGER sem CHECK; valores preservados)
```

### P.5 Contagens do Manifest

W5.4 é ratificação de scope; não abre nem fecha `DG-*`.

```text
DG-* (73): CLOSED = 66, PARAMETER_GAP = 6, OPEN-HUMAN = 0, BLOCKED = 1, FALSE_GAP = 0
```

---

## Anexo Q — W5.5: API HTTP de Review (W5.5 implementada)

Ratificação (DL-120) e implementação subsequente das cinco rotas de `22 §7` no
handler manual de `WebDashboardService.cs`. Sem migration, sem alteração das rotas
legacy, sem RBAC, sem `MatchMethod`/`MatchConfidence`/C7/M.4.

```text
W5.5: Contract ratified / Implementation complete (1 resolve gap: externalIdentity/channelSource/none)
```

### Q.1 Decisões ratificadas

| ID | Decisão | Contrato |
|---|---|---|
| D1 | `POST /api/review/resolve` = "declaração explícita de alteração + resolução"; termina em `Resolved` (`Open→InReview→Resolved` ou `InReview→Resolved`); nunca cria identidade implicitamente; `422 declared-change-invalid`; usa apenas capacidades de domínio existentes; audit before/after. | `22 §7.3`; DL-120 |
| D2 | Não se cria `/api/review/begin`; `InReview` é alcançado implicitamente por composição no `resolve`/`ignore`. As cinco operações de `22 §7` permanecem as únicas. | `22 §7`; DL-120 |
| D3 | `ReviewItem.Id` = identidade normativa (`id`/`reviewItemId`); `Fingerprint` só para compatibilidade legacy; sem schema/migration. | DL-120 |
| D4 | Formato `{error,message,correlationId}` nas cinco novas rotas; códigos estáveis; `400/401/403/404/409/422/500`; sem `Exception.Message`; `correlationId` opaco por pedido; legacy não normalizado; sem subsistema novo de tracing. | `22 §2/§7`; DL-120 |
| D5 | Rotas legacy mantidas sem alteração de semântica; novas rotas podem reutilizar serviços internos; remoção/depreciação definitiva é decisão futura. | DL-120 |
| D6 | `state ∈ {Open,InReview,Resolved,Ignored}`; inválido → `400 invalid-filter`; `offset` default `0`; `limit` default/máximo = `PARAMETER GAP`; ordem `CreatedAtUtc DESC, Id DESC`; rate limiting `OUT OF SCOPE`/`PARAMETER GAP`. | `22 §7.1/§8`; DL-120 |
| D7 | `subject = NormalizedIdentity`; **não** criar `RunId` em `ReviewItemEntity`; `runId` = dependência W5.6/C7, tratada como limitação actual (não solução definitiva). | `22 §7.1`; DL-120 |
| AuthZ | Mutações usam o modelo actual de `Administrator`; sem migration de roles nem ACL/`Operator`; RBAC fora de W5.5. | `22 §7`; `35`; DL-120 |

### Q.2 Scope ratificado

```text
IN:  5 rotas 22 §7; auth/CSRF existentes; authz Administrator; novo formato de erro;
     correlationId opaco; filtro state; paginação; leitura por ReviewItem.Id;
     integração ReviewLifecycle W5.4; audit; idempotência; respostas sanitizadas; testes HTTP.
OUT: /api/review/begin; remoção/semântica das rotas legacy; RBAC/Operator; migration de roles;
     MatchMethod/MatchConfidence; C7 completo; RunId em ReviewItem; M.4; reabertura automática;
     subsistema de tracing/correlation; rate limiting; alterações especulativas ao catálogo.
```

### Q.3 Dependências e gaps de implementação

Capacidades de domínio existentes verificadas (reutilizadas ou avaliadas; **não** inventar):

```text
channelAlias     : ApplyReviewApprovalAsync(AddAlias)  → SUPORTADO no resolve
canonicalChannel : ApplyReviewApprovalAsync(CreateChannel) → SUPORTADO no resolve
externalIdentity : RecordExternalIdentityAsync (CatalogResolver.cs:403) → NÃO suportado como declaração de Review
channelSource    : RecordChannelSourceAsync    (CatalogResolver.cs:2775) → NÃO suportado como declaração de Review
```

`W5.5 IMPLEMENTATION GAP` (FACT, reportado na implementação): apenas `channelAlias` e
`canonicalChannel` têm operação de domínio de Review **declarada** demonstrada
(`ApplyReviewApprovalAsync`). `externalIdentity` e `channelSource` só dispõem de
gravadores de ingestão com parâmetros sem contrato de declaração, e `none` não tem
semântica definida em `22 §7.3`; os três são rejeitados com `422 declared-change-invalid`.
Não foi criado subsistema novo nem domínio especulativo.

`PARAMETER GAP`: `limit` default/máximo; rate limiting. `W5.6/C7`: `RunId`/schema completo do `ReviewItem`.

### Q.4 Implementação (W5.5)

```text
Código:  WebDashboardService.cs — IsReviewApiPath/HandleReviewApiAsync +
         HandleReviewListAsync/DetailAsync/IgnoreAsync/ReopenAsync/ResolveAsync,
         WriteReviewApiErrorAsync, WriteReviewDomainErrorAsync, ReviewApiPayload.
         CatalogResolver.GetReviewItemAsync (leitura por ReviewItem.Id).
Rotas:   GET /api/reviews; GET /api/review?id; POST /api/review/resolve|ignore|reopen.
Legacy:  /api/catalog/reviews[/{fingerprint}/approve|exclude] inalteradas.
Testes:  WaveW55ReviewApiTests.cs (42, inclui B1/B2).
Evidência: build 0 erros; suite 2532 passed / 1 skipped / 0 failed.
B1/B2:   500 persistence-error no dispatcher; envelope {error,message,correlationId} nos
         401/403 das Review APIs via gate localizado (legacy inalterado).
Migration: nenhuma.
Gap: resolve — externalIdentity/channelSource/none → 422 (W5.5 IMPLEMENTATION GAP).
```

### Q.5 Contagens do Manifest

W5.5 é implementação; não abre nem fecha `DG-*`.

```text
DG-* (73): CLOSED = 66, PARAMETER_GAP = 6, OPEN-HUMAN = 0, BLOCKED = 1, FALSE_GAP = 0
```

---

## Anexo R — W5.6: contrato + especificação de MatchMethod/MatchConfidence ratificados e implementados

Ratificação documental do contrato (DL-121) e da especificação normativa
`49-W56-MATCH-CONFIDENCE-SPECIFICATION.md` (DL-122), agora **implementada** em código, schema e
testes: tabela método→confidence centralizada em `RecognitionMatchMethods`; `CatalogResolution`
transporta `MatchConfidence` (`double?`, init-only); a Recognition produz o valor por método; o
pipeline persiste sem recalcular (o `const 1.0` foi removido); `ChannelSource` tem a coluna
`MatchSemanticsVersion="msm1"` (migration `AddMatchSemanticsVersionAndNullableMatchConfidence`) e
`MatchConfidence` nullable; o endpoint manual legacy valida `MatchMethod`/`MatchConfidence` na
camada HTTP (OD-E). Valores/versão/política: Anexo S.

```text
W5.6: Contract + Specification ratified / Implementation complete
```

### R.1 Decisões ratificadas

| ID | Decisão | Contrato |
|---|---|---|
| D-W56-01 | Semântica de `MatchMethod`/`MatchConfidence` **versionada**; a versão identifica as regras/algoritmo e não é propriedade arbitrária por `ChannelSource`; mecanismo físico na especificação W5.6 (precedentes `sfp1`/DL-108, policy `Version`/DL-110). | DL-121 |
| D-W56-02 | `MatchConfidence` `double 0..1` **method-specific**; sem escala global; regra normativa própria por método (8 valores); valores concretos `OPEN`. | `05 §4.1`; `32:218`; DL-121 |
| D-W56-03 | `FuzzyScore ≠ MatchConfidence`; proibida a conversão `0..100 → 0..1`. | DL-119; `48`; `43 #24` |
| D-W56-04 | `Unknown`/`Ambiguous` → `MatchConfidence = null` (nunca `0`/melhor `FuzzyScore`). | DL-121 |
| D-W56-05 | Producer = Recognition; `CatalogResolution` transporta `MatchMethod`+`MatchConfidence`; pipeline persiste sem recalcular; `const 1.0` = divergência a corrigir. | DL-121 |
| D-W56-06 | C7 sem contrato implementável **não** é inventado; M.4 `OUT` (`W5.6 ≠ M.4`). | `32:163`; `C7`; `M.4` |
| D-W56-07 | Endpoint manual valida `MatchMethod` (8 valores), `MatchConfidence` (`0..1`) e versão; compatibilidade preservada. | DL-121 |

### R.2 Scope

```text
IN (implementado): semântica method-specific de MatchConfidence; valores da tabela
   normativa; versão `MatchSemanticsVersion="msm1"` persistida; produção em Recognition
   (CatalogResolution); pipeline apenas persiste; validação/compatibilidade do endpoint manual
   (OD-E); testes; reconciliação documental C6.
OUT: M.4; motor legacy (ChannelMatcher/MatchScorer/MatchingOptions); conversão FuzzyScore→MatchConfidence;
   campos C7 sem contrato (RunId/StreamId/Actor/Evidence/Candidates/Decision); API W5.5;
   persistência nova sem contrato.
OPEN: reconciliação documental C7 (permanece C7 `OPEN`/limitado); M.4 `OUT`.
```

### R.3 Contagens do Manifest

W5.6 é ratificação de contrato + especificação; não abre nem fecha `DG-*`.

```text
DG-* (73): CLOSED = 66, PARAMETER_GAP = 6, OPEN-HUMAN = 0, BLOCKED = 1, FALSE_GAP = 0
```

---

## Anexo S — W5.6: valores, versão e política do endpoint manual ratificados (DL-122) e implementados

Ratificação das decisões `OPEN` de DL-121, fixadas na especificação normativa
`49-W56-MATCH-CONFIDENCE-SPECIFICATION.md`. **Implementado** em código/schema/testes (ver Anexo R;
`WaveW56MatchConfidenceTests`).

| ID | Decisão ratificada |
|---|---|
| OD-A | `MatchMethod=ExplicitHeuristic` → `MatchConfidence = 0.80`. |
| OD-B | `MatchMethod=Fuzzy` → `MatchConfidence = 0.60`; **sem** cálculo a partir de `FuzzyScore` (proibido `FuzzyScore/100` ou equivalente); `FuzzyScore` permanece `0..100` diagnóstico. `Ambiguous → null`; `Unknown → null`. |
| OD-C | `MatchMethod=ManualReview` → `MatchConfidence = 1.0` ("explicitamente confirmado via review"; não certeza matemática; sem comparabilidade global). |
| OD-D | `MatchSemanticsVersion = "msm1"`, **persistida em `ChannelSource`**; proveniência derivada do algoritmo; rows novas recebem `"msm1"`; não exigida de clientes legacy do endpoint manual (servidor atribui a corrente); **não** usar `RecognitionPolicy` como substituto; distinta de `FingerprintVersion`. |
| OD-E | Endpoint manual: omissão de `MatchMethod` preserva comportamento; se fornecido, aceitar só os 8 valores; `MatchConfidence` só `double 0..1`; ambos → validar combinação; versão não exigida; inválidos → erro de validação sem persistência parcial; endpoints legacy inalterados. |

Tabela normativa dos 8 métodos:

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

Regra global implementada: `double 0..1`; method-specific; **sem** escala global comparável (nunca
ordenar métodos pelo número); `Unknown`/`Ambiguous → null`; `FuzzyScore != MatchConfidence`. C7
permanece `OPEN`/limitado e M.4 permanece `OUT`. `44` Q4 passa a **satisfeito** para a semântica de
matching de W5.6 (evidência: `WaveW56MatchConfidenceTests`, migration
`AddMatchSemanticsVersionAndNullableMatchConfidence`).
