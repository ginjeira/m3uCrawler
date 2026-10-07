# 47 — Resultados da revisão cruzada BÍBLIA 1.2

## Objectivo

Esta revisão verificou coerência semântica e também se os documentos exigem detalhes que depois deixam materialmente em aberto.

## Achados resolvidos

### BF-001 — Selection tinha regra fechada e regra "a decidir"
`31-DECISION-LOCK.md` já fechava DL-101, enquanto `09-SELECTION.md` ainda remetia a ordem exacta para ADR. `09-SELECTION.md` passa a reproduzir a decisão fechada; ADR documenta o racional.

### BF-002 — Precedência misturava configuração técnica e estado funcional
`14-CONFIGURATION.md` e `39-CONFIG-SCHEMA.md` passam a separar as duas classes. A cadeia de precedência aplica-se à configuração técnica; o estado funcional persistido não é sobrescrito silenciosamente no arranque.

### BF-003 — API inventory não era contrato implementável
`41-API-INVENTORY.md` passa a declarar que cada operação necessita de ficha completa em `22-API-CONTRACTS.md` antes da implementação final.

### BF-004 — Data contracts exigiam detalhes sem os enumerar
`23-DATA-CONTRACTS.md` passa a definir o conteúdo mínimo do contrato serializado.

### BF-005 — Domain schema não delimitava o que podia variar
`32-DOMAIN-SCHEMA.md` passa a declarar explicitamente quais propriedades semânticas não podem ser alteradas pela implementação.

## Pontos deliberadamente abertos

Continuam dependentes de ADR/contrato antes da implementação definitiva:
- algoritmo exacto de fingerprint;
- mecanismo concreto de secret storage;
- política SSRF detalhada;
- mecanismo concreto de lock/lease;
- retenção exacta;
- migration/rollback;
- versionamento concreto da API;
- schemas físicos de serialização;
- adapters concretos de providers.

Isto é aceitável porque a própria BÍBLIA os identifica como decisões pendentes. O implementador não pode escolhê-los silenciosamente.

## Conclusão

A BÍBLIA 1.2 é mais rigorosa do que a versão anterior porque distingue explicitamente decisão arquitectural de detalhe de implementação ainda não decidido.

A próxima validação é externa à BÍBLIA: `BÍBLIA → código actual → testes → runtime`.
