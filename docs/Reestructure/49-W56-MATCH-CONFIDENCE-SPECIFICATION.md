# 49 — W5.6: Especificação normativa de MatchMethod/MatchConfidence

**Estado:** RATIFICADO (especificação normativa); implementação **concluída** — evidência: `WaveW56MatchConfidenceTests` (45/45, incl. follow-up F7-B/F8-B/F9); migration `AddMatchSemanticsVersionAndNullableMatchConfidence`; commits `b613502` (W5.6) e `780fa01` (follow-up W5.6, rastreio em `46`); rastreio em `46` (W5.6 = IMPLEMENTED) e manifest Anexo R/S.

Este documento é a especificação normativa de `MatchMethod`/`MatchConfidence` (W5.6),
derivada da proposta `.kilo/plans/w56-specification.md` e das decisões humanas ratificadas
OD-A..OD-E. É implementável por outro agente **sem reinventar semântica**. As decisões de
contrato de origem estão em DL-121 (`31`) e as ratificações de valores/versão/política em
DL-122 (`31`).

Os valores da tabela do §7 são **decisões normativas de produto/domínio**, não probabilidades
universais nem uma escala global comparável. Uma implementação que contradiga este documento
está incorrecta relativamente à BÍBLIA.

---

## 1. Objetivo

Fechar, com evidência e sem inventar valores, a semântica de `MatchMethod`/`MatchConfidence`
antes da implementação de W5.6. Distinguir o que é **tecnicamente inferível** do que **foi
decidido** por ratificação humana, e proibir explicitamente a conversão de `FuzzyScore`.

---

## 2. Scope

**IN:** semântica normativa dos 8 métodos; regra de `MatchConfidence`; versionamento da
semântica (`MatchSemanticsVersion="msm1"`); produtor (Recognition) e transporte
(`CatalogResolution`); persistência pelo pipeline; validação/compatibilidade do endpoint
manual; reconciliação C6; fronteira C7; testes de implementação.

**OUT:** M.4 (wiring do snapshot ao Run/pipeline); motor legacy
(`ChannelMatcher`/`MatchScorer`/`MatchingOptions`); conversão `FuzzyScore→MatchConfidence`;
campos C7 (`RunId`/`StreamId`/`Actor`/`Evidence`/`Candidates`/`Decision`); API W5.5;
alterações de schema/migrations **nesta** especificação.

---

## 3. Fontes normativas

