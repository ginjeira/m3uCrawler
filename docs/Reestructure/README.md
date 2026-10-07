# m3uCrawler BÍBLIA
<!--
NOTA DE RECUPERAÇÃO (Wave 0.5, 2026-09-21)
=============================================
Este documento foi recuperado do commit órfão `a60c09b` (stash untracked,
2026-09-21 08:22:03+01:00) e corresponde ao snapshot da BÍBLIA 1.2 datado
de 2026-09-18. A versão normativa mais recente está em HEAD; este ficheiro
é apenas um marco histórico do corpus.

Estado de implementação posterior a 2026-09-18 (W5.0–W5.6, M.4) está
documentado nos suplementos tracked em HEAD:
- `docs/Reestructure/31-DECISION-LOCK.md` (DL-001..126)
- `docs/Reestructure/46-REQUIREMENT-TRACEABILITY.md`
- `docs/Reestructure/48-RECOGNITION-FUZZY-CONTRACT.md`
- `docs/Reestructure/49-W56-MATCH-CONFIDENCE-SPECIFICATION.md`

Em caso de divergência entre este documento e os suplementos tracked, a
BÍBLIA é a autoridade normativa; os supplements tracked reflectem o estado
de implementação corrente; este documento reflecte o estado normativo à
data de 2026-09-18.
-->


**Versão:** 1.2 — BASE NORMATIVA REVISTA  
**Data:** 2026-09-18

Este directório constitui conjuntamente a BÍBLIA do m3uCrawler.

`00-BIBLE.md` é a constituição. Os restantes documentos são partes normativas especializadas.

## Ordem de leitura recomendada

1. `00-BIBLE.md`
2. `01-PRODUCT.md`
3. `02-DOMAIN.md`
4. `27-GLOSSARY.md`
5. `31-DECISION-LOCK.md`
6. `32-DOMAIN-SCHEMA.md`
7. `33-STATE-MACHINES.md`
8. `34-PIPELINE-CONTRACTS.md`
9. `37-ARCHITECTURE.md`
10. `38-POLICIES.md`
11. `39-CONFIG-SCHEMA.md`
12. `40-ENTITY-LIFECYCLE.md`
13. `41-API-INVENTORY.md`
14. restantes documentos por domínio
15. `45-BIBLE-AUDIT.md`
16. `46-REQUIREMENT-TRACEABILITY.md`
17. `47-BIBLE-AUDIT-FINDINGS.md`
18. `48-RECOGNITION-FUZZY-CONTRACT.md`
19. `49-W56-MATCH-CONFIDENCE-SPECIFICATION.md`

Estes últimos (`46`–`49`) são as adições posteriores ao conjunto 1.2: os documentos de rastreabilidade e achados de auditoria, e as especificações ratificadas de fuzzy e match-confidence. O passo fuzzy (W5.3) está implementado, mas só é alcançável quando é passado um snapshot de `RecognitionPolicy`; a ligação desse snapshot ao Run/pipeline (M.4) permanece `OPEN`.

## Regra

A BÍBLIA define o produto e os contratos. Roadmap, AGENTS, ADRs, código e testes são derivados/implementações.

Se houver conflito, a divergência deve ser explicitamente resolvida; não pode ser escondida por interpretação.

## Critério

Uma lacuna deliberada deve estar marcada como decisão a fechar. Uma decisão fechada deve ser implementável sem adivinhação.
