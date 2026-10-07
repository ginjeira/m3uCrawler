# Wave W3s — País como classificação (parte segura, não ADR-gated)
- Data: 2026-09-19
- Estado: Concluída
- Commits: 2ac28d3
- BÍBLIA/ADR: país/classificação não é identidade (`docs/Reestructure/06-COUNTRY-MEDIA.md:1-18`); country data versionado/auditável (DL-106, `docs/Reestructure/31-DECISION-LOCK.md:118-119`); readiness inclui country data (`docs/Reestructure/15-APPLICATION.md:12-22`); ADR-0001 (`docs/adr/ADR-0001-country-data-ownership.md`, Proposed)

## Contexto / Objectivo
Achados C-05/C-09/C-10/C-12: país injetado em store estático process-global, cache não invalidada, GETs de país com efeitos de escrita e `ValidatePlaylist` legado com falsos positivos. Objectivo: país responde "esta fonte parece PT" sem poder criar identidade de canal, e a readiness reflecte country data.

## Âmbito implementado
- Leituras de país deixam de escrever (sem auto-provisão em GET).
- Removido o `ValidatePlaylist` legado.
- Readiness ganha `countryData` (required; entra em `missingRequired`; visível no Setup).
- Affinity de país com escopo por pedido (fim do estado global estático); disponível no web/scheduler.
- Testes de consistência do atributo `Country`.

## Ficheiros/componentes principais
- `m3uCrawler/Services/CountryChannelListService.cs`, `CountryChannelValidator.cs`
- `m3uCrawler/Services/Configuration/CountryConfigProvisioner.cs`, `OperationalReadinessService.cs`
- `m3uCrawler/Services/TelegramScraperService.cs`, `WebDashboardService.cs`, `Program.cs`
- Testes: `CountryAttributeConsistencyTests.cs`, `CountryChannelListServiceReadsTests.cs`, `CountryChannelValidatorAffinityScopeTests.cs`, `CountryConfigProvisionerTests.cs`
- Docs: `docs/architecture/configuration-lifecycle.md`, `CHANGELOG.md`

## Validação (build + suite + testes relevantes)
- Build Release: 0 errors / 51 warnings (baseline 52; −1 por remoção de um ficheiro de teste obsoleto; 0 warnings novos).
- Suite completa: 2090 passed / 0 failed / 1 skipped.

## Evidência
- Commit `2ac28d3` (`fix(country): classification-only reads, readiness and scoped affinities`).
- Diff: 19 ficheiros, +776/-213.

## Divergências / dívida / follow-ups
- C-01/C-02 (`AnalyzePlaylist` a sintetizar identidades) e C-03/C-16 (listas PT hardcoded + `channel-indicators.json`) permanecem pendentes.
- `runtime-data` com duas raízes (C-08/F-21) permanece pendente.
