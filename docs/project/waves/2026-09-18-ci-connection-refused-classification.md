# Wave CI — Classificação Linux de connection refused
- Data: 2026-09-18
- Estado: Concluída
- Commits: 69b7779
- BÍBLIA/ADR: testes (`docs/Reestructure/21-TESTING.md`); quality gates (`docs/Reestructure/44-QUALITY-GATES.md`)

## Contexto / Objectivo
A corrida CI falhava porque, em Linux, uma ligação recusada era classificada como `kind=Network` e o teste de observabilidade esperava a classificação anterior. Correcção de teste (não de produção) para alinhar com a classificação real.

## Âmbito implementado
- `ObservabilityTests` aceita `kind=Network` para connection-refused em Linux.
- Corrida CI 35369150660 passou a verde; build GHCR habilitado.

## Ficheiros/componentes principais
- `m3uCrawler.Tests/ObservabilityTests.cs`

## Validação (build + suite + testes relevantes)
- CI run 35369150660: success.
- Local: 1917 passed / 0 failed / 1 skipped.

## Evidência
- Commit `69b7779` (`fix(ci): accept linux connection refused network classification`).
- Diff: 1 ficheiro, +11/-5.

## Divergências / dívida / follow-ups
- n/d
