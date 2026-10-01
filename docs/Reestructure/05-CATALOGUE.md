# 05 — Catálogo canónico e reconhecimento

## 1. CanonicalChannel

É a identidade lógica de um canal que o operador pretende reconhecer.

Um canal pode ter múltiplas fontes e múltiplas streams.

## 2. Catálogo

O catálogo é a autoridade de identidade.

Deve conter, pelo menos:
- Key estável;
- nome canónico;
- aliases;
- identidades externas conhecidas;
- media kind;
- país/locale aplicável;
- estado activo/inactivo;
- metadados necessários ao output.

## 3. Uma fonte de canais

Não deve existir uma lista paralela cuja função seja dizer quais canais devem ser detectados. Se o operador quer que um canal seja detectado/reconhecido, esse canal existe no catálogo.

Dados auxiliares de reconhecimento podem existir, mas não são outra fonte de verdade sobre quais canais existem.

## 4. Matching

A ordem de reconhecimento deve ser determinística. A política base é:

1. identidade externa exacta;
2. tvg-id/canonical/provider identity conhecida;
3. nome normalizado;
4. alias conhecido;
5. heurística explicitamente definida;
6. fuzzy matching apenas quando houver política de confiança e desempate;
7. Review quando não houver reconhecimento inequívoco.

Nenhum passo posterior pode contradizer uma correspondência exacta anterior.

Normalization, Recognition e Fingerprint são conceitos distintos. Valores normalizados são evidência de matching, não identidade.

O fingerprint de stream (`04-PLAYLIST-STREAM.md §4.1`) é evidência técnica versionada: não cria identidade de canal (DL-001/DL-002), não participa na ordem de reconhecimento e é usado para deduplicar streams equivalentes dentro da mesma Source e como critério 6 de Selection. Uma `ChannelSource` pode possuir múltiplas streams candidatas por `(CanonicalChannel, Source)`; a identidade lógica persistente inclui `Fingerprint + FingerprintVersion` (`07-SOURCES.md §5`, `32-DOMAIN-SCHEMA.md`).

**Fuzzy matching.** O fuzzy matching é suportado mas NÃO fica activo por defeito; requer uma `RecognitionPolicy` explícita com `Fuzzy.Enabled = true`. Sem policy activa, o passo fuzzy não é executado. Threshold e restantes valores são parâmetros operacionais (`PARAMETER_GAP`). Um único candidato acima do threshold pode produzir `CANONICAL`, mas apenas se satisfizer as regras de desempate definidas pela policy.

**Resultado abaixo do threshold.** Ausência de candidato → `UNKNOWN`. Existência de candidatos plausíveis mas nenhum atingir o threshold → `AMBIGUOUS`.

**Resolve.** `Resolve` é uma operação administrativa auditada e pode alterar explicitamente o reconhecimento necessário — `CanonicalChannel`, `ChannelAlias`, `ExternalIdentity` e/ou `ChannelSource` — conforme a operação concreta. NUNCA cria identidade implicitamente como efeito colateral de matching automático.

**Ignore.** `Ignore` fecha o `ReviewItem`, exige motivo, NÃO elimina `CanonicalChannel`, NÃO elimina histórico, NÃO apaga indiscriminadamente `Stream`/`ChannelSource`, e impede aquela ocorrência de ser tratada como reconhecimento válido segundo a decisão registada.

### 4.1 Contrato de reconhecimento (W5.0)

**Normalized name.** O nome normalizado é um passo **próprio** da ordem de §4 (passo 3), distinto de alias conhecido (passo 4). Não pode ser colapsado no passo de alias.

**Identity rules.** `IdentityRule` é uma regra **explícita** de reconhecimento, não uma excepção silenciosa à ordem de §4. A sua precedência e os resultados possíveis (`Review`, `Excluded`) fazem parte do contrato. Uma regra nunca cria identidade.

**Provider namespace.** A identidade externa é comparada respeitando o namespace/`ProviderId` quando aplicável. Não existe scope `provider`/`source` nos scopes de policy de §38.

**Método e confiança.** `MatchMethod` representa o método **efectivo** de reconhecimento e distingue, no mínimo: `ExternalIdentityExact`, `TvgIdExact`, `CanonicalExact`, `NormalizedName`, `KnownAlias`, `ExplicitHeuristic`, `Fuzzy`, `ManualReview`. `MatchConfidence` tem domínio `0..1`; **não** é o score de fuzzy e valores de métodos diferentes **não** são directamente comparáveis sem semântica explícita. A semântica de ambos é versionada. Nenhum destes campos altera a decisão de reconhecimento (são registo, não autoridade).

