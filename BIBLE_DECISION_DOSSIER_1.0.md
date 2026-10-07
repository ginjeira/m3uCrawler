# BIBLE DECISION DOSSIER 1.0
<!--
NOTA DE RECUPERAÇÃO E CLASSIFICAÇÃO (Wave 0.5, 2026-09-21)
============================================================
Este dossier foi recuperado do commit órfão `a60c09b` (stash untracked,
2026-09-21 08:22:03+01:00).

CLASSIFICAÇÃO:
- **NÃO NORMATIVO** — este documento NÃO constitui fonte normativa do
  projecto.
- **DOSSIER PREPARATÓRIO** — é uma compilação read-only de opções
  (DG-01..DG-20) apresentadas ao proprietário para decisão. Foi
  produzido antes de qualquer decisão proprietária sobre DG-01..DG-20
  ter sido tomada.
- **DG-01..DG-20** — todas as decisões permanecem em estado `PENDING`.
  Nenhuma alternativa A/B/C foi escolhida.
- **Não constitui decisão proprietária** — nenhuma secção deste dossier
  substitui ou altera a autoridade da BÍBLIA.
- **Não altera a autoridade da BÍBLIA** — a BÍBLIA 1.2 (este directório,
  em particular `00-BIBLE.md`) é e continua a ser a fonte normativa de
  verdade do projecto.

REFERÊNCIAS NORMATIVAS VIGENTES (HEAD):
- `docs/Reestructure/00-BIBLE.md` — constituição normativa (versão 1.2).
- `docs/Reestructure/31-DECISION-LOCK.md` — DL-001..126, ratificados.
- `docs/Reestructure/46-REQUIREMENT-TRACEABILITY.md` — matriz de
  rastreabilidade.
- `docs/Reestructure/48-RECOGNITION-FUZZY-CONTRACT.md` — W5.3 (fuzzy).
- `docs/Reestructure/49-W56-MATCH-CONFIDENCE-SPECIFICATION.md` — W5.6
  (match confidence).
-->


> **Objectivo:** permitir ao proprietário tomar as 20 decisões funcionais `DG-01..DG-20` de forma informada e consistente, antes de produzir a BÍBLIA 1.3.
> **Modo:** preparação read-only. Nenhuma decisão é tomada aqui. Nenhum documento normativo, ADR, código ou teste foi alterado. O único ficheiro criado é este dossier.
> **Base:** `BIBLE_IMPLEMENTABILITY_GAP_MANIFEST_1.0.md` (auditoria reconciliada) + leitura directa dos documentos normativos em `docs/Reestructure/`.
> **Convenções:**
> - `DL-XXX` = Decision Lock, `docs/Reestructure/31-DECISION-LOCK.md` (DL-001..DL-116). Uma alternativa que contrarie um DL é marcada **INCOMPATÍVEL COM DECISION LOCK**.
> - `INFERENCE` = consequência não literal na BÍBLIA, derivada por inferência técnica.
> - ADR é **evidência documental**, nunca decisão funcional. `Proposed` não é normativo.
> - Não são decididos valores numéricos (timeouts, retries, thresholds, CIDRs, janelas, formatos concretos).

---

## 1. Scope

Fichas de decisão para as 20 decisões funcionais identificadas. Cada ficha permite responder: *o que estou a decidir, quais as consequências, que partes da BÍBLIA serão afectadas*. `DG-21` (rastreabilidade documental) não é decisão funcional e não consta como ficha; é referido como efeito de fecho.

---

## 2. Como usar este dossier

1. Ler a §4 (grafo de dependências) e a §5 (ordem recomendada).
2. Para cada DG, responder apenas à **§11 — Resposta esperada do proprietário**.
3. Decidir uma alternativa (A/B/…) ou determinar uma terceira, desde que compatível com os DL citados na **§4 — O que NÃO pode ser alterado**.
4. Não fechar uma DG dependente antes da DG de que depende.

---

## 3. Metodologia

Evidência extraída dos documentos reais com referências `file:line`. Alternativas construídas apenas a partir de leituras legítimas da BÍBLIA; quando existe apenas uma leitura razoável, isso é dito. Consequências apoiadas em norma ou marcadas `INFERENCE`.

---

## 4. DG dependency graph

Dependências **materialmente demonstráveis** (não inventadas):

```text
DG-01 (identidade/modelo do candidate)
  └─▶ DG-02 (lifecycle: expiry/reactivation)

DG-06 (normalização/canonicalização)
  └─▶ DG-07 (fuzzy: usa a comparação normalizada)

DG-04 (semântica de aquisição)
  ├─▶ DG-09 (classificação de falhas → Eligibility)
  └─◀▶ DG-14b (retry: nova Run vs mesma Run — decisão única alocada a DG-14)

DG-09 (Validation → Eligibility)
  └─▶ DG-10 (lifecycle de Eligibility: recovery/hysteresis)
        └─▶ DG-11 (canal conhecido sem fonte elegível)

DG-06 ┐
DG-11 ├─▶ DG-13 (conteúdo + serialização do output)
DG-12 ┘   (DG-12: nº de listas → nº de artifacts)

DG-16 (schema/merge de policy)
  ├─▶ DG-09/DG-10 (EligibilityPolicy)
  └─▶ DG-19 (ficha da família Policies)

DG-15 (autoridade config + ACL)
  └─▶ DG-19 (fichas de Sources/Telegram/Auth)

DG-17 (matriz de autoridade)
  ├─▶ DG-12 (autoridade de Media)
  ├─▶ DG-15 (autoridade de config)
  └─▶ DG-16 (autoridade de Policy)

DG-18 (backup/restore/migration/retention)
  └─▶ DG-19 (artifacts/retention nos contratos de Runs/Playlists)

DG-14 (Run/Scheduler)
  └─▶ DG-13d (estado do Run e output anterior num replace falhado)

DG-20 (governação normativa) — transversal; condiciona o fecho de todas
```

Sem dependência material: **DG-03** e **DG-05** (partilham apenas valores de parâmetros com DG-04d; valores não são decididos aqui).

---

## 5. Ordem recomendada de decisão

Baseada apenas nas dependências acima:

1. **Camada de modelo/entrada independente:** DG-01, DG-03, DG-05, DG-06, DG-16, DG-17, DG-20.
2. **Derivadas directas:** DG-02 (de DG-01), DG-07 (de DG-06), DG-04 (retry identity com DG-14).
3. **Estado:** DG-09 (de DG-04, DG-16), DG-10 (de DG-09), DG-11 (de DG-10).
4. **Publicação/runtime:** DG-12, DG-13 (de DG-06/11/12), DG-14.
5. **Configuração e operações:** DG-15 (de DG-17), DG-18.
6. **Contratos e governação final:** DG-19 (de DG-15/16/18), DG-20.
7. **Fecho documental:** DG-21 (não é decisão funcional; executa-se depois).

---

# 6. Fichas de decisão

---

## DG-01 — DiscoveryCandidate: identidade, persistência e relações

### 1. Pergunta exacta
O `DiscoveryCandidate` é uma **entidade persistente única por conta** (com `Id` estável e `LastSeen` actualizado) ou um **registo por ocorrência/Run**? Qual campo mapeia para `ProviderAccount.AccountKey`, qual o algoritmo/âmbito de `NormalizedIdentity` e a sua relação com `ExternalIdentity`, a re-descoberta é actualização de candidate ou nova Observation, e quem é a **autoridade única** da identidade da conta descoberta — incluindo o fallback quando o provider não expõe identidade estável?

### 2. Porque é necessária
Altera o estado persistido (uma linha actualizada vs várias linhas), a deduplicação, a criação de `ProviderAccount`/`Source`, a idempotência entre Runs e a endereçabilidade do candidate na API.

### 3. O que a BÍBLIA já decidiu
- `03-DISCOVERY.md:5` — "DiscoveryCandidate é uma possibilidade descoberta. Não é ainda uma Source aceite."
- `03-DISCOVERY.md:16` — `Discovery mechanism → Candidate → normalization → deduplication → account identity → Source`; `:18` "A passagem Candidate→Source deve ser explícita."
- `03-DISCOVERY.md:22` — "A mesma conta descoberta através de várias evidências não deve originar múltiplos processamentos equivalentes."
- `03-DISCOVERY.md:24` — "A deduplicação DEVE usar uma identidade funcional estável da conta quando o provider a disponibiliza. `AccountId` deve ser tratado como unidade de serialização…"
- `03-DISCOVERY.md:26` — "Não é permitido assumir que URL/ordem/linha de playlist identifica uma conta."
- `32-DOMAIN-SCHEMA.md:47-58` — campos `Id, ProviderId, ExternalIdentity, Evidence, NormalizedIdentity, Status, FirstSeen, LastSeen, RunId`.
- `32-DOMAIN-SCHEMA.md:25,37,45` — `ProviderAccount.AccountKey`, `Source.ProviderAccountId`, unicidade `ProviderAccount + Source Key`.
- `33-STATE-MACHINES.md:19-21` — `Discovered → Normalized → Deduplicated → Accepted | Rejected | Expired`.
- `02-DOMAIN.md:39,42` — candidate pertence ao grupo **Origem**, não **Identidade**.
- `34-PIPELINE-CONTRACTS.md:5-6` — P0 produz `DiscoveryCandidate[]`; "Não pode: criar CanonicalChannel."
- `28-TRUTH-AND-TRACEABILITY.md:8,11` — "identidade → CanonicalChannel"; "origem → Source".
- `16-PERSISTENCE.md:24` — unicidade "ProviderAccount por identidade funcional".

### 4. O que NÃO pode ser alterado
- **DL-002** (`31:12-13`) — Unknown não cria identidade.
- **DL-004** (`31:18-19`) — número/posição/ordem não são identidade.
- **DL-006** (`31:24-25`) — Provider, Account e Source são conceitos distintos.
- **DL-018** (`31:60-61`) — idempotência; **DL-023/024** (`31:75-79`) — histórico vs estado; ausência ≠ remoção.
- **DL-025** (`31:81-82`) — dados externos não confiáveis.
- Se a decisão alterar `31-DECISION-LOCK.md`, exige alteração explícita da BÍBLIA (`31:151-155`).

### 5. Alternativa A
Entidade persistente única por conta: `Id` estável reutilizado; re-descoberta actualiza `LastSeen`/`Status`; identidade funcional = `NormalizedIdentity` derivada do `ExternalIdentity` com namespace de provider; ligação a `ProviderAccount` por `AccountKey`, com fallback definido; re-descoberta é actualização de candidate.

### 6. Alternativa B
Registo por ocorrência/Run: cada descoberta cria linha nova; a linha anterior torna-se terminal; a conta é consolidada apenas na passagem explícita Candidate→Source; re-descoberta pode criar `Observation` em vez de actualizar candidate.

### 7. Outras alternativas
- **A/B híbrida:** entidade única por conta **+** `Observation` separada por ocorrência (candidate persistente, histórico de ocorrências append-oriented). Compatível com `32:179` (Observation append-oriented) e DL-023.
- **INCOMPATÍVEL COM DECISION LOCK:** URL/ordem/linha como identidade (DL-004, `03:26`); candidate como autoridade de identidade (`28:8-15`, `02:39,42`); criação implícita de `CanonicalChannel` (DL-002, `34:6`).

### 8. Consequências de cada alternativa
| Dimensão | A | B | Híbrida |
|---|---|---|---|
| Domínio | 1 conta = 1 candidate | N candidates por conta | candidate + histórico de ocorrências |
| Persistência | update `LastSeen`/`Status`; exige chave de unicidade | append de linhas; unicidade por Run | duas entidades; mais schema (`32:3`) |
| Lifecycle | transições in-place (DG-02) | linha terminal por ocorrência | candidate in-place + observações |
| Pipeline | P0 idempotente (`DL-018`) | risco de reprocessamento duplicado (`03:22`) | P0 idempotente |
| API | endereçável por `Id` (`41:31-35`) | id por ocorrência | endereçável por `Id` |
| Observabilidade | estado corrente + `LastSeen` | histórico por linha | histórico explícito |
| Testes | "duplicate discovery" (`21:45`) | matriz por ocorrência | ambos |
| Fallback sem identidade estável | decisão aberta | decisão aberta | decisão aberta |

### 9. Documentos afectados
`03-DISCOVERY.md`, `32-DOMAIN-SCHEMA.md`, `33-STATE-MACHINES.md`, `40-ENTITY-LIFECYCLE.md`, `02-DOMAIN.md`, `07-SOURCES.md`, `16-PERSISTENCE.md`, `27-GLOSSARY.md`, `28-TRUTH-AND-TRACEABILITY.md`, `34-PIPELINE-CONTRACTS.md`, `23-DATA-CONTRACTS.md`, `24-DECISIONS.md`, `46-REQUIREMENT-TRACEABILITY.md`.

