# 18 — Observabilidade, artifacts e reports

## 1. Logs

Logs devem permitir encontrar a primeira divergência sem expor segredos.

Devem possuir:
- RunId;
- contexto;
- operação;
- classificação do erro;
- duração quando útil.

## 2. Warnings

Warnings não devem crescer indefinidamente.

Uma mudança não deve introduzir warnings novos sem justificação.

## 3. Artifacts

Artifacts de execução devem ser versionados por schema.

Devem indicar:
- schema version;
- RunId;
- generatedAtUtc;
- origem;
- resultado;
- contagens.

## 4. Segurança dos artifacts

Qualquer serializer destinado ao operador deve aplicar sanitização centralizada.

## 5. Reports

Reports distinguem:
- intenção;
- resultado aplicado;
- falha;
- recurso remoto observado.

Nunca devem afirmar sucesso se só houve intenção.
