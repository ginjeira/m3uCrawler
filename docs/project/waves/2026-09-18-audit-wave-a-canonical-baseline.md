# Audit Wave A — Baseline canónico PT e endpoint de criação de canal
- Data: 2026-09-18
- Estado: Concluída
- Commits: 4dff2bf
- BÍBLIA/ADR: o catálogo é a autoridade de identidade (DL-001, `docs/Reestructure/31-DECISION-LOCK.md:9-11`); catálogo contém aliases/identidades externas (`docs/Reestructure/05-CATALOGUE.md:9-27`); baseline inicial não apaga estado local (DL-107, `docs/Reestructure/31-DECISION-LOCK.md:121-122`); ADR-0005 (`docs/adr/ADR-0005-catalog-baseline-vs-runtime.md`, Proposed)

## Contexto / Objectivo
Achado A1 da auditoria transversal: `docs/catalog/*.json` (baseline canónico PT) não era empacotado na imagem; a produção importava só `CatalogSeed`, esvaziado dos generalistas PT (RTP1/2/3, SIC, TVI, CMTV, CNN Portugal…) e o import falhava em silêncio (`LogDebug`). Achado F-01: `POST /api/catalog/channels` estava shadowed pelo handler de listagem e não criava nada.

## Âmbito implementado
- Baseline canónico PT empacotado/embutido e importado (Dockerfile + `M3U_BASELINE_PATH` + recurso embutido + fallback); ausência do baseline passa a log Warning.
- `POST /api/catalog/channels` deixa de ser shadowed e cria de facto; guardas de método/405 adicionadas.
- Testes de baseline embutido e de endpoint de criação de canal.

## Ficheiros/componentes principais
- `m3uCrawler/Services/Catalog/CatalogBaselineImporter.cs`, `ChannelCatalogBootstrapper.cs`
- `m3uCrawler/Services/WebDashboardService.cs`
- `m3uCrawler/Dockerfile`, `m3uCrawler/m3uCrawler.csproj`, `.github/workflows/package.yml`
- Testes: `CatalogBaselineEmbeddedTests.cs`, `CatalogChannelEndpointTests.cs`
- Docs: `m3uCrawler/README.md`, `CHANGELOG.md`

## Validação (build + suite + testes relevantes)
- Build Release: 0 errors / 52 warnings.
- Suite completa: 2023 passed / 0 failed / 1 skipped.

## Evidência
- Commit `4dff2bf` (`fix(catalog): ship canonical PT baseline and fix channel create endpoint`).
- Diff: 10 ficheiros, +708/-43.

## Divergências / dívida / follow-ups
- Baseline não inclui `tvg-id` (tabela `ExternalIdentity` semeada vazia) — endereçado em W4b; mapeamento `tvg-id`→canal pendente (ADR-0002).
- F-27 (guardas de método) apenas parcialmente coberto.