### 10. Dependências
`DG-01` é pré-condição de `DG-02`. Sem dependência material de DG-03/04/05.

### 11. Resposta esperada do proprietário
"Escolho a alternativa ___ para: (i) persistência/identidade do candidate; (ii) `NormalizedIdentity`; (iii) ligação a `ProviderAccount` e fallback sem identidade estável; (iv) re-descoberta (candidate vs Observation); (v) autoridade única da identidade da conta."

---

## DG-02 — DiscoveryCandidate lifecycle

### 1. Pergunta exacta
Que condição exacta faz transitar `DiscoveryCandidate` para `Expired`, e `Expired`/`Rejected` podem regressar por re-descoberta — sob que condição — preservando `FirstSeen`/`LastSeen` e histórico sem eliminação?

### 2. Porque é necessária
Determina se entidades expiram automaticamente, se reaparecem, e o que é preservado — afecta persistência, lifecycle e idempotência.

### 3. O que a BÍBLIA já decidiu
- `33-STATE-MACHINES.md:19-21` — estados terminais `Accepted | Rejected | Expired`.
- `40-ENTITY-LIFECYCLE.md:5-10` — "Não confundir: disabled; inactive; missing; expired; deleted."
- `40-ENTITY-LIFECYCLE.md:16-18` — "Missing numa execução altera `LastSeen`/estado segundo policy. Não implica delete automático."
- `40-ENTITY-LIFECYCLE.md:36-38` — hard delete é administrativo, explícito e auditado.
- `32-DOMAIN-SCHEMA.md:56-57` — `FirstSeen`/`LastSeen`.
- Facto: `40-ENTITY-LIFECYCLE.md` **não cobre** `DiscoveryCandidate`.

### 4. O que NÃO pode ser alterado
- **DL-023** (`31:75-76`) — histórico e estado actual distintos.
- **DL-024** (`31:78-79`) — ausência não é remoção.
- **DL-113** (`31:139-140`) — sem eliminação silenciosa dentro da retenção.

### 5. Alternativa A
`Expired` por **TTL desde `LastSeen`**; re-descoberta actualiza o registo e regressa a `Discovered`/`Normalized`.

### 6. Alternativa B
`Expired` por **ausência numa execução** (analogia a `40:18`); `Expired`/`Rejected` terminais; re-descoberta cria novo candidate.

### 7. Outras alternativas
- **C:** `Expired` apenas por **operação administrativa** (nunca automática).
- **D (híbrida):** `Expired` por TTL, mas reactivação só por operação auditada.
- **INCOMPATÍVEL COM DECISION LOCK:** tratar `Expired` como `deleted`/apagar silenciosamente (`40:5-10,38`; DL-023, DL-113).

### 8. Consequências
| Dimensão | A | B | C | D |
|---|---|---|---|---|
| Persistência | actualiza `LastSeen` | acumula registos | estado só por acção | TTL + auditoria |
| Lifecycle | reactivação automática | terminal | manual | reactivação auditada |
| Pipeline | idempotente (`DL-018`) | risco de duplicados | estável | estável |
| API | estado visível (`41:31-35`) | idem | idem | idem |
| Observabilidade | contadores por estado (`18:26-30`) | idem | idem | idem |
| Testes | "duplicate discovery" (`21:45`) | por ocorrência | transições admin | TTL + auditoria |

### 9. Documentos afectados
`33-STATE-MACHINES.md`, `40-ENTITY-LIFECYCLE.md`, `32-DOMAIN-SCHEMA.md`, `03-DISCOVERY.md`, `27-GLOSSARY.md`, `18-OBSERVABILITY.md`, `21-TESTING.md`, `31-DECISION-LOCK.md` (se alterado).

### 10. Dependências
`DG-02 depende de DG-01` (a semântica de reactivação pressupõe o modelo de persistência).

### 11. Resposta esperada do proprietário
"O trigger de `Expired` é ___, e `Expired`/`Rejected` são (terminais / reactiváveis sob condição ___), preservando `FirstSeen`/`LastSeen` e histórico."

---

## DG-03 — Parsing M3U

### 1. Pergunta exacta
Qual é o input M3U mínimo aceite (header, pares, atributos obrigatórios, comentários, ordenação) e, perante linha/entrada malformada, a entrada é descartada individualmente ou aborta a playlist — e qual o `Playlist.Status` resultante (`Success`/`Partial`/`Failed`), preservando entradas válidas?

### 2. Porque é necessária
Determina quantas entradas são persistidas, o resultado do Run e o conteúdo a jusante; material e observável.

### 3. O que a BÍBLIA já decidiu
- `04-PLAYLIST-STREAM.md:50-57` — parsing deve impor limite de tamanho/entradas/comprimento/tempo e cancelamento; limites configuráveis.
- `04-PLAYLIST-STREAM.md:7` — playlist é observação/versionamento, não identidade.
- `04-PLAYLIST-STREAM.md:44-46` — duplicados consolidados por fingerprint dentro da mesma Source; Sources diferentes não se confundem.
- `34-PIPELINE-CONTRACTS.md:15-19` — P2 produz `Playlist + Stream raw`; "Não pode: escolher canal canónico."
- `19-FAILURE-MODEL.md:30-37` — "Sucesso parcial deve ser representado explicitamente"; nunca converter parcial em sucesso.
- `23-DATA-CONTRACTS.md:29` — "Input M3U é não confiável e pode ser inconsistente."
- `32-DOMAIN-SCHEMA.md:60-70` — `Playlist` com `ContentHash`, `Format`, `Status`.

### 4. O que NÃO pode ser alterado
- **DL-025** (`31:81-82`) — validação/limites obrigatórios.
- **DL-019** (`31:63-64`) — output atómico (aplica-se a output, não ao parsing, mas relevante a montante).
- Não representar parcial como sucesso (`19:32-37`); P2 não escolhe canal (`34:19`).

### 5. Alternativa A
Input mínimo exige **header/estrutura canónica** (ex.: `#EXTM3U` + `#EXTINF` + URL); entradas fora do formato não são aceites. Entrada inválida é **descartada individualmente**; válidas preservadas; `Playlist.Status=Partial` quando aplicável.

### 6. Alternativa B
Input mínimo aceita **qualquer linha com URL**, sem header nem atributos obrigatórios. Linha malformada **aborta a playlist**; nada persistido; falha `Parsing`.

### 7. Outras alternativas
- **C:** estrutura canónica exigida, mas malformação **aborta** (combinação mais estrita em ambos os eixos).
- **INCOMPATÍVEL COM DECISION LOCK:** representar parcial como sucesso pleno (`19:32-37`); publicar output parcial (DL-019); P2 escolher canal (`34:19`).

### 8. Consequências
| Dimensão | A | B | C |
|---|---|---|---|
| Persistência | persiste entradas válidas; `Status` enum | não persiste | não persiste |
| Pipeline | P2→P3 com subset; Run pode ser `PartiallySucceeded` (`33:49-53`) | P2 falha | P2 falha |
| API | "validation result" (`41:73-76`) | erro | erro |
| Observabilidade | contagem de descartes (`18:24-30`) | erro de parsing | erro |
| Testes | "invalid response/partial" (`21:41,43`) | falha total | falha total |
| Identidade | sem efeito (P2/P6) | sem efeito | sem efeito |

### 9. Documentos afectados
`04-PLAYLIST-STREAM.md`, `34-PIPELINE-CONTRACTS.md`, `19-FAILURE-MODEL.md`, `23-DATA-CONTRACTS.md`, `32-DOMAIN-SCHEMA.md`, `33-STATE-MACHINES.md`, `21-TESTING.md`, `39-CONFIG-SCHEMA.md`.

### 10. Dependências
`INDEPENDENTE` (partilha apenas valores de limites com DG-04d, que não são decididos).

### 11. Resposta esperada do proprietário
"O input mínimo é ___, e uma entrada inválida é (descartada / aborta); o `Status` resultante é ___, preservando/não preservando as entradas válidas."

---

## DG-04 — Aquisição / retry

### 1. Pergunta exacta
Que condições de aquisição (HTTP/ligação/timeout/redirect/vazio/oversized/encoding) são **terminais vs retryable vs transiente-desconhecida**, qual o efeito persistido em candidate/Source/Playlist/Run, e — sem fixar valores — qual o enquadramento de tentativas/backoff/jitter/timeout?

### 2. Porque é necessária
Determina retries, estado de `Source`, resultado do Run e evidência persistida. (A identidade da retentativa — nova vs mesma Run — é decidida em **DG-14b**.)

### 3. O que a BÍBLIA já decidiu
- `19-FAILURE-MODEL.md:22-28` — cada falha define "retryable?", impacto no Run/item, compensação, observabilidade, estado persistido.
- `19-FAILURE-MODEL.md:41,43` — "Retries devem ser limitados, com backoff e cancelamento"; operações não idempotentes exigem idempotency key/reconciliação.
- `19-FAILURE-MODEL.md:47` — após restart, identificar Runs incompletos.
- `17-SECURITY.md:17,19-25` — URL externa hostil; allow/deny; bloqueio de redes internas; controlo de redirects; limites; timeouts; validação de protocolos.
- `33-STATE-MACHINES.md:43-59` — `Created → Running → Succeeded | PartiallySucceeded | Failed | Cancelled`.
- `08-VALIDATION.md:38-42` — distinguir transitória/persistente/ausência/removido.
- `03-DISCOVERY.md:30-36` — timeouts, limite de tamanho, cancelamento, classificação de falhas.

### 4. O que NÃO pode ser alterado
- **DL-109** (`31:127-128`) — lock/lease antes de efeitos; uma execução activa.
- **DL-115** (`31:145-146`) — transacções curtas; chamadas externas fora de transacção.
- **DL-018** (`31:60-61`) — idempotência.
- Run terminado não regressa a Running (`33:59`); retries ilimitados/sem cancelamento são inválidos (`19:41`).

### 5. Alternativa A (semântica)
Não-2xx **terminal** → `Source.Status=Error`; Run `Failed`. Falhas de rede transientes são retryable; falhas de credenciais/negócio são terminais.

### 6. Alternativa B (semântica)
Toda a falha é **registada na tentativa, sem tocar `Source`**; Run `PartiallySucceeded`; estado de `Source` só muda por validação explícita.

### 7. Outras alternativas
- **C:** mapeamento por **classe de erro** (`19:5-18`: Network/Timeout retryable; Authentication/Authorization/Data terminal; Unknown transiente) — taxonomia existe, o mapeamento é a decisão.
- **INCOMPATÍVEL COM DECISION LOCK:** Run terminado a regressar a `Running` (`33:59`); chamadas externas em transacção longa (DL-115); retries ilimitados/sem cancelamento (`19:41`); retry com efeitos duplicados sem idempotência (DL-018).

**Parâmetros (NÃO decidir valores aqui):** nº de tentativas, curva/cap de backoff, jitter, timeout, âmbito (por URL vs host). Regra existente: `19:41`; valores = configuração operacional.

### 8. Consequências
| Dimensão | A | B | C |
|---|---|---|---|
| `Source` | passa a `Error` (recuperável, `33:15-17`) | inalterado | inalterado |
| `Run` | `Failed` | `PartiallySucceeded` | conforme classe |
| Persistência | transição + evidência (`16:36-41`) | evidência da tentativa | transição por classe |
| Observabilidade | classificação de erro (`18:7-12`) | idem | idem |
| API | Runs cancel/status/history (`41:66-71`) | idem | idem |
| Testes | timeout/connection/cancel/restart (`21:36-46`) | idem | matriz por classe |

### 9. Documentos afectados
`19-FAILURE-MODEL.md`, `03-DISCOVERY.md`, `08-VALIDATION.md`, `17-SECURITY.md`, `33-STATE-MACHINES.md`, `13-RUNS.md`, `16-PERSISTENCE.md`, `18-OBSERVABILITY.md`, `21-TESTING.md`, `39-CONFIG-SCHEMA.md`.

### 10. Dependências
`DG-04` alimenta `DG-09` (classificação de falhas). A identidade da retentativa pertence a `DG-14b`.

### 11. Resposta esperada do proprietário
"Classifico as condições como (terminal/retryable/transiente) segundo ___, com efeito persistido ___; os valores de retry/timeout ficam como parâmetros operacionais."

---

## DG-05 — SSRF / segurança de URL

