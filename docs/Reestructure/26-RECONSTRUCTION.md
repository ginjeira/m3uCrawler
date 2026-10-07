# 26 — Reconstrução e fases de implementação

## 1. Regra

As fases são uma sequência de construção, não uma definição alternativa do produto.

Cada fase deve terminar num estado funcional e verificável.

## 2. Fase 0 — Fundação

Objectivo:
- repository;
- build;
- configuração base;
- SQLite;
- migrations;
- logging;
- segurança base;
- testes.

Gate: aplicação inicia, DB migra e testes base passam.

## 3. Fase 1 — Bootstrap

Implementar:
- lifecycle;
- admin;
- autenticação;
- readiness base;
- Dashboard mínimo.

Gate: fresh install utilizável.

## 4. Fase 2 — Providers, Accounts e Sources

Implementar:
- Provider;
- ProviderAccount;
- Source;
- configuração;
- lifecycle;
- constraints.

Gate: operador consegue configurar uma Source.

## 5. Fase 3 — Discovery

Implementar:
- adapters;
- Candidate;
- dedup;
- account serialization;
- aquisição.

Gate: discovery produz candidatos repetíveis.

## 6. Fase 4 — Playlist/Stream

Implementar:
- parsing;
- normalization;
- fingerprint;
- classificação.

Gate: fixtures produzem streams determinísticas.

## 7. Fase 5 — Catalogue/Recognition/Review

Implementar:
- CanonicalChannel;
- aliases;
- external identities;
- matching;
- ambiguity;
- Review.

Gate: unknown nunca cria catálogo implicitamente.

## 8. Fase 6 — ChannelSource/Validation/Eligibility

Gate: estado técnico é separado de identidade.

## 9. Fase 7 — Groups/Ordering

Gate: ordering não altera identidade; VOD permanece separado.

## 10. Fase 8 — Selection

Gate: ranking determinístico, policy snapshot e resultado auditável.

## 11. Fase 9 — Composition

Gate: M3U determinística e atomicamente publicada.

## 12. Fase 10 — Dispatcharr

Gate: dry-run, ownership e reconciliação segura.

## 13. Fase 11 — Runs/Scheduler/Operations

Gate: manual e scheduler convergem no mesmo coordinator; restart/recovery testados.

## 14. Fase 12 — Hardening

Security, performance, retention, backup/restore, migration, observability.

Ordem normativa de upgrade: 1. schema migration; 2. baseline upgrade. Respeitar DL-107: o baseline NUNCA apaga alterações locais; a migration NÃO deve destruir estado funcional existente.

## 15. Regra de gate

Uma fase não é concluída por código compilado. Exige:
- testes;
- documentação;
- invariantes;
- evidência;
- Git limpo relativamente ao trabalho;
- ausência de regressões não explicadas.

## 16. Reconstrução da implementação actual

Depois de a BÍBLIA ser aprovada:
1. auditar código actual contra cada requisito;
2. classificar cada divergência;
3. preservar o que estiver correcto;
4. migrar dados quando possível;
5. remover conceitos contraditórios;
6. alinhar documentação derivada;
7. reconstruir fases quando a implementação histórica não respeitar a arquitectura.
