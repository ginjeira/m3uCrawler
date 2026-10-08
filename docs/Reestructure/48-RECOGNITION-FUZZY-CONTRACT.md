# 48 — Contrato de Reconhecimento Fuzzy (W5.3 — IMPLEMENTADO; origem DESIGN)

**Estado:** IMPLEMENTADO (W5.3) — ratificado por `DL-118` (`31-DECISION-LOCK.md`); ver §17
**Baseline de design (pré-implementação):** `4721dc3` — W5.2 `feat(5.2): enforce deterministic recognition order`
**Implementação:** `84b35f5` — `feat(5.3-5.4): implement fuzzy recognition and review lifecycle`
**Âmbito:** fechar a semântica do passo 6 (fuzzy) de Recognition deixada como
`PARAMETER_GAP` por W5.0/W5.1/W5.2.

> **W5.3 implementado.** Decisões F1–F14 ratificadas em `05 §4.2`, `32`, `38
> §5.1`, `31` (DL-118), `46` e no `BIBLE_IMPLEMENTABILITY_GAP_MANIFEST` (Anexo O).
> Implementação: `Services/Recognition/FuzzyRecognition.cs` +
> `CatalogResolver.ResolveAsync` passo 6. Nenhuma migration, alteração de API
> pública incompatível, de Review lifecycle ou de ordem W5.2. `OPEN-D2` fica
> resolvido como "path legacy fora de scope"; `OPEN-D3` resolvido pela extensão
> `CatalogResolution.FuzzyScore`/`FuzzyDiagnostic`.

> Este documento **não contradiz** W5.0/W5.2. Adiciona semântica aos parâmetros
> deliberadamente abertos (`Fuzzy.Threshold`, `Fuzzy.AmbiguityMargin`,
> `Fuzzy.Weights`) e fecha métrica/campos/universo/desempate. Não há
> `DECISION REOPEN REQUIRED`. A ratificação deste documento nos documentos
> normativos (`05`, `32`, `38`, `31`, `46`) é uma proposta, não uma alteração
> silenciosa da BÍBLIA.

> **Reconciliação documental (2026-09-20):** W5.3 foi ratificado por `DL-118`
> (`31-DECISION-LOCK.md`) e implementado em `84b35f5`
> (`feat(5.3-5.4): implement fuzzy recognition and review lifecycle`);
> traceability em `46 §W5.3` (`COMPLIANT`, `WaveW53FuzzyRecognitionTests`, 35).
> O texto de design pré-implementação (baseline `4721dc3`) é mantido como
> histórico e anotado onde relevante (§1.5, §15); não é reescrito. A `OPEN-D2`
> (motor legacy hardcoded) permanece `DIVERGENT`/fora de scope e M.4/C7
> permanecem `OUT`/abertos — esta nota não os fecha.

---

## 0. Convenção de notação

- `FACT` — comportamento demonstrado pelo código/testes no baseline.
- `INFERENCE` — interpretação arquitectural do agente, não normativa.
- `DECISION` — decisão de design W5.3 proposta; requer ratificação.
- `PARAMETER` — valor operacional; não fixado na norma.
- `OPEN` — insuficiência de evidência para fechar sem decisão humana.

---

## 1. Inventário do fuzzy actual (OBJECTIVO A)

### 1.1 `FuzzyMatcher` — `m3uCrawler/Services/Matching/FuzzyMatcher.cs`

`FACT`

- `ExactScore = 100`, `NumericSiblingPenalty = 50`.
- Domínio do score: `0..100` (inteiro). O ramo fuzzy é limitado a `0..99`;
  apenas o exacto após normalização devolve `100`.
- Normaliza ambos os lados com `ChannelNormalizer.Normalize`.
- Ordem interna:
  1. vazio → `0` (`empty`);
  2. igualdade `Ordinal` após normalização → `100` (`exact`);
  3. substring (`q.Contains(c) || c.Contains(q)`, ambos `length >= 3` e
     `min/max >= 0.4`):
     - conflito de token numérico → `min(50, 60) = 50` (`numeric-sibling-guard`);
     - caso contrário → `95` (`substring`);
  4. tokens ordenados `Ordinal`; `TokenSetScore` = F1 de token-set
     (`round(100 · 2·|∩| / (|Q|+|C|))`, clamp `0..99`);
  5. `Levenshtein` sobre tokens unidos por espaço; `levScore =
     round(100 · (1 − lev/ml))`, clamp por `min(99, levScore)`;
  6. `ratio = max(qTokenSetScore, min(99, levScore))`;
  7. guardas de irmãos numéricos (contagem de tokens numéricos desigual →
     `min(ratio,50)`; token numérico com diferença de um dígito → `min(ratio,50)`);
  8. final `clamp(ratio, 0, 99)` (`fuzzy`).