### 1. Pergunta exacta
Que classes de URL/endereço devem ser bloqueadas (protocolos, loopback, link-local, private, IPv4/IPv6, DNS, redirects, DNS rebinding) e como se expressa a **política normativa** separada do **mecanismo de implementação** — sem fixar CIDRs/timeouts?

### 2. Porque é necessária
Determina se o sistema pode ser induzido a aceder a recursos internos; afecta aquisição, redirects, segurança e evidência.

### 3. O que a BÍBLIA já decidiu
- `17-SECURITY.md:17` — "Qualquer URL fornecida ou descoberta externamente deve ser tratada como potencialmente hostil."
- `17-SECURITY.md:19-25` — "Devem existir: allow/deny policy quando aplicável; bloqueio de redes internas quando o threat model exigir; controlo de redirects; limites de resposta; timeouts; validação de protocolos."
- `35-SECURITY-MODEL.md:58-59` — "nenhum input URL confiado automaticamente"; "nenhum parser sem limites".
- `17-SECURITY.md:5-13` — URLs com credenciais nunca em logs/reports/artifacts/erros.
- `03-DISCOVERY.md:55` — URL de stream com credenciais não é identidade.

### 4. O que NÃO pode ser alterado
- **DL-025** (`31:81-82`) — dados externos não confiáveis.
- **DL-020** (`31:66-67`) — secrets nunca em diagnóstico.
- A política detalhada está delegada a ADR (`24-DECISIONS.md:13`; `47-BIBLE-AUDIT-FINDINGS.md:29`) — evidência, não decisão.

### 5. Alternativa A (política mínima)
Política normativa obriga a: validação de protocolo, timeouts, limites de resposta e controlo de redirects; **não** classifica DNS/IP (deixa endereçamento privado como mecanismo).

### 6. Alternativa B (política por classes)
Política normativa obriga a **resolver e classificar** o endereço: bloquear loopback, link-local, private (IPv4/IPv6), fixar o endereço resolvido em redirects e prevenir rebinding.

### 7. Outras alternativas
- **C:** política declarada por **capacidade configurável** (modo restrito vs modo estendido) com defaults.
- **INCOMPATÍVEL COM DECISION LOCK:** aceitar/consumir URL externa sem validação (DL-025); registar credenciais em evidência (DL-020, `17:5-13`).

**Parâmetros (NÃO decidir):** CIDRs concretos, lista de protocolos permitidos, limites de resposta, timeouts.

### 8. Consequências
| Dimensão | A | B | C |
|---|---|---|---|
| Aquisição | redirect controlado, sem classificação IP | bloqueio de redes internas no redirect | conforme modo |
| Segurança | reduz exposição básica | reduz exposição a SSRF/rebinding | configurável |
| Persistência | `ErrorCode` de falha (`19:22-28`) | idem | idem |
| Observabilidade | sanitização (`DL-020`, `18:34`) | idem | idem |
| Testes | security tests (`21:8-9`) | incluir DNS/IPv6/redirect | matriz de modos |
| Domínio | sem efeito de identidade (`03:55-56`) | idem | idem |

### 9. Documentos afectados
`17-SECURITY.md`, `35-SECURITY-MODEL.md`, `03-DISCOVERY.md`, `19-FAILURE-MODEL.md`, `39-CONFIG-SCHEMA.md`, `21-TESTING.md`, `24-DECISIONS.md`, `47-BIBLE-AUDIT-FINDINGS.md`.

### 10. Dependências
`INDEPENDENTE` (partilha com DG-04d apenas valores de limites/timeouts).

### 11. Resposta esperada do proprietário
"A política SSRF normativa é (A/B/C) ___, sendo os valores concretos (CIDRs/timeouts) parâmetros operacionais definidos em ___."

---

## DG-06 — Normalização, canonicalização e fingerprint

### 1. Pergunta exacta
Quais são as transformações normativas por campo (nome, tvg-id, group, logo, URL…), qual a regra de metadata ausente/nullability, e qual o algoritmo normativo de canonicalização de URL/fingerprint — separando explicitamente **normalização**, **reconhecimento** e **fingerprint**?

### 2. Porque é necessária
Determina matching, deduplicação dentro da Source, desempate na Selection e determinismo do output.

### 3. O que a BÍBLIA já decidiu
- `04-PLAYLIST-STREAM.md:30-32` — normalização determinística; não altera silenciosamente a evidência original; guardar original e normalizado.
- `04-PLAYLIST-STREAM.md:36-38` — fingerprint segundo algoritmo normativo **versionado**; canonicalização remove apenas elementos explicitamente não-identificadores; nunca remover autenticação e reconstruir URL insegura.
- `04-PLAYLIST-STREAM.md:40` — versão inicial do algoritmo registada como ADR antes da implementação definitiva.
- `32-DOMAIN-SCHEMA.md:74-85` — `Fingerprint`, `FingerprintVersion`, `OriginalName`, `NormalizedName`, `OriginalTvgId`, `OriginalGroup`.
- `34-PIPELINE-CONTRACTS.md:21-24` — P3: "representação normalizada + original preservado".
- `02-DOMAIN.md:79-80,90` — identidade não depende de posição/URL; determinismo.
- `05-CATALOGUE.md:34-35` — nome normalizado é evidência de matching, não identidade.
- `32-DOMAIN-SCHEMA.md:3` — nullability não pode ser inferida.

### 4. O que NÃO pode ser alterado
- **DL-108** (`31:124-125`) — fingerprint versionado; mudança incompatível cria nova versão.
- **DL-101** (`31:86-98`) — determinístico; fingerprint é desempate (item 6).
- **DL-001/002** (`31:9-13`) — fingerprint não é identidade; não cria identidade.
- Variar nullability sem decisão (`32:3`); URL com credenciais reconstruída (`04:38`).

### 5. Alternativa A
Nome: trim + lower + collapse + normalização de diacríticos; `tvg-id` → `ExternalIdentity`; URL canónica com remoção de elementos não-identificadores; metadata ausente → `null` com nullability declarada.

### 6. Alternativa B
Nome: apenas trim + collapse; `tvg-id` verbatim; URL verbatim; metadata ausente → string vazia **ou** rejeição da entrada (sub-opção).

### 7. Outras alternativas
- **C:** transformações por campo declaradas numa **tabela normativa explícita** (cada campo: transformação + nullability), deixando o algoritmo de fingerprint a uma versão a promover (ADR-0002, hoje `Proposed`).
- **INCOMPATÍVEL COM DECISION LOCK:** fingerprint fuzzy/perceptual/não determinístico (DL-101, DL-108); fingerprint como identidade (DL-001/002); remover autenticação e reconstruir URL (DL-020, `04:38`).

### 8. Consequências
| Dimensão | A | B | C |
|---|---|---|---|
| Reconhecimento | mais matches por normalização | menos matches | definido por tabela |
| Dedup | fingerprint canónico estável | fingerprint cru | conforme tabela |
| Persistência | colunas original+normalizado; versão | idem | idem |
| Selection | desempate por fingerprint (`DL-101` item 6) | idem | idem |
| Output | metadados normalizados emitidos (`11:25`) | idem | idem |
| Migração | `DL-108` exige coexistência de versões | idem | idem |
| Testes | determinismo (`21:28-34`) | idem | matriz por campo |

### 9. Documentos afectados
`04-PLAYLIST-STREAM.md`, `32-DOMAIN-SCHEMA.md`, `34-PIPELINE-CONTRACTS.md`, `02-DOMAIN.md`, `05-CATALOGUE.md`, `23-DATA-CONTRACTS.md`, `09-SELECTION.md`, `21-TESTING.md`, `31-DECISION-LOCK.md` (DL-108), `24-DECISIONS.md`, `docs/adr/ADR-0002` (evidência).

### 10. Dependências
`DG-06` alimenta `DG-07` (comparação normalizada). Sem dependência de DG-01/02/03/04/05.

### 11. Resposta esperada do proprietário
"Defino as transformações por campo ___, a regra de metadata ausente ___, e o algoritmo normativo de canonicalização/fingerprint ___, separando normalização, reconhecimento e fingerprint."

---

## DG-07 — Reconhecimento fuzzy

### 1. Pergunta exacta
O fuzzy matching é **mandatório ou opcional** — autorizado por que tipo/scope de policy — e, quando usado, qual a métrica/limiar/normalização/desempate, e o resultado **abaixo do limiar** (`Unknown` vs `Ambiguous`), preservando a ordem de reconhecimento fechada?

### 2. Porque é necessária
Determina se streams quase-correspondentes são resolvidos automaticamente ou vão para Review; afecta identidade e output.

### 3. O que a BÍBLIA já decidiu
- `05-CATALOGUE.md:31-39` — ordem determinística 1→7; passo 6 "fuzzy matching apenas quando houver política de confiança e desempate"; passo 7 Review.
- `05-CATALOGUE.md:41` — passo posterior não contradiz match exacto anterior.
- `05-CATALOGUE.md:45,47` — empate → `AMBIGUOUS` → Review; "Não existe 'escolher o primeiro'."
- `05-CATALOGUE.md:51-53` — `UNKNOWN` → Review.
- `34-PIPELINE-CONTRACTS.md:41` — `Canonical / Unknown / Ambiguous / Excluded`.
- `38-POLICIES.md:37-44` — lista mínima de policies **não inclui** tipo de reconhecimento/confiança.
- `43-ANTI-PATTERNS.md:7` — proibido fazer fuzzy e escolher em empate silenciosamente.

### 4. O que NÃO pode ser alterado
- **DL-003** (`31:15-16`) — Unknown/Ambiguous → Review; heurística não esconde ambiguidade.
- **DL-002** (`31:12-13`) — fuzzy não cria identidade.
- **DL-101** (`31:86-98`) — determinístico, sem "score mágico".
- Ordem de matching fechada (`05:31-41`); qualidade não decide matching (`05:61`).

### 5. Alternativa A
Fuzzy **activo apenas com policy de confiança/desempate** (exige criar esse tipo/scope de policy); abaixo do limiar → `UNKNOWN` → Review.

### 6. Alternativa B
Passo fuzzy **omitido por defeito**; apenas exactos/alias/heurística explícita resolvem; não-exactos → `UNKNOWN` → Review.

### 7. Outras alternativas
- **C:** fuzzy activo, mas abaixo do limiar → `AMBIGUOUS` → Review.
- **INCOMPATÍVEL COM DECISION LOCK:** auto-resolver fuzzy sem policy de confiança/desempate (`05:38`); escolher empate (`05:47`; DL-003); contradizer match exacto (`05:41`); criar `CanonicalChannel` via fuzzy (DL-002).

**Parâmetros (NÃO decidir):** métrica, limiar, normalização de comparação, pesos, regra de empate.

### 8. Consequências
| Dimensão | A | B | C |
|---|---|---|---|
| Reconhecimento | mais correspondências, com policy | só exactos | mais correspondências |
| Review | abaixo do limiar → Unknown | mais itens Unknown | abaixo do limiar → Ambiguous |
| Policy | exige novo tipo/scope (`38:37-44`) | não exige | exige |
| API | Review resolve/ignore (`41:44-49`) | idem | idem |
| Testes | fixtures de fuzzy + empate (`21:21-22`) | fixtures de exactos | fixtures de ambos |
| Determinismo | preservado (DL-101) | preservado | preservado |

### 9. Documentos afectados
`05-CATALOGUE.md`, `38-POLICIES.md`, `43-ANTI-PATTERNS.md`, `34-PIPELINE-CONTRACTS.md`, `33-STATE-MACHINES.md`, `32-DOMAIN-SCHEMA.md`, `21-TESTING.md`, `30-REVIEW-CHECKLIST.md`.

### 10. Dependências
`DG-07 depende de DG-06` (normalização do nome é o input da comparação).

### 11. Resposta esperada do proprietário
"O fuzzy é (mandatório condicionado / omitido / activo) ___, autorizado por policy ___, e abaixo do limiar resulta em (Unknown / Ambiguous); métrica e limiar ficam como parâmetros."

---

## DG-08 — Review: efeitos concretos

### 1. Pergunta exacta
Que efeitos materiais concretos produzem `Resolve`, `Ignore`, criação de alias, mudança de identidade e reabertura — e que alteração exacta cada decisão deve declarar? (A regra de reabertura já está fechada: DL-105.)

### 2. Porque é necessária
Determina se Review cria/edita entidades de catálogo, se liga apenas relações, e o que acontece a streams/relações existentes.

