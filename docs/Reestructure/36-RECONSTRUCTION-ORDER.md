# 36 — Ordem de reconstrução do produto

Este documento transforma a BÍBLIA numa sequência concreta. Não redefine o produto.

## Gate A — Fundação
Build, config, DB, migrations, logging, auth base, test harness.

## Gate B — Domain
Entidades, IDs, constraints, repositories e regras puras.

## Gate C — First Run
Bootstrap + admin + readiness + Dashboard mínimo.

## Gate D — Sources
Provider → Account → Source + configuração.

## Gate E — Discovery
Adapters + Candidate + dedup + acquisition.

## Gate F — Media
Playlist + Stream + normalization + classification + country gate.

## Gate G — Catalogue
CanonicalChannel + aliases + external identities + recognition.

## Gate H — Review
Unknown/ambiguous + lifecycle + operações administrativas.

## Gate I — ChannelSource
Relação source/channel + provenance.

## Gate J — Validation
Observations + eligibility.

## Gate K — Policies
Priority + selection + snapshots + determinismo.

## Gate L — Ordering
Lists + items + groups + mappings + TV/Radio/VOD.

## Gate M — Output
Composition + M3U + atomic publication.

## Gate N — Dispatcharr
Dry-run + ownership + reconciliation + compensation.

## Gate O — Runtime
RunCoordinator + scheduler + cancellation + recovery.

## Gate P — Hardening
Security + retention + performance + backup/restore + upgrade.

## Gate rule

Nenhum gate passa por "compila". Deve possuir:
- testes;
- fixtures;
- evidência;
- documentação;
- migração quando aplicável;
- verificação de invariantes;
- estado Git identificável.

## Reconstrução da aplicação actual

Depois de congelada a BÍBLIA:
- mapear código existente para gates;
- classificar correcto/divergente/obsoleto;
- não preservar abstrações apenas porque existem;
- migrar dados antes de remover estruturas;
- manter cada alteração numa unidade auditável.
