# Wave W6b-2 — Observabilidade: SyncRun e observações de ChannelSource
- Data: 2026-09-19
- Estado: Concluída
- Commits: ee7baab
- BÍBLIA/ADR: observabilidade de runs (`docs/Reestructure/13-RUNS.md`); Observation/histórico (DL-023); persistência no catálogo (`16-PERSISTENCE.md`)

## Contexto / Objectivo
F-14 e F-11/sync-runs: a degradação só tinha produtor manual e o pipeline não gravava observações; os `SyncRunEntity`/`SyncRunStepEntity` existiam mas não eram emitidos pelo sync Dispatcharr. Objectivo: registar a execução do sync e as observações validadas de `ChannelSource` no catálogo.

## Âmbito implementado
- `DispatcharrSyncService` cria um `SyncRun` no início (`running`), regista passos (`read-plan`, `selection`, `dry-run`, `apply`, `apply-create`, `apply-associate`, `apply-remove`, `apply-protected`, `apply-errors`) e fecha-o com resultado `ok`/`partial`/`error`/`dry-run`.
- Registo best-effort e observável: falhas de persistência da run/passos são registadas (log/`Console.WriteLine`) e não abortam o sync; `DispatcharrApplyRecorder` acumula evidência sem alterar a semântica do apply.
- `ChannelSourceObservation` gravada em `PipelineIngestionService` para streams validados; dedupe por `(ChannelSourceId, ObservedAtUtc)`, re-ingerir o mesmo evento não cria duplicados.
- Nenhum segredo persistido (apenas contadores agregados e qualidades).

## Ficheiros/componentes principais
- `m3uCrawler/Services/Sync/DispatcharrSyncService.cs`
- `m3uCrawler/Services/Catalog/PipelineIngestionService.cs`, `CatalogResolver.cs`
- Testes: `m3uCrawler.Tests/WaveW6b2ObservabilityTests.cs` (472 linhas); `PipelineIngestionBridgeTests.cs`
- Docs: `CHANGELOG.md`

## Validação (build + suite + testes relevantes)
- Build Release: 0 errors / 51 warnings.
- Suite completa: 2170 passed / 0 failed / 1 skipped.
- Cobertura: resultados `ok`/`partial`/`error`/`dry-run`, passos por fase, dedupe de observações por `(ChannelSourceId, ObservedAtUtc)`.

## Evidência
- Commit `ee7baab` (`feat(observability): emit sync runs and channel source observations`).
- Diff: 6 ficheiros, +876/-68.

## Divergências / dívida / follow-ups
- Os endpoints/superfície que consomem `SyncRun` para consulta continuam a ser tratados em W6b-3 (ligar/esconder funcionalidades inertes).