- Razões observáveis: `empty`, `exact`, `substring`, `numeric-sibling-guard`,
  `fuzzy`.
- Determinismo: todos os passos são deterministas (ordenação `Ordinal`,
  `HashSet` apenas usado para contagem). Não há dependência de ordem de
  inserção.

### 1.2 `MatchScorer` — `m3uCrawler/Services/Matching/MatchScorer.cs`

`FACT`

- `Exact = 95`, `MinSafe = 80` (constantes **hardcoded**).
- `Classify(score, otherScores, threshold)`:
  - `score < threshold` → `None`;
  - `margin = 5` **hardcoded**; ambíguo se **algum** `other >= threshold` e
    `|other − score| <= 5`;
  - ambíguo → `Ambiguous`; senão `score >= 95` → `Exact`, senão `Matched`.

### 1.3 `MatchingOptions` — `m3uCrawler/Models/MatchingOptions.cs`

`FACT`

- `MatchThreshold = 80` (default), `ExactMatchScore = 95` (**não usado** pelo
  `MatchScorer`, que usa a própria constante `95`),
  `AmbiguityMargin = 5` (**não usado** pelo `MatchScorer`, que usa o literal
  `5`), `Aliases` (dicionário, **não usado** pelo `FuzzyMatcher`).

### 1.4 `ChannelMatcher` (path legacy curado) — `Services/Matching/ChannelMatcher.cs`

`FACT`

- `FindCuratedMatch` faz scan de **todos** os canais existentes, calcula
  `FuzzyMatcher.Score(aliasCanonical ?? identity, Normalize(channel.Name))`,
  ordena desc, `Take(5)`, e classifica com `MatchScorer` + `MatchThreshold`.
- Em ambiguidade, o `second` é escolhido como o **pior** score
  (`OrderBy(...).Last()`) e recebe o **mesmo** `MatchScore` do topo — quirk
  legacy; C3 (curated ambiguity sem `ReviewItem`).
- O path `Unknown` **não** usa fuzzy (só igualdade/alias exactos).

### 1.5 `CatalogResolver` (Recognition W5.2) — `Services/Catalog/CatalogResolver.cs:300-311`

`FACT`

- O passo 6 (fuzzy) é um **placeholder vazio**: quando
  `policy.FuzzyEnabled == true` não executa nada e não chama `FuzzyMatcher`;
  cai para `Unknown`.
- Ordem implementada: `IdentityRule → identidade externa exacta →
  CanonicalExact → NormalizedName → KnownAlias → ExplicitHeuristic →
  gate fuzzy (no-op) → Unknown`.

> **Nota (reconciliação 2026-09-20):** este parágrafo descreve o baseline
> histórico **pré-W5.3** (`4721dc3`), quando o passo 6 era um placeholder vazio.
> Está substituído pelo passo fuzzy implementado (`84b35f5`; diagnóstico em
> §14/`OPEN-D3`; ratificação `DL-118`). Mantido como registo do estado de design,
> não do estado actual.

### 1.6 `RecognitionPolicy` — `Services/Recognition/RecognitionPolicy.cs`

`FACT`

- `FuzzyEnabled=false` por defeito; `FuzzyThreshold`, `FuzzyAmbiguityMargin`,
  `FuzzyWeightsJson` são `null` = "não decidido" (`PARAMETER_GAP`).
- Scopes `system | global | group:{key} | channel:{key}`; precedência
  `channel > group > global > system`. Substituição **por inteiro** (sem merge
  campo-a-campo nesta fase).
- Snapshot imutável por Run (`recognition_policy_snapshots`).

### 1.7 Documentação normativa

`FACT`

