# 24 — Decisões arquitecturais e respectivo registo
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


A BÍBLIA não deve deixar decisões críticas implícitas.

Os itens abaixo são candidatos a ADR (racional/histórico). Os itens marcados **RESOLVIDO NORMATIVAMENTE** não requerem ADR; a norma está na BÍBLIA. Um ADR nunca é mecanismo obrigatório de decisão.

1. **RESOLVIDO NORMATIVAMENTE** (não requer ADR) — ver `04-PLAYLIST-STREAM.md` / `03-DISCOVERY.md` / `17-SECURITY.md` / `06-COUNTRY-MEDIA.md`.
2. racional e consequências do ranking de Source Selection já definido normativamente em DL-101;
3. racional e exemplos da precedência de policies já definida normativamente em DL-103;
4. histerese/churn;
5. **RESOLVIDO NORMATIVAMENTE** (não requer ADR) — ver `04-PLAYLIST-STREAM.md` / `03-DISCOVERY.md` / `17-SECURITY.md` / `06-COUNTRY-MEDIA.md`.
6. storage e lifecycle de secrets;
7. **RESOLVIDO NORMATIVAMENTE** (não requer ADR) — ver `04-PLAYLIST-STREAM.md` / `03-DISCOVERY.md` / `17-SECURITY.md` / `06-COUNTRY-MEDIA.md`.
8. scheduler locking;
9. retenção;
10. SQLite migration strategy;
11. API versioning;
12. artifact schemas;
13. **RESOLVIDO NORMATIVAMENTE** (não requer ADR) — ver `04-PLAYLIST-STREAM.md` / `03-DISCOVERY.md` / `17-SECURITY.md` / `06-COUNTRY-MEDIA.md`.
14. Review lifecycle;
15. catálogo baseline/runtime;
16. deployment rollback;
17. provider adapter architecture.

Um ADR não pode alterar uma regra normativa sem actualizar a BÍBLIA.

Um ADR não é mecanismo obrigatório de decisão; a BÍBLIA é a autoridade normativa. ADRs registam racional, não substituem a norma.
