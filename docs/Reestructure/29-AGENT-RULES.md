# 29 — Regras para programadores e agentes IA
<!--
NOTA DE RECUPERAÇÃO (Wave 0.5, 2026-09-21)
=============================================
Este documento foi recuperado do commit órfão `a60c09b` (stash untracked,
2026-09-21 08:22:03+01:00) e corresponde ao snapshot da BÍBLIA 1.2 datado
de 2026-09-18.

A referência a `PROJECT_WORKING_AGREEMENT.md` neste documento diz respeito
a um acordo de trabalho externo ao repositório deste projecto (não é um
ficheiro versionado nem um documento normativo). É distinto de:

- **BÍBLIA normativa versionada** (este directório, em particular
  `00-BIBLE.md`): fonte de verdade do produto, acima de tudo.
- **`AGENTS.md` operacional** (na raiz do repositório): regras operacionais
  para agentes AI/programadores; derivação complementar que pode divergir
  em regras operacionais mas nunca pode contradizer a BÍBLIA.
- **Restante documentação derivada** (`PROJECT_STATUS.md`, `ROADMAP.md`,
  `docs/IMPLEMENTATION_ROADMAP.md`, ADRs, etc.): derivações que descrevem
  estado de implementação ou decisões específicas; não são fonte normativa.

Estado de implementação posterior a 2026-09-18 (W5.0–W5.6, M.4) está
documentado nos suplementos tracked em HEAD:
- `docs/Reestructure/31-DECISION-LOCK.md` (DL-001..126)
- `docs/Reestructure/46-REQUIREMENT-TRACEABILITY.md`
- `docs/Reestructure/48-RECOGNITION-FUZZY-CONTRACT.md`
- `docs/Reestructure/49-W56-MATCH-CONFIDENCE-SPECIFICATION.md`

Em caso de divergência entre este documento e os supplements tracked, a
BÍBLIA é a autoridade normativa.
-->


## 1. Autoridade

Antes de alterar código, consultar a BÍBLIA aplicável.

Nunca usar o código existente como prova de que o comportamento é correcto.

## 2. Método

Seguir:
`estado conhecido → evidência → hipótese → teste dirigido → causa → alteração → teste → documentação → commit → validação`

## 3. Não inventar

Se a BÍBLIA não decidir um comportamento que afecte arquitectura ou contrato:
- não inventar;
- identificar a lacuna;
- propor decisão;
- actualizar BÍBLIA/ADR antes da implementação.

Não resolver por iniciativa própria um item marcado `A DECIDIR`/`TBD` quando a decisão for arquitectural ou comportamental; nesse caso, parar e pedir decisão. `TBD` só é admissível para parâmetros ou detalhe físico não-arquitectural.

## 4. Find first divergence

Em pipelines, descobrir primeiro onde o comportamento se desvia do contrato.

## 5. Não misturar trabalho

Uma alteração deve ter escopo claro, preservar WIP não relacionado e evitar `git add .`.

## 6. Definition of Done

Código não é concluído sem:
- testes;
- documentação necessária;
- validação;
- estado Git conhecido;
- registo do que foi feito e porquê.

## 7. Compatibilidade

Não preservar comportamento histórico apenas por ser histórico se contradizer a BÍBLIA. Nesse caso, tratar como migração/correcção.

## 8. Relatório

Cada iteração deve indicar:
- estado;
- evidência;
- alterações;
- testes;
- resultado;
- Git;
- próximo estado conhecido.

## 9. Atenção ADRs

Um ADR `Proposed` **não é fonte final de decisão** e não deve ser implementado directamente. Antes de qualquer implementação, o agente DEVE auditar o conteúdo do ADR contra a BÍBLIA (Regra de Completude de `00-BIBLE.md` §5/§6 e requisitos de `24-DECISIONS.md`). Alterações/código baseados em ADRs incompletos DEVEM ser rejeitados; apenas ADRs `Accepted` podem ser executados na implementação.

Critério de aceitação: em cada revisão de plano/código deve existir confirmação de que nenhum ADR `Proposed` foi tratado como definitivo.

Um ADR não é mecanismo obrigatório de decisão; a BÍBLIA é a autoridade normativa.