- `05-CATALOGUE.md §4/§4.1`: fuzzy opt-in; passo 6; abaixo do threshold →
  `UNKNOWN` (sem candidato) ou `AMBIGUOUS` (plausíveis); um único candidato
  forte pode → `CANONICAL`; `MatchConfidence ∈ 0..1` **não** é o score de fuzzy.
- `05 §5`: dois candidatos sem desempate normativo → `AMBIGUOUS`; não existe
  "escolher o primeiro".
- `05 §7/§8`: número/posição **nunca** é identidade; qualidade **nunca** entra
  em Matching.
- `38 §5.1` / `32`: schema mínimo; threshold/margem/pesos `PARAMETER_GAP`.
- `31-DECISION-LOCK.md DL-117`: fuzzy opt-in; abaixo do threshold; único
  candidato só produz `CANONICAL` se satisfizer o desempate da policy.

`INFERENCE` — Existem **dois** motores fuzzy no repositório: o legacy
(`ChannelMatcher` + `MatchScorer`, hardcoded, activo no path curado) e o novo
Recognition (`CatalogResolver`, W5.2, fuzzy não implementado). O presente
contrato fecha **o passo 6 de Recognition**. A reconciliação do path legacy é
uma decisão de scope separada (§14, `OPEN-D2`).

---

## 2. Campos do fuzzy (OBJECTIVO B)

| Field | Used by fuzzy? | Reason | Normalization | Normative source |
|---|---|---|---|---|
| `DisplayName` | **YES** | evidência de nome do canal; é o sinal observável da fonte | `ChannelNormalizer.Normalize` | `05 §4.1` (nome normalizado = evidência) + W5.3 F1 |
| `ChannelAlias.NormalizedAlias` | **YES** | alias conhecido é evidência de identidade já existente | já persistido normalizado; re-normalização idempotente | `05 §4.1` + W5.3 F1 |
| `CanonicalChannel.Key` | **NO** | já resolvido exactamente no passo 2 (`CanonicalExact`); é slug interno, não-facing à fonte | — | `05 §4` passos 2/3 |
| `ExternalIdentity` / `tvg-id` | **NO** | passo 1/2 exacto; repetir em fuzzy contradiria a prioridade exacta | — | `05 §4`, D9, DL-117 |
| provider namespace | **NO** (não é campo de score) | namespace qualifica identidade externa, não similaridade de nome | — | `05 §4.1` |
| `Group` | **NO** | agrupamento editorial, não identidade; pode ser blocking futuro, nunca score | — | `05 §4`/§7 |
| número / posição | **NO** (restrição fechada) | número não é identidade | — | `05 §7` |
| `Quality` (HD/FHD/SD/4K) | **NO** (restrição fechada) | qualidade nunca é Matching | — | `05 §8` |
| `Fingerprint` | **NO** (restrição fechada) | fingerprint é evidência técnica de stream, não identidade de canal | — | `05 §4` (DL-001/DL-002) |
| `Country` / `Group` | **NO** (score); possível blocking futuro | não é evidência de nome; pode reduzir universo sem decidir identidade | — | W5.3 F1 |

`DECISION F1` — O passo fuzzy compara a identidade normalizada da query contra
(a) `CanonicalChannel.DisplayName` normalizado e (b) cada
`ChannelAlias.NormalizedAlias` do canal. Nenhum outro campo produz score.

---

## 3. Contrato de normalização (OBJECTIVO C)

`FACT` — `ChannelNormalizer.Normalize` (`Services/Matching/ChannelNormalizer.cs`)
aplica, por esta ordem:

1. `FormD` + remoção de diacríticos (`\p{Mn}`);
2. pontuação de separação → espaço (`: | / \ - _ . , ;`);
3. split letra↔dígito (`([A-Za-zÀ-ÿ])(\d)` e `(\d)([A-Za-zÀ-ÿ])`);
4. remoção de prefixo geo (`^(?:[A-Za-z]{2,3})[:|]`);
5. remoção de `[...]`; remoção de conteúdo entre parênteses (com aninhamento);
6. remoção de tags de qualidade (`4K|UHD|FHD|HD|SD|HDR|HEVC`);
7. remoção de região (`East|West|...|Leste|Oeste|Norte|Sul`);
8. remoção de tokens de país (`Portugal|Espanha|...|PT|BR`);
9. colapso de espaços + `Trim`;
10. `FormC` + `ToLowerInvariant`.