**Clarificação W5.6 (DL-121).** `MatchConfidence` é **method-specific** e não comparável entre métodos (não existe escala global); cada método normativo tem regra própria. Em `Unknown` e `Ambiguous`, `MatchConfidence = null` (nunca `0` nem o `FuzzyScore` do melhor candidato). A **decisão semântica** de `MatchMethod`/`MatchConfidence` pertence a Recognition, que a transporta em `CatalogResolution`; o pipeline apenas a persiste em `ChannelSource`, sem recalcular. O `const confidence = 1.0` era a divergência pré-W5.6 e foi removido por W5.6 (`b613502`); o pipeline transporta agora o valor de `CatalogResolution.MatchConfidence` sem recalcular e `MatchSemanticsVersion = "msm1"` é persistido.

**Especificação W5.6 ratificada (DL-122).** Especificação normativa: `49-W56-MATCH-CONFIDENCE-SPECIFICATION.md`. Os valores são **decisões normativas por método** (não probabilidades universais) e **não** constituem uma escala global comparável: nunca ordenar métodos pelo número.

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

`Fuzzy` é `0.60` **sem** cálculo a partir de `FuzzyScore` (proibido `FuzzyScore/100` ou equivalente); `FuzzyScore` permanece `0..100` diagnóstico e **`FuzzyScore != MatchConfidence`**. `ManualReview=1.0` significa "explicitamente confirmado via review", não certeza matemática. A versão da semântica é `MatchSemanticsVersion = "msm1"`, **persistida** em `ChannelSource` (proveniência derivada do algoritmo; não exigida de clientes legacy do endpoint manual, que recebem a versão corrente). `Unknown`/`Ambiguous` → `null`. C7 permanece `OPEN`/limitado; M.4 permanece `OUT`.

**Resultados de P6.** Recognition produz `Canonical | Unknown | Ambiguous | Excluded`:
- `Excluded` = resultado de uma regra determinística de exclusão;
- `Unknown ≠ Excluded` e `Ambiguous ≠ Excluded`;
- `Rejected` **não** é resultado de Recognition.

**Desambiguação de `Ambiguous`.** O estado `Ambiguous` é qualificado pelo estágio: `Stage=Recognition, Status=Ambiguous` (falta de desempate de reconhecimento) ≠ `Stage=Selection, Status=Ambiguous` (falta de desempate de selecção). Não são o mesmo conceito; não se duplicam enums — o contexto do estágio faz parte do contrato.

### 4.2 Contrato fuzzy (W5.3)

O passo 6 de §4 está fechado em `48-RECOGNITION-FUZZY-CONTRACT.md` (F1–F14) e
implementado em `CatalogResolver.ResolveAsync`:

- **Campos:** a similaridade fuzzy compara a identidade normalizada apenas com
  `DisplayName` (normalizado) e `ChannelAlias.NormalizedAlias`. `Key`, identidade
  externa/`tvg-id`, namespace de provider, `Group`, país, número/posição,
  qualidade e fingerprint **não** produzem score.
- **Normalização:** `ChannelNormalizer.Normalize` (o mesmo normalizador dos
  passos exactos), aplicada à query e ao `DisplayName`; aliases usam a forma
  `NormalizedAlias` persistida.
- **Métrica:** `FuzzyMatcher` existente (exacto → substring → token-set F1 →
  Levenshtein → `max` → guardas de irmãos numéricos). As constantes internas da
  métrica fazem parte da métrica, não são parâmetros de policy.
- **Score:** inteiro `0..100`; não é `MatchConfidence` (`0..1`, W5.6).
- **Aceitação:** `candidateScore >= Fuzzy.Threshold`. Banda de plausibilidade
  `floor = Threshold - max(AmbiguityMargin, 0)`. Sem aceite e sem plausível →
  `UNKNOWN`; sem aceite mas com plausível → `AMBIGUOUS`.
- **Ambiguidade:** dois canais com diferença de score `<= AmbiguityMargin` são
  indistinguíveis → `AMBIGUOUS`. Só um candidato único aceite, separado dos
  restantes por mais do que a margem, produz `CANONICAL`.
- **Desempate:** nunca "escolher o primeiro"; sem `Id`, ordem de BD, inserção ou
  playlist; empate no topo → `AMBIGUOUS`.
- **Universo:** todos os `CanonicalChannel` activos e os seus aliases. Sem
  dependência de Eligibility e sem blocking por provider/grupo/país.
