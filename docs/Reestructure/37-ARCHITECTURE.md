# 37 — Arquitectura lógica
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


## 1. Bounded contexts

1. **Control Plane** — utilizadores, configuração, auth, readiness.
2. **Discovery** — candidatos, providers e contas.
3. **Ingestion** — aquisição, parsing e streams.
4. **Catalogue** — identidade e Review.
5. **Media Policy** — country/media classification.
6. **Source State** — ChannelSource, observations e eligibility.
7. **Decision Engine** — priority, selection e ordering.
8. **Publication** — composição e M3U.
9. **Integration** — Dispatcharr e ownership.
10. **Runtime** — RunCoordinator, scheduler e recovery.

## 2. Dependências

Control Plane → Application services  
Application services → Domain  
Domain → abstrações, não providers concretos  
Adapters → serviços externos  
Persistence → implementa repositórios  
UI/CLI → Application API

O domínio não pode depender de HTTP, Telegram, Dispatcharr ou filesystem.

## 3. Regra de fronteira

Uma integração externa é substituível sem alterar a semântica de CanonicalChannel, Ordering ou Selection.

## 4. Fluxo

`Discovery → Ingestion → Catalogue → SourceState → Decision → Publication → Integration`

O Runtime coordena; não redefine as regras de cada contexto.
