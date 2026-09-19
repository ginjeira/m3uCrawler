# Wave W4a — Selection DL-101 com um único selector
- Data: 2026-09-19
- Estado: Concluída
- Commits: fa1b593
- BÍBLIA/ADR: DL-101 ordenação lexicográfica (`docs/Reestructure/31-DECISION-LOCK.md:86-98`); DL-102 priority 1=preferido (`:100-101`); determinismo e resultado da selection (`docs/Reestructure/09-SELECTION.md:23-54`); ADR-0003 (Accepted)

## Contexto / Objectivo
Achados D-F6/D-F8/D-F9 e a divergência registada no ADR-0003: coexistiam dois selectors (`ChannelSourceSelector` e `SourceSelector`), a prioridade era ordenada ao contrário de DL-102 e a selection agrupava por `Id` enquanto a política/artefacto usavam `Key`.

## Âmbito implementado
- Um único `IChannelSourceSelector` implementa a ordem lexicográfica fechada de DL-101.
- Prioridade inteira corrigida: `1` = mais preferido (DL-102).
- `PlaylistComposerService` passa a respeitar a selection; agrupamento por `Key`.
- Testes de selecção, preview, stage e sync actualizados.

## Ficheiros/componentes principais
- `m3uCrawler/Services/Catalog/PlaylistComposerService.cs`
- `m3uCrawler/Services/SourceSelection/ChannelSourceSelector.cs`, `SourceSelectionModels.cs`, `SourceSelectionStage.cs`
- Testes: `ChannelSourceSelectorTests.cs`, `SourceSelectionStageTests.cs`, `SourceSelectionPreviewTests.cs`, `DispatcharrSyncServiceSourceSelectionTests.cs`

## Validação (build + suite + testes relevantes)
- Build Release: 0 errors / 52 warnings.
- Suite completa: 2104 passed / 1 flaky / 1 skipped (o flaky passa isoladamente).
- Testes de determinismo do ranking.

## Evidência
- Commit `fa1b593` (`fix(selection): implement DL-101 ranking with a single selector`).
- Diff: 8 ficheiros, +659/-216.

## Divergências / dívida / follow-ups
- D-F5 (`StreamOrderingPolicy` a fazer preferência de provider) permanece pendente.
- D-F2 (unicidade de `Scope`) permanece pendente.
- D-F10/F16 (caminhos de `playlist.m3u`/sync que ignoram a selection) — verificar em W6.