`31` DL-119/DL-121/**DL-122**; `05 §4`/`§4.1`; `32` (ChannelSource; `32:163` ReviewItem/C7);
`44`; `46` (secção W5.6); `48 §5/§12`; `43 #24`; manifest `D6`/`C6`/`C7`/`Anexo R`.
Esta especificação (`49`) é o contrato normativo de implementação de W5.6.

---

## 4. Baseline de implementação (FACT)

| # | Onde | Evidência |
|---|---|---|
| 1 | `MatchMethod` produzido | `CatalogResolver.ResolveAsync` `:192-193` (TvgIdExact/ExternalIdentityExact), `:236` (CanonicalExact), `:260` (NormalizedName), `:272` (KnownAlias), `:298,309` (ExplicitHeuristic), `:357` (Fuzzy); `FromCanonical`/`FromRule` (ManualReview). `null` em `Ambiguous`/`Unknown`. |
| 2 | `MatchConfidence` produzido | **Não existe em `CatalogResolution`.** Só o pipeline usa `const double confidence = 1.0` (`PipelineIngestionService.cs:284`). |
| 3 | `MatchConfidence` persistido | `RecordChannelSourceAsync(matchConfidence: confidence)`; `ChannelSourceEntity.MatchConfidence` (`double?`, **nullable** desde a migration `AddMatchSemanticsVersionAndNullableMatchConfidence`). |
| 4 | `MatchMethod` persistido | idem; `ChannelSourceEntity.MatchMethod` (string required, max 80). |
| 5 | hardcoded | `const double confidence = 1.0` (`PipelineIngestionService.cs:284`); `IngestionEntry.MatchConfidence`. |
| 6 | versão implícita | **Nenhuma** para match semantics. Precedente: `StreamFingerprint.Version="sfp1"` (const) + `ChannelSourceEntity.FingerprintVersion` (persistida) — DL-108. |
| 7 | endpoint manual | `POST /api/catalog/sources/{sourceId}/streams`; payload `ChannelSourcePayload { matchConfidence (double?), matchMethod (string?) }`; `payload.MatchMethod ?? "unknown"`. |
| 8 | persistência manual | `RecordChannelSourceAsync` → defaults `matchConfidence=0`, `matchMethod="unknown"`; valida apenas `matchMethod.Length <= 80`, sem enum/domínio nem clamp de confidence. |
| 9 | legado a preservar | Testes usam métodos arbitrários (`matchMethod: "test"`, `"legacy"`, `"test-raw"`, `"canonical-alias"`) e `MatchConfidence = 0`; `PipelineIngestionBridgeTests` só exige método não-vazio e `0 <= confidence <= 1`. O motor legacy (`ChannelMatcher`) **não** escreve estes campos. |

> **Nota (pós-implementação W5.6).** Os itens 2 e 5 descrevem a **baseline pré-W5.6** (histórica). Após a implementação: `CatalogResolution.MatchConfidence` existe (`double?`), o pipeline transporta o valor de Recognition e o `const 1.0` foi removido. O item 3 foi alinhado para `double?` (F9). Alinhamento documental F9: a rota real é `POST /api/catalog/sources/{sourceId}/streams`.

**Precedentes de valor:** `ExternalIdentityEntity.Confidence` clampada `0..1`, default 1.0;
`CatalogBaselineImporter` usa `Confidence = 1.0`.

---

## 5. Conjunto normativo de MatchMethod (fechado)

Exactamente 8 valores (inalterado): `ExternalIdentityExact`, `TvgIdExact`, `CanonicalExact`,
`NormalizedName`, `KnownAlias`, `ExplicitHeuristic`, `Fuzzy`, `ManualReview`
(`RecognitionMatchMethods`; `05 §4.1`; `32`; manifest `N.2`).

Mapeamento: passos 2–6 → método correspondente; `IdentityRule`/Review → `ManualReview`;
`Ambiguous`/`Unknown` → `MatchMethod` **nulo** (não persistido, pois só `Canonical` cria
`ChannelSource`). Não existe um nono valor; qualquer valor fora deste conjunto é inválido
quando fornecido explicitamente (§12).

---

## 6. Semântica de MatchConfidence

`double 0..1`; **method-specific**; **sem** escala global comparável; versionada; registo/
evidência, não autoridade (DL-121).

- `Unknown`/`Ambiguous` → `null` (DL-121; §9).
- `MatchConfidence` **não** é `FuzzyScore` (DL-119; §8).
- Métodos diferentes **não** são comparados nem ordenados pelo número.
- O valor por método é uma **decisão normativa** (§7), não uma probabilidade universal
  inferível a partir da técnica.

---

## 7. Tabela normativa dos 8 métodos (valores ratificados)

`MatchConfidence` é uma decisão normativa por método; nunca uma probabilidade universal, nem
um ranking global de métodos. Valores ratificados (OD-A/B/C + exactos):

| MatchMethod | Significado / evidência mínima | MatchConfidence | Domínio | Const/calc | null quando |
|---|---|---|---|---|---|
| `ExternalIdentityExact` | identidade externa exacta (namespace/provider) — passo 1 | **1.0** | `{1.0}` | const | nunca em `Canonical` |
| `TvgIdExact` | identidade exacta no namespace tvg-id — passo 1 | **1.0** | `{1.0}` | const | nunca em `Canonical` |
| `CanonicalExact` | Key canónica normalizada exacta — passo 3 | **1.0** | `{1.0}` | const | nunca em `Canonical` |
| `NormalizedName` | nome normalizado exacto (passo próprio) — passo 4 | **1.0** | `{1.0}` | const | nunca em `Canonical` |
| `KnownAlias` | alias persistido exacto — passo 5 | **1.0** | `{1.0}` | const | nunca em `Canonical` |
| `ExplicitHeuristic` | heurística explícita (ex.: `AffinityMember`) — passo 6 (não-exacto) | **0.80** | `{0.80}` | const | nunca em `Canonical` |
| `Fuzzy` | fuzzy opt-in aceite (score `0..100` técnico) | **0.60** | `{0.60}` | const | nunca em `Canonical` |
| `ManualReview` | decisão manual / `IdentityRule` (`FromRule`) | **1.0** | `{1.0}` | const | nunca em `Canonical` (é `Review`/`Excluded`/decisão manual) |

**Regras vinculativas:**

- **Exactos** e **`ManualReview`** → `1.0`. Em `ManualReview`, `1.0` significa
  **"explicitamente confirmado via review"** (autoridade da decisão humana), **não**
  certeza matemática, e **não** é comparável globalmente com os restantes valores (OD-C).
- **OD-A:** `ExplicitHeuristic` → `0.80` (constante; heurística explícita, não exacta).
- **OD-B:** `Fuzzy` → `0.60` (constante). **ESTRITAMENTE:** o valor **não** é calculado a
  partir de `FuzzyScore`; **proibida** qualquer fórmula `FuzzyScore/100` ou transformação
  equivalente. `FuzzyScore` permanece `int? 0..100` **diagnóstico**, sem relação numérica com
  `MatchConfidence`.
- `Unknown`/`Ambiguous` → `null` (§9); nunca `0` nem o `FuzzyScore` do melhor candidato.
- É proibido ordenar métodos pelo valor de `MatchConfidence` ou inferir "melhor método →
  maior número" (§6).

---

## 8. Separação de FuzzyScore (fechado)

`FuzzyScore` = `int? 0..100`, técnico, runtime-only, só em fuzzy `Canonical`; **não é**
`MatchConfidence` (DL-119; `48 §5`; `43 #24`). Proibida qualquer fórmula `FuzzyScore/100` ou
equivalente. O valor de `MatchConfidence` para `Fuzzy` é a constante **`0.60`** (OD-B), fixada
independentemente e **nunca** derivada de `FuzzyScore`.

---

## 9. Semântica Unknown/Ambiguous (fechado)

`MatchConfidence = null`; nunca `0` nem o `FuzzyScore` do melhor candidato (DL-121).
`MatchMethod` também é nulo. Sem alteração ao Review (W5.4/W5.5).

---

## 10. Versionamento (OD-D ratificado)

A semântica de `MatchMethod`/`MatchConfidence` é **versionada**. A decisão ratificada é:

- **`MatchSemanticsVersion = "msm1"`**, **persistida em `ChannelSource`** (a par de
  `MatchMethod`/`MatchConfidence`).
- Identifica as **regras/algoritmo** que produziram o par `MatchMethod`+`MatchConfidence`:
  é **proveniência derivada do algoritmo**, **não** uma propriedade arbitrária introduzida
  individualmente pelo operador num `ChannelSource`.
- Rows novas de `ChannelSource` produzidas por um par normativo W5.6 recebem `"msm1"`.
  **Não** é exigido de clientes legacy do endpoint manual: o servidor carimba a versão
  corrente apenas quando o par é normativo (§12, F8-B).
- **Proibido** usar `RecognitionPolicy` (ou `RecognitionPolicy.Version`) como substituto da
  versão da semântica de matching: são conceitos distintos.
- **Não confundir** com `FingerprintVersion` (DL-108): são versões independentes de conceitos
  independentes (fingerprint de stream vs. semântica de matching).

**F8-B ratificado (atribuição de versão).** `MatchSemanticsVersion` só identifica pares produzidos sob a semântica W5.6, isto é, quando **todas** as condições se verificam: `MatchMethod` é um dos 8 métodos normativos; `MatchConfidence` foi validado; e o par corresponde à tabela normativa (§7).
- **Payload legacy sem `MatchMethod`** (caminho manual) → `MatchMethod="unknown"`, `MatchConfidence=0`, **`MatchSemanticsVersion=null`** (nunca `"msm1"`).
- **Não** criar valores de compatibilidade como `"legacy"` ou outros.
- Caminhos normativos (Recognition/pipeline e endpoint manual explícito validado) → `"msm1"`.
- Consequência: rows históricas (anteriores a W5.6) e rows produzidas por payload legacy ficam com versão `null`; uma futura `msm2` distingue-se por valor. **Implementado** na wave de follow-up (F8-B; `CatalogResolver.RecordChannelSourceAsync` deriva a versão do par via `RecognitionMatchMethods.TryGetMatchConfidence`).

---

## 11. Contrato Recognition → CatalogResolution → Pipeline

```text
Recognition (CatalogResolver.ResolveAsync)
  → CatalogResolution { MatchMethod, MatchConfidence? }   // decisão semântica
  → PipelineIngestionService                              // apenas persiste
  → ChannelSourceEntity { MatchMethod, MatchConfidence, MatchSemanticsVersion="msm1" }
```

- `CatalogResolution` **deve** passar a transportar `MatchConfidence` (`double?`), espelhando
  `MatchMethod` (propriedade `init`-only aditiva).
- O pipeline deixa de usar `const double confidence = 1.0` e **não recalcula**; apenas
  persiste o que vem de `CatalogResolution`, gravando `MatchSemanticsVersion="msm1"`.
- `MatchConfidence` é `null` em `Unknown`/`Ambiguous` e no transporte (não persistido sem
  `ChannelSource`).
- O valor por método é atribuído pela Recognition segundo a tabela do §7; nunca pelo pipeline.

---

## 12. Validação do endpoint manual (OD-E ratificado)

Payload actual: `ChannelSourcePayload { CanonicalChannelId, StreamUrl, Quality, Epg,
Availability, ExternalStreamId, IsEnabled, MatchConfidence (double), MatchMethod (string?) }`.

Política ratificada (OD-E), a aplicar **na camada HTTP** (não no serviço de persistência, para
não quebrar consumidores legados internos):

- **`MatchMethod` ausente no payload** → preservar o comportamento existente; **não** se
  transforma um payload legacy num erro W5.6.
- **`MatchMethod` fornecido** → aceitar **apenas** os 8 valores normativos (§5); valor
  inválido → erro de validação.
- **`MatchConfidence` fornecido** → aceitar **apenas** `double` em `0..1`; fora do domínio →
  erro de validação.
- **Ambos fornecidos** → validar a combinação segundo a semântica W5.6 (§6/§7).
- **`MatchSemanticsVersion` não é exigido no payload**; o servidor carimba `"msm1"` apenas nos registos cujo par `MatchMethod`+`MatchConfidence` é normativo (F8-B).
- Valores explicitamente inválidos → **erro de validação, sem persistência parcial**,
  mantendo o contrato de erro da API onde compatível com W5.5 (`22 §2`; envelope
  `{error,message,correlationId}` nas rotas que já o usam).
- **Não** remover nem alterar endpoints legacy nesta wave. A validação **não** é feita em
  `RecordChannelSourceAsync` (serviço), para preservar testes/consumidores legados com
  métodos arbitrários (`"test"`/`"legacy"`/`"canonical-alias"`).

**F7-B ratificado (método sem confidence) — implementado.** Quando `MatchMethod` é fornecido explicitamente, `MatchConfidence` passa a ser **obrigatório**:
- `MatchMethod` presente **e** `MatchConfidence` ausente → **HTTP 400**, sem persistência.
- **Não** auto-preencher o valor do método (não fabricar `Fuzzy→0.60` etc.).
- **Não** persistir `MatchMethod` normativo com `MatchConfidence = null`.
- A produção semântica de `MatchConfidence` continua a pertencer a **Recognition**; no endpoint manual, o operador que declara o método declara também a confidence.
- Combinações fornecidas mantêm a validação normativa: `Fuzzy+0.60` válido; `Fuzzy+0.80`/`Fuzzy+1.0` → 400; `ExplicitHeuristic+0.80` válido; `CanonicalExact+1.0` válido; `CanonicalExact+0.80` → 400; `ManualReview+1.0` válido; `ManualReview+0.0` → 400.
- **Legacy** (payload sem `MatchMethod`) permanece inalterado: `"unknown"` + `0` + versão `null` (§10, F8-B).
- **Implementado** na wave de follow-up (F7-B; validação em `WebDashboardService`, antes de `RecordChannelSourceAsync`; sem migration/schema).

---

## 13. Reconciliação C6

C6 = `MatchConfidence`/`MatchMethod` "sem base → normativizados" (manifest C6). Com DL-121 +
esta especificação + DL-122, C6 fica **normativamente fechado** para: escala (`0..1`),
method-specific, `null` em `Unknown`/`Ambiguous`, producer=Recognition, persistência=pipeline,
versionamento (`MatchSemanticsVersion="msm1"`), valores concretos por método (tabela §7) e
política do endpoint manual (§12). Não restam valores `OPEN` de W5.6.

---

## 14. Fronteira C7

`RunId`/`StreamId`/`Actor`/`Evidence`/`Candidates`/`Decision` permanecem **sem contrato
implementável** (`32:163`); W5.6 **não** os implementa nem inventa (DL-121). `MatchMethod`/
`MatchConfidence`/`MatchSemanticsVersion` **não** fazem parte do `ReviewItem` (são campos de
`ChannelSource`) e ficam **completamente especificados** por esta especificação dentro dos
limites acima. A documentação declara C7 `OPEN`; **nenhum** campo C7 é adicionado.
M.4 permanece `OUT` (`W5.6 ≠ M.4`).

---

## 15. Compatibilidade

- **Pipeline/testes legados:** não alterar os defaults nem a validação (só comprimento) de
  `RecordChannelSourceAsync`; não quebrar `matchMethod:"test"/"legacy"/"canonical-alias"` nem
  `MatchConfidence=0`.
- **Endpoint manual:** validação apenas na camada HTTP; preservar payload/rotas; erros
  conforme `22 §2`; payload sem `MatchMethod` mantém comportamento.
- **`CatalogResolution`:** `MatchConfidence` como propriedade `init`-only aditiva (não quebra
  consumidores, à semelhança de `FuzzyScore`).
- **Motor legacy:** intacto (`OPEN-D2`); `ChannelMatcher`/`MatchScorer`/`MatchingOptions` não
  escrevem estes campos.

---

## 16. Testes exigidos na implementação

- **Por método:** `MatchMethod` correcto (já coberto por `WaveW52...`/`WaveW53...`) e
  `MatchConfidence` esperado — `1.0` nos 5 exactos e em `ManualReview`; `0.80` em
  `ExplicitHeuristic`; `0.60` em `Fuzzy`.
- `MatchConfidence` fora de `0..1` → rejeição/erro; `null` em `Unknown`/`Ambiguous`.
- **`Fuzzy`:** asserção negativa de que `MatchConfidence` **não** depende de `FuzzyScore`
  (nunca `score/100` nem transformação equivalente); alterar `FuzzyScore` não altera o
  `MatchConfidence` `0.60`.
- **Versionamento:** `MatchSemanticsVersion == "msm1"` em `ChannelSource` novo apenas quando o par é normativo; legacy (`"unknown"`+`0`) → `null` (§10, F8-B); o campo não é exigido no payload manual, pelo que a ausência não é erro.
- **Persistência:** `ChannelSource.MatchConfidence`/`MatchMethod`/`MatchSemanticsVersion`
  iguais aos de `CatalogResolution`; o pipeline **não** recalcula.
- **Endpoint manual:** válido → 200/201; método inválido → erro; confidence fora de `0..1` →
  erro; payload legado sem `MatchMethod` preservado.
- **Compatibilidade/regressão:** `PipelineIngestionBridgeTests`, `SourceSelection*Tests`,
  `RealPipelineDiagnosticTests` (métodos `"test"/"legacy"/"canonical-alias"`).
- **`ManualReview`:** `1.0` documentado como confirmação explícita via review, não como
  probabilidade nem ranking global.

---

## 17. Decisões ratificadas (OD-A..OD-E)

| ID | Decisão ratificada |
|---|---|
| **OD-A** | `ExplicitHeuristic` → `MatchConfidence = 0.80`. |
| **OD-B** | `Fuzzy` → `MatchConfidence = 0.60`; **sem** cálculo a partir de `FuzzyScore`, sem `FuzzyScore/100` nem transformação equivalente; `FuzzyScore` permanece `0..100` diagnóstico. `Ambiguous`→`null`, `Unknown`→`null`. |
| **OD-C** | `ManualReview` → `MatchConfidence = 1.0` ("explicitamente confirmado via review"; não é certeza matemática; sem comparabilidade global). |
| **OD-D** | `MatchSemanticsVersion = "msm1"`, **persistida** em `ChannelSource`; proveniência derivada do algoritmo; rows novas recebem `"msm1"`; não exigida de clientes legacy do endpoint manual (o servidor atribui a corrente); **não** usar `RecognitionPolicy` como substituto; distinta de `FingerprintVersion`. |
| **OD-E** | Política de compatibilidade do endpoint manual (§12): omissão de `MatchMethod` preserva comportamento; se fornecido, aceitar só os 8 valores; `MatchConfidence` só `double 0..1`; ambos → validar combinação; versão não exigida; inválidos → erro de validação sem persistência parcial; endpoints legacy inalterados. |

Exactos ratificados: `ExternalIdentityExact=1.0`, `TvgIdExact=1.0`, `CanonicalExact=1.0`,
`NormalizedName=1.0`, `KnownAlias=1.0`.

---

## 18. Avaliação de prontidão para implementação

**Fechados:** conjunto `MatchMethod`; separação `FuzzyScore`; `Unknown`/`Ambiguous→null`;
producer/transporte/persistência; valores concretos dos 8 métodos; versão `msm1` (persistida);
política do endpoint manual; C6; fronteira C7; testes.

**Abertos/limitações:** C7 `OPEN` (campos sem contrato — **não** pertencem a W5.6); M.4 `OUT`
(sem wave atribuída). A implementação de W5.6 foi **concluída** no commit `b613502` (semântica
method-specific de `MatchMethod`/`MatchConfidence`, `MatchConfidence` nullable, migration
`20260920200933_AddMatchSemanticsVersionAndNullableMatchConfidence` para `MatchSemanticsVersion`
e fim do `const 1.0`), com follow-up no commit `780fa01` (F7-B/F8-B/F9); esta especificação fixa o
contrato e a implementação realiza-o. C7 permanece `OPEN` e M.4 permanece `OUT`.

> Nota de reconciliação (2026-09-20): correcção documental de estado — o cabeçalho já declarava a
> implementação concluída, mas §18 mantinha linguagem pré-implementação. Não é uma nova decisão:
> OD-A..OD-E e DL-121/DL-122/DL-123 permanecem inalteradas, C7 continua `OPEN` e M.4 continua `OUT`.

```text
W5.6 SPECIFICATION: RATIFIED (normative) — implementation complete
```