### 3. O que a BÍBLIA já decidiu
- **DL-002** (`31:12-13`) — Unknown não cria identidade (implicitamente).
- `33-STATE-MACHINES.md:25-29` — `Open → InReview → Resolved`; `Open → Ignored` com razão explícita; reabertura auditada.
- `05-CATALOGUE.md:63-67` — "Adicionar, editar, aliasar, fundir ou desactivar um CanonicalChannel é uma mutação administrativa do catálogo e deve ficar auditada"; "Uma aprovação de Review que altere o catálogo deve declarar exactamente que mudança produz."
- `34-PIPELINE-CONTRACTS.md:45-52` — P7 saída "decisão administrativa + alteração explícita"; P8 cria/actualiza a relação identidade↔source.
- `32-DOMAIN-SCHEMA.md:101-110,136-148` — `ChannelAlias`, `ExternalIdentity`, `ReviewItem`.
- `41-API-INVENTORY.md:44-49` — Review: list/details/resolve/ignore/reopen.
- `08-VALIDATION.md:20-24` — Validation não altera identidade.

### 4. O que NÃO pode ser alterado
- **DL-001/002** (`31:9-13`) — catálogo é a autoridade; criação implícita proibida.
- **DL-003** (`31:15-16`) — incerteza vai para Review.
- **DL-008** (`31:30-31`) — Validation não altera identidade.
- **DL-105** (`31:114-116`) — lifecycle e reabertura auditada (fechado).
- **DL-023** (`31:75-76`) — histórico preservado.

### 5. Alternativa A — Resolve como relação
Aprovar liga a stream a um `CanonicalChannel` **existente** via `ChannelSource` (P8), sem mutação de identidade. `Ignore` fecha sem efeito de domínio.

### 6. Alternativa B — Resolve como mutação de catálogo declarada
Aprovar pode **criar/editar** `CanonicalChannel`, `ChannelAlias`, `ExternalIdentity`, fundir ou desactivar, declarando a mudança exacta e auditando. `Ignore` fecha sem criar relação.

### 7. Outras alternativas
- **C — Resolve decomposto por subtipo:** cada decisão mapeia em entidade distinta (alias vs external identity vs canal).
- **INCOMPATÍVEL COM DECISION LOCK:** Resolve/Ignore criar `CanonicalChannel` silenciosamente para limpar a fila (DL-002); reabertura sem operação auditada (DL-105); mudar identidade como efeito lateral de Validation (DL-008).

### 8. Consequências
| Dimensão | A | B | C |
|---|---|---|---|
| Domínio | só relação `ChannelSource` | cria/edita catálogo | mutação particionada |
| Persistência | `ChannelSource` + `AuditRecord` | `CanonicalChannel`/`Alias`/`ExternalIdentity` + audit | várias entidades |
| Lifecycle | catálogo inalterado | `Key` estável; desactivar em vez de apagar (`32:99`, `40:14`) | idem |
| Pipeline | P7→P8 | P7 emite alteração explícita | idem |
| API | resolve devolve relação | resolve com payload de mudança | payload por subtipo |
| Output | canal passa a ter fonte (INFERENCE) | canal pode entrar em ordering | idem |
| Dispatcharr | reconciliação normal | possível novo recurso `CrawlerManaged` (INFERENCE) | idem |
| Observabilidade | audit de relação | audit before/after (`32:257-267`) | idem |
| Testes | Q4 (`44:12-13`) | Q3+Q4 | Q3+Q4 |

### 9. Documentos afectados
`05-CATALOGUE.md`, `32-DOMAIN-SCHEMA.md`, `33-STATE-MACHINES.md`, `34-PIPELINE-CONTRACTS.md`, `40-ENTITY-LIFECYCLE.md`, `41-API-INVENTORY.md`, `22-API-CONTRACTS.md`, `23-DATA-CONTRACTS.md`, `18-OBSERVABILITY.md`, `21-TESTING.md`, `44-QUALITY-GATES.md`.

### 10. Dependências
`INDEPENDENTE` de DG-01..DG-07, mas a ficha de API de Review depende de `DG-19`. Nota do manifesto: ADR de Review lifecycle exigido (`24-DECISIONS.md:24`) — evidência documental, não decisão funcional.

### 11. Resposta esperada do proprietário
"`Resolve` produz (só relação / mutação de catálogo ___), `Ignore` produz ___, e a mudança declarada é ___; reabertura mantém-se como DL-105."

---

## DG-09 — Validation → Eligibility

### 1. Pergunta exacta
Qual é a consequência de Eligibility (`Unknown | Eligible | Ineligible`) para cada **categoria** de Validation já definida na BÍBLIA, sem inventar códigos de erro?

### 2. Porque é necessária
Determina quando um canal passa a elegível/inelegível e, portanto, o que entra na selecção e no output.

### 3. O que a BÍBLIA já decidiu
- `08-VALIDATION.md:28-32` — "Eligibility é a decisão derivada de observations + policy: `Eligible | Ineligible | Unknown`; deve existir evidência suficiente."
- `08-VALIDATION.md:36-42` — falha não apaga `ChannelSource`; distinguir transitória / persistente / ausência / removido-desactivado.
- `19-FAILURE-MODEL.md:5-18` — categorias (Configuration/Auth/Network/Timeout/Parsing/Validation/Data/Conflict/ExternalService/Security/Cancellation/Internal).
- `19-FAILURE-MODEL.md:22-28` — cada falha define `retryable?`, impacto, compensação, observabilidade, estado.
- `32-DOMAIN-SCHEMA.md:165-177` — `Observation.Result/ErrorCode` (`ErrorCode` sem enum).
- `32-DOMAIN-SCHEMA.md:181-191` — `Eligibility` derivado (subject, policy version, decision, reason, evidence, evaluatedAt).
- `33-STATE-MACHINES.md:39-41` — transição suportada por policy + evidence.

### 4. O que NÃO pode ser alterado
- **DL-008** (`31:30-31`) — Validation não altera identidade.
- **DL-009** (`31:33-34`) — Eligibility é derivada.
- **DL-024** (`31:78-79`) — ausência não é remoção.
- **DL-104** (`31:109-112`) — uma falha transitória não muda o estado publicado.
- `08-VALIDATION.md:44-46` — comportamento exacto versionado como ADR (evidência documental).

### 5. Alternativa A — mapeamento explícito por categoria
Tabela normativa `categoria de Validation → consequência de Eligibility`, com sucesso→pode sustentar `Eligible`; falha persistente→pode sustentar `Ineligible` após limiar; transitória→não muda estado publicado; ausência→estado próprio (não `Ineligible`); removido→estado explícito.

### 6. Alternativa B — mapeamento delegado à EligibilityPolicy
A BÍBLIA fixa apenas os **estados** e as **categorias**; a correspondência exacta é definida em `EligibilityPolicy` (configurada/versionada), não em tabela normativa.

### 7. Outras alternativas
- **C — híbrida:** tabela normativa de princípios + parâmetros por policy.
- **INCOMPATÍVEL COM DECISION LOCK:** ausência tratada como `Ineligible` (DL-024); falha transitória isolada a mudar estado (DL-104); Validation a alterar identidade (DL-008).

### 8. Consequências
| Dimensão | A | B | C |
|---|---|---|---|
| Domínio | previsível, fixo | flexível por policy | fixo + configurável |
| Persistência | `Eligibility` derivado com `reason`/policy version | idem | idem |
| Lifecycle | transições explícitas | transições por policy | híbrido |
| Pipeline | P9→P10 (`34:54-60`) | idem | idem |
| Selection | só `Eligible` entra (`09:30`; ADR-0003:59-61) | idem | idem |
| Output/DS | canais não elegíveis omitidos/registados | idem | idem |
| Testes | Q5 separação (`44:15-16`) | matriz de policy | ambos |

### 9. Documentos afectados
`08-VALIDATION.md`, `19-FAILURE-MODEL.md`, `32-DOMAIN-SCHEMA.md`, `33-STATE-MACHINES.md`, `38-POLICIES.md`, `24-DECISIONS.md`, `22-API-CONTRACTS.md`, `23-DATA-CONTRACTS.md`, `18-OBSERVABILITY.md`, `21-TESTING.md`.

### 10. Dependências
`DG-09 depende de DG-04` (classificação de falhas) e de `DG-16` (schema de `EligibilityPolicy`).

### 11. Resposta esperada do proprietário
"O mapeamento Validation→Eligibility é (tabela normativa / delegado a policy / híbrido) ___, respeitando DL-009/DL-024/DL-104."

---

## DG-10 — Eligibility lifecycle

### 1. Pergunta exacta
Como transita a Eligibility entre `Unknown | Eligible | Ineligible` em caso de falha, recuperação, ausência de observação, histerese, estado "último publicável" e expiração — e o que governa cada transição?

### 2. Porque é necessária
Determina churn do output, quando um canal deixa de ser publicado e se recupera.

### 3. O que a BÍBLIA já decidiu
- **DL-104** (`31:109-112`) — "A mudança de estado publicada não ocorre por uma única falha transitória. A policy deve permitir definir limiar de falhas consecutivas e/ou janela temporal. Na ausência de configuração específica, conserva-se o último estado publicável enquanto existir evidência recente dentro da janela operacional. O valor exacto da janela deve ser configuração operacional."
- `33-STATE-MACHINES.md:39-41` — `Unknown | Eligible | Ineligible`; transição por policy + evidence.
- `08-VALIDATION.md:44-46` — churn deve poder usar estabilidade/histerese; comportamento exacto versionado como ADR (evidência).
- `40-ENTITY-LIFECYCLE.md:16-23` — missing altera `LastSeen`/estado segundo policy; não delete automático; históricos de Observation permanecem segundo retenção.
- `38-POLICIES.md:36-44` — `EligibilityPolicy` existe; `:30-33` snapshot congela policies antes do Run.

### 4. O que NÃO pode ser alterado
- **DL-104** — histerese obrigatória; sem valor hard-coded.
- **DL-024** — ausência não é remoção.
- **DL-008** — Eligibility não altera identidade.
- **DL-103** (`31:103-107`) — precedência de policy.
- **DL-017** (`31:57-58`) — snapshot congela significado do Run.

### 5. Alternativa A — histerese como parâmetros de `EligibilityPolicy`
Limiar de falhas consecutivas e/ou janela temporal definidos em policy, congelados no snapshot; transição só após limiar.

### 6. Alternativa B — estado de publicação separado do cálculo
Manter o "último estado publicável" como estado distinto da decisão recalculada, conservando-o enquanto houver evidência na janela (exige campo/entidade novo — `BIBLE_GAP`).

### 7. Outras alternativas
- **C — recuperação só após nova validação bem sucedida** (reversão sustentada por observation de sucesso + policy).
- **D — expiração por janela operacional:** sem evidência recente, Eligibility deixa de ser sustentada.
- **INCOMPATÍVEL COM DECISION LOCK:** uma falha transitória mudar estado publicado (DL-104); ausência apagar relação (DL-024); janela/limiar hard-coded (DL-104); Eligibility alterar identidade (DL-008).

### 8. Consequências
| Dimensão | A | B | C | D |
|---|---|---|---|---|
| Domínio | estado derivado por policy | estado publicado separado | recuperação por evidência | expiração temporal |
| Persistência | policy version + evidence | novo campo/entidade (`32:181-191`) | idem A | idem A |
| Lifecycle | transição após limiar | estados explícitos | transição por sucesso | transição por janela |
| Output | muda conteúdo após limiar | pode conservar publicação | recupera após sucesso | pode perder canal |
| Selection | filtra por `Eligible` | idem | idem | idem |
| Observabilidade | `reason`/evidence | histórico explícito | idem | idem |
| Testes | Q5, failure tests | migração de schema | recuperação | fronteira temporal |

### 9. Documentos afectados
`33-STATE-MACHINES.md`, `40-ENTITY-LIFECYCLE.md`, `08-VALIDATION.md`, `38-POLICIES.md`, `32-DOMAIN-SCHEMA.md`, `24-DECISIONS.md`, `39-CONFIG-SCHEMA.md`, `18-OBSERVABILITY.md`, `21-TESTING.md`, `11-OUTPUT.md`, `12-DISPATCHARR.md`.

### 10. Dependências
`DG-10 depende de DG-09`. Alimenta `DG-11`.

### 11. Resposta esperada do proprietário
"A histerese/recuperação funciona assim: ___, incluindo o que é o 'último estado publicável' e o que acontece fora da janela; limiar/janela ficam como parâmetros."

---

## DG-11 — Canal conhecido sem fonte elegível

### 1. Pergunta exacta
Quando um `CanonicalChannel` é conhecido mas não existe `ChannelSource` elegível no snapshot, o que devem fazer Selection, Ordering, Composition/Output e Dispatcharr — e o que acontece ao output previamente publicado?

