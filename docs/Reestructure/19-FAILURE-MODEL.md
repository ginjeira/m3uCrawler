# 19 — Modelo de falhas

## 1. Categorias

No mínimo:
- Configuration;
- Authentication;
- Authorization;
- Network;
- Timeout;
- Parsing;
- Validation;
- Data;
- Conflict;
- ExternalService;
- Security;
- Cancellation;
- Internal.

## 2. Semântica

Cada falha deve definir:
- retryable?;
- impacto no Run;
- impacto no item;
- compensação necessária?;
- observabilidade;
- estado persistido.

## 3. Partial success

Sucesso parcial deve ser representado explicitamente.

Nunca converter:
`partially applied`
em
`success`.

## 4. Retries

Retries devem ser limitados, com backoff e cancelamento. Esta regra aplica-se ao retry técnico de operação, dentro da mesma Run; não cria uma nova Run nem um novo `RunId`.

Operações não idempotentes exigem idempotency key ou reconciliação.

**Retry técnico de operação (dentro de uma Run).** Uma operação sujeita a retry/backoff (por exemplo, rede) permanece na MESMA Run e NÃO cria novo `RunId`.

**Reexecução/retry lógico da Run.** Cria uma NOVA Run, com NOVO `RunId`, e mantém relação causal explícita com a Run anterior.

## 5. Restart

Após restart, a aplicação deve conseguir identificar Runs incompletos e não assumir que operações externas não aconteceram.

## 6. Classificação de falhas de aquisição

**Classificação de falhas de aquisição (normativa).**
Retry técnico dentro da mesma Run: `Network`, `DNS`, `timeout`, HTTP `429`, HTTP `5xx`. O retry técnico permanece na mesma Run, NÃO cria novo `RunId`, e usa os parâmetros operacionais (`PARAMETER_GAP`).
Falha terminal da operação: HTTP `401`, HTTP `403`, HTTP `404`, resposta vazia, conteúdo malformado, encoding inválido, resposta oversized.
Após esgotar os retries técnicos, a falha persistente fica registada na `Source` e participa na agregação da `Run`.
Manter separados: classificação técnica da falha; retry técnico; estado persistido da `Source`; estado agregado da `Run`. Esta classificação NÃO é uma Acquisition Policy configurável.
