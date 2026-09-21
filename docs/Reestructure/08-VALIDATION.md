# 08 — Validation, Observation e Eligibility

## 1. Observation

Observation é evidência obtida numa execução:
- disponibilidade;
- HTTP/connect result;
- tempo de resposta;
- status;
- qualidade observada;
- EPG;
- outros indicadores.

Observation é histórica e deve poder ser relacionada com um Run.

## 2. Validation

Validation mede o estado técnico da fonte/stream.

Validation não altera:
- CanonicalChannel;
- aliases;
- identidade;
- ordering.

`Observation` é a autoridade do facto técnico observado. Validation é o processo que interpreta/evalua observações segundo policy e não redefine o facto.

## 3. Eligibility

Eligibility é a decisão derivada de observations + policy:

`Eligible | Ineligible | Unknown`

Deve existir evidência suficiente para explicar a decisão.

## 4. Falha

Uma falha de validação não implica por si só apagar ChannelSource.

O sistema deve distinguir:
- falha transitória;
- falha persistente;
- ausência de observação;
- explicitamente removido/desactivado.

## 5. Histerese

Mudanças de estado que provoquem churn devem poder usar uma política de estabilidade/histerese. O comportamento exacto deve ser definido e versionado normativamente antes da implementação da funcionalidade; não é exigido ADR.

**Correspondência normativa.** Validation produz observações/resultados factuais; Eligibility é decisão derivada de evidence + policy. A correspondência entre categorias de Validation e `Eligible | Ineligible | Unknown` é NORMATIVA por categoria; NÃO é uma tabela arbitrariamente configurável pelo operador.

**Recuperação.** Nova evidência válida pode recuperar automaticamente `Ineligible → Eligible`.

**Último estado publicável.** O 'último estado publicável' é o último estado `Eligible` efectivamente publicável dentro da janela de histerese.

**Ausência.** Ausência de observação NÃO significa `Ineligible`.

**Histerese.** Os valores de histerese permanecem parâmetros operacionais (`PARAMETER_GAP`).