### 2. Porque é necessária
Determina se canais desaparecem do output, se o output anterior é mantido, e se recursos remotos são afectados.

### 3. O que a BÍBLIA já decidiu
- `09-SELECTION.md:46-54` — resultado distingue `selected | evaluated | no eligible source | ambiguous | not evaluated | error`; `not evaluated` ≠ `evaluated with zero`.
- `34-PIPELINE-CONTRACTS.md:66-68` — P12 "Produz stream escolhida ou estado sem escolha."
- `ADR-0003:59-61` (evidência) — só `Eligible` entra no ranking; não-eligíveis aparecem como não-elegíveis, nunca silenciosamente removidos.
- `10-ORDERING.md:38-40` — "Uma posição sem canal não deve ser preenchida com outra identidade."
- `11-OUTPUT.md:15-19,30-34` — determinismo por snapshot; sem ordenação implícita; escrita atómica; parcial nunca válido.
- `12-DISPATCHARR.md:13-22,45-47` — só `CrawlerManaged` pode ser removido automaticamente; `External/Unknown` nunca.
- `42-EXAMPLES.md:18-24` — "nenhum CanonicalChannel criado; nenhum output linear produzido a partir dela."

### 4. O que NÃO pode ser alterado
- **DL-013** (`31:45-46`) — estado remoto não altera catálogo.
- **DL-014** (`31:48-49`) — só `CrawlerManaged` pode ser removido automaticamente.
- **DL-019** (`31:63-64`) — output atómico.
- **DL-024** (`31:78-79`) — ausência não é remoção.
- **DL-116** (`31:148-149`) — reconciliação de estado remoto.
- Preencher o gap com outra identidade (`10:38-40`); tratar "no eligible source" como "evaluated with zero" (`09:54`).

### 5. Alternativa A — omitir do output
O canal fica sem stream e não é emitido; a posição de ordering permanece vazia; o estado `no eligible source` é registado.

### 6. Alternativa B — reutilizar a última selecção/output válido
Manter a stream previamente publicada até nova evidência elegível; exige referência persistida à última selecção (não localizada em `32` → `BIBLE_GAP`).

### 7. Outras alternativas
- **C — falhar o Run** (`Failed`/`PartiallySucceeded`) quando existirem canais listados sem fonte.
- **D — reconciliar apenas o recurso remoto conforme ownership**, sem alterar output (manter `External/Unknown`; decidir `CrawlerManaged`).
- **INCOMPATÍVEL COM DECISION LOCK:** preencher o gap com outra identidade (`10:38-40`); "no eligible source" como zero (`09:54`); usar estado remoto para decidir identidade (DL-013); apagar `ChannelSource`/`CanonicalChannel` (DL-024).

### 8. Consequências
| Dimensão | A | B | C | D |
|---|---|---|---|---|
| Output | canal omitido; posição vazia | mantém publicação anterior | sem output novo (DL-019) | inalterado |
| Persistência | regista estado de selecção | requer referência à última selecção | regista falha | `OwnershipRecord` |
| Ordering | item mantido, sem stream | idem | idem | idem |
| Dispatcharr | nada / reconciliação mínima | idem | idem | mantém `External/Unknown` |
| Selection | estado explícito | idem | idem | idem |
| Determinismo | preservado por snapshot (`11:17`) | risco de depender de execução anterior | preservado | preservado |
| Testes | Q6/Q7 (`44:18-22`) | caso de continuidade | caso de falha | Q8 |

### 9. Documentos afectados
`09-SELECTION.md`, `10-ORDERING.md`, `11-OUTPUT.md`, `12-DISPATCHARR.md`, `34-PIPELINE-CONTRACTS.md`, `40-ENTITY-LIFECYCLE.md`, `32-DOMAIN-SCHEMA.md`, `13-RUNS.md`, `18-OBSERVABILITY.md`, `21-TESTING.md`, `42-EXAMPLES.md`.

### 10. Dependências
`DG-11 depende de DG-10`. Alimenta `DG-13`.

### 11. Resposta esperada do proprietário
"Quando não há fonte elegível, o sistema (omite / mantém anterior / falha / reconcilia) ___, e o output anterior é ___, respeitando ownership e atomicidade."

---

## DG-12 — Ordering

### 1. Pergunta exacta
`Position` é única por lista? É permitido o mesmo canal em duas posições da mesma lista? Canal elegível ausente de qualquer OrderingList é excluído, incluído ou erro? Podem publicar-se várias listas no mesmo Run? Sem política, Radio e VOD são publicados ou omitidos?

### 2. Porque é necessária
Determina cardinalidade, unicidade, comportamento do output e publicação de media kinds.

### 3. O que a BÍBLIA já decidiu
- **DL-011** (`31:39-40`) — "Ordering não é identidade; atribui posição a um CanonicalChannel."
- **DL-012** (`31:42-43`) — "VOD não usa ordering linear; tem fluxo, grupos e output próprios."
- `10-ORDERING.md:9` — OrderingList configurável; `:14` `OrderingList + position → CanonicalChannel`; `:16` "O mesmo canal pode existir em várias listas/posições."; `:31` Radio "quando publicada"; `:35` VOD não usa posições lineares de TV; `:39-40` posição sem canal não é preenchida.
- `16-PERSISTENCE.md:27` — unicidade `(OrderingListId, Position)` **"quando uma posição for única"**; `:30` forma exacta das constraints a especificar antes da implementação.
- `06-COUNTRY-MEDIA.md:21-25` — classificação mínima Linear TV/Radio/VOD/Unknown; `:35` VOD "só é publicado se a política VOD o permitir."
- `11-OUTPUT.md:26-27` — número da OrderingList; não misturar VOD e linear.
- `32-DOMAIN-SCHEMA.md:195-209` — `OrderingList`/`OrderingItem`.

### 4. O que NÃO pode ser alterado
- **DL-011** — Ordering não é identidade.
- **DL-012** — VOD separado.
- `11-OUTPUT.md:19` — ordenações implícitas de DB proibidas.
- `10:38-40` — buraco não é preenchido com outra identidade.
- Se alterar constraints, exige schema normativo (`16:30`).

### 5. Alternativa A
`(OrderingListId, Position)` **única**; canal **pode** repetir-se em posições da mesma lista (posição não identifica canal); não listados **excluídos** do output; publicação de **uma lista por MediaKind** por Run; Radio/VOD omitidos sem lista/política própria.

### 6. Alternativa B
`(OrderingListId, Position)` única **e** canal único por lista (proíbe repetição); não listados excluídos; uma lista autoritativa por Run (demais preview); Radio/VOD omitidos sem política.

### 7. Outras alternativas
- **C:** unicidade configurável por lista (literal "quando… única" de `16:27`); não listados → erro/`Failed`.
- **D:** publicar todas as listas `Enabled`, um artifact por lista, incluindo Radio com lista própria.
- **INCOMPATÍVEL COM DECISION LOCK:** posição como identidade / preencher buraco (`DL-011`, `10:39-40`); misturar TV/Radio/VOD num output linear (`DL-012`, `11:27`); VOD publicado por defeito sem política (`06:35`).

### 8. Consequências
| Dimensão | A | B | C | D |
|---|---|---|---|---|
| Persistência | UNIQUE (lista, posição) | + UNIQUE (lista, canal) | constraint por flag | N playlists |
| Output | 1 artifact por MediaKind | 1 artifact autoritativo | conforme | N artifacts |
| Não listados | excluídos | excluídos | excluir/erro | por lista |
| Radio/VOD | omitidos sem política | idem | idem | Radio com lista própria |
| API Ordering | preview/listas (`41:60-64`) | idem | expõe flag | idem |
| Testes | unicidade + emissão determinística | conflito de duplicado | matriz | multi-artifact |

### 9. Documentos afectados
`06-COUNTRY-MEDIA.md`, `10-ORDERING.md`, `11-OUTPUT.md`, `16-PERSISTENCE.md`, `32-DOMAIN-SCHEMA.md`, `38-POLICIES.md`, `39-CONFIG-SCHEMA.md`, `41-API-INVENTORY.md`, `42-EXAMPLES.md`, `31-DECISION-LOCK.md` (DL-011/012).

### 10. Dependências
`DG-12` relaciona-se com `DG-17` (autoridade de Media) e `DG-16b` (policy/default); `DG-13` depende de DG-12 (nº de artifacts).

### 11. Resposta esperada do proprietário
"Decido: unicidade de Position ___, duplicados ___, não listados ___, listas publicadas ___, default de Radio/VOD ___."

---

## DG-13 — Output / Publicação

### 1. Pergunta exacta
Separação **semântica vs física**:
- **Semântico:** o que é uma `GeneratedPlaylist` e a enumeração de `publication state`.
- **Físico:** qual o contrato do M3U (atributos/encoding), naming/`Path`, atomicidade, parcial, replace falhado e output anterior.

### 2. Porque é necessária
Determina interoperabilidade, determinismo, integridade do artifact e o que os consumidores (Dispatcharr) recebem.

### 3. O que a BÍBLIA já decidiu
- `11-OUTPUT.md:15-19` — determinismo por RunSnapshot; proibida ordenação implícita.
- `11-OUTPUT.md:24-28` — respeitar sintaxe M3U; emitir metadados normalizados; usar número da OrderingList; não misturar VOD e linear; evitar URLs com credenciais quando possível.
- `11-OUTPUT.md:30-34` — "Escrever um output novo deve ser atómico: gerar temporariamente, validar e substituir"; "Um output parcial nunca deve ser apresentado como output válido."
- **DL-019** (`31:63-64`) — "Output é atómico."
- **DL-020** (`31:66-67`) — secrets fora de diagnóstico/output.
- `23-DATA-CONTRACTS.md:31,35,37-52` — M3U validado antes de publicar; artifacts com schema version e origem; contrato mínimo de 11 itens; "Quando faltar um detalhe necessário para interoperabilidade, existe `BIBLE_GAP`."
- `32-DOMAIN-SCHEMA.md:224-233` — `GeneratedPlaylist` com `ContentHash`, `Path/reference`, schema version, `publication state` (sem enum).
- `39-CONFIG-SCHEMA.md:55-61` — writer: temporário, replace atómico, permissões restritas, sem secrets.
- `40-ENTITY-LIFECYCLE.md:30-34` — Run imutável; artifacts elimináveis por retenção mantendo referência.

### 4. O que NÃO pode ser alterado
- **DL-019** — atomicidade; parcial nunca válido.
- **DL-020** — secrets fora do output.
- `11:24` — sintaxe M3U obrigatória.
- `11:17,19` — determinismo e ausência de ordenação implícita.
- `32:3` — schema físico relevante exige secção normativa.

### 5. Alternativa A (física)
M3U convencional mínimo: `#EXTM3U` + `#EXTINF:<duration>,<name>` + URL, com `tvg-id`/`tvg-name`/`tvg-logo`/`group-title` quando disponíveis; encoding e line-endings fixos declarados; `Path` estável por lista substituído atomicamente; parcial nunca persistido.

### 6. Alternativa B (física)
M3U com número explícito da OrderingList; `Path` por Run/versão (inclui `RunId`/`ContentHash`); parcial **persistido como artifact de diagnóstico não publicado**; output anterior mantido em replace falhado.

### 7. Outras alternativas
- **C:** raiz de output configurável + nome derivado da lista (combina estável e configurável).
- **Enum `publication state`:** F1 `Generated → Published → Superseded → Failed`; F2 binário `Published`/`NotPublished` (falha no Run); **F3 INCOMPATÍVEL** (parcial apresentável como válido).
- **INCOMPATÍVEL COM DECISION LOCK:** formato não-M3U ou credenciais em claro (`11:24`; DL-020); escrita não-atómica directa (DL-019); publicar parcial como válido ou substituir o anterior por parcial (DL-019).

**Não inventar um formato novo** — o contrato mínimo de 11 itens (`23:38-50`) é o limite mínimo; atributos concretos são decisão.

### 8. Consequências
| Dimensão | A | B |
|---|---|---|
| Output | estável para consumidores | versionado por execução |
| Persistência | um path por lista | N paths + retenção |
| Determinismo | ContentHash estável | por Run |
| Dispatcharr | consome path estável | consome artifact da execução |
| Retenção | limpeza simples | exige política (`DL-113`) |
| API Playlists | generated outputs/download (`41:73-76`) | vários artifacts |
| Testes | golden-file por contrato | hash + retenção |

