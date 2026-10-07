# Wave W4b — Identidade externa (tvg-id) no matching
- Data: 2026-09-19
- Estado: Concluída
- Commits: 146b507
- BÍBLIA/ADR: ordem de matching (passo 2 tvg-id/provider identity) (`docs/Reestructure/05-CATALOGUE.md:29-41`); `OriginalTvgId` e `ExternalIdentity` (`docs/Reestructure/32-DOMAIN-SCHEMA.md:72-85`, `:112-121`); DL-001/DL-002; ADR-0002 (Proposed)

## Contexto / Objectivo
Lacuna normativa confirmada: o matching por identidade externa (`tvg-id`) não existia. Requeria entidade `ExternalIdentity`, captura de `OriginalTvgId` no parser e inserção no resolver antes de nome/alias, com ambiguidade a seguir para Review.

## Âmbito implementado
- Entidade `ExternalIdentity` + migração aditiva `20260919063912_AddExternalIdentity` (com designer e snapshot).
- `OriginalTvgId` capturado no parser (`M3uStream`, `DiscoveredStream`, `PlaylistReader`).
- Passo 1/2 do resolver: identidade externa antes de nome/alias; ambiguidade → Review.
- Idempotente; o baseline não contém `tvg-id`, pelo que a tabela é semeada vazia.

## Ficheiros/componentes principais
- `m3uCrawler/Services/Catalog/CatalogEntities.cs`, `CatalogResolver.cs`, `ExternalIdentityNormalizer.cs`, `CatalogBaseline.cs`, `CatalogBaselineImporter.cs`, `ChannelCatalogDbContext.cs`, `PipelineIngestionService.cs`
- Migração: `20260919063912_AddExternalIdentity.cs`/`.Designer.cs`, `ChannelCatalogDbContextModelSnapshot.cs`
- `m3uCrawler/Models/DiscoveredStream.cs`, `M3uStream.cs`; `m3uCrawler/Services/M3uParserService.cs`, `Matching/ChannelMatcher.cs`, `Sync/PlaylistReader.cs`
- Testes: `ExternalIdentityResolutionTests.cs` (577 linhas)

## Validação (build + suite + testes relevantes)
- Build Release: 0 errors / 52 warnings.
- Suite completa: 2131 passed / 0 failed / 1 skipped.
- Testes de determinismo/idempotência e de ambiguidade→Review.

## Evidência
- Commit `146b507` (`feat(matching): external identity (tvg-id) resolution step`).
- Diff: 18 ficheiros, +2684/-26.

## Divergências / dívida / follow-ups
- Sem fonte de mapeamento `tvg-id`→canal no baseline (tabela semeada vazia) — seeding pendente, dependente de ADR-0002.
- ADR-0002 permanece `Proposed`.
