# 38 — Policies

## 1. Policy é dado configurável

Uma policy não deve estar codificada em condicionais espalhadas.

Cada policy tem:
- Key;
- Version;
- Scope;
- Enabled;
- Parameters;
- Created/Updated;
- audit metadata.

## 2. Scope

Scopes permitidos:
- system/default;
- global;
- group;
- channel.

## 3. Precedência

`channel > group > global > default`.

O merge deve ser campo-a-campo apenas quando o schema declarar campos independentes. Caso contrário, a camada superior substitui a policy inteira.

## 4. Snapshot

Antes do Run, o sistema resolve e congela as policies aplicáveis.

## 5. Tipos

No mínimo:
- CountryPolicy;
- MediaPolicy;
- ValidationPolicy;
- EligibilityPolicy;
- SourcePriorityPolicy;
- SourceSelectionPolicy;
- OutputPolicy;
- RecognitionPolicy;
- DispatcharrPolicy.

### 5.1 RecognitionPolicy (W5.0)

Scopes: `system/default`, `global`, `group`, `channel`; precedência `channel > group > global > system/default` (DL-103).

A policy resolvida é materializada como **snapshot imutável** associado ao Run antes do processamento de Recognition (DL-017). A identidade do Run é `RunCoordinator.RunId` (DL-124); identificadores de diagnóstico/observabilidade não são identidade de Run.

Schema mínimo:
- `Enabled`
- `Fuzzy.Enabled` (por defeito `false` — fuzzy é **opt-in**)
- `Fuzzy.Threshold` (`PARAMETER`; inteiro `0..100`; nulo/fora do intervalo com
  fuzzy ligado é fail-closed)
- `Fuzzy.AmbiguityMargin` (`PARAMETER`; inteiro `>= 0`; nulo ≡ `0`)
- `Fuzzy.Weights` (`PARAMETER`; JSON `{"displayName":n,"alias":n}`, nulo ≡ `1.0`;
  chaves desconhecidas ignoradas)

Versionamento (DL-110), persistência (SQLite, DL-022) e auditoria das mutações fazem parte do contrato. A semântica de threshold/margem/pesos está fechada em `48-RECOGNITION-FUZZY-CONTRACT.md` (W5.3); os valores concretos são `PARAMETER` — não podem ser fixados na BÍBLIA nem promovidos a partir de literais do código. `Fuzzy.Enabled=false` é o único default normativo.

## 6. Não misturar responsabilidades

CountryPolicy não escolhe stream.
ValidationPolicy não cria canais.
SelectionPolicy não atribui posições.
OutputPolicy não decide identidade.
DispatcharrPolicy não decide catálogo.

## 7. Autoridade

A autoridade normativa das regras é a policy por tipo. O snapshot resolvido de um Run é a autoridade histórica daquilo que foi efectivamente decidido nesse Run; não substitui a policy como definição normativa.

## 8. Schema e merge

**Schema por tipo.** Cada tipo de Policy DEVE declarar o seu schema de campos.

**Merge.** Regra geral: scalar/object → substituição; collection → semântica definida pelo schema; `null` explícito → valor explícito; ausência → herança; `Enabled=false` → policy explicitamente desactivada, não ausência. A precedência de DL-103 mantém-se.