`DECISION F2` — A normalização normativa do fuzzy é exactamente
`ChannelNormalizer.Normalize`, aplicada à query e ao `DisplayName` do candidato.
Aliases já persistidos em `NormalizedAlias` são usados como estão
(normalização idempotente). Case, acentos, pontuação, whitespace, **tags de
qualidade/região/país**, parênteses e brackets ficam cobertos por este contrato.

`OPEN-N1` — Normalização específica por idioma (stopwords, abreviaturas
`Int.`/`Internacional`, artigos) **não** está implementada. Fica fora do v1;
é uma evolução da métrica/normalização, não um bloqueio (o normalizador actual
é determinista e evidência suficiente para implementar).

---

## 4. Métrica (OBJECTIVO D)

`FACT` — Métrica existente = `FuzzyMatcher` (§1.1): exact → substring →
`max(token-set F1, Levenshtein normalizado)` → guardas numéricas.

`DECISION F3` — Manter o `FuzzyMatcher` existente como **base normativa** do
passo fuzzy, sem reescrever a métrica no v1. Justificação técnica:

| Critério | `max(F1, Levenshtein)` + substring (existente) |
|---|---|
| correcção | cobre abreviaturas PT (`CNN Int`), ruído de pontuação e reordenação de tokens |
| determinismo | total (ordenação `Ordinal`; sem estado de inserção) |
| explicabilidade | `Reason` por regra (`exact`/`substring`/`fuzzy`/guard) |
| compatibilidade catálogo TV | nomes curtos e tokens numéricos são o caso comum |
| custo | O(len²) por par; adequado ao catálogo actual |
| falsos positivos | mitigados pelas guardas numéricas e pelo threshold |
| falsos negativos | mitigados pelo substring + F1 |

`INFERENCE` — Alternativas (Levenshtein puro, substring puro, combinação
ponderada nova, `max(F1, Lev)` sem guardas) foram consideradas. O desempate
técnico é: **não introduzir uma métrica nova sem evidência de superioridade no
catálogo real**; a métrica existente é auditável e já testada
(`FuzzyMatcherTests`).

`DECISION F3a` — Constantes internas da métrica (`95` do substring, `50` da
penalização, `0.4` do rácio, `99` do cap, `100` do exacto) são **parte da
definição da métrica**, não thresholds de policy. Os `PARAMETER_GAP` de W5.0
(`Fuzzy.Threshold`, `Fuzzy.AmbiguityMargin`, `Fuzzy.Weights`) referem-se à
**aceitação/desempate**, não a estas constantes. Não são promovidas de literais
do código para a policy.

---

## 5. Domínio do score (OBJECTIVO E)

`DECISION F4` — Score técnico de fuzzy: **inteiro `0..100`**.

- `100` = igualdade exacta após normalização.
- `0..99` = similaridade fuzzy; um match fuzzy aceite nunca é `100`
  (o exacto é resolvido antes, passos 2–4).
- **Não** é `MatchConfidence`. `MatchConfidence` tem domínio `0..1` e semântica
  versionada própria (`05 §4.1`, `32`) — tratado em **W5.6**, não aqui.
- O score é exposto como evidência técnica; não altera a decisão (registo, não
  autoridade), em linha com `05 §4.1`.

`DECISION F5` — Combinação de campos num canal candidato:

```
bestOf(ch)  = round( max_f( w_f · sim_f ) )     com sim_f ∈ 0..100, w_f ∈ [0,1]
candidateScore(ch) = clamp(bestOf(ch), 0, 100)
```

- `f ∈ { displayName, alias }` (cada alias é avaliado individualmente).
- `w_f` vem de `Fuzzy.Weights`; `null`/ausente ⇒ `w_f = 1.0`.
- `w_f = 0` exclui o campo.
- Chaves desconhecidas no JSON são ignoradas; documento inválido ⇒ tratado como
  `null` (pesos iguais) — validação endurecida é evolução futura.
- `max` (não média) porque é a melhor evidência disponível; evita que um alias
  fraco dilua um `DisplayName` forte.
- A ambiguidade é medida **entre canais**, não entre campos do mesmo canal:
  vários aliases do mesmo canal com o mesmo score não criam ambiguidade.

---

## 6. Threshold (OBJECTIVO F)

`DECISION F6` — Candidato **aceite** ⇔ `candidateScore(ch) >= Fuzzy.Threshold`.

