# 40 — Lifecycle e retenção das entidades

## 1. Regra geral

Não confundir:
- disabled;
- inactive;
- missing;
- expired;
- deleted.

## 2. Catalogue

CanonicalChannel permanece até operação administrativa explícita de desactivação/remoção segundo as regras de referência.

## 3. ChannelSource

Missing numa execução altera `LastSeen`/estado segundo policy. Não implica delete automático.

## 4. Streams

Streams podem expirar da Source, mas histórico de Observation permanece segundo retenção.

## 5. Review

Review resolvido permanece como histórico de decisão enquanto a retenção o permitir.

Estados `Open | InReview | Resolved | Ignored` (`33`, DL-105). A reabertura (`Resolved/Ignored → Open`) é uma operação administrativa **manual**, auditada e justificada; a deteção automática de evidência materialmente incompatível permanece `OPEN` e fora de W5.4 (DL-119). A criação de `ReviewItem` a partir de fuzzy ambiguity (`CatalogResolution.FuzzyDiagnostic`) pertence a W5.4.

**Implementado em W5.4.** `Services/Catalog/ReviewLifecycle.cs` + `CatalogResolver` (operações auditadas via `IAuditService`). Sem migration: os valores persistidos preservam `Approved→Resolved` (1) e `Excluded→Ignored` (2). Evidência: `WaveW54ReviewLifecycleTests.cs`.

## 6. Runs

Run terminado é imutável quanto ao resultado fundamental. Correções administrativas são novos AuditRecords.

## 7. Artifacts

Artifacts podem ser eliminados pela política de retenção, desde que o Run mantenha a referência e o estado esperado.

## 8. Hard delete

Hard delete é administrativo, explícito e auditado. Não é consequência implícita de ausência numa source.

## DiscoveryCandidate

`DiscoveryCandidate` é terminal por `Run`. Não mantém `FirstSeen`/`LastSeen`; o histórico obtém-se através do `Run`/`Observation`. A ausência numa source não implica remoção (DL-024). Hard delete é apenas administrativo.