### 9. Documentos afectados
`11-OUTPUT.md`, `23-DATA-CONTRACTS.md`, `32-DOMAIN-SCHEMA.md`, `16-PERSISTENCE.md`, `31-DECISION-LOCK.md` (DL-019/020), `04-PLAYLIST-STREAM.md`, `18-OBSERVABILITY.md`, `39-CONFIG-SCHEMA.md`, `40-ENTITY-LIFECYCLE.md`, `13-RUNS.md`, `41-API-INVENTORY.md`, `12-DISPATCHARR.md`.

### 10. Dependências
`DG-13 depende de DG-06` (metadados normalizados), `DG-11` (conteúdo) e `DG-12` (nº de listas). Interage com `DG-14g` (cancelamento vs output publicado).

### 11. Resposta esperada do proprietário
"O contrato físico é ___, a enumeração de `publication state` é ___, o naming é ___, e um parcial/replace falhado resulta em ___."

---

## DG-14 — Run / Scheduler

### 1. Pergunta exacta
Decidir sobre: formato/estabilidade do `RunId`; retry (nova Run vs mesma Run); trigger manual durante Run activa; serializar vs coordenar; estado com lock perdido; estado após restart; cancelamento vs efeitos externos; base temporal e DST. **Separar comportamento semântico de mecanismo de lock.**

### 2. Porque é necessária
Determina concorrência, integridade de estado externo, retomada após falha e reprodutibilidade temporal.

### 3. O que a BÍBLIA já decidiu
- `13-RUNS.md:6-14` — Run com `RunId`, timestamps, trigger, estado, snapshot, contadores, erros, artifacts, resultado.
- `13-RUNS.md:20` — snapshot no início; `:34` idempotência; `:38-40` mesmo `RunCoordinator` para scheduler e manual; `:44` "duas execuções concorrentes… devem ser serializadas ou coordenadas por lease/lock"; `:46-49` impedir writers simultâneos/outputs corrompidos/reconciliação concorrente.
- `13-RUNS.md:53-55` — cancelamento propaga; Run cancelado não é sucesso parcial silencioso.
- `33-STATE-MACHINES.md:45-59` — `Created → Running → Succeeded | PartiallySucceeded | Failed | Cancelled`; terminado não regressa a Running.
- `19-FAILURE-MODEL.md:41,43,47` — retries limitados com backoff/cancelamento; idempotency key/reconciliação; após restart identificar Runs incompletos.
- **DL-016/017/018** (`31:54-61`), **DL-109** (`31:127-128`), **DL-114** (`31:142-143`), **DL-115** (`31:145-146`), **DL-116** (`31:148-149`).
- `16-PERSISTENCE.md:16` — timestamps UTC; `:34` sem transacção longa.
- `40-ENTITY-LIFECYCLE.md:30` — Run terminado imutável.

### 4. O que NÃO pode ser alterado
- **DL-109** — uma execução activa por runtime; lock antes de efeitos; lock perdido impede mutações.
- **DL-114** — single-instance por runtime.
- **DL-115** — transacções curtas; chamadas externas fora de transacção.
- **DL-116** — nunca assumir que chamada falhada não produziu efeito.
- **DL-016** — mesmo coordinator; **DL-017** — snapshot; **DL-018** — idempotência.
- Run terminado não regressa a `Running` (`33:59`); parcial não é sucesso (`19:34-37`).

### 5. Alternativa A
Rejeitar trigger manual durante Run activa (conflito); serialização estrita; retry = nova Run (novo `RunId`); lock perdido → `Failed`; restart → Runs não-terminais terminam `Failed`; cancelamento mantém efeitos externos já aplicados + reconciliação; agendamento em **UTC**.

### 6. Alternativa B
Enfileirar trigger manual (espera pelo lock); coordenação por lease; retries de operação dentro da mesma Run (contador por operação); lock perdido → `Cancelled`; restart → `Cancelled`; cancelamento com compensação/rollback quando seguro; agendamento em **timezone configurada com DST**, persistindo UTC.

### 7. Outras alternativas
- **C:** novo estado terminal explícito de interrupção (exige alteração normativa de `33`).
- **D:** modelo misto de agendamento (expressão local, referência UTC).
- **INCOMPATÍVEL COM DECISION LOCK:** reutilizar `RunId` e voltar a `Running` (DL-023, `33:59`, `40:30`); dois writers/reconciliações concorrentes (DL-109, DL-114); continuar mutações após perda de lock (DL-109); retomar sem reconciliação (DL-116, `19:47`); concluir como sucesso parcial silencioso (DL-019, `13:55`).

**Mecanismo de lock é deliberadamente aberto** (`47-BIBLE-AUDIT-FINDINGS.md:30`; `24-DECISIONS.md:14`) — não é decisão funcional.

### 8. Consequências
| Dimensão | A | B |
|---|---|---|
| Scheduler | sem overlap | fila/lease |
| Persistência | RunId opaco + novo registo por retry | contadores por operação / lease |
| Lifecycle | Failed (lock/restart) | Cancelled / estado explícito |
| Dispatcharr | reconciliação posterior (DL-116) | compensação quando seguro |
| Cancelamento | mantém efeitos + reconcilia | compensação/rollback |
| Tempo | UTC | timezone + DST |
| API Runs | start devolve conflito | start devolve "queued" |
| Observabilidade | RunId correlaciona (`18:7-12`) | eventos de lease |
| Testes | concorrência/restart | fila/DST |

### 9. Documentos afectados
`13-RUNS.md`, `33-STATE-MACHINES.md`, `19-FAILURE-MODEL.md`, `16-PERSISTENCE.md`, `31-DECISION-LOCK.md`, `40-ENTITY-LIFECYCLE.md`, `18-OBSERVABILITY.md`, `12-DISPATCHARR.md`, `11-OUTPUT.md`, `14-CONFIGURATION.md`, `39-CONFIG-SCHEMA.md`, `41-API-INVENTORY.md`, `24-DECISIONS.md`, `47-BIBLE-AUDIT-FINDINGS.md`.

### 10. Dependências
`DG-14` relaciona-se com `DG-04` (retry: a decisão de identidade da retentativa é aqui) e `DG-13d` (estado do Run/output num replace falhado). Mecanismo de lock = `DG-21b` (registro documental).

### 11. Resposta esperada do proprietário
"Decido: `RunId` ___, retry ___, trigger concorrente ___, serializar/coordenar ___, lock perdido ___, restart ___, cancelamento ___, timezone/DST ___; o mecanismo de lock fica para o registo documental."

---

## DG-15 — Configuration / permissions

### 1. Pergunta exacta
Preencher a matriz `Operation | UI | API | CLI | ENV | Persistence` apenas com o que a BÍBLIA declara; declarar a **autoridade** de cada propriedade técnica/funcional; definir a operação explícita para alterar estado funcional por CLI/ENV; definir a ACL operação↔papel↔interface.

### 2. Porque é necessária
Determina quem pode alterar o quê, o que sobrevive a restart e se há duas autoridades.

### 3. O que a BÍBLIA já decidiu
- `14-CONFIGURATION.md:5-13` — duas classes; técnica (defaults/ficheiro/env/CLI) com precedência `defaults < config file < environment < CLI`; funcional persistida "não é uma camada que um restart possa sobrescrever silenciosamente".
- `14-CONFIGURATION.md:46` — "O mesmo valor deve ter o mesmo significado no Dashboard, CLI e scheduler."
- `39-CONFIG-SCHEMA.md:5` — três classes distintas (técnica, funcional, secrets).
- `39-CONFIG-SCHEMA.md:41,43,45` — precedência; funcional sobrevive a restart; CLI/env só altera persistido "através de uma operação explicitamente definida".
- `39-CONFIG-SCHEMA.md:47` — "a BÍBLIA deve declarar qual das duas classes é a autoridade. O implementador não deve inferir."
- `35-SECURITY-MODEL.md:5-12` — papéis Administrator/Operator/Runtime; `:57` nenhum endpoint administrativo sem auth.
- **DL-021** (`31:69-70`) — UI/CLI não são autoridade; **DL-112** (`31:136-137`) — secrets por referência/store.

### 4. O que NÃO pode ser alterado
- **DL-021** — UI/CLI são interfaces.
- **DL-112** — secrets isolados do modelo funcional.
- `14:13`/`39:43` — restart não sobrescreve estado funcional.
- `39:45` — CLI/env não altera persistido silenciosamente no arranque.
- `14:46` — mesmo significado em todas as interfaces.

### 5. Alternativa A
Matriz UI/API/CLI/ENV aplica-se apenas a **configuração técnica** não persistida; estado funcional persistido é escrito **exclusivamente** por operações administrativas explícitas do produto; propriedades dual-class têm uma célula de autoridade declarada.

### 6. Alternativa B
Para propriedades que podem ser ambas, deixar uma célula "A DECIDIR" explícita por propriedade até a BÍBLIA declarar (cumprindo `39:47` caso a caso).

### 7. Outras alternativas
- **C:** autoridade declarada por propriedade numa tabela única (técnica vs funcional) + operações explícitas.
- **INCOMPATÍVEL COM DECISION LOCK:** ENV/CLI a sobrescrever estado funcional no arranque (DL-021/`39:45`); UI/CLI como autoridade (DL-021).

**Matriz (preenchida apenas com o que a BÍBLIA declara):**
| Operation | UI | API | CLI | ENV | Persistence |
|---|---|---|---|---|---|
| Config técnica (paths/ports/logging/limits/scheduler/output) | permitida (`39:9-15`) | permitida (`41:6-8`) | permitida (`14:11`) | permitida (`14:11`) | ficheiro/env (não persistida) |
| Estado funcional (providers/sources/policies/ordering/Dispatcharr) | via admin (`35:6`) | via admin (`41:24-84`) | **só por operação explícita** (`39:45`) | **só por operação explícita** (`39:45`) | SQLite (DL-022) |
| Secrets | não expostos | referência (`DL-112`) | referência | referência | secret store (`ADR-0004` Proposed) |
| ACL operação↔papel | **A DECIDIR** | **A DECIDIR** | **A DECIDIR** | **A DECIDIR** | — |

### 8. Consequências
| Dimensão | A | B | C |
|---|---|---|---|
| Autoridade | clara por classe | explícita, incremental | tabela única |
| Persistência | SQLite funcional (DL-022) | idem | idem |
| Segurança | least privilege (`35:44-46`) | idem | idem |
| API | fichas admin necessárias | idem | idem |
| Observabilidade | audit de alterações (`35:50`) | idem | idem |
| Testes | cobertura da matriz | idem | idem |

### 9. Documentos afectados
`14-CONFIGURATION.md`, `39-CONFIG-SCHEMA.md`, `35-SECURITY-MODEL.md`, `17-SECURITY.md`, `41-API-INVENTORY.md`, `22-API-CONTRACTS.md`, `32-DOMAIN-SCHEMA.md`, `16-PERSISTENCE.md`, `24-DECISIONS.md`, `ADR-0004` (evidência).

### 10. Dependências
`DG-15 depende de DG-17` (autoridade). `DG-19` depende de DG-15 (fichas de Sources/Auth/Telegram).

### 11. Resposta esperada do proprietário
"Declaro a autoridade de cada propriedade técnica/funcional ___, a operação explícita para CLI/ENV ___, e a ACL operação↔papel↔interface ___."

---

## DG-16 — Policy schema / merge

### 1. Pergunta exacta
Definir o **schema** de campos de cada policy e a semântica de **merge** para scalar, object, collection, `null`, ausente, inherit e precedência — preservando DL-103 e sem inventar merge recursivo.

### 2. Porque é necessária
Determina o resultado efectivo das policies e, portanto, país/media/validação/elegibilidade/selecção/output/Dispatcharr.

### 3. O que a BÍBLIA já decidiu
- `38-POLICIES.md:7-14` — policy: `Key, Version, Scope, Enabled, Parameters, timestamps, audit`.
- `38-POLICIES.md:18-22` — scopes `system/default; global; group; channel`.
- `38-POLICIES.md:26` — precedência `channel > group > global > default`.
- `38-POLICIES.md:28` — "O merge deve ser campo-a-campo apenas quando o schema declarar campos independentes. Caso contrário, a camada superior substitui a policy inteira."
- `38-POLICIES.md:32` — resolve e congela policies antes do Run.
- `38-POLICIES.md:36-44` — tipos mínimos (Country/Media/Validation/Eligibility/SourcePriority/SourceSelection/Output/Dispatcharr).
- `38-POLICIES.md:48-52` — não misturar responsabilidades.
- **DL-103** (`31:103-107`) — precedência; "Uma camada só existe quando configurada; não existe 'override vazio' que apague uma camada anterior."