- `Fuzzy.Threshold` ∈ `0..100`, inteiro. `>=` (exactamente no threshold conta
  como aceite).
- Valor concreto: **`PARAMETER`** (não fixado na norma).
- `FuzzyEnabled=true` com `FuzzyThreshold == null` ou fora de `0..100` ⇒
  **fail-closed**: o passo fuzzy não executa e o resultado é `Unknown`
  (comportamento observável idêntico a fuzzy off). Deve emitir diagnóstico de
  configuração (observabilidade), sem inventar um default.

`DECISION F7` — Banda de plausibilidade (fecha "candidatos plausíveis abaixo do
threshold → `AMBIGUOUS`", `05 §4`, DL-117):

```
floor = Fuzzy.Threshold - max(Fuzzy.AmbiguityMargin, 0)
plausível(ch) = floor <= candidateScore(ch) < Fuzzy.Threshold
descartado(ch) = candidateScore(ch) < floor
```

- Sem candidato aceite **e** sem candidato plausível ⇒ `UNKNOWN`.
- Sem candidato aceite **e** com ≥1 plausível ⇒ `AMBIGUOUS`.
- Nota: com `margin = 0` a banda é vazia; o caso "plausíveis abaixo do
  threshold" não ocorre e cai em `UNKNOWN`. É uma consequência deliberada do
  valor do parâmetro, não uma contradição da norma.

---

## 7. Ambiguity margin (OBJECTIVO G)

`DECISION F8` — `Fuzzy.AmbiguityMargin` ∈ inteiro `>= 0` (default `0` quando
`null`). Define a **tolerância de indistinguibilidade** entre candidatos, em
unidades de score:

- Dois canais candidatos com diferença de score `<= margin` são considerados
  indistinguíveis.
- `margin` também define a largura da banda de plausibilidade abaixo do
  threshold (F7).

Exemplos conceptuais (score 0..100):

| best | second | margin | diff | resultado |
|---|---|---|---|---|
| 91 | 90 | 1 | 1 | diff `<=` margin → **AMBIGUOUS** |
| 91 | 70 | 1 | 21 | diff `>` margin → **CANONICAL(best)** |
| 80 | 79 | 5 | 1 | diff `<=` margin → **AMBIGUOUS** |
| 80 | 74 | 5 | 6 | diff `>` margin → **CANONICAL(best)** |
| 79 | 78 | 5 | — | nenhum aceite, ambos plausíveis → **AMBIGUOUS** |
| 79 | 70 | 5 | — | nenhum aceite, 79 plausível, 70 descartado → **AMBIGUOUS** |
| 79 | 74 | 5 | — | nenhum aceite, nenhum plausível (floor=75) → **UNKNOWN** |

`DECISION F9` — Algoritmo determinístico de decisão (formal):

```
S = { (ch, score) : ch CanonicalChannel activo, score = candidateScore(ch),
                    score >= floor }                       // floor = Threshold - margin
if S = ∅:                         return UNKNOWN
b = max score em S
if b < Threshold:                 return AMBIGUOUS            // plausíveis, nenhum aceite
B = { ch ∈ S : score(ch) = b }
if |B| > 1:                       return AMBIGUOUS            // empate no topo
second = max{ score(ch) : ch ∈ S, ch ∉ B }                    // ⊥ se não existir
if second ≠ ⊥ and (b - second) <= margin:
                                  return AMBIGUOUS
return CANONICAL(argmax B)                                     // |B| = 1
```

- Sempre determinístico: só usa `max`/`second max` sobre conjuntos; a ordem de
  enumeração, `Id`, ordem de BD, inserção ou playlist **não** influenciam o
  resultado.
- Um único canal com múltiplos aliases a pontuar `b` é `|B| = 1` (um canal),
  logo não é ambiguidade.

---

## 8. Tie-break (OBJECTIVO H)

`DECISION F10` — Regra de desempate do fuzzy:

1. **Nunca** escolhe entre scores equivalentes (`|B| > 1` → `AMBIGUOUS`).
2. Usa a margem (F8/F9) para separar candidatos não equivalentes.
3. **Não** existe regra determinística adicional de preferência (sem
   `first result wins`, sem `Id`, sem ordem de inserção/playlist/BD).
4. Quando a margem não é suficiente → `AMBIGUOUS` (vai para Review; criação de
   `ReviewItem` é W5.4).

Consequência: uma execução repetida sobre o mesmo catálogo/policy produz sempre
o mesmo resultado.

---

## 9. Geração de candidatos (OBJECTIVO I)

`DECISION F11` — Universo de candidatos = **todos os `CanonicalChannel`
activos (`IsEnabled = true`)**, incluindo os seus `ChannelAlias`. Nenhum outro
universo.

- Não depende de Eligibility (não introduz dependência W5 → P10).
- Não restringe a provider namespace nem a `Group` (não são identidade).
- Não usa canais inactivos.
- O passo fuzzy só corre depois de os passos 1–5 falharem, pelo que candidatos
  já resolvidos exactamente nunca chegam aqui.

`INFERENCE` — Um filtro de blocking (ex.: mesmo `Country`/`Group` ou
namespace) poderia reduzir o universo, mas introduziria um critério de
identidade não normativo. Fica explicitamente fora do v1.

---

## 10. Complexidade (OBJECTIVO J)

`FACT` — Os passos `CanonicalExact`/`NormalizedName` já fazem scan em memória
de todos os canais activos (`CatalogResolver.cs:200-204`; N.3).

`DECISION F12` — Corrections-first: o passo fuzzy faz scan completo dos canais
activos, `O(N · A)` similaridades por identidade não resolvida, com
`N` = canais activos e `A` = nº médio de aliases por canal (normalmente `A`=0).
Sem pré-indexação, sem candidate blocking, sem cache no v1.

- Justificação: o custo é da mesma ordem do scan já existente em W5.2; a
  optimização prematura arriscaria incorrectude determinística.
- Evolução futura (índice normalizado em BD / blocking) é permitida desde que
  preserve exactamente F9–F11. Não faz parte do v1.

---

## 11. Policy schema (OBJECTIVO M)

`FACT` — Campos existentes em `RecognitionPolicy` /
`recognition_policies`: `Enabled`, `FuzzyEnabled`, `FuzzyThreshold?`,
`FuzzyAmbiguityMargin?`, `FuzzyWeightsJson?`, `Version`, `ScopeKey`,
`CanonicalChannelKey`, `GroupKey`.

`DECISION F13` — O schema existente é **suficiente** para o v1. Não é
necessário novo campo nem migration para W5.3.

| Campo | Tipo | Semântica | Default | Scope | Versioning |
|---|---|---|---|---|---|
| `Fuzzy.Enabled` | bool | liga/desliga o passo 6 | `false` (normativo) | todos | `Version` + snapshot |
| `Fuzzy.Threshold` | int `0..100`? | score mínimo de aceitação (F6) | `null` ⇒ fail-closed | todos | `Version` + snapshot |
| `Fuzzy.AmbiguityMargin` | int `>=0`? | tolerância de indistinguibilidade + banda de plausibilidade (F7/F8) | `null` ≡ `0` | todos | `Version` + snapshot |
| `Fuzzy.Weights` | JSON `{"displayName":n,"alias":n}`? | pesos por campo (F5) | `null` ≡ `1.0` | todos | `Version` + snapshot |

`OPEN-P2` (opcional, não bloqueante) — Se operadores precisarem de controlar a
banda de plausibilidade independentemente da margem de ambiguidade, um campo
futuro `Fuzzy.CandidateFloor` (int `0..Threshold`) pode ser proposto como
emenda a W5.1 (schema + migration). **Não é necessário para W5.3.**

---

## 12. Fronteira com Review (OBJECTIVO L)

`DECISION F14` — W5.3 altera apenas o passo 6 dentro de
`CatalogResolver.ResolveAsync` e devolve `CatalogResolution`. **Não** cria,
actualiza, fecha ou reabre `ReviewItem` — isso pertence a W5.4/W5.5.

| Resultado fuzzy | `CatalogResolution` devolvido | `MatchMethod` | Review |
|---|---|---|---|
| `CANONICAL` | `FromCanonical(ch, RecognitionMatchMethods.Fuzzy)` + `PolicyVersion` | `Fuzzy` | nenhum |
| `AMBIGUOUS` | `CatalogResolution.Ambiguous("fuzzy-ambiguous")` + `PolicyVersion` | `null` | nenhum (W5.4 cria `ReviewItem`) |
| `UNKNOWN` | `CatalogResolution.Unknown()` + `PolicyVersion` | `null` | nenhum |

Interface W5.3 → W5.4: para permitir Review informado, W5.3 expõe
**diagnóstico técnico** (não `MatchConfidence`): lista de candidatos com
`CanonicalChannelId`, score `0..100` e razão, e o motivo
(`fuzzy-ambiguous` / `fuzzy-below-threshold`). `MatchConfidence` (`0..1`) e a
semântica versionada de `MatchMethod` são W5.6 e **não** são antecipados aqui.
`PolicyVersion` já é preenchido em todos os resultados (W5.2).

`DECISION F14a` — `MatchMethod = Fuzzy` só é registado quando o resultado é
`Canonical`. Resultados `Ambiguous`/`Unknown` não têm método efectivo (registo,
não autoridade) — consistente com W5.2.

---

## 13. Matriz de testes (OBJECTIVO K)

Casos que `WaveW53FuzzyRecognitionTests` deve cobrir após implementação:

| # | Caso | Esperado |
|---|---|---|
| 1 | fuzzy disabled (default) | passo não executa; `Unknown` |
| 2 | fuzzy enabled + nenhum candidato no catálogo | `Unknown` |
| 3 | abaixo do threshold, sem plausíveis | `Unknown` |
| 4 | exactamente no threshold | aceite; `Canonical` |
| 5 | um candidato claro | `Canonical` + `MatchMethod=Fuzzy` |
| 6 | dois aceites separados por margem suficiente | `Canonical` do melhor |
| 7 | dois aceites dentro da margem | `Ambiguous` |
| 8 | empate exacto de score | `Ambiguous` |
| 9 | três candidatos (melhor + 2 próximos) | `Ambiguous` |
| 10 | diferenças de título normalizado | match conforme normalização |
| 11 | alias normalizado | match; `Canonical` |
| 12 | acentos | normalizados; match |
| 13 | pontuação | normalizada; match |
| 14 | whitespace | normalizado; match |
| 15 | case | normalizado; match |
| 16 | provider namespace presente | não altera score (não é campo) |
| 17 | colisão cross-namespace | não resolve por fuzzy; exact steps decidem |
| 18 | fingerprint diferente | não influencia resultado |
| 19 | quality diferente (HD/SD) | não influencia resultado |
| 20 | número/posição diferente | não influencia resultado |
| 21 | execução repetida | resultado idêntico (determinismo) |
| 22 | ausência de criação de `CanonicalChannel` | contagem de canais inalterada |
| 23 | versão do snapshot usada | `PolicyVersion` = versão do snapshot, não da policy mutável |
| 24 | fuzzy enabled + `Threshold=null` | fail-closed; `Unknown` |
| 25 | margem=0 + empate no topo | `Ambiguous` |
| 26 | margem=0 + segundo fora | `Canonical` |
| 27 | plausíveis abaixo do threshold (margin>0), nenhum aceite | `Ambiguous` |
| 28 | mesmo canal com múltiplos aliases fortes | não é ambiguidade |

---

## 14. Divergências e decisões abertas

- **`OPEN-D1` (PARAMETER)** — Valores de `Fuzzy.Threshold`,
  `Fuzzy.AmbiguityMargin`, `Fuzzy.Weights`: permanecem `PARAMETER` (W5.0 D3).
  Sem default normativo. Semântica fechada (§6–§8, §11).
- **`OPEN-N1`** — Normalização específica de idioma/abreviaturas: fora do v1.
- **`OPEN-P2`** — `Fuzzy.CandidateFloor` opcional (não bloqueante).
- **`OPEN-D2` (RESOLVIDO em W5.3)** — O path legacy `ChannelMatcher` +
  `MatchScorer` mantém fuzzy **hardcoded** (`MatchThreshold=80`, `margin=5`,
  `Exact=95`) e permanece `DIVERGENT` (C2, W5.0). Decisão W5.3: **fora de
  scope**; a reconciliação do path legacy é uma wave futura separada. Nenhum
  ficheiro do motor legacy foi alterado.
- **`OPEN-D3` (RESOLVIDO em W5.3)** — O diagnóstico técnico é transportado em
  `CatalogResolution.FuzzyScore` (`int?`, `0..100`) e
  `CatalogResolution.FuzzyDiagnostic` (`FuzzyRecognitionDiagnostic?` com
  candidatos, scores e `DecisionReason`), ambos `init`-only e aditivos.
  Distintos de `MatchConfidence` (W5.6). Consumidores existentes não são
  quebrados (propriedades novas opcionais).

### Análise de reabertura

`DECISION` — Nenhuma decisão de W5.0/W5.2 é contradita. Não há
`DECISION REOPEN REQUIRED`:

- D2/DL-117 (opt-in, abaixo do threshold, único acima → `CANONICAL` se
  desempate satisfeito) é implementado, não alterado.
- D3 (`PARAMETER_GAP`) é respeitado: valores ficam parâmetros.
- W5.2 (ordem, `MatchMethod`, `Ambiguous` sem "primeiro") é preservado pelo
  passo 6 inserido no gate existente.

---

## 15. Alterações documentais propostas (para ratificação) — histórico pré-ratificação

1. `05-CATALOGUE.md §4.1` — adicionar a semântica fuzzy de §5–§9 deste
   documento (referência a `48`).
2. `38-POLICIES.md §5.1` / `32-DOMAIN-SCHEMA.md` — substituir `PARAMETER_GAP`
   de threshold/margem/pesos por `PARAMETER` com semântica definida (F6/F7/F8)
   e schema de `Fuzzy.Weights` (F5/F13).
3. `31-DECISION-LOCK.md` — novo DL (ex.: `DL-118`) a fixar métrica base,
   campos, universo e desempate. **[histórico — ratificado como `DL-118`]**
4. `46-REQUIREMENT-TRACEABILITY.md` / `BIBLE_IMPLEMENTABILITY_GAP_MANIFEST`
   (Anexo O) — registar W5.3 como design e a wave de implementação.

Estas alterações **não** são feitas nesta wave sem ratificação; `48` é a
proposta. **[histórico — pré-ratificação]**

> **Reconciliação documental (2026-09-20):** a ratificação ocorreu via `DL-118`
> (`31-DECISION-LOCK.md`) e W5.3 foi implementado em `84b35f5`. As alterações de
> §15 (itens 1–4) estão reflectidas em `05`, `32`, `38`, `31` e `46`. Os
> enunciados relativos a `OPEN-D2` (motor legacy `ChannelMatcher`/`MatchScorer`
> hardcoded, `DIVERGENT`, fora de scope) e a M.4/C7 (`OUT`/aberto) mantêm-se
> válidos; esta nota não os altera nem fecha.

---

## 16. Plano de implementação de W5.3 (executado)

1. **Policy (sem schema novo):** validar `Fuzzy.Threshold` obrigatório quando
   `FuzzyEnabled=true`; `AmbiguityMargin = null ≡ 0`; `Weights = null ≡ 1.0`.
2. **`CatalogResolver.ResolveAsync` passo 6:** substituir o placeholder por:
   carregar canais activos + aliases; calcular `candidateScore` (F5) com
   `FuzzyMatcher` reutilizado; aplicar F9; devolver
   `Canonical(Fuzzy)` / `Ambiguous("fuzzy-ambiguous")` / `Unknown`, sempre com
   `PolicyVersion`.
3. **Diagnóstico:** expor candidatos/scores para W5.4 (resolver `OPEN-D3`),
   sem tocar Review lifecycle/API.
4. **Testes:** `WaveW53FuzzyRecognitionTests` (§13).
5. **Fora de scope:** path legacy `ChannelMatcher`/`MatchScorer` (resolver
   `OPEN-D2`), `MatchConfidence`/`MatchMethod` versionados (W5.6), Review
   (W5.4/W5.5).
6. **Gates:** `dotnet build`/`dotnet test` verdes; `git diff --check` clean.

---

## 17. Estado

`W5.3 IMPLEMENTATION STATUS: COMPLETE` — decisões F1–F14 ratificadas (§15),
`OPEN-D2` e `OPEN-D3` resolvidos (§14). Implementação em
`Services/Recognition/FuzzyRecognition.cs` e `CatalogResolver.ResolveAsync`
passo 6; testes `WaveW53FuzzyRecognitionTests` (35). Nenhum valor de `PARAMETER`
é fixado pela norma (mantém-se D3).
