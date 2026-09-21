# Governação da reconstrução

## Objectivo

Definir como o projecto passa da implementação histórica para a arquitectura normativa da BÍBLIA sem perder evidência, decisões ou trabalho já válido.

## Princípio

Não fazer um "big bang rewrite".

A reconstrução deve ser orientada por contratos:

```text
BÍBLIA
→ AUDIT
→ GAP
→ DECISION
→ WAVE
→ TEST
→ GATE
```

## BIBLE AUDIT

A primeira auditoria deve:

- ler toda a BÍBLIA;
- inspeccionar o código;
- inspeccionar testes;
- inspeccionar migrations/schema;
- inspeccionar configuração;
- inspeccionar documentação actual;
- identificar conceitos duplicados;
- localizar a primeira divergência em cada cadeia;
- produzir a matriz `46-REQUIREMENT-TRACEABILITY.md`.

Não deve alterar o código.

## O que fazer com funcionalidades existentes

### Conforme

Preservar e integrar.

### Parcialmente conforme

Completar sem criar uma segunda implementação.

### Divergente

Tratar como migração/correcção.

### Legacy

Preservar apenas quando houver razão de compatibilidade e sem permitir que redefina a arquitectura normativa.

### Sem cobertura

Adicionar implementação e testes numa wave apropriada.

## Documentação

A documentação antiga não deve ser apagada apenas porque existe a BÍBLIA.

Enquanto for útil para compreender o estado actual:

- marcar como actual/legacy;
- apontar para a BÍBLIA;
- remover afirmações normativas contraditórias;
- absorver decisões relevantes na BÍBLIA quando necessário.

## Critério de fecho de uma wave

Uma wave só fecha quando:

1. requisito normativo está implementado;
2. testes relevantes demonstram o contrato;
3. acceptance gates passam;
4. documentação derivada está actualizada;
5. traceability está actualizada;
6. não ficaram divergências escondidas no mesmo escopo;
7. Git está limpo relativamente ao trabalho da wave.

## Regra de não-regressão arquitectural

Nenhuma wave pode introduzir:

- segunda autoridade para identidade;
- segunda pipeline para o mesmo conceito;
- estado implícito não documentado;
- decisão por score onde a BÍBLIA exige ordem determinística;
- mutação externa onde o contrato exige dry-run;
- perda silenciosa de dados;
- exposição de secrets;
- dependência circular entre bounded contexts.

## Relação com agentes

`AGENTS.md` define o método operacional.

`29-AGENT-RULES.md` define os princípios normativos aplicáveis ao trabalho de implementação.

Os dois devem permanecer coerentes, mas não devem duplicar a BÍBLIA inteira.