### 4. O que NÃO pode ser alterado
- **DL-103** — precedência e proibição de camada vazia anular inferior.
- **DL-101/102** (`31:86-101`) — determinismo; priority 1 = preferível.
- `38:28` — merge campo-a-campo **só** se o schema declarar campos independentes.
- Nenhum termo "inherit" existe na BÍBLIA; sem base para o inventar.

### 5. Alternativa A
Por defeito, a camada superior **substitui a policy inteira**; campo-a-campo apenas para campos que o schema do tipo declarar independentes; campo ausente = não configurado (sem override).

### 6. Alternativa B
Schema por tipo de policy declara explicitamente **campos independentes**; merge campo-a-campo nesses campos; restantes substituem; comportamento de `null` explícito declarado por campo.

### 7. Outras alternativas
- **C:** coleções com semântica declarada por campo (substituir/unir) — a BÍBLIA não decide; tem de ser declarado.
- **INCOMPATÍVEL:** merge recursivo profundo por defeito (sem base em `38:28`); camada vazia a anular inferior (DL-103); ordem de merge diferente de `channel > group > global > default` (`38:26`; DL-103).

### 8. Consequências
| Dimensão | A | B | C |
|---|---|---|---|
| Domínio | previsível, substituição | granular | granular com coleções |
| Persistência | policy Version + audit | idem | idem |
| Pipeline | snapshot congela (`38:32`) | idem | idem |
| Selection | critério activo (`09:36`) | idem | idem |
| API | Policies CRUD + preview (`41:57-58`) | idem | idem |
| Testes | Q6 determinismo (`44:18-19`) | idem | matriz null/collection |

### 9. Documentos afectados
`38-POLICIES.md`, `39-CONFIG-SCHEMA.md`, `31-DECISION-LOCK.md`, `09-SELECTION.md`, `32-DOMAIN-SCHEMA.md`, `34-PIPELINE-CONTRACTS.md`, `41-API-INVENTORY.md`, `22-API-CONTRACTS.md`, `44-QUALITY-GATES.md`.

### 10. Dependências
`DG-16` alimenta `DG-09`/`DG-10` (EligibilityPolicy) e `DG-19` (ficha de Policies). Relaciona-se com `DG-17` (autoridade da policy).

### 11. Resposta esperada do proprietário
"O schema por tipo é ___, e o merge é (substituição / campo-a-campo) ___, com semântica de `null` e coleções ___."

---

## DG-17 — Authority matrix

### 1. Pergunta exacta
Para cada conceito central, qual é a **autoridade normativa única** (ou que autoridades concorrentes existem)?

### 2. Porque é necessária
`28-TRUTH-AND-TRACEABILITY.md:5` exige uma única autoridade por conceito; sem isso há risco de segunda autoridade (regressão, `45:66`) e de conflito entre documentos.

### 3. O que a BÍBLIA já decidiu
- `28-TRUTH-AND-TRACEABILITY.md:5` — "Para cada conceito deve existir uma única autoridade."
- `28:7-16` — exemplos: posição→OrderingList; identidade→CanonicalChannel; origem→Source; estado técnico→Observation; elegibilidade→Eligibility; escolha→Selection; output→GeneratedPlaylist; recursos remotos→Dispatcharr+Ownership.
- `DL-001` (`31:9-10`) — catálogo é a identidade.
- `05-CATALOGUE.md:11,23-27` — catálogo é a autoridade; sem lista paralela.

### 4. O que NÃO pode ser alterado
- **DL-001, DL-008, DL-009, DL-010, DL-011, DL-013, DL-014, DL-017, DL-116** (cf. `31`).
- Nenhuma decisão pode criar uma segunda autoridade (`45:66`).

**Matriz — preenchida apenas com o que a BÍBLIA declara; conflitos assinalados:**
| Concept | Single normative authority (declarada?) | Evidence |
|---|---|---|
| Canonical identity | `CanonicalChannel` — **declarada** | `DL-001` `31:9-10`; `28:8`; `05:11,23-27` |
| Country | **concorrentes:** `CountryProfile` (conceptual) vs `country.json` (ADR-0001, Proposed) vs overlay do operador | `06:5`; `02:18`; `DL-106` `31:118-119`; sem secção em `32` |
| Media | **concorrentes:** `MediaClassification` (resultado) vs `MediaPolicy` (entrada) | `02:19`; `06:27`; `38:38`; sem secção em `32` |
| Source | `Source` — **declarada** | `28:10`; `07:15`; `DL-005/006` |
| Validation | **concorrentes:** `Observation` (estado) vs processo Validation | `28:11`; `08:18,20-24`; `DL-008` |
| Eligibility | `Eligibility` — **declarada** (derivada) | `28:12`; `08:28`; `DL-009`; `32:181-191` |
| Priority | `SourcePriorityPolicy` — **parcial** (em `02`, não em `28`) | `02:23,50`; `09:5-6`; `DL-010` |
| Selection | **concorrentes:** resultado Selection vs `SourceSelectionPolicy` | `28:13`; `09:9`; `02:24` |
| Ordering | `OrderingList` — **declarada** | `28:9`; `10:5-9`; `DL-011` |
| Output | `GeneratedPlaylist` — **declarada** | `28:14`; `11:30-34`; `DL-019` |
| Dispatcharr actual | **concorrentes:** estado remoto observado vs entidade persistida | `28:15`; `12:5,27`; `DL-013`; `DL-116` |
| Dispatcharr desired | **concorrentes:** intenção calculada vs `GeneratedPlaylist` vs `DispatcharrPolicy` | `DL-116`; `12:27-31`; `38:44` |
| Ownership | `OwnershipRecord` — **declarada** | `28:15`; `12:14-22`; `DL-014`; `32:235-243` |
| Policy | **concorrentes:** `Policy` genérico vs por-tipo vs snapshot resolvido | `38:7-32`; `39:51`; sem secção em `32` |
| Run | `Run` + `RunSnapshot` — **declarada** | `13:5-14`; `45:51`; `DL-017`; `32:245-255` |

### 5. Alternativa A
Declarar autoridade para os conceitos em falta (Country, Media, Priority, Selection, Dispatcharr actual/desired, Policy) sem criar entidades novas.

### 6. Alternativa B
Declarar autoridade **e** criar/adicionar as entidades em falta a `32-DOMAIN-SCHEMA.md` (ex.: `CountryProfile`, `MediaClassification`, `Selection`), com migração.

### 7. Outras alternativas
- **C:** declarar autoridade por **conceito** e distinguir explicitamente "autoridade de dados" de "autoridade de decisão".
- **INCOMPATÍVEL:** qualquer opção que introduza segunda autoridade (`45:66`) ou contradiga DL-001/008/009/010/011/013/014.

### 8. Consequências
| Dimensão | A | B | C |
|---|---|---|---|
| Domínio | clarifica sem novas entidades | cria entidades em falta | separa dados/decisão |
| Persistência | sem migração | migração + schema (`16:30`) | possível schema |
| Rastreabilidade | melhora `46:30` | idem | idem |
| API | fichas podem fechar | novas operações possíveis | idem |
| Testes | Q1 (`44`) | Q1 + migração | Q1 |
| Regressão | elimina segunda autoridade | idem | idem |

### 9. Documentos afectados
`28-TRUTH-AND-TRACEABILITY.md`, `02-DOMAIN.md`, `32-DOMAIN-SCHEMA.md`, `05/06/07/08/09/10/11/12/13`, `38-POLICIES.md`, `31-DECISION-LOCK.md`, `45-BIBLE-AUDIT.md`, `46-REQUIREMENT-TRACEABILITY.md`, `ADR-0001` (evidência).

### 10. Dependências
`DG-17` é transversal: alimenta `DG-12` (Media), `DG-15` (config) e `DG-16` (Policy).

### 11. Resposta esperada do proprietário
"Declaro como autoridade única de cada conceito: Canonical identity ___, Country ___, Media ___, Source ___, Validation ___, Eligibility ___, Priority ___, Selection ___, Ordering ___, Output ___, Dispatcharr actual ___, Dispatcharr desired ___, Ownership ___, Policy ___, Run ___."

---

## DG-18 — Backup / restore / migration / downgrade / retention

### 1. Pergunta exacta
Separar **fresh install, restore, upgrade, migration, downgrade e retention**; indicar quais são decisões funcionais e quais são procedimentos operacionais.

### 2. Porque é necessária
Determina o que é preservado/regenerado, o que acontece a secrets e o que pode ser apagado.

### 3. O que a BÍBLIA já decidiu
- `20-OPERATIONS.md:5` — "Uma instalação nova deve conseguir chegar a estado funcional sem passos manuais escondidos."
- `20-OPERATIONS.md:19-26` — upgrade: preservar dados; aplicar migrations; validar readiness; iniciar nova versão; health; rollback por imagem + restore.
- `20-OPERATIONS.md:30-38` — backup inclui DB, configuração, sessões, artifacts necessários, dados de runtime, versão; secrets protegidos.
- `20-OPERATIONS.md:42-45` — restore testável: `backup → restore → migration → health → functional test`.
- `16-PERSISTENCE.md:44-48` — migrations determinísticas, testadas, preservam dados, declaram incompatibilidades, com rollback/restore.
- `17-SECURITY.md:57-59` — backups podem conter secrets; confidenciais.
- **DL-022** (`31:72-73`) — SQLite é autoridade persistente; **DL-023** (`31:75-76`) — histórico vs estado; **DL-107** (`31:121-122`) — baseline upgrade não apaga alterações locais; **DL-112** (`31:136-137`) — secrets; **DL-113** (`31:139-140`) — retenção explícita, sem eliminação silenciosa.
- `20-OPERATIONS.md:49` — RPO/RTO definidos pelo ambiente, não pelo software.

### 4. O que NÃO pode ser alterado
- **DL-022, DL-023, DL-107, DL-112, DL-113**.
- Migração destrutiva sem declarar incompatibilidade (`16:46`).
- Cache/artifacts como autoridade de reconstrução (DL-022).
- Software a inventar RPO/RTO (`20:49`).

### 5. Alternativa A
Downgrade = **rollback por imagem + restore**; sem downgrade in-place de schema; retenção explícita e configurável; migrations determinísticas com rollback/restore.

### 6. Alternativa B
Downgrade in-place **apenas quando o rollback SQL for seguro**; caso contrário restore; mesma política de retenção/secrets.

### 7. Outras alternativas
- **C:** distinguir formalmente "estado funcional preservado", "artifacts regeneráveis" e "secrets re-provisionados".
- **INCOMPATÍVEL:** eliminação silenciosa dentro da retenção (DL-113); migração destrutiva sem declarar incompatibilidade (`16:46`); cache/artifacts como autoridade (DL-022); software a impor RPO/RTO (`20:49`).

### 8. Consequências
| Dimensão | A | B | C |
|---|---|---|---|
| Persistência | migrations + restore | rollback SQL quando seguro | separação por classe de dado |
| Segurança | secrets no backup protegidos (`17:57-59`) | idem | re-provisão explícita |
| Lifecycle | Run/evidence preservados (DL-023) | idem | idem |
| Operação | procedimento claro | mais complexo | mais explícito |
| Testes | Q11 (`44:33-34`) | + testes de rollback SQL | + matriz de dados |
| RPO/RTO | definidos pelo ambiente | idem | idem |

### 9. Documentos afectados
`20-OPERATIONS.md`, `16-PERSISTENCE.md`, `26-RECONSTRUCTION.md`, `36-RECONSTRUCTION-ORDER.md`, `40-ENTITY-LIFECYCLE.md`, `17-SECURITY.md`, `47-BIBLE-AUDIT-FINDINGS.md`, `24-DECISIONS.md`, `44-QUALITY-GATES.md`.

### 10. Dependências
`DG-18` relaciona-se com `DG-19` (artifacts/retention) e com os registos documentais (ADR-0006/0005/0004, `Proposed`).

### 11. Resposta esperada do proprietário
"Downgrade é ___, retenção é ___, secrets no backup/restore são ___, e a diferença restore fresh vs upgrade é ___."

---

## DG-19 — API contracts

### 1. Pergunta exacta
Por família de `41-API-INVENTORY.md`: família, operações, decisões semânticas dependentes, contratos em falta; e quais contratos só podem fechar depois de DGs específicas. Confirmar quais famílias já têm ficha completa (em particular Country).

### 2. Porque é necessária
Determina o que é implementável; `22-API-CONTRACTS.md:46-48` declara que família sem ficha suficiente é `BIBLE_GAP`.