- **Review:** W5.3 devolve `CatalogResolution` (`Canonical` com
  `MatchMethod=Fuzzy`, `Ambiguous`, `Unknown`) e **nunca** cria ou actualiza
  `ReviewItem`. W5.4 integra `Ambiguous` + `FuzzyDiagnostic.DecisionReason
  = fuzzy-ambiguous` como `reasonSignature` do `ReviewItem`
  (`PipelineIngestionService.AmbiguousReasonSignature`), ficando o item em
  `Open` para decisão humana (lifecycle `33`/DL-105/DL-119).

`Fuzzy.Enabled = false` é o único default normativo. Os valores de
`Fuzzy.Threshold`, `Fuzzy.AmbiguityMargin` e `Fuzzy.Weights` são `PARAMETER`
(semântica fechada, valor não fixado). Threshold nulo/fora de `0..100` com fuzzy
ligado é **fail-closed** (`UNKNOWN` + diagnóstico de configuração).

## 5. Ambiguidade

Se dois candidatos satisfizerem a regra sem desempate normativo, o resultado é `AMBIGUOUS` e vai para Review.

Não existe "escolher o primeiro".

## 6. Desconhecido

`UNKNOWN` significa que não existe reconhecimento suficiente.

A consequência normativa é Review, não criação automática de CanonicalChannel.

## 7. Identidade e número

Número/posição da Source pode ser guardado como evidência, mas nunca é identidade.

## 8. Qualidade

HD/FHD/SD pode influenciar Selection, mas nunca Matching.

## 9. Alteração do catálogo

Adicionar, editar, aliasar, fundir ou desactivar um CanonicalChannel é uma mutação administrativa do catálogo e deve ficar auditada.

Uma aprovação de Review que altere o catálogo deve declarar exactamente que mudança produz.

### 9.1 Materialização de ChannelSource em aprovação (W-REVIEW-02)

Uma aprovação de Review com evidência completa (campos persistidos pela W-REVIEW-01: `StreamUrl`, `SourceId`, `StreamFingerprint`, `StreamFingerprintVersion`, `RunId`) **materializa um `ChannelSource`** na mesma transacção da aprovação. Sem evidência, a aprovação prossegue sem materialização.

- **Gate:** `ApprovedCanonicalChannelId.HasValue` ∧ `StreamUrl != null` ∧ `SourceId > 0`.
- **Atomicidade:** `AddAlias` corre num único `SaveChangesAsync` (alias + ReviewItem + ChannelSource). `CreateChannel` mantém dois `SaveChanges` por restrição estrutural (o `Id` do `CanonicalChannel` só é conhecido após o primeiro save); a materialização corre no segundo save (alias + ReviewItem + ChannelSource atómicos entre si; canonical já committed).
- **`MatchMethod`:** literal `"ReviewApproval"` (constante em `Services/Recognition/RecognitionMatchMethods.cs`); `MatchConfidence = 1.0`; `IsEnabled = true`; `Availability = Discovered`.
- **Idempotência:** chamada repetida com mesmo `(CanonicalChannelId, SourceId, Fingerprint, FingerprintVersion)` actualiza a mesma row (sem duplicação).
- **Concorrência:** UNIQUE filtered em `channel_sources(CanonicalChannelId, SourceId, Fingerprint, FingerprintVersion) WHERE "Fingerprint" IS NOT NULL` (migration `20260921220000_AddChannelSourceUniqueOnFingerprint`, W-REVIEW-02B). Aplicação impõe dedup adicional via lookup `(CanonicalChannelId, SourceId, Fingerprint, FingerprintVersion)` (`RecordChannelSourceAsync:2964-2981`). Em aprovação (`AddAlias` / `CreateChannel`), violação do UNIQUE é detectada, row existente é recarregada e reutilizada — sem segunda inserção. Em duplicação de `CanonicalChannel.Key` na primeira escrita de `CreateChannel`, a violação é traduzida para `ChannelAdministrationException(DuplicateKey)` (HTTP 409). Sem `RowVersion`/`xmin` — provider de produção é SQLite. `CreateChannel` corre os dois `SaveChanges` (canonical-create + alias+review+cs) dentro de `BeginTransactionAsync`; orphan canonical eliminado.
- **Exclude** nunca materializa (gate bloqueado: `ApplyIgnoreIgnoreTransition` zera `ApprovedCanonicalChannelId`).
- **Audit:** novos eventos `catalog.review.approval.materialize_created` / `materialize_skipped` (best-effort, mesmo mecanismo do HTTP layer).
- **Reviews legadas (pré-W-REVIEW-01):** `StreamUrl=null`/`SourceId=null` → skip sem erro; aprovação prossegue normalmente.