### 3. O que a BÍBLIA já decidiu
- `41-API-INVENTORY.md:1-3` — inventário define a superfície; cada operação **deve** ter ficha completa em `22` antes da implementação final; uma linha do inventário não é contrato.
- `41:86` — endpoint mutante requer auth, autorização e auditoria.
- `22-API-CONTRACTS.md:28-46` — campos mínimos da ficha.
- `22:46,48` — família sem ficha suficiente é `BIBLE_GAP`; todo o endpoint de `41` deve ter contrato.
- `22:64-131` — **fichas completas da família Country** (4 endpoints).
- **DL-110** (`31:130-131`) — contratos incompatíveis recebem nova versão.

### 4. O que NÃO pode ser alterado
- **DL-110** — versionamento de contratos.
- `22:46,48` — não implementar endpoints sem ficha.
- `22:71` — GET read-only sem criação/escrita.
- `41:86` — auth/authz/audit em mutações.

### 5. Alternativa A
Tratar `22` §6 (Country) como ficha da família e **adicionar Country ao inventário `41`**, resolvendo a inconsistência; restantes fichas só após as DGs dependentes fecharem.

### 6. Alternativa B
Manter Country como excepção documentada no inventário (nota explícita) e produzir as restantes fichas apenas após as DGs dependentes.

### 7. Outras alternativas
- **C:** produzir primeiro um **esqueleto de ficha por família** (operações + dependências), preenchendo cada contrato à medida que a DG respectiva fecha.
- **INCOMPATÍVEL COM DL-110** / BÍBLIA: implementar endpoints sem ficha (`22:48`); mudança incompatível sem nova versão (DL-110); GET com side effects (`22:71`).

**Famílias, operações e dependências:**
| Família (`41`) | Operações | DG dependente | Ficha em `22` |
|---|---|---|---|
| System | lifecycle/health/version | — | ausente |
| Auth | login/logout/current user/CSRF | DG-15, ADR-0004 | ausente |
| Telegram | config/auth/start/code/password/disconnect | DG-15, ADR-0004 | ausente |
| Sources | list/CRUD/enable/test/status | DG-15 | ausente |
| Discovery | start/list/details/accept-reject | DG-01/DG-02 | ausente |
| Catalogue | channels/details/aliases/external identities/import-export | DG-01/DG-08 | ausente |
| Review | list/details/resolve/ignore/reopen | DG-08 | ausente |
| Validation | run/observations/eligibility | DG-09/DG-10/DG-16 | ausente |
| Policies | CRUD/effective preview | DG-16 | ausente |
| Ordering | lists/items/import-export/preview | DG-12 | ausente |
| Runs | start/cancel/status/history/artifacts | DG-14/DG-18 | ausente |
| Playlists | generated/download/validation result | DG-13/DG-18 | ausente |
| Dispatcharr | config/test/dry-run/sync/reconciliation/ownership | DG-14/DG-18 | ausente |
| Country | (não listada em `41`) | DG-17 | **completa** `22:64-131` |

### 8. Consequências
| Dimensão | A | B | C |
|---|---|---|---|
| Inventário | consistente com `22:48` | excepção documentada | consistente, incremental |
| Implementação | desbloqueia Country | idem | gates por DG |
| Testes | contrato Country | idem | por família |
| Clientes | sem surpresa | idem | idem |
| Governação | resolve conflito documental | mantém conflito explícito | progressivo |

### 9. Documentos afectados
`41-API-INVENTORY.md`, `22-API-CONTRACTS.md`, `24-DECISIONS.md`, `23-DATA-CONTRACTS.md`, `34-PIPELINE-CONTRACTS.md`, `35-SECURITY-MODEL.md`, `17-SECURITY.md`, `31-DECISION-LOCK.md`, `ADR-0001` (evidência).

### 10. Dependências
`DG-19 depende de DG-15`, `DG-16`, `DG-18` e das DGs semânticas que cada ficha referencia (DG-08/DG-09/DG-10/DG-12/DG-13/DG-14).

### 11. Resposta esperada do proprietário
"Confirmo o tratamento do inventário/Country (A/B/C) ___, e a ordem de produção das fichas por família ___."

---

## DG-20 — Governação normativa

### 1. Pergunta exacta
Definir a fronteira entre **norma** e **detalhe físico**: quando um detalhe deve estar na BÍBLIA vs na implementação; como se fazem testes de aceitação; como se altera a norma; qual a autoridade documental — sem transformar ADRs em decisões funcionais.

### 2. Porque é necessária
Determina se o sistema pode divergir com duas implementações materialmente diferentes sem violar a BÍBLIA.

### 3. O que a BÍBLIA já decidiu
- `00-BIBLE.md:5,11,13` — BÍBLIA é normativa; divergência = problema de implementação/migração.
- `00-BIBLE.md:17-28` — hierarquia de autoridade; ADR não pode contradizer a BÍBLIA.
- `00-BIBLE.md:56` — teste de insuficiência: duas soluções materialmente diferentes.
- `00-BIBLE.md:58-73` — elementos obrigatórios de decisão.
- `00-BIBLE.md:77,79` — alteração normativa identifica secção/motivo/impactos; delegação mantém completude.
- `00-BIBLE.md:132` — detalhe necessário não especificado deve ser decidido antes.
- `29-AGENT-RULES.md:16-20` — não inventar; identificar lacuna; propor decisão.
- `29-AGENT-RULES.md:56,58` — `Proposed` não é fonte final nem implementável.
- `45-BIBLE-AUDIT.md:55-58,66` — integridade de delegação; regressão arquitectural.
- `46-REQUIREMENT-TRACEABILITY.md:23-34,40-42,52` — matriz requisito→BÍBLIA→contrato→teste; não marcar COMPLIANT por nome.
- `32-DOMAIN-SCHEMA.md:3` — critério de detalhe físico relevante.
- `16-PERSISTENCE.md:30` — constraints no schema normativo antes da implementação.
- `43-ANTI-PATTERNS.md:19` — proibido resolver "A DECIDIR" por iniciativa do agente.

### 4. O que NÃO pode ser alterado
- Hierarquia de autoridade (`00:17-26`).
- **DL-021** (`31:69-70`) — UI/CLI não são autoridade.
- `Proposed` ADR não implementável (`29:56`).
- Proibido introduzir segunda autoridade/transição não definida/decisão implícita (`45:66`).
- Regra de alteração de DL (`31:151-155`).

### 5. Alternativa A
Um detalhe é normativo **apenas** se afecta significado/tipo/nullability/cardinalidade/unicidade/referências/lifecycle/invariantes ou comportamento/migração (`32:3`); caso contrário, a implementação é livre.

### 6. Alternativa B
Para detalhes que afectam comportamento/migração, é **obrigatória** uma secção normativa de schema/migration antes da implementação (`32:3`, `16:30`); ausência → `BIBLE_GAP`, não implementável (`29:18`, `43:19`).

### 7. Outras alternativas
- **C:** combinar A e B com uma **checklist de aceitação por requisito** preenchida pela auditoria (`46:40-42`).
- **INCOMPATÍVEL:** tratar ADR `Proposed` como decisão (`00:28`, `24:29`, `29:56`); agente resolver "A DECIDIR" (`43:19`); declarar `COMPLIANT` por nome/compilação (`46:34`, `43:20`); introduzir segunda autoridade (`45:66`).

### 8. Consequências
| Dimensão | A | B | C |
|---|---|---|---|
| Governação | fronteira clara | fronteira + gate | fronteira + aceitação mensurável |
| Rastreabilidade | melhora | idem | matriz `46` |
| Implementação | liberdade em detalhes | detalhes bloqueiam até norma | por requisito |
| Testes | comportamentais | comportamentais + schema | acceptance gates |
| ADRs | evidência | evidência | evidência |
| Regressão | evita segunda autoridade | idem | idem |

### 9. Documentos afectados
`00-BIBLE.md`, `29-AGENT-RULES.md`, `45-BIBLE-AUDIT.md`, `46-REQUIREMENT-TRACEABILITY.md`, `32-DOMAIN-SCHEMA.md`, `16-PERSISTENCE.md`, `43-ANTI-PATTERNS.md`, `28-TRUTH-AND-TRACEABILITY.md`, `24-DECISIONS.md`, `31-DECISION-LOCK.md`, `44-QUALITY-GATES.md`, `23-DATA-CONTRACTS.md`, `docs/adr/README.md`.

### 10. Dependências
`DG-20` é transversal; condiciona o fecho de todas as DGs (fronteira norma/implementação) e o preenchimento da matriz de rastreabilidade (`DG-21e`).

### 11. Resposta esperada do proprietário
"Adopto a fronteira norma/detalhe físico (A/B/C) ___, o processo de alteração normativa ___, e o método de testes de aceitação ___."

---

# 7. Resumo final

## 7.1 Pergunta por DG

```text
DG-01 — O DiscoveryCandidate é persistente único ou por ocorrência, e qual a sua identidade/ligação a conta?
DG-02 — O que provoca Expired e como se reactiva, preservando histórico?
DG-03 — Qual o input M3U mínimo e a regra parcial-vs-total?
DG-04 — Que condições são terminais vs retryable e qual o efeito persistido?
DG-05 — Que política SSRF normativa adoptar (classes/DNS/redirects)?
DG-06 — Quais as transformações/canonicalização/fingerprint normativos?
DG-07 — O fuzzy é opcional/mandatório, e o que resulta abaixo do limiar?
DG-08 — Que efeitos concretos têm Resolve, Ignore, alias e mudança de identidade?
DG-09 — Como mapeia cada categoria de Validation para Eligibility?
DG-10 — Como transita Eligibility em falha/recuperação/ausência/histerese/expiração?
DG-11 — O que fazer quando um canal conhecido não tem fonte elegível?
DG-12 — Unicidade de Position, duplicados, não listados, listas, Radio/VOD?
DG-13 — Qual o contrato físico do M3U, publication state, naming e parcial?
DG-14 — RunId, retry, trigger concorrente, lock, restart, cancelamento, DST?
DG-15 — Autoridade de config, operação explícita e ACL de permissões?
DG-16 — Qual o schema de policy e a semântica de merge?
DG-17 — Qual a autoridade única de cada conceito central?
DG-18 — Como se separam fresh/restore/upgrade/migration/downgrade/retention?
DG-19 — Quais as fichas de API em falta e de que DGs dependem?
DG-20 — Qual a fronteira entre norma e detalhe físico e o método de aceitação?
```

## 7.2 Decisões independentes

```text
DG-03 (parsing M3U)
DG-05 (SSRF)
DG-06 (normalização/canonicalização/fingerprint)
DG-20 (governação normativa)
```

## 7.3 Decisões dependentes

```text
DG-02 depende de DG-01
DG-07 depende de DG-06
DG-09 depende de DG-04 e DG-16
DG-10 depende de DG-09
DG-11 depende de DG-10
DG-13 depende de DG-06, DG-11 e DG-12
DG-12 relaciona-se com DG-17
DG-15 depende de DG-17
DG-16 relaciona-se com DG-17
DG-19 depende de DG-15, DG-16, DG-18 e das DGs semânticas referenciadas
DG-14 relaciona-se com DG-04 (identidade de retentativa) e DG-13d
DG-18 relaciona-se com DG-19
```

## 7.4 Ordem recomendada

```text
1. DG-01, DG-03, DG-05, DG-06, DG-16, DG-17, DG-20
2. DG-02, DG-07, DG-04
3. DG-09, DG-10, DG-11
4. DG-12, DG-13, DG-14
5. DG-15, DG-18
6. DG-19
7. DG-21 (fecho documental, não funcional)
```

## 7.5 Estado Git

```text
branch: feature/phase-9c-first-run-dashboard
HEAD: bde06122740b6b8d2c6ff493fff6a0e4326c4b9c
working tree: BIBLE_DECISION_DOSSIER_1.0.md (untracked) — único ficheiro criado/modificado por esta tarefa
```

Confirmado por verificação Git read-only: os únicos ficheiros rastreados modificados são `AGENTS.md`, `CONTRIBUTING.md`, `README.md`, `ROADMAP.md` (**pré-existentes**, conjunto inalterado). Nenhum documento em `docs/Reestructure/`, nenhum ADR, código, teste, `PROJECT_STATUS` ou wave foi alterado. Nenhum commit criado. O único artefacto desta tarefa é `BIBLE_DECISION_DOSSIER_1.0.md`.

---

## 8. Limitações

- Dossier **documental**; não executou código nem testes.
- Não decide valores nem alternativas; apresenta as opções legítimas e as incompatibilidades com o Decision Lock.
- Referências `file:line` válidas para o estado actual dos ficheiros.
- Consequências marcadas `INFERENCE` não são normativas.
- ADRs foram usados apenas como evidência; `Proposed` não é tratado como decisão.
